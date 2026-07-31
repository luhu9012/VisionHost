using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.Logic.WaitSignal
{
    [Node(
        type: NodeType.WaitSignal,
        category: NodeCategory.Logic,
        displayName: "状态信号等待",
        description: "阻塞等待外部 IO/PLC 或系统全局触发信号",
        parameterType: typeof(WaitSignalParam)
        , icon: "⏳"
    )]
    [NodePort("ExecIn", PortType.In, PortCategory.Data)]
    [NodePort("ExecOut", PortType.Out, PortCategory.Data)]
    [NodePort("TimeoutExec", PortType.Out, PortCategory.Data)] // 超时未等到信号时走此分支
    [NodePort("SignalState", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    public class WaitSignalExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            var param = node.ParameterModel as WaitSignalParam;
            if (param == null) throw new InvalidOperationException($"节点 [{node.DisplayName}] 参数未配置。");

            context.Log($"⏳ [等待信号] 监听信号: {param.SignalName}, 期待状态: {param.TargetState}, 超时设定: {param.TimeoutMs}ms");

            var sw = Stopwatch.StartNew();
            bool isSuccess = false;

            while (sw.ElapsedMilliseconds < param.TimeoutMs)
            {
                token.ThrowIfCancellationRequested();

                // 💡 架构建议：从 Context 的全局共享内存/IO总线中查询信号状态
                // bool currentSignal = context.GetGlobalSignal(param.SignalName);
                bool currentSignal = true; // (Demo 阶段模拟信号到达)

                if (currentSignal == param.TargetState)
                {
                    isSuccess = true;
                    break;
                }

                await Task.Delay(param.CheckIntervalMs, token);
            }

            context.SetOutputValue(node, "SignalState", isSuccess);

            if (isSuccess)
            {
                context.Log($"✔ [信号等到了] 耗时: {sw.ElapsedMilliseconds} ms");
                context.SetActiveNextPort(node, "ExecOut");
            }
            else
            {
                context.Log($"⚠️ [等待信号超时] 超过 {param.TimeoutMs} ms 未接收到预期的 [{param.SignalName}] 信号");
                context.SetActiveNextPort(node, "TimeoutExec");
            }
        }
    }
}