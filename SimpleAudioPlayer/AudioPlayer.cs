using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Handles;
using SimpleAudioPlayer.Native;

namespace SimpleAudioPlayer;

public class AudioPlayer
{
    private AudioCallbacks? _callbacks;
    private readonly AudioContextHandle _ctx;

    public PlaybackState State { get; set; }
    public Action<PlaybackState>? StateChanged { get; set; }

    public AudioPlayer(SampleFormat sampleFormat = SampleFormat.F32, uint channels = 2, uint sampleRate = 44100)
    {
        State = PlaybackState.Stopped;
        _ctx = NativeMethods.AudioContextCreate();
        NativeMethods.AudioInitDevice(_ctx, sampleFormat, channels, sampleRate);
    }

    public void ChangeHandler(IAudioCallbackHandler handler)
    {
        _callbacks?.Dispose();
        _callbacks = new AudioCallbacks();
        _callbacks.Handler = handler;
        NativeMethods.AudioInitDecoder(
            _ctx,
            _callbacks.ReadProxy,
            _callbacks.SeekProxy,
            _callbacks.TellProxy,
            IntPtr.Zero);
        State = PlaybackState.Stopped;
    }

    public bool Play()
    {
        if (_callbacks?.Handler == null)
        {
            return false;
        }
        
        if (State is PlaybackState.Stopped or PlaybackState.Paused)
        {
            return _callbacks.Handler.Play(_ctx);
        }

        return false;
    }
    
    public void Pause() => NativeMethods.AudioStop(_ctx);
    public void Stop() => NativeMethods.AudioStop(_ctx);
    
    public double GetDuration()
    {
        NativeMethods.GetDuration(_ctx, out double duration);
        return duration;
    }
    
    public double GetTime()
    {
        NativeMethods.GetTime(_ctx, out double time);
        return time;
    }

    public MaResult Seek(double time)
    {
        return NativeMethods.SeekToTime(_ctx, time);
    }

    public void Dispose()
    {
        _callbacks?.Dispose();
    }
}