using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Utils;

namespace SimpleAudioPlayer.Native;

public class DeviceCallbacks: IDisposable
{
    private readonly ContextAwareScheduler _scheduler;
    private readonly object _syncRoot = new();
    private readonly AudioContextHandle _ctx;

    private GCHandle _onStopHandle;
    private GCHandle _deviceStateChangedCallback;
    private volatile bool _disposed;
    private NativeMethods.StopCallback StopProxy { get; }
    private NativeMethods.DeviceStateChangedCallback DeviceStateChangedProxy { get; }

    public Action<MaDeviceNotificationType>? DeviceStateChanged { get; set; }

    public Action? PlayCompleted { get; set; }

    public Action<MaResult>? PlaybackStopped { get; set; }

    public DeviceCallbacks(AudioContextHandle ctx, SampleFormat sampleFormat = SampleFormat.F32, uint channels = 2, uint sampleRate = 0)
        : this(ctx, new AudioPlayerOptions
        {
            SampleFormat = sampleFormat,
            Channels = channels,
            SampleRate = sampleRate
        })
    {
    }

    internal DeviceCallbacks(AudioContextHandle ctx, AudioPlayerOptions options)
    {
        options.Validate();
        _ctx = ctx;
        _scheduler = new ContextAwareScheduler();
        StopProxy = ProxyStop;
        DeviceStateChangedProxy = ProxyDeviceStateChanged;

        _onStopHandle = GCHandle.Alloc(StopProxy);
        _deviceStateChangedCallback = GCHandle.Alloc(DeviceStateChangedProxy);
        var nativeConfig = NativeAudioDeviceConfig.FromOptions(options);
        var result = NativeMethods.AudioInitDeviceEx(_ctx, StopProxy, DeviceStateChangedProxy, in nativeConfig);
        if (result != MaResult.MaSuccess)
        {
            Dispose();
            throw new InvalidOperationException($"Failed to initialize audio device: {result}");
        }
    }


    private void ProxyStop()
    {
        _scheduler.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            MaResult result;
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                result = GetDecodeResult();
                try
                {
                    NativeMethods.AudioStop(_ctx);
                }
                catch (ObjectDisposedException)
                {
                    // 播放器已在回调排队后被释放，安全退出。
                    return;
                }
            }

            if (PlaybackStopped != null)
            {
                PlaybackStopped.Invoke(result);
            }
            else if (result == MaResult.MaSuccess || result == MaResult.MaAtEnd)
            {
                PlayCompleted?.Invoke();
            }
        });

    }

    private MaResult GetDecodeResult()
    {
        try
        {
            return NativeMethods.GetDecodeResult(_ctx);
        }
        catch (EntryPointNotFoundException)
        {
            return MaResult.MaSuccess;
        }
        catch (ObjectDisposedException)
        {
            // 上下文句柄已释放（播放器 Dispose 与回调竞态），按成功结束处理。
            return MaResult.MaSuccess;
        }
    }

    private void ProxyDeviceStateChanged(IntPtr pNotification)
    {
        if (_disposed)
        {
            return;
        }

        var notificationType = GetNotificationType(pNotification);
        DeviceStateChanged?.Invoke(notificationType);
    }

    private static MaDeviceNotificationType GetNotificationType(IntPtr pNotification)
    {
        return (MaDeviceNotificationType)Marshal.ReadInt32(
            pNotification,
            IntPtr.Size // 自动适应 x86/x64
        );
    }

    public void Dispose()
    {
        // 先标记，让仍在调度队列中的停止回调尽早退出，避免访问已释放的上下文句柄。
        _disposed = true;

        if (_onStopHandle.IsAllocated)
        {
            _onStopHandle.Free();
        }

        if (_deviceStateChangedCallback.IsAllocated)
        {
            _deviceStateChangedCallback.Free();
        }
    }
}
