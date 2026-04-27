using System.Buffers;
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
        if (bytesToRead > int.MaxValue)
        {
            bytesRead = 0;
            return MaResult.MaInvalidArgs;
        }

        var count = (int)bytesToRead;
        var bytes = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            var read = stream.Read(bytes, 0, count);
            Marshal.Copy(bytes, 0, pBuffer, read);
            bytesRead = (UIntPtr)read;
            return read > 0 ? MaResult.MaSuccess : MaResult.MaAtEnd;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
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