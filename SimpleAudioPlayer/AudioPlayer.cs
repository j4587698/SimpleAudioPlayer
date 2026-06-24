using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Handles;
using SimpleAudioPlayer.Native;

namespace SimpleAudioPlayer;

/// <summary>
/// 音频播放器。
/// <para>
/// 使用完毕后请务必显式调用 <see cref="Dispose"/>（建议配合 <c>using</c>）。
/// 类型提供了终结器作为兜底，但那只是 best-effort：终结器会调用所用 handler 的 Dispose，
/// 若该 Dispose 阻塞，会占住进程唯一的终结器线程。终结器无法替代显式释放。
/// </para>
/// </summary>
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
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~AudioPlayer()
    {
        // Best-effort 兜底，应对用户忘记显式 Dispose 的情况；不是严格安全的终结器。
        // 它会沿用与显式释放相同的顺序，因而会在终结器线程上执行用户 handler 的 Dispose()。
        // 我们能用 try/catch 防止其异常使进程崩溃，但无法防止其阻塞——若用户 Dispose() 阻塞，
        // 会拖住整个终结器线程。请始终显式调用 Dispose()，不要依赖终结器。
        Dispose(false);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            // 显式释放路径。顺序至关重要，且不能颠倒：
            // 1) 先 DisposeHandler()：设置 handler 的 _disposed 标志并唤醒其阻塞中的 OnRead
            //    （例如正在等待网络下载的流），使 OnRead 立即返回。
            //    否则下一步 _ctx.Dispose() 会在 join 解码线程时，因解码线程仍卡在 OnRead 而死锁。
            // 2) 再 _ctx.Dispose()：停止并卸载 native 设备、join 解码线程；返回后 native 不再回调。
            // 3) 最后释放被固定的回调委托 GCHandle（此时 native 已停，安全）。
            _callbacks?.DisposeHandler();
            _ctx.Dispose();
            _callbacks?.Dispose();
            _deviceCallbacks?.Dispose();
        }
        else
        {
            // 终结器路径（best-effort，非严格安全）：顺序与显式路径相同——同样必须先唤醒并释放
            // handler，否则 _ctx.Dispose() 的解码线程 join 会死锁、永久占住终结器线程；而且若先释放
            // 委托 GCHandle，SafeHandle 自身终结时 native 仍可能回调已释放委托而崩溃。因此该顺序在
            // 当前架构下不可避免地会在终结器线程上执行用户 handler.Dispose()。
            // try/catch 仅能拦截其异常以避免进程崩溃，无法阻止其阻塞——若用户 Dispose() 阻塞，
            // 终结器线程会被拖住。这只是兜底，用户仍应始终显式调用 Dispose()。
            try
            {
                _callbacks?.DisposeHandler();
                _ctx.Dispose();
                _callbacks?.FreeDelegateHandles();
                _deviceCallbacks?.Dispose();
            }
            catch
            {
                // 终结器中绝不抛出。
            }
        }
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
