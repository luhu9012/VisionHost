using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.Logic.ForLoop
{
    [Node(
        type: NodeType.ForLoop,
        category: NodeCategory.Logic,
        displayName: "循环控制",
        description: "用于多目标/阵列产品的遍历处理，支持迭代状态输出与Break打断",
        parameterType: typeof(ForLoopParam)
          , icon: "🔁"
    )]
    [NodePort("ExecIn", PortType.In, PortCategory.Data)]
    [NodePort("LoopBody", PortType.Out, PortCategory.Data)]  // 每轮循环触发
    [NodePort("Completed", PortType.Out, PortCategory.Data)] // 循环结束触发
    [NodePort("CountIn", PortType.In, PortCategory.Data, dataType: "Integer", colorHex: "#F1C40F")]
    [NodePort("Index", PortType.Out, PortCategory.Data, dataType: "Integer", colorHex: "#F1C40F")]
    [NodePort("IsLast", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    public class ForLoopExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            var param = node.ParameterModel as ForLoopParam;
            if (param == null) throw new InvalidOperationException($"节点 [{node.DisplayName}] 参数未配置。");

            int totalCount = param.Count;
            int? inputCount = context.GetInputValue<int?>(node, "CountIn");
            if (inputCount.HasValue && inputCount.Value > 0) totalCount = inputCount.Value;

            // 获取当前迭代状态
            int currentIdx = context.GetState<int>(node, "CurrentIndex", param.StartIndex);
            bool isBreak = context.GetState<bool>(node, "IsBreakRequested", false);

            int endIdx = param.StartIndex + totalCount;

            // 检查是否被打断或已达到循环上限
            if (!isBreak && currentIdx < endIdx)
            {
                bool isLastItem = (currentIdx + param.Step >= endIdx);

                context.SetOutputValue(node, "Index", currentIdx);
                context.SetOutputValue(node, "IsLast", isLastItem);

                context.Log($"🔄 [ForLoop 迭代] Index: {currentIdx} / {endIdx - 1} {(isLastItem ? "(最后一轮)" : "")}");

                // 激活 LoopBody 分支
                context.SetActiveNextPort(node, "LoopBody");

                // 累加 Index，保留状态供下一次调度评估
                context.SetState(node, "CurrentIndex", currentIdx + param.Step);
            }
            else
            {
                // 循环完成或被 Break，重置内部状态并走 Completed 分支
                context.SetState(node, "CurrentIndex", param.StartIndex);
                context.SetState(node, "IsBreakRequested", false);

                context.Log(isBreak ? $"⏹️ [ForLoop 强行中断(Break)]" : $"✔ [ForLoop 循环完成]");
                context.SetActiveNextPort(node, "Completed");
            }

            await Task.CompletedTask;
        }
    }
}