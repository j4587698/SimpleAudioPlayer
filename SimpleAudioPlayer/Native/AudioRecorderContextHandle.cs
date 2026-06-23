using System.Runtime.InteropServices;

namespace SimpleAudioPlayer.Native;

public class AudioRecorderContextHandle() : SafeHandle(IntPtr.Zero, true)
{
    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.AudioRecorderCleanup(handle);
        handle = IntPtr.Zero;
        return true;
    }
}
