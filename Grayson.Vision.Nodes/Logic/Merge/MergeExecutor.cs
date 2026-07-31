using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.Logic.Merge
{
    [Node(
        type: NodeType.Merge,
        category: NodeCategory.Logic,
        displayName: "流程/数据汇聚 (Merge)",
        description: "汇聚分支控制流，并将多路或多轮循环数据整合输出",
        parameterType: typeof(MergeParam)
         , icon: "🔀"
    )]
    //[NodePort("ExecIn1", PortType.In, PortCategory.Exec)]
    //[NodePort("ExecIn2", PortType.In, PortCategory.Exec)]
    //[NodePort("ExecIn3", PortType.In, PortCategory.Exec)]
    //[NodePort("ExecOut", PortType.Out, PortCategory.Exec)]
    [NodePort("DataIn1", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#E67E22")]
    [NodePort("DataIn2", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#E67E22")]
    [NodePort("DataOut", PortType.Out, PortCategory.Data, dataType: "Object", colorHex: "#E67E22")]
    [NodePort("ListOut", PortType.Out, PortCategory.Data, dataType: "List", colorHex: "#9B59B6")]
    public class MergeExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            var param = node.ParameterModel as MergeParam;
            if (param == null) throw new InvalidOperationException($"节点 [{node.DisplayName}] 参数未配置。");

            object val1 = context.GetInputValue<object>(node, "DataIn1");
            object val2 = context.GetInputValue<object>(node, "DataIn2");

            // 获取或初始化 List 缓存
            var accumulatedList = context.GetState<List<object>>(node, "AccumulatedList", new List<object>());

            object finalResult = null;

            switch (param.DataMode)
            {
                case MergeDataMode.FirstNonNull:
                    finalResult = val1 ?? val2;
                    break;

                case MergeDataMode.PassThrough:
                    finalResult = val2 ?? val1;
                    break;

                case MergeDataMode.CollectList:
                    if (val1 != null) accumulatedList.Add(val1);
                    if (val2 != null) accumulatedList.Add(val2);
                    finalResult = accumulatedList;
                    break;
            }

            // 更新状态与输出
            context.SetState(node, "AccumulatedList", accumulatedList);
            context.SetOutputValue(node, "DataOut", finalResult);
            context.SetOutputValue(node, "ListOut", accumulatedList);

            context.Log($"🔀 [Merge 汇聚] 模式: {param.DataMode}, 当前收集总数: {accumulatedList.Count}");

            // 统一向下激活控制流
            context.SetActiveNextPort(node, "ExecOut");

            await Task.CompletedTask;
        }
    }
}