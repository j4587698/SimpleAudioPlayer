using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;
using SimpleAudioPlayer.Utils;

namespace SimpleAudioPlayer.Handles;

public sealed class HttpStreamHandle : AudioCallbackHandlerBase
{
    private const int BufferSize = 1 * 1024 * 1024; // 1MB环形缓冲区
    private const int WaitTimeoutMs = 1000;
    private const int MaxRetryCount = 3;
    private const int RetryDelayMs = 500;

    private readonly string _url;
    private readonly HttpClient _httpClient;
    private readonly bool _needDispose;
    private readonly bool _supportRange;
    private readonly long _fileSize;
    private readonly object _syncLock = new();
    private readonly byte[] _ringBuffer;

    private int _readPos;
    private int _writePos;
    private int _bytesAvailable;
    private long _virtualPosition;
    private long _downloadPosition;
    private bool _disposed;
    private bool _downloadCompleted;
    private Exception? _downloadError;

    private Stream? _responseStream;
    private Task? _downloadTask;
    private CancellationTokenSource? _cts;

    private HttpStreamHandle(string url, HttpClient client, long fileSize, bool supportRange, bool needDispose)
    {
        _url = url;
        _ringBuffer = new byte[BufferSize];
        _httpClient = client;
        _fileSize = fileSize;
        _supportRange = supportRange;
        _needDispose = needDispose;

        StartDownload(_virtualPosition);
    }

    public override bool CanSeek => _supportRange;

    public static async Task<HttpStreamHandle> CreateAsync(string url, HttpClient? client = null)
    {
        var httpClient = client ?? new HttpClient();
        var res = await httpClient.CheckRangeSupportWithSizeAsync(url);
        return new HttpStreamHandle(url, httpClient, res.FileSize ?? 0, res.SupportsRange, client == null);
    }

    private void StartDownload(long startPosition)
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        lock (_syncLock)
        {
            _downloadCompleted = false;
            _downloadError = null;
            _downloadPosition = startPosition;
        }
        ClearLastError();

        _downloadTask = Task.Run(async () =>
        {
            var nextPosition = startPosition;
            var retryCount = 0;

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        nextPosition = await DownloadSegmentAsync(nextPosition, cts.Token);
                        MarkDownloadCompleted();
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception) when (_supportRange && retryCount < MaxRetryCount)
                    {
                        retryCount++;
                        nextPosition = GetDownloadPosition();
                        await Task.Delay(RetryDelayMs, cts.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                MarkDownloadFailed(ex);
            }
        }, cts.Token);
    }

    private async Task<long> DownloadSegmentAsync(long startPosition, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _url);
        if (_supportRange)
            request.Headers.Range = new RangeHeaderValue(startPosition, null);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using var responseStream = await response.Content.ReadAsStreamAsync();
        _responseStream = responseStream;

        var expectedEnd = response.Content.Headers.ContentLength.HasValue
            ? startPosition + response.Content.Headers.ContentLength.Value
            : _fileSize > 0 ? _fileSize : (long?)null;
        var nextPosition = startPosition;
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesToRequest;
                lock (_syncLock)
                {
                    while (BufferSize - _bytesAvailable == 0 && !cancellationToken.IsCancellationRequested)
                    {
                        Monitor.Wait(_syncLock, WaitTimeoutMs);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    bytesToRequest = Math.Min(buffer.Length, BufferSize - _bytesAvailable);
                }

                var bytesRead = await responseStream.ReadAsync(
                    buffer, 0, bytesToRequest, cancellationToken);

                if (bytesRead == 0)
                {
                    if (expectedEnd.HasValue && nextPosition < expectedEnd.Value)
                    {
                        throw new EndOfStreamException("HTTP stream ended before the expected content length.");
                    }

                    return nextPosition;
                }

                lock (_syncLock)
                {
                    WriteToBuffer(buffer, 0, bytesRead);
                    _bytesAvailable += bytesRead;
                    _downloadPosition = nextPosition + bytesRead;
                    Monitor.PulseAll(_syncLock);
                }

                nextPosition += bytesRead;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return nextPosition;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void MarkDownloadCompleted()
    {
        lock (_syncLock)
        {
            _downloadCompleted = true;
            Monitor.PulseAll(_syncLock);
        }
    }

    private void MarkDownloadFailed(Exception error)
    {
        lock (_syncLock)
        {
            _downloadError = error;
            _downloadCompleted = true;
            SetLastError(MaResult.MaIoError, error);
            Monitor.PulseAll(_syncLock);
        }
    }

    private long GetDownloadPosition()
    {
        lock (_syncLock)
        {
            return _downloadPosition;
        }
    }

    private void WriteToBuffer(byte[] data, int offset, int count)
    {
        var firstChunk = Math.Min(count, BufferSize - _writePos);
        Buffer.BlockCopy(data, offset, _ringBuffer, _writePos, firstChunk);

        _writePos = (_writePos + firstChunk) % BufferSize;

        var remaining = count - firstChunk;
        if (remaining > 0)
        {
            Buffer.BlockCopy(data, offset + firstChunk, _ringBuffer, _writePos, remaining);
            _writePos = (_writePos + remaining) % BufferSize;
        }
    }

    public override MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead)
    {
        bytesRead = UIntPtr.Zero;
        var remaining = (int)bytesToRead;

        try
        {
            while (remaining > 0 && _cts?.IsCancellationRequested != true)
            {
                lock (_syncLock)
                {
                    if (_bytesAvailable > 0)
                    {
                        var read = ReadFromBuffer(pBuffer, (int)bytesToRead - remaining,
                            Math.Min(remaining, _bytesAvailable));
                        _virtualPosition += read;
                        bytesRead = (UIntPtr)((int)bytesRead + read);
                        remaining -= read;

                        // 如果释放了足够空间，通知下载线程
                        if (BufferSize - _bytesAvailable > BufferSize / 2)
                        {
                            Monitor.PulseAll(_syncLock);
                        }
                        continue;
                    }

                    if (_downloadCompleted)
                    {
                        if (_downloadError != null)
                        {
                            return bytesRead == 0
                                ? Fail(MaResult.MaIoError, _downloadError)
                                : MaResult.MaSuccess;
                        }

                        return remaining == (int)bytesToRead ?
                            MaResult.MaAtEnd :
                            MaResult.MaSuccess;
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

    private unsafe int ReadFromBuffer(IntPtr dest, int offset, int count)
    {
        var read = 0;
        var destPtr = (byte*)dest.ToPointer() + offset;

        while (count > 0 && _bytesAvailable > 0)
        {
            var bytesToCopy = Math.Min(count,
                _readPos < _writePos ?
                    _writePos - _readPos :
                    BufferSize - _readPos);

            fixed (byte* src = &_ringBuffer[_readPos])
            {
                Buffer.MemoryCopy(src, destPtr, bytesToCopy, bytesToCopy);
            }

            _readPos = (_readPos + bytesToCopy) % BufferSize;
            _bytesAvailable -= bytesToCopy;
            destPtr += bytesToCopy;
            count -= bytesToCopy;
            read += bytesToCopy;
        }

        return read;
    }

    public override MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin)
    {
        if (!_supportRange)
            return MaResult.MaInvalidOperation;

        if (origin == SeekOrigin.End && _fileSize <= 0)
        {
            return MaResult.MaNotImplemented;
        }

        try
        {
            var oldCts = _cts;
            var oldTask = _downloadTask;
            oldCts?.Cancel();
            oldTask?.Wait(WaitTimeoutMs);
            if (oldTask?.IsCompleted == true)
            {
                oldCts?.Dispose();
            }
        }
        catch
        {
            // 忽略取消异常
        }

        lock (_syncLock)
        {
            var newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _virtualPosition + offset,
                SeekOrigin.End => _fileSize + offset,
                _ => throw new NotSupportedException()
            };

            if (newPosition < 0)
            {
                return MaResult.MaInvalidArgs;
            }

            // 重置缓冲区状态
            _readPos = 0;
            _writePos = 0;
            _bytesAvailable = 0;
            _virtualPosition = newPosition;
            _downloadPosition = newPosition;
            _downloadCompleted = false;
            _downloadError = null;

            // 重新启动下载任务
            StartDownload(newPosition);
        }

        return MaResult.MaSuccess;
    }

    public override MaResult OnTell(IntPtr pDecoder, out long pCursor)
    {
        lock (_syncLock)
        {
            pCursor = _virtualPosition;
        }

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

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        lock (_syncLock)
        {
            Monitor.PulseAll(_syncLock);
        }

        try
        {
            _downloadTask?.Wait(WaitTimeoutMs);
        }
        catch
        {
            // 忽略所有异常
        }

        _responseStream?.Dispose();
        _cts?.Dispose();

        if (_needDispose)
        {
            _httpClient.Dispose();
        }
    }
}
