using Grayson.Vision.Contracts.Core;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Devices.Interfaces
{
    /// <summary>
    /// 设备池服务契约（Core 层实现，UI 层只读/委托调用）。
    /// 负责插件加载、设备扫描、注册、持久化等硬件管理核心逻辑。
    /// </summary>
    public interface IDevicePoolService
    {
        Task InitializeAsync();

        IReadOnlyCollection<IHardwarePlugin> GetAllPlugins();

        IReadOnlyCollection<IDevice> GetAllDevices();

        IDevice GetDevice(string deviceKey);

        TDevice GetDevice<TDevice>(string deviceKey) where TDevice : class, IDevice;

        Result RemoveDevice(string deviceKey);

        Task<Result> ScanAndRegisterDevicesAsync();

        event System.EventHandler<DeviceStateChangedEventArgs> DeviceStateChanged;
    }
}
