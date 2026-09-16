using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// 内参链的解算产物。★ 一次性把"数字 / 覆盖度 / 逐张证据 / 人话结论"都端出来，
    /// 因为内参标定最容易犯的错是<b>只看一个总数就收工</b>：
    /// 残差小不等于标定对（合成图无噪声时全正对姿态也能到 0.008 px），
    /// 所以结论必须和覆盖度、逐张误差、参数不确定度一起看。
    /// </summary>
    public sealed class IntrinsicsSolveOutcome
    {
        public bool Success;
        public CalibError Error;

        /// <summary>解算出的内参（发布用）。</summary>
        public IntrinsicsResult Intrinsics;

        /// <summary>姿态覆盖度结论（含人话提示）。</summary>
        public IntrinsicsCoverage Coverage;

        /// <summary>每张图的几何事实 + 重投影（界面逐行显示）。</summary>
        public List<BoardViewFact> Views = new List<BoardViewFact>();

        /// <summary>尝试过多少张（含失败的）。</summary>
        public int ViewCount;

        /// <summary>HALCON 给的总体反投影 RMSE（像素）。</summary>
        public double RmsePx = double.NaN;

        /// <summary>
        /// 本次解算对应的持久化产物（含<b>去畸变影响量</b> <c>DistortionImpact</c>）。
        ///
        /// ★ 为什么必须挂在 outcome 上：量化是在 runner 里算的、也随产物落盘了，
        ///   但 <b>UI 拿不到就等于没算</b> —— "去畸变后会好多少"那块界面只能从这里取数。
        ///   （曾经就是只落盘不回传，导致对比视图无从下手。）
        /// </summary>
        public CalibExport Export;

        // ── 解出的参数（原始口径 + 换算）──
        public double FocusM = double.NaN;
        public double KappaMetric = double.NaN;

        /// <summary>归一化口径的 k1（= κ·f²）。★ 和人眼熟悉的 Brown/Conrady 系数可对照。</summary>
        public double KappaNormalized = double.NaN;

        public double PixelPitchM = double.NaN;
        public int Width;
        public int Height;

        /// <summary>焦距的相对标准差（来自 HALCON 的 <c>params_deviations</c>）。</summary>
        public double FocusRelDev = double.NaN;

        public double KappaRelDev = double.NaN;

        public string RawParams = "-";
        public string RawDeviations = "-";

        /// <summary>单张最差重投影（像素）与它来自哪一张。</summary>
        public double MaxPoseRmsPx = double.NaN;
        public string WorstPoseName = string.Empty;

        /// <summary>逐张判读结论（人话）。</summary>
        public readonly List<string> ResidualNotes = new List<string>();

        /// <summary>
        /// 逐张"位姿读回轨迹"（原始板位姿 → 法向 → 中心深度）。
        /// ★ 覆盖度说"全都不斜"时，靠它区分"用户真没斜着拍"和"我们把位姿读错了"。
        /// </summary>
        public readonly List<string> GeometryTrace = new List<string>();

        /// <summary>
        /// 逐张失败原因（"第 4 张：找不到定位图案"这种，人话）。
        /// ★ 它不是内部日志：操作员界面要靠它说清"这一张为什么没过、要不要重拍"。
        ///   内参链的成败九成在摆板手感上，把失败原因吞掉等于把最有用的一条线索扔了。
        /// </summary>
        public readonly List<string> ViewErrors = new List<string>();

        /// <summary>把 HALCON 的 κ 换成"人话镜头畸变"：真值附近的归一化系数。</summary>
        public double KappaNormalizedValue
        {
            get { return KappaNormalized; }
        }

        /// <summary>在给定像素半径处，畸变把点挪了多少像素（"去畸变之后会好多少"）。</summary>
        public double DistortionShiftPx(double radiusPx)
        {
            return IntrinsicsGeometry.DistortionShiftPx(radiusPx, KappaMetric, PixelPitchM);
        }

        /// <summary>算单张最差值并生成逐张判读。在 <c>Solve()</c> 之后调用一次。</summary>
        public void EvaluateResiduals(double warnPx = 0.3, double blockPx = 0.5)
        {
            MaxPoseRmsPx = double.NaN;
            WorstPoseName = string.Empty;

            for (int i = 0; i < Views.Count; i++)
            {
                BoardViewFact f = Views[i];
                if (f.MarkCount <= 0 || double.IsNaN(f.ResidualPx))
                {
                    continue;
                }

                if (double.IsNaN(MaxPoseRmsPx) || f.ResidualPx > MaxPoseRmsPx)
                {
                    MaxPoseRmsPx = f.ResidualPx;
                    WorstPoseName = string.IsNullOrEmpty(f.Label)
                        ? "第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 张"
                        : f.Label;
                }
            }

            ResidualNotes.Clear();
            ResidualNotes.AddRange(IntrinsicsGeometry.JudgeResiduals(Views, warnPx, blockPx));
        }

        /// <summary>一行话的结论（界面顶部 / 日志用）。</summary>
        public string Describe()
        {
            if (!Success)
            {
                return "内参标定失败：" + (Error == null ? "未知原因" : Error.ToString());
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "焦距 {0:F3} mm（{1:F1} px）、主点 ({2:F1}, {3:F1}) px、畸变 κ = {4:F1}（1/m²，"
                + "约合归一化 k1 = {5:F3}）；反投影 RMSE {6:F3} px（{7} 张图）",
                FocusM * 1000.0, GetFocalPx(), Intrinsics.PrincipalPointPx[0], Intrinsics.PrincipalPointPx[1],
                KappaMetric, KappaNormalized, RmsePx, Intrinsics.UsedPoseCount));

            if (!double.IsNaN(MaxPoseRmsPx))
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "，单张最差 {0:F3} px", MaxPoseRmsPx));
            }

            return sb.ToString();
        }

        public double GetFocalPx()
        {
            return Intrinsics != null && Intrinsics.FocalLengthPx != null && Intrinsics.FocalLengthPx.Length > 0
                ? Intrinsics.FocalLengthPx[0]
                : double.NaN;
        }

        /// <summary>
        /// 全部逐行报告（人眼复盘："到底哪一张在拖后腿、覆盖度够不够"）。
        /// </summary>
        public List<string> ReportLines()
        {
            var lines = new List<string>();
            if (Intrinsics != null)
            {
                lines.Add("解出参数：" + RawParams);
                if (!string.Equals(RawDeviations, "-", StringComparison.Ordinal))
                {
                    lines.Add("参数标准差：" + RawDeviations);
                    lines.Add("  ↑ 合成图/低噪声时 sx 的标准差接近 0 属正常（sx 只与焦距以比值出现）");
                }
            }

            if (Coverage != null)
            {
                lines.Add("覆盖度：" + Coverage.Describe());
                for (int i = 0; i < Coverage.Hints.Count; i++)
                {
                    lines.Add("  · " + Coverage.Hints[i]);
                }
            }

            if (Views != null && Views.Count > 0)
            {
                for (int i = 0; i < Views.Count; i++)
                {
                    BoardViewFact f = Views[i];
                    string name = string.IsNullOrEmpty(f.Label)
                        ? "第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 张"
                        : f.Label;
                    if (f.MarkCount <= 0)
                    {
                        lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0}：没找到板", name));
                        continue;
                    }

                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}：倾斜 {1:F1}°，深度 {2:F1} mm，检出 {3} 个 mark，重投影 {4}",
                        name, f.TiltDeg, f.CenterZ * 1000.0, f.MarkCount,
                        double.IsNaN(f.ResidualPx) ? "算不出来" : f.ResidualPx.ToString("F3", CultureInfo.InvariantCulture) + " px"));
                }
            }

            for (int i = 0; i < ResidualNotes.Count; i++)
            {
                lines.Add("  ⚠ " + ResidualNotes[i]);
            }

            return lines;
        }
    }
}
