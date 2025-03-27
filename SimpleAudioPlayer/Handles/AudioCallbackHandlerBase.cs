using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;

namespace SimpleAudioPlayer.Handles;

public abstract class AudioCallbackHandlerBase: IAudioCallbackHandler
{
    public abstract void Dispose();
    public abstract MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead);
    public abstract MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin);
    public abstract MaResult OnTell(IntPtr pDecoder, out long pCursor);

    public virtual bool Play(AudioContextHandle ctx)
    {
        return NativeMethods.AudioPlay(ctx) == MaResult.MaSuccess;
    }

    public virtual bool Pause(AudioContextHandle ctx)
    {
        return NativeMethods.AudioStop(ctx) == MaResult.MaSuccess;
    }

    public virtual bool Stop(AudioContextHandle ctx)
    {
        return NativeMethods.AudioStop(ctx) == MaResult.MaSuccess;
    }

    public virtual bool Seek(AudioContextHandle ctx, double time)
    {
        return NativeMethods.SeekToTime(ctx, time) == MaResult.MaSuccess;
    }

    public virtual double GetTime(AudioContextHandle ctx)
    {
        var res = NativeMethods.GetTime(ctx, out var time) == MaResult.MaSuccess;
        if (res)
        {
            return time;
        }

        return 0;
    }

    public virtual double GetDuration(AudioContextHandle ctx)
    {
        var res = NativeMethods.GetDuration(ctx, out var time) == MaResult.MaSuccess;
        if (res)
        {
            return time;
        }

        return 0;
    }
}