using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>
    /// 工位级设备管理器契约：统一接管设备生命周期、租赁、占用与释放。
    /// 实现位于 Grayson.Vision.Core，UI 层仅绑定/订阅状态。
    /// </summary>
    public interface IDeviceManager : IDisposable
    {
        /// <summary>所属工位 ID</summary>
        string StationId { get; }

        /// <summary>所有已注册逻辑设备的当前状态快照</summary>
        IReadOnlyDictionary<string, DeviceState> GetDeviceStates();

        /// <summary>设备状态变更通知</summary>
        event EventHandler<DeviceStateChangedEventArgs> DeviceStateChanged;

        /// <summary>
        /// 注册或替换逻辑设备到设备管理器。
        /// 通常由系统初始化或 UI 配置流程调用。
        /// </summary>
        void RegisterDevice(string logicalDeviceKey, IDevice device, DeviceAccessMode defaultAccessMode = DeviceAccessMode.Exclusive);

        /// <summary>取消注册逻辑设备</summary>
        bool UnregisterDevice(string logicalDeviceKey);

        /// <summary>
        /// 申请设备租赁。工单位于执行前统一调用，失败则不应执行节点。
        /// </summary>
        Task<Result<IDeviceLease>> AcquireAsync(string workOrderId, string logicalDeviceKey,
            DeviceAccessMode? accessMode = null, TimeSpan? timeout = null);

        /// <summary>
        /// 释放指定租赁。
        /// </summary>
        Task ReleaseAsync(IDeviceLease lease);

        /// <summary>
        /// 打开所有已注册设备（不建立独占占用）。
        /// </summary>
        Task<Result> OpenAllAsync();

        /// <summary>
        /// 关闭所有已注册设备并释放资源。
        /// </summary>
        Task CloseAllAsync();

        /// <summary>
        /// 对Faulted/Locked设备进行重连尝试。
        /// </summary>
        Task<Result> ReconnectAsync(string logicalDeviceKey);

        /// <summary>
        /// 获取指定逻辑设备的物理实例（不建立租赁，仅用于状态查询等只读场景）。
        /// </summary>
        IDevice GetDeviceInstance(string logicalDeviceKey);

        /// <summary>
        /// 统一执行设备心跳检测，并将故障设备状态置为 Error 且触发事件。
        /// </summary>
        Task<Result> HeartbeatAllAsync();

        /// <summary>
        /// 获取当前所有活跃租赁快照。
        /// </summary>
        IReadOnlyCollection<IDeviceLease> GetActiveLeases();
    }

    /// <summary>
    /// 设备状态变更事件参数
    /// </summary>
    public class DeviceStateChangedEventArgs : EventArgs
    {
        public string StationId { get; }
        public string LogicalDeviceKey { get; }
        public DeviceState OldState { get; }
        public DeviceState NewState { get; }
        public string Reason { get; }

        public DeviceStateChangedEventArgs(string stationId, string logicalDeviceKey,
            DeviceState oldState, DeviceState newState, string reason = null)
        {
            StationId = stationId;
            LogicalDeviceKey = logicalDeviceKey;
            OldState = oldState;
            NewState = newState;
            Reason = reason;
        }
    }
}
