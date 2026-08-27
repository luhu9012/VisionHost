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

        /// <summary>
        /// 节点实时预览显示上下文（编辑器属性面板调试期由宿主经 IWorkerClient.SetPreviewContext 注入）。
        /// 调度器在构建调试单步的 NodeExecutionContext 时读取此值赋给 Preview；
        /// 生产运行恒为 null，节点内所有预览调用判空跳过，行为零变化。
        /// </summary>
        public IFlowPreviewContext PreviewContext { get; set; }

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
        /// 按逻辑设备 Key 返回已注册的设备实例，不在解析阶段触发租赁。
        /// </summary>
        public object ResolveDevice(string logicalName)
        {
            if (string.IsNullOrWhiteSpace(logicalName)) return null;

            return DeviceManager.GetDeviceInstance(logicalName);
        }
    }
}
