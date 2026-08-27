using Grayson.Vision.Core.Client.Proxy;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Station.Triggers;
using Grayson.Vision.Contracts.Station.WorkOrderTracking;
using Grayson.Vision.Core.Devices;
using Grayson.Vision.Core.Triggers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly ConcurrentDictionary<string, ITriggerSource> _triggerSources = new ConcurrentDictionary<string, ITriggerSource>();
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
            worker.HostRuntime = this; // 设置宿主引用，使 Start/Stop 能联动触发源
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

        public IWorkerClient GetStationClient(string stationId)
        {
            return _clients.TryGetValue(stationId, out var client) ? client : null;
        }

        /// <summary>
        /// 获取指定工位的触发源实例（供 UI 读取节拍统计/手动触发测试）。
        /// </summary>
        public ITriggerSource GetTriggerSource(string stationId)
        {
            return _triggerSources.TryGetValue(stationId, out var source) ? source : null;
        }

        /// <summary>
        /// 为工位配置并装配触发源（在工位创建后、启动前调用）。
        /// 若工位已有触发源，先停止并替换。
        /// </summary>
        public void SetupTriggerSource(string stationId, TriggerSourceConfig config)
        {
            if (string.IsNullOrEmpty(stationId)) return;

            // 先清理旧的
            if (_triggerSources.TryRemove(stationId, out var oldSource))
            {
                try { oldSource.Stop(); oldSource.Dispose(); }
                catch (Exception ex)
                {
                    LogBus.Info("StationHostRuntime",
                        $"[{stationId}] 清理旧触发源异常: {ex.Message}");
                }
            }

            var source = TriggerSourceFactory.Create(stationId, config, DevicePool);
            _triggerSources[stationId] = source;

            // 绑定触发事件 → 调用 TriggerStationAsync
            source.OnTriggered += (s, e) =>
            {
                try
                {
                    LogBus.Debug("StationHostRuntime",
                        $"[{stationId}] 收到触发信号 ({e.SourceType})，执行 TriggerOnceAsync");
                    var sw = Stopwatch.StartNew();
                    TriggerStationAsync(stationId, e.BatchId).ContinueWith(t =>
                    {
                        sw.Stop();
                        // 通知触发源执行完成（恢复可接受新触发状态 + 记录节拍）
                        if (s is TriggerSourceBase baseSource)
                        {
                            baseSource.MarkExecutionComplete(sw.Elapsed.TotalMilliseconds);
                        }
                    }, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    LogBus.Error("StationHostRuntime",
                        $"[{stationId}] 触发源→执行回调异常: {ex.Message}", ex);
                }
            };

            source.OnError += (s, e) =>
            {
                LogBus.Warn("StationHostRuntime",
                    $"[{stationId}] 触发源异常 ({e.Source}): {e.Error?.Message} [可恢复={e.IsRecoverable}]");
            };

            LogBus.Info("StationHostRuntime",
                $"[{stationId}] 触发源已装配: {source.SourceType}");
        }

        /// <summary>
        /// 启动指定工位的触发源（工位 StartAsync 后调用）。
        /// </summary>
        public void StartTriggerSource(string stationId)
        {
            if (_triggerSources.TryGetValue(stationId, out var source))
            {
                source.Start();
            }
        }

        /// <summary>
        /// 停止指定工位的触发源（工位 StopAsync 后调用）。
        /// </summary>
        public void StopTriggerSource(string stationId)
        {
            if (_triggerSources.TryGetValue(stationId, out var source))
            {
                source.Stop();
            }
        }

        /// <summary>
        /// 手动触发指定工位（Manual 源的正常路径 / 其他源的"测试触发"）。
        /// </summary>
        public void ManualTrigger(string stationId)
        {
            if (_triggerSources.TryGetValue(stationId, out var source))
            {
                source.ManualTrigger();
            }
            else
            {
                // 无触发源时直接走旧路径（兼容未配置触发源的工位）
                LogBus.Info("StationHostRuntime",
                    $"[{stationId}] 无触发源，直接调用 TriggerStationAsync");
                TriggerStationAsync(stationId).ConfigureAwait(false);
            }
        }

        public bool RemoveStation(string stationId)
        {
            // 先停止并释放触发源
            if (_triggerSources.TryRemove(stationId, out var triggerSource))
            {
                try { triggerSource.Stop(); triggerSource.Dispose(); }
                catch (Exception ex)
                {
                    LogBus.Info("StationHostRuntime",
                        $"[{stationId}] 释放触发源异常: {ex.Message}");
                }
            }

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

        /// <summary>
        /// 创建工位并加载配方、绑定设备映射，装配触发源。
        /// 这是 UI 配置工位后的标准入口。
        /// </summary>
        public async Task<IWorkerClient> CreateStationWithRecipeAsync(
            string stationId,
            RecipeModel recipe,
            IEnumerable<Grayson.Vision.Contracts.Recipe.Models.RecipeDeviceMappingModel> deviceMappings = null,
            WorkMode mode = WorkMode.Production,
            TriggerSourceConfig triggerSourceConfig = null)
        {
            var client = await CreateEmbeddedStationAsync(stationId);
            var worker = _workers[stationId];

            var recipeMappings = (deviceMappings ?? recipe?.LogicalDevices)?.ToList();
            if (recipeMappings != null)
            {
                foreach (var mapping in recipeMappings)
                {
                    if (mapping == null) continue;

                    var mappedDeviceKey = mapping.MappedDeviceId;
                    var logicalDeviceKey = !string.IsNullOrWhiteSpace(mapping.LogicalDeviceId)
                        ? mapping.LogicalDeviceId
                        : mapping.LogicalDeviceName;

                    if (string.IsNullOrWhiteSpace(mappedDeviceKey) || string.IsNullOrWhiteSpace(logicalDeviceKey))
                        continue;

                    if (!DevicePool.ContainsDevice(mappedDeviceKey))
                    {
                        Debug.WriteLine($"[StationHost] 工位 [{stationId}] 绑定失败：设备池不存在 [{mappedDeviceKey}]");
                        continue;
                    }

                    bool leasedToMe = DevicePool.GetLeasedDeviceKeys(stationId).Any(k => k == mappedDeviceKey);
                    if (!leasedToMe)
                    {
                        if (!DevicePool.LeaseDeviceToStation(mappedDeviceKey, stationId))
                        {
                            Debug.WriteLine($"[StationHost] 工位 [{stationId}] 领用设备 [{mappedDeviceKey}] 失败：已被其他工位占用");
                            continue;
                        }
                    }

                    var device = DevicePool.GetDevice(mappedDeviceKey);
                    if (device != null)
                    {
                        worker.Context.RegisterDevice(logicalDeviceKey, device);
                    }
                }
            }

            worker.Mode = mode;
            if (recipe?.MainProcess != null)
            {
                await worker.LoadRecipeAsync(recipe.MainProcess);
            }

            // 装配触发源（配置为 null 时默认 Manual，与旧行为兼容）
            SetupTriggerSource(stationId, triggerSourceConfig ?? new TriggerSourceConfig());

            return client;
        }

        /// <summary>
        /// 外部触发指定工位执行一次（单帧/单物料）。
        /// 生产模式下由 PLC/IO 输入、MES 或 UI 手动触发信号的统一入口。
        /// 注意：如果工位已装配触发源，通常由触发源自动调用此方法；
        /// 手动触发建议走 ManualTrigger(stationId) → 触发源 → 本方法。
        /// </summary>
        public async Task<bool> TriggerStationAsync(string stationId, string batchId = null)
        {
            if (_clients.TryGetValue(stationId, out var client))
            {
                await client.TriggerOnceAsync(batchId).ConfigureAwait(false);
                return true;
            }

            if (_workers.TryGetValue(stationId, out var worker))
            {
                await worker.TriggerOnceAsync(batchId).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        public StationWorker GetStationWorker(string stationId)
        {
            return _workers.TryGetValue(stationId, out var worker) ? worker : null;
        }

        public IEnumerable<string> GetStationIds() => _clients.Keys.ToArray();

        public IWorkOrderTracker GetWorkOrderTracker(string stationId)
        {
            return _workers.TryGetValue(stationId, out var worker) ? worker.WorkOrderTracker : null;
        }

        public void Dispose()
        {
            // 先停止并释放所有触发源
            foreach (var source in _triggerSources.Values)
            {
                try { source.Stop(); source.Dispose(); }
                catch (Exception ex)
                {
                    LogBus.Info("StationHostRuntime",
                        $"释放触发源异常: {ex.Message}");
                }
            }
            _triggerSources.Clear();

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
