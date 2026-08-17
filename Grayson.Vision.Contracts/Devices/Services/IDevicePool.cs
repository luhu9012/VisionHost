using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Devices.Services
{
    /// <summary>
    /// 全局设备池服务契约。
    /// 负责：插件加载、物理设备扫描、设备实例注册/领用/释放、持久化同步。
    /// UI 层统一面向此接口编程，不直接依赖 Core 实现。
    /// </summary>
    public interface IDevicePool : IDisposable
    {
        /// <summary>
        /// 设备池是否已完成初始化。
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// 底层设备插件管理器（用于 UI 层插件列表、扫描等高级场景）。
        /// </summary>
        IDevicePluginManager PluginManager { get; }

        /// <summary>
        /// 设备状态发生变化时触发（兼容性事件）。
        /// </summary>
        event EventHandler<DeviceStateChangedEventArgs> OnDeviceStateChanged;

        /// <summary>
        /// 初始化：加载所有插件并从持久化存储恢复已注册设备实例。
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// 扫描所有已加载插件下的在线物理硬件信息（只读模式，不修改内存池与数据库）。
        /// </summary>
        List<DeviceInfo> ScanAllPhysicalDevices();

        /// <summary>
        /// 将扫描到的物理设备加入设备池并持久化。
        /// </summary>
        Task<Result<IDevice>> AddDeviceToPoolAndSaveAsync(DeviceInfo info, string userDeviceKey, string connectionString = "");

        /// <summary>
        /// 手动创建设备（按优先级自动路由到 SDK 插件或通用协议插件）并持久化。
        /// </summary>
        Task<Result<IDevice>> CreateAndSaveManualDeviceAsync(DeviceCategory category, string brand, string deviceKey, string connectionString);

        /// <summary>
        /// 增量批量同步在线物理设备到数据库与内存池。
        /// </summary>
        Task<Result<int>> AddIncrementalPhysicalDevicesAsync(IEnumerable<DeviceInfo> onlineInfos);

        /// <summary>
        /// 更新设备逻辑名称/连接参数，并同步数据库与内存映射。
        /// </summary>
        Task<Result> UpdateDeviceMappingAsync(IDevice device, string newConnectionString = null);

        /// <summary>
        /// 从内存池和数据库中移除指定设备，并断开连接释放资源。
        /// </summary>
        bool RemoveDevice(string deviceKey);

        /// <summary>
        /// 获取池中所有已注册设备。
        /// </summary>
        IEnumerable<IDevice> GetAllDevices();

        /// <summary>
        /// 按逻辑 Key 获取设备实例。
        /// </summary>
        IDevice GetDevice(string deviceKey);

        /// <summary>
        /// 检查池中是否包含指定逻辑 Key 的设备。
        /// </summary>
        bool ContainsDevice(string deviceKey);

        /// <summary>
        /// 工位领用设备：将池中指定逻辑 Key 的设备注册到工位运行上下文中。
        /// </summary>
        bool LeaseDeviceToStation(string deviceKey, string stationId);

        /// <summary>
        /// 工位归还设备：解除设备与工位的绑定关系。
        /// </summary>
        bool ReturnDeviceFromStation(string deviceKey, string stationId);

        /// <summary>
        /// 获取指定工位当前已领用的所有设备逻辑 Key。
        /// </summary>
        IEnumerable<string> GetLeasedDeviceKeys(string stationId);

        /// <summary>
        /// 连接指定设备（调测/运行前调用）。
        /// </summary>
        Result ConnectDevice(string deviceKey);

        /// <summary>
        /// 断开指定设备连接。
        /// </summary>
        Result DisconnectDevice(string deviceKey);
    }
}
