using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.DeviceIO.AxisMove
{
    [Node(
        type: NodeType.AxisMove,
        category: NodeCategory.DeviceIO,
        displayName: "运动轴移动",
        description: "控制伺服/步进轴移动至指定坐标",
        parameterType: typeof(AxisMoveParam)
         , icon: "🚚"
    )]
    //[NodePort("ExecIn", PortType.In, PortCategory.Exec)]
    //[NodePort("ExecOut", PortType.Out, PortCategory.Exec)]
    [NodePort("PositionIn", PortType.In, PortCategory.Data, dataType: "Double", colorHex: "#3498DB")]
    [NodePort("ActualPos", PortType.Out, PortCategory.Data, dataType: "Double", colorHex: "#3498DB")]
    public class AxisMoveExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            var param = node.ParameterModel as AxisMoveParam;
            if (param == null) throw new InvalidOperationException($"节点 [{node.DisplayName}] 参数缺失。");

            double targetPos = param.TargetPosition;
            double? inputPos = context.GetInputValue<double?>(node, "PositionIn");
            if (inputPos.HasValue) targetPos = inputPos.Value;

            string modeStr = param.IsRelative ? "相对" : "绝对";
            context.Log($"🚚 [轴定位开始] 轴: {param.AxisName}, 模式: {modeStr}, 目标位置: {targetPos} mm, 速度: {param.Speed}");

            await Task.Delay(200, token); // 模拟运动控制定位过程

            context.SetOutputValue(node, "ActualPos", targetPos);
            context.Log($"✔ [轴定位完成] 轴 {param.AxisName} 已到达位置 {targetPos}");
        }
    }
}