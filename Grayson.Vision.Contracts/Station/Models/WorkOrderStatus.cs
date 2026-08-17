namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 工单完整生命周期状态
    /// </summary>
    public enum WorkOrderStatus
    {
        /// <summary>已创建，等待执行</summary>
        Created,

        /// <summary>正在执行</summary>
        Running,

        /// <summary>正常完成</summary>
        Completed_OK,

        /// <summary>执行结果判定为 NG</summary>
        Completed_NG,

        /// <summary>因超时终止</summary>
        Abort_Timeout,

        /// <summary>被取消</summary>
        Abort_Cancel,

        /// <summary>因异常终止</summary>
        Abort_Error,

        /// <summary>因安全联锁（急停、光栅等）终止</summary>
        Abort_Safety,

        /// <summary>等待资源（设备被占用）中</summary>
        Waiting_Resource
    }
}
