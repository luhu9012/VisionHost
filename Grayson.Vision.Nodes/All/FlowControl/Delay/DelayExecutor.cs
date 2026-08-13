using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.FlowControl.Delay
{
    [Node(NodeType.Delay, NodeCategory.FlowControl, typeof(DelayParam))]
    [NodePort("Input", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#7C3AED")]
    [NodePort("DelayTime", PortType.In, PortCategory.Data, dataType: "Int32", colorHex: "#3498DB")]
    [NodePort("Output", PortType.Out, PortCategory.Data, dataType: "Object", colorHex: "#2ECC71")]
    public class DelayExecutor : NodeExecutorBase<DelayParam>
    {
        public const string PORT_IN_DATA = "Input";
        public const string PORT_IN_DELAY_TIME = "DelayTime";
        public const string PORT_OUT_DATA = "Output";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, DelayParam param, NodeExecutionContext context, CancellationToken token)
        {
            // 优先读取动态输入的 DelayTime，若未连线或无效则使用参数面板设置的值
            object dynamicTimeObj = context.GetInputValue<object>(node, PORT_IN_DELAY_TIME, null);
            int delayMs = param.DelayMs;

            if (dynamicTimeObj != null && int.TryParse(dynamicTimeObj.ToString(), out int parsedMs))
            {
                delayMs = Math.Max(0, parsedMs);
            }

            // 获取透传数据
            object inputVal = context.GetInputValue<object>(node, PORT_IN_DATA, null);

            context.Log($"⏳ [Delay] 开始延时等待: {delayMs} ms...");

            if (delayMs > 0)
            {
                // 使用非阻塞的异步 Task.Delay，并支持 CancellationToken 响应流程强行停止
                await Task.Delay(delayMs, token);
            }

            // 延时完成后透传数据并向下游继续触发
            context.SetOutputValue(node, PORT_OUT_DATA, inputVal);
            context.Log($"✅ [Delay] 延时 {delayMs} ms 完成，恢复流程流转。");
        }
    }
}