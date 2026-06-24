using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;

namespace SimpleAudioPlayer.Handles;

public abstract class AudioCallbackHandlerBase: IAudioCallbackHandler
{
    // 这些状态由 native 回调线程写入、由播放完成调度线程读取，使用 volatile 后备字段保证可见性。
    private volatile int _lastResult = (int)MaResult.MaSuccess;
    private volatile Exception? _lastError;

    public MaResult LastResult => (MaResult)_lastResult;
    public Exception? LastError => _lastError;
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
        // 先写 error 再写 result：读取方先读 result 再读 error 时即可见到一致的错误信息。
        _lastError = error;
        _lastResult = (int)result;
    }

    protected void ClearLastError()
    {
        _lastError = null;
        _lastResult = (int)MaResult.MaSuccess;
    }

    protected MaResult Fail(MaResult result, Exception? error)
    {
        SetLastError(result, error);
        return result;
    }

    private bool RecordNativeResult(MaResult result)
    {
        _lastError = null;
        _lastResult = (int)result;
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
