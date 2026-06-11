using System.Buffers;
using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;

namespace SimpleAudioPlayer.Handles;

public class FileStreamHandler(string filePath) : AudioCallbackHandlerBase
{
    private readonly FileStream _stream = File.OpenRead(filePath);
    public override bool CanSeek => true;

    public override MaResult OnRead(
        IntPtr pDecoder,
        IntPtr pBuffer,
        nuint bytesToRead,
        out nuint bytesRead)
    {
        if (bytesToRead > int.MaxValue)
        {
            bytesRead = 0;
            return MaResult.MaInvalidArgs;
        }

        var count = (int)bytesToRead;
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            int read = _stream.Read(buffer, 0, count);
            Marshal.Copy(buffer, 0, pBuffer, read);
            bytesRead = (nuint)read;
            return read > 0 ? MaResult.MaSuccess : MaResult.MaAtEnd;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override MaResult OnSeek(
        IntPtr pDecoder,
        long offset,
        SeekOrigin origin)
    {
        _stream.Seek(offset, origin);
        return MaResult.MaSuccess;
    }

    public override MaResult OnTell(IntPtr pDecoder, out long pCursor)
    {

        pCursor = _stream.Position;
        return MaResult.MaSuccess;
    }

    public override MaResult OnGetLength(out long length)
    {
        length = _stream.Length;
        return MaResult.MaSuccess;
    }

    public override bool Stop(AudioContextHandle ctx)
    {
        return base.Stop(ctx);
    }


    public override void Dispose()
    {
        _stream.Dispose();
    }

}
