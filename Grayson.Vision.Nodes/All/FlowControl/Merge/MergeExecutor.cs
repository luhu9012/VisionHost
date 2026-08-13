using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.FlowControl.Merge
{
    [Node(NodeType.Merge, NodeCategory.FlowControl, typeof(MergeParam))]
    [NodePort("In1", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#7C3AED")]
    [NodePort("In2", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#7C3AED")]
    [NodePort("Output", PortType.Out, PortCategory.Data, dataType: "Object", colorHex: "#2ECC71")]
    public class MergeExecutor : NodeExecutorBase<MergeParam>
    {
        public const string PORT_IN_1 = "In1";
        public const string PORT_IN_2 = "In2";
        public const string PORT_OUT = "Output";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, MergeParam param, NodeExecutionContext context, CancellationToken token)
        {
            object in1Val = context.GetInputValue<object>(node, PORT_IN_1, null);
            object in2Val = context.GetInputValue<object>(node, PORT_IN_2, null);

            object mergedResult = null;

            if (param.PassFirstNonNull)
            {
                // 模式 1：获取第一个非 null 的分支输出
                mergedResult = in1Val ?? in2Val;
            }
            else
            {
                // 模式 2：后覆盖前（以最新的有效输入为准）
                mergedResult = in2Val ?? in1Val;
            }

            // 输出汇合后的数据
            context.SetOutputValue(node, PORT_OUT, mergedResult);

            string statusLog = mergedResult != null
                ? $"已接收到有效分支数据: {mergedResult}"
                : "所有输入分支均为空值";

            context.Log($"🔀 [Merge] 分支汇合完成，{statusLog}");

            await Task.CompletedTask;
        }
    }
}