
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExecutionContext = Grayson.Vision.Contracts.Flow.Contexts.ExecutionContext;

namespace Grayson.Vision.Contracts.Flow.Executants
{
    public enum ExecutionMode
    {
        Continuous,
        Step,
        Paused,
        Stopped
    }

    /// <summary>
    /// 独立流程执行引擎（基于预编译 ExecutionChain 顺序驱动）
    /// </summary>
    public class FlowExecutor
    {
        private readonly ExecutionChain _executionChain;
        private readonly ExecutionContext _context;
        private CancellationTokenSource _cts;

        private int _currentStepIndex = 0;
        public ExecutionMode State { get; private set; } = ExecutionMode.Stopped;
        // 🌟 新增：执行链完成事件
        public event EventHandler<ChainCompletedEventArgs> OnChainCompleted;

        public FlowExecutor(ExecutionChain executionChain, ExecutionContext context)
        {
            _executionChain = executionChain ?? throw new ArgumentNullException(nameof(executionChain));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// 启动连续运行
        /// </summary>
        /// <summary>
        /// 启动连续运行
        /// </summary>
        public async Task RunContinuousAsync()
        {
            if (State == ExecutionMode.Continuous) return;
            ResetIndex();
            State = ExecutionMode.Continuous;
            _cts = new CancellationTokenSource();
            LogBus.Info("Engine", $"▶ 引擎按预编译执行链开始运行 (共 {_executionChain.Count} 个节点)...");

            ChainExecutionResult execResult = ChainExecutionResult.Success;
            Exception fatalException = null;

            // 🌟 启动耗时计时器
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var execChain = _executionChain.Nodes;

                while (_currentStepIndex < execChain.Count && State == ExecutionMode.Continuous)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var node = execChain[_currentStepIndex];
                    if (node.Enable)
                    {
                        bool success = await ExecuteNodeWithHandlingAsync(node, _cts.Token);
                        if (!success)
                        {
                            LogBus.Warn("Engine", $"🛑 节点 [{node.DisplayName}] 执行失败，中止流程。");
                            execResult = ChainExecutionResult.Failed;
                            break;
                        }
                    }
                    else
                    {
                        LogBus.Debug("Engine", $"⏭ 节点 [{node.DisplayName}] 被禁用，自动跳过。");
                    }

                    _currentStepIndex++;
                }

                if (execResult == ChainExecutionResult.Success)
                {
                    LogBus.Info("Engine", $"✔ 执行链顺利运行完毕。");
                }
            }
            catch (OperationCanceledException)
            {
                execResult = ChainExecutionResult.Canceled;
                LogBus.Info("Engine", "⏹ 流程已被手动停止。");
            }
            catch (Exception ex)
            {
                execResult = ChainExecutionResult.Failed;
                fatalException = ex;
                LogBus.Error("Engine", $"❌ 引擎致命错误: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();
                Stop();
                // 🌟 核心：通知上层执行链已结束，并传入总耗时 ExecutionTimeMs
                OnChainCompleted?.Invoke(this, new ChainCompletedEventArgs(execResult, fatalException, stopwatch.Elapsed.TotalMilliseconds));
            }
        }
        /// <summary>
        /// 单步执行下一个节点
        /// </summary>
        public async Task StepAsync()
        {
            var execChain = _executionChain.Nodes;
            if (execChain.Count == 0)
            {
                LogBus.Warn("Engine", "单步执行取消: 执行链中没有可执行节点。");
                return;
            }

            if (_currentStepIndex >= execChain.Count)
            {
                LogBus.Info("Engine", "🔄 单步执行到达末尾");
                OnChainCompleted?.Invoke(this, new ChainCompletedEventArgs(ChainExecutionResult.StepEndReached));
                return;
            }

            State = ExecutionMode.Step;
            _cts = new CancellationTokenSource();

            var stopwatch = Stopwatch.StartNew();

            while (_currentStepIndex < execChain.Count)
            {
                var node = execChain[_currentStepIndex];
                if (node.Enable)
                {
                    LogBus.Info("Engine", $"⏯ [单步 {_currentStepIndex + 1}/{execChain.Count}] 准备执行节点: {node.DisplayName}");
                    await ExecuteNodeWithHandlingAsync(node, _cts.Token);
                    _currentStepIndex++;
                    break;
                }
                else
                {
                    LogBus.Info("Engine", $"⏭ 跳过禁用节点: {node.DisplayName}");
                    _currentStepIndex++;
                }
            }

            stopwatch.Stop();
            State = ExecutionMode.Paused;

            // 🌟 如果单步刚好执行完最后一个节点，触发完成通知并传入耗时
            if (_currentStepIndex >= execChain.Count)
            {
                OnChainCompleted?.Invoke(this, new ChainCompletedEventArgs(ChainExecutionResult.Success, null, stopwatch.Elapsed.TotalMilliseconds));
            }
        }
        /// <summary>
        /// 带数据绑定与异常捕获的单个节点执行
        /// </summary>
        private async Task<bool> ExecuteNodeWithHandlingAsync(FlowNodeBase node, CancellationToken token)
        {
            node.IsRunning = true;
            node.HasError = false;
            _context.NotifyNodeExecuting(node);
            LogBus.Debug("Engine", $"========== 开始执行节点 [{node.DisplayName}] (ID: {node.NodeId}) ==========");

            try
            {
                // 1. 数据绑定：提取上游输入数据
                PrepareNodeInputs(node);

                // 2. 构建节点运行上下文并触发业务逻辑
                var nodeExecContext = new NodeExecutionContext(_context, new FrameCycleContext());

                LogBus.Debug("Engine", $"正在触发节点 [{node.DisplayName}] 的核心业务逻辑 (ExecuteAsync)...");

                if (node.Executor is INodeExecutor executor)
                {
                    await executor.ExecuteAsync(node, nodeExecContext, token);
                    LogBus.Info("Engine", $"节点 [{node.DisplayName}] 核心逻辑执行成功。");
                }
                else if (node.ExecutorType != null)
                {
                    var dynamicExecutor = Activator.CreateInstance(node.ExecutorType) as INodeExecutor;
                    if (dynamicExecutor != null)
                    {
                        await dynamicExecutor.ExecuteAsync(node, nodeExecContext, token);
                        LogBus.Info("Engine", $"节点 [{node.DisplayName}] 核心逻辑执行成功。");
                    }
                }
                else
                {
                    LogBus.Warn("Engine", $"节点 [{node.DisplayName}] 未挂载有效的 INodeExecutor 执行器，跳过业务逻辑执行。");
                }

                // 3. 将节点的输出写回端口并向下游传播
                PropagateNodeOutputs(node);

                node.IsRunning = false;
                _context.NotifyNodeExecuted(node);
                LogBus.Debug("Engine", $"========== 节点 [{node.DisplayName}] 处理结束 ==========");
                return true;
            }
            catch (OperationCanceledException)
            {
                node.IsRunning = false;
                LogBus.Warn("Engine", $"节点 [{node.DisplayName}] 执行被取消。");
                throw;
            }
            catch (Exception ex)
            {
                node.IsRunning = false;
                LogBus.Error("Engine", $"⚠️ 节点 [{node.DisplayName}] 运行时异常: {ex.Message}", ex);
                node.HasError = true;
                bool handled = _context.RaiseExecutionError(node, ex);
                if (handled)
                {
                    LogBus.Info("Engine", $"🛡️ 异常已被 TryCatch 节点接管处理。");
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 预置节点输入数据（从入站连线拉取数据）
        /// </summary>
        private void PrepareNodeInputs(FlowNodeBase node)
        {
            var incomingConnections = _executionChain.Connections.Where(c => c.TargetNode == node).ToList();
            LogBus.Debug("Engine", $"节点 [{node.DisplayName}] 正在准备输入数据，入站连线数: {incomingConnections.Count}");

            foreach (var conn in incomingConnections)
            {
                _context.PassDataThroughConnection(conn);

                if (conn.SourcePort != null && conn.TargetPort != null)
                {
                    conn.TargetPort.DataValue = conn.SourcePort.DataValue;
                    LogBus.Debug("Engine", $"[输入绑定] 映射 {conn.SourceNode?.DisplayName}.{conn.SourcePort.PortName} -> {conn.TargetPort.PortName}，当前值: {conn.TargetPort.DataValue ?? "null"}");
                }
            }
        }

        /// <summary>
        /// 传播节点输出数据
        /// </summary>
        private void PropagateNodeOutputs(FlowNodeBase node)
        {
            LogBus.Debug("Engine", $"正在推送节点 [{node.DisplayName}] 的输出端口数据，端口数: {node.OutputPorts?.Count ?? 0}");

            if (node.OutputPorts == null) return;

            foreach (var port in node.OutputPorts)
            {
                if (port.DataValue == null)
                {
                    var cachedVal = _context.GetPortValue(port.PortId);
                    if (cachedVal != null)
                    {
                        port.DataValue = cachedVal;
                    }
                }

                if (port.DataValue == null)
                {
                    LogBus.Warn("Engine", $"节点 [{node.DisplayName}] 输出端口 [{port.PortName}] 的 DataValue 为 null！");
                }
                else
                {
                    LogBus.Debug("Engine", $"[输出广播] 端口 [{port.PortName}] 数据已就绪 (类型: {port.DataValue.GetType().Name})");
                }

                _context.SetPortValue(port.PortId, port.DataValue);
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            State = ExecutionMode.Stopped;
            foreach (var n in _executionChain.Nodes) n.IsRunning = false;
            LogBus.Info("Engine", "引擎已停止。");
        }

        public void ResetIndex()
        {
            _currentStepIndex = 0;
            LogBus.Debug("Engine", "执行索引已重置。");
        }
    }
}