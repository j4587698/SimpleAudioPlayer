using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;
using SimpleAudioPlayer.Utils;

namespace SimpleAudioPlayer.Handles;

public class FileStreamHandler(string filePath) : AudioCallbackHandlerBase
{
    private readonly FileStream _stream = File.OpenRead(filePath);

    public override MaResult OnRead(
        IntPtr pDecoder,
        IntPtr pBuffer,
        nuint bytesToRead,
        out nuint bytesRead)
    {
        byte[] buffer = new byte[(int)bytesToRead];
        int read = _stream.Read(buffer, 0, (int)bytesToRead);
            
        Marshal.Copy(buffer, 0, pBuffer, read);
        bytesRead = (nuint)read;
            
        return read > 0 ? MaResult.MaSuccess : MaResult.MaAtEnd;
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

    public override bool Stop(AudioContextHandle ctx)
    {
        var res = NativeMethods.AudioStop(ctx) == MaResult.MaSuccess;
        if (res)
        {
            _stream.Seek(0, SeekOrigin.Begin);
        }

        return res;
    }
    

    public override void Dispose()
    {
        _stream.Dispose();
    }
    
}