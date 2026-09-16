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

            // 🌟 启动 = 明确的"重新开始"动作，同时解除暂停状态
            //    （编辑器场景：暂停后点「运行」能直接恢复，无需单独点恢复按钮）
            _isPaused = false;

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

        /// <summary>
        /// 【单步】触发：在任务流形成的执行链中，从头部节点开始，每次触发只执行 1 个节点，
        /// 与前后节点的输入输出上下文关联（索引跨触发保留），走到链尾后自动回到链头。
        /// 对应编辑器「单步」按钮 / 纯视觉链工位的手动单次触发。
        /// </summary>
        public Task TriggerOnceAsync(string batchId = null)
        {
            return ExecuteScopedAsync(batchId, "【单步】", () => _executor.StepAsync());
        }

        /// <summary>
        /// 【运行】触发：从链头完整执行整条执行链一次（ResetIndex 从 0 开始，节点间上下文关联）。
        /// 对应编辑器「运行」按钮、业务过程内部的视觉段（采图→匹配→标定需一次拿全结果）、
        /// 以及生产触发源的单周期节拍。
        /// </summary>
        public Task RunContinuousAsync(string batchId = null)
        {
            return ExecuteScopedAsync(batchId, "【运行】", () => _executor.RunContinuousAsync());
        }

        /// <summary>
        /// 【分段执行】只执行执行链 [startIndex, endIndexExclusive) 区间内的节点。
        /// 供复合工位"上相机段 → 机械动作 → 下相机段"交错节拍使用（业务过程内部直连）。
        /// </summary>
        internal Task RunRangeAsync(int startIndex, int endIndexExclusive, string batchId = null)
        {
            return ExecuteScopedAsync(batchId, "【分段】", () => _executor.RunRangeAsync(startIndex, endIndexExclusive));
        }

        /// <summary>
        /// 单次/连续执行的公共脚手架：工单追踪 + 节点执行上下文注入 + 异常/完成统计。
        /// <paramref name="runCore"/> 只允许调用 FlowExecutor 的执行原语（StepAsync / RunContinuousAsync）。
        /// </summary>
        private async Task ExecuteScopedAsync(string batchId, string modeLabel, Func<Task> runCore)
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

                var nodeExecContext = workOrderContext.CreateNodeContext(_stationContext.ResolveDevice);
                // 🌟 注入实时预览上下文：宿主（工位监视页/FlowEdit）经 IWorkerClient.SetPreviewContext
                //    注入即生效，节点内 Preview?.Add 的叠加图形（模板匹配轮廓等）实时上屏；
                //    未注入时为 null，节点侧调用天然跳过，不影响执行
                nodeExecContext.Preview = _stationContext.PreviewContext;
                _executor.SetNodeExecutionContext(nodeExecContext);

                LogBus.Info("SimpleTriggerScheduler", $"[{_stationId}] {modeLabel} 触发执行链（{_executionChain?.Count ?? 0} 个节点）。");
                await runCore().ConfigureAwait(false);

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
                LogBus.Error("SimpleTriggerScheduler", $"[{_stationId}] {modeLabel} 触发执行失败: {ex.Message}", ex);
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

                // 🌟 注入实时预览上下文（与 ExecuteScopedAsync 同源：谁 SetPreviewContext 就用谁的，
                //    未注入为 null 节点侧天然跳过）
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
                    // 调试连续循环 = 反复完整执行整条链（每次从链头 ResetIndex），
                    // 不是单步步进（单步走 TriggerOnceAsync，由用户手动逐次点击）。
                    await RunContinuousAsync().ConfigureAwait(false);
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
