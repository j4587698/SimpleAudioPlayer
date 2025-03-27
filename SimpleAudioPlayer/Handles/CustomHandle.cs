using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;

namespace SimpleAudioPlayer.Handles;

public class CustomHandle(
    Func<byte[], int, int, int> onRead,
    Func<long, SeekOrigin, bool> onSeek,
    Func<long> onTell): AudioCallbackHandlerBase
{
    public override void Dispose()
    {
    }

    public override MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead)
    {
        var buffer = new byte[bytesToRead];
        var read = onRead(buffer, 0, (int)bytesToRead);
        Marshal.Copy(buffer, 0, pBuffer, read);
        bytesRead = (UIntPtr)read;
        
        return read > 0 ? MaResult.MaSuccess : MaResult.MaAtEnd;
    }

    public override MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin)
    {
        return onSeek(offset, origin) ? MaResult.MaSuccess : MaResult.MaNotImplemented;
    }

    public override MaResult OnTell(IntPtr pDecoder, out long pCursor)
    {
        var cursor = onTell();
        pCursor = cursor;
        return MaResult.MaSuccess;
    }
}