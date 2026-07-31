using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.Logic.SwitchCase
{
    [Node(
        type: NodeType.SwitchCase,
        category: NodeCategory.Logic,
        displayName: "多路分支",
        description: "根据整型/字符串变量分发多条线路",
        parameterType: typeof(SwitchCaseParam)
        , icon: "🔀"
    )]
    //[NodePort("ExecIn", PortType.In, PortCategory.Data)]
    [NodePort("Case_0", PortType.Out, PortCategory.Data)]
    [NodePort("Case_1", PortType.Out, PortCategory.Data)]
    [NodePort("Default", PortType.Out, PortCategory.Data)]
    [NodePort("SelectIn", PortType.In, PortCategory.Data, dataType: "String", colorHex: "#E74C3C")]
    public class SwitchCaseExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            var param = node.ParameterModel as SwitchCaseParam;
            if (param == null) throw new InvalidOperationException($"节点 [{node.DisplayName}] 参数未配置。");

            string selector = context.GetInputValue<string>(node, "SelectIn") ?? param.SelectorKey;

            context.Log($"🔀 [Switch 选择] 当前匹配键: '{selector}'");

            // 根据Selector匹配端口，找不到则走 Default
            if (selector == "Type_A")
                context.SetActiveNextPort(node, "Case_0");
            else if (selector == "Type_B")
                context.SetActiveNextPort(node, "Case_1");
            else
                context.SetActiveNextPort(node, "Default");

            await Task.CompletedTask;
        }
    }
}