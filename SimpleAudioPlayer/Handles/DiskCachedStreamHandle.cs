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
    private readonly CancellationTokenSource _cts = new();
    private readonly object _syncLock = new();
    private readonly Task _downloadTask;
    private readonly FileStream _cacheStream;

    private long _totalDownloaded;
    private long _currentPosition;
    private long? _totalSize;
    private bool _isCompleted;
    private bool _isDisposed;
    private bool _cacheCleaned;
    private Exception? _error;

    public DiskCachedStreamHandle(
        Stream stream,
        int bufferSize = DefaultBufferSize,
        long totalSize = -1,
        string? cacheFilePath = null,
        bool? deleteCacheOnDispose = null,
        bool enableSeek = false,
        bool leaveOpen = false)
    {
        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

        _sourceStream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        _enableSeek = enableSeek;
        _totalSize = totalSize > 0 ? totalSize : stream.CanSeek ? stream.Length : null;
        CacheFilePath = cacheFilePath ?? Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sapcache");
        _deleteCacheOnDispose = deleteCacheOnDispose ?? cacheFilePath == null;

        var directory = Path.GetDirectoryName(CacheFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _cacheStream = new FileStream(
            CacheFilePath,
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
    public long CachedBytes => Interlocked.Read(ref _totalDownloaded);
    public long? TotalSize => _totalSize;
    public bool IsCompleted => _isCompleted;
    public override bool CanSeek => _enableSeek && _totalSize.HasValue;

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

                    var available = _totalDownloaded - _currentPosition;
                    if (available > 0)
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

                    if (_error != null)
                    {
                        return Fail(MaResult.MaIoError, _error);
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
            while (target > _totalDownloaded)
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
        var knownLength = _totalSize ?? (_isCompleted ? _totalDownloaded : (long?)null);
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
                var bytesRead = await _sourceStream.ReadAsync(readBuffer, 0, readBufferSize, _cts.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                lock (_syncLock)
                {
                    _cacheStream.Position = _totalDownloaded;
                    _cacheStream.Write(readBuffer, 0, bytesRead);
                    _totalDownloaded += bytesRead;
                    Monitor.PulseAll(_syncLock);
                }

                ProgressChanged?.Invoke(Interlocked.Read(ref _totalDownloaded), _totalSize);
            }

            lock (_syncLock)
            {
                if (!_totalSize.HasValue)
                {
                    _totalSize = _totalDownloaded;
                }

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
            Monitor.PulseAll(_syncLock);
        }

        DownloadCompleted?.Invoke(false, error);
    }

    private void CleanupCache()
    {
        lock (_syncLock)
        {
            if (_cacheCleaned)
            {
                return;
            }

            _cacheCleaned = true;
            _cacheStream.Dispose();
        }

        _cts.Dispose();
        if (_deleteCacheOnDispose)
        {
            try
            {
                File.Delete(CacheFilePath);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
