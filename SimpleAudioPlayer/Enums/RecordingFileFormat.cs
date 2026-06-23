namespace SimpleAudioPlayer.Enums;

/// <summary>
/// Recording output container format.
/// </summary>
public enum RecordingFileFormat
{
    /// <summary>
    /// AAC audio in an M4A container.
    /// </summary>
    M4A = 1,

    /// <summary>
    /// AAC audio in an ADTS stream.
    /// </summary>
    Aac = 2,

    /// <summary>
    /// PCM audio in a WAV container.
    /// </summary>
    Wav = 3,

    /// <summary>
    /// Raw interleaved PCM frames without a container header.
    /// </summary>
    Pcm = 4
}
