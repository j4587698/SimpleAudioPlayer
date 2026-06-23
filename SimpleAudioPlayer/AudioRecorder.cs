using SimpleAudioPlayer.Enums;
using SimpleAudioPlayer.Native;
using System.Runtime.InteropServices;

namespace SimpleAudioPlayer;

public sealed class AudioRecorder : IDisposable
{
    private const int AvSeekSize = 0x10000;
    private const int AvSeekForce = 0x20000;
    private readonly SampleFormat _sampleFormat;
    private readonly uint _channels;
    private readonly uint _sampleRate;
    private readonly uint _bitRate;
    private AudioRecorderContextHandle? _ctx;
    private AudioRecorderStreamSink? _streamSink;
    private bool _disposed;
    private bool _isRecording;
    private ulong _capturedFrames;
    private ulong _droppedFrames;
    private MaResult _lastResult = MaResult.MaSuccess;

    public AudioRecorder(
        SampleFormat sampleFormat = SampleFormat.F32,
        uint channels = 2,
        uint sampleRate = 44100,
        uint bitRate = 128000)
    {
        if (sampleFormat == SampleFormat.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleFormat), sampleFormat, "Sample format must be known.");
        }
        if (channels == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be greater than zero.");
        }
        if (sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be greater than zero.");
        }

        _sampleFormat = sampleFormat;
        _channels = channels;
        _sampleRate = sampleRate;
        _bitRate = bitRate;
    }

    public bool IsRecording => _isRecording;

    public MaResult LastResult => _lastResult;

    public ulong CapturedFrames => _ctx is { IsInvalid: false }
        ? NativeMethods.AudioRecorderGetCapturedFrames(_ctx)
        : _capturedFrames;

    public ulong DroppedFrames => _ctx is { IsInvalid: false }
        ? NativeMethods.AudioRecorderGetDroppedFrames(_ctx)
        : _droppedFrames;

    public bool Start(string outputPath, RecordingFileFormat format = RecordingFileFormat.Wav)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ValidateFormat(format);
        if (format == RecordingFileFormat.Pcm)
        {
            throw new ArgumentException("Raw PCM recording requires a stream output.", nameof(format));
        }

        if (_isRecording)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(outputPath);
        var context = NativeMethods.AudioRecorderContextCreate();
        if (context.IsInvalid)
        {
            throw new InvalidOperationException("Failed to create audio recorder context.");
        }

        var result = NativeMethods.AudioRecorderInitFile(
            context,
            fullPath,
            format,
            _sampleFormat,
            _channels,
            _sampleRate,
            _bitRate);

        if (result != MaResult.MaSuccess)
        {
            _lastResult = result;
            context.Dispose();
            return false;
        }

        result = NativeMethods.AudioRecorderStart(context);
        if (result != MaResult.MaSuccess)
        {
            _lastResult = result;
            context.Dispose();
            return false;
        }

        _ctx = context;
        _capturedFrames = 0;
        _droppedFrames = 0;
        _lastResult = MaResult.MaSuccess;
        _isRecording = true;
        return true;
    }

    public bool Start(Stream output, RecordingFileFormat format = RecordingFileFormat.Wav)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);
        ValidateFormat(format);

        if (!output.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        }
        if (RequiresSeekableOutput(format) && !output.CanSeek)
        {
            throw new ArgumentException("WAV and M4A recording require a seekable stream.", nameof(output));
        }
        if (_isRecording)
        {
            return false;
        }

        var context = NativeMethods.AudioRecorderContextCreate();
        if (context.IsInvalid)
        {
            throw new InvalidOperationException("Failed to create audio recorder context.");
        }

        var sink = new AudioRecorderStreamSink(output);
        var result = NativeMethods.AudioRecorderInitStream(
            context,
            format,
            sink.WriteCallback,
            output.CanSeek ? sink.SeekCallback : null,
            sink.UserData,
            _sampleFormat,
            _channels,
            _sampleRate,
            _bitRate);

        if (result != MaResult.MaSuccess)
        {
            _lastResult = result;
            context.Dispose();
            sink.Dispose();
            return false;
        }

        result = NativeMethods.AudioRecorderStart(context);
        if (result != MaResult.MaSuccess)
        {
            _lastResult = result;
            context.Dispose();
            sink.Dispose();
            return false;
        }

        _ctx = context;
        _streamSink = sink;
        _capturedFrames = 0;
        _droppedFrames = 0;
        _lastResult = MaResult.MaSuccess;
        _isRecording = true;
        return true;
    }

    public bool Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isRecording || _ctx == null)
        {
            return false;
        }

        var context = _ctx;
        var result = NativeMethods.AudioRecorderStop(context);
        _capturedFrames = NativeMethods.AudioRecorderGetCapturedFrames(context);
        _droppedFrames = NativeMethods.AudioRecorderGetDroppedFrames(context);
        _lastResult = result == MaResult.MaSuccess
            ? NativeMethods.AudioRecorderGetResult(context)
            : result;

        _isRecording = false;
        _ctx = null;
        context.Dispose();
        _streamSink?.Dispose();
        _streamSink = null;
        return _lastResult == MaResult.MaSuccess;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_isRecording)
        {
            Stop();
        }
        else
        {
            _ctx?.Dispose();
            _ctx = null;
            _streamSink?.Dispose();
            _streamSink = null;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void ValidateFormat(RecordingFileFormat format)
    {
        if (format is not RecordingFileFormat.M4A and not RecordingFileFormat.Aac and not RecordingFileFormat.Wav and not RecordingFileFormat.Pcm)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported recording file format.");
        }
    }

    private static bool RequiresSeekableOutput(RecordingFileFormat format)
    {
        return format is RecordingFileFormat.Wav or RecordingFileFormat.M4A;
    }

    private sealed class AudioRecorderStreamSink : IDisposable
    {
        private readonly object _syncRoot = new();
        private readonly Stream _stream;
        private readonly GCHandle _handle;
        private bool _disposed;

        public AudioRecorderStreamSink(Stream stream)
        {
            _stream = stream;
            _handle = GCHandle.Alloc(this);
            UserData = GCHandle.ToIntPtr(_handle);
            WriteCallback = Write;
            SeekCallback = Seek;
        }

        public IntPtr UserData { get; }

        public NativeMethods.AudioRecorderWriteDelegate WriteCallback { get; }

        public NativeMethods.AudioRecorderSeekDelegate SeekCallback { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _handle.Free();
            _disposed = true;
        }

        private static unsafe MaResult Write(IntPtr userdata, IntPtr buffer, nuint bytesToWrite, out nuint bytesWritten)
        {
            bytesWritten = 0;
            try
            {
                if (bytesToWrite > int.MaxValue)
                {
                    return MaResult.MaInvalidArgs;
                }

                var sink = FromUserData(userdata);
                var length = (int)bytesToWrite;
                var span = new ReadOnlySpan<byte>(buffer.ToPointer(), length);
                lock (sink._syncRoot)
                {
                    sink._stream.Write(span);
                }
                bytesWritten = bytesToWrite;
                return MaResult.MaSuccess;
            }
            catch
            {
                return MaResult.MaIoError;
            }
        }

        private static MaResult Seek(IntPtr userdata, long offset, int origin, out long cursor)
        {
            cursor = 0;
            try
            {
                var sink = FromUserData(userdata);
                lock (sink._syncRoot)
                {
                    if ((origin & AvSeekSize) == AvSeekSize)
                    {
                        cursor = sink._stream.Length;
                        return MaResult.MaSuccess;
                    }

                    if (!sink._stream.CanSeek)
                    {
                        return MaResult.MaNotImplemented;
                    }

                    var seekOrigin = (origin & ~AvSeekForce) switch
                    {
                        0 => SeekOrigin.Begin,
                        1 => SeekOrigin.Current,
                        2 => SeekOrigin.End,
                        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported seek origin.")
                    };
                    cursor = sink._stream.Seek(offset, seekOrigin);
                    return MaResult.MaSuccess;
                }
            }
            catch
            {
                return MaResult.MaIoError;
            }
        }

        private static AudioRecorderStreamSink FromUserData(IntPtr userdata)
        {
            var handle = GCHandle.FromIntPtr(userdata);
            return handle.Target as AudioRecorderStreamSink
                ?? throw new InvalidOperationException("Invalid recorder stream callback state.");
        }
    }
}
