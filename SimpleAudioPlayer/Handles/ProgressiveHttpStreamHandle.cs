using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;
using SimpleAudioPlayer.Utils;

namespace SimpleAudioPlayer.Handles;

public sealed class ProgressiveHttpStreamHandle : AudioCallbackHandlerBase
{
    private const int DefaultReadBufferSize = 81920;
    private const int PlaybackBufferSize = 1 * 1024 * 1024;
    private const int WaitTimeoutMs = 1000;
    private const int MaxRetryCount = 3;
    private const int RetryDelayMs = 500;

    private readonly string _url;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly bool _deletePartialOnDispose;
    private readonly bool _supportsRange;
    private readonly long _fileSize;
    private readonly object _syncLock = new();
    private readonly byte[] _playbackBuffer = new byte[PlaybackBufferSize];

    private FileStream? _cacheStream;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _streamCts;
    private Task? _downloadTask;
    private Task? _streamTask;
    private Exception? _downloadError;
    private Exception? _streamError;
    private ProgressiveDownloadState _downloadState = ProgressiveDownloadState.Downloading;

    private long _prefixDownloaded;
    private long _currentPosition;
    private long _streamDownloadPosition;
    private int _readPos;
    private int _writePos;
    private int _bytesAvailable;
    private bool _streamCompleted;
    private bool _useStreamingPlayback;
    private bool _disposed;
    private int _userSeekDepth;

    private ProgressiveHttpStreamHandle(
        string url,
        HttpClient httpClient,
        long fileSize,
        bool supportsRange,
        string finalFilePath,
        bool overwrite,
        bool deletePartialOnDispose,
        bool disposeHttpClient,
        int readBufferSize)
    {
        _url = url;
        _httpClient = httpClient;
        _fileSize = fileSize;
        _supportsRange = supportsRange;
        _disposeHttpClient = disposeHttpClient;
        _deletePartialOnDispose = deletePartialOnDispose;
        FinalFilePath = Path.GetFullPath(finalFilePath);
        PartialFilePath = FinalFilePath + ".part";

        PrepareOutputFiles(overwrite);
        _cacheStream = new FileStream(
            PartialFilePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            readBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        StartCompleteDownload(readBufferSize);
    }

    public event Action<long, long?>? ProgressChanged;
    public event EventHandler<ProgressiveDownloadStateChangedEventArgs>? DownloadStateChanged;

    public string PartialFilePath { get; }
    public string FinalFilePath { get; }
    public long DownloadedBytes => Interlocked.Read(ref _prefixDownloaded);
    public long TotalBytes => _fileSize;
    public ProgressiveDownloadState DownloadState => _downloadState;
    public override bool CanSeek => _supportsRange;

    public static async Task<ProgressiveHttpStreamHandle> CreateAsync(
        string url,
        string finalFilePath,
        HttpClient? client = null,
        int readBufferSize = DefaultReadBufferSize,
        bool overwrite = false,
        bool deletePartialOnDispose = false)
    {
        if (readBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readBufferSize));
        }

        var httpClient = client ?? new HttpClient();
        var rangeInfo = await httpClient.CheckRangeSupportWithSizeAsync(url);

        return new ProgressiveHttpStreamHandle(
            url,
            httpClient,
            rangeInfo.FileSize ?? 0,
            rangeInfo.SupportsRange,
            finalFilePath,
            overwrite,
            deletePartialOnDispose,
            client == null,
            readBufferSize);
    }

    public override MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead)
    {
        bytesRead = 0;
        if (_disposed) return MaResult.MaError;
        if (bytesToRead > int.MaxValue) return MaResult.MaInvalidArgs;

        return _useStreamingPlayback
            ? ReadFromStreamingBuffer(pBuffer, (int)bytesToRead, out bytesRead)
            : ReadFromProgressiveFile(pBuffer, (int)bytesToRead, out bytesRead);
    }

    public override MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin)
    {
        if (!_supportsRange)
        {
            return MaResult.MaNotImplemented;
        }

        if (origin == SeekOrigin.End && _fileSize <= 0)
        {
            return MaResult.MaNotImplemented;
        }

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Interlocked.Read(ref _currentPosition) + offset,
            SeekOrigin.End => _fileSize + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0 || _fileSize > 0 && target > _fileSize)
        {
            return MaResult.MaInvalidArgs;
        }

        var seekedToCache = false;
        var shouldCancelRangeRead = false;
        lock (_syncLock)
        {
            if (_downloadState != ProgressiveDownloadState.Incomplete && target <= _prefixDownloaded)
            {
                seekedToCache = true;
                shouldCancelRangeRead = _useStreamingPlayback;
                _useStreamingPlayback = false;
                _currentPosition = target;
                Monitor.PulseAll(_syncLock);
            }
        }

        if (seekedToCache)
        {
            if (shouldCancelRangeRead)
            {
                CancelStreamingPlayback();
            }

            return MaResult.MaSuccess;
        }

        var isUserSeek = Interlocked.CompareExchange(ref _userSeekDepth, 0, 0) > 0;
        if (isUserSeek)
        {
            MarkDownloadIncomplete();
        }

        StartStreamingPlayback(target);
        return MaResult.MaSuccess;
    }

    public override MaResult OnTell(IntPtr pDecoder, out long pCursor)
    {
        pCursor = Interlocked.Read(ref _currentPosition);
        return MaResult.MaSuccess;
    }

    public override MaResult OnGetLength(out long length)
    {
        if (_fileSize <= 0)
        {
            length = 0;
            return MaResult.MaNotImplemented;
        }

        length = _fileSize;
        return MaResult.MaSuccess;
    }

    public override bool Seek(AudioContextHandle ctx, double time)
    {
        Interlocked.Increment(ref _userSeekDepth);
        try
        {
            return base.Seek(ctx, time);
        }
        finally
        {
            Interlocked.Decrement(ref _userSeekDepth);
        }
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelDownload(ProgressiveDownloadState.Cancelled);
        CancelStreamingPlayback();

        lock (_syncLock)
        {
            _cacheStream?.Dispose();
            _cacheStream = null;
            Monitor.PulseAll(_syncLock);
        }

        _downloadCts?.Dispose();
        _streamCts?.Dispose();
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        if (_deletePartialOnDispose && File.Exists(PartialFilePath))
        {
            TryDelete(PartialFilePath);
        }
    }

    private MaResult ReadFromProgressiveFile(IntPtr pBuffer, int bytesToRead, out nuint bytesRead)
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
                    if (_downloadError != null)
                    {
                        return Fail(MaResult.MaIoError, _downloadError);
                    }

                    var available = _prefixDownloaded - _currentPosition;
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
                            return MaResult.MaSuccess;
                        }
                    }

                    if (_downloadState == ProgressiveDownloadState.Completed &&
                        (_fileSize <= 0 || _currentPosition >= _fileSize))
                    {
                        return MaResult.MaAtEnd;
                    }

                    if (_downloadState is ProgressiveDownloadState.Failed or ProgressiveDownloadState.Cancelled)
                    {
                        return Fail(MaResult.MaIoError, _downloadError);
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

    private MaResult ReadFromStreamingBuffer(IntPtr pBuffer, int bytesToRead, out nuint bytesRead)
    {
        bytesRead = 0;
        var remaining = bytesToRead;

        try
        {
            while (remaining > 0)
            {
                lock (_syncLock)
                {
                    if (_bytesAvailable > 0)
                    {
                        var read = ReadFromRingBuffer(pBuffer, bytesToRead - remaining, Math.Min(remaining, _bytesAvailable));
                        _currentPosition += read;
                        bytesRead = (nuint)((int)bytesRead + read);
                        remaining -= read;

                        if (PlaybackBufferSize - _bytesAvailable > PlaybackBufferSize / 2)
                        {
                            Monitor.PulseAll(_syncLock);
                        }

                        continue;
                    }

                    if (_streamError != null)
                    {
                        return bytesRead == 0
                            ? Fail(MaResult.MaIoError, _streamError)
                            : MaResult.MaSuccess;
                    }

                    if (_streamCompleted)
                    {
                        return bytesRead == 0 ? MaResult.MaAtEnd : MaResult.MaSuccess;
                    }

                    Monitor.Wait(_syncLock, WaitTimeoutMs);
                }
            }

            return MaResult.MaSuccess;
        }
        catch (Exception ex)
        {
            return Fail(MaResult.MaError, ex);
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
        var buffer = ArrayPool<byte>.Shared.Rent(readBufferSize);
        try
        {
            await using var responseStream = await OpenHttpStreamAsync(0, cancellationToken);
            var nextPosition = 0L;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await responseStream.ReadAsync(buffer, 0, readBufferSize, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                lock (_syncLock)
                {
                    if (_cacheStream == null)
                    {
                        return;
                    }

                    _cacheStream.Position = nextPosition;
                    _cacheStream.Write(buffer, 0, read);
                    nextPosition += read;
                    _prefixDownloaded = nextPosition;
                    Monitor.PulseAll(_syncLock);
                }

                ProgressChanged?.Invoke(
                    Interlocked.Read(ref _prefixDownloaded),
                    _fileSize > 0 ? _fileSize : null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_fileSize > 0 && nextPosition < _fileSize)
            {
                throw new EndOfStreamException("HTTP stream ended before the expected content length.");
            }

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
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void StartStreamingPlayback(long startPosition)
    {
        if (!_supportsRange)
        {
            lock (_syncLock)
            {
                _streamError = new NotSupportedException("HTTP server does not support range requests.");
                _streamCompleted = true;
                Monitor.PulseAll(_syncLock);
            }

            return;
        }

        CancelStreamingPlayback();

        lock (_syncLock)
        {
            _readPos = 0;
            _writePos = 0;
            _bytesAvailable = 0;
            _currentPosition = startPosition;
            _streamDownloadPosition = startPosition;
            _streamCompleted = false;
            _streamError = null;
            _useStreamingPlayback = true;
            Monitor.PulseAll(_syncLock);
        }

        if (_fileSize > 0 && startPosition >= _fileSize)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _streamCts = cts;
        _streamTask = Task.Run(() => RunStreamingPlaybackAsync(startPosition, cts.Token), cts.Token);
    }

    private async Task RunStreamingPlaybackAsync(long startPosition, CancellationToken cancellationToken)
    {
        var nextPosition = startPosition;
        var retryCount = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    nextPosition = await DownloadPlaybackSegmentAsync(nextPosition, cancellationToken);
                    lock (_syncLock)
                    {
                        _streamCompleted = true;
                        Monitor.PulseAll(_syncLock);
                    }

                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception) when (retryCount < MaxRetryCount)
                {
                    retryCount++;
                    lock (_syncLock)
                    {
                        nextPosition = _streamDownloadPosition;
                    }

                    await Task.Delay(RetryDelayMs, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            lock (_syncLock)
            {
                _streamError = ex;
                _streamCompleted = true;
                Monitor.PulseAll(_syncLock);
            }
        }
    }

    private async Task<long> DownloadPlaybackSegmentAsync(long startPosition, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DefaultReadBufferSize);
        try
        {
            await using var responseStream = await OpenHttpStreamAsync(startPosition, cancellationToken);
            var nextPosition = startPosition;
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesToRequest;
                lock (_syncLock)
                {
                    while (PlaybackBufferSize - _bytesAvailable == 0 && !cancellationToken.IsCancellationRequested)
                    {
                        Monitor.Wait(_syncLock, WaitTimeoutMs);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    bytesToRequest = Math.Min(buffer.Length, PlaybackBufferSize - _bytesAvailable);
                }

                var read = await responseStream.ReadAsync(buffer, 0, bytesToRequest, cancellationToken);
                if (read == 0)
                {
                    if (_fileSize > 0 && nextPosition < _fileSize)
                    {
                        throw new EndOfStreamException("HTTP stream ended before the expected content length.");
                    }

                    return nextPosition;
                }

                lock (_syncLock)
                {
                    WriteToRingBuffer(buffer, 0, read);
                    _bytesAvailable += read;
                    nextPosition += read;
                    _streamDownloadPosition = nextPosition;
                    Monitor.PulseAll(_syncLock);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return nextPosition;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

        if (_supportsRange && startPosition > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.Dispose();
            throw new InvalidDataException("HTTP server did not return a partial response for the requested range.");
        }

        var expectedLength = response.Content.Headers.ContentLength;
        if (_fileSize > 0 && expectedLength.HasValue && startPosition + expectedLength.Value > _fileSize)
        {
            response.Dispose();
            throw new InvalidDataException("HTTP response length exceeds the declared file size.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new ResponseStream(response, stream);
    }

    private unsafe int ReadFromRingBuffer(IntPtr dest, int offset, int count)
    {
        var read = 0;
        var destPtr = (byte*)dest.ToPointer() + offset;

        while (count > 0 && _bytesAvailable > 0)
        {
            var bytesToCopy = Math.Min(count,
                _readPos < _writePos
                    ? _writePos - _readPos
                    : PlaybackBufferSize - _readPos);

            fixed (byte* src = &_playbackBuffer[_readPos])
            {
                Buffer.MemoryCopy(src, destPtr, bytesToCopy, bytesToCopy);
            }

            _readPos = (_readPos + bytesToCopy) % PlaybackBufferSize;
            _bytesAvailable -= bytesToCopy;
            destPtr += bytesToCopy;
            count -= bytesToCopy;
            read += bytesToCopy;
        }

        return read;
    }

    private void WriteToRingBuffer(byte[] data, int offset, int count)
    {
        var firstChunk = Math.Min(count, PlaybackBufferSize - _writePos);
        Buffer.BlockCopy(data, offset, _playbackBuffer, _writePos, firstChunk);
        _writePos = (_writePos + firstChunk) % PlaybackBufferSize;

        var remaining = count - firstChunk;
        if (remaining > 0)
        {
            Buffer.BlockCopy(data, offset + firstChunk, _playbackBuffer, _writePos, remaining);
            _writePos = (_writePos + remaining) % PlaybackBufferSize;
        }
    }

    private void CompleteDownload()
    {
        lock (_syncLock)
        {
            if (_downloadState == ProgressiveDownloadState.Incomplete || _cacheStream == null)
            {
                return;
            }

            _cacheStream.Flush(true);
            _cacheStream.Dispose();
            _cacheStream = null;
            if (File.Exists(FinalFilePath))
            {
                File.Delete(FinalFilePath);
            }

            File.Move(PartialFilePath, FinalFilePath);
            _cacheStream = new FileStream(FinalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            SetDownloadState(ProgressiveDownloadState.Completed, null);
            ClearLastError();
            Monitor.PulseAll(_syncLock);
        }
    }

    private void MarkDownloadIncomplete()
    {
        var oldCts = _downloadCts;
        if (_downloadState == ProgressiveDownloadState.Downloading)
        {
            oldCts?.Cancel();
            lock (_syncLock)
            {
                SetDownloadState(ProgressiveDownloadState.Incomplete, null);
                _cacheStream?.Flush();
                Monitor.PulseAll(_syncLock);
            }
        }
    }

    private void FailDownload(Exception error)
    {
        lock (_syncLock)
        {
            _downloadError = error;
            SetLastError(MaResult.MaIoError, error);
            SetDownloadState(ProgressiveDownloadState.Failed, error);
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

    private void CancelStreamingPlayback()
    {
        _streamCts?.Cancel();
        lock (_syncLock)
        {
            Monitor.PulseAll(_syncLock);
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

    private void PrepareOutputFiles(bool overwrite)
    {
        var directory = Path.GetDirectoryName(FinalFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!overwrite && (File.Exists(FinalFilePath) || File.Exists(PartialFilePath)))
        {
            throw new IOException("The target progressive download file already exists.");
        }

        if (overwrite)
        {
            TryDelete(FinalFilePath);
            TryDelete(PartialFilePath);
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
            GC.SuppressFinalize(this);
        }
    }
}
