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
using Grayson.Vision.Core.Processes;
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
        /// 自愈：确保工位业务过程已挂载。若曾被 FlowEdit 编辑器（RebindWorkerClientAsync）
        /// 对共享 worker 执行 DetachProcess 卸载，按工位配置声明的过程键重新装配。
        /// 由工位监视页启动/单次触发前调用；编辑器侧不调用（保持纯视觉链调试模式）。
        /// </summary>
        public void EnsureStationProcessAttached(string stationId)
        {
            if (string.IsNullOrWhiteSpace(stationId)) return;
            if (_workers.TryGetValue(stationId, out var worker))
            {
                worker.EnsureProcessAttached();
            }
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

            // 绑定触发事件 → 调用整周期执行
            source.OnTriggered += (s, e) =>
            {
                try
                {
                    LogBus.Debug("StationHostRuntime",
                        $"[{stationId}] 收到触发信号 ({e.SourceType})，执行一次完整周期");
                    var sw = Stopwatch.StartNew();
                    TriggerStationFullCycleAsync(stationId, e.BatchId).ContinueWith(t =>
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
                // 无触发源时直接走整周期路径（兼容未配置触发源的工位）
                LogBus.Info("StationHostRuntime",
                    $"[{stationId}] 无触发源，直接调用 TriggerStationFullCycleAsync");
                TriggerStationFullCycleAsync(stationId).ConfigureAwait(false);
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
        /// processKey + processConfigJson：可选挂载业务过程（见 IStationHostRuntime 说明）。
        /// </summary>
        public async Task<IWorkerClient> CreateStationWithRecipeAsync(
            string stationId,
            RecipeModel recipe,
            IEnumerable<Grayson.Vision.Contracts.Recipe.Models.RecipeDeviceMappingModel> deviceMappings = null,
            WorkMode mode = WorkMode.Production,
            TriggerSourceConfig triggerSourceConfig = null,
            string processKey = null,
            string processConfigJson = null,
            string taskTemplateCode = null)
        {
            var client = await CreateEmbeddedStationAsync(stationId);
            var worker = _workers[stationId];
            worker.TaskTemplateCode = taskTemplateCode; // 任务模板代码贯通（独立引擎读模板判据）

            var recipeMappings = (deviceMappings ?? recipe?.LogicalDevices)?.ToList();
            if (recipeMappings != null)
            {
                // ★ 逻辑设备 → 物理设备 对账表。
                //   目的：抓出「两个逻辑设备指向同一台物理设备」——这种配置下其中至少一条
                //   链会抓到错误的设备，而运行期日志只会回显逻辑名（"开始采集，逻辑相机: X"），
                //   看不出任何异常 ⇒ 必须在这里出声，不能静默注册。
                var logicalOwners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

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
                        // 不静默：绑定失败此前只写 Debug（Release 下完全不可见），运行期表现为
                        // 节点侧"未找到逻辑相机/设备"，此处补一条可检索的警告。
                        LogBus.Warn("StationHostRuntime",
                            $"[{stationId}] 绑定失败：逻辑设备 [{logicalDeviceKey}] 指向的物理设备 [{mappedDeviceKey}] 不在设备池中（请检查设备是否已被移除）。");
                        continue;
                    }

                    bool leasedToMe = DevicePool.GetLeasedDeviceKeys(stationId).Any(k => k == mappedDeviceKey);
                    if (!leasedToMe)
                    {
                        if (!DevicePool.LeaseDeviceToStation(mappedDeviceKey, stationId))
                        {
                            LogBus.Warn("StationHostRuntime",
                                $"[{stationId}] 领用设备 [{mappedDeviceKey}] 失败：已被其他工位占用（逻辑设备 [{logicalDeviceKey}] 本次未绑定）。");
                            continue;
                        }
                    }

                    var device = DevicePool.GetDevice(mappedDeviceKey);
                    if (device != null)
                    {
                        // ★★ 硬拦：同一台物理设备已被本工位另一条逻辑设备占用时【拒绝注册】。
                        //   两个逻辑名共用一个物理实例 ⇒ 两条链操作的是同一台硬件，其中一条必然
                        //   "看起来完全正常、但用的是别人的设备"（本例：配"下固定相机"却出上相机画面）。
                        //   拒绝注册会让该逻辑设备在节点侧报"未注册"，错误立刻可见、可修；静默注册则相反。
                        if (logicalOwners.TryGetValue(mappedDeviceKey, out var priorOwners) && priorOwners.Count > 0)
                        {
                            priorOwners.Add(logicalDeviceKey);
                            LogBus.Error("StationHostRuntime",
                                $"[{stationId}] ⛔ 拒绝注册逻辑设备 [{logicalDeviceKey}]：物理设备 [{mappedDeviceKey}] 已被本工位逻辑设备 [{priorOwners[0]}] 占用。" +
                                $"请到【工位管理 → 逻辑设备映射】把两条逻辑设备改绑到各自的物理设备后重新装配本工位。");
                            continue;
                        }

                        worker.Context.RegisterDevice(logicalDeviceKey, device);

                        // ★ 物理身份对账：逻辑名只是"别名"，真正决定抓哪个硬件的是这一行。
                        LogBus.Info("StationHostRuntime",
                            $"[{stationId}] 逻辑设备 [{logicalDeviceKey}] → 物理 [{mappedDeviceKey}] (设备键: {device.DeviceKey ?? "-"} | SN/物理ID: {device.DeviceId ?? "-"})");

                        logicalOwners[mappedDeviceKey] = new List<string> { logicalDeviceKey };
                    }
                }

                foreach (var kv in logicalOwners)
                {
                    if (kv.Value.Count <= 1) continue;

                    LogBus.Error("StationHostRuntime",
                        $"[{stationId}] ⛔ 设备映射冲突：逻辑设备 [{string.Join(" / ", kv.Value)}] 同时指向同一台物理设备 [{kv.Key}]。" +
                        $"这会让其中至少一条链静默操作错误的硬件（日志里的『逻辑相机/逻辑设备』只是回显，看不出异常）。" +
                        $"请到【工位管理 → 逻辑设备映射】把每个逻辑设备改绑到各自的物理设备后保存。");
                }
            }

            worker.Mode = mode;
            if (recipe?.MainProcess != null)
            {
                await worker.LoadRecipeAsync(recipe.MainProcess);
            }

            // 🌟 装配业务过程（工位配置声明了 ProcessKey 时挂载：
            //    之后所有触发入口自动执行完整业务周期）
            if (!string.IsNullOrWhiteSpace(processKey))
            {
                // 记录配置声明（供 EnsureStationProcessAttached 自愈重挂：
                // FlowEdit 编辑器调试会 DetachProcess 卸载共享 worker 的业务过程）
                worker.DesiredProcessKey = processKey;
                worker.DesiredProcessConfigJson = processConfigJson;

                var process = StationProcessFactory.Create(processKey, processConfigJson, worker);
                if (process != null)
                {
                    worker.AttachProcess(process);
                }
                else
                {
                    LogBus.Warn("StationHostRuntime",
                        $"[{stationId}] 业务过程 [{processKey}] 装配失败（未注册或实例化异常），工位回退为纯视觉链模式。");
                }
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

        /// <summary>
        /// 触发源驱动的一次完整周期执行（生产节拍语义，2026-09-09 修正）：
        /// - 工位挂载业务过程 → TriggerOnceAsync（完整业务周期：走位/拍照/吸放/判定，
        ///   过程内部用 RunContinuousAsync 直连调度器跑完整视觉段）；
        /// - 纯视觉链工位（未挂业务过程）→ RunContinuousAsync（一次完整视觉链，而非单节点步进）。
        /// 此前触发源一律走 TriggerOnceAsync：有过程时正确，但纯视觉链工位会退化成
        /// "每个触发信号只执行 1 个节点"，节拍与整链任务对不上（每拍采一张图只跑一步）。
        /// </summary>
        private async Task TriggerStationFullCycleAsync(string stationId, string batchId = null)
        {
            if (_workers.TryGetValue(stationId, out var worker))
            {
                if (worker.Process != null)
                {
                    await worker.TriggerOnceAsync(batchId).ConfigureAwait(false);
                }
                else
                {
                    await worker.RunContinuousAsync(batchId).ConfigureAwait(false);
                }
                return;
            }

            if (_clients.TryGetValue(stationId, out var client))
            {
                await client.TriggerOnceAsync(batchId).ConfigureAwait(false);
                return;
            }

            LogBus.Warn("StationHostRuntime", $"[{stationId}] 触发执行失败：找不到工位 Worker/Client。");
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

        /// <summary>
        /// 已注册的全部业务过程键（UI 下拉数据源）。
        /// </summary>
        public IEnumerable<string> GetSupportedProcessKeys()
        {
            return StationProcessFactory.GetSupportedKeys();
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
