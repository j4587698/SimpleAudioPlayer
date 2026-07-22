namespace SimpleAudioPlayer.Enums;

/// <summary>
/// Specifies the playback latency preference.
/// </summary>
public enum AudioLatencyMode
{
    /// <summary>
    /// Prioritizes low latency over buffering and power efficiency.
    /// </summary>
    LowLatency = 0,

    /// <summary>
    /// Prioritizes stable media playback and platform processing paths.
    /// </summary>
    Playback = 1
}
