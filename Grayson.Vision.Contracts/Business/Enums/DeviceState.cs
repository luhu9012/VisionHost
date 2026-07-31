namespace Grayson.Vision.Contracts.Business.Enums
{
    /// <summary>所有硬件通用状态</summary>
    public enum DeviceState
    {
        /// <summary>未连接</summary>
        Disconnected,
        /// <summary>连接中</summary>
        Connecting,
        /// <summary>正常在线</summary>
        Connected,
        /// <summary>故障异常</summary>
        Error
    }
}