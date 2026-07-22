using SimpleAudioPlayer.Enums;

namespace SimpleAudioPlayer;

/// <summary>
/// Configures the audio output device used by <see cref="AudioPlayer"/>.
/// </summary>
public sealed class AudioPlayerOptions
{
    public SampleFormat SampleFormat { get; init; } = SampleFormat.F32;

    /// <summary>
    /// Output channel count. Set to 0 to use the device's native channel count.
    /// </summary>
    public uint Channels { get; init; } = 2;

    /// <summary>
    /// Output sample rate. Set to 0 to use the device's native sample rate.
    /// </summary>
    public uint SampleRate { get; init; }

    public AudioLatencyMode LatencyMode { get; init; } = AudioLatencyMode.Playback;

    public AudioPlaybackUsage Usage { get; init; } = AudioPlaybackUsage.Media;

    public AudioContentType ContentType { get; init; } = AudioContentType.Music;

    /// <summary>
    /// Preferred period size in milliseconds. Set to 0 to use the backend default.
    /// </summary>
    public uint PeriodSizeInMilliseconds { get; init; }

    /// <summary>
    /// Preferred number of periods. Set to 0 to use the backend default.
    /// </summary>
    public uint Periods { get; init; }

    public AudioShareMode ShareMode { get; init; } = AudioShareMode.Shared;

    internal void Validate()
    {
        ValidateEnum(SampleFormat, nameof(SampleFormat));
        if (SampleFormat == SampleFormat.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleFormat), SampleFormat, "Sample format must be known.");
        }

        ValidateEnum(LatencyMode, nameof(LatencyMode));
        ValidateEnum(Usage, nameof(Usage));
        ValidateEnum(ContentType, nameof(ContentType));
        ValidateEnum(ShareMode, nameof(ShareMode));
    }

    private static void ValidateEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Unknown {typeof(TEnum).Name} value.");
        }
    }
}
