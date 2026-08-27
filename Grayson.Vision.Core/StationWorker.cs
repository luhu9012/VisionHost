using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Infrastructure.Metrics;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Core.Scheduling;
using Grayson.Vision.Core.Station;
using System;
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

            await _scheduler.StopAsync().ConfigureAwait(false);

            UpdateState(ActiveExecutionChain?.Count > 0 ? StationState.Idle : StationState.Stopped);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已停止。");
        }

        /// <summary>
        /// 暂停工位：挂起调度，不响应新触发。
        /// </summary>
        public async Task PauseAsync()
        {
            if (State != StationState.Running) return;

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

            await _scheduler.ResumeAsync().ConfigureAwait(false);
            UpdateState(StationState.Running);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已恢复运行。");
        }

        /// <summary>
        /// 单次触发（单步或单帧执行）
        /// </summary>
        public Task TriggerOnceAsync(string batchId = null)
        {
            if (State == StationState.ErrorLocked)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 处于 ErrorLocked，拒绝新触发。");
                return Task.CompletedTask;
            }
            return _scheduler?.TriggerOnceAsync(batchId) ?? Task.CompletedTask;
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
        /// 注入节点实时预览显示上下文（编辑器属性面板调试专用）。
        /// 暂存到 StationContext，由调度器在构建调试单步 NodeExecutionContext 时读取；
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
        /// </summary>
        public async Task EmergencyStopAsync(string reason = null)
        {
            await StopAsync().ConfigureAwait(false);
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