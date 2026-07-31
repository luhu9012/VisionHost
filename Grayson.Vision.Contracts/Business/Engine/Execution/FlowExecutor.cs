using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Business.Models;

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
            _context.Log($"▶ 流程 [{_process.ProcessName}] 开始连续运行...");

            try
            {
                // 每次运行重新计算拓扑执行链
                var execChain = GetExecutableExecutionChain();

                while (_currentStepIndex < execChain.Count && State == ExecutionMode.Continuous)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var node = execChain[_currentStepIndex];
                    if (node.Enable)
                    {
                        bool success = await ExecuteNodeWithHandlingAsync(node, _cts.Token);
                        if (!success)
                        {
                            _context.Log($"🛑 节点 [{node.DisplayName}] 执行中断，中止流程。");
                            break;
                        }
                    }

                    _currentStepIndex++;
                }

                _context.Log($"✔ 流程 [{_process.ProcessName}] 执行完毕。");
            }
            catch (OperationCanceledException)
            {
                _context.Log("⏹ 流程已被手动停止。");
            }
            catch (Exception ex)
            {
                _context.Log($"❌ 引擎致命错误: {ex.Message}");
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
            if (execChain.Count == 0) return;

            if (_currentStepIndex >= execChain.Count)
            {
                _currentStepIndex = 0;
                _context.Log("🔄 单步执行到达末尾，自动重置回起点。");
            }

            State = ExecutionMode.Step;
            _cts = new CancellationTokenSource();

            while (_currentStepIndex < execChain.Count)
            {
                var node = execChain[_currentStepIndex];
                if (node.Enable)
                {
                    _context.Log($"⏯ [单步 {_currentStepIndex + 1}/{execChain.Count}] 节点: {node.DisplayName}");
                    await ExecuteNodeWithHandlingAsync(node, _cts.Token);
                    _currentStepIndex++;
                    break;
                }
                else
                {
                    _context.Log($"⏭ 跳过禁用节点: {node.DisplayName}");
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

            try
            {
                // 1. 数据绑定：提取上游输入数据（沿着依赖连线拉取数据）
                PrepareNodeInputs(node);

                // 2. 模拟/调用实际节点业务逻辑
                await Task.Delay(200, token); // 根据实际需要调整或调用 node.Execute(_context)

                // 3. 将节点的输出写回端口并向下游传播
                PropagateNodeOutputs(node);

                node.IsRunning = false;
                _context.NotifyNodeExecuted(node);
                return true;
            }
            catch (OperationCanceledException)
            {
                node.IsRunning = false;
                throw;
            }
            catch (Exception ex)
            {
                node.IsRunning = false;
                _context.Log($"⚠️ 节点 [{node.DisplayName}] 运行时异常: {ex.Message}");

                // 触发事件总线处理异常
                bool handled = _context.RaiseExecutionError(node, ex);
                if (handled)
                {
                    _context.Log($"🛡️ 异常已被 TryCatch 节点接管处理。");
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
            // 🌟 核心修复：移除 Category == Exec / Data 的判断，直接通过 TargetNode 关联连线
            var incomingConnections = _process.Connections.Where(c => c.TargetNode == node);

            foreach (var conn in incomingConnections)
            {
                // 传输数据并对 TargetPort 赋值
                _context.PassDataThroughConnection(conn);

                // 同步赋值 targetPort.DataValue，防止 ViewModel 数据不同步
                if (conn.SourcePort != null && conn.TargetPort != null)
                {
                    conn.TargetPort.DataValue = conn.SourcePort.DataValue;
                }
            }
        }

        /// <summary>
        /// 传播节点输出数据
        /// </summary>
        private void PropagateNodeOutputs(FlowNodeBase node)
        {
            // 🌟 核心修复：对节点的所有输出端口进行处理，不再过滤 PortCategory.Data
            foreach (var port in node.OutputPorts)
            {
                if (port.DataValue == null)
                {
                    // 若节点业务中未显式设置 DataValue，则填充模拟数据以供测试
                    port.DataValue = $"Data_{node.DisplayName}_{DateTime.Now:ss}";
                }
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
        }

        public void ResetIndex()
        {
            _currentStepIndex = 0;
        }
    }
}