using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Handles;
using SimpleAudioPlayer.Native;

namespace SimpleAudioPlayer;

public class AudioPlayer: IDisposable
{
    private AudioCallbacks? _callbacks;
    private DeviceCallbacks _deviceCallbacks;
    private readonly AudioContextHandle _ctx;

    public Action<MaDeviceNotificationType>? DeviceNotificationChanged;
    
    public AudioPlayer(SampleFormat sampleFormat = SampleFormat.F32, uint channels = 2, uint sampleRate = 44100)
    {
        _ctx = NativeMethods.AudioContextCreate();
        _deviceCallbacks = new DeviceCallbacks(_ctx, sampleFormat, channels, sampleRate);
        _deviceCallbacks.DeviceStateChanged = type => DeviceNotificationChanged?.Invoke(type);
    }

    public float Volume {
        get => NativeMethods.GetVolume(_ctx);
        set
        {
            if (value is >= 0 and <= 1)
            {
                var result = NativeMethods.SetVolume(_ctx, value);
            }
        } 
    }
    
    public double Time
    {
        get => GetTime();
        set => Seek(value);
    }
    
    public double Duration => GetDuration();

    public void Load(IAudioCallbackHandler handler)
    {
        _callbacks?.Dispose();
        _callbacks = new AudioCallbacks();
    
        _callbacks.Handler = handler;

        var result = NativeMethods.AudioInitDecoder(
            _ctx,
            _callbacks.ReadProxy,
            _callbacks.SeekProxy,
            _callbacks.TellProxy,
            IntPtr.Zero);
    
        Console.WriteLine($"[5] Init result: {result}");
    }

    public bool Play()
    {
        return _callbacks?.Handler != null && _callbacks.Handler.Play(_ctx);
    }

    public bool Pause()
    {
        return _callbacks?.Handler != null && _callbacks.Handler.Pause(_ctx);
    }

    public bool Stop()
    {
        return _callbacks?.Handler != null && _callbacks.Handler.Stop(_ctx);
    }
    
    public double GetDuration()
    {
        if (_callbacks?.Handler == null)
        {
            return 0;
        }

        return _callbacks.Handler.GetDuration(_ctx);
    }
    
    public double GetTime()
    {
        if (_callbacks?.Handler == null)
        {
            return 0;
        }

        return _callbacks.Handler.GetTime(_ctx);
    }

    public bool Seek(double time)
    {
        return _callbacks?.Handler != null && _callbacks.Handler.Seek(_ctx, time);
    }
    
    public PlayState GetPlayState()
    {
        return NativeMethods.GetPlayState(_ctx);
    }
    
    public void Dispose()
    {
        _callbacks?.Dispose();
        _deviceCallbacks.Dispose();
    }
}