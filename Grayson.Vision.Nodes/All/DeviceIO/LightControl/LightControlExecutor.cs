using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.DeviceIO.LightControl
{
    [Node(NodeType.LightControl, NodeCategory.DeviceIO, typeof(LightControlParam))]
    [NodePort("IntensityIn", PortType.In, PortCategory.Data, dataType: "Int32", colorHex: "#F39C12")]
    [NodePort("Success", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    public class LightControlExecutor : NodeExecutorBase<LightControlParam>
    {
        public const string PORT_IN_INTENSITY = "IntensityIn";
        public const string PORT_OUT_IS_SUCCESS = "Success";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, LightControlParam param, NodeExecutionContext context, CancellationToken token)
        {
            ILightController light = context.GetHardware<ILightController>(param.LightAlias);
            if (light == null)
            {
                context.Log($"❌ [LightControl] 错误: 未能在系统中找到别名为 [{param.LightAlias}] 的光源设备对象。");
                context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, false);
                return;
            }

            // 支持通过端口动态覆盖亮度参数
            object dynamicIntensityObj = context.GetInputValue<object>(node, PORT_IN_INTENSITY, null);
            int finalIntensity = param.Intensity;

            if (dynamicIntensityObj != null && int.TryParse(dynamicIntensityObj.ToString(), out int parsedVal))
            {
                //finalIntensity = Math.Clamp(parsedVal, 0, 255);
            }

            context.Log($"💡 [LightControl] 调节光源 [{param.LightAlias}] - 通道: {param.Channel}, 状态: {(param.TurnOn ? "开启" : "关闭")}, 亮度: {finalIntensity}");

            bool Success = true;

            // 1. 设置开关状态
            var switchRes = light.TurnChannel(param.Channel, param.TurnOn);
            if (!switchRes.Success)
            {
                context.Log($"⚠️ [LightControl] 切换通道状态失败: {switchRes.Message}");
                Success = false;
            }

            // 2. 如果是开启状态，调控对应亮度
            if (param.TurnOn && Success)
            {
                var intensityRes = light.SetIntensity(param.Channel, finalIntensity);
                if (!intensityRes.Success)
                {
                    context.Log($"❌ [LightControl] 设置亮度失败: {intensityRes.Message}");
                    Success = false;
                }
            }

            context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, Success);

            if (Success)
            {
                context.Log($"✅ [LightControl] 光源通道 {param.Channel} 调控完成。");
            }

            await Task.CompletedTask;
        }
    }
}