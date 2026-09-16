using System;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.CalibrationApply
{
    [Node(NodeType.CalibrationApply, NodeCategory.CalibrationLocation, typeof(CalibrationApplyParam))]
    // 端口配色约定：X/列=蓝(#3498DB)、Y/行=橙(#E67E22)、角度=青(#1ABC9C)。
    // InputX 应接 ShapeMatch.MatchCol（蓝）、InputY 应接 MatchRow（橙）——同色配对防接反。
    // ★ 角度透传（2026-09-12）：InputAngle 应接 ShapeMatch.MatchAngle（青），OutputAngle 原样透传给下游。
    //   本节点仍只做【纯 H 位置映射】，角度不做任何换算（e/t/O 补偿在流程编排层/生产引擎叠加），
    //   这里只是把姿态角在节点图上「显式化、可连线、可复用」，消除"角度去哪了"的视觉断点。
    [NodePort("InputX", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#3498DB")]
    [NodePort("InputY", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#E67E22")]
    [NodePort("InputAngle", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#1ABC9C")]
    [NodePort("OutputX", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#3498DB")]
    [NodePort("OutputY", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E67E22")]
    [NodePort("OutputAngle", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#1ABC9C")]
    public class CalibrationApplyExecutor : NodeExecutorBase<CalibrationApplyParam>
    {
        public const string PORT_IN_X = "InputX";
        public const string PORT_IN_Y = "InputY";
        public const string PORT_IN_ANGLE = "InputAngle";
        public const string PORT_OUT_X = "OutputX";
        public const string PORT_OUT_Y = "OutputY";
        public const string PORT_OUT_ANGLE = "OutputAngle";

        private readonly ICalibrationService _calibService;

        public CalibrationApplyExecutor()
        {
            _calibService = new CalibrationService();
        }

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, CalibrationApplyParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            // ★职责边界（2026-09-12 明确）：本节点只做【纯 H 映射】——像素 ↔ 机械坐标（命令位域 H(u)）。
            //   输出是「机械手该停哪」的 H(u)，不是工件真位。偏心 e / 对针 t / 旋转中心 O 的补偿
            //   不在这里叠加：那需要拍照机位 P_photo 与作业 U 角，属于流程编排层/生产引擎的职责
            //   （唯一真源 CalibrationGeometry；生产引擎 VisionPickPlaceProcess / MahjongDualNozzleProcess
            //   在拿到本节点输出的 H(u) 后按 X_obj=P_photo+O−H(u)、C=X_obj−R(U−U0)·e 叠加）。
            //   请勿把 e/t 塞进本节点——历史上校验台/引擎各抄一份公式反复调不准的根源就在此。

            double inX = context.GetInputValue<double>(node, PORT_IN_X);
            double inY = context.GetInputValue<double>(node, PORT_IN_Y);

            // ★ 角度透传（2026-09-12）：姿态角不做任何换算，原样从 InputAngle → OutputAngle。
            //   未连线时 GetInputValue 返回 default(double)=0，透传 0 是无害的（下游生产引擎
            //   读的是 ShapeMatch.MatchAngle 直接端口，不依赖这里的透传；此处只为节点图可视化复用）。
            //   注意：不要在此叠加 e/t/O 的角度补偿——本节点是纯 H 位置映射，姿态补偿是流程编排层的职责。
            double inAngle = context.GetInputValue<double>(node, PORT_IN_ANGLE);
            context.SetOutputValue(node, PORT_OUT_ANGLE, inAngle);

            // 检查点①：矩阵路径配置
            if (string.IsNullOrEmpty(param.HomMatFilePath))
            {
                context.Log($"[{node.DisplayName}] 错误：尚未指定有效的标定矩阵文件路径！请确认已执行标定并发布矩阵。");
                return;
            }
            // 检查点②：矩阵文件存在性（生产环境矩阵被误删/路径被改是最常见静默失败源）
            if (!System.IO.File.Exists(param.HomMatFilePath))
            {
                context.Log($"[{node.DisplayName}] ❌ 标定矩阵文件不存在: {param.HomMatFilePath}（请重新标定并发布，或修正矩阵路径）");
                return;
            }
            // 检查点③：输入像素坐标合理性（NaN/非法值直接拦截，避免把坏值带进走位）
            if (double.IsNaN(inX) || double.IsNaN(inY) || Math.Abs(inX) > 100000 || Math.Abs(inY) > 100000)
            {
                context.Log($"[{node.DisplayName}] ⚠ 输入像素坐标异常: ({inX:F2}, {inY:F2})——上游匹配节点可能未输出有效结果，本次转换跳过。");
                return;
            }

            if (param.IsPixelToWorld)
            {
                // 像素坐标 -> 机械物理坐标 (mm)
                var mapRes = _calibService.MapPixelToWorld(param.HomMatFilePath, inX, inY);
                if (mapRes.Success)
                {
                    context.SetOutputValue(node, PORT_OUT_X, mapRes.Data.WorldX);
                    context.SetOutputValue(node, PORT_OUT_Y, mapRes.Data.WorldY);
                    context.Log($"[{node.DisplayName}] 坐标转换成功：Pixel({inX:F2}, {inY:F2}) ➔ World({mapRes.Data.WorldX:F3}, {mapRes.Data.WorldY:F3}) mm");
                }
                else
                {
                    context.Log($"[{node.DisplayName}] 坐标转换失败：{mapRes.Message}");
                }
            }
            else
            {
                // 机械物理坐标 -> 像素坐标
                var mapRes = _calibService.MapWorldToPixel(param.HomMatFilePath, inX, inY);
                if (mapRes.Success)
                {
                    context.SetOutputValue(node, PORT_OUT_X, mapRes.Data.PixelX);
                    context.SetOutputValue(node, PORT_OUT_Y, mapRes.Data.PixelY);
                    context.Log($"[{node.DisplayName}] 坐标逆转换成功：World({inX:F3}, {inY:F3}) ➔ Pixel({mapRes.Data.PixelX:F2}, {mapRes.Data.PixelY:F2}) px");
                }
                else
                {
                    context.Log($"[{node.DisplayName}] 坐标逆转换失败：{mapRes.Message}");
                }
            }
        }
    }
}