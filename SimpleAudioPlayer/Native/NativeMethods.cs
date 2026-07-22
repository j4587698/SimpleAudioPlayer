using System.Runtime.InteropServices;
using SimpleAudioPlayer.Enums;

namespace SimpleAudioPlayer.Native;

public static unsafe partial class NativeMethods
{
    internal const uint AudioDeviceConfigVersion = 1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult ReadDelegate(IntPtr pDecoder, IntPtr pBufferOut, nuint bytesToRead, out nuint pBytesRead);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult SeekDelegate(IntPtr pDecoder, long byteOffset, SeekOrigin origin);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult TellDelegate(IntPtr pDecoder, out long pCursor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult LengthDelegate(IntPtr pDecoder, out long pLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StopCallback();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DeviceStateChangedCallback(IntPtr pNotification);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult AudioRecorderWriteDelegate(IntPtr userdata, IntPtr buffer, nuint bytesToWrite, out nuint bytesWritten);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult AudioRecorderSeekDelegate(IntPtr userdata, long offset, int origin, out long cursor);

    private const string LibraryName = "libaudio_player";

    [LibraryImport(LibraryName, EntryPoint = "audio_context_create")]
    public static partial AudioContextHandle AudioContextCreate();

    [LibraryImport(LibraryName, EntryPoint = "audio_init_device")]
    public static partial MaResult AudioInitDevice(
        AudioContextHandle ctx,
        StopCallback onStop,
        DeviceStateChangedCallback onDeviceStateChanged,
        SampleFormat format,
        uint channels,
        uint sampleRate);

    [LibraryImport(LibraryName, EntryPoint = "audio_init_device_ex")]
    internal static partial MaResult AudioInitDeviceEx(
        AudioContextHandle ctx,
        StopCallback onStop,
        DeviceStateChangedCallback onDeviceStateChanged,
        in NativeAudioDeviceConfig config);

    [LibraryImport(LibraryName, EntryPoint = "audio_init_decoder")]
    public static partial MaResult AudioInitDecoder(
        AudioContextHandle ctx,
        ReadDelegate onRead,
        SeekDelegate onSeek,
        TellDelegate onTell,
        LengthDelegate onGetLength,
        uint canSeek,
        IntPtr userdata);

    [LibraryImport(LibraryName, EntryPoint = "audio_play")]
    public static partial MaResult AudioPlay(AudioContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "audio_stop")]
    public static partial MaResult AudioStop(AudioContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "audio_cleanup")]
    public static partial void AudioCleanup(IntPtr ctx);

    [LibraryImport(LibraryName, EntryPoint = "seek_to_time")]
    public static partial MaResult SeekToTime(AudioContextHandle ctx, double seconds);

    [LibraryImport(LibraryName, EntryPoint = "get_decoder")]
    public static partial MaResult GetDecoder(AudioContextHandle ctx, out IntPtr pDecoder);

    [LibraryImport(LibraryName, EntryPoint = "get_length_in_pcm_frames")]
    public static partial MaResult GetLengthInPcmFrames(AudioContextHandle ctx, out ulong frames);

    [LibraryImport(LibraryName, EntryPoint = "get_cursor_in_pcm_frames")]
    public static partial MaResult GetCursorInPcmFrames(AudioContextHandle ctx, out ulong frames);

    [LibraryImport(LibraryName, EntryPoint = "get_time")]
    public static partial MaResult GetTime(AudioContextHandle ctx, out double seconds);

    [LibraryImport(LibraryName, EntryPoint = "get_duration")]
    public static partial MaResult GetDuration(AudioContextHandle ctx, out double seconds);

    [LibraryImport(LibraryName, EntryPoint = "get_volume")]
    public static partial float GetVolume(AudioContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "set_volume")]
    public static partial MaResult SetVolume(AudioContextHandle ctx, float volume);

    [LibraryImport(LibraryName, EntryPoint = "get_play_state")]
    public static partial PlayState GetPlayState(AudioContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "get_decode_result")]
    public static partial MaResult GetDecodeResult(AudioContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "audio_recorder_context_create")]
    public static partial AudioRecorderContextHandle AudioRecorderContextCreate();

    [LibraryImport(LibraryName, EntryPoint = "audio_recorder_init_file", StringMarshalling = StringMarshalling.Utf8)]
    public static partial MaResult AudioRecorderInitFile(
        AudioRecorderContextHandle ctx,
        string outputPath,
        RecordingFileFormat container,
        SampleFormat format,
        uint channels,
        uint sampleRate,
        uint bitRate);

    [LibraryImport(LibraryName, EntryPoint = "audio_recorder_init_stream")]
    public static partial MaResult AudioRecorderInitStream(
        AudioRecorderContextHandle ctx,
        RecordingFileFormat container,
        AudioRecorderWriteDelegate onWrite,
        AudioRecorderSeekDelegate? onSeek,
        IntPtr userdata,
        SampleFormat format,
        uint channels,
        uint sampleRate,
        uint bitRate);

    [LibraryImport(LibraryName, EntryPoint = "audio_recorder_start")]
    public static partial MaResult AudioRecorderStart(AudioRecorderContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "audio_recorder_stop")]
    public static partial MaResult AudioRecorderStop(AudioRecorderContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "audio_recorder_cleanup")]
    public static partial void AudioRecorderCleanup(IntPtr ctx);

    [LibraryImport(LibraryName, EntryPoint = "audio_recorder_get_captured_frames")]
    public static partial ulong AudioRecorderGetCapturedFrames(AudioRecorderContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "audio_recorder_get_dropped_frames")]
    public static partial ulong AudioRecorderGetDroppedFrames(AudioRecorderContextHandle ctx);

    [LibraryImport(LibraryName, EntryPoint = "audio_recorder_get_result")]
    public static partial MaResult AudioRecorderGetResult(AudioRecorderContextHandle ctx);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAudioDeviceConfig
{
    public uint StructSize;
    public uint Version;
    public SampleFormat Format;
    public uint Channels;
    public uint SampleRate;
    public uint PeriodSizeInMilliseconds;
    public uint Periods;
    public AudioLatencyMode LatencyMode;
    public AudioPlaybackUsage Usage;
    public AudioContentType ContentType;
    public AudioShareMode ShareMode;

    public static NativeAudioDeviceConfig FromOptions(AudioPlayerOptions options)
    {
        return new NativeAudioDeviceConfig
        {
            StructSize = (uint)Marshal.SizeOf<NativeAudioDeviceConfig>(),
            Version = NativeMethods.AudioDeviceConfigVersion,
            Format = options.SampleFormat,
            Channels = options.Channels,
            SampleRate = options.SampleRate,
            PeriodSizeInMilliseconds = options.PeriodSizeInMilliseconds,
            Periods = options.Periods,
            LatencyMode = options.LatencyMode,
            Usage = options.Usage,
            ContentType = options.ContentType,
            ShareMode = options.ShareMode
        };
    }
}
