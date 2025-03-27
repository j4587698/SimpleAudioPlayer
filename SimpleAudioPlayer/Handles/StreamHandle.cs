using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;

namespace SimpleAudioPlayer.Handles;

public class StreamHandle(Stream stream): AudioCallbackHandlerBase
{
    public override void Dispose()
    {
        stream.Dispose();
    }

    public override MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead)
    {
        var bytes = new byte[bytesToRead];
        var read = stream.Read(bytes, 0, (int)bytesToRead);
        Marshal.Copy(bytes, 0, pBuffer, read);
        bytesRead = (UIntPtr)read;
        
        return read > 0 ? MaResult.MaSuccess : MaResult.MaAtEnd;
    }

    public override MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin)
    {
        if (!stream.CanSeek)
        {
            return MaResult.MaNotImplemented;
        }
        
        stream.Seek(offset, origin);
        return MaResult.MaSuccess;
    }

    public override MaResult OnTell(IntPtr pDecoder, out long pCursor)
    {
        pCursor = stream.Position;
        return MaResult.MaSuccess;
    }
}