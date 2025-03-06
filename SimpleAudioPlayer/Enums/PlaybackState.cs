namespace SimpleAudioPlayer.Enums;

/// <summary>
/// 表示媒体播放器的当前状态
/// </summary>
public enum PlaybackState
{
    /// <summary>
    /// 播放已停止（初始状态）
    /// </summary>
    Stopped,

    /// <summary>
    /// 正在播放媒体
    /// </summary>
    Playing,

    /// <summary>
    /// 播放已暂停（可恢复状态）
    /// </summary>
    Paused,

    /// <summary>
    /// 正在缓冲媒体数据
    /// </summary>
    Buffering,

    /// <summary>
    /// 媒体已播放完毕
    /// </summary>
    Completed,

    /// <summary>
    /// 发生错误，无法继续播放
    /// </summary>
    Error
}