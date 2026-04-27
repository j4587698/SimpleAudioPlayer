using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Handles;
using SimpleAudioPlayer.Native;

namespace SimpleAudioPlayer;

public class AudioPlayer: IDisposable
{
    private AudioCallbacks? _callbacks;
    private DeviceCallbacks _deviceCallbacks;
    private readonly AudioContextHandle _ctx;
    private bool _disposed;

    public Action<MaDeviceNotificationType>? DeviceNotificationChanged;
    public Action? PlayCompleted { get; set; }
    
    public AudioPlayer(SampleFormat sampleFormat = SampleFormat.F32, uint channels = 2, uint sampleRate = 44100)
    {
        _ctx = NativeMethods.AudioContextCreate();
        if (_ctx.IsInvalid)
        {
            throw new InvalidOperationException("Failed to create audio context.");
        }

        try
        {
            _deviceCallbacks = new DeviceCallbacks(_ctx, sampleFormat, channels, sampleRate);
        }
        catch
        {
            _ctx.Dispose();
            throw;
        }

        _deviceCallbacks.DeviceStateChanged = type => DeviceNotificationChanged?.Invoke(type);
        _deviceCallbacks.PlayCompleted = () => PlayCompleted?.Invoke();
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
        var callbacks = new AudioCallbacks
        {
            Handler = handler
        };

        var result = NativeMethods.AudioInitDecoder(
            _ctx,
            callbacks.ReadProxy,
            callbacks.SeekProxy,
            callbacks.TellProxy,
            IntPtr.Zero);

        var oldCallbacks = _callbacks;
        if (result != MaResult.MaSuccess)
        {
            _callbacks = null;
            callbacks.Dispose();
            oldCallbacks?.Dispose();
            throw new InvalidOperationException($"Failed to initialize audio decoder: {result}");
        }

        _callbacks = callbacks;
        oldCallbacks?.Dispose();

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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _callbacks?.DisposeHandler();
        _ctx.Dispose();
        _callbacks?.Dispose();
        _deviceCallbacks.Dispose();
        GC.SuppressFinalize(this);
    }
}