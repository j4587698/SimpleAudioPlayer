namespace SimpleAudioPlayer.Enums;

/// <summary>
/// 设备状态枚举 
/// </summary>
public enum PlayState
{
    /// <summary>
    /// 设备未初始化状态
    /// <para>ma_device_state_uninitialized</para>
    /// </summary>
    Uninitialized = 0,

    /// <summary>
    /// 设备已停止（初始化后的默认状态）
    /// <para>ma_device_state_stopped</para>
    /// </summary>
    Stopped = 1,

    /// <summary>
    /// 设备已启动（正在请求/传递音频数据）
    /// <para>ma_device_state_started</para>
    /// </summary>
    Started = 2,

    /// <summary>
    /// 设备正在启动（从停止状态转换到启动状态）
    /// <para>ma_device_state_starting</para>
    /// </summary>
    Starting = 3,

    /// <summary>
    /// 设备正在停止（从启动状态转换到停止状态）
    /// <para>ma_device_state_stopping</para>
    /// </summary>
    Stopping = 4
}
