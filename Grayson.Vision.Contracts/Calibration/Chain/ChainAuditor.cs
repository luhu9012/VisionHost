//===================================================================================
// 文件名: ChainAuditor.cs
// 说 明: 链校验台核心（范式2 三级校验的 L1 静态门禁可视 + L2 数学校验；设计真源
//        《标定链路v2_工位驱动标定工作流设计_2026-09-27.md》§6）。
//
// 与门禁的关系：ChainEngine.Validate 是落盘/装载时的硬拦（fail-closed）；本类是
//   **同一把尺的人类可读版**——把每条门禁、每个节点的"会红理由"摊开给人看，
//   并补上静态可算的数学校验项（形状门复验/条件数/像素当量/EIH 平移量级）。
//   纪律：只读审计，一个数不改；判据与生产端同源（ChainEngine.Validate + ChainFitter 门限）。
//
// L2 项（本类新增，静态可算）：
//   · 形状门复验：|σ1/σ2−1| 从**落盘矩阵线性部分 2×2** 的真奇异值复算（与 ChainFitter 同判据）；
//   · 条件数 σ1/σ2（病态矩阵在 RMS 上看不出来的另一种形状病）；
//   · 像素当量 mm/px = 1/√(σ1σ2)——超出现实量级（<0.001 或 >10）提示可疑；
//   · EIH 平移量级 |t|：几 mm~几十 mm 正常，>200mm 警告（Cam→Flange 不可能是几百 mm 平移）；
//   · DeltaRefPixel 自洽：映射到世界后与主工具 Offset 的距离打印（= 拍照位到法兰原点距离，
//     供人工核对；纯静态无法拿拍照位真值，只提示不硬拦）。
//   · RMS 复算（重喂点对比对落盘值）属 L3 真机页：需要重新采集，不在本类。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Grayson.Vision.Contracts.Calibration.Services;

namespace Grayson.Vision.Contracts.Calibration.Chain
{
    /// <summary>一个节点（相机/工具）的审计行</summary>
    public sealed class ChainAuditRow
    {
        public string Target { get; set; }
        /// <summary>Camera-EIH / Camera-ETH / Tool-Master / Tool-Slave</summary>
        public string Kind { get; set; }
        /// <summary>矩阵/偏移已落值（骨架 Pending = false）</summary>
        public bool Filled { get; set; }
        public double? ShapeDev { get; set; }
        public double? CondNumber { get; set; }
        /// <summary>等效像素当量 mm/px（仅相机）</summary>
        public double? PixelScaleMmPerPx { get; set; }
        /// <summary>EIH 相机法兰系平移量 |t| mm（仅 EIH）</summary>
        public double? EihTranslationMm { get; set; }
        public double? RmsMm { get; set; }
        public int? PointPairs { get; set; }
        public double? CalibZMm { get; set; }
        public string CalibToolTag { get; set; }
        public bool HasDeltaRef { get; set; }
        public double?[] DeltaRefPixel { get; set; }
        /// <summary>DeltaRef 映射到世界后与主工具 Offset 的距离 mm（仅下相机）</summary>
        public double? DeltaRefToWorldDistMm { get; set; }
        public List<string> Issues { get; } = new List<string>();
        public List<string> Infos { get; } = new List<string>();
        public bool Ok { get { return Issues.Count == 0; } }

        // —— 展示辅助（校验台 DataGrid 直接绑定）——
        public string StatusText { get { return !Filled ? "○ 待填" : (Ok ? "✓ 通过" : "✗ 有问题"); } }
        public string ProblemText { get { return string.Join("；", Issues); } }
        public string InfoText { get { return string.Join("；", Infos); } }
        public string DeltaRefText
        {
            get { return !HasDeltaRef ? "—" : (DeltaRefPixel == null ? "有" : "(" + DeltaRefPixel[0] + "," + DeltaRefPixel[1] + ")"); }
        }
    }

    /// <summary>整链审计报告</summary>
    public sealed class ChainAuditReport
    {
        public string StationCode { get; set; }
        public bool Loaded { get; set; }
        public string LoadError { get; set; }
        public List<ChainAuditRow> Rows { get; } = new List<ChainAuditRow>();
        public List<string> GlobalIssues { get; } = new List<string>();
        public List<string> EdgeLines { get; } = new List<string>();
        public int EdgeCount { get { return EdgeLines.Count; } }

        public bool AllOk { get { return Loaded && GlobalIssues.Count == 0 && Rows.All(r => r.Ok); } }
    }

    public static class ChainAuditor
    {
        /// <summary>形状门限（与 ChainFitter.ShapeGateRatio 同源，勿单独改动）</summary>
        public const double ShapeGateRatio = ChainFitter.ShapeGateRatio;
        /// <summary>像素当量现实量级（mm/px）：工业相机常见 0.005~5；出界=提示可疑不硬拦</summary>
        public const double PixelScaleMin = 0.001, PixelScaleMax = 10.0;
        /// <summary>EIH 平移量级上限（mm）：Cam→Flange 几 mm~几十 mm</summary>
        public const double EihTranslationWarnMm = 200.0;

        /// <summary>按工位码从 Recipes\Workstations\{code}\Calib\Chain.json 载入并审计</summary>
        public static ChainAuditReport AuditStation(string stationCode)
        {
            var report = new ChainAuditReport { StationCode = stationCode };
            string path = System.IO.Path.Combine(
                CalibrationMatrixStore.GetStationCalibDir(stationCode), "Chain.json");
            string err;
            StationCalibGraph g;
            if (!StationCalibGraph.TryLoad(path, out g, out err))
            {
                report.Loaded = false;
                report.LoadError = "未找到/无法解析 Chain.json：" + err
                    + "（路径=" + path + "）。链未落盘 ⇒ 生产端 fail-closed 拒绝启动，属预期状态。";
                return report;
            }
            report.Loaded = true;
            Audit(report, g);
            return report;
        }

        /// <summary>对内存中的链图做审计（探针直接喂图，不落盘）</summary>
        public static void Audit(ChainAuditReport report, StationCalibGraph g)
        {
            report.StationCode = g.StationCode;
            report.Loaded = true;

            // ---- L1a：全量门禁（与生产端同一把尺）----
            var gateErrs = ChainEngine.Validate(g, g.StationCode);
            foreach (var e in gateErrs) report.GlobalIssues.Add("门禁: " + e);

            // ---- 边清单 ----
            foreach (var e in g.Edges)
                report.EdgeLines.Add(e.FromCameraId + " --" + e.Usage + "--> " + e.ToToolId);

            string masterOffsetSummary = MasterOffset(g);

            // ---- 相机行 ----
            foreach (var cam in g.Cameras)
            {
                var row = new ChainAuditRow
                {
                    Target = cam.CameraId,
                    Kind = cam.Mount == ChainCameraMount.EyeInHand ? "Camera-EIH" : "Camera-ETH",
                    HasDeltaRef = cam.DeltaRefPixel != null,
                };
                if (cam.DeltaRefPixel != null)
                    row.DeltaRefPixel = new double?[] { cam.DeltaRefPixel[0], cam.DeltaRefPixel[1] };
                if (cam.Meta != null)
                {
                    row.RmsMm = cam.Meta.RmsMm;
                    row.PointPairs = cam.Meta.PointPairs;
                    row.CalibZMm = cam.Meta.CalibZHeightMm;
                    row.CalibToolTag = cam.Meta.CalibToolTag;
                }

                if (cam.Matrix == null || cam.Matrix.Length != 6)
                {
                    row.Filled = false;
                    row.Issues.Add("矩阵未填（骨架 Pending）——先跑链向导完成九点采集");
                    report.Rows.Add(row);
                    continue;
                }
                row.Filled = true;
                AnalyzeMatrix(row, cam.Matrix);

                if (cam.Mount == ChainCameraMount.EyeInHand)
                {
                    double t = Math.Sqrt(cam.Matrix[2] * cam.Matrix[2] + cam.Matrix[5] * cam.Matrix[5]);
                    row.EihTranslationMm = t;
                    if (t > EihTranslationWarnMm)
                        row.Issues.Add("EIH 平移量 |t|=" + t.ToString("F1", CultureInfo.InvariantCulture)
                            + "mm 超常（Cam→Flange 应为几 mm~几十 mm）——疑似把法兰原点错当世界原点");
                }
                if (row.CalibZMm == null && cam.Mount == ChainCameraMount.EyeInHand)
                    row.Issues.Add("EIH 相机未填 CalibZHeightMm——生产拍照 Z≠标定 Z 时平移分量线性漂移⇒乘性过纠（血泪），L2 无法自洽");

                if (g.Edges.Any(e => e.FromCameraId == cam.CameraId && e.Usage == ChainUsage.DownCameraCorrect)
                    && !row.HasDeltaRef)
                    row.Issues.Add("有 DownCameraCorrect 边但缺 DeltaRefPixel——生产端 δ 将显式降级（纠偏不生效）");

                // ★2026-09-28 R6e：下相机纠偏用途必须有『标定高度 Z』，否则生产端无法核对拍照 Z（像素当量 1/物距 缩放）。
                if (row.HasDeltaRef && row.CalibZMm == null)
                    row.Issues.Add("有 DeltaRefPixel（下相机差分消费）却缺 CalibZHeightMm——生产端无法核对『拍照 Z = 标定 Z』，" +
                        "工件 Z 一变像素当量即按 1/物距 缩放（向导相机段『标定高度 Z』补填）");

                if (row.HasDeltaRef && cam.Matrix != null)
                {
                    // DeltaRef 映射到世界，与主工具 Offset 的距离 = 拍照位到法兰原点距离（人工核对项）
                    double wx = cam.Matrix[0] * cam.DeltaRefPixel[0] + cam.Matrix[1] * cam.DeltaRefPixel[1] + cam.Matrix[2];
                    double wy = cam.Matrix[3] * cam.DeltaRefPixel[0] + cam.Matrix[4] * cam.DeltaRefPixel[1] + cam.Matrix[5];
                    var master = g.Tools.FirstOrDefault(t => t.IsMaster);
                    if (master != null && master.Offset != null && master.Offset.Length == 2)
                    {
                        double d = Math.Sqrt(Math.Pow(wx - master.Offset[0], 2) + Math.Pow(wy - master.Offset[1], 2));
                        row.DeltaRefToWorldDistMm = d;
                        row.Infos.Add("DeltaRef 世界映射=(" + wx.ToString("F1", CultureInfo.InvariantCulture) + ","
                            + wy.ToString("F1", CultureInfo.InvariantCulture) + ")，距主工具 Offset "
                            + d.ToString("F1", CultureInfo.InvariantCulture) + "mm（=拍照位到法兰原点距离，请人工核对量级）");
                    }
                }
                report.Rows.Add(row);
            }

            // ---- 工具行 ----
            foreach (var t in g.Tools)
            {
                var row = new ChainAuditRow
                {
                    Target = t.ToolId,
                    Kind = t.IsMaster ? "Tool-Master" : "Tool-Slave",
                    HasDeltaRef = false,
                };
                if (t.Meta != null)
                {
                    row.RmsMm = t.Meta.RmsMm;
                    row.PointPairs = t.Meta.PointPairs;
                    row.CalibZMm = t.Meta.CalibZHeightMm;
                    row.CalibToolTag = t.Meta.CalibToolTag;
                }
                if (t.Offset == null || t.Offset.Length != 2)
                {
                    row.Filled = false;
                    row.Issues.Add("Offset 未填（骨架 Pending）——先实测对针直量/刚性阵列 Δ");
                }
                else
                {
                    row.Filled = true;
                    row.Infos.Add("Offset=(" + t.Offset[0].ToString("F3", CultureInfo.InvariantCulture) + ","
                        + t.Offset[1].ToString("F3", CultureInfo.InvariantCulture) + ") mm（符号内蕴）");
                }
                report.Rows.Add(row);
            }

            // ---- 全局软提示 ----
            if (masterOffsetSummary.Length > 0)
                report.GlobalIssues.Add(masterOffsetSummary);
        }

        /// <summary>线性部分 2×2 真奇异值 → 形状偏差/条件数/像素当量（与 ChainFitter 同判据）</summary>
        private static void AnalyzeMatrix(ChainAuditRow row, double[] m)
        {
            var lin = DenseMatrix.OfArray(new[,]
            {
                { m[0], m[1] },
                { m[3], m[4] },
            });
            var svd = lin.Svd(true);
            double s1 = svd.S[0], s2 = svd.S[1];
            row.ShapeDev = s2 <= 1e-12 ? double.PositiveInfinity : Math.Abs(s1 / s2 - 1.0);
            row.CondNumber = s2 <= 1e-12 ? double.PositiveInfinity : s1 / s2;
            row.PixelScaleMmPerPx = (s1 > 1e-12 && s2 > 1e-12) ? 1.0 / Math.Sqrt(s1 * s2) : (double?)null;

            if (row.ShapeDev > ShapeGateRatio)
                row.Issues.Add("形状失真超门 |σ1/σ2−1|=" + row.ShapeDev.Value.ToString("F4", CultureInfo.InvariantCulture)
                    + " > " + ShapeGateRatio.ToString("F2", CultureInfo.InvariantCulture)
                    + "（σ1=" + s1.ToString("F4", CultureInfo.InvariantCulture) + " σ2=" + s2.ToString("F4", CultureInfo.InvariantCulture)
                    + "）——RMS 抓不到的形状病，须重新采集");
            if (row.CondNumber > 50)
                row.Issues.Add("条件数 σ1/σ2=" + row.CondNumber.Value.ToString("F1", CultureInfo.InvariantCulture)
                    + " 过大——点对近共线或分布过窄，求逆放大误差");
            if (row.PixelScaleMmPerPx != null
                && (row.PixelScaleMmPerPx < PixelScaleMin || row.PixelScaleMmPerPx > PixelScaleMax))
                row.Issues.Add("像素当量 " + row.PixelScaleMmPerPx.Value.ToString("G3", CultureInfo.InvariantCulture)
                    + " mm/px 出现实量级（" + PixelScaleMin + "~" + PixelScaleMax + "）——疑似单位/点序错乱");
        }

        private static string MasterOffset(StationCalibGraph g)
        {
            var master = g.Tools.FirstOrDefault(t => t.IsMaster);
            if (master == null) return "无主工具节点";
            if (master.Offset == null || master.Offset.Length != 2)
                return "主工具 " + master.ToolId + " Offset 未填";
            return "";
        }
    }
}
