using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>一个点的反投影明细（AR 叠加与残差矢量图的原子条目）。</summary>
    public sealed class ReprojectionPoint
    {
        public int Index;

        /// <summary>观测像素（X = col，Y = row）。</summary>
        public Vec2 Pixel;

        /// <summary>观测世界位（控制器反馈位）。</summary>
        public Vec2 World;

        /// <summary>把观测世界位经 H⁻¹ 投回像素 —— AR 叠加就画它。</summary>
        public Vec2 PredictedPixel;

        /// <summary>把观测像素经 H 正算到世界 —— 落点残差的载体。</summary>
        public Vec2 PredictedWorld;

        /// <summary>像素域残差（观测 − 预测），及其模长。这是"看见才算通过"里的那个"看见"。</summary>
        public Vec2 PixelResidual;
        public double PixelResidualPx;

        /// <summary>世界域残差（观测 − 预测）向量，及其模长（mm）。</summary>
        public Vec2 WorldResidual;
        public double WorldResidualMm;

        /// <summary>该点距机器人原点的半径（mm）。</summary>
        public double OriginRadiusMm;

        /// <summary>
        /// 该点是否属于内圈（九个点里离原点最近的四个角点之一）。
        /// <b>null = 不知道</b>（调用方没给内圈半径）。
        ///
        /// ★★ 为什么必须是可空而<b>不是</b> `bool`（2026-09-14）：修前它在
        ///   <paramref name="innerLimitMm"/> 未知时会把<b>每一个点</b>都标成 `true` ——
        ///   因为"内圈半径"退化成 `double.MaxValue`，于是 `半径 &lt;= MaxValue` 恒成立。
        ///   生产路径（`NinePointRunner`）**恰恰不传**那个限值（传 `double.NaN`），
        ///   所以现场每一个 `ReprojectionPoint` 都带着一个**错的 `true`**。
        ///   当时没出事只是因为**全仓没有人读这个字段**（只写不读）——
        ///   等哪天有人拿它做"内圈点单独看"的图，就会得到一张全是内圈点的图，且**毫无征兆**。
        ///   ⇒ 取 `bool?`：`null`（不知道）与 `false`（已知不在内圈）是两件事，
        ///     和本项目"未测到的量一律 NaN、绝不留 0 冒充真值"是同一条规矩。
        /// </summary>
        public bool? IsInnerCorner;
    }

    /// <summary>
    /// ★ AR 反投影与残差分析。
    ///
    /// 设计文档把这项判据的优先级放在<b>任何 RMS 数字之上</b>，理由是它对的地方很朴素：
    ///   · 重投回的点与图上实际特征重合 → 标定准；
    ///   · 系统性往一个方向偏 → 有平移/尺度误差；
    ///   · 越往外偏得越多 → 尺度或畸变问题（<see cref="RadialTrendSlopeMmPerMm"/> 抓它）；
    ///   · 内外圈表现不同 → 旋转中心或标定 Z 高度问题。
    ///
    /// 所以本类不满足于给一个 RMS，它把上面四条各自变成一个可判读的量。
    /// </summary>
    public static class ReprojectionAnalyzer
    {
        /// <summary>
        /// 把世界位投回像素（H⁻¹）。矩阵不可逆时抛 <see cref="InvalidOperationException"/>。
        /// ★ 本仓暂无调用者（对方通常直接 `<c>h.Invert().Transform(w)</c>` 或走 <see cref="Analyze"/> 的
        ///   一次性求逆）；保留是因为它是**对外便利入口**（AR 叠加的宿主侧会用到），
        ///   与上一条"字段从不被填"不同 —— 这不是死路径，是**库的公开面**。
        /// </summary>
        public static Vec2 PixelFromWorld(HomMat2D h, Vec2 world)
        {
            return h.Invert().Transform(world);
        }

        /// <summary>把像素正算到世界（H）。★ 同 <see cref="PixelFromWorld"/>：对外便利入口。</summary>
        public static Vec2 WorldFromPixel(HomMat2D h, Vec2 pixel)
        {
            return h.Transform(pixel);
        }

        /// <summary>
        /// 分析一组观测。只吃 <see cref="CalibObservation.IsUsable"/> 的点。
        /// </summary>
        /// <param name="innerLimitMm">内圈半径（mm）；NaN = 未知，不判内圈余量。</param>
        /// <param name="outerLimitMm">外圈半径（mm）；NaN = 未知。</param>
        public static ReprojectionReport Analyze(HomMat2D h, IList<CalibObservation> observations,
            double innerLimitMm = double.NaN, double outerLimitMm = double.NaN)
        {
            var report = new ReprojectionReport();

            if (!h.IsFinite)
            {
                report.Error = "H 不是有限矩阵，无法做反投影分析。";
                return report;
            }

            bool invertible = h.IsInvertible;
            if (!invertible)
            {
                report.Error = "H 不可逆（det≈0），无法做 AR 反投影（像素域残差仍可算，但精度已不可信）。";
            }

            var usable = new List<CalibObservation>();
            if (observations != null)
            {
                for (int i = 0; i < observations.Count; i++)
                {
                    if (observations[i] != null && observations[i].IsUsable)
                    {
                        usable.Add(observations[i]);
                    }
                }
            }

            if (usable.Count == 0)
            {
                report.Error = "没有可用观测点，无法做反投影分析。";
                return report;
            }

            double innerRadius = double.MaxValue;
            if (!double.IsNaN(innerLimitMm))
            {
                innerRadius = innerLimitMm;
            }

            var pixelResiduals = new List<double>();
            var worldResiduals = new List<double>();
            var radii = new List<double>();

            double sx = 0.0;
            double sy = 0.0;

            // 逆矩阵只求一次（每个点都 Invert 一遍是白烧 CPU，且放大浮点噪声）
            HomMat2D hInv = default(HomMat2D);
            if (invertible)
            {
                hInv = h.Invert();
            }

            for (int i = 0; i < usable.Count; i++)
            {
                CalibObservation obs = usable[i];

                var p = new ReprojectionPoint
                {
                    Index = obs.Index,
                    Pixel = obs.Pixel,
                    World = obs.World,
                    PredictedWorld = h.Transform(obs.Pixel),
                    OriginRadiusMm = obs.World.Length
                };

                if (invertible)
                {
                    p.PredictedPixel = hInv.Transform(obs.World);
                }
                else
                {
                    p.PredictedPixel = new Vec2(double.NaN, double.NaN);
                }

                p.PixelResidual = new Vec2(obs.Pixel.X - p.PredictedPixel.X, obs.Pixel.Y - p.PredictedPixel.Y);
                p.PixelResidualPx = p.PixelResidual.IsFinite ? p.PixelResidual.Length : double.NaN;

                p.WorldResidual = obs.World - p.PredictedWorld;
                p.WorldResidualMm = p.WorldResidual.Length;

                // ★ 只在"确实知道内圈半径"时才判内圈；不知道就留 null。
                //   修前这里是 `p.OriginRadiusMm <= innerRadius`，而 innerRadius 在未知时
                //   是 double.MaxValue ⇒ 每个点都被标成"内圈"。详见 IsInnerCorner 的说明。
                p.IsInnerCorner = double.IsNaN(innerLimitMm)
                    ? (bool?)null
                    : (bool?)(p.OriginRadiusMm <= innerRadius);

                report.Points.Add(p);

                if (p.PixelResidual.IsFinite)
                {
                    pixelResiduals.Add(p.PixelResidualPx);
                    sx += p.PixelResidual.X;
                    sy += p.PixelResidual.Y;
                }

                worldResiduals.Add(p.WorldResidualMm);
                radii.Add(p.OriginRadiusMm);
            }

            report.RmsPixelResidualPx = GeometryUtil.Rms(pixelResiduals);
            report.MaxPixelResidualPx = GeometryUtil.Max(pixelResiduals);
            report.RmsWorldResidualMm = GeometryUtil.Rms(worldResiduals);
            report.MaxWorldResidualMm = GeometryUtil.Max(worldResiduals);

            if (pixelResiduals.Count > 0)
            {
                report.MeanBiasPx = new Vec2(sx / pixelResiduals.Count, sy / pixelResiduals.Count);
                report.MeanBiasPxNorm = report.MeanBiasPx.Length;
                report.MeanBiasDeg = Math.Atan2(report.MeanBiasPx.Y, report.MeanBiasPx.X) * 180.0 / Math.PI;
            }

            // 残差随半径的线性趋势："越往外偏得越多"的量化形式（畸变/尺度问题的指纹）
            double slope;
            double r;
            if (GeometryUtil.LinearTrend(radii, worldResiduals, out slope, out r))
            {
                report.RadialTrendSlopeMmPerMm = slope;
                report.RadialTrendR = r;
            }

            // 内/外圈余量
            if (!double.IsNaN(innerLimitMm))
            {
                double nearest = double.MaxValue;
                for (int i = 0; i < radii.Count; i++)
                {
                    if (radii[i] < nearest)
                    {
                        nearest = radii[i];
                    }
                }

                report.InnerMarginMm = nearest - innerLimitMm;
            }

            if (!double.IsNaN(outerLimitMm))
            {
                double farthest = 0.0;
                for (int i = 0; i < radii.Count; i++)
                {
                    if (radii[i] > farthest)
                    {
                        farthest = radii[i];
                    }
                }

                report.OuterMarginMm = outerLimitMm - farthest;
            }

            return report;
        }

        /// <summary>
        /// 把一个计划的九点目标也一并投回像素（"指哪打哪"的预演）。
        ///
        /// ★ 本仓暂无调用者：它对应的是"**跑之前**先看一遍打算走的那九个点会落在图的哪儿"，
        ///   也就是设计文档 §10.3 的预演口径。真正的接线要等向导第 3 步的实时预览（P1），
        ///   所以这里先按"对外便利入口"保留，不当作死路径删。
        ///
        /// ★★ 顺手修掉两处（2026-09-14）：
        ///   ① 不可逆判据以前是**自己抄的一遍** `Math.Abs(h.Det) > 1e-15` ——
        ///      与 `HomMat2D.Invert()` 的守卫是同一个数的两份拷贝，靠巧合一致
        ///      ⇒ 改成 <see cref="HomMat2D.IsInvertible"/>（同源）；
        ///   ② 逆矩阵以前写在**循环里**（每个点 `h.Invert()` 一次），而同一个文件的
        ///      <see cref="Analyze"/> 明确注释过「逆矩阵只求一次（每个点都 Invert 一遍是白烧 CPU）」——
        ///      同一个文件里两套写法，是"注释说的和代码做的相反"的典型 ⇒ 提到循环外。
        ///
        /// ★ 注意本方法**不填残差**（`PredictedWorld = 计划目标` 是同一件事，
        ///   所以世界残差恒 0、像素残差恒 NaN）。它是"预演"，不是"校验结果"——
        ///   别把它的输出喂给 <see cref="ReprojectionReport.AllWithin"/>（那会全判不合格）。
        /// </summary>
        public static List<ReprojectionPoint> ProjectPlan(HomMat2D h, SamplePlan plan)
        {
            var list = new List<ReprojectionPoint>();
            if (plan == null || !h.IsFinite)
            {
                return list;
            }

            bool invertible = h.IsInvertible;

            // ★ 逆矩阵只求一次（与 Analyze 同一口径）
            HomMat2D hInv = default(HomMat2D);
            if (invertible)
            {
                hInv = h.Invert();
            }

            for (int i = 0; i < plan.Samples.Count; i++)
            {
                PlannedSample s = plan.Samples[i];
                var p = new ReprojectionPoint
                {
                    Index = s.Index,
                    World = s.Target.Xy,
                    PredictedWorld = s.Target.Xy,
                    OriginRadiusMm = s.Target.Xy.Length
                };

                p.PredictedPixel = invertible
                    ? hInv.Transform(s.Target.Xy)
                    : new Vec2(double.NaN, double.NaN);

                list.Add(p);
            }

            return list;
        }
    }

    /// <summary>反投影分析结果。</summary>
    public sealed class ReprojectionReport
    {
        public readonly List<ReprojectionPoint> Points = new List<ReprojectionPoint>();

        public double RmsPixelResidualPx;
        public double MaxPixelResidualPx;

        public double RmsWorldResidualMm;
        public double MaxWorldResidualMm;

        /// <summary>平均偏置（像素）—— 非零即"系统性往一个方向偏"。</summary>
        public Vec2 MeanBiasPx;

        public double MeanBiasPxNorm;
        public double MeanBiasDeg = double.NaN;

        /// <summary>残差对半径的线性斜率（mm/mm）。正值即"越往外偏得越多"。</summary>
        public double RadialTrendSlopeMmPerMm;
        public double RadialTrendR;

        /// <summary>内圈余量（mm）：最近观测半径 − 内圈半径。&lt;0 ⇒ 该点必被拒。</summary>
        public double InnerMarginMm = double.NaN;

        /// <summary>外圈余量（mm）：外圈半径 − 最远观测半径。</summary>
        public double OuterMarginMm = double.NaN;

        public string Error;

        public int Count
        {
            get { return Points.Count; }
        }

        /// <summary>全部点是否都落在像素容差内（界面上"看见重合"的数字形式）。</summary>
        public bool AllWithin(double tolerancePx)
        {
            if (Points.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < Points.Count; i++)
            {
                double v = Points[i].PixelResidualPx;
                if (double.IsNaN(v) || v > tolerancePx)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>找出残差最大的点（"标出贡献最大的点，建议剔除并重解"）。</summary>
        public ReprojectionPoint WorstPixel()
        {
            ReprojectionPoint worst = null;
            double best = -1.0;
            for (int i = 0; i < Points.Count; i++)
            {
                double v = Points[i].PixelResidualPx;
                if (!double.IsNaN(v) && v > best)
                {
                    best = v;
                    worst = Points[i];
                }
            }

            return worst;
        }

        /// <summary>
        /// 人话判读。★ 刻意不给"通过/不通过"的单一结论 ——
        /// 上面四条现象各自指向不同的根因，把它们压成一个布尔值等于把诊断信息丢掉。
        /// </summary>
        public string Verdict(double pixelTolerance = 1.0)
        {
            if (!string.IsNullOrEmpty(Error))
            {
                return Error;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "像素域残差 RMS {0:F3} px，最大 {1:F3} px（{2} 点）",
                RmsPixelResidualPx, MaxPixelResidualPx, Points.Count);
            sb.AppendLine();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "世界域残差 RMS {0:F4} mm，最大 {1:F4} mm",
                RmsWorldResidualMm, MaxWorldResidualMm);
            sb.AppendLine();

            if (MeanBiasPxNorm > 0.15 * Math.Max(RmsPixelResidualPx, 1e-9) && MeanBiasPxNorm > 0.05)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "⚠ 存在系统性偏置 {0:F3} px（方向 {1:F1}°）—— 指向平移或尺度误差，而不是随机噪声。",
                    MeanBiasPxNorm, MeanBiasDeg);
                sb.AppendLine();
            }

            if (Math.Abs(RadialTrendR) > 0.5)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "⚠ 残差随半径变化（斜率 {0:F5} mm/mm，相关系数 {1:F2}）—— 越往外偏得越多，"
                    + "典型根因是尺度误差或镜头畸变。",
                    RadialTrendSlopeMmPerMm, RadialTrendR);
                sb.AppendLine();
            }

            if (!double.IsNaN(InnerMarginMm))
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "内圈余量 {0:F2} mm{1}",
                    InnerMarginMm, InnerMarginMm < 0.0 ? "  ← 已越界，该点必被拒" : string.Empty);
                sb.AppendLine();
            }

            if (!double.IsNaN(OuterMarginMm))
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "外圈余量 {0:F2} mm{1}",
                    OuterMarginMm, OuterMarginMm < 50.0 ? "  ← 偏薄，真机上容易擦边" : string.Empty);
                sb.AppendLine();
            }

            if (AllWithin(pixelTolerance))
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "→ 全部点在 {0:F1} px 内 → 反投影目视重合。", pixelTolerance);
                return sb.ToString();
            }

            sb.AppendFormat(CultureInfo.InvariantCulture,
                "→ 有超过 {0:F1} px 的点：先看残差矢量图往哪偏，再决定剔点还是重采。", pixelTolerance);

            // ★★ 点名「最差点」—— 这是本类最该说而没说的一句话（2026-09-14 补）。
            //
            //   为什么值得单独立一段：`Verdict()` 是**用户唯一看到的那句诊断**
            //   （`NinePointRunner` 取它填 `Diagnostics.ReprojectionVerdict`，
            //     `CalibWizardViewModel` 又把它拼进验收文案）。而它以前只说
            //   "先看图再决定剔点还是重采"，**从不说是哪个点** —— 用户得自己拿眼睛在图里找最偏的。
            //
            //   更别扭的是：本类**早就把它算好了**（<see cref="WorstPixel"/>），
            //   该方法的注释甚至写着「标出贡献最大的点，建议剔除并重解」——
            //   算得出、说得出，就是**没接到输出**（与"字段从不被填"同族的第四种：
            //   数据算对了但没接到输出）。现在把它接上。
            //
            //   ★ 只在"确实有点超容差"时点名：全部合格还说"建议剔除"就是过度拒绝
            //     （乱报会让人不再看告警）—— 这条有反面对照断言钉着。
            //   ★ 别忘工具侧是现成的：`WizardRunOptions.ExcludeIndices` → `ApplyExclusions`
            //     就是"精确剔掉一个点"，所以这句话是**可执行的**，不是泛泛建议。
            ReprojectionPoint worst = WorstPixel();
            if (worst != null)
            {
                sb.AppendLine();
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "  最差点是 #{0}：残差 {1:F3} px（世界 {2:F4} mm，半径 {3:F2} mm{4}）；"
                    + "观测像素 ({5:F1}, {6:F1}) ↔ 世界 ({7:F3}, {8:F3})。"
                    + "剔掉这一个再重解，通常比整体重采便宜。",
                    worst.Index, worst.PixelResidualPx, worst.WorldResidualMm, worst.OriginRadiusMm,
                    worst.IsInnerCorner == true ? "，内圈" : string.Empty,
                    worst.Pixel.X, worst.Pixel.Y, worst.World.X, worst.World.Y);
            }

            return sb.ToString();
        }
    }
}
