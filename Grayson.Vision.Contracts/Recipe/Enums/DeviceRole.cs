namespace Grayson.Vision.Contracts.Recipe.Enums
{
    /// <summary>
    /// 配方中设备的角色定义。
    /// </summary>
    public enum DeviceRole
    {
        /// <summary>主设备：正常生产流程使用</summary>
        Primary,

        /// <summary>备用设备：主设备故障时切换</summary>
        Standby,

        /// <summary>校准设备：用于标定与校验</summary>
        Calibration,

        /// <summary>检测/参考设备：不直接参与执行，用于对比或 Audit</summary>
        Reference
    }
}
