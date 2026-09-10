//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CaliperMeasureExecutor.cs
// 说 明: CaliperMeasure 节点（P2 §5.2 测量节点族，NodeCategory.Measurement2D）：
//        沿目标边缘多探针"找边点"（亚像素），输出 EdgeRows/EdgeCols（double[]）与 EdgeCount，
//        不做几何拟合（拟合用 FitLine/FitCircle 节点）。种子可接 ShapeMatch 输出。
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

namespace Grayson.Vision.Nodes.All.Measurement2D.CaliperMeasure
{
    [Node(NodeType.CaliperMeasure, NodeCategory.Measurement2D, typeof(CaliperMeasureParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    // 端口配色沿用全库约定：行/Row=橙、列/Col=蓝、角度=青（与 ShapeMatch.MatchRow/MatchCol/MatchAngle 同色配对）
    [NodePort("MidRow", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#E67E22")]
    [NodePort("MidCol", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#3498DB")]
    [NodePort("MidPhi", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#1ABC9C")]
    // 边缘点数组（double[]：Row 数组 / Col 数组，顺序一一对应）；dataType=object 兼容数组传递
    [NodePort("EdgeRows", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#E67E22")]
    [NodePort("EdgeCols", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#3498DB")]
    [NodePort("EdgeCount", PortType.Out, PortCategory.Data, dataType: "int", colorHex: "#2ECC71")]
    [NodePort("MeasureImage", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    public class CaliperMeasureExecutor : NodeExecutorBase<CaliperMeasureParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_IN_MID_ROW = "MidRow";
        public const string PORT_IN_MID_COL = "MidCol";
        public const string PORT_IN_MID_PHI = "MidPhi";
        public const string PORT_OUT_EDGE_ROWS = "EdgeRows";
        public const string PORT_OUT_EDGE_COLS = "EdgeCols";
        public const string PORT_OUT_EDGE_COUNT = "EdgeCount";
        public const string PORT_OUT_IMAGE = "MeasureImage";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, CaliperMeasureParam param,
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

            // 种子解析：数据端口连线优先，未连线回落参数面板
            double midRow = ResolveSeed(param.SeedMidRow, context.GetInputValue<object>(node, PORT_IN_MID_ROW, null), imgH);
            double midCol = ResolveSeed(param.SeedMidCol, context.GetInputValue<object>(node, PORT_IN_MID_COL, null), imgW);
            double phiDeg = ResolveAny(param.EdgePhiDeg, context.GetInputValue<object>(node, PORT_IN_MID_PHI, null));
            double phiRad = phiDeg * Math.PI / 180.0;

            if (!param.HasValidSeed(imgW, imgH) && (midRow <= 0 || midCol <= 0))
            {
                context.Log("[" + node.DisplayName + "] 错误：种子中点无效。请在属性面板填边缘段中点(Row,Col)，" +
                    "或接上游 MidRow/MidCol（如 ShapeMatch 输出）。");
                Preview?.BeginScene();
                Preview?.AddBorrowed(inputImage);
                Preview?.AddText("⚠️ 种子无效：请填边缘段中点(Row,Col)+走向，或接上游粗定位", 12, 12, "red");
                return;
            }

            // 预览：底图 + 种子中点 + 探针散布范围示意
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

            // 沿边多探针找边点（拟合在本节点被忽略，只取点集）
            var res = await Task.Run(() => CaliperNodeMeasure.MeasureLine(inputImage,
                midRow, midCol, phiRad,
                param.HalfSpanAlongEdge, param.ScanHalf, param.ProbeAvgHalf,
                param.NumPoints, param.Sigma, param.Threshold, param.Transition, param.Select,
                0)); // minEdgePoints=0：本节点不需要拟合，一点也算成功

            if (!res.Success)
            {
                context.Log("[" + node.DisplayName + "] ❌ 卡尺找边异常: " + res.Message);
                Preview?.AddText("⚠️ 测量异常: " + res.Message, 12, 40, "red");
                return;
            }

            var mo = res.Data;
            int n = mo.EdgeCount;
            sw.Stop();

            // 输出边缘点数组（每探针 1 个代表点；探针漏边则该处缺席，Rows/Cols 同序）
            var rows = new double[n];
            var cols = new double[n];
            for (int i = 0; i < n; i++)
            {
                rows[i] = mo.Points[i].Row;
                cols[i] = mo.Points[i].Col;
            }
            context.SetOutputValue(node, PORT_OUT_EDGE_ROWS, rows);
            context.SetOutputValue(node, PORT_OUT_EDGE_COLS, cols);
            context.SetOutputValue(node, PORT_OUT_EDGE_COUNT, n);

            // 可视化：命中点 lime 小十字
            int cap = Math.Min(n, 200);
            for (int i = 0; i < cap; i++)
            {
                Preview?.AddCross(mo.Points[i].Row, mo.Points[i].Col, 5, "lime");
            }
            string sample = "";
            if (n > 0)
            {
                sample = $"  例: ({mo.Points[0].Row:F1},{mo.Points[0].Col:F1})";
                if (n > 1) sample += $" ... ({mo.Points[n - 1].Row:F1},{mo.Points[n - 1].Col:F1})";
            }
            Preview?.AddText($"🔎 边缘点 {n}/{param.NumPoints}{sample}", 12, 40, "lime");

            context.Log($"[" + node.DisplayName + $"] ✅ 卡尺找边完成：命中 {n}/{param.NumPoints} 点" +
                (n > 0 ? $"  首({mo.Points[0].Row:F2},{mo.Points[0].Col:F2}) 末({mo.Points[n - 1].Row:F2},{mo.Points[n - 1].Col:F2})" : "") +
                $" 耗时 {sw.ElapsedMilliseconds}ms" +
                (n == 0 ? "（所有探针漏边：阈值过高/种子偏离边缘超过 ScanHalf/光照太弱）" : ""));
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
