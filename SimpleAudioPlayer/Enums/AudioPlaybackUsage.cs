namespace SimpleAudioPlayer.Enums;

/// <summary>
/// Describes how the application intends to use the playback stream.
/// Unsupported mappings safely fall back to the platform default.
/// </summary>
public enum AudioPlaybackUsage
{
    Default = 0,
    Media = 1,
    Game = 2,
    VoiceCommunication = 3,
    Notification = 4,
    Alarm = 5
}
