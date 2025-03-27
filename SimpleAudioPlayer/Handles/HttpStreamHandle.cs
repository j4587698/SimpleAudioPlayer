using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;
using SimpleAudioPlayer.Utils;

namespace SimpleAudioPlayer.Handles;

public class HttpStreamHandle : AudioCallbackHandlerBase
{
    private const int BufferSize = 1 * 1024 * 1024; // 1MB环形缓冲区

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
    private bool _disposed;
    private bool _downloadCompleted;
    private Stream? _responseStream;
    private Task? _downloadTask;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _dataAvailableEvent = new(false);
    private readonly ManualResetEventSlim _bufferSpaceEvent = new();
    private int _waitTimeout = 100;
    private DateTime _lastWriteTime = DateTime.MinValue;

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
    
    public static async Task<HttpStreamHandle> CreateAsync(string url, HttpClient? client = null)
    {
        var httpClient = client ?? new HttpClient();
        var res = await httpClient.CheckRangeSupportWithSizeAsync(url);
        return new HttpStreamHandle(url, httpClient, res.FileSize ?? 0, res.SupportsRange, client == null);
    }

    private void StartDownload(long startPosition)
    {
        _cts = new CancellationTokenSource();
        _downloadTask = Task.Run(async () =>
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, _url);
                if (_supportRange)
                    request.Headers.Range = new RangeHeaderValue(startPosition, null);

                using var response = await _httpClient.SendAsync(
                    request, 
                    HttpCompletionOption.ResponseHeadersRead,
                    _cts.Token);

                _responseStream = await response.Content.ReadAsStreamAsync();

                var buffer = ArrayPool<byte>.Shared.Rent(8192);
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var availableSpace = BufferSize - _bytesAvailable;
                        if (availableSpace == 0)
                        {
                            // 缓冲区已满，等待消费
                            _bufferSpaceEvent.Wait(_waitTimeout);
                            continue;
                        }
                        var bytesRead = await _responseStream.ReadAsync(
                            buffer, 0, Math.Min(buffer.Length, availableSpace), _cts.Token);
                        
                        if (bytesRead == 0)
                        {
                            _downloadCompleted = true;
                            _dataAvailableEvent.Set();
                            break;
                        }

                        lock (_syncLock)
                        {
                            WriteToBuffer(buffer, 0, bytesRead);
                            _bytesAvailable += bytesRead;
                            _dataAvailableEvent.Set();
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
        }, _cts.Token);
    }

    private void WriteToBuffer(byte[] data, int offset, int count)
    {
        if (_cts is { IsCancellationRequested: false })
        {
            lock (_syncLock)
            {
                var writeSize = Math.Min(count, BufferSize - _writePos);

                Buffer.BlockCopy(data, offset, _ringBuffer, _writePos, writeSize);

                _writePos = (_writePos + writeSize) % BufferSize;
                offset += writeSize;
                count -= writeSize;
                if (count > 0)
                {
                    Buffer.BlockCopy(data, offset, _ringBuffer, _writePos, count);
                }
                
                _lastWriteTime = DateTime.Now;
                _waitTimeout = 100; // 重置等待时间
            }
        }
    }

    public override MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead)
    {
        bytesRead = UIntPtr.Zero;
        var remaining = (int)bytesToRead;
        while (_cts is { IsCancellationRequested: false })
        {

            if (_bytesAvailable == 0)
            {
                if (_downloadCompleted)
                {
                    return MaResult.MaAtEnd;
                }
                _dataAvailableEvent.Wait(_waitTimeout);
                continue;
            }
            lock (_syncLock)
            {
                if (_bytesAvailable > 0)
                {
                    var read = ReadFromBuffer(pBuffer, 0, Math.Min(remaining, _bytesAvailable));
                    _virtualPosition += read;
                    bytesRead = (UIntPtr)read;
                    return MaResult.MaSuccess;
                }
            }
        }
        
        return MaResult.MaSuccess;
    }

    private unsafe int ReadFromBuffer(IntPtr dest, int offset, int count)
    {
        var read = 0;
        var destPtr = (byte*)dest.ToPointer() + offset;

        lock (_syncLock)
        {
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
                
                // 通知有空间可用
                _bufferSpaceEvent.Set();
            }
        }
        return read;
    }

    public override MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin)
    {

        if (!_supportRange)
            return MaResult.MaInvalidOperation;

        _cts?.Cancel();
        _downloadTask?.Wait();
        
        lock (_syncLock)
        {
            var newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _virtualPosition + offset,
                SeekOrigin.End => _fileSize + offset,
                _ => throw new NotSupportedException()
            };

            // 重置缓冲区状态
            _readPos = 0;
            _writePos = 0;
            _bytesAvailable = 0;
            _virtualPosition = newPosition;
            _downloadCompleted = false;

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
        Thread.Sleep(1);
        return MaResult.MaSuccess;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _cts?.Cancel();
        _responseStream?.Dispose();
        
        if (_needDispose)
            _httpClient.Dispose();
    }
}
