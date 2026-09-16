using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>旋转中心解算请求。</summary>
    public sealed class RotationSolveRequest
    {
        /// <summary>已标定的 H（像素 → 世界）。没有 H 就只能在像素域定圆 —— 那是错的（见类注释）。</summary>
        public HomMat2D H;

        /// <summary>是否已有 H。</summary>
        public bool HasH;

        public readonly List<CalibObservation> Observations = new List<CalibObservation>();

        /// <summary>与观测一一对应的绝对角度 U（度）。</summary>
        public readonly List<double> AbsoluteU = new List<double>();

        /// <summary>最少点数（3 点定圆是下限，实战建议 ≥ 6）。</summary>
        public int MinimumPoints = 3;

        /// <summary>角度跨度下限（度）。太小则圆心标准差爆炸。</summary>
        public double MinimumAngularSpanDeg = 30.0;

        /// <summary>残差上限（mm）：超过即判失败（避免把一个"用散点拟合出来的圆心"当成结果）。</summary>
        public double MaxResidualMm = 0.5;

        /// <summary>拟合圆的半径下限（mm）：接近 0 说明相机几乎在旋转轴上，那是另一种工况，需要确认。</summary>
        public double MinRadiusMm = 0.05;
    }

    /// <summary>
    /// ★ 旋转中心（O）解算。
    ///
    /// **为什么必须在"映射域"里定圆，而不是在像素域：**
    ///   旋转时相机绕轴公转，同一个固定 Mark 在<b>法兰/世界域</b>里的轨迹是一个真正的圆
    ///   （半径 = 相机光轴到旋转轴的横向距离）。而像素坐标是世界坐标的一次<b>仿射</b>变换，
    ///   <b>仿射把圆变成椭圆</b>。所以在像素域直接 Kåsa 定圆，圆心必然偏 ——
    ///   这个偏移量随图像旋转角变大而变大，正是"转一个角度看起来对、转一圈就不对"的根因。
    ///
    /// **为什么半径必须丢弃：**
    ///   拟合半径 = 相机光轴到旋转轴的横向距离，它会被延伸杆长度、吸嘴偏心、
    ///   甚至 Mark 到镜头的距离污染。它是一个<b>可读出来的诊断量</b>，不是可写进产物的几何量。
    ///   把半径写进产物，等于把一个"看起来像个几何参数的中间量"伪装成真值 —— 主项目已明令禁止。
    ///
    /// 本类同时算出"像素域定圆"的圆心，并把两者之差报出来：
    ///   这个差值不是一个装饰性数字，它是<b>证明"不能在像素域定圆"的现场证据</b>。
    /// </summary>
    public static class RotationCircleSolver
    {
        public static RotationCenterResult Solve(RotationSolveRequest req)
        {
            if (req == null)
            {
                throw new ArgumentNullException("req");
            }

            var result = new RotationCenterResult();
            var points = new List<RotationSamplePoint>();

            // ── 前置：必须有 H ──
            // ★ 为什么宁可拒绝也不"先按像素域凑一个"：
            //   唯一真源 CalibrationGeometry 里记着这条路走不通的实测代价 —— 误差 8.46 mm。
            //   世界域的点是正圆，经 H⁻¹（各向异性 + 旋转）拉回像素域后变成<b>椭圆</b>，
            //   对椭圆（尤其弧段）做 Kåsa 定圆，圆心必偏。给它一个"看着像能用"的圆心，
            //   比直接报错危险得多 —— 后者至少不会被人拿去上线。
            if (!req.HasH || !req.H.IsFinite)
            {
                result.Success = false;
                result.Error = CalibError.Create(CalibFailureKind.SolveFailed,
                    "旋转中心必须在映射域定圆（先逐点经 H 映射到世界，再在世界里定圆），但当前没有可用的 H。"
                    + "像素域定圆→圆心再映射在真机上实测偏 8.46 mm，所以这里直接拒绝。请先完成九点标定。");
                return result;
            }

            // ── 收集可用点 ──
            int n = Math.Min(req.Observations.Count, req.AbsoluteU.Count);
            for (int i = 0; i < n; i++)
            {
                CalibObservation obs = req.Observations[i];
                if (obs == null || !obs.HasPixel || !obs.Pixel.IsFinite)
                {
                    continue;
                }

                var p = new RotationSamplePoint
                {
                    Index = obs.Index,
                    Pixel = obs.Pixel,
                    AbsoluteU = req.AbsoluteU[i],
                    Mapped = req.HasH ? req.H.Transform(obs.Pixel) : obs.Pixel,
                    OriginRadiusMm = obs.World.IsFinite ? obs.World.Length : double.NaN
                };
                points.Add(p);
            }

            result.SamplePoints.AddRange(points);
            result.MappedPointCount = points.Count;

            if (points.Count < Math.Max(3, req.MinimumPoints))
            {
                result.Success = false;
                result.Error = CalibError.Create(CalibFailureKind.SolveFailed,
                    string.Format(CultureInfo.InvariantCulture,
                        "可用旋转采样点只有 {0} 个，少于下限 {1} 个。旋转中心至少需要 3 个非同角度的点才能定圆。",
                        points.Count, Math.Max(3, req.MinimumPoints)));
                return result;
            }

            // ── 基准角：只锁一次，取首个采样点的绝对 U ──
            double refU0 = points[0].AbsoluteU;
            result.RefU0Deg = refU0;

            var angles = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                points[i].RelativeU = NormalizeDeg(points[i].AbsoluteU - refU0);
                angles[i] = points[i].RelativeU;
            }

            result.AnglesDeg = angles;

            // ── 角度跨度检查：跨度太小 ⇒ 圆心标准差爆炸 ──
            double span = SpanDeg(angles);
            result.AngularSpanDeg = span;
            if (span < req.MinimumAngularSpanDeg)
            {
                result.Success = false;
                result.Error = CalibError.Create(CalibFailureKind.SolveFailed,
                    string.Format(CultureInfo.InvariantCulture,
                        "旋转角跨度只有 {0:F1}°（下限 {1:F1}°）：这么小的跨度上，定圆圆心的标准差会被放大几十倍，"
                        + "解出来的 O 不能用。请把旋转范围加大（有条件就转满一圈）。",
                        span, req.MinimumAngularSpanDeg));
                return result;
            }

            // ── 在映射域定圆（正确做法）──
            var mapped = new List<Vec2>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                mapped.Add(points[i].Mapped);
            }

            Vec2 center;
            double radius;
            bool ok = GeometryUtil.FitCircleKasa(mapped, out center, out radius);
            result.FittedAtMapped = true;

            if (!ok)
            {
                result.Success = false;
                result.Error = CalibError.Create(CalibFailureKind.SolveFailed,
                    "映射域圆拟合失败（点近似共线或分布过窄）。");
                return result;
            }

            result.Center = center;
            result.FittedRadiusMm = radius;

            // ── 残差 ──
            var residuals = new List<double>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                double d = points[i].Mapped.DistanceTo(center);
                points[i].DistanceToCenterMm = d;
                points[i].ResidualMm = d - radius;
                residuals.Add(points[i].ResidualMm);
            }

            result.ResidualsMm = residuals.ToArray();
            result.ResidualMaxMm = GeometryUtil.Max(AbsAll(residuals));
            result.RmsMm = GeometryUtil.Rms(residuals);

            // ── 对照：像素域定圆（错误做法）——差值就是"为什么不能在像素域定圆"的证据 ──
            double pixelVsMappedMm = double.NaN;
            if (req.HasH)
            {
                Vec2 pixelCenter;
                double pixelRadius;
                var pixels = new List<Vec2>(points.Count);
                for (int i = 0; i < points.Count; i++)
                {
                    pixels.Add(points[i].Pixel);
                }

                if (GeometryUtil.FitCircleKasa(pixels, out pixelCenter, out pixelRadius))
                {
                    result.PixelDomainCenter = pixelCenter;
                    Vec2 mappedOfPixelCenter = req.H.Transform(pixelCenter);
                    result.PixelDomainCenterInWorld = mappedOfPixelCenter;
                    pixelVsMappedMm = mappedOfPixelCenter.DistanceTo(center);
                    result.PixelVsMappedCenterMm = pixelVsMappedMm;
                }
            }

            // ── 半径合理性 ──
            if (radius < req.MinRadiusMm)
            {
                result.Success = false;
                result.Error = CalibError.Create(CalibFailureKind.SolveFailed,
                    string.Format(CultureInfo.InvariantCulture,
                        "拟合半径只有 {0:F4} mm（下限 {1:F4} mm）：相机几乎就在旋转轴上。"
                        + "这种工况下圆心对噪声极度敏感，请确认相机安装与标定域是否正常。",
                        radius, req.MinRadiusMm));
                return result;
            }

            // ── 残差门禁 ──
            if (result.ResidualMaxMm > req.MaxResidualMm)
            {
                result.Success = false;
                result.Error = CalibError.Create(CalibFailureKind.QualityGate,
                    string.Format(CultureInfo.InvariantCulture,
                        "旋转采样点最大残差 {0:F4} mm 超过上限 {1:F4} mm：这些点不落在同一个圆上。"
                        + "典型原因：① 采点期间 Mark 被误识别（候选跳变）；② 角度实际没有变化；"
                        + "③ 相机随 Z 动但 Z 没锁住。请先看逐点残差，把跳变点剔掉再解。",
                        result.ResidualMaxMm, req.MaxResidualMm));
                return result;
            }

            result.Success = true;
            return result;
        }

        /// <summary>把角度归一化到 (−180, 180]。</summary>
        public static double NormalizeDeg(double deg)
        {
            double d = deg % 360.0;
            if (d > 180.0)
            {
                d -= 360.0;
            }
            else if (d <= -180.0)
            {
                d += 360.0;
            }

            return d;
        }

        /// <summary>
        /// 角度跨度。★ 不能直接用 max−min：转了 350° 时 max−min 可能是 20°，
        /// 会被误判成"跨度太小"。这里取 360 − 最大空隙，才是真实的覆盖跨度。
        /// </summary>
        public static double SpanDeg(double[] angles)
        {
            if (angles == null || angles.Length < 2)
            {
                return 0.0;
            }

            var sorted = new double[angles.Length];
            Array.Copy(angles, sorted, angles.Length);
            Array.Sort(sorted);

            double maxGap = 0.0;
            for (int i = 1; i < sorted.Length; i++)
            {
                double gap = sorted[i] - sorted[i - 1];
                if (gap > maxGap)
                {
                    maxGap = gap;
                }
            }

            // 首尾相接的"绕回空隙"
            double wrapGap = (sorted[0] + 360.0) - sorted[sorted.Length - 1];
            if (wrapGap > maxGap)
            {
                maxGap = wrapGap;
            }

            double span = 360.0 - maxGap;
            return span < 0.0 ? 0.0 : (span > 360.0 ? 360.0 : span);
        }

        private static List<double> AbsAll(List<double> v)
        {
            var r = new List<double>(v.Count);
            for (int i = 0; i < v.Count; i++)
            {
                r.Add(Math.Abs(v[i]));
            }

            return r;
        }
    }
}
