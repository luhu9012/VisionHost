using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.FlowControl.WaitSignal
{
    [Node(NodeType.WaitSignal, NodeCategory.FlowControl, typeof(WaitSignalParam))]
    [NodePort("SignalIn", PortType.In, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    [NodePort("Success", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    [NodePort("Timeout", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#E74C3C")]
    public class WaitSignalExecutor : NodeExecutorBase<WaitSignalParam>
    {
        public const string PORT_IN_SIGNAL = "SignalIn";
        public const string PORT_OUT_SUCCESS = "Success";
        public const string PORT_OUT_TIMEOUT = "Timeout";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, WaitSignalParam param, NodeExecutionContext context, CancellationToken token)
        {
            int timeoutMs = param.TimeoutMs;
            int intervalMs = Math.Max(10, param.PollIntervalMs);
            bool expectedState = param.ExpectedState;

            context.Log($"🚥 [WaitSignal] 开始等待信号 [{param.SignalName}] -> 期待值: {expectedState}, 超时设定: {(timeoutMs > 0 ? timeoutMs + "ms" : "无限等待")}");

            Stopwatch stopwatch = Stopwatch.StartNew();
            bool isSuccess = false;
            bool isTimeout = false;

            while (!token.IsCancellationRequested)
            {
                // 从上下文或动态端口读取当前信号状态
                // 1. 优先读取输入端口上连线的数据值
                // 2. 如果端口没连线，也可以从 context 全局变量表里查询 param.SignalName
                bool currentState = context.GetInputValue<bool>(node, PORT_IN_SIGNAL, false);

                if (currentState == expectedState)
                {
                    isSuccess = true;
                    break;
                }

                // 检查是否超时
                if (timeoutMs > 0 && stopwatch.ElapsedMilliseconds >= timeoutMs)
                {
                    isTimeout = true;
                    break;
                }

                await Task.Delay(intervalMs, token);
            }

            stopwatch.Stop();

            if (isSuccess)
            {
                context.Log($"✅ [WaitSignal] 信号 [{param.SignalName}] 已触发，耗时: {stopwatch.ElapsedMilliseconds} ms");
                context.SetOutputValue(node, PORT_OUT_SUCCESS, true);
                context.SetOutputValue(node, PORT_OUT_TIMEOUT, false);
            }
            else if (isTimeout)
            {
                context.Log($"⚠️ [WaitSignal] 等待信号 [{param.SignalName}] 超时！已等待 {stopwatch.ElapsedMilliseconds} ms");
                context.SetOutputValue(node, PORT_OUT_SUCCESS, false);
                context.SetOutputValue(node, PORT_OUT_TIMEOUT, true);
            }

            await Task.CompletedTask;
        }
    }
}