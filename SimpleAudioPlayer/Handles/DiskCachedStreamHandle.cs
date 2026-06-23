using System.Buffers;
using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;

namespace SimpleAudioPlayer.Handles;

public sealed class DiskCachedStreamHandle : AudioCallbackHandlerBase
{
    private const int DefaultBufferSize = 81920;
    private const int WaitTimeoutMs = 1000;
    private const int SeekTimeoutMs = 10000;

    private readonly Stream _sourceStream;
    private readonly bool _leaveOpen;
    private readonly bool _deleteCacheOnDispose;
    private readonly bool _enableSeek;
    private readonly bool _sourceCanSeek;
    private readonly bool _commitCacheOnComplete;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _syncLock = new();
    private readonly SemaphoreSlim _sourceReadLock = new(1, 1);
    private readonly CachedByteRangeSet _cachedRanges = new();
    private readonly Task _downloadTask;
    private FileStream? _cacheStream;

    private long _cachedBytes;
    private long _currentPosition;
    private long _backgroundPosition;
    private long? _totalSize;
    private bool _isCompleted;
    private bool _isDisposed;
    private bool _cacheCleaned;
    private bool _cacheCommitted;
    private Exception? _error;

    public DiskCachedStreamHandle(
        Stream stream,
        int bufferSize = DefaultBufferSize,
        long totalSize = -1,
        string? cacheFilePath = null,
        bool? deleteCacheOnDispose = null,
        bool enableSeek = false,
        bool leaveOpen = false,
        bool commitCacheOnComplete = false,
        bool overwrite = false)
    {
        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

        if (commitCacheOnComplete && string.IsNullOrWhiteSpace(cacheFilePath))
        {
            throw new ArgumentException("A cache file path is required when committing the cache on completion.", nameof(cacheFilePath));
        }

        _sourceStream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        _sourceCanSeek = stream.CanSeek;
        _enableSeek = enableSeek;
        _commitCacheOnComplete = commitCacheOnComplete;
        _totalSize = totalSize > 0 ? totalSize : stream.CanSeek ? stream.Length : null;
        CacheFilePath = cacheFilePath ?? CreateDefaultPartialFilePath();
        PartialFilePath = commitCacheOnComplete ? CacheFilePath + ".part" : CacheFilePath;
        _deleteCacheOnDispose = deleteCacheOnDispose ?? cacheFilePath == null;

        PrepareCacheFiles(overwrite);

        var directory = Path.GetDirectoryName(PartialFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _cacheStream = new FileStream(
            PartialFilePath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        _downloadTask = Task.Run(() => RunDownloadTask(bufferSize));
    }

    public event Action<long, long?>? ProgressChanged;
    public event Action<bool, Exception?>? DownloadCompleted;

    public string CacheFilePath { get; }
    public string PartialFilePath { get; }
    public long CachedBytes => Interlocked.Read(ref _cachedBytes);
    public long? TotalSize => _totalSize;
    public bool IsCompleted => _isCompleted;
    public bool IsCacheCommitted => _cacheCommitted;
    public override bool CanSeek => _totalSize.HasValue && (_sourceCanSeek || _enableSeek);

    public override MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead)
    {
        bytesRead = 0;
        if (_isDisposed) return MaResult.MaError;
        if (bytesToRead > int.MaxValue) return MaResult.MaInvalidArgs;

        var requested = (int)bytesToRead;
        if (requested == 0)
        {
            return MaResult.MaSuccess;
        }

        var rented = ArrayPool<byte>.Shared.Rent(requested);
        try
        {
            while (true)
            {
                int read;
                lock (_syncLock)
                {
                    if (_isDisposed) return MaResult.MaError;
                    if (_error != null && _cacheStream == null)
                    {
                        return Fail(MaResult.MaIoError, _error);
                    }

                    var available = _cachedRanges.GetContiguousAvailable(_currentPosition, requested);
                    if (available > 0 && _cacheStream != null)
                    {
                        var bytesToCopy = (int)Math.Min(requested, available);
                        _cacheStream.Position = _currentPosition;
                        read = _cacheStream.Read(rented, 0, bytesToCopy);
                        if (read > 0)
                        {
                            _currentPosition += read;
                            Marshal.Copy(rented, 0, pBuffer, read);
                            bytesRead = (nuint)read;
                            return MaResult.MaSuccess;
                        }
                    }

                    if (_sourceCanSeek && _cacheStream != null && _totalSize.HasValue && _currentPosition < _totalSize.Value)
                    {
                        read = ReadSourceRange(_currentPosition, rented, requested);
                        if (read > 0)
                        {
                            _cacheStream.Position = _currentPosition;
                            _cacheStream.Write(rented, 0, read);
                            _cachedRanges.Add(_currentPosition, read);
                            _cachedBytes = _cachedRanges.TotalBytes;
                            _currentPosition += read;
                            Marshal.Copy(rented, 0, pBuffer, read);
                            bytesRead = (nuint)read;
                            ProgressChanged?.Invoke(Interlocked.Read(ref _cachedBytes), _totalSize);
                            TryCommitCacheLocked();
                            Monitor.PulseAll(_syncLock);
                            return MaResult.MaSuccess;
                        }
                    }

                    if (_error != null)
                    {
                        return Fail(MaResult.MaIoError, _error);
                    }

                    if (_totalSize.HasValue && _currentPosition >= _totalSize.Value)
                    {
                        return MaResult.MaAtEnd;
                    }

                    if (_cts.IsCancellationRequested)
                    {
                        return MaResult.MaCancelled;
                    }

                    if (_isCompleted)
                    {
                        return MaResult.MaAtEnd;
                    }

                    Monitor.Wait(_syncLock, WaitTimeoutMs);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin)
    {
        if (!CanSeek)
        {
            return MaResult.MaNotImplemented;
        }

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Interlocked.Read(ref _currentPosition) + offset,
            SeekOrigin.End => _totalSize!.Value + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0 || target > _totalSize!.Value)
        {
            return MaResult.MaInvalidArgs;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(SeekTimeoutMs);
        lock (_syncLock)
        {
            if (_sourceCanSeek)
            {
                _currentPosition = target;
                return MaResult.MaSuccess;
            }

            while (target > GetContiguousPrefixLengthLocked())
            {
                if (_error != null)
                {
                    return Fail(MaResult.MaIoError, _error);
                }

                if (_isCompleted || _cts.IsCancellationRequested)
                {
                    return MaResult.MaInvalidArgs;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return MaResult.MaTimeout;
                }

                Monitor.Wait(_syncLock, Math.Min(WaitTimeoutMs, (int)remaining.TotalMilliseconds));
            }

            _currentPosition = target;
            return MaResult.MaSuccess;
        }
    }

    public override MaResult OnTell(IntPtr pDecoder, out long pCursor)
    {
        pCursor = Interlocked.Read(ref _currentPosition);
        return MaResult.MaSuccess;
    }

    public override MaResult OnGetLength(out long length)
    {
        var knownLength = _totalSize ?? (_isCompleted ? GetContiguousPrefixLengthLocked() : (long?)null);
        if (!knownLength.HasValue)
        {
            length = 0;
            return MaResult.MaNotImplemented;
        }

        length = knownLength.Value;
        return MaResult.MaSuccess;
    }

    public void Cancel()
    {
        _cts.Cancel();
        lock (_syncLock)
        {
            Monitor.PulseAll(_syncLock);
        }
    }

    public override void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Cancel();
        if (!_leaveOpen)
        {
            _sourceStream.Dispose();
        }

        if (_downloadTask.IsCompleted)
        {
            CleanupCache();
        }
        else
        {
            _downloadTask.ContinueWith(_ => CleanupCache(), TaskScheduler.Default);
        }
    }

    private async Task RunDownloadTask(int readBufferSize)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(readBufferSize);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var writePosition = Interlocked.Read(ref _backgroundPosition);
                var bytesRead = await ReadSourceRangeAsync(writePosition, readBuffer, readBufferSize, _cts.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                lock (_syncLock)
                {
                    if (_cacheStream == null)
                    {
                        return;
                    }

                    _cacheStream.Position = writePosition;
                    _cacheStream.Write(readBuffer, 0, bytesRead);
                    _cachedRanges.Add(writePosition, bytesRead);
                    _cachedBytes = _cachedRanges.TotalBytes;
                    _backgroundPosition = writePosition + bytesRead;
                    Monitor.PulseAll(_syncLock);
                }

                ProgressChanged?.Invoke(Interlocked.Read(ref _cachedBytes), _totalSize);
            }

            lock (_syncLock)
            {
                if (_totalSize.HasValue && !_cachedRanges.CoversCompleteFile(_totalSize.Value))
                {
                    throw new EndOfStreamException("Stream ended before the expected content length.");
                }

                if (!_totalSize.HasValue)
                {
                    _totalSize = GetContiguousPrefixLengthLocked();
                }

                TryCommitCacheLocked();
                _isCompleted = true;
                ClearLastError();
                Monitor.PulseAll(_syncLock);
            }

            DownloadCompleted?.Invoke(true, null);
        }
        catch (OperationCanceledException)
        {
            lock (_syncLock)
            {
                Monitor.PulseAll(_syncLock);
            }
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
            lock (_syncLock)
            {
                Monitor.PulseAll(_syncLock);
            }
        }
        catch (Exception ex)
        {
            CompleteWithError(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private void CompleteWithError(Exception error)
    {
        lock (_syncLock)
        {
            _error = error;
            _isCompleted = true;
            SetLastError(MaResult.MaIoError, error);
            if (_commitCacheOnComplete)
            {
                CleanupCacheFileLocked();
            }

            Monitor.PulseAll(_syncLock);
        }

        DownloadCompleted?.Invoke(false, error);
    }

    private void CleanupCache()
    {
        lock (_syncLock)
        {
            CleanupCacheFileLocked();
        }

        _sourceReadLock.Dispose();
        _cts.Dispose();
    }

    private void TryCommitCacheLocked()
    {
        if (!_commitCacheOnComplete || _cacheCommitted || _cacheStream == null)
        {
            return;
        }

        if (!_totalSize.HasValue || !_cachedRanges.CoversCompleteFile(_totalSize.Value))
        {
            return;
        }

        _cacheStream.SetLength(_totalSize.Value);
        _cacheStream.Flush(true);
        _cacheStream.Dispose();
        _cacheStream = null;
        if (File.Exists(CacheFilePath))
        {
            File.Delete(CacheFilePath);
        }

        File.Move(PartialFilePath, CacheFilePath);
        _cacheStream = new FileStream(CacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _cacheCommitted = true;
    }

    private void CleanupCacheFileLocked()
    {
        if (_cacheCleaned)
        {
            return;
        }

        _cacheCleaned = true;
        _cacheStream?.Dispose();
        _cacheStream = null;

        if (!_cacheCommitted && (_deleteCacheOnDispose || _commitCacheOnComplete))
        {
            TryDelete(PartialFilePath);
        }
    }

    private void PrepareCacheFiles(bool overwrite)
    {
        if (!_commitCacheOnComplete)
        {
            return;
        }

        var directory = Path.GetDirectoryName(CacheFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (overwrite)
        {
            TryDelete(CacheFilePath);
            TryDelete(PartialFilePath);
            return;
        }

        if (File.Exists(CacheFilePath) || File.Exists(PartialFilePath))
        {
            throw new IOException("The target stream cache file already exists.");
        }
    }

    private long GetContiguousPrefixLengthLocked()
    {
        return _cachedRanges.GetContiguousAvailable(0, long.MaxValue);
    }

    private int ReadSourceRange(long position, byte[] buffer, int count)
    {
        _sourceReadLock.Wait(_cts.Token);
        try
        {
            if (_sourceCanSeek)
            {
                _sourceStream.Seek(position, SeekOrigin.Begin);
            }

            return _sourceStream.Read(buffer, 0, count);
        }
        finally
        {
            _sourceReadLock.Release();
        }
    }

    private async Task<int> ReadSourceRangeAsync(
        long position,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        await _sourceReadLock.WaitAsync(cancellationToken);
        try
        {
            if (_sourceCanSeek)
            {
                _sourceStream.Seek(position, SeekOrigin.Begin);
            }

            return await _sourceStream.ReadAsync(buffer, 0, count, cancellationToken);
        }
        finally
        {
            _sourceReadLock.Release();
        }
    }

    private static string CreateDefaultPartialFilePath()
    {
        return Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.part");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

}
