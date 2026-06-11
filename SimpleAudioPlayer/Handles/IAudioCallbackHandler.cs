using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;

namespace SimpleAudioPlayer.Handles;

public interface IAudioCallbackHandler: IDisposable
{
    bool CanSeek { get; }

    MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead);
    MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin);
    MaResult OnTell(IntPtr pDecoder, out long pCursor);
    MaResult OnGetLength(out long length);

    bool Play(AudioContextHandle ctx);
    bool Pause(AudioContextHandle ctx);
    bool Stop(AudioContextHandle ctx);

    bool Seek(AudioContextHandle ctx, double time);
    double GetTime(AudioContextHandle ctx);
    double GetDuration(AudioContextHandle ctx);

}
