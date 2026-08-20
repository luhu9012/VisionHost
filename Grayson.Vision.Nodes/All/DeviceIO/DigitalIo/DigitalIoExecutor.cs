using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.DeviceIO.DigitalIo
{
    [Node(NodeType.MotionCardIo, NodeCategory.DeviceIO, typeof(DigitalIoParam))]
    [NodePort("StateIn", PortType.In, PortCategory.Data, dataType: "Boolean", colorHex: "#7C3AED", isRequired:false)]
    [NodePort("StateOut", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    [NodePort("Success", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#3498DB")]
    public class DigitalIoExecutor : NodeExecutorBase<DigitalIoParam>
    {
        public const string PORT_IN_STATE = "StateIn";
        public const string PORT_OUT_STATE = "StateOut";
        public const string PORT_OUT_SUCCESS = "Success";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, DigitalIoParam param, NodeExecutionContext context, CancellationToken token)
        {
            IMotionCard motionCard = context.GetHardware<IMotionCard>(param.CardAlias);
            if (motionCard == null)
            {
                context.Log($"❌ [DigitalIO] 错误: 未找到别名为 [{param.CardAlias}] 的运动控制卡");
                context.SetOutputValue(node, PORT_OUT_SUCCESS, false);
                return;
            }

            if (param.IsOutput)
            {
                // ===== 写 Output 模式 =====
                bool? inputVal = context.GetInputValue<bool?>(node, PORT_IN_STATE, null);
                bool targetState = inputVal.HasValue ? inputVal.Value : param.OutState;

                context.Log($"🔌 [DigitalIO] 正在将 Output [{param.IoIndex}] 设置为: {targetState}");
                var result = motionCard.SetOutput(param.IoIndex, targetState);

                context.SetOutputValue(node, PORT_OUT_SUCCESS, result.Success);
                context.SetOutputValue(node, PORT_OUT_STATE, targetState);
            }
            else
            {
                // ===== 读 Input 模式 =====
                context.Log($"🔌 [DigitalIO] 正在读取 Input [{param.IoIndex}] 状态...");
                var result = motionCard.GetInput(param.IoIndex);

                context.SetOutputValue(node, PORT_OUT_SUCCESS, result.Success);
                if (result.Success)
                {
                    context.SetOutputValue(node, PORT_OUT_STATE, result.Data);
                    context.Log($"✅ [DigitalIO] Input [{param.IoIndex}] 读取结果: {result.Data}");
                }
                else
                {
                    context.Log($"❌ [DigitalIO] 读取 Input [{param.IoIndex}] 失败: {result.Message}");
                }
            }
            await Task.CompletedTask;
        }
    }
}