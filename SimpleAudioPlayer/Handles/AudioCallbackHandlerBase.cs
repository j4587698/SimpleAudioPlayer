using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;

namespace SimpleAudioPlayer.Handles;

public abstract class AudioCallbackHandlerBase: IAudioCallbackHandler
{
    public MaResult LastResult { get; private set; } = MaResult.MaSuccess;
    public Exception? LastError { get; private set; }
    public virtual bool CanSeek => true;

    public abstract void Dispose();
    public abstract MaResult OnRead(IntPtr pDecoder, IntPtr pBuffer, nuint bytesToRead, out nuint bytesRead);
    public abstract MaResult OnSeek(IntPtr pDecoder, long offset, SeekOrigin origin);
    public abstract MaResult OnTell(IntPtr pDecoder, out long pCursor);

    public virtual MaResult OnGetLength(out long length)
    {
        length = 0;
        return MaResult.MaNotImplemented;
    }

    internal void SetLastError(MaResult result, Exception? error)
    {
        LastResult = result;
        LastError = error;
    }

    protected void ClearLastError()
    {
        LastResult = MaResult.MaSuccess;
        LastError = null;
    }

    protected MaResult Fail(MaResult result, Exception? error)
    {
        SetLastError(result, error);
        return result;
    }

    private bool RecordNativeResult(MaResult result)
    {
        LastResult = result;
        LastError = null;
        return result == MaResult.MaSuccess;
    }

    public virtual bool Play(AudioContextHandle ctx)
    {
        return RecordNativeResult(NativeMethods.AudioPlay(ctx));
    }

    public virtual bool Pause(AudioContextHandle ctx)
    {
        return RecordNativeResult(NativeMethods.AudioStop(ctx));
    }

    public virtual bool Stop(AudioContextHandle ctx)
    {
        if (!RecordNativeResult(NativeMethods.AudioStop(ctx)))
        {
            return false;
        }

        return RecordNativeResult(NativeMethods.SeekToTime(ctx, 0));
    }

    public virtual bool Seek(AudioContextHandle ctx, double time)
    {
        return RecordNativeResult(NativeMethods.SeekToTime(ctx, time));
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
