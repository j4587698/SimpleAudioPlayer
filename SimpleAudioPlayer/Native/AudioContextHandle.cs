using System.Runtime.InteropServices;

namespace SimpleAudioPlayer.Native;

public class AudioContextHandle() : SafeHandle(IntPtr.Zero, true)
{
    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.AudioCleanup(this);
        return true;
    }
}