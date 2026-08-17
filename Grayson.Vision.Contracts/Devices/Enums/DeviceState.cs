namespace Grayson.Vision.Contracts.Devices.Enums
{
    /// <summary>
    /// 硬件设备统一生命周期状态机。
    /// 兼容旧值位置保留前 4 个枚举值，避免已编译插件/上层 UI 出现破坏性变更。
    /// </summary>
    public enum DeviceState
    {
        /// <summary>未连接（旧值：已初始化但未打开）</summary>
        Disconnected = 0,
        /// <summary>连接中</summary>
        Connecting = 1,
        /// <summary>正常在线（旧值：已连接）</summary>
        Connected = 2,
        /// <summary>故障异常</summary>
        Error = 3,

        /// <summary>未初始化（仅声明，未加载配置）</summary>
        Uninitialized = 10,
        /// <summary>已关闭，句柄已释放</summary>
        Closed = 11,
        /// <summary>已打开，可接受任务调度</summary>
        Opened = 12,
        /// <summary>被某个工单/工位占用</summary>
        Busy = 13,

        /// <summary>正在软复位</summary>
        Resetting = 20,
        /// <summary>正在重连</summary>
        Reconnecting = 21,

        /// <summary>离线/不可用，等待人工介入</summary>
        Locked = 30
    }
}
