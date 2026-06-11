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
    private PlaybackState _playbackState = PlaybackState.Stopped;

    public Action<MaDeviceNotificationType>? DeviceNotificationChanged;
    public Action? PlayCompleted { get; set; }
    public Action<PlaybackState>? PlaybackStateChanged { get; set; }
    public Action<PlaybackFailedEventArgs>? PlaybackFailed { get; set; }

    public PlaybackState PlaybackState => _playbackState;

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
        _deviceCallbacks.PlaybackStopped = OnNativePlaybackStopped;
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
            callbacks.LengthProxy,
            handler.CanSeek ? 1u : 0u,
            IntPtr.Zero);

        var oldCallbacks = _callbacks;
        if (result != MaResult.MaSuccess)
        {
            _callbacks = null;
            callbacks.Dispose();
            oldCallbacks?.Dispose();
            NotifyPlaybackFailed(result, null);
            throw new InvalidOperationException($"Failed to initialize audio decoder: {result}");
        }

        _callbacks = callbacks;
        oldCallbacks?.Dispose();
        SetPlaybackState(PlaybackState.Stopped);

    }

    public bool Play()
    {
        if (_callbacks?.Handler == null)
        {
            return false;
        }

        var success = _callbacks.Handler.Play(_ctx);
        if (success)
        {
            SetPlaybackState(PlaybackState.Playing);
        }
        else
        {
            NotifyPlaybackFailed(GetHandlerResult(), GetHandlerError());
        }

        return success;
    }

    public bool Pause()
    {
        if (_callbacks?.Handler == null)
        {
            return false;
        }

        var success = _callbacks.Handler.Pause(_ctx);
        if (success)
        {
            SetPlaybackState(PlaybackState.Paused);
        }
        else
        {
            NotifyPlaybackFailed(GetHandlerResult(), GetHandlerError());
        }

        return success;
    }

    public bool Stop()
    {
        if (_callbacks?.Handler == null)
        {
            return false;
        }

        var success = _callbacks.Handler.Stop(_ctx);
        if (success)
        {
            SetPlaybackState(PlaybackState.Stopped);
        }
        else
        {
            NotifyPlaybackFailed(GetHandlerResult(), GetHandlerError());
        }

        return success;
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
        if (_callbacks?.Handler == null)
        {
            return false;
        }

        var success = _callbacks.Handler.Seek(_ctx, time);
        if (!success)
        {
            NotifyPlaybackFailed(GetHandlerResult(), GetHandlerError());
        }

        return success;
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

    private void OnNativePlaybackStopped(MaResult result)
    {
        if (_disposed)
        {
            return;
        }

        var handlerResult = GetHandlerResult();
        var handlerError = GetHandlerError();
        if (IsFailure(result) || handlerError != null && IsFailure(handlerResult))
        {
            var failureResult = handlerError != null && IsFailure(handlerResult)
                ? handlerResult
                : result;
            NotifyPlaybackFailed(failureResult, handlerError);
            return;
        }

        SetPlaybackState(PlaybackState.Completed);
        PlayCompleted?.Invoke();
    }

    private void SetPlaybackState(PlaybackState state)
    {
        if (_playbackState == state)
        {
            return;
        }

        _playbackState = state;
        PlaybackStateChanged?.Invoke(state);
    }

    private void NotifyPlaybackFailed(MaResult result, Exception? exception)
    {
        SetPlaybackState(PlaybackState.Error);
        PlaybackFailed?.Invoke(new PlaybackFailedEventArgs(result, exception));
    }

    private MaResult GetHandlerResult()
    {
        return _callbacks?.Handler is AudioCallbackHandlerBase handler
            ? handler.LastResult
            : MaResult.MaError;
    }

    private Exception? GetHandlerError()
    {
        return (_callbacks?.Handler as AudioCallbackHandlerBase)?.LastError;
    }

    private static bool IsFailure(MaResult result)
    {
        return result != MaResult.MaSuccess && result != MaResult.MaAtEnd;
    }
}
