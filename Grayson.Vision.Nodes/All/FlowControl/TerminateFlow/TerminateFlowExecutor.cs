using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.FlowControl.TerminateFlow
{
    [Node(NodeType.TerminateFlow, NodeCategory.FlowControl, typeof(TerminateFlowParam))]
    [NodePort("Trigger", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#E74C3C")]
    [NodePort("ReasonIn", PortType.In, PortCategory.Data, dataType: "String", colorHex: "#3498DB")]
    public class TerminateFlowExecutor : NodeExecutorBase<TerminateFlowParam>
    {
        public const string PORT_IN_TRIGGER = "Trigger";
        public const string PORT_IN_REASON = "ReasonIn";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, TerminateFlowParam param, NodeExecutionContext context, CancellationToken token)
        {
            // 优先获取动态绑定的异常原因说明，若未绑定则使用参数面板里的配置
            string dynamicReason = context.GetInputValue<string>(node, PORT_IN_REASON, null);
            string finalReason = !string.IsNullOrWhiteSpace(dynamicReason) ? dynamicReason : param.Reason;

            if (param.IsError)
            {
                context.Log($"🛑 [TerminateFlow] 捕获强行终止信号！原因: {finalReason} (ExitCode: {param.ExitCode})");

                // 方式 1: 标记上下文或触发 FlowEngine 的终止标志（根据你的 Context 架构）
                // context.IsTerminated = true;

                // 方式 2: 直接抛出 OperationCanceledException 或自定义终止异常中断执行链
                throw new OperationCanceledException($"[流程终止] {finalReason}");
            }
            else
            {
                context.Log($"🛑 [TerminateFlow] 流程正常提前结束。原因: {finalReason}");
                // 正常提前退出逻辑，可以中断后续执行流
            }

            await Task.CompletedTask;
        }
    }
}