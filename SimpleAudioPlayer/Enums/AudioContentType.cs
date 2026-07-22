namespace SimpleAudioPlayer.Enums;

/// <summary>
/// Describes the content carried by the playback stream.
/// Unsupported mappings safely fall back to the platform default.
/// </summary>
public enum AudioContentType
{
    Default = 0,
    Music = 1,
    Speech = 2,
    Movie = 3,
    Sonification = 4
}
