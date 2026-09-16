using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// 质量门限与开关。默认值全部来自主项目 <c>BuildHomMatHealthReport</c> 的既有口径。
    /// </summary>
    public sealed class MatrixQualityThresholds
    {
        /// <summary>形状判据容差：|σ1/σ2 − 1| 超过它即"形状非法"（主项目用 0.03）。</summary>
        public double SigmaRatioTolerance = 0.03;

        /// <summary>
        /// 剪切提醒阈值：|cos(两列夹角)| 超过它即提示（主项目用 0.05）。
        /// ★ 这里比的是"偏离正交的程度"（0 = 严格正交），0.05 相当于偏离 2.87°。
        /// 提示性，不阻断。
        /// </summary>
        public double ShearWarnRatio = 0.05;

        /// <summary>网格边长离散度（CV）提醒阈值（主项目用 0.10）。</summary>
        public double GridCvWarn = 0.10;

        /// <summary>网格夹角偏离 90° 的提醒阈值（主项目用 3°）。</summary>
        public double GridAngleWarnDeg = 3.0;

        /// <summary>RMS 提醒阈值（主项目用 0.5 mm）。</summary>
        public double RmsWarnMm = 0.5;

        /// <summary>
        /// ★ 放宽形状门禁（对应主项目的 <c>RelaxShapeGate</c>「诊断模式」）。
        /// 打开后"形状非法"降级为提醒、不再阻断发布 —— 纯软件标定无法根治相机安装几何造成的失真，
        /// 允许先保存推进，但必须在报告里留下明确提醒。
        /// </summary>
        public bool ShapeGateRelaxed;
    }

    /// <summary>
    /// 矩阵质量评估器。★ 口径与主项目 <c>CalibrationService.BuildHomMatHealthReport</c>
    /// <b>逐条对等</b>，再额外提供 LOO 留一法。
    ///
    /// 为什么形状（σ1/σ2）优先于 RMS：
    ///   落点 = 差分 H(p_tip) − H(u)。仿射吸收不掉的系统性误差全进 σ1/σ2，随机误差全进 RMS。
    ///   主项目实测：失真 22.7% 时跨 80 mm 就偏约 19 mm，而九点 RMS 只有 0.541 mm。
    ///   <b>残差小 ≠ 落点准</b>，所以先看形状、再看 LOO、最后才看自拟合 RMS。
    /// </summary>
    public static class MatrixQualityEvaluator
    {
        /// <summary>全量评估。</summary>
        public static CalibDiagnostics Evaluate(
            HomMat2D h,
            IList<CalibSample> samples,
            CalibTopology topology,
            Vec2? imageCenterPx,
            MatrixQualityThresholds thresholds)
        {
            if (thresholds == null)
            {
                thresholds = new MatrixQualityThresholds();
            }

            var d = new CalibDiagnostics();
            if (topology != null)
            {
                d.CameraMount = topology.CameraMount;
            }

            var usable = NinePointSolver.CollectUsable(samples);

            // ── ① 两轴像素当量：列向量范数（旋转不变）──────
            double h11 = h.H11, h12 = h.H12, h21 = h.H21, h22 = h.H22;
            d.ColumnNormFirst = Math.Sqrt(h11 * h11 + h21 * h21);
            d.ColumnNormSecond = Math.Sqrt(h12 * h12 + h22 * h22);
            double scaleMax = Math.Max(d.ColumnNormFirst, d.ColumnNormSecond);
            d.AxisScaleDeviationPct = scaleMax > 1e-12
                ? Math.Abs(d.ColumnNormFirst - d.ColumnNormSecond) / scaleMax * 100.0
                : 0.0;

            // ── 形状：奇异值比（唯一硬拦判据）──────
            //    σ1,2² = (trA ± √(trA² − 4·detA²)) / 2，其中 trA = Σ 元素平方
            //    （等价于 AᵀA 的特征值，两种写法数学上一致）
            double detA = h11 * h22 - h12 * h21;
            d.DetA = detA;

            double trA = h11 * h11 + h21 * h21 + h12 * h12 + h22 * h22;
            double disc = Math.Sqrt(Math.Max(0.0, trA * trA - 4.0 * detA * detA));
            d.Sigma1 = Math.Sqrt(Math.Max(0.0, (trA + disc) / 2.0));
            d.Sigma2 = Math.Sqrt(Math.Max(1e-18, (trA - disc) / 2.0));
            d.SigmaRatio = d.Sigma2 > 1e-12 ? d.Sigma1 / d.Sigma2 : double.NaN;
            d.AnisotropyPct = double.IsNaN(d.SigmaRatio) ? double.NaN : (d.SigmaRatio - 1.0) * 100.0;

            d.BadShape = double.IsNaN(d.SigmaRatio)
                         || Math.Abs(d.SigmaRatio - 1.0) > thresholds.SigmaRatioTolerance;

            // ── ② 正交性 / 剪切（提示性）──────
            //    ★ 必须用「两列向量的夹角」，不能用 |h12|/|h11|：
            //      h11/h22 是像里"像素 X 轴 / Y 轴"在世界系下的投影，图像一旋转它们
            //      就趋近 0，比值随之爆掉（真机 151° 下 X=3792.8% / Y=2238.5%），
            //      而真实剪切只有 1.67% —— 结果每次标定都误报剪切告警。
            //      列夹角 = 纯旋转不变量，才是"两轴是否正交"的正确度量。
            double colDot = h11 * h12 + h21 * h22;
            double colNorm1 = Math.Sqrt(h11 * h11 + h21 * h21);
            double colNorm2 = Math.Sqrt(h12 * h12 + h22 * h22);
            double cosAngle = (colNorm1 > 1e-12 && colNorm2 > 1e-12)
                ? colDot / (colNorm1 * colNorm2)
                : 0.0;
            cosAngle = Math.Max(-1.0, Math.Min(1.0, cosAngle));
            d.ColumnAngleDeg = Math.Acos(cosAngle) * 180.0 / Math.PI;
            d.ShearRatio = Math.Abs(cosAngle);
            d.ShearSuspicious = d.ShearRatio > thresholds.ShearWarnRatio;

            // ── ③ 行列式 / 镜像 ──────
            //    ★ det<0 在 EyeInHand 下是固有物理，不阻断；只有 EyeToHand 才红阻。
            d.MirrorDetected = detA < 0.0;
            d.MirrorIsBlocker = d.MirrorDetected && d.CameraMount == CameraMountKind.EyeToHand;

            // ── ④⑤ 网格重建 / 夹角（需要完整的 9 点）──────
            if (usable.Count >= 9)
            {
                ComputeGrid(usable, h, d, thresholds);
            }

            // ── 残差 ──────
            var residuals = new List<double>(usable.Count);
            var radii = new List<double>(usable.Count);
            Vec2 pixelCenter = imageCenterPx.HasValue
                ? imageCenterPx.Value
                : GeometryUtil.Centroid(CollectPixels(usable));

            for (int i = 0; i < usable.Count; i++)
            {
                CalibSample s = usable[i];
                residuals.Add(h.Transform(s.Pixel).DistanceTo(s.FeedbackXy));
                radii.Add(s.Pixel.DistanceTo(pixelCenter));
            }

            d.PerPointResidualMm = residuals.ToArray();
            d.PerPointRadiusMm = radii.ToArray();
            d.RmsMm = GeometryUtil.Rms(residuals);
            d.LooRmsMm = ComputeLooRms(usable);
            d.ExcludedCount = CountExcluded(samples);
            d.NearestCornerRadiusMm = NinePointSolver.ComputeNearestCornerRadius(topology);

            // ── 结论与门禁 ──────
            BuildNotes(d, thresholds);
            return d;
        }

        /// <summary>
        /// ④ 网格重建（行/列边长 CV）+ ⑤ 行/列夹角。
        /// ★ 与主项目同构：把像素经 H 映射后再量边长，这样它衡量的是<b>矩阵本身</b>的规整度。
        ///   注意点序是按编号 1..9 逐行排列：#2/#8 是上/中下点（纵向），#4/#6 是左/右点（横向）。
        /// </summary>
        private static void ComputeGrid(IList<CalibSample> usable, HomMat2D h, CalibDiagnostics d, MatrixQualityThresholds t)
        {
            // 按编号升序取前 9 个（usable 已按 Index 排序）
            var mapped = new Vec2[9];
            for (int i = 0; i < 9; i++)
            {
                mapped[i] = h.Transform(usable[i].Pixel);
            }

            var rowDists = new List<double>();
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 2; col++)
                {
                    int i = row * 3 + col;
                    int j = i + 1;
                    rowDists.Add(mapped[i].DistanceTo(mapped[j]));
                }
            }

            var colDists = new List<double>();
            for (int col = 0; col < 3; col++)
            {
                for (int r = 0; r < 2; r++)
                {
                    int i = r * 3 + col;
                    int j = i + 3;
                    colDists.Add(mapped[i].DistanceTo(mapped[j]));
                }
            }

            d.GridRowEdgeMeanMm = GeometryUtil.Mean(rowDists);
            d.GridRowEdgeCvPct = Cv(rowDists) * 100.0;
            d.GridColEdgeMeanMm = GeometryUtil.Mean(colDists);
            d.GridColEdgeCvPct = Cv(colDists) * 100.0;
            d.GridIrregular = (Cv(rowDists) > t.GridCvWarn) || (Cv(colDists) > t.GridCvWarn);

            // 纵向 = #8 − #2（索引 7 与 1）；横向 = #6 − #4（索引 5 与 3）
            Vec2 v = mapped[7] - mapped[1];
            Vec2 hz = mapped[5] - mapped[3];
            double denom = v.Length * hz.Length;
            if (denom > 1e-12)
            {
                double cosAng = (v.X * hz.X + v.Y * hz.Y) / denom;
                if (cosAng > 1.0) cosAng = 1.0;
                if (cosAng < -1.0) cosAng = -1.0;
                d.GridAngleDeg = Math.Acos(cosAng) * 180.0 / Math.PI;
            }

            d.GridAngleSuspicious = Math.Abs(d.GridAngleDeg - 90.0) > t.GridAngleWarnDeg;
        }

        /// <summary>
        /// LOO 留一法：轮流拿掉一个点，用其余点拟合，再算被拿掉那个点的预测误差。
        /// 这是对"真实落点能力"更诚实的估计（自拟合 RMS 必然偏乐观）。
        /// </summary>
        public static double ComputeLooRms(IList<CalibSample> usable)
        {
            if (usable == null || usable.Count <= NinePointSolver.MinUsablePoints)
            {
                return double.NaN;
            }

            var errs = new List<double>(usable.Count);
            for (int hold = 0; hold < usable.Count; hold++)
            {
                var rows = new List<double[]>(usable.Count - 1);
                var rhsX = new List<double>(usable.Count - 1);
                var rhsY = new List<double>(usable.Count - 1);
                for (int i = 0; i < usable.Count; i++)
                {
                    if (i == hold)
                    {
                        continue;
                    }

                    CalibSample s = usable[i];
                    rows.Add(new double[] { s.Pixel.X, s.Pixel.Y, 1.0 });
                    rhsX.Add(s.FeedbackXy.X);
                    rhsY.Add(s.FeedbackXy.Y);
                }

                double[] sx = GeometryUtil.SolveLeastSquares(rows, rhsX, 3);
                double[] sy = GeometryUtil.SolveLeastSquares(rows, rhsY, 3);
                if (sx == null || sy == null)
                {
                    return double.NaN;
                }

                var h = new HomMat2D(sx[0], sx[1], sx[2], sy[0], sy[1], sy[2]);
                CalibSample held = usable[hold];
                errs.Add(h.Transform(held.Pixel).DistanceTo(held.FeedbackXy));
            }

            return GeometryUtil.Rms(errs);
        }

        /// <summary>总体标准差 / 均值（与主项目 StdDev 一致：除以 N，不是 N−1）。</summary>
        private static double Cv(IList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0.0;
            }

            double mean = GeometryUtil.Mean(values);
            if (mean <= 1e-12)
            {
                return 0.0;
            }

            double s = 0.0;
            for (int i = 0; i < values.Count; i++)
            {
                double dv = values[i] - mean;
                s += dv * dv;
            }

            return Math.Sqrt(s / values.Count) / mean;
        }

        private static void BuildNotes(CalibDiagnostics d, MatrixQualityThresholds t)
        {
            // ① 形状
            if (d.BadShape)
            {
                if (t.ShapeGateRelaxed)
                {
                    d.Notes.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "⚠ 形状非法（各向异性 σ1/σ2 = {0:F3}，失真量 {1:F1}%）—— 诊断模式已放宽为提醒。"
                        + "成因多为相机安装几何（非正交/各向异性/roll）而非九点数据污染；"
                        + "此矩阵用于引导会按距离放大误差，务必用『走位比对』复核。",
                        d.SigmaRatio, d.AnisotropyPct));
                }
                else
                {
                    d.HasBlocker = true;
                    d.Notes.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "⛔ 形状非法（各向异性 σ1/σ2 = {0:F3}，失真量 {1:F1}% > 容差 {2:P0}）→ 禁止发布。"
                        + "落点误差 = 失真 × 差分距离，九点 RMS 小根本发现不了。"
                        + "先跑『直线性探针』：沿世界 +X 走 0/20/40mm 验三点是否共线 —— 弦弧差大 = 世界侧轨迹是弧线，与相机无关。",
                        d.SigmaRatio, d.AnisotropyPct, t.SigmaRatioTolerance));
                }
            }
            else
            {
                d.Notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "✔ 形状合法（各向异性 σ1/σ2 = {0:F4}，应≈1.000）。",
                    d.SigmaRatio));
            }

            // ③ 镜像
            if (d.MirrorDetected)
            {
                if (d.CameraMount == CameraMountKind.EyeInHand)
                {
                    d.Notes.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "ℹ det(A) = {0:F6} < 0 = 镜像变换；EyeInHand（相机随动）下机械 X+ 使静止特征相对视场左移，"
                        + "H 首列系数必然反号 → det 恒负，属<b>固有物理</b>，不阻断发布。质量以 RMS/网格规整为准。",
                        d.DetA));
                }
                else if (d.MirrorIsBlocker)
                {
                    d.HasBlocker = true;
                    d.Notes.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "⛔ det(A) = {0:F6} < 0 = 镜像变换；EyeToHand（固定相机）下镜像不是固有物理，"
                        + "请检查轴方向定义与相机成像是否被翻转。",
                        d.DetA));
                }
                else
                {
                    d.Notes.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "⚠ det(A) = {0:F6} < 0 = 镜像变换；眼型未知，按保守提示处理（未阻断）。",
                        d.DetA));
                }
            }

            // ① 两轴当量（提示性）
            if (d.AxisScaleDeviationPct > 3.0)
            {
                d.Notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "⚠ 两轴像素当量差 {0:F2}%（列范数 {1:F5} vs {2:F5}）。这是提示项、不是门禁 —— "
                    + "★ 判据必须用<b>列向量范数</b>：旧写法拿 |h11| vs |h22| 比对，旋转 ~45° 时两者都趋零，"
                    + "而本工位 151° 图像旋转下它会假报警（真机矩阵对角元差 39.6%，列范数只差 2.31%）。",
                    d.AxisScaleDeviationPct, d.ColumnNormFirst, d.ColumnNormSecond));
            }

            if (d.ShearSuspicious)
            {
                d.Notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "⚠ 剪切偏大（两列夹角 {0:F3}°，偏离正交 {1:F3}°，|cos|={2:F3}）—— 提示项，不阻断。",
                    d.ColumnAngleDeg, Math.Abs(90.0 - d.ColumnAngleDeg), d.ShearRatio));
            }

            if (d.GridIrregular)
            {
                d.Notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "⚠ 网格边长离散大（行 CV {0:F1}% / 列 CV {1:F1}%）→ 个别采样点不可靠。",
                    d.GridRowEdgeCvPct, d.GridColEdgeCvPct));
            }

            if (d.GridAngleSuspicious)
            {
                d.Notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "⚠ 网格行/列夹角 {0:F1}°，偏离 90° 超 {1:F0}° → 轴/相机安装不垂直（提示项）。",
                    d.GridAngleDeg, t.GridAngleWarnDeg));
            }

            if (d.RmsMm > t.RmsWarnMm)
            {
                d.Notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "⚠ RMS 重投影 {0:F4} mm > {1:F2} mm，高精度引导建议压到 0.5 mm 以内。",
                    d.RmsMm, t.RmsWarnMm));
            }

            if (!double.IsNaN(d.LooRmsMm) && d.RmsMm > 1e-6 && d.LooRmsMm > d.RmsMm * 1.5)
            {
                d.Notes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "⚠ LOO 留一残差 {0:F4} mm 明显高于自拟合 {1:F4} mm ⇒ 点数偏少 / 某点影响过大，自拟合 RMS 偏乐观。",
                    d.LooRmsMm, d.RmsMm));
            }

            // 残差是否随半径增大（畸变特征判读）
            double slope;
            double corr;
            if (GeometryUtil.LinearTrend(ToDoubleList(d.PerPointRadiusMm), ToDoubleList(d.PerPointResidualMm), out slope, out corr))
            {
                if (corr > 0.7 && slope > 0.0)
                {
                    d.Notes.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "⚠ 残差随半径显著增大（相关系数 {0:F3}）⇒ 呈径向畸变特征，建议做畸变量化评估。",
                        corr));
                }
            }
        }

        private static List<Vec2> CollectPixels(IList<CalibSample> usable)
        {
            var list = new List<Vec2>(usable.Count);
            for (int i = 0; i < usable.Count; i++)
            {
                list.Add(usable[i].Pixel);
            }

            return list;
        }

        private static List<double> ToDoubleList(double[] arr)
        {
            var list = new List<double>(arr == null ? 0 : arr.Length);
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    list.Add(arr[i]);
                }
            }

            return list;
        }

        private static int CountExcluded(IList<CalibSample> samples)
        {
            int n = 0;
            if (samples == null)
            {
                return 0;
            }

            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] != null && !samples[i].IsUsable)
                {
                    n++;
                }
            }

            return n;
        }
    }
}
