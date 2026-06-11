using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Handles;

namespace SimpleAudioPlayer.Native;

public unsafe class AudioCallbacks : IDisposable
{
    private GCHandle _readHandle;
    private GCHandle _seekHandle;
    private GCHandle _tellHandle;
    private GCHandle _lengthHandle;
    private IAudioCallbackHandler? _handler;

    public NativeMethods.ReadDelegate ReadProxy { get; }
    public NativeMethods.SeekDelegate SeekProxy { get; }
    public NativeMethods.TellDelegate TellProxy { get; }
    public NativeMethods.LengthDelegate LengthProxy { get; }

    public IAudioCallbackHandler? Handler
    {
        get => _handler;
        set => _handler = value ?? throw new ArgumentNullException(nameof(value));
    }

    public AudioCallbacks()
    {
        // 初始化固定委托
        ReadProxy = ProxyRead;
        SeekProxy = ProxySeek;
        TellProxy = ProxyTell;
        LengthProxy = ProxyGetLength;

        _readHandle = GCHandle.Alloc(ReadProxy);
        _seekHandle = GCHandle.Alloc(SeekProxy);
        _tellHandle = GCHandle.Alloc(TellProxy);
        _lengthHandle = GCHandle.Alloc(LengthProxy);
    }

    private MaResult ProxyRead(
        IntPtr pDecoder,
        IntPtr pBuffer,
        nuint bytesToRead,
        out nuint bytesRead)
    {
        var handler = _handler;
        if (handler == null)
        {
            bytesRead = 0;
            return MaResult.MaNotImplemented;
        }

        try
        {
            var result = handler.OnRead(pDecoder, pBuffer, bytesToRead, out var bytesReadInt);
            bytesRead = bytesReadInt;
            return result;
        }
        catch (Exception ex)
        {
            if (handler is AudioCallbackHandlerBase callbackHandler)
            {
                callbackHandler.SetLastError(MaResult.MaError, ex);
            }

            bytesRead = 0;
            return MaResult.MaError;
        }
    }

    private MaResult ProxySeek(
        IntPtr pDecoder,
        long offset,
        SeekOrigin origin)
    {
        var handler = _handler;
        if (handler == null)
            return MaResult.MaNotImplemented;

        try
        {
            return handler.OnSeek(pDecoder, offset, origin);
        }
        catch (Exception ex)
        {
            if (handler is AudioCallbackHandlerBase callbackHandler)
            {
                callbackHandler.SetLastError(MaResult.MaError, ex);
            }

            return MaResult.MaError;
        }
    }

    private MaResult ProxyTell(IntPtr pDecoder, out long pCursor)
    {
        var handler = _handler;
        if (handler == null)
        {
            pCursor = 0;
            return MaResult.MaNotImplemented;
        }

        try
        {
            var result = handler.OnTell(pDecoder, out var pCursorInt);
            pCursor = pCursorInt;
            return result;
        }
        catch (Exception ex)
        {
            if (handler is AudioCallbackHandlerBase callbackHandler)
            {
                callbackHandler.SetLastError(MaResult.MaError, ex);
            }

            pCursor = 0;
            return MaResult.MaError;
        }
    }

    private MaResult ProxyGetLength(IntPtr pDecoder, out long pLength)
    {
        var handler = _handler;
        if (handler == null)
        {
            pLength = 0;
            return MaResult.MaNotImplemented;
        }

        try
        {
            return handler.OnGetLength(out pLength);
        }
        catch (Exception ex)
        {
            if (handler is AudioCallbackHandlerBase callbackHandler)
            {
                callbackHandler.SetLastError(MaResult.MaError, ex);
            }

            pLength = 0;
            return MaResult.MaError;
        }
    }

    public void Dispose()
    {
        DisposeHandler();
        if (_readHandle.IsAllocated) _readHandle.Free();
        if (_seekHandle.IsAllocated) _seekHandle.Free();
        if (_tellHandle.IsAllocated) _tellHandle.Free();
        if (_lengthHandle.IsAllocated) _lengthHandle.Free();
    }

    internal void DisposeHandler()
    {
        var handler = _handler;
        _handler = null;
        handler?.Dispose();
    }
}
