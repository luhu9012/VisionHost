using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.DeviceIO.LightControl
{
    [Node(
        type: NodeType.LightControl,
        category: NodeCategory.DeviceIO,
        displayName: " 光照控制",
        description: "调控光源控制器通道亮度",
        parameterType: typeof(LightControlParam)
         , icon: "??"
    )]
    //[NodePort("ExecIn", PortType.In, PortCategory.Exec)]
    //[NodePort("ExecOut", PortType.Out, PortCategory.Exec)]
    [NodePort("BrightnessIn", PortType.In, PortCategory.Data, dataType: "Integer", colorHex: "#F1C40F")]
    public class LightControlExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            var param = node.ParameterModel as LightControlParam;
            if (param == null) throw new InvalidOperationException($"节点 [{node.DisplayName}] 参数缺失。");

            int brightness = param.Brightness;
            int? inputBrightness = context.GetInputValue<int?>(node, "BrightnessIn");
            if (inputBrightness.HasValue) brightness = inputBrightness.Value;

            context.Log($"?? [光源控制] 控制器: {param.ControllerAlias}, 通道: {param.Channel}, 开关: {param.TurnOn}, 亮度: {brightness}");

            await Task.Delay(30, token); // 模拟串口/网口调光指令延迟

            context.Log($"? [光源控制成功] 通道 {param.Channel} 设定亮度为 {brightness}");
        }
    }
}