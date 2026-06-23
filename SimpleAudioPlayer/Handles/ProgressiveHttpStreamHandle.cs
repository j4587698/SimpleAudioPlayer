using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;

namespace SimpleAudioPlayer.Handles;

public sealed class ProgressiveHttpStreamHandle : AudioCallbackHandlerBase
{
    private const int DefaultReadBufferSize = 81920;
    private const int WaitTimeoutMs = 1000;
    private const int MaxRetryCount = 3;
    private const int RetryDelayMs = 500;
    private const int IndexFlushBytes = 512 * 1024;
    private const long PlaybackReuseWindowBytes = 1024 * 1024;

    private readonly string _url;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly bool _deletePartialOnDispose;
    private readonly bool _supportsRange;
    private readonly string? _eTag;
    private readonly DateTimeOffset? _lastModified;
    private readonly object _syncLock = new();
    private readonly CachedByteRangeSet _cachedRanges = new();

    private FileStream? _cacheStream;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _playbackCts;
    private Task? _downloadTask;
    private Task? _playbackTask;
    private Exception? _downloadError;
    private Exception? _playbackError;
    private ProgressiveDownloadState _downloadState = ProgressiveDownloadState.Downloading;

    private long _fileSize;
    private long _currentPosition;
    private long _downloadedBytes;
    private long _bytesSinceIndexFlush;
    private long _activePlaybackStart = -1;
    private long _activePlaybackPosition;
    private bool _activePlaybackCompleted = true;
    private bool _disposed;

    private ProgressiveHttpStreamHandle(
        string url,
        HttpClient httpClient,
        HttpResourceInfo resourceInfo,
        string finalFilePath,
        bool overwrite,
        bool resume,
        bool deletePartialOnDispose,
        bool disposeHttpClient,
        int readBufferSize)
    {
        _url = url;
        _httpClient = httpClient;
        _fileSize = resourceInfo.FileSize ?? 0;
        _supportsRange = resourceInfo.SupportsRange;
        _eTag = resourceInfo.ETag;
        _lastModified = resourceInfo.LastModified;
        _disposeHttpClient = disposeHttpClient;
        _deletePartialOnDispose = deletePartialOnDispose;
        FinalFilePath = Path.GetFullPath(finalFilePath);
        PartialFilePath = FinalFilePath + ".part";
        IndexFilePath = PartialFilePath + ".idx";

        PrepareOutputFiles(overwrite, resume);
        InitializeCache(resume, readBufferSize);
        StartCompleteDownload(readBufferSize);
    }

    public event Action<long, long?>? ProgressChanged;
    public event EventHandler<ProgressiveDownloadStateChangedEventArgs>? DownloadStateChanged;

    public string PartialFilePath { get; }
    public string FinalFilePath { get; }
    public string IndexFilePath { get; }
    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);
    public long TotalBytes => Interlocked.Read(ref _fileSize);
    public override bool CanSeek => _supportsRange;
    public ProgressiveDownloadState DownloadState => _downloadState;

    public static async Task<ProgressiveHttpStreamHandle> CreateAsync(
        string url,
        string finalFilePath,
        HttpClient? client = null,
        int readBufferSize = DefaultReadBufferSize,
        bool overwrite = false,
        bool deletePartialOnDispose = false,
        bool resume = true)
    {
        if (readBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readBufferSize));
        }

        var httpClient = client ?? new HttpClient();
        var resourceInfo = await ProbeResourceAsync(httpClient, url);

        return new ProgressiveHttpStreamHandle(
            url,
            httpClient,
            resourceInfo,
            finalFilePath,
            overwrite,
            resume,
            deletePartialOnDispose,
            client == null,
            readBufferSize);
    }

    public override MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead)
    {
        bytesRead = 0;
        if (_disposed) return MaResult.MaError;
        if (bytesToRead > int.MaxValue) return MaResult.MaInvalidArgs;

        return ReadFromCacheOrDownload(pBuffer, (int)bytesToRead, out bytesRead);
    }

    public override MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin)
    {
        if (!_supportsRange)
        {
            return MaResult.MaNotImplemented;
        }

        var fileSize = Interlocked.Read(ref _fileSize);
        if (origin == SeekOrigin.End && fileSize <= 0)
        {
            return MaResult.MaNotImplemented;
        }

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Interlocked.Read(ref _currentPosition) + offset,
            SeekOrigin.End => fileSize + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0 || fileSize > 0 && target > fileSize)
        {
            return MaResult.MaInvalidArgs;
        }

        lock (_syncLock)
        {
            _currentPosition = target;
            CancelPlaybackDownloadLocked();
            Monitor.PulseAll(_syncLock);
        }

        return MaResult.MaSuccess;
    }

    public override MaResult OnTell(IntPtr pDecoder, out long pCursor)
    {
        pCursor = Interlocked.Read(ref _currentPosition);
        return MaResult.MaSuccess;
    }

    public override MaResult OnGetLength(out long length)
    {
        var fileSize = Interlocked.Read(ref _fileSize);
        if (fileSize <= 0)
        {
            length = 0;
            return MaResult.MaNotImplemented;
        }

        length = fileSize;
        return MaResult.MaSuccess;
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelDownload(ProgressiveDownloadState.Cancelled);

        lock (_syncLock)
        {
            CancelPlaybackDownloadLocked();
            if (!_deletePartialOnDispose && _downloadState != ProgressiveDownloadState.Completed)
            {
                PersistIndexLocked(force: true);
            }

            _cacheStream?.Dispose();
            _cacheStream = null;
            Monitor.PulseAll(_syncLock);
        }

        _downloadCts?.Dispose();
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        if (_deletePartialOnDispose)
        {
            TryDelete(PartialFilePath);
            TryDelete(IndexFilePath);
        }
    }

    private MaResult ReadFromCacheOrDownload(IntPtr pBuffer, int bytesToRead, out nuint bytesRead)
    {
        bytesRead = 0;
        if (bytesToRead == 0)
        {
            return MaResult.MaSuccess;
        }

        var rented = ArrayPool<byte>.Shared.Rent(bytesToRead);
        try
        {
            while (true)
            {
                lock (_syncLock)
                {
                    if (_disposed)
                    {
                        return MaResult.MaError;
                    }

                    var available = _cachedRanges.GetContiguousAvailable(_currentPosition, bytesToRead);
                    if (available > 0 && _cacheStream != null)
                    {
                        var bytesToCopy = (int)Math.Min(bytesToRead, available);
                        _cacheStream.Position = _currentPosition;
                        var read = _cacheStream.Read(rented, 0, bytesToCopy);
                        if (read > 0)
                        {
                            _currentPosition += read;
                            Marshal.Copy(rented, 0, pBuffer, read);
                            bytesRead = (nuint)read;
                            ClearLastError();
                            return MaResult.MaSuccess;
                        }
                    }

                    if (IsAtEndLocked())
                    {
                        return MaResult.MaAtEnd;
                    }

                    if (_playbackError != null)
                    {
                        return Fail(MaResult.MaIoError, _playbackError);
                    }

                    if (_downloadState is ProgressiveDownloadState.Failed or ProgressiveDownloadState.Cancelled
                        && !_supportsRange)
                    {
                        return Fail(MaResult.MaIoError, _downloadError);
                    }

                    if (_supportsRange)
                    {
                        EnsurePlaybackDownloadLocked(_currentPosition);
                    }

                    Monitor.Wait(_syncLock, WaitTimeoutMs);
                }
            }
        }
        catch (Exception ex)
        {
            return Fail(MaResult.MaError, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void StartCompleteDownload(int readBufferSize)
    {
        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        _downloadTask = Task.Run(() => RunCompleteDownloadAsync(readBufferSize, cts.Token), cts.Token);
    }

    private async Task RunCompleteDownloadAsync(int readBufferSize, CancellationToken cancellationToken)
    {
        try
        {
            if (Interlocked.Read(ref _fileSize) <= 0)
            {
                var end = await DownloadRangeAsync(0, null, readBufferSize, cancellationToken, isPlaybackDownload: false);
                Interlocked.Exchange(ref _fileSize, end);
                CompleteDownload();
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                long? start;
                long? stopBefore;
                var fileSize = Interlocked.Read(ref _fileSize);
                lock (_syncLock)
                {
                    start = _cachedRanges.FindFirstMissing(fileSize);
                    if (!start.HasValue)
                    {
                        break;
                    }

                    stopBefore = _cachedRanges.FindNextRangeStartAfter(start.Value);
                    if (stopBefore > fileSize)
                    {
                        stopBefore = fileSize;
                    }
                }

                await DownloadRangeWithRetryAsync(
                    start.Value,
                    stopBefore,
                    readBufferSize,
                    cancellationToken,
                    isPlaybackDownload: false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            CompleteDownload();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            FailDownload(ex);
        }
    }

    private async Task DownloadRangeWithRetryAsync(
        long start,
        long? stopBefore,
        int readBufferSize,
        CancellationToken cancellationToken,
        bool isPlaybackDownload)
    {
        var nextPosition = start;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadRangeAsync(
                    nextPosition,
                    stopBefore,
                    readBufferSize,
                    cancellationToken,
                    isPlaybackDownload);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (attempt < MaxRetryCount)
            {
                lock (_syncLock)
                {
                    nextPosition = isPlaybackDownload
                        ? _activePlaybackPosition
                        : _cachedRanges.GetContiguousAvailable(start, long.MaxValue) > 0
                            ? start + _cachedRanges.GetContiguousAvailable(start, long.MaxValue)
                            : start;
                }

                await Task.Delay(RetryDelayMs, cancellationToken);
            }
        }
    }

    private async Task<long> DownloadRangeAsync(
        long start,
        long? stopBefore,
        int readBufferSize,
        CancellationToken cancellationToken,
        bool isPlaybackDownload)
    {
        if (stopBefore.HasValue && stopBefore.Value <= start)
        {
            return start;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(readBufferSize);
        try
        {
            await using var responseStream = await OpenHttpStreamAsync(start, cancellationToken);
            var nextPosition = start;
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesToRequest = buffer.Length;
                if (stopBefore.HasValue)
                {
                    var remaining = stopBefore.Value - nextPosition;
                    if (remaining <= 0)
                    {
                        return nextPosition;
                    }

                    bytesToRequest = (int)Math.Min(bytesToRequest, remaining);
                }

                var read = await responseStream.ReadAsync(buffer, 0, bytesToRequest, cancellationToken);
                if (read == 0)
                {
                    var fileSize = Interlocked.Read(ref _fileSize);
                    if (fileSize > 0 && nextPosition < fileSize && (!stopBefore.HasValue || nextPosition < stopBefore.Value))
                    {
                        throw new EndOfStreamException("HTTP stream ended before the expected content length.");
                    }

                    return nextPosition;
                }

                var downloaded = WriteCacheBytes(nextPosition, buffer, read, isPlaybackDownload);
                nextPosition += read;
                var currentFileSize = Interlocked.Read(ref _fileSize);
                ProgressChanged?.Invoke(downloaded, currentFileSize > 0 ? currentFileSize : null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return nextPosition;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private long WriteCacheBytes(long position, byte[] data, int count, bool isPlaybackDownload)
    {
        lock (_syncLock)
        {
            if (_cacheStream == null)
            {
                return Interlocked.Read(ref _downloadedBytes);
            }

            _cacheStream.Position = position;
            _cacheStream.Write(data, 0, count);
            _cachedRanges.Add(position, count);
            var downloaded = _cachedRanges.TotalBytes;
            Interlocked.Exchange(ref _downloadedBytes, downloaded);

            if (isPlaybackDownload)
            {
                _activePlaybackPosition = Math.Max(_activePlaybackPosition, position + count);
            }

            _bytesSinceIndexFlush += count;
            PersistIndexLocked(force: false);
            Monitor.PulseAll(_syncLock);
            return downloaded;
        }
    }

    private void EnsurePlaybackDownloadLocked(long startPosition)
    {
        var fileSize = Interlocked.Read(ref _fileSize);
        if (!_supportsRange || fileSize > 0 && startPosition >= fileSize)
        {
            return;
        }

        var canReuseActiveDownload =
            !_activePlaybackCompleted
            && _playbackCts?.IsCancellationRequested == false
            && startPosition >= _activePlaybackStart
            && startPosition <= _activePlaybackPosition + PlaybackReuseWindowBytes;

        if (canReuseActiveDownload)
        {
            return;
        }

        CancelPlaybackDownloadLocked();

        var cts = new CancellationTokenSource();
        _playbackCts = cts;
        _playbackError = null;
        _activePlaybackStart = startPosition;
        _activePlaybackPosition = startPosition;
        _activePlaybackCompleted = false;
        _playbackTask = Task.Run(() => RunPlaybackDownloadAsync(startPosition, cts), cts.Token);
    }

    private async Task RunPlaybackDownloadAsync(long startPosition, CancellationTokenSource cts)
    {
        try
        {
            await DownloadRangeWithRetryAsync(
                startPosition,
                null,
                DefaultReadBufferSize,
                cts.Token,
                isPlaybackDownload: true);

            lock (_syncLock)
            {
                if (ReferenceEquals(_playbackCts, cts))
                {
                    _activePlaybackCompleted = true;
                    Monitor.PulseAll(_syncLock);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            lock (_syncLock)
            {
                if (ReferenceEquals(_playbackCts, cts))
                {
                    _playbackError = ex;
                    _activePlaybackCompleted = true;
                    Monitor.PulseAll(_syncLock);
                }
            }
        }
    }

    private void CompleteDownload()
    {
        lock (_syncLock)
        {
            if (_cacheStream == null)
            {
                return;
            }

            if (Interlocked.Read(ref _fileSize) <= 0)
            {
                Interlocked.Exchange(ref _fileSize, _cachedRanges.TotalBytes);
            }

            var fileSize = Interlocked.Read(ref _fileSize);
            if (fileSize <= 0 || !_cachedRanges.CoversCompleteFile(fileSize))
            {
                return;
            }

            CancelPlaybackDownloadLocked();
            _cacheStream.SetLength(fileSize);
            _cacheStream.Flush(true);
            _cacheStream.Dispose();
            _cacheStream = null;

            try
            {
                if (File.Exists(FinalFilePath))
                {
                    File.Delete(FinalFilePath);
                }

                File.Move(PartialFilePath, FinalFilePath);
                TryDelete(IndexFilePath);
                _cacheStream = new FileStream(FinalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                SetDownloadState(ProgressiveDownloadState.Completed, null);
            }
            catch
            {
                try
                {
                    _cacheStream = new FileStream(PartialFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                catch
                {
                    _cacheStream = null;
                }
            }
            ClearLastError();
            Monitor.PulseAll(_syncLock);
        }
    }

    private void FailDownload(Exception error)
    {
        lock (_syncLock)
        {
            _downloadError = error;
            SetLastError(MaResult.MaIoError, error);
            SetDownloadState(ProgressiveDownloadState.Failed, error);
            PersistIndexLocked(force: true);
            Monitor.PulseAll(_syncLock);
        }
    }

    private void CancelDownload(ProgressiveDownloadState state)
    {
        _downloadCts?.Cancel();
        if (_downloadState == ProgressiveDownloadState.Downloading)
        {
            lock (_syncLock)
            {
                SetDownloadState(state, null);
                Monitor.PulseAll(_syncLock);
            }
        }
    }

    private void CancelPlaybackDownloadLocked()
    {
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();
        _playbackCts = null;
        _activePlaybackCompleted = true;
        Monitor.PulseAll(_syncLock);
    }

    private bool IsAtEndLocked()
    {
        var fileSize = Interlocked.Read(ref _fileSize);
        return fileSize > 0 && _currentPosition >= fileSize
            || _downloadState == ProgressiveDownloadState.Completed && _cachedRanges.GetContiguousAvailable(_currentPosition, 1) == 0;
    }

    private async Task<Stream> OpenHttpStreamAsync(long startPosition, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _url);
        if (_supportsRange)
        {
            request.Headers.Range = new RangeHeaderValue(startPosition, null);
        }
        else if (startPosition > 0)
        {
            throw new NotSupportedException("HTTP server does not support range requests.");
        }

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (_supportsRange && response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.Dispose();
            throw new InvalidDataException("HTTP server did not return a partial response for the requested range.");
        }

        var expectedLength = response.Content.Headers.ContentLength;
        var fileSize = Interlocked.Read(ref _fileSize);
        if (fileSize > 0 && expectedLength.HasValue && startPosition + expectedLength.Value > fileSize)
        {
            response.Dispose();
            throw new InvalidDataException("HTTP response length exceeds the declared file size.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new ResponseStream(response, stream);
    }

    private void InitializeCache(bool resume, int readBufferSize)
    {
        var loaded = false;
        if (resume && File.Exists(PartialFilePath) && File.Exists(IndexFilePath))
        {
            var index = ProgressiveHttpCacheIndex.TryLoad(IndexFilePath);
            if (index != null && index.Matches(_url, _fileSize, _eTag, _lastModified))
            {
                var partLength = new FileInfo(PartialFilePath).Length;
                var validRanges = index.Ranges
                    .Where(range => range.Start >= 0
                        && range.Length > 0
                        && range.EndExclusive <= partLength
                        && (_fileSize <= 0 || range.EndExclusive <= _fileSize))
                    .ToArray();

                if (validRanges.Length == index.Ranges.Count)
                {
                    _cachedRanges.Replace(validRanges);
                    Interlocked.Exchange(ref _downloadedBytes, _cachedRanges.TotalBytes);
                    loaded = true;
                }
            }
        }

        if (!loaded)
        {
            TryDelete(PartialFilePath);
            TryDelete(IndexFilePath);
        }

        _cacheStream = new FileStream(
            PartialFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            readBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        if (_fileSize > 0 && _cacheStream.Length > _fileSize)
        {
            _cacheStream.SetLength(_fileSize);
        }
    }

    private void PersistIndexLocked(bool force)
    {
        if (_downloadState == ProgressiveDownloadState.Completed || _cacheStream == null)
        {
            return;
        }

        if (!force && _bytesSinceIndexFlush < IndexFlushBytes)
        {
            return;
        }

        _cacheStream.Flush(false);
        var index = ProgressiveHttpCacheIndex.Create(
            _url,
            Interlocked.Read(ref _fileSize),
            _eTag,
            _lastModified,
            _cachedRanges.Ranges);
        index.Save(IndexFilePath);
        _bytesSinceIndexFlush = 0;
    }

    private void PrepareOutputFiles(bool overwrite, bool resume)
    {
        var directory = Path.GetDirectoryName(FinalFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (overwrite)
        {
            TryDelete(FinalFilePath);
            TryDelete(PartialFilePath);
            TryDelete(IndexFilePath);
            return;
        }

        if (File.Exists(FinalFilePath))
        {
            throw new IOException("The target progressive download file already exists.");
        }

        if (!resume && (File.Exists(PartialFilePath) || File.Exists(IndexFilePath)))
        {
            throw new IOException("The target progressive download file already exists.");
        }
    }

    private void SetDownloadState(ProgressiveDownloadState state, Exception? error)
    {
        if (_downloadState == state && error == null)
        {
            return;
        }

        _downloadState = state;
        DownloadStateChanged?.Invoke(
            this,
            new ProgressiveDownloadStateChangedEventArgs(
                state,
                error,
                PartialFilePath,
                state == ProgressiveDownloadState.Completed ? FinalFilePath : null));
    }

    private static async Task<HttpResourceInfo> ProbeResourceAsync(HttpClient httpClient, string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Headers = { Range = new RangeHeaderValue(0, 0) }
            };
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var supportsRange = response.StatusCode == HttpStatusCode.PartialContent;
            long? fileSize = null;
            if (supportsRange)
            {
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.HasLength == true)
                {
                    fileSize = contentRange.Length;
                }
            }

            fileSize ??= response.Content.Headers.ContentLength > 0
                ? response.Content.Headers.ContentLength
                : null;

            return new HttpResourceInfo(
                supportsRange,
                fileSize,
                response.Headers.ETag?.Tag,
                response.Content.Headers.LastModified);
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to check range support.", ex);
        }
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

    private readonly record struct HttpResourceInfo(
        bool SupportsRange,
        long? FileSize,
        string? ETag,
        DateTimeOffset? LastModified);

    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
        }
    }
}
