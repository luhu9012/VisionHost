//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: FitLineExecutor.cs
// 说 明: FitLine 节点（P2 §5.2 测量节点族，NodeCategory.Measurement2D）：
//        沿目标边缘散布多探针直线卡尺 → FitLineContourXld("tukey") 亚像素拟合直线。
//        种子可接 ShapeMatch 输出（粗定位→精测链）；输出拟合线段端点 + 质量分。
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

namespace Grayson.Vision.Nodes.All.Measurement2D.FitLine
{
    [Node(NodeType.FitLine, NodeCategory.Measurement2D, typeof(FitLineParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    // 端口配色沿用全库约定：行/Row=橙、列/Col=蓝、角度=青（与 ShapeMatch.MatchRow/MatchCol/MatchAngle 同色配对）
    [NodePort("SeedMidRow", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#E67E22")]
    [NodePort("SeedMidCol", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#3498DB")]
    [NodePort("SeedPhi", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#1ABC9C")]
    [NodePort("Row1", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E67E22")]
    [NodePort("Col1", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#3498DB")]
    [NodePort("Row2", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E67E22")]
    [NodePort("Col2", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#3498DB")]
    [NodePort("Score", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#2ECC71")]
    [NodePort("MeasureImage", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    public class FitLineExecutor : NodeExecutorBase<FitLineParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_IN_SEED_ROW = "SeedMidRow";
        public const string PORT_IN_SEED_COL = "SeedMidCol";
        public const string PORT_IN_SEED_PHI = "SeedPhi";
        public const string PORT_OUT_R1 = "Row1";
        public const string PORT_OUT_C1 = "Col1";
        public const string PORT_OUT_R2 = "Row2";
        public const string PORT_OUT_C2 = "Col2";
        public const string PORT_OUT_SCORE = "Score";
        public const string PORT_OUT_IMAGE = "MeasureImage";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, FitLineParam param,
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
            double midRow = ResolveSeed(param.SeedMidRow, context.GetInputValue<object>(node, PORT_IN_SEED_ROW, null), imgH);
            double midCol = ResolveSeed(param.SeedMidCol, context.GetInputValue<object>(node, PORT_IN_SEED_COL, null), imgW);
            double phiDeg = ResolveAny(param.EdgePhiDeg, context.GetInputValue<object>(node, PORT_IN_SEED_PHI, null));
            double phiRad = phiDeg * Math.PI / 180.0;

            if (!param.HasValidSeed(imgW, imgH) && (midRow <= 0 || midCol <= 0))
            {
                context.Log("[" + node.DisplayName + "] 错误：种子中点无效。请在属性面板填边缘段中点(Row,Col)，" +
                    "或接上游 SeedMidRow/SeedMidCol（如 ShapeMatch 输出）。");
                Preview?.BeginScene();
                Preview?.AddBorrowed(inputImage);
                Preview?.AddText("⚠️ 种子无效：请填边缘段中点(Row,Col)+走向，或接上游粗定位", 12, 12, "red");
                return;
            }

            // 预览：底图 + 种子中点 + 边缘走向示意（探针散布方向 = 边缘走向 ±90° 扫描）
            Preview?.BeginScene();
            Preview?.AddBorrowed(inputImage);
            Preview?.AddCross(midRow, midCol, 20, "orange");
            double dRow = Math.Sin(phiRad), dCol = Math.Cos(phiRad);
            double halfSpan = Math.Max(4.0, param.HalfSpanAlongEdge);
            Preview?.AddCross(midRow + halfSpan * dRow, midCol + halfSpan * dCol, 10, "orange");
            Preview?.AddCross(midRow - halfSpan * dRow, midCol - halfSpan * dCol, 10, "orange");
            Preview?.AddText($"种子: ({midRow:F0}, {midCol:F0})  走向 {phiDeg:F0}°  探针 {param.NumPoints}", 12, 12, "yellow");

            // 效果图输出（借用输入帧）
            context.SetOutputValue(node, PORT_OUT_IMAGE, inputImage);

            // 直线卡尺取样 + 拟合（HALCON 算子全部后台执行）
            var res = await Task.Run(() => CaliperNodeMeasure.MeasureLine(inputImage,
                midRow, midCol, phiRad,
                param.HalfSpanAlongEdge, param.ScanHalf, param.ProbeAvgHalf,
                param.NumPoints, param.Sigma, param.Threshold, param.Transition, param.Select,
                Math.Max(2, param.MinEdgePoints)));

            if (!res.Success)
            {
                context.Log("[" + node.DisplayName + "] ❌ 直线卡尺测量异常: " + res.Message);
                Preview?.AddText("⚠️ 测量异常: " + res.Message, 12, 40, "red");
                return;
            }

            var mo = res.Data;
            if (mo.LineFit == null)
            {
                string reason = mo.EdgeCount < Math.Max(2, param.MinEdgePoints)
                    ? $"有效边缘点 {mo.EdgeCount} 个（< 需要 {Math.Max(2, param.MinEdgePoints)}）：阈值过高漏边、种子偏离边缘超过 ScanHalf、或光照太弱"
                    : "边缘点不足，无法拟合直线";
                context.Log("[" + node.DisplayName + "] ❌ 直线拟合失败: " + reason);
                Preview?.AddText("⚠️ 拟合失败: " + reason, 12, 40, "red");
                return;
            }

            var fit = mo.LineFit;
            sw.Stop();

            // 拟合质量分（0~1）：残差 RMS≈0.02px 亚像素级→近 1；RMS≥1px 明显离群→走低
            double score = Math.Max(0.0, Math.Min(1.0, 1.0 - fit.RmsError));

            context.SetOutputValue(node, PORT_OUT_R1, fit.Row1);
            context.SetOutputValue(node, PORT_OUT_C1, fit.Col1);
            context.SetOutputValue(node, PORT_OUT_R2, fit.Row2);
            context.SetOutputValue(node, PORT_OUT_C2, fit.Col2);
            context.SetOutputValue(node, PORT_OUT_SCORE, score);

            // 可视化：边缘点小十字（lime，限 200）+ 拟合线段两端绿十字 + 文本
            int cap = Math.Min(mo.EdgeCount, 200);
            for (int i = 0; i < cap; i++)
            {
                var p = mo.Points[i];
                Preview?.AddCross(p.Row, p.Col, 5, "lime");
            }
            Preview?.AddCross(fit.Row1, fit.Col1, 16, "green");
            Preview?.AddCross(fit.Row2, fit.Col2, 16, "green");
            Preview?.AddText($"📏 拟合线: ({fit.Row1:F1},{fit.Col1:F1})→({fit.Row2:F1},{fit.Col2:F1})  " +
                $"RMS={fit.RmsError:F3}px 分={score:F2} 点={mo.EdgeCount}", 12, 40, "green");

            context.Log($"[" + node.DisplayName + $"] ✅ 直线拟合成功：线段 ({fit.Row1:F2},{fit.Col1:F2})→" +
                $"({fit.Row2:F2},{fit.Col2:F2}) RMS={fit.RmsError:F3}px 质量分={score:F2} " +
                $"边缘点={mo.EdgeCount} 耗时 {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>数据端口值（boxed double）→ double；无效/未连线回落参数默认值（限定在 [0,upper) 图内）</summary>
        private static double ResolveSeed(double fallback, object dyn, double upper)
        {
            if (dyn != null)
            {
                try
                {
                    double v = Convert.ToDouble(dyn, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(v) && !double.IsInfinity(v) && (upper <= 0 || (v > 0 && v < upper))) return v;
                }
                catch { }
            }
            return fallback;
        }

        /// <summary>任意 double 端口（角度等，不限定范围）</summary>
        private static double ResolveAny(double fallback, object dyn)
        {
            if (dyn != null)
            {
                try
                {
                    double v = Convert.ToDouble(dyn, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(v) && !double.IsInfinity(v)) return v;
                }
                catch { }
            }
            return fallback;
        }
    }
}
