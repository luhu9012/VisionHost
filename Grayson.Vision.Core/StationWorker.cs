using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Processes;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Infrastructure.Metrics;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Core.Scheduling;
using Grayson.Vision.Core.Station;
using Grayson.Vision.Core.Processes;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core
{
    /// <summary>
    /// 工位核心 Worker (遵循 PackML 工业状态机)
    /// </summary>
    public class StationWorker : IStationWorkerHost, IStationWorkerEvents, IDisposable
    {
        public string StationId { get; }

        /// <summary>
        /// 工位当前状态
        /// </summary>
        public StationState State { get; private set; } = StationState.Stopped;

        public WorkMode Mode
        {
            get => _scheduler?.Mode ?? WorkMode.Production;
            set
            {
                if (_scheduler != null) _scheduler.Mode = value;
            }
        }

        private readonly StationContext _stationContext;
        private IWorkflowScheduler _scheduler;
        private FlowProcessModel _currentRecipe;

        /// <summary>
        /// 宿主运行时引用（由 StationHostRuntime 在创建时设置），
        /// 用于工位 Start/Stop 时联动启停触发源。
        /// </summary>
        public StationHostRuntime HostRuntime { get; set; }

        public ExecutionChain ActiveExecutionChain { get; private set; }

        /// <summary>工位指标聚合</summary>
        public StationMetrics Metrics { get; }

        /// <summary>最近工单追踪</summary>
        public WorkOrderTracker WorkOrderTracker { get; }

        #region 业务过程挂载（配置驱动：机器结构级时序，不随产品变化）

        /// <summary>
        /// 当前工位挂载的业务过程；null = 未挂载（纯视觉链/编辑器调试模式）。
        /// 挂载后 TriggerOnceAsync 的所有入口（UI 按钮、PLC/IO 触发源、手动触发）
        /// 自动跑完整业务周期；过程内部用 <see cref="RunVisionChainAsync"/> 直连调度器跑视觉段。
        /// </summary>
        public IStationProcess Process { get; private set; }

        /// <summary>
        /// 工位配置声明的业务过程键（CreateStationWithRecipeAsync 装配时记录）。
        /// FlowEdit 编辑器调试时会对共享 worker 执行 DetachProcess 卸载业务过程，
        /// 生产侧（工位监视页）触发前用其自愈重挂，保证配置声明的过程不因调试操作永久丢失。
        /// </summary>
        public string DesiredProcessKey { get; set; }

        /// <summary>工位配置声明的业务过程参数 JSON（原样回传给工厂重挂）。</summary>
        public string DesiredProcessConfigJson { get; set; }

        /// <summary>已挂载业务过程的键（未挂载为 null），供 UI 提示使用。</summary>
        public string ProcessKey => Process?.ProcessKey;

        /// <summary>最近一次执行链的运行结果（Success / Failed / Canceled / StepEndReached），随链完成事件更新。</summary>
        public ChainExecutionResult LastChainResult { get; private set; } = ChainExecutionResult.Success;

        /// <summary>最近一次执行链失败的异常消息（成功时为 null），供业务过程诊断日志使用。</summary>
        public string LastChainError { get; private set; }

        /// <summary>过程当前周期的取消源（Stop/急停时取消正在运行的周期）。</summary>
        private CancellationTokenSource _processCts;

        /// <summary>过程触发互斥锁：防止 PLC 高频信号并发跑多个周期导致撞机。</summary>
        private readonly SemaphoreSlim _processExecLock = new SemaphoreSlim(1, 1);

        /// <summary>过程级暂停标志（与调度器 _isPaused 独立，暂停期间视觉链照跑，仅拒绝新周期）。</summary>
        private bool _processPaused;

        /// <summary>
        /// 挂载业务过程（由 StationHostRuntime 创建工位时按配置装配）。
        /// </summary>
        public void AttachProcess(IStationProcess process)
        {
            if (process == null) throw new ArgumentNullException(nameof(process));
            Process = process;
            _processPaused = false;
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已挂载业务过程 [{process.ProcessKey}]，触发入口将执行完整业务周期。");
        }

        /// <summary>
        /// 卸载业务过程（编辑器调试模式专用：恢复为纯视觉链单次触发）。
        /// </summary>
        public void DetachProcess()
        {
            if (Process == null) return;
            var key = Process.ProcessKey;
            _processPaused = false;
            _processCts?.Cancel();
            _processCts?.Dispose();
            _processCts = null;
            Process = null;
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已卸载业务过程 [{key}]，恢复视觉链调试模式。");
        }

        /// <summary>
        /// 自愈重挂业务过程：Process 为空但配置声明了过程键时，按配置重新装配。
        /// 由工位监视页启动/触发前调用（FlowEdit 编辑器不调用，保持纯视觉链调试模式）。
        /// 修复场景：FlowEdit 打开/切换工位时 RebindWorkerClientAsync 会对【共享 worker 实例】
        /// 执行 DetachProcess，导致工位监视页的业务过程被永久卸载（点启动只剩纯视觉链）。
        /// </summary>
        public void EnsureProcessAttached()
        {
            if (Process != null) return;
            if (string.IsNullOrWhiteSpace(DesiredProcessKey)) return;

            try
            {
                var process = StationProcessFactory.Create(DesiredProcessKey, DesiredProcessConfigJson, this);
                if (process != null)
                {
                    AttachProcess(process);
                    LogBus.Warn("StationWorker",
                        $"工位 [{StationId}] 检测到业务过程已被卸载（可能被 FlowEdit 调试卸载），已按配置自动重新挂载 [{process.ProcessKey}]。");
                }
                else
                {
                    LogBus.Warn("StationWorker",
                        $"工位 [{StationId}] 按配置 [{DesiredProcessKey}] 自愈挂载失败（工厂返回 null），仍为纯视觉链模式。");
                }
            }
            catch (Exception ex)
            {
                LogBus.Error("StationWorker", $"工位 [{StationId}] 按配置自愈挂载业务过程失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 业务过程内部触发视觉链（直连调度器，避免再次进入 TriggerOnceAsync 的
        /// 过程分流造成"过程 → 过程"递归；与编辑器/监视页同一链路）。
        /// ⚠ 必须走【运行】RunContinuousAsync（完整链）：业务过程 Phase1 需要一次拿到
        /// 采图→匹配→标定的完整结果；若走单步，每次只执行 1 个节点，匹配分数恒为默认 0。
        /// </summary>
        internal Task RunVisionChainAsync(string batchId = null)
        {
            return _scheduler?.RunContinuousAsync(batchId) ?? Task.CompletedTask;
        }

        #endregion

        #region 事件定义
        public event EventHandler<StationState> OnStateChanged;
        public event EventHandler<ImageRenderEventArgs> OnFrameRendered;
        public event EventHandler<NodeEventArgs> OnNodeExecuting;
        public event EventHandler<NodeEventArgs> OnNodeExecuted;
        public event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError;
        public event EventHandler<ChainCompletedEventArgs> OnExecutionCompleted;
        public event EventHandler<string> OnLogReceived;
        #endregion

        public StationWorker(string stationId)
        {
            StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
            _stationContext = new StationContext(stationId);
            Metrics = new StationMetrics(stationId);
            WorkOrderTracker = new WorkOrderTracker();
            CreateDefaultScheduler();

            var engineContext = _stationContext.GlobalEngineContext;
            if (engineContext != null)
            {
                engineContext.OnNodeExecuting += Engine_OnNodeExecuting;
                engineContext.OnNodeExecuted += Engine_OnNodeExecuted;
                engineContext.OnExecutionError += Engine_OnExecutionError;
            }
        }

        public StationWorker(string stationId, IWorkflowScheduler scheduler)
        {
            StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
            _stationContext = new StationContext(stationId);
            Metrics = new StationMetrics(stationId);
            WorkOrderTracker = new WorkOrderTracker();
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            BindSchedulerEvents();
        }

        /// <summary>
        /// 当前工位的 StationContext（设备、参数、全局数据总线）。
        /// </summary>
        public StationContext Context => _stationContext;

        private void CreateDefaultScheduler()
        {
            _scheduler = new SimpleTriggerScheduler(StationId, _stationContext, WorkOrderTracker);
            BindSchedulerEvents();
        }

        private void BindSchedulerEvents()
        {
            if (_scheduler == null) return;
            _scheduler.OnExecutionCompleted += Scheduler_OnExecutionCompleted;
            _scheduler.OnNodeExecuting += (s, e) => OnNodeExecuting?.Invoke(this, e);
            _scheduler.OnNodeExecuted += (s, e) => OnNodeExecuted?.Invoke(this, e);
            _scheduler.OnExecutionError += (s, e) => OnExecutionError?.Invoke(this, e);
        }

        private void UnbindSchedulerEvents()
        {
            if (_scheduler == null) return;
            _scheduler.OnExecutionCompleted -= Scheduler_OnExecutionCompleted;
        }

        /// <summary>
        /// 加载配方并初始化基于 ExecutionChain 的执行器
        /// 🛡️ 工业级规范：不强抛致命异常，通过返回 Task&lt;bool&gt; 告知上层加载状态，确保 UI 能够正常启动
        /// </summary>
        public async Task LoadRecipeAsync(FlowProcessModel recipe)
        {
            // 如果调度器已运行，先停止再换链
            if (_scheduler?.IsRunning == true)
            {
                await _scheduler.StopAsync().ConfigureAwait(false);
            }

            if (recipe == null)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 传入配方为空，回退为空白执行链。");
                ActiveExecutionChain = new ExecutionChain();
                await _scheduler.LoadExecutionChainAsync(ActiveExecutionChain).ConfigureAwait(false);
                UpdateState(StationState.Stopped);
                return;
            }

            _currentRecipe = recipe;

            var buildResult = ExecutionChain.BuildAndValidate(_currentRecipe);
            if (!buildResult.IsSuccess)
            {
                LogBus.Error("StationWorker", $"工位 [{StationId}] 加载配方 [{_currentRecipe.ProcessName}] 校验未通过：{buildResult.ErrorMessage}");
                ActiveExecutionChain = buildResult.Chain ?? new ExecutionChain();
                await _scheduler.LoadExecutionChainAsync(ActiveExecutionChain).ConfigureAwait(false);
                UpdateState(StationState.Faulted);
                return;
            }

            ActiveExecutionChain = buildResult.Chain;
            await _scheduler.LoadExecutionChainAsync(ActiveExecutionChain).ConfigureAwait(false);

            UpdateState(StationState.Idle);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 成功加载配方并就绪执行链: {_currentRecipe.ProcessName}");
        }

        /// <summary>
        /// 处理调度器传上来的完成事件并继续抛给 Worker 订阅者
        /// </summary>
        private void Scheduler_OnExecutionCompleted(object sender, ChainCompletedEventArgs e)
        {
            LogBus.Info("StationWorker", $"工位 [{StationId}] 执行链结束，执行结果: {e.Result}");

            // 🌟 记录最近一次链执行结果，供业务过程在视觉段结束后核对：
            //    设备级失败（相机打开失败/节点异常）必须与"视觉 NG（匹配分数不足）"分流——
            //    前者是设备故障（红灯+蜂鸣持续等待复位），后者是业务 NG（提示后自动回待机）。
            LastChainResult = e.Result;
            LastChainError = e.Exception?.Message;

            bool isOk = e.Result == ChainExecutionResult.Success || e.Result == ChainExecutionResult.StepEndReached;
            Metrics.RecordWorkOrderCompleted(isOk);

            switch (e.Result)
            {
                case ChainExecutionResult.Success:
                    UpdateState(StationState.Idle);
                    break;

                case ChainExecutionResult.Failed:
                    UpdateState(StationState.Faulted);
                    break;

                case ChainExecutionResult.Canceled:
                    UpdateState(ActiveExecutionChain?.Count > 0 ? StationState.Idle : StationState.Stopped);
                    break;

                case ChainExecutionResult.StepEndReached:
                    UpdateState(StationState.Idle);
                    break;
            }

            OnExecutionCompleted?.Invoke(this, e);
        }

        public async Task StartAsync()
        {
            // 自愈：若业务过程曾被 FlowEdit 调试 DetachProcess 卸载，启动前按配置重挂，
            // 恢复生产模式完整业务周期（纯视觉链只属于编辑器调试场景）。
            EnsureProcessAttached();

            if (_scheduler == null || ActiveExecutionChain == null || ActiveExecutionChain.Count == 0)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 缺乏有效执行链，无法启动。");
                return;
            }

            if (State == StationState.Running) return;

            UpdateState(StationState.Running);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已启动 (模式: {Mode})。");

            // 🌟 启动触发源——生产模式下工位将自动被外部信号（PLC/IO/定时器）驱动，
            //    不再空转等待；Manual 源则等待 UI 按钮触发。
            try
            {
                HostRuntime?.StartTriggerSource(StationId);
            }
            catch (Exception ex)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 启动触发源异常: {ex.Message}");
            }

            await _scheduler.StartAsync().ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            // 🌟 先停止触发源——不再响应外部信号
            try
            {
                HostRuntime?.StopTriggerSource(StationId);
            }
            catch (Exception ex)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 停止触发源异常: {ex.Message}");
            }

            if (Process != null)
            {
                // 过程模式：取消当前周期 + 安全收尾（抬 Z / 关真空），不拆调度器
                try { _processCts?.Cancel(); }
                catch (ObjectDisposedException) { }
                try
                {
                    // 普通停止 = 安全收尾语义：允许收尾运动（抬 Z）完整跑完，
                    // 因此不能用已取消的 token（MoveAbs 下发后等位会立即抛 OCE，抬 Z 半途而废）。
                    await Process.SafeStopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogBus.Warn("StationWorker", $"工位 [{StationId}] 业务过程安全收尾异常: {ex.Message}");
                }
                _processCts?.Dispose();
                _processCts = null;

                UpdateState(ActiveExecutionChain?.Count > 0 ? StationState.Idle : StationState.Stopped);
                LogBus.Info("StationWorker", $"工位 [{StationId}] 已停止（业务过程安全收尾完成）。");
                return;
            }

            await _scheduler.StopAsync().ConfigureAwait(false);

            UpdateState(ActiveExecutionChain?.Count > 0 ? StationState.Idle : StationState.Stopped);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已停止。");
        }

        /// <summary>
        /// 暂停工位。
        /// - 未挂业务过程：挂起调度器（不响应新触发），保留执行链；
        /// - 已挂业务过程：停止触发源 + 置过程暂停标志，**当前周期安全跑完、不再接新周期**。
        ///   注意不能走 scheduler 的 Pause——过程内部要用调度器跑视觉段，scheduler 的
        ///   _isPaused 会静默拒绝视觉触发，导致运动时序断裂（撞机风险）。
        /// </summary>
        public async Task PauseAsync()
        {
            if (State != StationState.Running) return;

            if (Process != null)
            {
                _processPaused = true;
                try
                {
                    HostRuntime?.StopTriggerSource(StationId);
                }
                catch (Exception ex)
                {
                    LogBus.Warn("StationWorker", $"工位 [{StationId}] 暂停时停止触发源异常: {ex.Message}");
                }
                UpdateState(StationState.Paused);
                LogBus.Info("StationWorker", $"工位 [{StationId}] 已暂停（业务过程：当前周期跑完后不再接新周期）。");
                return;
            }

            await _scheduler.PauseAsync().ConfigureAwait(false);
            UpdateState(StationState.Paused);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已暂停。");
        }

        /// <summary>
        /// 从暂停恢复。
        /// </summary>
        public async Task ResumeAsync()
        {
            if (State != StationState.Paused) return;

            if (Process != null)
            {
                _processPaused = false;
                try
                {
                    HostRuntime?.StartTriggerSource(StationId);
                }
                catch (Exception ex)
                {
                    LogBus.Warn("StationWorker", $"工位 [{StationId}] 恢复时启动触发源异常: {ex.Message}");
                }
                UpdateState(StationState.Running);
                LogBus.Info("StationWorker", $"工位 [{StationId}] 已恢复运行（业务过程）。");
                return;
            }

            await _scheduler.ResumeAsync().ConfigureAwait(false);
            UpdateState(StationState.Running);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已恢复运行。");
        }

        /// <summary>
        /// 单次触发（生产入口：UI 单次/PLC 触发/手动触发）。
        /// 挂载业务过程后：跑**完整业务周期**（过程内部视觉段走 RunContinuousAsync 完整链）；
        /// 未挂载（纯视觉链）：退化为调度器单步——一次 1 个节点（编辑器「单步」语义）。
        /// </summary>
        public Task TriggerOnceAsync(string batchId = null)
        {
            if (State == StationState.ErrorLocked)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 处于 ErrorLocked，拒绝新触发。");
                return Task.CompletedTask;
            }

            if (Process != null)
            {
                return RunProcessOnceAsync(batchId);
            }

            return _scheduler?.TriggerOnceAsync(batchId) ?? Task.CompletedTask;
        }

        /// <summary>
        /// 【运行】完整执行视觉链一次（从链头 ResetIndex，节点间上下文关联）。
        /// 编辑器「运行」按钮专用；始终跑纯视觉链、**不进入业务过程分流**——
        /// 即使 StartAsync 自愈重挂了业务过程，编辑器运行也绝不触发运动/IO 时序。
        /// </summary>
        public Task RunContinuousAsync(string batchId = null)
        {
            if (State == StationState.ErrorLocked)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 处于 ErrorLocked，拒绝连续运行。");
                return Task.CompletedTask;
            }
            return _scheduler?.RunContinuousAsync(batchId) ?? Task.CompletedTask;
        }

        /// <summary>
        /// 【单步】视觉链步进：从链头开始、一次只执行 1 个节点（索引跨触发保留，上下文关联）。
        /// 编辑器「单步」按钮专用；始终跑纯视觉链、**不进入业务过程分流**，
        /// 防止编辑器误触发运动时序撞机（即使 StartAsync 自愈重挂了业务过程）。
        /// </summary>
        public Task StepChainAsync(string batchId = null)
        {
            if (State == StationState.ErrorLocked)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 处于 ErrorLocked，拒绝单步执行。");
                return Task.CompletedTask;
            }
            return _scheduler?.TriggerOnceAsync(batchId) ?? Task.CompletedTask;
        }

        /// <summary>
        /// 执行一次完整业务周期（互斥锁防并发撞机）。
        /// 返回 false（业务 NG）→ Idle；异常 → Faulted；取消 → Idle。
        /// 指标按过程返回值记录工位级 OK/NG。
        /// </summary>
        private async Task RunProcessOnceAsync(string batchId)
        {
            if (_processPaused)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 业务过程已暂停，忽略新周期触发。");
                return;
            }

            if (!await _processExecLock.WaitAsync(0).ConfigureAwait(false))
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 业务过程正在执行中，忽略重复触发信号。");
                return;
            }

            // 快照业务过程：Process 是属性（每次访问重新求值），await 期间可能被
            // DetachProcess（FlowEdit 编辑器 Rebind 时会对共享 worker 卸载）置 null，
            // 必须用局部变量贯穿整个周期，避免恢复后取 Process.ProcessKey 抛 NRE。
            var process = Process;
            if (process == null)
            {
                _processExecLock.Release();
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 未挂载业务过程，忽略周期触发。");
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                if (_processCts == null || _processCts.IsCancellationRequested)
                {
                    _processCts?.Dispose();
                    _processCts = new CancellationTokenSource();
                }

                bool ok = await process.RunAsync(_processCts.Token).ConfigureAwait(false);
                sw.Stop();
                Metrics.RecordWorkOrderCompleted(ok);
                LogBus.Info("StationWorker",
                    $"工位 [{StationId}] 业务过程 [{process.ProcessKey}] 完成: {(ok ? "OK" : "NG")}，耗时 {sw.Elapsed.TotalMilliseconds:F0}ms");
                UpdateState(StationState.Idle);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 业务过程 [{process?.ProcessKey}] 被取消（停止/急停）。");
                // 急停后 EmergencyStopAsync 已置 ErrorLocked，这里不得覆盖回 Idle。
                if (State != StationState.ErrorLocked)
                    UpdateState(StationState.Idle);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Metrics.RecordWorkOrderCompleted(false);
                LogBus.Error("StationWorker", $"工位 [{StationId}] 业务过程 [{process?.ProcessKey}] 异常: {ex.Message}", ex);
                UpdateState(StationState.Faulted);
            }
            finally
            {
                _processExecLock.Release();
            }
        }

        /// <summary>
        /// 仅单步执行指定的单个节点（适用于节点属性弹窗调试）
        /// </summary>
        public Task StepNodeAsync(FlowNodeBase node)
        {
            if (State == StationState.ErrorLocked)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 处于 ErrorLocked，拒绝单步执行。");
                return Task.CompletedTask;
            }
            return _scheduler?.StepNodeAsync(node) ?? Task.CompletedTask;
        }

        /// <summary>
        /// 注入节点实时预览显示上下文（编辑器属性面板调试、工位监视页生产可视化共用）。
        /// 暂存到 StationContext，由调度器构建 NodeExecutionContext（单步调试与生产执行链）时读取；
        /// null 表示清除注入。
        /// </summary>
        public void SetPreviewContext(IFlowPreviewContext preview)
        {
            _stationContext.PreviewContext = preview;
        }

        /// <summary>
        /// 工单级复位：终止当前工单并释放本次占用设备，不改变工位全局状态。
        /// </summary>
        public async Task WorkOrderResetAsync()
        {
            if (_scheduler != null)
            {
                await _scheduler.StopAsync().ConfigureAwait(false);
            }
            UpdateState(StationState.Idle);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 工单级复位完成。");
        }

        /// <summary>
        /// 工位软复位：执行 ResetBlueprint（回安全点、IO 复位、清空队列），不重新初始化硬件句柄。
        /// </summary>
        public async Task SoftResetAsync()
        {
            if (State == StationState.Running)
            {
                await StopAsync().ConfigureAwait(false);
            }

            UpdateState(StationState.Resetting);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 开始软复位...");

            // TODO: 加载并执行 ResetBlueprint（保留给后续实现）
            _stationContext.GlobalEngineContext?.SharedVariables.Clear();

            await _stationContext.DeviceManager.OpenAllAsync().ConfigureAwait(false);

            UpdateState(StationState.Idle);

            // 复位通知业务过程：清除报警灯/蜂鸣器，恢复待机指示（急停/故障解除后灯随复位归位）
            if (Process != null)
            {
                try { await Process.OnResetAsync().ConfigureAwait(false); }
                catch (Exception ex) { LogBus.Warn("StationWorker", $"工位 [{StationId}] 过程复位指示失败: {ex.Message}"); }
            }

            LogBus.Info("StationWorker", $"工位 [{StationId}] 软复位完成。");
        }

        /// <summary>
        /// 硬件全复位：关闭所有设备句柄并重新 Open，用于断连后恢复。
        /// </summary>
        public async Task HardwareResetAsync()
        {
            if (State == StationState.Running)
            {
                await StopAsync().ConfigureAwait(false);
            }

            UpdateState(StationState.Resetting);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 开始硬件全复位...");

            await _stationContext.DeviceManager.CloseAllAsync().ConfigureAwait(false);
            await _stationContext.DeviceManager.OpenAllAsync().ConfigureAwait(false);

            UpdateState(StationState.Idle);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 硬件全复位完成。");
        }

        /// <summary>
        /// 急停：进入 ErrorLocked，终止所有工单。
        /// 关键：必须显式下发控制器级全轴急停（RapidStop）——上位机取消任务（CancellationToken）
        /// 并不会停住 ZMC 已缓冲的轴运动，轴会继续跑完整个运动，急停将形同虚设。
        /// 急停语义 = 立即停住并锁定；不复位、不抬 Z（操作员确认机械安全后手动复位）。
        /// </summary>
        public async Task EmergencyStopAsync(string reason = null)
        {
            // 1) 全轴急停 + 关真空（业务过程内部实现：先关真空再 RapidStop）
            if (Process != null)
            {
                try { await Process.EmergencyStopAsync().ConfigureAwait(false); }
                catch (Exception ex) { LogBus.Warn("StationWorker", $"工位 [{StationId}] 全轴急停指令异常: {ex.Message}"); }
            }
            else
            {
                // 未挂业务过程（纯视觉链/硬件控制模式）：直接对工位绑定的运动卡下发全轴急停
                try
                {
                    foreach (var key in _stationContext.DeviceManager.GetDeviceStates().Keys)
                    {
                        if (_stationContext.ResolveDevice(key) is IMotionCard mc)
                        {
                            var r = mc.RapidStop();
                            if (r.Success) LogBus.Info("StationWorker", $"工位 [{StationId}] 运动卡 [{key}] 全轴急停已下发。");
                            else LogBus.Warn("StationWorker", $"工位 [{StationId}] 运动卡 [{key}] 全轴急停下发失败: {r.Message}");
                        }
                    }
                }
                catch (Exception ex) { LogBus.Warn("StationWorker", $"工位 [{StationId}] 全轴急停异常: {ex.Message}"); }
            }

            // 2) 取消当前业务周期（等待循环/视觉流立即终止；RunAsync 内部对 OCE 直接上抛，不再抬 Z）
            try { _processCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            _processCts?.Dispose();
            _processCts = null;

            // 3) 进入锁定态。注意 RunProcessOnceAsync 的 catch(OCE) 会回写 Idle，
            //    必须先置 ErrorLocked，并让 OCE 分支检测到锁定态后保持不覆盖。
            UpdateState(StationState.ErrorLocked);
            LogBus.Error("StationWorker", $"工位 [{StationId}] 触发急停！原因: {reason ?? "未指定"}");
        }

        #region 事件路由与状态更新
        private void Engine_OnNodeExecuting(object sender, FlowNodeBase node)
        {
            OnNodeExecuting?.Invoke(this, new NodeEventArgs(StationId, node));
        }

        private void Engine_OnNodeExecuted(object sender, FlowNodeBase node)
        {
            OnNodeExecuted?.Invoke(this, new NodeEventArgs(StationId, node));

            // 推送渲染事件
            var imagePort = node.OutputPorts?.FirstOrDefault(p =>
                p.DataType == "Image" || (p.PortName != null && p.PortName.IndexOf("Image", StringComparison.OrdinalIgnoreCase) >= 0));

            if (imagePort?.DataValue != null)
            {
                OnFrameRendered?.Invoke(this, new ImageRenderEventArgs
                {
                    StationId = StationId,
                    NodeId = node.NodeId,
                    NodeName = node.DisplayName,
                    RenderData = imagePort.DataValue
                });
            }
        }

        private void Engine_OnExecutionError(object sender, NodeExecutionErrorEventArgs e)
        {
            Metrics.RecordWorkOrderCompleted(isOk: false);
            OnExecutionError?.Invoke(this, e);
        }

        private void UpdateState(StationState newState)
        {
            if (State == newState) return;
            State = newState;
            OnStateChanged?.Invoke(this, State);
        }

        public void Dispose()
        {
            UnbindSchedulerEvents();
            _processCts?.Dispose();
            _processExecLock.Dispose();
            _scheduler?.Dispose();
            _stationContext?.DeviceManager?.Dispose();

            if (_stationContext?.GlobalEngineContext != null)
            {
                _stationContext.GlobalEngineContext.OnNodeExecuting -= Engine_OnNodeExecuting;
                _stationContext.GlobalEngineContext.OnNodeExecuted -= Engine_OnNodeExecuted;
                _stationContext.GlobalEngineContext.OnExecutionError -= Engine_OnExecutionError;
            }
        }
        #endregion
    }
}