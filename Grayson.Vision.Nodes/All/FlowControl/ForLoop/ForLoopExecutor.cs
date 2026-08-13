using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.FlowControl.ForLoop
{
    [Node(NodeType.ForLoop, NodeCategory.FlowControl, typeof(ForLoopParam))]
    [NodePort("CountIn", PortType.In, PortCategory.Data, dataType: "Int32", colorHex: "#3498DB")]
    [NodePort("Index", PortType.Out, PortCategory.Data, dataType: "Int32", colorHex: "#F39C12")]
    [NodePort("LoopBody", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    [NodePort("Completed", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#9B59B6")]
    public class ForLoopExecutor : NodeExecutorBase<ForLoopParam>
    {
        public const string PORT_IN_COUNT = "CountIn";
        public const string PORT_OUT_INDEX = "Index";
        public const string PORT_OUT_LOOP_BODY = "LoopBody";
        public const string PORT_OUT_COMPLETED = "Completed";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ForLoopParam param, NodeExecutionContext context, CancellationToken token)
        {
            // 优先获取动态绑定的输入 Count，若未绑定则使用参数面板里的 Count
            object dynamicCountObj = context.GetInputValue<object>(node, PORT_IN_COUNT, null);
            int totalCount = param.Count;

            if (dynamicCountObj != null && int.TryParse(dynamicCountObj.ToString(), out int parsedCount))
            {
                totalCount = parsedCount;
            }

            int step = Math.Max(1, param.Step);
            int start = param.StartIndex;
            int currentIteration = 0;

            context.Log($"🔄 [ForLoop] 开始循环: 起始值={start}, 迭代总数={totalCount}, 步长={step}");

            for (int i = 0; i < totalCount; i++)
            {
                token.ThrowIfCancellationRequested();

                int currentIndex = start + (i * step);
                currentIteration++;

                // 1. 更新当前索引数据
                context.SetOutputValue(node, PORT_OUT_INDEX, currentIndex);
                // 2. 触发循环体 (LoopBody 输出信号/真值)
                context.SetOutputValue(node, PORT_OUT_LOOP_BODY, true);

                context.Log($"🔄 [ForLoop] 迭代 [{currentIteration}/{totalCount}] -> 当前 Index={currentIndex}");

                // 若引擎包含下游节点同步调度的能力，可在此处 await 下游分支执行完毕
                await Task.Yield();
            }

            // 循环结束：重置/关闭 LoopBody 信号，激活 Completed 端口
            context.SetOutputValue(node, PORT_OUT_LOOP_BODY, false);
            context.SetOutputValue(node, PORT_OUT_COMPLETED, true);

            context.Log($"✅ [ForLoop] 循环完成，共执行 {currentIteration} 次迭代。");
        }
    }
}