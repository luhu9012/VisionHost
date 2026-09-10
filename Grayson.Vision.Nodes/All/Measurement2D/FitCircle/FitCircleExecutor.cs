//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: FitCircleExecutor.cs
// 说 明: FitCircle 节点（P2 §5.2 测量节点族，NodeCategory.Measurement2D）：
//        环形/弧形卡尺在种子圆心处取样边缘点 → FitCircleContourXld("algebraic") 亚像素拟合
//        圆心与半径。种子可接 ShapeMatch.MatchRow/MatchCol（粗定位→精测链）；
//        输出 CenterRow/CenterCol/Radius + 效果图（借用输入帧，边缘点/拟合圆叠加）。
//        所有 HALCON 算子经 HalconWrapper.Measure2D.CaliperNodeMeasure 执行（Node 零句柄引用）。
//===================================================================================
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.HalconWrapper.Measure2D;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Measurement2D.FitCircle
{
    [Node(NodeType.FitCircle, NodeCategory.Measurement2D, typeof(FitCircleParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    // 种子端口配色沿用全库端口色约定：行/Row=橙、列/Col=蓝（与 ShapeMatch.MatchRow/MatchCol 同色配对，接反一目了然）
    [NodePort("SeedRow", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#E67E22")]
    [NodePort("SeedCol", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#3498DB")]
    [NodePort("SeedRadius", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#8E44AD")]
    [NodePort("CenterRow", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E67E22")]
    [NodePort("CenterCol", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#3498DB")]
    [NodePort("Radius", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#8E44AD")]
    // 效果图输出：端口名含 "Image" → 节点执行后自动推送帧事件，编辑器/工位监视主视图上屏（与 ShapeMatch 同通路）
    [NodePort("MeasureImage", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    public class FitCircleExecutor : NodeExecutorBase<FitCircleParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_IN_SEED_ROW = "SeedRow";
        public const string PORT_IN_SEED_COL = "SeedCol";
        public const string PORT_IN_SEED_RADIUS = "SeedRadius";
        public const string PORT_OUT_CENTER_ROW = "CenterRow";
        public const string PORT_OUT_CENTER_COL = "CenterCol";
        public const string PORT_OUT_RADIUS = "Radius";
        public const string PORT_OUT_IMAGE = "MeasureImage";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, FitCircleParam param,
            NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            object inputImage = context.GetInputValue<object>(node, PORT_IN_IMAGE, null);
            if (inputImage == null)
            {
                context.Log("[" + node.DisplayName + "] 错误：未获取到有效输入图像句柄！单步模式请先点『相机快照』采集一张。");
                Preview?.BeginScene();
                Preview?.AddText("⚠️ 输入图像为空：请先采集/运行上游节点", 12, 12, "red");
                return;
            }

            if (!ImageBasicTool.TryGetImageSize(inputImage, out int imgW, out int imgH))
            {
                context.Log("[" + node.DisplayName + "] 错误：无法获取输入图像尺寸。");
                Preview?.BeginScene();
                Preview?.AddText("⚠️ 输入图像无效", 12, 12, "red");
                return;
            }

            // 种子解析：数据端口连线优先（ShapeMatch 粗定位直连），未连线回落参数面板
            double row = ResolveSeed(param.SeedRow, context.GetInputValue<object>(node, PORT_IN_SEED_ROW, null), imgH);
            double col = ResolveSeed(param.SeedCol, context.GetInputValue<object>(node, PORT_IN_SEED_COL, null), imgW);
            double radius = ResolveSeed(param.Radius, context.GetInputValue<object>(node, PORT_IN_SEED_RADIUS, null), -1);

            if (!param.HasValidSeed(imgW, imgH) && (row <= 0 || col <= 0 || radius <= 0))
            {
                context.Log("[" + node.DisplayName + "] 错误：种子圆心/半径无效。请在属性面板填写粗圆心(Row,Col)与期望半径，" +
                    "或接上游 SeedRow/SeedCol（如 ShapeMatch 输出）；圆心应落在图内。");
                Preview?.BeginScene();
                Preview?.AddBorrowed(inputImage);
                Preview?.AddText("⚠️ 种子无效：请填圆心(Row,Col)+半径，或接上游粗定位", 12, 12, "red");
                return;
            }

            // 预览：底图 + 种子环带范围提示（种子圆/种子中心）
            Preview?.BeginScene();
            Preview?.AddBorrowed(inputImage);
            Preview?.AddCross(row, col, 24, "orange");
            Preview?.AddText($"种子: ({row:F0}, {col:F0}) R≈{radius:F0}  环半宽 {param.AnnulusHalf:F0}px", 12, 12, "yellow");

            // 效果图输出（借用输入帧；测量带/边缘点/拟合圆叠加与帧共存）
            context.SetOutputValue(node, PORT_OUT_IMAGE, inputImage);

            // 环形卡尺取样 + 圆拟合（HALCON 算子全部后台执行）
            double arcStart = param.ArcStartDeg * Math.PI / 180.0;
            double arcExtent = param.ArcExtentDeg * Math.PI / 180.0;
            var res = await Task.Run(() => CaliperNodeMeasure.MeasureCircle(inputImage,
                row, col, radius, arcStart, arcExtent, param.AnnulusHalf,
                param.Sigma, param.Threshold, param.Transition, param.Select,
                Math.Max(3, param.MinEdgePoints)));

            if (!res.Success)
            {
                context.Log("[" + node.DisplayName + "] ❌ 环形卡尺测量异常: " + res.Message);
                Preview?.AddText("⚠️ 测量异常: " + res.Message, 12, 40, "red");
                return;
            }

            var mo = res.Data;
            if (mo.CircleFit == null)
            {
                string reason = mo.EdgeCount < Math.Max(3, param.MinEdgePoints)
                    ? $"有效边缘点 {mo.EdgeCount} 个（< 需要 {Math.Max(3, param.MinEdgePoints)} 个）：阈值 {param.Threshold:F0} 过高漏边、或半径/环半宽/圆心偏离过大"
                    : "边缘点不足，无法拟合圆";
                context.Log($"[" + node.DisplayName + $"] ❌ 圆拟合失败: {reason}（边缘点 {mo.EdgeCount}）");
                Preview?.AddText($"⚠️ 拟合失败: {reason}", 12, 40, "red");
                return;
            }

            var fit = mo.CircleFit;
            sw.Stop();

            // 输出：圆心/半径（像素亚像素级，可接 CalibrationApply 或几何建构）
            context.SetOutputValue(node, PORT_OUT_CENTER_ROW, fit.CenterRow);
            context.SetOutputValue(node, PORT_OUT_CENTER_COL, fit.CenterCol);
            context.SetOutputValue(node, PORT_OUT_RADIUS, fit.Radius);

            // 可视化：边缘点小十字（lime，限 200 个防重） + 拟合圆心大十字(green) + 结果文本
            int cap = Math.Min(mo.EdgeCount, 200);
            for (int i = 0; i < cap; i++)
            {
                var p = mo.Points[i];
                Preview?.AddCross(p.Row, p.Col, 5, "lime");
            }
            Preview?.AddCross(fit.CenterRow, fit.CenterCol, 44, "green");
            Preview?.AddText($"⭕ 拟合: 圆心({fit.CenterRow:F2}, {fit.CenterCol:F2})  R={fit.Radius:F3}  " +
                $"RMS={fit.RmsError:F3}px  点数={mo.EdgeCount}", 12, 40, "green");

            context.Log($"[" + node.DisplayName + $"] ✅ 圆拟合成功：圆心=({fit.CenterRow:F3}, {fit.CenterCol:F3}) " +
                $"R={fit.Radius:F3}px RMS={fit.RmsError:F3}px 边缘点={mo.EdgeCount} 耗时 {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>数据端口值（boxed double）→ double；无效/未连线回落参数默认值</summary>
        private static double ResolveSeed(double fallback, object dyn, double upper)
        {
            if (dyn != null)
            {
                try
                {
                    double v = Convert.ToDouble(dyn, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(v) && !double.IsInfinity(v) && (upper <= 0 || (v > 0 && v < upper))) return v;
                }
                catch { /* 端口类型不符则回落参数 */ }
            }
            return fallback;
        }
    }
}
