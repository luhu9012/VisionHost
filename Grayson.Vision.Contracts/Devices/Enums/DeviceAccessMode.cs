namespace Grayson.Vision.Contracts.Devices.Enums
{
    /// <summary>
    /// 设备资源占用模式：调度器申请设备时声明。
    /// </summary>
    public enum DeviceAccessMode
    {
        /// <summary>独占模式（默认）：机器人、相机等不能并发共享。</summary>
        Exclusive = 0,

        /// <summary>共享只读模式：如 PLC 寄存器只读、状态查询等。</summary>
        SharedReadOnly = 1,

        /// <summary>共享写模式：允许多个工单/节点同时写，实现自行保证线程安全。</summary>
        SharedReadWrite = 2
    }
}
