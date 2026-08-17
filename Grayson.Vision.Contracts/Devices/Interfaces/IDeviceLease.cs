using Grayson.Vision.Contracts.Devices.Enums;
using System;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>
    /// 设备资源租赁契约：一次工单对某逻辑设备的占用凭证。
    /// 节点只应使用 Lease 中暴露的设备实例，禁止自行 Open/Close 硬件。
    /// </summary>
    public interface IDeviceLease : IDisposable
    {
        /// <summary>租赁唯一标识</summary>
        string LeaseId { get; }

        /// <summary>工位 ID</summary>
        string StationId { get; }

        /// <summary>工单/批次 ID</summary>
        string WorkOrderId { get; }

        /// <summary>逻辑设备名</summary>
        string LogicalDeviceKey { get; }

        /// <summary>占用模式</summary>
        DeviceAccessMode AccessMode { get; }

        /// <summary>租赁开始时间</summary>
        DateTime LeasedAt { get; }

        /// <summary>当前租赁是否仍有效</summary>
        bool IsValid { get; }

        /// <summary>获取被租赁的物理设备实例</summary>
        IDevice Device { get; }

        /// <summary>
        /// 释放租赁。调用后节点不得再访问 Device。
        /// </summary>
        void Release();
    }
}
