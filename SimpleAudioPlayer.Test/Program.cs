using System.Reflection;
using System.Runtime.InteropServices;
using SimpleAudioPlayer;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;

NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveNativeLibrary);

var runRecordingSmoke = args.Contains("--recording-smoke", StringComparer.OrdinalIgnoreCase)
    || Environment.GetEnvironmentVariable("SIMPLE_AUDIO_PLAYER_RECORDING_SMOKE") == "1";

var tests = new List<(string Name, Action Test)>
{
    ("recording format enum matches native values", RecordingFormatValuesMatchNative),
    ("recorder validates constructor options", RecorderValidatesConstructorOptions),
    ("recorder default state is stopped", RecorderDefaultStateIsStopped),
    ("recorder validates start arguments before native calls", RecorderValidatesStartArguments),
    ("native recorder entry points match exports", NativeRecorderEntryPointsMatchExports)
};

if (runRecordingSmoke)
{
    tests.Add(("recorder writes pcm, wav, aac, and m4a streams", RecorderWritesRecordingStreams));
}

var failed = 0;
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {name}");
        Console.WriteLine(ex);
    }
}

if (failed > 0)
{
    Environment.ExitCode = 1;
}

static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
{
    if (!string.Equals(libraryName, "libaudio_player", StringComparison.Ordinal))
    {
        return IntPtr.Zero;
    }

    var fileName = OperatingSystem.IsWindows() ? "libaudio_player.dll" : "libaudio_player";
    var appLocalPath = Path.Combine(AppContext.BaseDirectory, fileName);
    return File.Exists(appLocalPath)
        ? NativeLibrary.Load(appLocalPath)
        : IntPtr.Zero;
}

static void RecordingFormatValuesMatchNative()
{
    AssertEqual(1, (int)RecordingFileFormat.M4A);
    AssertEqual(2, (int)RecordingFileFormat.Aac);
    AssertEqual(3, (int)RecordingFileFormat.Wav);
    AssertEqual(4, (int)RecordingFileFormat.Pcm);
}

static void RecorderWritesRecordingStreams()
{
    var previousBackend = Environment.GetEnvironmentVariable("SIMPLE_AUDIO_PLAYER_RECORDING_BACKEND");
    try
    {
        Environment.SetEnvironmentVariable("SIMPLE_AUDIO_PLAYER_RECORDING_BACKEND", "simulated");
        var pcm = RecordStream(RecordingFileFormat.Pcm);
        var wav = RecordStream(RecordingFileFormat.Wav);
        var aac = RecordStream(RecordingFileFormat.Aac);
        var m4a = RecordStream(RecordingFileFormat.M4A);

        AssertRawPcm(pcm);
        AssertRiffWave(wav);
        AssertAdtsAac(aac);
        AssertM4A(m4a);
    }
    finally
    {
        Environment.SetEnvironmentVariable("SIMPLE_AUDIO_PLAYER_RECORDING_BACKEND", previousBackend);
    }
}

static byte[] RecordStream(RecordingFileFormat format)
{
    using var recorder = new AudioRecorder(SampleFormat.F32, channels: 2, sampleRate: 44100, bitRate: 128000);
    using var output = new MemoryStream();

    if (!recorder.Start(output, format))
    {
        throw new InvalidOperationException($"Failed to start {format} recording: {recorder.LastResult}.");
    }

    Thread.Sleep(TimeSpan.FromSeconds(2));

    if (!recorder.Stop())
    {
        throw new InvalidOperationException($"Failed to stop {format} recording: {recorder.LastResult}.");
    }

    AssertGreaterThan(0ul, recorder.CapturedFrames);
    var data = output.ToArray();
    AssertMinLength(data, format == RecordingFileFormat.Wav ? 44 : 8);

    Console.WriteLine($"INFO {format} bytes={data.Length}, capturedFrames={recorder.CapturedFrames}, droppedFrames={recorder.DroppedFrames}");
    return data;
}

static void AssertRawPcm(byte[] data)
{
    AssertMinLength(data, 8);
    AssertEqual(0, data.Length % 8);
}

static void AssertRiffWave(byte[] data)
{
    AssertMinLength(data, 12);
    AssertEqual("RIFF", System.Text.Encoding.ASCII.GetString(data, 0, 4));
    AssertEqual("WAVE", System.Text.Encoding.ASCII.GetString(data, 8, 4));
}

static void AssertAdtsAac(byte[] data)
{
    AssertMinLength(data, 2);

    var hasAdtsSyncWord = data[0] == 0xFF && (data[1] & 0xF0) == 0xF0;
    if (!hasAdtsSyncWord)
    {
        throw new InvalidOperationException("AAC file does not start with an ADTS sync word.");
    }
}

static void AssertM4A(byte[] data)
{
    AssertMinLength(data, 8);
    AssertEqual("ftyp", System.Text.Encoding.ASCII.GetString(data, 4, 4));
}

static void RecorderValidatesConstructorOptions()
{
    AssertThrows<ArgumentOutOfRangeException>(() => new AudioRecorder(SampleFormat.Unknown));
    AssertThrows<ArgumentOutOfRangeException>(() => new AudioRecorder(channels: 0));
    AssertThrows<ArgumentOutOfRangeException>(() => new AudioRecorder(sampleRate: 0));
}

static void RecorderDefaultStateIsStopped()
{
    using var recorder = new AudioRecorder();

    AssertFalse(recorder.IsRecording);
    AssertFalse(recorder.Stop());
    AssertEqual(MaResult.MaSuccess, recorder.LastResult);
    AssertEqual(0ul, recorder.CapturedFrames);
    AssertEqual(0ul, recorder.DroppedFrames);
}

static void RecorderValidatesStartArguments()
{
    using var recorder = new AudioRecorder();

    AssertThrows<ArgumentException>(() => recorder.Start(""));
    AssertThrows<ArgumentOutOfRangeException>(() => recorder.Start("recording.wav", (RecordingFileFormat)99));
    AssertThrows<ArgumentException>(() => recorder.Start(new MemoryStream(Array.Empty<byte>(), false)));
    AssertThrows<ArgumentException>(() => recorder.Start(new NonSeekableWritableStream(), RecordingFileFormat.Wav));
    AssertThrows<ArgumentException>(() => recorder.Start(new NonSeekableWritableStream(), RecordingFileFormat.M4A));
    AssertThrows<ArgumentOutOfRangeException>(() => recorder.Start(new MemoryStream(), (RecordingFileFormat)99));
    AssertThrows<ArgumentException>(() => recorder.Start("recording.pcm", RecordingFileFormat.Pcm));
}

static void NativeRecorderEntryPointsMatchExports()
{
    AssertLibraryImport(nameof(NativeMethods.AudioRecorderContextCreate), "audio_recorder_context_create");
    AssertLibraryImport(nameof(NativeMethods.AudioRecorderInitFile), "audio_recorder_init_file", StringMarshalling.Utf8);
    AssertLibraryImport(nameof(NativeMethods.AudioRecorderInitStream), "audio_recorder_init_stream");
    AssertLibraryImport(nameof(NativeMethods.AudioRecorderStart), "audio_recorder_start");
    AssertLibraryImport(nameof(NativeMethods.AudioRecorderStop), "audio_recorder_stop");
    AssertLibraryImport(nameof(NativeMethods.AudioRecorderCleanup), "audio_recorder_cleanup");
    AssertLibraryImport(nameof(NativeMethods.AudioRecorderGetCapturedFrames), "audio_recorder_get_captured_frames");
    AssertLibraryImport(nameof(NativeMethods.AudioRecorderGetDroppedFrames), "audio_recorder_get_dropped_frames");
    AssertLibraryImport(nameof(NativeMethods.AudioRecorderGetResult), "audio_recorder_get_result");
}

static void AssertLibraryImport(
    string methodName,
    string entryPoint,
    StringMarshalling stringMarshalling = StringMarshalling.Custom)
{
    var method = typeof(NativeMethods).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
    if (method == null)
    {
        throw new InvalidOperationException($"Missing native method {methodName}.");
    }

    var attribute = method.GetCustomAttribute<LibraryImportAttribute>();
    if (attribute == null)
    {
        throw new InvalidOperationException($"Missing LibraryImportAttribute on {methodName}.");
    }

    AssertEqual("libaudio_player", attribute.LibraryName);
    AssertEqual(entryPoint, attribute.EntryPoint);

    if (stringMarshalling != StringMarshalling.Custom)
    {
        AssertEqual(stringMarshalling, attribute.StringMarshalling);
    }
}

static void AssertFalse(bool value)
{
    if (value)
    {
        throw new InvalidOperationException("Expected false.");
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }
}

static void AssertGreaterThan<T>(T minExclusive, T actual)
    where T : IComparable<T>
{
    if (actual.CompareTo(minExclusive) <= 0)
    {
        throw new InvalidOperationException($"Expected greater than {minExclusive}, actual {actual}.");
    }
}

static void AssertMinLength(byte[] data, int minLength)
{
    if (data.Length < minLength)
    {
        throw new InvalidOperationException($"Expected data length at least {minLength}, actual {data.Length}.");
    }
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class NonSeekableWritableStream : MemoryStream
{
    public override bool CanSeek => false;
}
