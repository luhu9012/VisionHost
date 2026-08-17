using Grayson.Vision.Core.Client.Proxy;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Station.WorkOrderTracking;
using Grayson.Vision.Core.Devices;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Station
{
    /// <summary>
    /// StationHost 运行时根。
    /// 同进程内多线程运行的核心入口，统一持有全局设备池与所有工位 Worker 实例。
    /// 既可在 WpfUI/FlowEdit 中作为 Embedded 宿主复用，也可被 WorkerHost 独立进程挂载。
    /// </summary>
    public class StationHostRuntime : IStationHostRuntime
    {
        private readonly ConcurrentDictionary<string, StationWorker> _workers = new ConcurrentDictionary<string, StationWorker>();
        private readonly ConcurrentDictionary<string, IWorkerClient> _clients = new ConcurrentDictionary<string, IWorkerClient>();
        private readonly object _initLock = new object();
        private bool _isInitialized;

        /// <summary>
        /// 进程内共享的 StationHostRuntime 单例引用。WpfUI 入口（App.xaml.cs）负责设置。
        /// </summary>
        public static StationHostRuntime GlobalInstance { get; set; }

        /// <summary>
        /// 安全联锁服务（允许为空）。
        /// </summary>
        public ISafetyInterlockService SafetyInterlock { get; private set; }

        /// <summary>
        /// 注册安全联锁服务。
        /// </summary>
        public void RegisterSafetyInterlock(ISafetyInterlockService safetyInterlockService)
        {
            SafetyInterlock = safetyInterlockService;
        }

        /// <summary>
        /// 全局设备池。
        /// </summary>
        public IDevicePool DevicePool { get; }

        /// <summary>
        /// 默认构造：内部自动创建 DevicePool。
        /// </summary>
        public StationHostRuntime()
        {
            DevicePool = new DevicePool();
        }

        /// <summary>
        /// 外部注入设备池（用于测试或自定义实现）。
        /// </summary>
        public StationHostRuntime(IDevicePool devicePool)
        {
            DevicePool = devicePool ?? throw new ArgumentNullException(nameof(devicePool));
        }

        /// <summary>
        /// 初始化：加载插件、恢复设备池。
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await DevicePool.InitializeAsync();

            lock (_initLock)
            {
                if (_isInitialized) return;
                _isInitialized = true;
            }
        }

        /// <summary>
        /// 创建嵌入式工位并返回客户端代理。
        /// </summary>
        public async Task<IWorkerClient> CreateEmbeddedStationAsync(string stationId)
        {
            if (_clients.TryGetValue(stationId, out var existingClient))
                return existingClient;

            var worker = new StationWorker(stationId);
            _workers[stationId] = worker;

            // 把已分配给该工位的设备注册到 StationContext
            var leasedKeys = DevicePool.GetLeasedDeviceKeys(stationId).ToList();
            foreach (var key in leasedKeys)
            {
                var device = DevicePool.GetDevice(key);
                if (device != null)
                    worker.Context.RegisterDevice(key, device);
            }

            var client = new EmbeddedWorkerClientProxy(worker);
            await client.ConnectAsync();

            _clients[stationId] = client;
            return client;
        }

        /// <summary>
        /// 创建工位并加载配方、绑定设备映射。
        /// 这是 UI 配置工位后的标准入口。
        /// </summary>
        public async Task<IWorkerClient> CreateStationWithRecipeAsync(
            string stationId,
            RecipeModel recipe,
            IEnumerable<Grayson.Vision.Contracts.Recipe.Models.RecipeDeviceMappingModel> deviceMappings = null,
            WorkMode mode = WorkMode.Production)
        {
            var client = await CreateEmbeddedStationAsync(stationId);
            var worker = _workers[stationId];

            if (deviceMappings != null)
            {
                // TODO: Phase B - RecipeDeviceMappingModel 等字段对齐后，恢复此逻辑
                // 临时注释以保证编译通过
                /*
                foreach (var mapping in deviceMappings)
                {
                    // 先尝试领用设备
                    if (!string.IsNullOrEmpty(mapping.MappedDeviceKey))
                    {
                        if (!DevicePool.ContainsDevice(mapping.MappedDeviceKey))
                        {
                            System.Diagnostics.Debug.WriteLine($"[StationHost] 工位 [{stationId}] 绑定失败：设备池不存在 [{mapping.MappedDeviceKey}]");
                            continue;
                        }

                        bool leasedToMe = DevicePool.GetLeasedDeviceKeys(stationId).Any(k => k == mapping.MappedDeviceKey);
                        if (!leasedToMe)
                        {
                            if (!DevicePool.LeaseDeviceToStation(mapping.MappedDeviceKey, stationId))
                            {
                                System.Diagnostics.Debug.WriteLine($"[StationHost] 工位 [{stationId}] 领用设备 [{mapping.MappedDeviceKey}] 失败：已被其他工位占用");
                                continue;
                            }
                        }

                        var device = DevicePool.GetDevice(mapping.MappedDeviceKey);
                        if (device != null)
                            worker.Context.RegisterDevice(mapping.LogicalDeviceId, device);
                    }
                }
                */
            }

            worker.Mode = mode;
            if (recipe?.MainProcess != null)
            {
                await worker.LoadRecipeAsync(recipe.MainProcess);
            }

            return client;
        }

        public IWorkerClient GetStationClient(string stationId)
        {
            return _clients.TryGetValue(stationId, out var client) ? client : null;
        }

        public StationWorker GetStationWorker(string stationId)
        {
            return _workers.TryGetValue(stationId, out var worker) ? worker : null;
        }

        public bool RemoveStation(string stationId)
        {
            if (_clients.TryRemove(stationId, out var client))
            {
                client.Dispose();
            }

            if (_workers.TryRemove(stationId, out var worker))
            {
                // 归还该工位领用的全部设备
                var leasedKeys = DevicePool.GetLeasedDeviceKeys(stationId).ToList();
                foreach (var key in leasedKeys)
                {
                    DevicePool.ReturnDeviceFromStation(key, stationId);
                }

                worker.Dispose();
                return true;
            }

            return false;
        }

        public IEnumerable<string> GetStationIds() => _clients.Keys.ToArray();

        public IWorkOrderTracker GetWorkOrderTracker(string stationId)
        {
            return _workers.TryGetValue(stationId, out var worker) ? worker.WorkOrderTracker : null;
        }

        public void Dispose()
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
            _clients.Clear();

            foreach (var worker in _workers.Values)
            {
                worker.Dispose();
            }
            _workers.Clear();

            DevicePool?.Dispose();
        }
    }
}
