using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.FlowControl.WhileLoop
{
    [Node(NodeType.WhileLoop, NodeCategory.FlowControl, typeof(WhileLoopParam))]
    [NodePort("Condition", PortType.In, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    [NodePort("Index", PortType.Out, PortCategory.Data, dataType: "Int32", colorHex: "#F39C12")]
    [NodePort("LoopBody", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    [NodePort("Completed", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#9B59B6")]
    public class WhileLoopExecutor : NodeExecutorBase<WhileLoopParam>
    {
        public const string PORT_IN_CONDITION = "Condition";
        public const string PORT_OUT_INDEX = "Index";
        public const string PORT_OUT_LOOP_BODY = "LoopBody";
        public const string PORT_OUT_COMPLETED = "Completed";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, WhileLoopParam param, NodeExecutionContext context, CancellationToken token)
        {
            int iterationCount = 0;
            int maxIterations = Math.Max(1, param.MaxIterations);

            context.Log($"🔁 [WhileLoop] 开始条件循环，最大防呆上限={maxIterations} 次");

            while (iterationCount < maxIterations)
            {
                token.ThrowIfCancellationRequested();

                // 评估输入端口的布尔条件（若未连接或为空则默认取 false）
                bool condition = context.GetInputValue<bool>(node, PORT_IN_CONDITION, false);

                // 如果是标准 While 模式（CheckAtStart），在迭代开始前校验条件
                if (param.CheckConditionAtStart && !condition && iterationCount == 0)
                {
                    context.Log($"🔁 [WhileLoop] 初始条件为 False，跳过循环。");
                    break;
                }

                if (iterationCount > 0 && !condition)
                {
                    context.Log($"🔁 [WhileLoop] 条件不满足 (False)，退出循环。当前迭代次数: {iterationCount}");
                    break;
                }

                // 1. 输出当前迭代索引 (从 0 开始)
                context.SetOutputValue(node, PORT_OUT_INDEX, iterationCount);
                // 2. 触发循环体引脚
                context.SetOutputValue(node, PORT_OUT_LOOP_BODY, true);

                iterationCount++;
                context.Log($"🔁 [WhileLoop] 执行循环体迭代 [{iterationCount}/{maxIterations}]");

                // 可选的每次循环间隔延时
                if (param.IntervalMs > 0)
                {
                    await Task.Delay(param.IntervalMs, token);
                }
                else
                {
                    await Task.Yield();
                }
            }

            if (iterationCount >= maxIterations)
            {
                context.Log($"⚠️ [WhileLoop] 已达到最大限制迭代次数 [{maxIterations}]，触发强行防呆退出！");
            }

            // 退出循环：关闭 LoopBody 信号，触发 Completed 端口
            context.SetOutputValue(node, PORT_OUT_LOOP_BODY, false);
            context.SetOutputValue(node, PORT_OUT_COMPLETED, true);

            context.Log($"✅ [WhileLoop] 循环完成，累计迭代 {iterationCount} 次。");
        }
    }
}