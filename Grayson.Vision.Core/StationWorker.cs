using Grayson.Vision.Contracts.Business.Engine;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Enums; 
using Grayson.Vision.Contracts.Business.Events;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core
{
    public class StationWorker : IStationWorkerHost, IStationWorkerEvents
    {
        public string StationId { get; }
        public StationState State { get; private set; } = StationState.Stopped;

        // 🌟 新增：当前运行模式（默认为调试模式）
        public WorkMode Mode { get; set; } = WorkMode.Production;

        private readonly StationContext _stationContext;
        private FlowExecutor _executor;
        private FlowProcessModel _currentRecipe;

        // 🌟 用于控制“连续调试运行”的取消令牌
        private CancellationTokenSource _continuousLoopCts;

        #region 事件定义
        public event EventHandler<StationState> OnStateChanged;
        public event EventHandler<ImageRenderEventArgs> OnFrameRendered;
        public event EventHandler<NodeEventArgs> OnNodeExecuting;
        public event EventHandler<NodeEventArgs> OnNodeExecuted;
        public event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError;
        public event EventHandler<string> OnLogReceived;
        #endregion

        public StationWorker(string stationId)
        {
            StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
            _stationContext = new StationContext(stationId);

            var engineContext = _stationContext.GlobalEngineContext;
            if (engineContext != null)
            {
                engineContext.OnNodeExecuting += Engine_OnNodeExecuting;
                engineContext.OnNodeExecuted += Engine_OnNodeExecuted;
                engineContext.OnExecutionError += Engine_OnExecutionError;
            }
        }

        public Task LoadRecipeAsync(FlowProcessModel recipe)
        {
            _currentRecipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
            _executor = new FlowExecutor(_currentRecipe, _stationContext.GlobalEngineContext);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 成功加载配方: {_currentRecipe.ProcessName}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 启动工位 (开启连续模式或进入就绪状态)
        /// </summary>
        public Task StartAsync()
        {
            if (_executor == null)
                throw new InvalidOperationException($"工位 [{StationId}] 未加载配方，无法启动！");

            if (State == StationState.Running) return Task.CompletedTask;

            UpdateState(StationState.Running);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已启动 (模式: {Mode})。");

            // 🌟 如果在调试模式下点击连续运行，自动开启后台软循环节拍
            if (Mode == WorkMode.Debug)
            {
                _continuousLoopCts = new CancellationTokenSource();
                Task.Run(() => StartDebugContinuousLoopAsync(_continuousLoopCts.Token));
            }
            else
            {
                // 产线模式：开启硬件/相机 Trigger 监听句柄 (如有)
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 停止工位
        /// </summary>
        public Task StopAsync()
        {
            // 取消调试连续循环
            _continuousLoopCts?.Cancel();
            _continuousLoopCts?.Dispose();
            _continuousLoopCts = null;

            _executor?.Stop();
            UpdateState(StationState.Stopped);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已停止。");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 单次/单步触发 (兼容调试模式下的强行单步测试)
        /// </summary>
        public async Task TriggerOnceAsync(string batchId = null)
        {
            if (_executor == null)
            {
                LogBus.Error("StationWorker", $"工位 [{StationId}] 未加载配方，无法触发。");
                return;
            }

            // 🌟 状态守护兼容：如果是 Debug 模式且当前 Stopped，允许单次临时放行
            bool tempStarted = false;
            if (State == StationState.Stopped)
            {
                if (Mode == WorkMode.Debug)
                {
                    LogBus.Info("StationWorker", $"调试模式下响应单步触发，临时启动执行...");
                    tempStarted = true;
                }
                else
                {
                    LogBus.Warn("StationWorker", $"产线模式下工位未处于 Running 状态，拒绝触发。");
                    return;
                }
            }

            try
            {
                var cycleContext = new FrameCycleContext { BatchId = batchId ?? Guid.NewGuid().ToString("N") };
                var nodeExecContext = new NodeExecutionContext(
                    _stationContext.GlobalEngineContext,
                    cycleContext,
                    _stationContext.ResolveDevice
                );

                await _executor.RunContinuousAsync();
            }
            finally
            {
                // 临时触发完毕恢复状态
                if (tempStarted && State == StationState.Running)
                {
                    UpdateState(StationState.Stopped);
                }
            }
        }

        /// <summary>
        /// 🌟 调试模式下的连续软触发循环线程
        /// </summary>
        private async Task StartDebugContinuousLoopAsync(CancellationToken token)
        {
            LogBus.Info("StationWorker", $"进入调试连续运行循环...");
            while (!token.IsCancellationRequested && State == StationState.Running)
            {
                try
                {
                    await TriggerOnceAsync();
                    // 调试模式下连续运行添加适量间隔 (如 50ms)，防止 CPU 占满卡死 UI
                    await Task.Delay(50, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogBus.Error("StationWorker", $"连续调试运行发生异常: {ex.Message}");
                    break;
                }
            }
            LogBus.Info("StationWorker", $"退出调试连续运行循环。");
        }

        #region 事件路由与状态更新
        private void Engine_OnNodeExecuting(object sender, FlowNodeBase node)
        {
            OnNodeExecuting?.Invoke(this, new NodeEventArgs(StationId, node));
        }

        private void Engine_OnNodeExecuted(object sender, FlowNodeBase node)
        {
            OnNodeExecuted?.Invoke(this, new NodeEventArgs(StationId, node));

            var imagePort = node.OutputPorts?.FirstOrDefault(p => p.DataType == "Image" || (p.PortName != null && p.PortName.Contains("Image")));
            if (imagePort?.DataValue != null)
            {
                OnFrameRendered?.Invoke(this, new ImageRenderEventArgs
                {
                    StationId = StationId,
                    NodeId = node.NodeId,
                    RenderData = imagePort.DataValue
                });
            }
        }

        private void Engine_OnExecutionError(object sender, NodeExecutionErrorEventArgs e)
        {
            OnExecutionError?.Invoke(this, e);
        }

        private void UpdateState(StationState newState)
        {
            State = newState;
            OnStateChanged?.Invoke(this, State);
        }

        public void Dispose()
        {
            StopAsync().Wait();
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