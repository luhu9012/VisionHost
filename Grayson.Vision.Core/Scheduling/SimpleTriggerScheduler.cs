using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Core.Flow;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Scheduling
{
    /// <summary>
    /// 简单触发调度器：保持现有 StationWorker 中“单次触发 + 调试连续循环”的行为。
    /// 后续可扩展为流水线/事件驱动等调度策略。
    /// </summary>
    public class SimpleTriggerScheduler : IWorkflowScheduler
    {
        private readonly string _stationId;
        private readonly StationContext _stationContext;
        private readonly Grayson.Vision.Core.Station.WorkOrderTracker _workOrderTracker;
        private FlowExecutor _executor;
        private ExecutionChain _executionChain;
        private CancellationTokenSource _continuousLoopCts;
        private readonly SemaphoreSlim _execLock = new SemaphoreSlim(1, 1);

        public string StationId => _stationId;
        public WorkMode Mode { get; set; } = WorkMode.Production;
        public bool IsRunning => _continuousLoopCts != null && !_continuousLoopCts.IsCancellationRequested;

        public event EventHandler<ChainCompletedEventArgs> OnExecutionCompleted;
        public event EventHandler<NodeEventArgs> OnNodeExecuting;
        public event EventHandler<NodeEventArgs> OnNodeExecuted;
        public event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError;

        public SimpleTriggerScheduler(string stationId, StationContext stationContext,
            Grayson.Vision.Core.Station.WorkOrderTracker workOrderTracker = null)
        {
            _stationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
            _stationContext = stationContext ?? throw new ArgumentNullException(nameof(stationContext));
            _workOrderTracker = workOrderTracker;
        }

        public Task LoadExecutionChainAsync(ExecutionChain chain)
        {
            if (_executor != null)
            {
                _executor.OnChainCompleted -= Executor_OnChainCompleted;
            }

            _executionChain = chain ?? new ExecutionChain();
            _executor = new FlowExecutor(_executionChain, _stationContext.GlobalEngineContext);
            _executor.OnChainCompleted += Executor_OnChainCompleted;

            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_executor == null || _executionChain == null || _executionChain.Count == 0)
            {
                LogBus.Warn("SimpleTriggerScheduler", $"[{_stationId}] 缺乏有效执行链，无法启动。");
                return Task.CompletedTask;
            }

            if (Mode == WorkMode.Debug)
            {
                _continuousLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _ = Task.Run(() => StartDebugContinuousLoopAsync(_continuousLoopCts.Token));
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _continuousLoopCts?.Cancel();
            _continuousLoopCts?.Dispose();
            _continuousLoopCts = null;
            _executor?.Stop();
            return Task.CompletedTask;
        }

        private bool _isPaused;

        /// <summary>
        /// 暂停调度：停止接受新的触发，但保留当前执行链。
        /// </summary>
        public Task PauseAsync()
        {
            _isPaused = true;
            if (Mode == WorkMode.Debug)
            {
                _continuousLoopCts?.Cancel();
            }
            LogBus.Info("SimpleTriggerScheduler", $"[{_stationId}] 调度已暂停。");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 恢复调度：重新接受触发。
        /// </summary>
        public Task ResumeAsync()
        {
            _isPaused = false;
            LogBus.Info("SimpleTriggerScheduler", $"[{_stationId}] 调度已恢复。");
            return Task.CompletedTask;
        }

        public async Task TriggerOnceAsync(string batchId = null)
        {
            if (!await AcquireExecutionLockAsync().ConfigureAwait(false)) return;

            WorkOrderExecutionContext workOrderContext = null;
            WorkOrder workOrder = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                workOrder = new WorkOrder(null, _stationId, batchId, Mode == WorkMode.Debug ? "DEBUG_SCHEDULER" : "TRIGGER");
                _workOrderTracker?.Track(workOrder);
                workOrderContext = new WorkOrderExecutionContext(workOrder, _stationContext.GlobalEngineContext);

                // TODO: 若未来 StationContext 持有当前 RecipeModel，可在此处绑定配方追溯快照
                // workOrderContext.BindRecipeTraceability(_stationContext.CurrentRecipe?.CreateTraceabilitySnapshot());

                var nodeExecContext = workOrderContext.CreateNodeContext(_stationContext.ResolveDevice);
                _executor.SetNodeExecutionContext(nodeExecContext);

                await _executor.StepAsync().ConfigureAwait(false);

                sw.Stop();
                workOrder.MarkCompleted(isOk: true);
                workOrder.ResultData = new WorkOrderResultData
                {
                    IsOk = true,
                    CycleTimeMs = sw.Elapsed.TotalMilliseconds
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                workOrder?.MarkAborted(WorkOrderStatus.Abort_Error,
                    reason: ex.Message,
                    ex: ex,
                    faultCode: -1000);
                if (workOrder != null)
                {
                    workOrder.ResultData = new WorkOrderResultData
                    {
                        IsOk = false,
                        CycleTimeMs = sw.Elapsed.TotalMilliseconds,
                        ErrorMessage = ex.Message
                    };
                }
                LogBus.Error("SimpleTriggerScheduler", $"[{_stationId}] 触发单次运行失败: {ex.Message}", ex);
            }
            finally
            {
                _execLock.Release();
                workOrderContext?.Dispose();
            }
        }

        public async Task StepNodeAsync(FlowNodeBase node)
        {
            if (node == null) return;
            if (!await AcquireExecutionLockAsync().ConfigureAwait(false)) return;

            try
            {
                var cycleContext = new FrameCycleContext { BatchId = Guid.NewGuid().ToString("N") };
                var nodeExecContext = new NodeExecutionContext(
                    _stationContext.GlobalEngineContext,
                    cycleContext,
                    _stationContext.ResolveDevice
                );

                // 🌟 调试单步链路注入实时预览上下文（编辑器属性面板专用；
                //    生产触发链路 TriggerOnceAsync 不注入，Preview 恒为 null）
                nodeExecContext.Preview = _stationContext.PreviewContext;

                LogBus.Info("SimpleTriggerScheduler", $"[{_stationId}] 调试单步运行节点: {node.DisplayName} ({node.NodeId})");

                _stationContext.GlobalEngineContext?.NotifyNodeExecuting(node);

                var executor = node.Executor;
                if (executor != null)
                {
                    await executor.ExecuteAsync(node, nodeExecContext, CancellationToken.None).ConfigureAwait(false);
                }

                _stationContext.GlobalEngineContext?.NotifyNodeExecuted(node);
            }
            catch (Exception ex)
            {
                LogBus.Error("SimpleTriggerScheduler", $"[{_stationId}] 节点 [{node.DisplayName}] 单步运行失败: {ex.Message}", ex);
            }
            finally
            {
                _execLock.Release();
            }
        }

        private async Task StartDebugContinuousLoopAsync(CancellationToken token)
        {
            LogBus.Info("SimpleTriggerScheduler", $"[{_stationId}] 进入调试连续运行循环...");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await TriggerOnceAsync().ConfigureAwait(false);
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogBus.Error("SimpleTriggerScheduler", $"[{_stationId}] 连续调试运行异常: {ex.Message}");
                    break;
                }
            }

            LogBus.Info("SimpleTriggerScheduler", $"[{_stationId}] 退出调试连续运行循环。");
        }

        private async Task<bool> AcquireExecutionLockAsync()
        {
            if (_isPaused)
            {
                LogBus.Warn("SimpleTriggerScheduler", $"[{_stationId}] 调度处于暂停状态，忽略触发。");
                return false;
            }

            if (_executor == null || _executionChain == null || _executionChain.Count == 0)
            {
                LogBus.Warn("SimpleTriggerScheduler", $"[{_stationId}] 未加载有效执行链，忽略触发。");
                return false;
            }

            if (!await _execLock.WaitAsync(0).ConfigureAwait(false))
            {
                LogBus.Warn("SimpleTriggerScheduler", $"[{_stationId}] 正在执行中，忽略重复触发信号。");
                return false;
            }

            return true;
        }

        private void Executor_OnChainCompleted(object sender, ChainCompletedEventArgs e)
        {
            OnExecutionCompleted?.Invoke(this, e);
        }

        public void Dispose()
        {
            StopAsync().Wait(TimeSpan.FromSeconds(5));
            _execLock.Dispose();
        }
    }
}
