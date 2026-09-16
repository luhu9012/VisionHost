using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>自检结果。</summary>
    public sealed class SelfCheckReport
    {
        public readonly List<string> Lines = new List<string>();
        public int Passed;
        public int Failed;

        public bool AllPassed
        {
            get { return Failed == 0; }
        }

        public void Check(bool ok, string title, string detail)
        {
            if (ok)
            {
                Passed++;
                Lines.Add("[PASS] " + title + (string.IsNullOrEmpty(detail) ? string.Empty : "  —— " + detail));
            }
            else
            {
                Failed++;
                Lines.Add("[FAIL] " + title + (string.IsNullOrEmpty(detail) ? string.Empty : "  —— " + detail));
            }
        }

        public string ToText()
        {
            return string.Join(Environment.NewLine, Lines.ToArray());
        }
    }

    /// <summary>
    /// ★ 算法层离线自检（不连机、不接相机）。
    ///
    /// 为什么值得单独做：本项目最贵的历史教训就是"算出来的东西看着对、其实是错的"，
    /// 所以这里把几条<b>血汗结论</b>做成可自动复跑的断言。
    ///
    /// ⚠ 其中第 ② 组断言用的是<b>真机已发布的 .tup 矩阵</b>（155 字节那份），
    ///   不是构造出来的样例 —— 因为"判据对不对"这件事，只有拿真数据才压得住。
    /// </summary>
    public static class CalibSelfCheck
    {
        /// <summary>
        /// 真机已发布的九点矩阵（来自 ST_002 / Cam_A 的 .tup，2026-09 实测）。
        /// det = −0.0405（负），σ1/σ2 = 1.028 —— 这组数把两条重要口径钉死了。
        /// </summary>
        public static readonly HomMat2D RealStationHom = new HomMat2D(
            -5.3606559567018668e-03, 2.0331699576237314e-01, 1.3958856434627907e+02,
            1.9871790279894849e-01, 8.8771401050753065e-03, -3.8231860281097035e+01);

        public static SelfCheckReport Run(double stepX = 10.0, double stepY = 10.0)
        {
            var report = new SelfCheckReport();

            IdealNinePointClosedLoop(report, stepX, stepY);
            RealMatrixSemantics(report);
            ColumnNormIsRotationInvariant(report, stepX, stepY);
            DiagonalCriterionIsRotationDependent(report);
            ShearCriterionIsRotationInvariant(report);
            AnisotropyIsCaughtBySigma(report, stepX, stepY);
            ShapeGateBlockerAndRelax(report);
            LooDefinedUnderNoise(report, stepX, stepY);
            TupRoundTripAndByteFormat(report);

            return report;
        }

        // ── ① 理想九点闭环 ──────────────────────────────────────────────
        private static void IdealNinePointClosedLoop(SelfCheckReport report, double stepX, double stepY)
        {
            var world = new SimulatedWorld();
            world.PixelNoisePx = 0.0;
            world.FeedbackNoiseMm = 0.0;

            CalibTopology topo = MakeTopology(stepX, stepY);
            List<CalibSample> samples = BuildIdealSamples(world, topo, stepX, stepY);

            NinePointResult solved = NinePointSolver.Solve(samples, topo);
            if (!solved.Success)
            {
                report.Check(false, "理想九点闭环（解算）", "解算失败：" + solved.Error);
                return;
            }

            HomMat2D expected = world.ExpectedH();
            double[] a = solved.H.ToArray();
            double[] b = expected.ToArray();
            double maxDiff = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = Math.Abs(a[i] - b[i]);
                if (d > maxDiff)
                {
                    maxDiff = d;
                }
            }

            report.Check(maxDiff < 1e-6, "理想九点闭环（解出的 H ≈ 解析 H）",
                string.Format(CultureInfo.InvariantCulture, "最大逐元素偏差 {0:E3} mm", maxDiff));

            report.Check(solved.RmsMm < 1e-6, "理想九点残差 ≈ 0",
                string.Format(CultureInfo.InvariantCulture, "RMS = {0:E3} mm", solved.RmsMm));
        }

        // ── ② 真机矩阵口径复现（最关键的一组）───────────────────────────
        private static void RealMatrixSemantics(SelfCheckReport report)
        {
            var t = new MatrixQualityThresholds();
            var samples = SamplesFromMatrix(RealStationHom, 9);

            // (a) 矩阵形状指标
            CalibDiagnostics d = MatrixQualityEvaluator.Evaluate(
                RealStationHom, samples, MakeTopology(10.0, 10.0, CameraMountKind.EyeInHand), null, t);

            report.Check(Math.Abs(d.DetA - (-0.04045)) < 5e-4, "真机矩阵 det(A) 复现为负值",
                string.Format(CultureInfo.InvariantCulture, "det = {0:F6}", d.DetA));

            report.Check(d.MirrorDetected, "真机矩阵被判定为镜像变换", "MirrorDetected = " + d.MirrorDetected);

            report.Check(Math.Abs(d.SigmaRatio - 1.0283) < 1e-3, "真机矩阵 σ1/σ2 ≈ 1.028（形状合法）",
                string.Format(CultureInfo.InvariantCulture, "σ1/σ2 = {0:F4}，失真量 {1:F2}%", d.SigmaRatio, d.AnisotropyPct));

            report.Check(!d.BadShape, "真机矩阵不应被判为形状非法（容差 0.03）",
                string.Format(CultureInfo.InvariantCulture, "|σ1/σ2 − 1| = {0:F4}", Math.Abs(d.SigmaRatio - 1.0)));

            report.Check(Math.Abs(d.AxisScaleDeviationPct - 2.31) < 0.05, "真机矩阵两轴当量差 ≈ 2.31%（列范数）",
                string.Format(CultureInfo.InvariantCulture, "col={0:F5} row={1:F5}，差 {2:F2}%",
                    d.ColumnNormFirst, d.ColumnNormSecond, d.AxisScaleDeviationPct));

            // (b) ★ 镜像在 EyeInHand 下不得阻断（否则会把真机矩阵整个毙掉）
            report.Check(!d.MirrorIsBlocker, "EyeInHand 下镜像不构成硬阻",
                "MirrorIsBlocker = " + d.MirrorIsBlocker);
            report.Check(!d.HasBlocker, "★ EyeInHand + 真机矩阵 ⇒ HasBlocker 必须为 false（否则真机全部标定都发不出去）",
                "HasBlocker = " + d.HasBlocker);

            // (c) EyeToHand 下同样的矩阵必须被拦
            CalibDiagnostics dFixed = MatrixQualityEvaluator.Evaluate(
                RealStationHom, samples, MakeTopology(10.0, 10.0, CameraMountKind.EyeToHand), null, t);
            report.Check(dFixed.MirrorIsBlocker && dFixed.HasBlocker, "EyeToHand 下同一矩阵必须被阻断",
                string.Format(CultureInfo.InvariantCulture, "MirrorIsBlocker={0} HasBlocker={1}",
                    dFixed.MirrorIsBlocker, dFixed.HasBlocker));

            // (d) ★ 反证：对角元判据在真机矩阵上会假报警（39.6%），而列范数只差 2.31%
            double naivePct = NaiveDiagonalDeviationPct(RealStationHom);
            report.Check(naivePct > 30.0 && d.AxisScaleDeviationPct < 3.0,
                "★（反证）|h11| vs |h22| 在真机矩阵上假报警",
                string.Format(CultureInfo.InvariantCulture,
                    "对角元差 {0:F2}%（会误判形状非法）vs 列范数差 {1:F2}%（真实）—— 所以判据只能用列范数",
                    naivePct, d.AxisScaleDeviationPct));
        }

        // ── ③ 列范数对旋转不变 ──────────────────────────────────────────
        private static void ColumnNormIsRotationInvariant(SelfCheckReport report, double stepX, double stepY)
        {
            var world = new SimulatedWorld();
            world.PixelNoisePx = 0.0;
            world.FeedbackNoiseMm = 0.0;
            world.ImageRotationDeg = 151.0;   // 本工位的真实量级

            CalibTopology topo = MakeTopology(stepX, stepY);
            List<CalibSample> samples = BuildIdealSamples(world, topo, stepX, stepY);

            NinePointResult solved = NinePointSolver.Solve(samples, topo);
            if (!solved.Success)
            {
                report.Check(false, "151° 大旋转（解算）", "解算失败：" + solved.Error);
                return;
            }

            CalibDiagnostics d = MatrixQualityEvaluator.Evaluate(solved.H, samples, topo, null, new MatrixQualityThresholds());

            report.Check(d.SigmaRatio < 1.001, "151° 下 σ1/σ2 ≈ 1.000（旋转不改形状）",
                string.Format(CultureInfo.InvariantCulture, "σ1/σ2 = {0:F6}", d.SigmaRatio));

            report.Check(d.AxisScaleDeviationPct < 0.001, "151° 下两轴当量偏差 ≈ 0（列范数旋转不变）",
                string.Format(CultureInfo.InvariantCulture, "偏差 = {0:F6}%（列范数 {1:F6} / {2:F6}）",
                    d.AxisScaleDeviationPct, d.ColumnNormFirst, d.ColumnNormSecond));

            report.Check(!d.HasBlocker, "151° 且无异常时不应阻断", "HasBlocker = " + d.HasBlocker);
        }

        // ── ④ ★ 对角元判据随旋转角剧烈摆动 → 数值上不可用 ────────────────
        /// <summary>
        /// ★ 决定性反证：把<b>同一个物理缺陷</b>（20% 两轴当量差）放在不同旋转角下，
        /// 看两个判据各自怎么报。
        ///
        ///   列范数 / σ1σ2  → 无论转多少度都报 20% / 1.25（对旋转不变，说的是矩阵本身的形状）；
        ///   对角元 |h11| vs |h22| → 0° 时报 20%，90° 时报 <b>0%</b> ——
        ///     同一个缺陷在 90° 附近<b>凭空消失</b>，因为它把缺陷量算进了 cos θ 里。
        ///
        /// 结论：对角元判据把"轴当量差"和"图像旋转角"混在了一起，不可用作门禁。
        /// （本工位真实旋转角 ≈151°，正落在它假报警最凶的区间；真机矩阵上它报 39.6%。）
        /// </summary>
        private static void DiagonalCriterionIsRotationDependent(SelfCheckReport report)
        {
            double[] angles = new double[] { 0.0, 15.0, 30.0, 45.0, 60.0, 75.0, 90.0 };
            var table = new List<string>(angles.Length);

            double naiveMin = double.MaxValue;
            double naiveMax = double.MinValue;
            double colMin = double.MaxValue;
            double colMax = double.MinValue;
            double sigMin = double.MaxValue;
            double sigMax = double.MinValue;
            double minAbsDiagAt90 = double.MaxValue;
            double colNormAt90 = 0.0;

            for (int k = 0; k < angles.Length; k++)
            {
                double ang = angles[k];

                var world = new SimulatedWorld();
                world.PixelNoisePx = 0.0;
                world.FeedbackNoiseMm = 0.0;
                world.ImageRotationDeg = ang;
                world.MmPerPixelYFactor = 0.8;   // ★ 物理缺陷固定不变：20% 两轴当量差

                CalibTopology topo = MakeTopology(10.0, 10.0);
                List<CalibSample> samples = BuildIdealSamples(world, topo, 10.0, 10.0);
                NinePointResult solved = NinePointSolver.Solve(samples, topo);
                if (!solved.Success)
                {
                    report.Check(false, "★（反证）旋转角扫描（解算）",
                        string.Format(CultureInfo.InvariantCulture, "解算失败 @ {0}°：{1}", ang, solved.Error));
                    return;
                }

                CalibDiagnostics d = MatrixQualityEvaluator.Evaluate(
                    solved.H, samples, topo, null, new MatrixQualityThresholds());

                double naive = NaiveDiagonalDeviationPct(solved.H);

                naiveMin = Math.Min(naiveMin, naive);
                naiveMax = Math.Max(naiveMax, naive);
                colMin = Math.Min(colMin, d.AxisScaleDeviationPct);
                colMax = Math.Max(colMax, d.AxisScaleDeviationPct);
                sigMin = Math.Min(sigMin, d.SigmaRatio);
                sigMax = Math.Max(sigMax, d.SigmaRatio);

                if (ang == 90.0)
                {
                    minAbsDiagAt90 = Math.Max(Math.Abs(solved.H.H11), Math.Abs(solved.H.H22));
                    colNormAt90 = Math.Sqrt(solved.H.H11 * solved.H.H11 + solved.H.H21 * solved.H.H21);
                }

                table.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0:F0}°: 列范数 {1:F2}% / 对角元 {2:F2}%", ang, d.AxisScaleDeviationPct, naive));
            }

            string sweep = string.Join(" | ", table.ToArray());

            // (a) 列范数：缺陷恒定可见（对旋转不变）
            report.Check((colMax - colMin) < 0.05 && colMax > 19.5,
                "★（反证）列范数在同一缺陷下恒定报 20%（对旋转不变）",
                string.Format(CultureInfo.InvariantCulture,
                    "扫描 {0:F1}% ~ {1:F1}%（摆动 {2:F3} 个百分点）",
                    colMin, colMax, colMax - colMin));

            // (b) 对角元：同一缺陷从 20% 摆到 0%（缺陷在 90° 凭空消失）
            report.Check(naiveMax > 19.0 && naiveMin < 1.0 && (naiveMax - naiveMin) > 18.0,
                "★（反证）同一缺陷下对角元判据 20% ⇒ 0%（缺陷凭空消失）→ 不可用作门禁",
                string.Format(CultureInfo.InvariantCulture,
                    "扫描 {0:F2}% ~ {1:F2}%（摆动 {2:F2} 个百分点）　{3}",
                    naiveMin, naiveMax, naiveMax - naiveMin, sweep));

            // (c) σ1/σ2 同样对旋转不变
            report.Check((sigMax - sigMin) < 1e-3 && Math.Abs(sigMax - 1.25) < 1e-3,
                "σ1/σ2 在同一缺陷下恒定报 1.25（对旋转不变）",
                string.Format(CultureInfo.InvariantCulture, "扫描 {0:F4} ~ {1:F4}", sigMin, sigMax));

            // (d) 90° 处对角元数值退化（根源）
            report.Check(minAbsDiagAt90 < 1e-12,
                "90° 处对角元退化到 ~1e-15（相对 mm 量级差 16 个数量级）",
                string.Format(CultureInfo.InvariantCulture, "max(|h11|,|h22|) = {0:E3}，而列范数 = {1:F5}",
                    minAbsDiagAt90, colNormAt90));
        }

        // ── ⑤ ★ 剪切判据：轴对齐分母不可用（与对角元同一个错）────────────
        /// <summary>
        /// ★ 又一个"分母被旋转吃掉"的坑：旧写法 <c>shear = |h12| / |h11|</c>，
        /// 分母 h11/h22 是"像素 X/Y 轴在世界系下的分量"，图像一转就趋近 0。
        /// 真机矩阵（151°）上它报 <b>X=3792.8% / Y=2238.5%</b>，而真实剪切只有 1.67% ——
        /// 于是每次标定都会误报剪切告警。
        /// 正确做法：两列向量夹角 → <c>shear = |cos(夹角)|</c>，天然旋转不变。
        /// </summary>
        private static void ShearCriterionIsRotationInvariant(SelfCheckReport report)
        {
            // (a) 真机矩阵：不变式给出 1.67%，且不应告警
            var samples = SamplesFromMatrix(RealStationHom, 9);
            CalibDiagnostics d = MatrixQualityEvaluator.Evaluate(
                RealStationHom, samples, MakeTopology(10.0, 10.0, CameraMountKind.EyeInHand), null,
                new MatrixQualityThresholds());

            report.Check(Math.Abs(d.ShearRatio - 0.01666) < 1e-4 && Math.Abs(d.ColumnAngleDeg - 89.0452) < 1e-3,
                "★ 真机矩阵剪切（列夹角法）= 1.67% / 列夹角 89.045°",
                string.Format(CultureInfo.InvariantCulture,
                    "|cos| = {0:F5}（{1:F3}%），列夹角 {2:F4}°（偏离正交 {3:F4}°）",
                    d.ShearRatio, d.ShearRatio * 100.0, d.ColumnAngleDeg, Math.Abs(90.0 - d.ColumnAngleDeg)));

            report.Check(!d.ShearSuspicious,
                "★ 真机矩阵不应报剪切告警（列夹角法）",
                string.Format(CultureInfo.InvariantCulture, "ShearSuspicious = {0}（阈值 {1}）",
                    d.ShearSuspicious, 0.05));

            double legacyReal = NaiveAxisAlignedShearPct(RealStationHom);
            report.Check(legacyReal > 1000.0,
                "★（反证）轴对齐分母 |h12|/|h11| 在真机矩阵上爆到 3792.8% → 必然误报",
                string.Format(CultureInfo.InvariantCulture,
                    "轴对齐 {0:F1}%（假）vs 列夹角法 {1:F2}%（真）—— 差 {2:F0} 倍",
                    legacyReal, d.ShearRatio * 100.0, legacyReal / (d.ShearRatio * 100.0)));

            // 报告文本能被真正渲染出来（格式串参数个数写错会在这里抛 FormatException）
            string report1;
            try
            {
                report1 = d.ToReportText();
            }
            catch (Exception ex)
            {
                report1 = null;
                report.Check(false, "健康报告文本可渲染", "抛异常：" + ex.Message);
            }

            if (report1 != null)
            {
                report.Check(report1.Contains("健康检查") && report1.Contains("正交性")
                             && report1.Contains("89.045"),
                    "健康报告文本可渲染且含剪切/正交性行",
                    "长度 " + report1.Length + " 字符，含列夹角 89.045°");
            }

            // (b) 旋转扫描：把真机矩阵整体旋转，两种写法各自怎么变
            double[] angles = new double[] { 0.0, 30.0, 60.0, 90.0, 151.0 };
            var table = new List<string>(angles.Length);
            double invMin = double.MaxValue, invMax = double.MinValue;
            double legMin = double.MaxValue, legMax = double.MinValue;

            for (int k = 0; k < angles.Length; k++)
            {
                double ang = angles[k];
                HomMat2D r = RotateMatrix(RealStationHom, ang);

                CalibDiagnostics dr = MatrixQualityEvaluator.Evaluate(
                    r, SamplesFromMatrix(r, 9), MakeTopology(10.0, 10.0, CameraMountKind.EyeInHand), null,
                    new MatrixQualityThresholds());

                double leg = NaiveAxisAlignedShearPct(r);

                invMin = Math.Min(invMin, dr.ShearRatio * 100.0);
                invMax = Math.Max(invMax, dr.ShearRatio * 100.0);
                legMin = Math.Min(legMin, leg);
                legMax = Math.Max(legMax, leg);

                table.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0:F0}°: 不变式 {1:F2}% / 轴对齐 {2:F1}%", ang, dr.ShearRatio * 100.0, leg));
            }

            report.Check((invMax - invMin) < 1e-6,
                "★（反证）列夹角法剪切对旋转完全不变",
                string.Format(CultureInfo.InvariantCulture,
                    "扫描 {0:F3}% ~ {1:F3}%（摆动 {2:E1} 个百分点）",
                    invMin, invMax, invMax - invMin));

            report.Check((legMax - legMin) > 1000.0 && legMin < 10.0,
                "★（反证）轴对齐剪切比随旋转从 3792.8% 摆到 4.5% → 数值上不可用",
                string.Format(CultureInfo.InvariantCulture,
                    "扫描 {0:F1}% ~ {1:F1}%（摆动 {2:F0} 个百分点）　{3}",
                    legMin, legMax, legMax - legMin, string.Join(" | ", table.ToArray())));
        }

        // ── ⑥ 真实两轴失真必须被 σ 抓住 ────────────────────────────────
        private static void AnisotropyIsCaughtBySigma(SelfCheckReport report, double stepX, double stepY)
        {
            var world = new SimulatedWorld();
            world.PixelNoisePx = 0.0;
            world.FeedbackNoiseMm = 0.0;
            world.ImageRotationDeg = 37.0;     // 任意角度，验证与旋转无关
            world.MmPerPixelYFactor = 0.8;     // 注入 20% 两轴当量差

            CalibTopology topo = MakeTopology(stepX, stepY);
            List<CalibSample> samples = BuildIdealSamples(world, topo, stepX, stepY);
            NinePointResult solved = NinePointSolver.Solve(samples, topo);
            if (!solved.Success)
            {
                report.Check(false, "两轴失真场景（解算）", "解算失败：" + solved.Error);
                return;
            }

            CalibDiagnostics d = MatrixQualityEvaluator.Evaluate(solved.H, samples, topo, null, new MatrixQualityThresholds());

            report.Check(Math.Abs(d.AxisScaleDeviationPct - 20.0) < 0.01, "注入 20% 两轴当量差 → 列范数如实报出",
                string.Format(CultureInfo.InvariantCulture, "列范数差 {0:F2}%", d.AxisScaleDeviationPct));

            report.Check(Math.Abs(d.SigmaRatio - 1.25) < 1e-3, "同一失真 → σ1/σ2 = 1.25（与旋转角无关）",
                string.Format(CultureInfo.InvariantCulture, "σ1/σ2 = {0:F4}（旋转角 {1}°）", d.SigmaRatio, world.ImageRotationDeg));

            report.Check(d.BadShape, "该失真必须被判为形状非法", "BadShape = " + d.BadShape);
        }

        // ── ⑦ 形状门禁与放宽开关 ────────────────────────────────────────
        private static void ShapeGateBlockerAndRelax(SelfCheckReport report)
        {
            var samples = SamplesFromMatrix(RealStationHom, 9);

            // 人为做一个"形状非法"的矩阵（横向拉 20%）
            var skew = new HomMat2D(
                RealStationHom.H11, RealStationHom.H12 * 1.2, RealStationHom.H13,
                RealStationHom.H21, RealStationHom.H22 * 1.2, RealStationHom.H23);

            var strict = new MatrixQualityThresholds { ShapeGateRelaxed = false };
            var relaxed = new MatrixQualityThresholds { ShapeGateRelaxed = true };

            CalibDiagnostics dStrict = MatrixQualityEvaluator.Evaluate(
                skew, samples, MakeTopology(10.0, 10.0, CameraMountKind.EyeInHand), null, strict);
            CalibDiagnostics dRelaxed = MatrixQualityEvaluator.Evaluate(
                skew, samples, MakeTopology(10.0, 10.0, CameraMountKind.EyeInHand), null, relaxed);

            report.Check(dStrict.BadShape && dStrict.HasBlocker, "形状非法 ⇒ 默认阻断发布",
                string.Format(CultureInfo.InvariantCulture, "σ1/σ2 = {0:F4}，HasBlocker = {1}", dStrict.SigmaRatio, dStrict.HasBlocker));

            report.Check(dRelaxed.BadShape && !dRelaxed.HasBlocker, "放宽门禁（诊断模式）⇒ 降级为提醒、不再阻断",
                string.Format(CultureInfo.InvariantCulture, "BadShape = {0}，HasBlocker = {1}", dRelaxed.BadShape, dRelaxed.HasBlocker));
        }

        // ── ⑧ 噪声下 LOO 有定义 ─────────────────────────────────────────
        private static void LooDefinedUnderNoise(SelfCheckReport report, double stepX, double stepY)
        {
            var world = new SimulatedWorld();
            world.PixelNoisePx = 0.35;
            world.FeedbackNoiseMm = 0.01;
            world.ResetRandom();

            CalibTopology topo = MakeTopology(stepX, stepY);
            List<CalibSample> samples = BuildIdealSamples(world, topo, stepX, stepY, true);

            NinePointResult solved = NinePointSolver.Solve(samples, topo);
            if (!solved.Success)
            {
                report.Check(false, "噪声下解算", "解算失败：" + solved.Error);
                return;
            }

            CalibDiagnostics d = MatrixQualityEvaluator.Evaluate(solved.H, samples, topo, null, new MatrixQualityThresholds());

            report.Check(solved.RmsMm > 0.0 && solved.RmsMm < 0.05, "噪声下残差为有限小量",
                string.Format(CultureInfo.InvariantCulture, "RMS = {0:F4} mm", solved.RmsMm));

            report.Check(!double.IsNaN(d.LooRmsMm) && d.LooRmsMm > 0.0, "LOO 有定义且 > 0",
                string.Format(CultureInfo.InvariantCulture, "LOO = {0:F4} mm（自拟合 {1:F4} mm）", d.LooRmsMm, d.RmsMm));
        }

        // ── ⑨ .tup 往返 + 逐位字节格式 ──────────────────────────────────
        private static void TupRoundTripAndByteFormat(SelfCheckReport report)
        {
            HomMat2D h = RealStationHom;

            string temp = Path.Combine(Path.GetTempPath(), "vct_selfcheck_" + Guid.NewGuid().ToString("N") + ".tup");
            try
            {
                HomMatIO.WriteTup(temp, h);

                byte[] bytes = File.ReadAllBytes(temp);
                string text = System.Text.Encoding.ASCII.GetString(bytes);

                report.Check(text.StartsWith("06\n", StringComparison.Ordinal), ".tup 首行为十六进制元素数 06",
                    "实际开头：" + Escape(text.Substring(0, Math.Min(6, text.Length))));

                report.Check(text.IndexOf('\r') < 0, ".tup 使用 LF 换行（无 CR）", "含 CR = " + (text.IndexOf('\r') >= 0));

                string[] lines = text.Split('\n');
                string secondLine = lines.Length > 1 ? lines[1] : string.Empty;
                report.Check(secondLine.StartsWith("2 ", StringComparison.Ordinal), ".tup 每行以类型码 2（double）开头",
                    "第 2 行：" + secondLine);

                // ★ 逐位对照真机产物
                const string expectedSecondLine = "2 -5.3606559567018668e-03";
                report.Check(secondLine == expectedSecondLine, ".tup 数值格式逐位等价 %.16e",
                    "第 2 行：" + secondLine + "（期望 " + expectedSecondLine + "）");

                // 往返逐位一致
                HomMat2D back = HomMatIO.ReadTup(temp);
                double[] a = h.ToArray();
                double[] b = back.ToArray();
                bool identical = true;
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i])
                    {
                        identical = false;
                    }
                }

                report.Check(identical, ".tup 往返逐位一致", identical ? "6 参数全部相同" : "存在不一致");

                report.Check(bytes.Length == 155, ".tup 文件长度与真机产物一致（155 字节）", "实际 " + bytes.Length + " 字节");
            }
            catch (Exception ex)
            {
                report.Check(false, ".tup 自检", ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        // ── 辅助 ────────────────────────────────────────────────────────

        /// <summary>旧的"对角元判据"（|h11| vs |h22|）—— 只用于反证它不可用。</summary>
        private static double NaiveDiagonalDeviationPct(HomMat2D h)
        {
            double a = Math.Abs(h.H11);
            double b = Math.Abs(h.H22);
            double m = Math.Max(a, b);
            return m > 1e-12 ? Math.Abs(a - b) / m * 100.0 : 0.0;
        }

        /// <summary>
        /// 旧的"轴对齐剪切比" max(|h12|/|h11|, |h21|/|h22|) —— 只用于反证它不可用。
        /// 分母 h11/h22 会被图像旋转吃掉，比值随之爆炸。
        /// </summary>
        private static double NaiveAxisAlignedShearPct(HomMat2D h)
        {
            double sx = Math.Abs(h.H12) / Math.Max(Math.Abs(h.H11), 1e-9);
            double sy = Math.Abs(h.H21) / Math.Max(Math.Abs(h.H22), 1e-9);
            return Math.Max(sx, sy) * 100.0;
        }

        /// <summary>把仿射矩阵在<b>世界侧</b>整体旋转 angleDeg（用于验证判据的旋转不变性）。</summary>
        private static HomMat2D RotateMatrix(HomMat2D h, double angleDeg)
        {
            double r = angleDeg * Math.PI / 180.0;
            double c = Math.Cos(r);
            double s = Math.Sin(r);

            // A' = R · A（平移分量不动；R 只作用在世界坐标的 2×2 线性部分）
            return new HomMat2D(
                c * h.H11 - s * h.H21, c * h.H12 - s * h.H22, h.H13,
                s * h.H11 + c * h.H21, s * h.H12 + c * h.H22, h.H23);
        }

        private static CalibTopology MakeTopology(double stepX, double stepY, CameraMountKind mount = CameraMountKind.EyeInHand)
        {
            return new CalibTopology
            {
                BasePosXY = new Vec2(300.0, 200.0),
                StepX = stepX,
                StepY = stepY,
                WorkZ = -5.0,
                CalibU0 = 0.0,
                Hand = Handedness.Lefty,
                CameraMount = mount
            };
        }

        /// <summary>
        /// 用理想相机模型造 9 对 (u, P)。
        /// ★ 这里造的是<b>理想投影</b>而不是合成图的真值像素 —— 本自检针对的是<b>解算器</b>，
        ///   所以要把相机模型本身排除掉。"合成图 → 真提取器 → 解算"那条链由采样编排覆盖。
        /// </summary>
        private static List<CalibSample> BuildIdealSamples(
            SimulatedWorld world, CalibTopology topo, double stepX, double stepY, bool useNoisy = false)
        {
            MotionPose[] targets = NinePointGrid.BuildTargets(topo.BasePosXY, topo.WorkZ, topo.CalibU0, stepX, stepY);
            var list = new List<CalibSample>(targets.Length);

            for (int i = 0; i < targets.Length; i++)
            {
                Vec2 u = useNoisy ? world.ProjectNoisy(targets[i].Xy) : world.Project(targets[i].Xy);
                Vec2 fb = useNoisy ? world.NoisyFeedback(targets[i].Xy) : targets[i].Xy;

                list.Add(new CalibSample
                {
                    Index = i + 1,
                    State = CalibSampleState.Ok,
                    PlanXy = targets[i].Xy,
                    PlanZ = targets[i].Z,
                    PlanU = targets[i].U,
                    FeedbackXy = fb,
                    FeedbackU = targets[i].U,
                    Pixel = u,
                    HasPixel = true,
                    Quality = 1.0,
                    CandidateCount = 1
                });
            }

            return list;
        }

        /// <summary>
        /// 由给定矩阵反造一组"完美自洽"的样本：Pixel 取规则网格，
        /// Feedback = H(pixel) ⇒ 残差恒为 0，于是可以纯净地评估<b>矩阵本身</b>的形状指标。
        /// </summary>
        private static List<CalibSample> SamplesFromMatrix(HomMat2D h, int count)
        {
            var offsets = NinePointGrid.BuildOffsets(100.0, 100.0);
            var list = new List<CalibSample>(count);

            for (int i = 0; i < count && i < offsets.Length; i++)
            {
                var pixel = new Vec2(640.0 + offsets[i].X, 512.0 + offsets[i].Y);
                list.Add(new CalibSample
                {
                    Index = i + 1,
                    State = CalibSampleState.Ok,
                    Pixel = pixel,
                    HasPixel = true,
                    Quality = 1.0,
                    CandidateCount = 1,
                    FeedbackXy = h.Transform(pixel),
                    FeedbackU = 0.0,
                    PlanXy = h.Transform(pixel)
                });
            }

            return list;
        }

        private static string Escape(string s)
        {
            return s.Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
