using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.DeviceIO.AxisReadPos
{
    /// <summary>
    /// 轴位置读取执行器
    /// 查询单轴当前指令位置(DPOS)或编码器反馈位置(MPOS)、运动状态、报警标志
    /// 不执行任何运动，纯读取
    /// </summary>
    [Node(NodeType.MotionCardAxisReadPos, NodeCategory.DeviceIO, typeof(AxisReadPosParam))]
    [NodePort("Position", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#2ECC71")]
    [NodePort("IsIdle", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#3498DB")]
    [NodePort("IsAlarm", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#E74C3C")]
    [NodePort("Success", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#F39C12")]
    public class AxisReadPosExecutor : NodeExecutorBase<AxisReadPosParam>
    {
        public const string PORT_OUT_POSITION = "Position";
        public const string PORT_OUT_IS_IDLE = "IsIdle";
        public const string PORT_OUT_IS_ALARM = "IsAlarm";
        public const string PORT_OUT_SUCCESS = "Success";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, AxisReadPosParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            IMotionCard card = context.GetHardware<IMotionCard>(param.CardAlias);
            if (card == null)
            {
                context.Log($"❌ [AxisReadPos] 未找到运动控制卡 [{param.CardAlias}]");
                context.SetOutputValue(node, PORT_OUT_SUCCESS, false);
                return;
            }

            // 1. 读取位置
            double position = 0;
            if (param.ReadFeedback)
            {
                var res = card.GetFeedbackPosition(param.AxisIndex);
                if (!res.Success)
                {
                    context.Log($"❌ [AxisReadPos] 轴[{param.AxisIndex}] 反馈位置读取失败: {res.Message}");
                    context.SetOutputValue(node, PORT_OUT_SUCCESS, false);
                    return;
                }
                position = res.Data;
            }
            else
            {
                var res = card.GetCommandPosition(param.AxisIndex);
                if (!res.Success)
                {
                    context.Log($"❌ [AxisReadPos] 轴[{param.AxisIndex}] 指令位置读取失败: {res.Message}");
                    context.SetOutputValue(node, PORT_OUT_SUCCESS, false);
                    return;
                }
                position = res.Data;
            }

            // 2. 读取运动状态
            bool isIdle = false;
            var idleRes = card.IsAxisIdle(param.AxisIndex);
            if (idleRes.Success)
                isIdle = idleRes.Data;

            // 3. 读取报警标志
            bool isAlarm = false;
            var statusRes = card.GetAxisStatus(param.AxisIndex);
            if (statusRes.Success)
                isAlarm = statusRes.Data.HasFlag(AxisStatusFlags.Alarm);

            // 4. 输出
            context.SetOutputValue(node, PORT_OUT_POSITION, position);
            context.SetOutputValue(node, PORT_OUT_IS_IDLE, isIdle);
            context.SetOutputValue(node, PORT_OUT_IS_ALARM, isAlarm);
            context.SetOutputValue(node, PORT_OUT_SUCCESS, true);

            context.Log($"📍 [AxisReadPos] 轴[{param.AxisIndex}] 位置={position:F3}, 空闲={isIdle}, 报警={isAlarm}");
        }
    }
}
