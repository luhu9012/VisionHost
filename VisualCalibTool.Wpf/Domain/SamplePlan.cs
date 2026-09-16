using System;
using System.Collections.Generic;
using System.Globalization;

namespace VisualCalibTool.Domain
{
    /// <summary>计划告警级别。</summary>
    public enum PlanIssueSeverity
    {
        /// <summary>仅提示（不影响执行）。</summary>
        Info = 0,

        /// <summary>有风险但可跑（例如步长偏小、外圈余量薄）。</summary>
        Warn = 1,

        /// <summary>阻断：这个计划<b>不该发车</b>（内圈必被拒 / 点必跑出视野 / 参数非法）。</summary>
        Blocker = 2
    }

    /// <summary>计划告警码（稳定字符串，便于回归断言与日志检索，不用自由文案当键）。</summary>
    public static class PlanIssueCode
    {
        public const string BaseNotSet = "BASE_NOT_SET";
        public const string StepNonPositive = "STEP_NON_POSITIVE";
        public const string StepTooSmall = "STEP_TOO_SMALL";
        public const string StepSpanTooSmall = "STEP_SPAN_TOO_SMALL";
        public const string StepAdvice = "STEP_ADVICE";
        public const string StepTooLargePixelSpan = "STEP_TOO_LARGE_PIXEL_SPAN";
        public const string InnerRadiusNegative = "INNER_RADIUS_NEGATIVE";
        public const string InnerRadiusTight = "INNER_RADIUS_TIGHT";
        public const string OuterLimitExceeded = "OUTER_LIMIT_EXCEEDED";
        public const string OuterRadiusTight = "OUTER_RADIUS_TIGHT";
        public const string SoftLimitExceeded = "SOFT_LIMIT_EXCEEDED";
        public const string ZOutOfRange = "Z_OUT_OF_RANGE";
        public const string ZNotResolved = "Z_NOT_RESOLVED";
        public const string Unreachable = "UNREACHABLE";
        public const string ReachUnknown = "REACH_UNKNOWN";
        public const string FovUnknown = "FOV_UNKNOWN";
        public const string GridDegenerate = "GRID_DEGENERATE";
    }

    /// <summary>一条计划告警。"卡哪说哪"的最小单元 —— 必须能落到<b>具体哪个点</b>。</summary>
    public sealed class PlanIssue
    {
        public PlanIssueSeverity Severity;
        public string Code;

        /// <summary>人类可读原因（允许带毫米级数字）。</summary>
        public string Message;

        /// <summary>关联采样点序号（从 1 开始）；0 = 全局性告警。</summary>
        public int SampleIndex;

        public static PlanIssue Info(string code, string message, int sampleIndex = 0)
        {
            return new PlanIssue { Severity = PlanIssueSeverity.Info, Code = code, Message = message, SampleIndex = sampleIndex };
        }

        public static PlanIssue Warn(string code, string message, int sampleIndex = 0)
        {
            return new PlanIssue { Severity = PlanIssueSeverity.Warn, Code = code, Message = message, SampleIndex = sampleIndex };
        }

        public static PlanIssue Blocker(string code, string message, int sampleIndex = 0)
        {
            return new PlanIssue { Severity = PlanIssueSeverity.Blocker, Code = code, Message = message, SampleIndex = sampleIndex };
        }

        /// <summary>是否已由控制器/规划消解掉（预检通过后把 UNREACHABLE 标为已消解，不重复报警）。</summary>
        public bool Resolved;

        public override string ToString()
        {
            string where = SampleIndex > 0
                ? string.Format(CultureInfo.InvariantCulture, " [#{0}]", SampleIndex)
                : string.Empty;
            return string.Format(CultureInfo.InvariantCulture, "[{0}] {1}{2}: {3}",
                Severity, Code, where, Message);
        }
    }

    /// <summary>计划中的单个采样点。</summary>
    public sealed class PlannedSample
    {
        /// <summary>序号（与 <c>NinePointGrid</c> 编号一致，从 1 开始）。</summary>
        public int Index;

        /// <summary>目标位姿（世界）。</summary>
        public MotionPose Target;

        /// <summary>该点相对基准位的偏移（mm），复盘"这个点在哪"最直观。</summary>
        public Vec2 Offset;

        /// <summary>非空 = 该点<b>不发车</b>，内容即原因（预检判定不可达 / 内圈越界）。</summary>
        public string SkipReason;

        /// <summary>按当前 H 预测的像素位置（可为 NaN = 无 H 可用）。用于"这点会落在视野哪个位置"。</summary>
        public Vec2 PredictedPixel;

        public bool HasPredictedPixel;

        /// <summary>该点距机器人原点的半径（mm），可达性讨论用。</summary>
        public double OriginRadiusMm;

        public bool Skipped
        {
            get { return !string.IsNullOrEmpty(SkipReason); }
        }

        public string Short()
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0} ({1:F2}, {2:F2}, Z{3:F2}, U{4:F2})",
                Index, Target.X, Target.Y, Target.Z, Target.U);
        }
    }

    /// <summary>
    /// 采样计划（纯计算产物，<b>不含任何运动</b>）。
    ///
    /// ★ 为什么把它单独做成一层：设计文档 §6.2 的每一行——步长多大、点会不会跑出视野、
    ///   内圈会不会越界、外圈还有多少余量——都是<b>可以在发车前算出来</b>的。
    ///   把这些算清楚再发车，比撞了再解释便宜得多（零运动校核只能回答"终点合法吗"，
    ///   它答不了"这一步步长会不会让点跑出视野"）。
    ///
    /// ★ 铁律：本类<b>绝不</b>自己做逆解或奇异判断（上位机没有臂长与关节限位）。
    ///   它只做"几何一致性 + 已知约束"的检查，可达性一律留给控制器的 <c>CHECK</c>。
    /// </summary>
    public sealed class SamplePlan
    {
        public CalibChainKind Chain = CalibChainKind.NinePoint;

        public Vec2 BaseXy;
        public double Z;
        public double U;

        public double StepX;
        public double StepY;

        /// <summary>是否翻转 X（镜像工位）。★ 只影响规划偏移，不影响网格位置语义。</summary>
        public bool MirroredX;

        /// <summary>走访顺序（编号序列）。★ 只影响次序，不影响网格位置。</summary>
        public int[] Order = new int[0];

        public readonly List<PlannedSample> Samples = new List<PlannedSample>();
        public readonly List<PlanIssue> Issues = new List<PlanIssue>();

        // ── 几何派生量（全部 mm / px，供界面与日志直接展示）──
        public double CenterRadiusMm;
        public double NearestCornerRadiusMm;

        /// <summary>步长在图像上对应的像素跨度（|偏移| / mmPerPixel）。</summary>
        public double StepSpanPx = double.NaN;

        /// <summary>整张网格在图像上的像素跨度（对角线）。</summary>
        public double GridSpanPx = double.NaN;

        /// <summary>图像短边（px）；未知为 0。</summary>
        public int ImageShortSidePx;

        /// <summary>FOV 反推的推荐步长（mm）——设计文档口径：FOV 的 1/3 ~ 1/2。</summary>
        public double RecommendedStepMinMm = double.NaN;
        public double RecommendedStepMaxMm = double.NaN;

        /// <summary>计划签名：任一影响几何的输入变了就变，用于"要不要重算/重采"的判据。</summary>
        public string Signature = string.Empty;

        public int PlannedCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Samples.Count; i++)
                {
                    if (!Samples[i].Skipped)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        public int SkippedCount
        {
            get { return Samples.Count - PlannedCount; }
        }

        public bool HasBlocker
        {
            get
            {
                for (int i = 0; i < Issues.Count; i++)
                {
                    if (!Issues[i].Resolved && Issues[i].Severity == PlanIssueSeverity.Blocker)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>可发车 = 无阻断 且 可走的点数足够解算（最少 3 点，少于则解不出仿射）。</summary>
        public bool IsViable
        {
            get { return !HasBlocker && PlannedCount >= MinimumSamples; }
        }

        /// <summary>仿射解算的最少点数（6 个未知量 → 理论上 3 点，实战要冗余，这里只作硬下限）。</summary>
        public const int MinimumSamples = 3;

        public void AddIssue(PlanIssue issue)
        {
            if (issue != null)
            {
                Issues.Add(issue);
            }
        }

        public IEnumerable<PlanIssue> Blockers()
        {
            for (int i = 0; i < Issues.Count; i++)
            {
                if (!Issues[i].Resolved && Issues[i].Severity == PlanIssueSeverity.Blocker)
                {
                    yield return Issues[i];
                }
            }
        }

        public IEnumerable<PlanIssue> Warnings()
        {
            for (int i = 0; i < Issues.Count; i++)
            {
                if (!Issues[i].Resolved && Issues[i].Severity == PlanIssueSeverity.Warn)
                {
                    yield return Issues[i];
                }
            }
        }

        /// <summary>把"实际执行序"排出来（按 <see cref="Order"/> 取点，跳过的也保留以便显示"哪点没走"）。</summary>
        public List<PlannedSample> InExecutionOrder()
        {
            var result = new List<PlannedSample>(Samples.Count);
            if (Order != null && Order.Length > 0)
            {
                for (int k = 0; k < Order.Length; k++)
                {
                    PlannedSample s = Find(Order[k]);
                    if (s != null)
                    {
                        result.Add(s);
                    }
                }
            }
            else
            {
                result.AddRange(Samples);
            }

            return result;
        }

        public PlannedSample Find(int index)
        {
            for (int i = 0; i < Samples.Count; i++)
            {
                if (Samples[i].Index == index)
                {
                    return Samples[i];
                }
            }

            return null;
        }

        public string Describe()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} 计划：基准({1:F2},{2:F2}) Z{3:F2} U{4:F2}，步长 {5:F2}×{6:F2} mm，"
                + "可走 {7}/{8} 点，最近角点半径 {9:F2} mm",
                Chain, BaseXy.X, BaseXy.Y, Z, U, StepX, StepY,
                PlannedCount, Samples.Count, NearestCornerRadiusMm);
        }
    }
}
