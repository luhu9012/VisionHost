using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.Logging;

namespace Grayson.Vision.Contracts.Business.Engine.Execution
{
    public enum ExecutionMode
    {
        Continuous,
        Step,
        Paused,
        Stopped
    }

    /// <summary>
    /// 独立流程执行引擎（适配纯数据流驱动/无 Exec 端口架构）
    /// </summary>
    public class FlowExecutor
    {
        private readonly FlowProcessModel _process;
        private readonly ExecutionContext _context;
        private CancellationTokenSource _cts;

        private int _currentStepIndex = 0;
        public ExecutionMode State { get; private set; } = ExecutionMode.Stopped;

        public FlowExecutor(FlowProcessModel process, ExecutionContext context)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// 启动连续运行
        /// </summary>
        public async Task RunContinuousAsync()
        {
            if (State == ExecutionMode.Continuous) return;

            State = ExecutionMode.Continuous;
            _cts = new CancellationTokenSource();
            LogBus.Info("Engine", $"▶ 流程 [{_process.ProcessName}] 开始连续运行...");

            try
            {
                // 每次运行重新计算拓扑执行链
                var execChain = GetExecutableExecutionChain();
                LogBus.Debug("Engine", $"拓扑排序完成，共解析出 {execChain.Count} 个执行节点。");

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
                            break;
                        }
                    }
                    else
                    {
                        LogBus.Debug("Engine", $"⏭ 节点 [{node.DisplayName}] 被禁用，自动跳过。");
                    }

                    _currentStepIndex++;
                }

                LogBus.Info("Engine", $"✔ 流程 [{_process.ProcessName}] 执行完毕。");
            }
            catch (OperationCanceledException)
            {
                LogBus.Info("Engine", "⏹ 流程已被手动停止。");
            }
            catch (Exception ex)
            {
                LogBus.Error("Engine", $"❌ 引擎致命错误: {ex.Message}", ex);
            }
            finally
            {
                Stop();
            }
        }

        /// <summary>
        /// 单步执行下一个节点
        /// </summary>
        public async Task StepAsync()
        {
            var execChain = GetExecutableExecutionChain();
            if (execChain.Count == 0)
            {
                LogBus.Warn("Engine", "单步执行取消: 当前流程没有可执行节点。");
                return;
            }

            if (_currentStepIndex >= execChain.Count)
            {
                _currentStepIndex = 0;
                LogBus.Info("Engine", "🔄 单步执行到达末尾，自动重置回起点。");
            }

            State = ExecutionMode.Step;
            _cts = new CancellationTokenSource();

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

            State = ExecutionMode.Paused;
        }

        /// <summary>
        /// 带数据绑定与异常捕获的单个节点执行
        /// </summary>
        private async Task<bool> ExecuteNodeWithHandlingAsync(FlowNodeBase node, CancellationToken token)
        {
            node.IsRunning = true;
            _context.NotifyNodeExecuting(node);
            LogBus.Debug("Engine", $"========== 开始执行节点 [{node.DisplayName}] (ID: {node.NodeId}) ==========");

            try
            {
                // 1. 数据绑定：提取上游输入数据（沿着依赖连线拉取数据）
                PrepareNodeInputs(node);

                // 🌟 核心修复：构建节点的单次运行上下文，真正调用 ExecuteCoreAsync()
                var nodeExecContext = new NodeExecutionContext(_context, new FrameCycleContext());

                LogBus.Debug("Engine", $"正在触发节点 [{node.DisplayName}] 的核心业务逻辑 (ExecuteAsync)...");
                // 🌟 方式 A：如果 node 本身实现了 INodeExecutor 或继承自 NodeExecutorBase
                // 判断 node 的 Type 是否直接或间接实现了 INodeExecutor 接口
                // 🌟 核心修改：判断 node.Executor 是否实现了 INodeExecutor
                if (node.Executor is INodeExecutor executor)
                {
                    await executor.ExecuteAsync(node, nodeExecContext, token);
                    LogBus.Info("Engine", $"节点 [{node.DisplayName}] 核心逻辑执行成功。");
                }
                else if (node.ExecutorType != null)
                {
                    // 如果挂载的是 Type，动态实例化
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

                // 触发事件总线处理异常
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
        /// 预置节点输入数据（从所有入站连线拉取上游数据）
        /// </summary>
        private void PrepareNodeInputs(FlowNodeBase node)
        {
            var incomingConnections = _process.Connections.Where(c => c.TargetNode == node).ToList();
            LogBus.Debug("Engine", $"节点 [{node.DisplayName}] 正在准备输入数据，入站连线数: {incomingConnections.Count}");

            foreach (var conn in incomingConnections)
            {
                // 传输数据并对 TargetPort 赋值
                _context.PassDataThroughConnection(conn);

                // 同步赋值 targetPort.DataValue，防止 ViewModel 数据不同步
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
                // 🌟【关键修复】：如果在算子内部没同步设置 port.DataValue，从 Context 尝试同步回填
                if (port.DataValue == null)
                {
                    var cachedVal = _context.GetPortValue(port.PortId);
                    if (cachedVal != null)
                    {
                        port.DataValue = cachedVal;
                    }
                }

                // 再次检查校验
                if (port.DataValue == null)
                {
                    LogBus.Warn("Engine", $"节点 [{node.DisplayName}] 输出端口 [{port.PortName}] 的 DataValue 为 null！");
                }
                else
                {
                    LogBus.Debug("Engine", $"[输出广播] 端口 [{port.PortName}] 数据已就绪 (类型: {port.DataValue.GetType().Name})");
                }

                // 确保 Context 字典中包含了该端口值
                _context.SetPortValue(port.PortId, port.DataValue);
            }
        }

        /// <summary>
        /// 基于数据拓扑依赖关系的 Kahn 算法构建执行链条（不再依赖 Exec 控制线）
        /// </summary>
        private List<FlowNodeBase> GetExecutableExecutionChain()
        {
            var nodes = _process.Nodes.ToList();
            var connections = _process.Connections.Where(c => c.SourceNode != null && c.TargetNode != null).ToList();

            // 计算每个节点的入度 (In-degree)
            var inDegree = nodes.ToDictionary(n => n, n => 0);
            foreach (var conn in connections)
            {
                if (inDegree.ContainsKey(conn.TargetNode))
                {
                    inDegree[conn.TargetNode]++;
                }
            }

            // 存放入度为 0 的节点（起始/根节点）
            var queue = new Queue<FlowNodeBase>(nodes.Where(n => inDegree[n] == 0));
            var resultChain = new List<FlowNodeBase>();

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                resultChain.Add(curr);

                // 找到以当前节点为源的所有连线的下游节点
                var outgoingConnections = connections.Where(c => c.SourceNode == curr);
                foreach (var conn in outgoingConnections)
                {
                    var target = conn.TargetNode;
                    if (inDegree.ContainsKey(target))
                    {
                        inDegree[target]--;
                        if (inDegree[target] == 0)
                        {
                            queue.Enqueue(target);
                        }
                    }
                }
            }

            // 兜底校验：如果流程图存在死循环/环形连线导致部分节点入度无法归零，将未处理节点强制拼接到末尾
            if (resultChain.Count < nodes.Count)
            {
                LogBus.Warn("Engine", "流程中检测到可能的环形依赖，部分未排序节点将强制追加至末尾。");
                foreach (var node in nodes)
                {
                    if (!resultChain.Contains(node))
                    {
                        resultChain.Add(node);
                    }
                }
            }

            return resultChain;
        }

        public void Stop()
        {
            _cts?.Cancel();
            State = ExecutionMode.Stopped;
            foreach (var n in _process.Nodes) n.IsRunning = false;
            LogBus.Info("Engine", "引擎已停止。");
        }

        public void ResetIndex()
        {
            _currentStepIndex = 0;
            LogBus.Debug("Engine", "执行索引已重置。");
        }
    }
}