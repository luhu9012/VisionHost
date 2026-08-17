using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Core.Devices;
using Grayson.Vision.Core.Station;
using System;


namespace Grayson.Vision.Core
{
    /// <summary>
    /// 工位级独立运行上下文
    /// 管理当前工位分配的硬件设备池 (相机、PLC、串口等逻辑映射)
    /// </summary>
    public class StationContext
    {
        public string StationId { get; }

        /// <summary>
        /// 工位全局共享数据管线
        /// 注意：业务节点只允许读取全局参数（标定矩阵、工位配置），禁止随意写入；写全局参数需要专门权限节点。
        /// </summary>
        public ExecutionContext GlobalEngineContext { get; } = new ExecutionContext();

        /// <summary>
        /// 工位全局参数服务：标定矩阵、工位参数，节点只读；写需要授权。
        /// </summary>
        public IStationParameterService StationParameters { get; }

        /// <summary>
        /// 工位设备管理器：统一负责设备注册、生命周期、租赁、占用与释放
        /// 节点不直接访问设备实例，应通过执行时注入的 IDeviceLease 使用设备。
        /// </summary>
        public IDeviceManager DeviceManager { get; }

        public StationContext(string stationId)
        {
            StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
            DeviceManager = new DeviceManager(StationId);
            StationParameters = new DefaultStationParameterService(StationId);
        }

        public StationContext(string stationId, IDeviceManager deviceManager, IStationParameterService parameterService = null)
        {
            StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
            DeviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            StationParameters = parameterService ?? new DefaultStationParameterService(stationId);
        }

        /// <summary>
        /// 注册/绑定逻辑硬件设备 (例如: 将逻辑名 "MainCam" 映射到具体的 Camera 驱动句柄)
        /// </summary>
        public void RegisterDevice(string logicalName, IDevice deviceInstance)
        {
            DeviceManager.RegisterDevice(logicalName, deviceInstance);
        }

        /// <summary>
        /// 硬件解析委托 API (由 NodeExecutionContext 调用)
        /// 兼容旧委托签名：仅返回租赁中或已注册设备的物理实例。
        /// 建议新节点通过执行上下文获取 IDeviceLease 以避免绕过资源管理。
        /// </summary>
        public object ResolveDevice(string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName)) return null;

            var states = DeviceManager.GetDeviceStates();
            if (!states.ContainsKey(logicalName)) return null;

            var leaseResult = DeviceManager.AcquireAsync(null, logicalName).GetAwaiter().GetResult();
            if (leaseResult?.Success == true && leaseResult.Data != null)
            {
                return leaseResult.Data.Device;
            }

            return null;
        }
    }
}
