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

var tests = new List<(string Name, Action Test)>
{
    ("recording format enum matches native values", RecordingFormatValuesMatchNative),
    ("recorder validates constructor options", RecorderValidatesConstructorOptions),
    ("recorder default state is stopped", RecorderDefaultStateIsStopped),
    ("recorder validates start arguments before native calls", RecorderValidatesStartArguments),
    ("native recorder entry points match exports", NativeRecorderEntryPointsMatchExports),
    ("disk cached stream uses disposable part cache", DiskCachedStreamUsesDisposablePartCache),
    ("disk cached stream handles seekable and explicit cached seek", DiskCachedStreamHandlesSeekableAndExplicitCachedSeek),
    ("disk cached stream preserves cache after seekable source reads", DiskCachedStreamPreservesCacheAfterSeekableSourceReads),
    ("disk cached stream commits completed cache", DiskCachedStreamCommitsCompletedCache),
    ("disk cached stream deletes incomplete persistent cache", DiskCachedStreamDeletesIncompletePersistentCache),
    ("progressive cache index rejects modified files", ProgressiveCacheIndexRejectsModifiedFiles),
    ("progressive cache index allows missing optional validators", ProgressiveCacheIndexAllowsMissingOptionalValidators),
    ("progressive http resumes partial cache and persists seek ranges", ProgressiveHttpResumesPartialCacheAndSeekRanges),
    ("progressive http truncates stale partial tails", ProgressiveHttpTruncatesStalePartialTails)
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
