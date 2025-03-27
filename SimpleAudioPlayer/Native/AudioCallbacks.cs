using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Handles;

namespace SimpleAudioPlayer.Native;

public unsafe class AudioCallbacks : IDisposable
{
    private GCHandle _readHandle;
    private GCHandle _seekHandle;
    private GCHandle _tellHandle;
    private IAudioCallbackHandler? _handler;

    public NativeMethods.ReadDelegate ReadProxy { get; }
    public NativeMethods.SeekDelegate SeekProxy { get; }
    public NativeMethods.TellDelegate TellProxy { get; }

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

        _readHandle = GCHandle.Alloc(ReadProxy);
        _seekHandle = GCHandle.Alloc(SeekProxy);
        _tellHandle = GCHandle.Alloc(TellProxy);
    }

    private MaResult ProxyRead(
        IntPtr pDecoder,
        IntPtr pBuffer,
        nuint bytesToRead,
        out nuint bytesRead)
    {
        if (_handler == null)
        {
            bytesRead = 0;
            return MaResult.MaNotImplemented;
        }

        try
        {
            var result = _handler.OnRead(pDecoder, pBuffer, bytesToRead, out var bytesReadInt);
            bytesRead = bytesReadInt;
            return result;
        }
        catch (Exception ex)
        {
            bytesRead = 0;
            return MaResult.MaError;
        }
    }

    private MaResult ProxySeek(
        IntPtr pDecoder,
        long offset,
        SeekOrigin origin)
    {
        if (_handler == null)
            return MaResult.MaNotImplemented;

        try
        {
            return _handler.OnSeek(pDecoder, offset, origin);
        }
        catch (Exception ex)
        {
            return MaResult.MaError;
        }
    }

    private MaResult ProxyTell(IntPtr pDecoder, out long pCursor)
    {
        if (_handler == null)
        {
            pCursor = 0;
            return MaResult.MaNotImplemented;
        }

        try
        {
            var result = _handler.OnTell(pDecoder, out var pCursorInt);
            pCursor = pCursorInt;
            return result;
        }
        catch (Exception ex)
        {
            pCursor = 0;
            return MaResult.MaError;
        }
    }

    public void Dispose()
    {
        _handler?.Dispose();
        if (_readHandle.IsAllocated) _readHandle.Free();
        if (_seekHandle.IsAllocated) _seekHandle.Free();
        if (_tellHandle.IsAllocated) _tellHandle.Free();
    }
}