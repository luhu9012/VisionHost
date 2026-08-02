using Grayson.Vision.Contracts.Business.Engine.Execution;
using System;
using System.Collections.Concurrent;


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
        /// </summary>
        public ExecutionContext GlobalEngineContext { get; } = new ExecutionContext();

        /// <summary>
        /// 逻辑设备名称 -> 物理硬件驱动实例映射字典
        /// </summary>
        private readonly ConcurrentDictionary<string, object> _hardwareDeviceMap
            = new ConcurrentDictionary<string, object>();

        public StationContext(string stationId)
        {
            StationId = stationId;
        }

        /// <summary>
        /// 注册/绑定逻辑硬件设备 (例如: 将逻辑名 "MainCam" 映射到具体的 Camera 驱动句柄)
        /// </summary>
        public void RegisterDevice(string logicalName, object deviceInstance)
        {
            if (string.IsNullOrEmpty(logicalName) || deviceInstance == null) return;
            _hardwareDeviceMap[logicalName] = deviceInstance;
        }

        /// <summary>
        /// 硬件解析委托 API (由 NodeExecutionContext 调用)
        /// </summary>
        public object ResolveDevice(string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName)) return null;
            _hardwareDeviceMap.TryGetValue(logicalName, out var device);
            return device;
        }
    }
}