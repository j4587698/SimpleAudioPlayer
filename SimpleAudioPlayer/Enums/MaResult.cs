namespace SimpleAudioPlayer.Enums;

public enum MaResult
{
    /// <summary>
    /// 操作成功
    /// </summary>
    MaSuccess = 0,

    // ===============================
    // 标准系统错误 (映射到常见错误码)
    // ===============================
    /// <summary>通用错误</summary>
    MaError = -1,

    /// <summary>无效参数</summary>
    MaInvalidArgs = -2,

    /// <summary>无效操作</summary>
    MaInvalidOperation = -3,

    /// <summary>内存不足</summary>
    MaOutOfMemory = -4,

    /// <summary>超出范围</summary>
    MaOutOfRange = -5,

    /// <summary>访问被拒绝</summary>
    MaAccessDenied = -6,

    /// <summary>资源不存在</summary>
    MaDoesNotExist = -7,

    /// <summary>资源已存在</summary>
    MaAlreadyExists = -8,

    /// <summary>打开文件过多</summary>
    MaTooManyOpenFiles = -9,

    /// <summary>无效文件</summary>
    MaInvalidFile = -10,

    /// <summary>文件过大</summary>
    MaTooBig = -11,

    /// <summary>路径过长</summary>
    MaPathTooLong = -12,

    /// <summary>名称过长</summary>
    MaNameTooLong = -13,

    /// <summary>不是目录</summary>
    MaNotDirectory = -14,

    /// <summary>是目录</summary>
    MaIsDirectory = -15,

    /// <summary>目录非空</summary>
    MaDirectoryNotEmpty = -16,

    /// <summary>到达结尾</summary>
    MaAtEnd = -17,

    /// <summary>磁盘空间不足</summary>
    MaNoSpace = -18,

    /// <summary>资源忙</summary>
    MaBusy = -19,

    /// <summary>I/O错误</summary>
    MaIoError = -20,

    /// <summary>操作中断</summary>
    MaInterrupt = -21,

    /// <summary>资源不可用</summary>
    MaUnavailable = -22,

    /// <summary>资源已被占用</summary>
    MaAlreadyInUse = -23,

    /// <summary>错误地址</summary>
    MaBadAddress = -24,

    /// <summary>无效寻址</summary>
    MaBadSeek = -25,

    /// <summary>管道错误</summary>
    MaBadPipe = -26,

    /// <summary>死锁</summary>
    MaDeadlock = -27,

    /// <summary>符号链接过多</summary>
    MaTooManyLinks = -28,

    /// <summary>未实现功能</summary>
    MaNotImplemented = -29,

    /// <summary>无消息</summary>
    MaNoMessage = -30,

    /// <summary>错误消息</summary>
    MaBadMessage = -31,

    /// <summary>无可用数据</summary>
    MaNoDataAvailable = -32,

    /// <summary>无效数据</summary>
    MaInvalidData = -33,

    /// <summary>操作超时</summary>
    MaTimeout = -34,

    /// <summary>网络不可用</summary>
    MaNoNetwork = -35,

    /// <summary>非唯一项</summary>
    MaNotUnique = -36,

    /// <summary>非套接字</summary>
    MaNotSocket = -37,

    /// <summary>无地址信息</summary>
    MaNoAddress = -38,

    /// <summary>协议错误</summary>
    MaBadProtocol = -39,

    /// <summary>协议不可用</summary>
    MaProtocolUnavailable = -40,

    /// <summary>协议不支持</summary>
    MaProtocolNotSupported = -41,

    /// <summary>协议族不支持</summary>
    MaProtocolFamilyNotSupported = -42,

    /// <summary>地址族不支持</summary>
    MaAddressFamilyNotSupported = -43,

    /// <summary>套接字类型不支持</summary>
    MaSocketNotSupported = -44,

    /// <summary>连接重置</summary>
    MaConnectionReset = -45,

    /// <summary>已连接</summary>
    MaAlreadyConnected = -46,

    /// <summary>未连接</summary>
    MaNotConnected = -47,

    /// <summary>连接被拒绝</summary>
    MaConnectionRefused = -48,

    /// <summary>无主机</summary>
    MaNoHost = -49,

    /// <summary>操作进行中</summary>
    MaInProgress = -50,

    /// <summary>操作已取消</summary>
    MaCancelled = -51,

    /// <summary>内存已映射</summary>
    MaMemoryAlreadyMapped = -52,

    // ======================
    // 非标准错误（自定义错误）
    // ======================
    /// <summary>CRC校验失败</summary>
    MaCrcMismatch = -100,

    // ======================
    // miniaudio 特定错误
    // ======================
    /// <summary>格式不支持</summary>
    MaFormatNotSupported = -200,

    /// <summary>设备类型不支持</summary>
    MaDeviceTypeNotSupported = -201,

    /// <summary>共享模式不支持</summary>
    MaShareModeNotSupported = -202,

    /// <summary>无可用后端</summary>
    MaNoBackend = -203,

    /// <summary>无可用设备</summary>
    MaNoDevice = -204,

    /// <summary>API未找到</summary>
    MaApiNotFound = -205,

    /// <summary>无效设备配置</summary>
    MaInvalidDeviceConfig = -206,

    /// <summary>循环操作</summary>
    MaLoop = -207,

    /// <summary>后端未启用</summary>
    MaBackendNotEnabled = -208,

    // ======================
    // 状态错误
    // ======================
    /// <summary>设备未初始化</summary>
    MaDeviceNotInitialized = -300,

    /// <summary>设备已初始化</summary>
    MA_DEVICE_ALREADY_INITIALIZED = -301,

    /// <summary>设备未启动</summary>
    MA_DEVICE_NOT_STARTED = -302,

    /// <summary>设备未停止</summary>
    MA_DEVICE_NOT_STOPPED = -303,

    // ======================
    // 操作错误
    // ======================
    /// <summary>后端初始化失败</summary>
    MaFailedToInitBackend = -400,

    /// <summary>打开后端设备失败</summary>
    MaFailedToOpenBackendDevice = -401,

    /// <summary>启动后端设备失败</summary>
    MaFailedToStartBackendDevice = -402,

    /// <summary>停止后端设备失败</summary>
    MaFailedToStopBackendDevice = -403
}