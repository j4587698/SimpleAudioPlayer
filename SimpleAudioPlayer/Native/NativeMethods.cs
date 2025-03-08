using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;

namespace SimpleAudioPlayer.Native;

public static unsafe partial class NativeMethods
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult ReadDelegate(IntPtr pDecoder, IntPtr pBufferOut, ulong bytesToRead, out nuint* pBytesRead);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult SeekDelegate(IntPtr pDecoder, long byteOffset, SeekOrigin origin);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult TellDelegate(IntPtr pDecoder, out nuint* pCursor);
    
    private const string LibraryName = "libaudio_player";

    [LibraryImport(LibraryName, EntryPoint = "audio_context_create")]
    public static partial AudioContextHandle AudioContextCreate();

    [LibraryImport(LibraryName, EntryPoint = "audio_init_device")]
    public static partial MaResult AudioInitDevice(
        AudioContextHandle ctx,
        SampleFormat format,
        uint channels,
        uint sampleRate);

    [LibraryImport(LibraryName, EntryPoint = "audio_init_decoder")]
    public static partial MaResult AudioInitDecoder(
        AudioContextHandle ctx,
        ReadDelegate onRead,
        SeekDelegate onSeek,
        TellDelegate onTell,
        IntPtr userdata);

    [LibraryImport(LibraryName, EntryPoint = "audio_play")]
    public static partial MaResult AudioPlay(AudioContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "audio_stop")]
    public static partial MaResult AudioStop(AudioContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "audio_cleanup")]
    public static partial void AudioCleanup(AudioContextHandle ctx);
    
    [LibraryImport(LibraryName, EntryPoint = "seek_to_time")]
    public static partial MaResult SeekToTime(AudioContextHandle ctx, double seconds);

    [LibraryImport(LibraryName, EntryPoint = "get_decoder")]
    public static partial MaResult GetDecoder(AudioContextHandle ctx, out IntPtr pDecoder);
    
    [LibraryImport(LibraryName, EntryPoint = "get_length_in_pcm_frames")]
    public static partial MaResult GetLengthInPcmFrames(AudioContextHandle ctx, out ulong frames);

    [LibraryImport(LibraryName, EntryPoint = "get_cursor_in_pcm_frames")]
    public static partial void GetCursorInPcmFrames(AudioContextHandle ctx, out ulong frames);
    
    [LibraryImport(LibraryName, EntryPoint = "get_time")]
    public static partial MaResult GetTime(AudioContextHandle ctx, out double seconds);

    [LibraryImport(LibraryName, EntryPoint = "get_duration")]
    public static partial MaResult GetDuration(AudioContextHandle ctx, out double seconds);
}