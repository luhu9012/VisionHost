using Grayson.Vision.Contracts.Devices;
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

namespace Grayson.Vision.Nodes.All.DeviceIO.AxisMoveAbs
{
    [Node(NodeType.MotionCardAxisMoveAbs, NodeCategory.DeviceIO, typeof(AxisMoveAbsParam))]
    [NodePort("TargetPosIn", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#7C3AED")]
    [NodePort("CurrentPosOut", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#2ECC71")]
    [NodePort("Success", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#3498DB")]
    public class AxisMoveAbsExecutor : NodeExecutorBase<AxisMoveAbsParam>
    {
        public const string PORT_IN_TARGET_POS = "TargetPosIn";
        public const string PORT_OUT_CURRENT_POS = "CurrentPosOut";
        public const string PORT_OUT_SUCCESS = "Success";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, AxisMoveAbsParam param, NodeExecutionContext context, CancellationToken token)
        {
            IMotionCard card = context.GetHardware<IMotionCard>(param.CardAlias);
            if (card == null)
            {
                context.Log($"❌ [AxisMoveAbs] 错误: 未找到运动控制卡 [{param.CardAlias}][cite: 2]");
                context.SetOutputValue(node, PORT_OUT_SUCCESS, false);
                return;
            }

            // 动态获取目标坐标（如上一级视觉计算得到的定位数据）
            // 用 double? 兼容 CalibrationApply 输出的 double 类型；GetInputValue 直接 is T 匹配，float→double 不通
            double? dynamicPos = context.GetInputValue<double?>(node, PORT_IN_TARGET_POS, null);
            float finalPos = dynamicPos.HasValue ? (float)dynamicPos.Value : param.TargetPosition;

            context.Log($"🚀 [AxisMoveAbs] 轴 [{param.AxisIndex}] 开始绝对定位到: {finalPos}, 速度: {param.Speed}");

            var moveRes = card.MoveAbsolute(param.AxisIndex, finalPos, param.Speed); 
            if (!moveRes.Success)
            {
                context.Log($"❌ [AxisMoveAbs] 发送定位指令失败: {moveRes.Message}");
                context.SetOutputValue(node, PORT_OUT_SUCCESS, false);
                return;
            }

            // 同步等待完成处理
            if (param.WaitUntilDone)
            {
                Stopwatch sw = Stopwatch.StartNew();
                while (!token.IsCancellationRequested)
                {
                    var idleRes = card.IsAxisIdle(param.AxisIndex); 
                    var statusRes = card.GetAxisStatus(param.AxisIndex); 

                    // 校验硬件报警标志
                    if (statusRes.Success && statusRes.Data.HasFlag(AxisStatusFlags.Alarm)) 
                    {
                        context.Log($"❌ [AxisMoveAbs] 轴 [{param.AxisIndex}] 触发硬件报警！");
                        card.StopAxis(param.AxisIndex); 
                        context.SetOutputValue(node, PORT_OUT_SUCCESS, false);
                        return;
                    }

                    // 轴停止运动
                    if (idleRes.Success && idleRes.Data)
                    {
                        break;
                    }

                    if (sw.ElapsedMilliseconds > param.TimeoutMs)
                    {
                        context.Log($"❌ [AxisMoveAbs] 轴 [{param.AxisIndex}] 运动超时，执行停止。");
                        card.StopAxis(param.AxisIndex); 
                        context.SetOutputValue(node, PORT_OUT_SUCCESS, false);
                        return;
                    }

                    await Task.Delay(20, token); // 降低 CPU 占用
                }
            }

            // 读取并回传实际坐标
            var posRes = card.GetCommandPosition(param.AxisIndex); 
            float currentPos = posRes.Success ? posRes.Data : finalPos;

            context.Log($"✅ [AxisMoveAbs] 轴 [{param.AxisIndex}] 定位完成，当前实际坐标: {currentPos}");
            context.SetOutputValue(node, PORT_OUT_CURRENT_POS, currentPos);
            context.SetOutputValue(node, PORT_OUT_SUCCESS, true);
        }
    }
}