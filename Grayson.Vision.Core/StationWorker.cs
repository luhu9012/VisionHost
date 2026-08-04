using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
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

        public WorkMode Mode { get; set; } = WorkMode.Production;

        private readonly StationContext _stationContext;
        private FlowExecutor _executor;
        private FlowProcessModel _currentRecipe;
        private readonly SemaphoreSlim _execLock = new SemaphoreSlim(1, 1); // 保证单步/连续触发的线程安全

        public ExecutionChain ActiveExecutionChain { get; private set; }

        private CancellationTokenSource _continuousLoopCts;

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

            var engineContext = _stationContext.GlobalEngineContext;
            if (engineContext != null)
            {
                engineContext.OnNodeExecuting += Engine_OnNodeExecuting;
                engineContext.OnNodeExecuted += Engine_OnNodeExecuted;
                engineContext.OnExecutionError += Engine_OnExecutionError;
            }
        }

        /// <summary>
        /// 加载配方并初始化基于 ExecutionChain 的执行器
        /// 🛡️ 工业级规范：不强抛致命异常，通过返回 Task&lt;bool&gt; 告知上层加载状态，确保 UI 能够正常启动
        /// </summary>
        /// <summary>
        /// 加载配方并初始化基于 ExecutionChain 的执行器
        /// 🛡️ 遵守 IStationWorkerHost 契约 ( Task LoadRecipeAsync )
        /// </summary>
        public Task LoadRecipeAsync(FlowProcessModel recipe)
        {
            // 先取消订阅旧 executor 的事件
            if (_executor != null)
            {
                _executor.OnChainCompleted -= Executor_OnChainCompleted;
            }
            if (recipe == null)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 传入配方为空，回退为空白执行链。");
                ActiveExecutionChain = new ExecutionChain();
                _executor = new FlowExecutor(ActiveExecutionChain, _stationContext.GlobalEngineContext);
                _executor.OnChainCompleted += Executor_OnChainCompleted; // 🌟 订阅
                UpdateState(StationState.Stopped);
                return Task.CompletedTask;
            }

            _currentRecipe = recipe;

            // 构建/获取执行链
            var buildResult = ExecutionChain.BuildAndValidate(_currentRecipe);
            if (!buildResult.IsSuccess)
            {
                // 🛡️ 防暴关键：不抛 Exception，仅记录错误并将工位标记为 Faulted
                LogBus.Error("StationWorker", $"工位 [{StationId}] 加载配方 [{_currentRecipe.ProcessName}] 校验未通过：{buildResult.ErrorMessage}");

                // 依然保留已构建的部分链或空链，保障 UI 能够渲染错误节点
                ActiveExecutionChain = buildResult.Chain ?? new ExecutionChain();
                _executor = new FlowExecutor(ActiveExecutionChain, _stationContext.GlobalEngineContext);
                _executor.OnChainCompleted += Executor_OnChainCompleted; // 🌟 订阅

                // 标记为 Faulted，告知上层加载配方失败
                UpdateState(StationState.Faulted);
                return Task.CompletedTask;
            }

            ActiveExecutionChain = buildResult.Chain;
            _executor = new FlowExecutor(ActiveExecutionChain, _stationContext.GlobalEngineContext);
            _executor.OnChainCompleted += Executor_OnChainCompleted; // 🌟 订阅

            // 配方就绪，进入待机/就绪状态
            UpdateState(StationState.Idle); // 或根据你的枚举调整为可运行状态
            LogBus.Info("StationWorker", $"工位 [{StationId}] 成功加载配方并就绪执行链: {_currentRecipe.ProcessName}");

            return Task.CompletedTask;
        }
        // 🌟 处理 Executor 传上来的完成事件并继续抛给 Worker 订阅者
        private void Executor_OnChainCompleted(object sender, ChainCompletedEventArgs e)
        {
            LogBus.Info("StationWorker", $"工位 [{StationId}] 执行链结束，执行结果: {e.Result}");

            switch (e.Result)
            {
                case ChainExecutionResult.Success:
                    // 🌟 正常跑完最后一个节点：
                    // 如果是连续生产/调试循环，保持 Running 状态；
                    // 如果是单次/单步触发完成，切回 Idle 等待下一次信号。
                    if (Mode == WorkMode.Production || Mode == WorkMode.Debug)
                    {
                        // 如果是 TriggerOnce 模式，执行完后切回 Idle
                        UpdateState(StationState.Idle);
                    }
                    break;

                case ChainExecutionResult.Failed:
                    // 🌟 节点执行失败/致命错误：切入 Faulted 状态，阻止后续触发
                    UpdateState(StationState.Faulted);
                    break;

                case ChainExecutionResult.Canceled:
                    // 🌟 手动停止取消：切入 Stopped 状态
                    UpdateState(StationState.Stopped);
                    break;

                case ChainExecutionResult.StepEndReached:
                    // 🌟 单步已到末尾：切回 Paused 或 Idle
                    UpdateState(StationState.Idle);
                    break;
            }

            // 🌟 将完成事件与结果继续向上层抛出（通知 UI / 应用层做清理和复位）
            OnExecutionCompleted?.Invoke(this, e);
        }
        public async Task StartAsync()
        {
            if (_executor == null || ActiveExecutionChain == null || ActiveExecutionChain.Count == 0)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 缺乏有效执行链，无法启动。");
                return;
            }

            if (State == StationState.Running) return;

            UpdateState(StationState.Running);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已启动 (模式: {Mode})。");

            if (Mode == WorkMode.Debug)
            {
                _continuousLoopCts = new CancellationTokenSource();
                _ = Task.Run(() => StartDebugContinuousLoopAsync(_continuousLoopCts.Token));
            }
            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            _continuousLoopCts?.Cancel();
            _continuousLoopCts?.Dispose();
            _continuousLoopCts = null;

            _executor?.Stop();

            // 如果配方有效切回 Idle，无配方切为 Stopped
            UpdateState(ActiveExecutionChain?.Count > 0 ? StationState.Idle : StationState.Stopped);
            LogBus.Info("StationWorker", $"工位 [{StationId}] 已停止。");
            await Task.CompletedTask;
        }

        /// <summary>
        /// 单次触发（单步或单帧执行）
        /// </summary>
        public async Task TriggerOnceAsync(string batchId = null)
        {
            if (_executor == null || ActiveExecutionChain == null || ActiveExecutionChain.Count == 0)
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 未加载有效配方，忽略触发。");
                return;
            }

            // 锁保护：防止重入导致的线程竞争
            if (!await _execLock.WaitAsync(0))
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 正在执行中，忽略重复触发信号。");
                return;
            }

            try
            {
                var cycleContext = new FrameCycleContext { BatchId = batchId ?? Guid.NewGuid().ToString("N") };
                var nodeExecContext = new NodeExecutionContext(
                    _stationContext.GlobalEngineContext,
                    cycleContext,
                    _stationContext.ResolveDevice
                );

                await _executor.StepAsync();
            }
            catch (Exception ex)
            {
                LogBus.Error("StationWorker", $"工位 [{StationId}] 触发单次运行失败: {ex.Message}", ex);
            }
            finally
            {
                _execLock.Release();
            }
        }
        /// <summary>
        /// 仅单步执行指定的单个节点（适用于节点属性弹窗调试）
        /// </summary>
        public async Task StepNodeAsync(FlowNodeBase node)
        {
            if (node == null) return;

            if (!await _execLock.WaitAsync(0))
            {
                LogBus.Warn("StationWorker", $"工位 [{StationId}] 正在执行中，忽略重复触发信号。");
                return;
            }

            try
            {
                var cycleContext = new FrameCycleContext { BatchId = Guid.NewGuid().ToString("N") };
                var nodeExecContext = new NodeExecutionContext(
                    _stationContext.GlobalEngineContext,
                    cycleContext,
                    _stationContext.ResolveDevice
                );

                // 如果 FlowExecutor 支持单节点执行，可以直接调用 executor.ExecuteNodeAsync(node, nodeExecContext)
                // 或者是单节点 Executor 的实例化运行：
                LogBus.Info("StationWorker", $"调试单步运行节点: {node.DisplayName} ({node.NodeId})");

                // 触发节点开始/结束事件，以供 UI 渲染和日志收集
                _stationContext.GlobalEngineContext?.NotifyNodeExecuting(node);

                // 获取/创建对应的 NodeExecutor 并执行
                var executor = node.Executor;
                if (executor != null)
                {
                    await executor.ExecuteAsync(node, nodeExecContext, CancellationToken.None);
                }

                _stationContext.GlobalEngineContext?.NotifyNodeExecuted(node);
            }
            catch (Exception ex)
            {
                LogBus.Error("StationWorker", $"节点 [{node.DisplayName}] 单步运行失败: {ex.Message}", ex);
            }
            finally
            {
                _execLock.Release();
            }
        }

        private async Task StartDebugContinuousLoopAsync(CancellationToken token)
        {
            LogBus.Info("StationWorker", $"进入调试连续运行循环...");
            
            while (!token.IsCancellationRequested && State == StationState.Running)
            {
                try
                {
                    await TriggerOnceAsync();
                    await Task.Delay(50, token); // 工业连续调试微小间隔
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
            if (_executor != null)
            {
                _executor.OnChainCompleted -= Executor_OnChainCompleted;
            }
            _continuousLoopCts?.Cancel();
            _continuousLoopCts?.Dispose();
            _execLock?.Dispose();

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