using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>圆 Mark 评分输入（把主项目那一大段 ComposeCircleReport 需要的东西抽成纯数据）。</summary>
    public sealed class CircleScoreInput
    {
        public bool Success;
        public int CandidateCount;

        /// <summary>最终选中的像素位置（列 = X，行 = Y）。</summary>
        public double PixelX;
        public double PixelY;

        /// <summary>选中候选的实际圆度（0~1）。</summary>
        public double Circularity = 0.85;

        /// <summary>选中候选的等效半径（px）；&lt;=0 表示未知。</summary>
        public double RadiusPx;

        /// <summary>参考半径（px）；&lt;=1 表示尚无参考 → 半径项给中性分。</summary>
        public double ReferenceRadiusPx;

        /// <summary>与最近其它候选的距离（px）；无其它候选 = double.MaxValue。</summary>
        public double NearestOtherDistancePx = double.MaxValue;

        /// <summary>期望像素位置；&lt;=0 表示无期望（全图搜索）→ 可预测性项给中性分。</summary>
        public double ExpectedPx;
        public double ExpectedPy;

        public double SearchRadiusPx = 150.0;

        /// <summary>是否走过降级路径（动态阈值兜底 / 区域中心兜底）→ 总分 ×0.85。</summary>
        public bool UsedFallback;

        public string FailDetail;
    }

    /// <summary>
    /// ★ 特征匹配质量评分。逐条对等主项目口径，<b>但不碰 HALCON</b> ——
    /// 因此这些权重和公式可以被离线单测钉住，而不是"只有跑真机才知道对不对"。
    ///
    /// 圆 Mark：`100 × (0.35×圆度 + 0.25×半径一致性 + 0.20×唯一性 + 0.20×可预测性)`，降级 ×0.85，clamp [1,99]
    /// 十字    ：`100 × (0.7×(0.75 + 0.25×夹角分) + 0.30×唯一性)`，夹角容忍 30°
    ///
    /// ★ 一个刻意的设计：<b>半径一致性在"还没有参考半径"时给 0.8 而不是 0</b>。
    ///   第一个点必然没有参考半径 —— 若给 0，首点分数会被系统性压低，
    ///   操作员会误以为"识别质量差"，于是去乱调参数。中性分才是诚实的表达。
    /// </summary>
    public static class FeatureQualityScorer
    {
        public const double CircleWeightCircularity = 0.35;
        public const double CircleWeightRadius = 0.25;
        public const double CircleWeightUniqueness = 0.20;
        public const double CircleWeightPredictability = 0.20;

        /// <summary>降级路径命中时的总分折扣（主项目口径）。</summary>
        public const double FallbackDiscount = 0.85;

        /// <summary>半径一致性的线性容忍比例（= 参考半径容差 0.4）。</summary>
        public const double RadiusToleranceRatio = 0.4;

        /// <summary>十字两臂夹角误差容忍（度）。</summary>
        public const double CrossAngleToleranceDeg = 30.0;

        public static double Clamp01(double v)
        {
            if (v < 0.0)
            {
                return 0.0;
            }

            return v > 1.0 ? 1.0 : v;
        }

        /// <summary>分数等级文字（≥85 极佳 / ≥70 良好 / ≥55 可用 / 其余 偏弱）。</summary>
        public static string Verdict(double score)
        {
            if (score >= 85.0)
            {
                return "极佳";
            }

            if (score >= 70.0)
            {
                return "良好";
            }

            if (score >= 55.0)
            {
                return "可用";
            }

            return "偏弱";
        }

        /// <summary>圆度 → 分（0.4 映射到 0，1.0 映射到 1）。</summary>
        public static double CircularityScore(double circularity)
        {
            return Clamp01((circularity - 0.4) / 0.6);
        }

        /// <summary>半径一致性 → 分（偏差 ≤40% 内线性，超过 0）。</summary>
        public static double RadiusScore(double radiusPx, double referenceRadiusPx)
        {
            if (referenceRadiusPx <= 1.0 || radiusPx <= 0.001)
            {
                return 0.8; // 尚无参考（首点/预览）→ 中性分，不给 0
            }

            double devRatio = Math.Abs(radiusPx - referenceRadiusPx) / referenceRadiusPx;
            return Clamp01(1.0 - devRatio / RadiusToleranceRatio);
        }

        /// <summary>候选唯一性 → 分（最近干扰候选超过 2.5×半径即视为无歧义）。</summary>
        public static double UniquenessScore(int candidateCount, double nearestOtherDistancePx, double radiusPx)
        {
            if (candidateCount <= 1 || nearestOtherDistancePx >= double.MaxValue)
            {
                return 1.0;
            }

            double refLen = 2.5 * Math.Max(radiusPx, 10.0);
            if (nearestOtherDistancePx > refLen)
            {
                return 1.0;
            }

            return 0.35 + 0.65 * Clamp01(nearestOtherDistancePx / refLen);
        }

        /// <summary>位置可预测性 → 分（偏差 ≤3×搜索半径内线性）。</summary>
        public static double PredictabilityScore(double pixelX, double pixelY,
            double expectedPx, double expectedPy, double searchRadiusPx)
        {
            if (expectedPx <= 0.0 || expectedPy <= 0.0)
            {
                return 0.85; // 无期望位置引导（全图搜索）→ 中性分
            }

            double dx = pixelX - expectedPx;
            double dy = pixelY - expectedPy;
            double dev = Math.Sqrt(dx * dx + dy * dy);
            return Clamp01(1.0 - dev / (3.0 * Math.Max(searchRadiusPx, 10.0)));
        }

        /// <summary>组装圆 Mark 质量报告。</summary>
        public static FeatureMatchReport ScoreCircle(CircleScoreInput input)
        {
            if (input == null)
            {
                throw new ArgumentNullException("input");
            }

            var report = new FeatureMatchReport
            {
                Success = input.Success,
                CandidateCount = input.CandidateCount,
                UsedFallback = input.UsedFallback,
                RadiusPx = input.RadiusPx,
                PixelX = input.PixelX,
                PixelY = input.PixelY
            };

            var sb = new System.Text.StringBuilder();

            if (!input.Success)
            {
                report.Score = 0.0;
                report.Verdict = "失败";
                sb.AppendLine(string.IsNullOrEmpty(input.FailDetail) ? "识别失败" : input.FailDetail);
                report.Detail = sb.ToString();
                return report;
            }

            double circScore = CircularityScore(input.Circularity);
            double radiusScore = RadiusScore(input.RadiusPx, input.ReferenceRadiusPx);
            double uniqueScore = UniquenessScore(input.CandidateCount, input.NearestOtherDistancePx, input.RadiusPx);
            double devScore = PredictabilityScore(input.PixelX, input.PixelY,
                input.ExpectedPx, input.ExpectedPy, input.SearchRadiusPx);

            double score = 100.0 * (CircleWeightCircularity * circScore
                                    + CircleWeightRadius * radiusScore
                                    + CircleWeightUniqueness * uniqueScore
                                    + CircleWeightPredictability * devScore);
            if (input.UsedFallback)
            {
                score *= FallbackDiscount;
            }

            score = Math.Max(1.0, Math.Min(99.0, score));
            report.Score = score;
            report.Verdict = Verdict(score);

            sb.AppendFormat(CultureInfo.InvariantCulture,
                "候选 {0} 个，综合匹配分 {1:F0}/100 · {2}", input.CandidateCount, score, report.Verdict);
            sb.AppendLine();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "圆度 {0:F2}（权重 {1:F2}）→ 分 {2:F0}{3}",
                input.Circularity, CircleWeightCircularity, circScore * 100.0,
                input.UsedFallback ? "  [降级路径命中，总分×" + FallbackDiscount.ToString("F2", CultureInfo.InvariantCulture) + "]" : string.Empty);
            sb.AppendLine();

            if (input.ReferenceRadiusPx > 1.0 && input.RadiusPx > 0.001)
            {
                double devRatio = Math.Abs(input.RadiusPx - input.ReferenceRadiusPx) / input.ReferenceRadiusPx;
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "半径一致性 {0:F1}px vs 参考 {1:F1}px（偏差 {2:F0}%）→ 分 {3:F0}",
                    input.RadiusPx, input.ReferenceRadiusPx, devRatio * 100.0, radiusScore * 100.0);
            }
            else
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "半径 {0}（暂无参考，中性分 {1:F0}）",
                    input.RadiusPx > 0.001 ? input.RadiusPx.ToString("F1", CultureInfo.InvariantCulture) + "px" : "未知",
                    radiusScore * 100.0);
            }

            sb.AppendLine();
            sb.AppendFormat(CultureInfo.InvariantCulture, "唯一性 分 {0:F0}", uniqueScore * 100.0);
            sb.AppendLine();
            sb.AppendFormat(CultureInfo.InvariantCulture, "可预测性 分 {0:F0}", devScore * 100.0);

            report.Detail = sb.ToString();
            return report;
        }

        /// <summary>组装十字 Mark 质量报告。</summary>
        public static FeatureMatchReport ScoreCross(int candidateCount, bool found, double angleDevDeg,
            bool usedFallback)
        {
            var report = new FeatureMatchReport
            {
                Success = true,
                CandidateCount = candidateCount,
                UsedFallback = usedFallback,
                CrossAngleDevDeg = angleDevDeg
            };

            var sb = new System.Text.StringBuilder();

            if (found)
            {
                double angleScore = Clamp01(1.0 - Math.Abs(angleDevDeg) / CrossAngleToleranceDeg);
                double uniqueScore = candidateCount <= 1 ? 1.0 : 0.85;
                double score = 100.0 * (0.7 * (0.75 + 0.25 * angleScore) + 0.3 * uniqueScore);
                score = Math.Max(1.0, Math.Min(99.0, score));

                report.Score = score;
                report.Verdict = Verdict(score);

                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "十字直线对命中：候选 {0} 个，综合匹配分 {1:F0}/100 · {2}", candidateCount, score, report.Verdict);
                sb.AppendLine();
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "两臂夹角误差 {0:F1}°（容忍 {1:F0}°）→ 角度分 {2:F0}",
                    Math.Abs(angleDevDeg), CrossAngleToleranceDeg, angleScore * 100.0);
            }
            else
            {
                // 兜底路径（最大区域中心）：中低分，且如实标注"这不是精确十字中心"
                double score = candidateCount > 0 ? 45.0 : 1.0;
                report.Score = score;
                report.Verdict = candidateCount > 0 ? "偏弱" : "失败";
                report.UsedFallback = true;
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "未找到近似垂直的直线对 → 退回最大候选区域中心（分 {0:F0}）。该结果不是精确十字中心，建议调参或改用圆 Mark。",
                    score);
            }

            report.Detail = sb.ToString();
            return report;
        }

        /// <summary>
        /// 把 0~100 匹配分折算为 0~1 质量分（观测模型用 0~1，报告用 0~100 —— 两个口径都要留）。
        /// </summary>
        public static double ToUnitScore(double matchScore0To100)
        {
            return Clamp01(matchScore0To100 / 100.0);
        }

        /// <summary>给定候选列表，挑出"选中点"的圆度与最近干扰距离（供评分输入）。</summary>
        public static void DeriveCircleContext(IList<MatchCandidateInfo> candidates, double pixelX, double pixelY,
            out int count, out double circularity, out double nearestOtherDistancePx)
        {
            count = candidates == null ? 0 : candidates.Count;
            circularity = 0.85;
            nearestOtherDistancePx = double.MaxValue;

            if (candidates == null || candidates.Count == 0)
            {
                return;
            }

            int chosen = -1;
            double best = double.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                double dx = candidates[i].PixelX - pixelX;
                double dy = candidates[i].PixelY - pixelY;
                double d = dx * dx + dy * dy;
                if (d < best)
                {
                    best = d;
                    chosen = i;
                }
            }

            if (chosen < 0)
            {
                return;
            }

            circularity = candidates[chosen].Circularity;

            // 用"候选之间的几何距离"而不是 DistanceToExpected —— 后者是各候选到期望点的距离，
            // 拿它当"最近干扰距离"会把语义搞错（两个都很靠近期望的候选会被误判成很远）。
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i == chosen)
                {
                    continue;
                }

                double dx = candidates[i].PixelX - candidates[chosen].PixelX;
                double dy = candidates[i].PixelY - candidates[chosen].PixelY;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < nearestOtherDistancePx)
                {
                    nearestOtherDistancePx = d;
                }
            }
        }
    }
}
