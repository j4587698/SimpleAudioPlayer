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
    private bool _commitFailedButComplete;
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

    /// <summary>
    /// 当缓存数据已完整下载，但最终提交（重命名为正式缓存文件）失败时为 true。
    /// 此时 <see cref="PartialFilePath"/> 仍保留着完整数据，可供手动恢复，不会被删除。
    /// </summary>
    public bool IsCacheDataCompleteButUncommitted => _commitFailedButComplete;
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
                long sourceReadPosition = -1;
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
                        var read = _cacheStream.Read(rented, 0, bytesToCopy);
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
                        // 需要从可定位源读取：记录位置，离开锁后再做同步 I/O，
                        // 避免持锁阻塞 OnSeek 与后台缓存写入。
                        sourceReadPosition = _currentPosition;
                    }
                    else
                    {
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
                        continue;
                    }
                }

                // 锁外执行源读取（内部由 _sourceReadLock 串行化源访问）。
                int sourceRead;
                try
                {
                    sourceRead = ReadSourceRange(sourceReadPosition, rented, requested);
                }
                catch (OperationCanceledException)
                {
                    return MaResult.MaCancelled;
                }
                catch (Exception ex)
                {
                    lock (_syncLock)
                    {
                        _error ??= ex;
                    }

                    return Fail(MaResult.MaIoError, ex);
                }

                lock (_syncLock)
                {
                    if (_isDisposed) return MaResult.MaError;

                    // OnRead 由单一解码线程调用，正常情况下 _currentPosition 不会在锁外读取期间改变；
                    // 此处的相等判断作为防御，避免位置被改动后写入错误数据。
                    if (sourceRead > 0 && _cacheStream != null && _currentPosition == sourceReadPosition)
                    {
                        _cacheStream.Position = sourceReadPosition;
                        _cacheStream.Write(rented, 0, sourceRead);
                        _cachedRanges.Add(sourceReadPosition, sourceRead);
                        _cachedBytes = _cachedRanges.TotalBytes;
                        _currentPosition += sourceRead;
                        Marshal.Copy(rented, 0, pBuffer, sourceRead);
                        bytesRead = (nuint)sourceRead;
                        ProgressChanged?.Invoke(Interlocked.Read(ref _cachedBytes), _totalSize);
                        TryCommitCacheLocked();
                        Monitor.PulseAll(_syncLock);
                        return MaResult.MaSuccess;
                    }

                    // 源已到末尾（sourceRead == 0）或位置已变化：与“读取缓存失败”一致地处理，
                    // 检查错误/结束/取消并在必要时等待后台进度，避免在声明的 totalSize 大于实际
                    // 流长度等边界下忙等死循环。
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
            _downloadTask.ContinueWith(
                t =>
                {
                    _ = t.Exception;
                    CleanupCache();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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

        try
        {
            if (File.Exists(CacheFilePath))
            {
                File.Delete(CacheFilePath);
            }

            File.Move(PartialFilePath, CacheFilePath);
            _cacheStream = new FileStream(CacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _cacheCommitted = true;
        }
        catch
        {
            // 数据已完整写入分区文件，仅最终重命名失败：保留 .part 供恢复，避免丢失完整数据。
            _commitFailedButComplete = true;
            try
            {
                _cacheStream = new FileStream(PartialFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch
            {
                _cacheStream = null;
            }

            throw;
        }
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

        if (!_cacheCommitted && !_commitFailedButComplete && (_deleteCacheOnDispose || _commitCacheOnComplete))
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
