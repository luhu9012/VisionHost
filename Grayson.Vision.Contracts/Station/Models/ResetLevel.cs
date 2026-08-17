namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 复位分级
    /// </summary>
    public enum ResetLevel
    {
        /// <summary>
        /// 工单级复位：只终止当前工单，释放本次占用设备，不改工位全局状态。
        /// </summary>
        WorkOrder,

        /// <summary>
        /// 工位软复位（生产复位）：执行 ResetBlueprint，回安全点、IO 复位、清空队列，不重新初始化硬件句柄。
        /// </summary>
        StationSoft,

        /// <summary>
        /// 硬件全复位：关闭并重新 Open 所有设备句柄，用于断连后恢复。
        /// </summary>
        HardwareFull
    }
}
