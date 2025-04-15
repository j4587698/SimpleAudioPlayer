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
    private bool _getLength;
    
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
                        // 等待缓冲区空间
                        lock (_syncLock)
                        {
                            while (BufferSize - _bytesAvailable == 0 && !_cts.IsCancellationRequested)
                            {
                                Monitor.Wait(_syncLock, WaitTimeoutMs);
                            }
                            
                            if (_cts.IsCancellationRequested)
                            {
                                break;
                            }
                        }

                        var bytesRead = await _responseStream.ReadAsync(
                            buffer, 0, Math.Min(buffer.Length, BufferSize - _bytesAvailable), _cts.Token);
                        
                        if (bytesRead == 0)
                        {
                            lock (_syncLock)
                            {
                                _downloadCompleted = true;
                                Monitor.PulseAll(_syncLock);
                            }
                            break;
                        }

                        lock (_syncLock)
                        {
                            WriteToBuffer(buffer, 0, bytesRead);
                            _bytesAvailable += bytesRead;
                            Monitor.PulseAll(_syncLock);
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
            catch (Exception ex)
            {
                Console.WriteLine($"Download error: {ex.Message}");
                lock (_syncLock)
                {
                    _downloadCompleted = true;
                    Monitor.PulseAll(_syncLock);
                }
            }
        }, _cts.Token);
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
            while (remaining > 0 && !_cts.IsCancellationRequested)
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
            Console.WriteLine($"Read error: {ex.Message}");
            return MaResult.MaError;
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

        if (origin == SeekOrigin.End && offset == 0)
        {
            _getLength = true;
            return MaResult.MaSuccess;
        }

        try
        {
            _cts?.Cancel();
            _downloadTask?.Wait(WaitTimeoutMs);
        }
        catch
        {
            // 忽略取消异常
        }
        finally
        {
            _cts = new CancellationTokenSource();
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
        if (_getLength)
        {
            pCursor = _fileSize;
            _getLength = false;
            return MaResult.MaSuccess;
        }
        
        lock (_syncLock)
        {
            pCursor = _virtualPosition;
        }
        
        return MaResult.MaSuccess;
    }

    public override void Dispose()
    {
        if (_disposed) return;

        _cts?.Cancel();

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