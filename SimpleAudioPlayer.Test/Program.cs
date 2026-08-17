using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using SimpleAudioPlayer;
using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Handles;
using SimpleAudioPlayer.Native;

NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveNativeLibrary);

var runRecordingSmoke = args.Contains("--recording-smoke", StringComparer.OrdinalIgnoreCase)
    || Environment.GetEnvironmentVariable("SIMPLE_AUDIO_PLAYER_RECORDING_SMOKE") == "1";
var runPlaybackSmoke = args.Contains("--playback-smoke", StringComparer.OrdinalIgnoreCase)
    || Environment.GetEnvironmentVariable("SIMPLE_AUDIO_PLAYER_PLAYBACK_SMOKE") == "1";

var tests = new List<(string Name, Action Test)>
{
    ("player option enums match native values", PlayerOptionEnumValuesMatchNative),
    ("player options use media playback defaults", PlayerOptionsUseMediaPlaybackDefaults),
    ("player options map to native device config", PlayerOptionsMapToNativeDeviceConfig),
    ("player options reject invalid enum values", PlayerOptionsRejectInvalidEnumValues),
    ("recording format enum matches native values", RecordingFormatValuesMatchNative),
    ("recorder validates constructor options", RecorderValidatesConstructorOptions),
    ("recorder default state is stopped", RecorderDefaultStateIsStopped),
    ("recorder validates start arguments before native calls", RecorderValidatesStartArguments),
    ("native recorder entry points match exports", NativeRecorderEntryPointsMatchExports),
    ("disk cached stream uses disposable part cache", DiskCachedStreamUsesDisposablePartCache),
    ("disk cached stream handles seekable and explicit cached seek", DiskCachedStreamHandlesSeekableAndExplicitCachedSeek),
    ("disk cached stream preserves cache after seekable source reads", DiskCachedStreamPreservesCacheAfterSeekableSourceReads),
    ("disk cached stream commits completed cache", DiskCachedStreamCommitsCompletedCache),
    ("disk cached stream reports commit failure", DiskCachedStreamReportsCommitFailure),
    ("disk cached stream deletes incomplete persistent cache", DiskCachedStreamDeletesIncompletePersistentCache),
    ("disk cached stream avoids busy loop on seekable short stream", DiskCachedStreamSeekableShortStreamDoesNotBusyLoop),
    ("progressive cache index rejects modified files", ProgressiveCacheIndexRejectsModifiedFiles),
    ("progressive cache index allows missing optional validators", ProgressiveCacheIndexAllowsMissingOptionalValidators),
    ("progressive http resumes partial cache and persists seek ranges", ProgressiveHttpResumesPartialCacheAndSeekRanges),
    ("progressive http truncates stale partial tails", ProgressiveHttpTruncatesStalePartialTails),
    ("progressive http reports final commit failure", ProgressiveHttpReportsFinalCommitFailure),
    ("memory read cache window basic sequential read write", MemoryReadCacheWindowBasicSequentialReadWrite),
    ("memory read cache window extends and sliding", MemoryReadCacheWindowExtendsAndSliding),
    ("memory read cache window fill from disk and reset", MemoryReadCacheWindowFillFromDiskAndReset),
    ("disk cached stream reads accurately with memory cache", DiskCachedStreamReadsAccuratelyWithMemoryCache),
    ("progressive http stream reads accurately with memory cache", ProgressiveHttpStreamReadsAccuratelyWithMemoryCache)
};

if (runRecordingSmoke)
{
    tests.Add(("recorder writes pcm, wav, aac, and m4a streams", RecorderWritesRecordingStreams));
}

if (runPlaybackSmoke)
{
    tests.Add(("player initializes extended device configuration", PlayerInitializesExtendedDeviceConfiguration));
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

static void PlayerOptionEnumValuesMatchNative()
{
    AssertEqual(0, (int)AudioLatencyMode.LowLatency);
    AssertEqual(1, (int)AudioLatencyMode.Playback);
    AssertEqual(0, (int)AudioPlaybackUsage.Default);
    AssertEqual(1, (int)AudioPlaybackUsage.Media);
    AssertEqual(5, (int)AudioPlaybackUsage.Alarm);
    AssertEqual(0, (int)AudioContentType.Default);
    AssertEqual(1, (int)AudioContentType.Music);
    AssertEqual(4, (int)AudioContentType.Sonification);
    AssertEqual(0, (int)AudioShareMode.Shared);
    AssertEqual(1, (int)AudioShareMode.Exclusive);
}

static void PlayerOptionsUseMediaPlaybackDefaults()
{
    var options = new AudioPlayerOptions();

    AssertEqual(SampleFormat.F32, options.SampleFormat);
    AssertEqual(2u, options.Channels);
    AssertEqual(0u, options.SampleRate);
    AssertEqual(AudioLatencyMode.Playback, options.LatencyMode);
    AssertEqual(AudioPlaybackUsage.Media, options.Usage);
    AssertEqual(AudioContentType.Music, options.ContentType);
    AssertEqual(AudioShareMode.Shared, options.ShareMode);
    AssertEqual(0u, options.PeriodSizeInMilliseconds);
    AssertEqual(0u, options.Periods);
}

static void PlayerOptionsMapToNativeDeviceConfig()
{
    var options = new AudioPlayerOptions
    {
        SampleFormat = SampleFormat.S16,
        Channels = 0,
        SampleRate = 0,
        LatencyMode = AudioLatencyMode.LowLatency,
        Usage = AudioPlaybackUsage.Game,
        ContentType = AudioContentType.Sonification,
        PeriodSizeInMilliseconds = 20,
        Periods = 3,
        ShareMode = AudioShareMode.Exclusive
    };

    options.Validate();
    var config = NativeAudioDeviceConfig.FromOptions(options);

    AssertEqual((uint)Marshal.SizeOf<NativeAudioDeviceConfig>(), config.StructSize);
    AssertEqual(NativeMethods.AudioDeviceConfigVersion, config.Version);
    AssertEqual(options.SampleFormat, config.Format);
    AssertEqual(options.Channels, config.Channels);
    AssertEqual(options.SampleRate, config.SampleRate);
    AssertEqual(options.LatencyMode, config.LatencyMode);
    AssertEqual(options.Usage, config.Usage);
    AssertEqual(options.ContentType, config.ContentType);
    AssertEqual(options.PeriodSizeInMilliseconds, config.PeriodSizeInMilliseconds);
    AssertEqual(options.Periods, config.Periods);
    AssertEqual(options.ShareMode, config.ShareMode);
}

static void PlayerOptionsRejectInvalidEnumValues()
{
    AssertThrows<ArgumentOutOfRangeException>(() => new AudioPlayerOptions
    {
        SampleFormat = SampleFormat.Unknown
    }.Validate());
    AssertThrows<ArgumentOutOfRangeException>(() => new AudioPlayerOptions
    {
        LatencyMode = (AudioLatencyMode)99
    }.Validate());
    AssertThrows<ArgumentOutOfRangeException>(() => new AudioPlayerOptions
    {
        Usage = (AudioPlaybackUsage)99
    }.Validate());
    AssertThrows<ArgumentOutOfRangeException>(() => new AudioPlayerOptions
    {
        ContentType = (AudioContentType)99
    }.Validate());
    AssertThrows<ArgumentOutOfRangeException>(() => new AudioPlayerOptions
    {
        ShareMode = (AudioShareMode)99
    }.Validate());
}

static void PlayerInitializesExtendedDeviceConfiguration()
{
    using var player = new AudioPlayer(new AudioPlayerOptions
    {
        SampleRate = 0,
        LatencyMode = AudioLatencyMode.Playback,
        Usage = AudioPlaybackUsage.Media,
        ContentType = AudioContentType.Music,
        ShareMode = AudioShareMode.Shared
    });

    AssertEqual(PlayState.Stopped, player.GetPlayState());
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

static void DiskCachedStreamUsesDisposablePartCache()
{
    var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    var source = new SlowByteArrayStream(data, 0, data.Length, maxChunkSize: 16);
    var handle = new DiskCachedStreamHandle(source, bufferSize: 16);
    var cachePath = handle.CacheFilePath;

    try
    {
        AssertEqual(".part", Path.GetExtension(cachePath));
        AssertEqual(cachePath, handle.PartialFilePath);

        var buffer = Marshal.AllocHGlobal(32);
        try
        {
            var result = handle.OnRead(IntPtr.Zero, buffer, 32, out var bytesRead);
            AssertEqual(MaResult.MaSuccess, result);
            AssertGreaterThan((nuint)0, bytesRead);
            AssertTrue(File.Exists(cachePath));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
    finally
    {
        handle.Dispose();
    }

    WaitUntil(() => !File.Exists(cachePath), TimeSpan.FromSeconds(2));
}

static void DiskCachedStreamHandlesSeekableAndExplicitCachedSeek()
{
    var data = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
    using (var seekableSourceHandle = new DiskCachedStreamHandle(new MemoryStream(data)))
    {
        AssertTrue(seekableSourceHandle.CanSeek);
        AssertEqual(MaResult.MaSuccess, seekableSourceHandle.OnSeek(IntPtr.Zero, 96, SeekOrigin.Begin));

        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            AssertEqual(MaResult.MaSuccess, seekableSourceHandle.OnSeek(IntPtr.Zero, 0, SeekOrigin.End));
            AssertEqual(MaResult.MaAtEnd, seekableSourceHandle.OnRead(IntPtr.Zero, buffer, 1, out var bytesRead));
            AssertEqual((nuint)0, bytesRead);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    using (var nonSeekableDefaultHandle = new DiskCachedStreamHandle(
        new SlowByteArrayStream(data, 0, data.Length, maxChunkSize: 16),
        totalSize: data.Length))
    {
        AssertFalse(nonSeekableDefaultHandle.CanSeek);
        AssertEqual(MaResult.MaNotImplemented, nonSeekableDefaultHandle.OnSeek(IntPtr.Zero, 0, SeekOrigin.Begin));
    }

    using var cachedSeekHandle = new DiskCachedStreamHandle(
        new SlowByteArrayStream(data, 0, data.Length, maxChunkSize: 16),
        totalSize: data.Length,
        enableSeek: true);
    WaitUntil(() => cachedSeekHandle.IsCompleted, TimeSpan.FromSeconds(2));
    AssertTrue(cachedSeekHandle.CanSeek);
    AssertEqual(MaResult.MaSuccess, cachedSeekHandle.OnSeek(IntPtr.Zero, 64, SeekOrigin.Begin));
}

static void DiskCachedStreamCommitsCompletedCache()
{
    var tempDir = CreateTempDirectory();
    try
    {
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var finalPath = Path.Combine(tempDir, "stream-cache.bin");
        var source = new SlowByteArrayStream(data, 0, data.Length, maxChunkSize: 16);
        using var handle = new DiskCachedStreamHandle(
            source,
            bufferSize: 16,
            totalSize: data.Length,
            cacheFilePath: finalPath,
            commitCacheOnComplete: true);

        AssertEqual(finalPath, handle.CacheFilePath);
        AssertEqual(finalPath + ".part", handle.PartialFilePath);
        WaitUntil(() => handle.IsCompleted, TimeSpan.FromSeconds(2));

        AssertTrue(handle.IsCacheCommitted);
        AssertTrue(File.Exists(finalPath));
        AssertFalse(File.Exists(finalPath + ".part"));
        AssertSequenceEqual(data, File.ReadAllBytes(finalPath));
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

static void DiskCachedStreamPreservesCacheAfterSeekableSourceReads()
{
    var tempDir = CreateTempDirectory();
    try
    {
        var data = Enumerable.Range(0, 512).Select(i => (byte)(i % 251)).ToArray();
        var finalPath = Path.Combine(tempDir, "seekable-cache.bin");
        using var handle = new DiskCachedStreamHandle(
            new MemoryStream(data),
            bufferSize: 16,
            cacheFilePath: finalPath,
            commitCacheOnComplete: true);

        AssertTrue(handle.CanSeek);
        AssertEqual(MaResult.MaSuccess, handle.OnSeek(IntPtr.Zero, 400, SeekOrigin.Begin));

        var buffer = Marshal.AllocHGlobal(16);
        try
        {
            AssertEqual(MaResult.MaSuccess, handle.OnRead(IntPtr.Zero, buffer, 16, out var bytesRead));
            AssertEqual((nuint)16, bytesRead);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        WaitUntil(() => handle.IsCompleted, TimeSpan.FromSeconds(2));
        AssertTrue(handle.IsCacheCommitted);
        AssertSequenceEqual(data, File.ReadAllBytes(finalPath));
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

static void DiskCachedStreamDeletesIncompletePersistentCache()
{
    var tempDir = CreateTempDirectory();
    try
    {
        var data = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var finalPath = Path.Combine(tempDir, "stream-cache.bin");
        var source = new SlowByteArrayStream(data, 0, data.Length, maxChunkSize: 16);
        using var handle = new DiskCachedStreamHandle(
            source,
            bufferSize: 16,
            totalSize: 64,
            cacheFilePath: finalPath,
            commitCacheOnComplete: true);

        WaitUntil(() => handle.IsCompleted, TimeSpan.FromSeconds(2));

        AssertFalse(handle.IsCacheCommitted);
        AssertTrue(handle.LastError is EndOfStreamException);
        AssertFalse(File.Exists(finalPath));
        AssertFalse(File.Exists(finalPath + ".part"));
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

static void DiskCachedStreamReportsCommitFailure()
{
    var tempDir = CreateTempDirectory();
    try
    {
        var data = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        var finalPath = Path.Combine(tempDir, "stream-cache.bin");
        Directory.CreateDirectory(finalPath);

        using var handle = new DiskCachedStreamHandle(
            new MemoryStream(data),
            bufferSize: 16,
            totalSize: data.Length,
            cacheFilePath: finalPath,
            commitCacheOnComplete: true);

        WaitUntil(() => handle.IsCompleted, TimeSpan.FromSeconds(2));

        AssertFalse(handle.IsCacheCommitted);
        AssertEqual(MaResult.MaIoError, handle.LastResult);
        AssertTrue(handle.LastError is IOException);
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

static void DiskCachedStreamSeekableShortStreamDoesNotBusyLoop()
{
    // 声明的 totalSize 大于实际可 seek 源长度时，读取应在有限时间内收敛到错误/结束，
    // 而非忙等死循环（回归保护：OnRead 锁外读源后 sourceRead == 0 的处理）。
    var data = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    using var handle = new DiskCachedStreamHandle(
        new MemoryStream(data),
        bufferSize: 16,
        totalSize: 64);

    var buffer = Marshal.AllocHGlobal(16);
    try
    {
        var result = MaResult.MaSuccess;
        var bytesReadTotal = 0;
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            result = handle.OnRead(IntPtr.Zero, buffer, 16, out var bytesRead);
            if (result != MaResult.MaSuccess)
            {
                break;
            }

            bytesReadTotal += (int)bytesRead;
            // 成功读取的字节不应超过实际源长度。
            AssertTrue(bytesReadTotal <= data.Length);
        }

        // 应在超时前收敛到非 Success（EOF 或 IO 错误），证明没有忙等死循环。
        AssertTrue(result is MaResult.MaIoError or MaResult.MaAtEnd);
        AssertEqual(data.Length, bytesReadTotal);
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static void ProgressiveCacheIndexRejectsModifiedFiles()
{
    var tempDir = CreateTempDirectory();
    try
    {
        var indexPath = Path.Combine(tempDir, "audio.bin.part.idx");
        var index = ProgressiveHttpCacheIndex.Create(
            "https://example.test/audio.bin",
            1024,
            "\"v1\"",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            [new CachedByteRange(0, 128), new CachedByteRange(512, 64)]);

        index.Save(indexPath);
        AssertTrue(ProgressiveHttpCacheIndex.TryLoad(indexPath) != null);

        var bytes = File.ReadAllBytes(indexPath);
        bytes[12] ^= 0x40;
        File.WriteAllBytes(indexPath, bytes);

        AssertTrue(ProgressiveHttpCacheIndex.TryLoad(indexPath) == null);
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

static void ProgressiveCacheIndexAllowsMissingOptionalValidators()
{
    var index = ProgressiveHttpCacheIndex.Create(
        "https://example.test/audio.bin",
        1024,
        "\"v1\"",
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        [new CachedByteRange(0, 128)]);

    AssertTrue(index.Matches("https://example.test/audio.bin", 1024, null, null));
    AssertFalse(index.Matches("https://example.test/other.bin", 1024, null, null));
    AssertFalse(index.Matches("https://example.test/audio.bin", 2048, null, null));
    AssertFalse(index.Matches("https://example.test/audio.bin", 1024, "\"v2\"", null));
    AssertFalse(index.Matches(
        "https://example.test/audio.bin",
        1024,
        null,
        new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));
}

static void ProgressiveHttpResumesPartialCacheAndSeekRanges()
{
    var tempDir = CreateTempDirectory();
    try
    {
        var data = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var url = "https://example.test/audio.bin";
        var finalPath = Path.Combine(tempDir, "audio.bin");
        var partPath = finalPath + ".part";
        var indexPath = partPath + ".idx";
        var lastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using (var part = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            part.Position = 0;
            part.Write(data, 0, 64);
            part.Position = 512;
            part.Write(data, 512, 64);
        }

        ProgressiveHttpCacheIndex.Create(
            url,
            data.Length,
            "\"v1\"",
            lastModified,
            [new CachedByteRange(0, 64), new CachedByteRange(512, 64)])
            .Save(indexPath);

        using var client = new HttpClient(new RangeHttpMessageHandler(data, "\"v1\"", lastModified));
        using var handle = ProgressiveHttpStreamHandle.CreateAsync(
            url,
            finalPath,
            client,
            readBufferSize: 16,
            resume: true)
            .GetAwaiter()
            .GetResult();

        AssertGreaterThan(0L, handle.DownloadedBytes);
        AssertEqual(data.Length, handle.TotalBytes);
        AssertEqual(MaResult.MaSuccess, handle.OnSeek(IntPtr.Zero, 2048, SeekOrigin.Begin));

        var buffer = Marshal.AllocHGlobal(32);
        try
        {
            var totalRead = 0;
            while (totalRead < 32)
            {
                var readResult = handle.OnRead(IntPtr.Zero, IntPtr.Add(buffer, totalRead), (nuint)(32 - totalRead), out var bytesRead);
                AssertEqual(MaResult.MaSuccess, readResult);
                AssertGreaterThan((nuint)0, bytesRead);
                totalRead += (int)bytesRead;
            }

            var actual = new byte[32];
            Marshal.Copy(buffer, actual, 0, actual.Length);
            AssertSequenceEqual(data.Skip(2048).Take(32).ToArray(), actual);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        handle.Dispose();

        var resumedIndex = ProgressiveHttpCacheIndex.TryLoad(indexPath)
            ?? throw new InvalidOperationException("Expected persisted progressive index.");
        AssertTrue(resumedIndex.Ranges.Any(range => range.Start <= 2048 && range.EndExclusive >= 2080));
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

static void ProgressiveHttpTruncatesStalePartialTails()
{
    var tempDir = CreateTempDirectory();
    try
    {
        var data = Enumerable.Range(0, 512).Select(i => (byte)(i % 251)).ToArray();
        var url = "https://example.test/full.bin";
        var finalPath = Path.Combine(tempDir, "full.bin");
        var partPath = finalPath + ".part";
        var indexPath = partPath + ".idx";
        var lastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using (var part = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            part.Write(data, 0, data.Length);
            part.SetLength(data.Length + 128);
        }

        ProgressiveHttpCacheIndex.Create(
            url,
            data.Length,
            "\"v1\"",
            lastModified,
            [new CachedByteRange(0, data.Length)])
            .Save(indexPath);

        using var client = new HttpClient(new RangeHttpMessageHandler(data, "\"v1\"", lastModified));
        using var handle = ProgressiveHttpStreamHandle.CreateAsync(url, finalPath, client).GetAwaiter().GetResult();

        WaitUntil(() => handle.DownloadState == ProgressiveDownloadState.Completed, TimeSpan.FromSeconds(2));
        AssertEqual(data.Length, (int)new FileInfo(finalPath).Length);
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

static void ProgressiveHttpReportsFinalCommitFailure()
{
    var tempDir = CreateTempDirectory();
    try
    {
        var data = Enumerable.Range(0, 512).Select(i => (byte)(i % 251)).ToArray();
        var url = "https://example.test/final-commit-failure.bin";
        var finalPath = Path.Combine(tempDir, "final-commit-failure.bin");
        var lastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(finalPath);

        using var client = new HttpClient(new RangeHttpMessageHandler(data, "\"v1\"", lastModified));
        using var handle = ProgressiveHttpStreamHandle.CreateAsync(
            url,
            finalPath,
            client,
            readBufferSize: 16)
            .GetAwaiter()
            .GetResult();

        WaitUntil(() => handle.DownloadState == ProgressiveDownloadState.Failed, TimeSpan.FromSeconds(2));
        AssertEqual(MaResult.MaIoError, handle.LastResult);
        AssertTrue(handle.LastError is IOException);
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

static void MemoryReadCacheWindowBasicSequentialReadWrite()
{
    var cache = new MemoryReadCacheWindow(capacity: 1024);
    var dest = new byte[256];

    // 空缓存读取失败
    AssertFalse(cache.TryRead(0, dest, out _));

    var data = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
    cache.OnWrite(0, data);

    AssertEqual(0L, cache.StartOffset);
    AssertEqual(100, cache.ValidLength);

    // 从 0 处读取 50 字节
    AssertTrue(cache.TryRead(0, dest.AsSpan(0, 50), out var read1));
    AssertEqual(50, read1);
    AssertSequenceEqual(data.Take(50).ToArray(), dest.Take(50).ToArray());

    // 从 50 处读取剩余 50 字节
    AssertTrue(cache.TryRead(50, dest.AsSpan(0, 50), out var read2));
    AssertEqual(50, read2);
    AssertSequenceEqual(data.Skip(50).Take(50).ToArray(), dest.Take(50).ToArray());

    // 读取越界位置
    AssertFalse(cache.TryRead(100, dest.AsSpan(0, 10), out _));
    AssertFalse(cache.TryRead(-1, dest.AsSpan(0, 10), out _));
}

static void MemoryReadCacheWindowExtendsAndSliding()
{
    var cache = new MemoryReadCacheWindow(capacity: 1024);
    var data1 = Enumerable.Range(0, 500).Select(i => (byte)(i % 256)).ToArray();
    var data2 = Enumerable.Range(500, 500).Select(i => (byte)(i % 256)).ToArray();
    var data3 = Enumerable.Range(1000, 200).Select(i => (byte)(i % 256)).ToArray();

    cache.OnWrite(0, data1);
    AssertEqual(500, cache.ValidLength);

    // 连续写入扩展
    cache.OnWrite(500, data2);
    AssertEqual(1000, cache.ValidLength);

    var dest = new byte[1000];
    AssertTrue(cache.TryRead(0, dest, out var readTotal));
    AssertEqual(1000, readTotal);
    var expected = data1.Concat(data2).ToArray();
    AssertSequenceEqual(expected, dest);

    // 写入超出 Capacity (1024) 时被截断至 Capacity
    cache.OnWrite(1000, data3);
    AssertEqual(1024, cache.ValidLength);

    var dest2 = new byte[1024];
    AssertTrue(cache.TryRead(0, dest2, out var read1024));
    AssertEqual(1024, read1024);
}

static void MemoryReadCacheWindowFillFromDiskAndReset()
{
    var data = Enumerable.Range(0, 4096).Select(i => (byte)(i % 253)).ToArray();
    using var stream = new MemoryStream(data);
    var ranges = new CachedByteRangeSet();
    ranges.Add(0, 4096);

    var cache = new MemoryReadCacheWindow(capacity: 2048);
    cache.FillFromDisk(stream, position: 256, ranges);

    AssertEqual(256L, cache.StartOffset);
    AssertEqual(2048, cache.ValidLength);

    var dest = new byte[512];
    AssertTrue(cache.TryRead(256, dest, out var readBytes));
    AssertEqual(512, readBytes);
    AssertSequenceEqual(data.Skip(256).Take(512).ToArray(), dest);

    cache.Reset();
    AssertEqual(-1L, cache.StartOffset);
    AssertEqual(0, cache.ValidLength);
    AssertFalse(cache.TryRead(256, dest, out _));
}

static void DiskCachedStreamReadsAccuratelyWithMemoryCache()
{
    var data = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
    using var sourceStream = new MemoryStream(data);
    using var handle = new DiskCachedStreamHandle(
        sourceStream,
        bufferSize: 512,
        totalSize: data.Length,
        leaveOpen: true);

    var buffer = new byte[256];
    var gch = GCHandle.Alloc(buffer, GCHandleType.Pinned);
    try
    {
        var ptr = gch.AddrOfPinnedObject();
        var allRead = new List<byte>();
        while (true)
        {
            var res = handle.OnRead(IntPtr.Zero, ptr, (nuint)buffer.Length, out var bytesRead);
            if (res == MaResult.MaAtEnd || (res == MaResult.MaSuccess && bytesRead == 0))
            {
                break;
            }
            AssertEqual(MaResult.MaSuccess, res);
            allRead.AddRange(buffer.Take((int)bytesRead));
        }

        AssertSequenceEqual(data, allRead);
    }
    finally
    {
        gch.Free();
    }
}

static void ProgressiveHttpStreamReadsAccuratelyWithMemoryCache()
{
    var tempDir = CreateTempDirectory();
    try
    {
        var data = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var url = "https://example.test/memory-cache-test.bin";
        var finalPath = Path.Combine(tempDir, "memory-cache-test.bin");
        var lastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using var client = new HttpClient(new RangeHttpMessageHandler(data, "\"v1\"", lastModified));
        using var handle = ProgressiveHttpStreamHandle.CreateAsync(
            url,
            finalPath,
            client,
            readBufferSize: 64)
            .GetAwaiter()
            .GetResult();

        var buffer = new byte[128];
        var gch = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var ptr = gch.AddrOfPinnedObject();
            var allRead = new List<byte>();
            while (allRead.Count < data.Length)
            {
                var res = handle.OnRead(IntPtr.Zero, ptr, (nuint)buffer.Length, out var bytesRead);
                if (res == MaResult.MaAtEnd || (res == MaResult.MaSuccess && bytesRead == 0))
                {
                    break;
                }
                AssertEqual(MaResult.MaSuccess, res);
                allRead.AddRange(buffer.Take((int)bytesRead));
            }

            AssertSequenceEqual(data, allRead);
        }
        finally
        {
            gch.Free();
        }
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

static void AssertFalse(bool value)
{
    if (value)
    {
        throw new InvalidOperationException("Expected false.");
    }
}

static void AssertTrue(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Expected true.");
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

static void AssertSequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
{
    if (expected.Count != actual.Count)
    {
        throw new InvalidOperationException($"Expected sequence length {expected.Count}, actual {actual.Count}.");
    }

    for (var i = 0; i < expected.Count; i++)
    {
        if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
        {
            throw new InvalidOperationException($"Expected item {i} to be {expected[i]}, actual {actual[i]}.");
        }
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

static string CreateTempDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "sap-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void WaitUntil(Func<bool> predicate, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!predicate())
    {
        if (DateTime.UtcNow >= deadline)
        {
            throw new TimeoutException("Timed out waiting for condition.");
        }

        Thread.Sleep(10);
    }
}

sealed class NonSeekableWritableStream : MemoryStream
{
    public override bool CanSeek => false;
}

sealed class RangeHttpMessageHandler(
    byte[] data,
    string eTag,
    DateTimeOffset lastModified) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var range = request.Headers.Range?.Ranges.SingleOrDefault();
        var start = range?.From ?? 0;
        var end = range?.To ?? data.Length - 1;
        if (start < 0 || start >= data.Length || end < start)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        }

        var length = (int)Math.Min(data.Length - start, end - start + 1);
        var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new StreamContent(new SlowByteArrayStream(data, (int)start, length, maxChunkSize: 16))
        };

        response.Headers.ETag = new EntityTagHeaderValue(eTag);
        response.Content.Headers.LastModified = lastModified;
        response.Content.Headers.ContentLength = length;
        if (range != null)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, data.Length);
        }

        return Task.FromResult(response);
    }
}

sealed class SlowByteArrayStream : Stream
{
    private readonly byte[] _data;
    private readonly int _offset;
    private readonly int _length;
    private readonly int _maxChunkSize;
    private readonly int _end;
    private int _position;

    public SlowByteArrayStream(byte[] data, int offset, int length, int maxChunkSize)
    {
        _data = data;
        _offset = offset;
        _length = length;
        _maxChunkSize = maxChunkSize;
        _end = offset + length;
        _position = offset;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position - _offset;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _end)
        {
            return 0;
        }

        var bytesToCopy = Math.Min(Math.Min(count, _maxChunkSize), _end - _position);
        Buffer.BlockCopy(_data, _position, buffer, offset, bytesToCopy);
        _position += bytesToCopy;
        Thread.Sleep(1);
        return bytesToCopy;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var read = Read(rented, 0, buffer.Length);
            rented.AsMemory(0, read).CopyTo(buffer);
            return ValueTask.FromResult(read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
