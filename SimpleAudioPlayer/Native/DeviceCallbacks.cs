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
    private NativeMethods.StopCallback StopProxy { get; }
    private NativeMethods.DeviceStateChangedCallback DeviceStateChangedProxy { get; }
    
    public Action<MaDeviceNotificationType>? DeviceStateChanged { get; set; }

    public Action? PlayCompleted { get; set; }

    public DeviceCallbacks(AudioContextHandle ctx, SampleFormat sampleFormat = SampleFormat.F32, uint channels = 2, uint sampleRate = 44100)
    {
        _ctx = ctx;
        _scheduler = new ContextAwareScheduler();
        StopProxy = ProxyStop;
        DeviceStateChangedProxy = ProxyDeviceStateChanged;
        
        _onStopHandle = GCHandle.Alloc(StopProxy);
        _deviceStateChangedCallback = GCHandle.Alloc(DeviceStateChangedProxy);
        NativeMethods.AudioInitDevice(_ctx, StopProxy, DeviceStateChangedProxy, sampleFormat, channels, sampleRate);
    }
    
    
    private void ProxyStop()
    {
        _scheduler.Post(() =>
        {
            lock (_syncRoot)
            {
                NativeMethods.AudioStop(_ctx);
            }
        });
        PlayCompleted?.Invoke();
    }

    private void ProxyDeviceStateChanged(IntPtr pNotification)
    {
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