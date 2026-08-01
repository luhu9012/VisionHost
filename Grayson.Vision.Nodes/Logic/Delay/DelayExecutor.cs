using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.Logic.Delay
{
    [Node(
        type: NodeType.Delay,
        category: NodeCategory.Logic,
        displayName: "延时等待",
        description: "线程休眠等待指定毫秒数",
        parameterType: typeof(DelayParam)
         , icon: "??"
    )]
    [NodePort("ExecIn", PortType.In, PortCategory.Data)]
    [NodePort("ExecOut", PortType.Out, PortCategory.Data)]
    [NodePort("DelayMsIn", PortType.In, PortCategory.Data, dataType: "Integer", colorHex: "#F1C40F")]
    public class DelayExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            var param = node.ParameterModel as DelayParam;
            if (param == null) throw new InvalidOperationException($"节点 [{node.DisplayName}] 参数未配置。");

            int ms = param.DelayMs;
            int? inputMs = context.GetInputValue<int?>(node, "DelayMsIn");
            if (inputMs.HasValue && inputMs.Value >= 0) ms = inputMs.Value;

            context.Log($"?? [延时开始] 等待 {ms} ms...");

            // 正确配合 Token 进行非阻塞异步等待
            await Task.Delay(ms, token);

            context.Log($"? [延时结束]");
        }
    }
}