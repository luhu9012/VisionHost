using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// ★ 吸嘴偏心（e）解算。
    ///
    /// 公式只有一行：<b>e = O − H(p_tip)</b>
    ///   · O = 旋转中心（世界，来自旋转中心链）；
    ///   · p_tip = 工具尖在图像上的像素位置；
    ///   · H(p_tip) = "要让工具尖出现在 p_tip，法兰该停在哪"（H 的命令位域语义）。
    ///   · 差值就是"工具尖相对旋转轴的偏移量"，也就是转头之后会跑掉的那一段。
    ///
    /// ★ 为什么必须<b>减</b>而不是加：H 是像素 → 法兰命令位域的映射，它是"落点式"口径；
    ///   两个落点相减得到的才是空间位移。真吸嘴偏心 e 与中间量 ToolEccW = −m 是两回事，
    ///   混用会把偏心量算成它的负值 —— 生产上表现为"越校正越偏"。
    ///
    /// ★ 方法语义（主项目铁律）：<c>LegacyUnknown</c> 在 EyeToHand（固定相机）下<b>视为过期</b>。
    ///   因为固定相机下"工具尖在哪"没法从旧数据外推 —— 相机不动，工具尖动，
    ///   旧口径量出来的东西与新工况无关。这里如实把它标成过期而不是照单全收。
    /// </summary>
    public static class ToolOffsetSolver
    {
        /// <summary>工具尖像素的估计结果。</summary>
        public sealed class TipEstimate
        {
            public Vec2 Pixel;
            public int SampleCount;

            /// <summary>重复定位离散度（mm 或 px，取决于传入的是哪个域）。大 ⇒ 对针没对好。</summary>
            public double Scatter;

            public double ScatterMax;

            public bool Success;
            public string FailReason;
        }

        /// <summary>
        /// 从多次"工具尖压住特征"的观测里估计工具尖像素。
        /// ★ 多次测量必须报<b>离散度</b>而不只是均值：均值看起来总是很漂亮，
        ///   离散度才是"对针到底稳不稳"的唯一证据。
        /// </summary>
        public static TipEstimate EstimateTipPixel(IList<Vec2> pixels, double maxScatterPx = 3.0)
        {
            var est = new TipEstimate();

            if (pixels == null || pixels.Count == 0)
            {
                est.FailReason = "没有任何工具尖观测。";
                return est;
            }

            var valid = new List<Vec2>(pixels.Count);
            for (int i = 0; i < pixels.Count; i++)
            {
                if (pixels[i].IsFinite)
                {
                    valid.Add(pixels[i]);
                }
            }

            if (valid.Count == 0)
            {
                est.FailReason = "工具尖观测全部非法（NaN / Inf）。";
                return est;
            }

            Vec2 c = GeometryUtil.Centroid(valid);
            var dists = new List<double>(valid.Count);
            for (int i = 0; i < valid.Count; i++)
            {
                dists.Add(valid[i].DistanceTo(c));
            }

            est.Pixel = c;
            est.SampleCount = valid.Count;
            est.Scatter = GeometryUtil.Rms(dists);
            est.ScatterMax = GeometryUtil.Max(dists);
            est.Success = true;

            if (valid.Count >= 2 && est.Scatter > maxScatterPx)
            {
                est.Success = false;
                est.FailReason = string.Format(CultureInfo.InvariantCulture,
                    "工具尖重复定位离散度 {0:F3} px（上限 {1:F3} px）：几次对针落点不一致，"
                    + "先解决对针稳定性再谈偏心。",
                    est.Scatter, maxScatterPx);
            }

            return est;
        }

        /// <summary>
        /// 由 O、工具尖像素、H 解出偏心 e。
        /// </summary>
        public static ToolOffsetResult Solve(HomMat2D h, Vec2 tipPixel, Vec2 rotCenterWorld,
            ToolOffsetMethod method, CameraMountKind mount, int tipSampleCount = 0,
            double tipScatterMm = 0.0)
        {
            var result = new ToolOffsetResult
            {
                TipPixel = tipPixel,
                TipWorld = h.Transform(tipPixel),
                RotCenterWorld = rotCenterWorld,
                Method = method,
                TipSampleCount = tipSampleCount,
                TipScatterMm = tipScatterMm
            };

            if (!h.IsFinite)
            {
                result.Success = false;
                result.Error = CalibError.Create(CalibFailureKind.SolveFailed,
                    "H 不是有限矩阵，无法把工具尖像素换算到世界坐标。");
                return result;
            }

            if (!tipPixel.IsFinite)
            {
                result.Success = false;
                result.Error = CalibError.Create(CalibFailureKind.SolveFailed,
                    "工具尖像素非法 —— 没有工具尖位置就没有偏心量。");
                return result;
            }

            if (!rotCenterWorld.IsFinite)
            {
                result.Success = false;
                result.Error = CalibError.Create(CalibFailureKind.SolveFailed,
                    "旋转中心 O 非法 —— 偏心 e = O − H(p_tip) 依赖它，请先完成旋转中心标定。");
                return result;
            }

            // ★ 方法过期判据（主项目铁律）：固定相机 + LegacyUnknown ⇒ 过期
            if (mount == CameraMountKind.EyeToHand && method == ToolOffsetMethod.LegacyUnknown)
            {
                result.Success = false;
                result.Error = CalibError.Create(CalibFailureKind.QualityGate,
                    "固定相机（EyeToHand）下，偏心方法为 LegacyUnknown ⇒ 按过期处理。"
                    + "固定相机里相机不动、工具尖动，旧口径量出的偏心与新工况无关，必须用 EyeToHandImage 重标。");
                return result;
            }

            result.Ecc = rotCenterWorld - result.TipWorld;
            result.EccMagnitudeMm = result.Ecc.Length;
            result.EccDirectionDeg = Math.Atan2(result.Ecc.Y, result.Ecc.X) * 180.0 / Math.PI;
            result.Success = true;
            return result;
        }

        /// <summary>
        /// 反算：给定偏心 e 与旋转中心 O，工具尖应该出现在哪个像素（"设完参数看一眼对不对"）。
        /// </summary>
        public static Vec2 TipPixelFromEcc(HomMat2D h, Vec2 ecc, Vec2 rotCenterWorld)
        {
            Vec2 tipWorld = rotCenterWorld - ecc;
            return h.Invert().Transform(tipWorld);
        }

        /// <summary>
        /// ★ 由"同一 Mark 在不同 U 下的观测"求偏心方向的<b>一致性</b>：返回最大方向偏差（度）。
        ///
        /// ⚠ "要不要按角度归一"取决于<b>像面是否随法兰滚转</b>（<paramref name="rotateToU0"/>）：
        ///   · true（固定相机 / 非随动安装）：<c>O − H(u_k)</c> 带着 R(ΔU)，
        ///     不归一就直接比方向，会把"转了 30°"读成"偏心方向差了 30°"；
        ///   · false（眼在手，相机装在法兰上）：画面与相机一起滚，"尖压住特征"是刚性状态，
        ///     <c>O − H(u_k)</c> <b>本来就是基准角口径</b>，再转一次等于双重旋转，
        ///     会凭空造出一个方向偏差告警（真机上表现为"完美对针被判成对针不稳"）。
        /// </summary>
        public static double EccDirectionSpreadDeg(HomMat2D h, IList<CalibObservation> observations,
            IList<double> absoluteU, Vec2 rotCenterWorld, double u0Deg, bool rotateToU0)
        {
            if (observations == null || observations.Count < 2 || absoluteU == null)
            {
                return double.NaN;
            }

            int n = Math.Min(observations.Count, absoluteU.Count);
            var angles = new List<double>(n);
            double sumSin = 0.0;
            double sumCos = 0.0;
            int used = 0;

            for (int i = 0; i < n; i++)
            {
                CalibObservation o = observations[i];
                if (o == null || !o.HasPixel || !o.Pixel.IsFinite)
                {
                    continue;
                }

                Vec2 raw = rotCenterWorld - h.Transform(o.Pixel);
                Vec2 e = rotateToU0 ? raw.Rotate(u0Deg - absoluteU[i]) : raw;
                if (!e.IsFinite || e.Length < 1e-9)
                {
                    continue;
                }

                double ang = Math.Atan2(e.Y, e.X);
                angles.Add(ang);
                sumSin += Math.Sin(ang);
                sumCos += Math.Cos(ang);
                used++;
            }

            if (used < 2)
            {
                return double.NaN;
            }

            double mean = Math.Atan2(sumSin / used, sumCos / used);
            double maxDev = 0.0;
            for (int i = 0; i < angles.Count; i++)
            {
                double dev = Math.Abs(RotationCircleSolver.NormalizeDeg((angles[i] - mean) * 180.0 / Math.PI));
                if (dev > maxDev)
                {
                    maxDev = dev;
                }
            }

            return maxDev;
        }

        /// <summary>
        /// ★ 把各角度的偏心观测<b>归一到基准角 U0</b> 再取代表值。
        ///
        /// e 的定义是"<b>基准角下</b>吸嘴尖相对回转中心的偏移"，所以只要观测带着角度旋转就必须转回。
        /// 但"带着角度旋转"是有前提的 —— <b>像面不随法兰滚转</b>（<paramref name="rotateToU0"/>）：
        ///   · 固定相机：O − H(p_k) 含 R(ΔU) ⇒ 必须转回 U0（不转就会把 e 抹掉）；
        ///   · 眼在手（相机装在法兰上）：画面跟着法兰滚 ⇒ "尖压住特征"是刚性状态，
        ///     O − H(p_k) 已经是基准角口径 ⇒ <b>再转就是双重旋转</b>。
        ///
        /// 双重旋转的代价是可解析的：报告值 = (1/n)Σ R(−Δu_k)·e。
        /// 对称角（如 −45/0/+45）下它恰好等于 e —— 也就是说这个坑<b>会被对称角完全掩盖</b>，
        /// 只有非对称角才会暴露（表现为 |e| 缩水 + 伪造的方向偏差）。
        /// 所以这里把前提做成显式参数，而不是让它藏在一个"总是要转"的硬编码里。
        ///
        /// 做法：逐点（按需）转回 U0，取均值，再反算出一个"等价于 U0 下的 p_tip"，
        /// 让 <see cref="Solve"/> 仍然是唯一的公式出口（不在这里重抄一遍 e = O − H(p_tip)）。
        /// </summary>
        public static bool SolveNormalized(HomMat2D h, IList<Vec2> tipPixels, IList<double> absoluteU,
            Vec2 rotCenterWorld, double u0Deg, bool rotateToU0, out Vec2 equivalentTipPixel,
            out Vec2 meanEcc, out double scatterMm)
        {
            equivalentTipPixel = new Vec2(double.NaN, double.NaN);
            meanEcc = Vec2.Zero;
            scatterMm = double.NaN;

            if (tipPixels == null || absoluteU == null)
            {
                return false;
            }

            int n = Math.Min(tipPixels.Count, absoluteU.Count);
            var eccs = new List<Vec2>(n);
            for (int i = 0; i < n; i++)
            {
                if (!tipPixels[i].IsFinite)
                {
                    continue;
                }

                Vec2 raw = rotCenterWorld - h.Transform(tipPixels[i]);
                Vec2 e = rotateToU0 ? raw.Rotate(u0Deg - absoluteU[i]) : raw;
                if (e.IsFinite)
                {
                    eccs.Add(e);
                }
            }

            if (eccs.Count == 0)
            {
                return false;
            }

            Vec2 mean = GeometryUtil.Centroid(eccs);
            var dists = new List<double>(eccs.Count);
            for (int i = 0; i < eccs.Count; i++)
            {
                dists.Add(eccs[i].DistanceTo(mean));
            }

            meanEcc = mean;
            scatterMm = GeometryUtil.Rms(dists);

            try
            {
                Vec2 tipWorld = rotCenterWorld - mean;
                equivalentTipPixel = h.Invert().Transform(tipWorld);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            return equivalentTipPixel.IsFinite;
        }
    }
}
