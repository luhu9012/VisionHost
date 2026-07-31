using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.DeviceIO.DigitalOutput
{
    [Node(
        type: NodeType.DigitalOutput,
        category: NodeCategory.DeviceIO,
        displayName: "数字 IO 输出",
        description: "控制气缸、电磁阀、蜂鸣器等 DO 信号",
        parameterType: typeof(DigitalOutputParam)
         , icon: "🚨"
    )]
    //[NodePort("ExecIn", PortType.In, PortCategory.Exec)]
    //[NodePort("ExecOut", PortType.Out, PortCategory.Exec)]
    [NodePort("SignalIn", PortType.In, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    public class DigitalOutputExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            var param = node.ParameterModel as DigitalOutputParam;
            if (param == null) throw new InvalidOperationException($"节点 [{node.DisplayName}] 参数缺失。");

            bool targetVal = param.OutputValue;
            bool? signalIn = context.GetInputValue<bool?>(node, "SignalIn");
            if (signalIn.HasValue) targetVal = signalIn.Value;

            if (param.IsPulse)
            {
                context.Log($"🚨 [IO 脉冲触发] DO 板卡: {param.IoCardAlias}, 通道: {param.Channel}, 脉冲时长: {param.PulseDurationMs}ms");
                await Task.Delay(param.PulseDurationMs, token);
                context.Log($"✔ [IO 脉冲完成] 通道 {param.Channel} 已归位");
            }
            else
            {
                context.Log($"🚨 [IO 电平状态] DO 板卡: {param.IoCardAlias}, 通道: {param.Channel} <- {targetVal}");
                await Task.Delay(20, token);
            }
        }
    }
}