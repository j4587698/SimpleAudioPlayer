using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Utils;

namespace SimpleAudioPlayer.Handles;

public class HttpStreamHandle: AudioCallbackHandlerBase
{
    private string _url;
    private readonly HttpClient _httpClient;
    private readonly bool _needDispose;
    private readonly bool _supportRange;
    private readonly long _fileSize;
    private long _position;
    private readonly object _syncLock = new();
    private bool _isEnd;
    private Stream? _stream;
    
    public HttpStreamHandle(string url, HttpClient? client = null)
    {
        _url = url;
        if (client != null)
        {
            _httpClient = client;
        }
        else
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
            _needDispose = true;
        }

        var res = _httpClient.CheckRangeSupportWithSize(url);
        _fileSize = res.FileSize ?? 0;
        _supportRange = res.SupportsRange;
    }
    
    
    public override void Dispose()
    {
        if (_needDispose)
        {
            _httpClient?.Dispose();
        }
    }

    public override MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, ulong bytesToRead, out UIntPtr bytesRead)
    {
        lock (_syncLock)
        {
            if (_stream == null)
            {
                // 发起Range请求
                var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.Range = new RangeHeaderValue(_position, null);

                var response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
                _stream = response.Content.ReadAsStream();
            }

            var bytes = new byte[bytesToRead];
            var read = _stream.Read(bytes, 0, (int)bytesToRead);
            Marshal.Copy(bytes, 0, pBuffer, read);
            bytesRead = (UIntPtr)read;
            _position += read;

            return read > 0 ? MaResult.MaSuccess : MaResult.MaAtEnd;
        }
    }

    public override MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin)
    {
        if (!_supportRange)
        {
            return MaResult.MaInvalidOperation;
        }

        // if (origin == SeekOrigin.End && offset == -1)
        // {
        //     _isEnd = true;
        //     return MaResult.MaSuccess;
        // }

        if (origin == SeekOrigin.Begin && offset == _position)
        {
            return MaResult.MaSuccess;
        }

        if (origin == SeekOrigin.Current && offset == 0)
        {
            return MaResult.MaSuccess;
        }
        
        lock (_syncLock)
        {
            try
            {
                // 计算绝对位置
                long newPosition = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => _fileSize + offset,
                    _ => throw new NotSupportedException()
                };

                // 关闭当前连接
                _stream?.Dispose();

                // 发起Range请求
                var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.Range = new RangeHeaderValue(newPosition, null);

                var response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
                _stream = response.Content.ReadAsStream();

                // 重置缓冲状态
                _position = newPosition;

                return MaResult.MaSuccess;
            }
            catch (Exception ex)
            {
                return MaResult.MaError;
            }
        }
    }

    public override MaResult OnTell(IntPtr pDecoder, out UIntPtr pCursor)
    {
        if (_isEnd)
        {
            _isEnd = false;
            pCursor = (UIntPtr)(_fileSize - 1);
            return MaResult.MaSuccess;
        }
        
        lock (_syncLock)
        {
            pCursor = (UIntPtr)_position;
        }
        return MaResult.MaSuccess;
    }
}