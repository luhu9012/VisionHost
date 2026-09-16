using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>规划请求（把"当前知道的一切"喂进来，换出一份可发车的计划）。</summary>
    public sealed class PlanBuildRequest
    {
        public CalibChainKind Chain = CalibChainKind.NinePoint;

        public CalibTopology Topology;

        /// <summary>走访顺序。null = 用该链的默认顺序。</summary>
        public int[] Order;

        /// <summary>图像短边（px）。&lt;=0 = 未知 → 跳过 FOV 相关判据并告警。</summary>
        public int ImageShortSidePx;

        public int ImageWidthPx;
        public int ImageHeightPx;

        /// <summary>世界 mm / 像素（X 轴）。&lt;=0 = 未知。</summary>
        public double MmPerPixel;

        /// <summary>已知的 H（可选）。给了就能预测每个点在图像上的落点。</summary>
        public HomMat2D? KnownH;

        // ── 阈值（给默认值，现场需要可调）──

        /// <summary>步长在图像上至少要有多少像素跨度（太小 → 矩阵病态）。</summary>
        public double MinStepSpanPx = 8.0;

        /// <summary>整张网格允许占图像短边的最大比例（超过 ⇒ 角点可能跑出视野）。</summary>
        public double MaxGridSpanRatio = 0.85;

        /// <summary>内/外圈余量告警线（mm）：低于它就提示"擦着边过"。</summary>
        public double MarginWarnMm = 50.0;

        /// <summary>最小允许步长（mm）：低于它按"步长太小"告警。</summary>
            public double MinStepMm = 0.5;
    }

    /// <summary>
    /// ★ 采样计划生成器（纯计算，<b>不发车</b>）。
    ///
    /// 它负责回答设计文档 §6.2 里那些"能在发车前算清"的问题：
    ///   · 步长多大合适（经验值 = FOV 的 1/3 ~ 1/2）→ 给出推荐区间并对照当前值；
    ///   · 点会不会跑出视野 → 用像素尺度把网格跨度换成像素，和图像短边比；
    ///   · 内圈会不会越界 → 最近角点半径 = |基准| − 步长×√2，小于 0 就是必被拒；
    ///   · 外圈还有多少余量 → 最远角点半径与行程上限的差。
    ///
    /// ★ 显式<b>不</b>做的事：不做逆解、不做奇异判断。上位机没有臂长与关节限位，
    ///   可达性一律交给控制器的 <c>CHECK</c>（零运动校核）。这里只保证"几何上自洽 + 不违反已知约束"。
    /// </summary>
    public static class SamplePlanBuilder
    {
        /// <summary>
        /// ★ 旋转采样计划：XY 固定在基准位，U 逐点变化。
        ///
        /// 与九点计划的唯一区别是"顺序里变的是 U 而不是 XY"，其余检查（基准位已知、Z 已解析、
        /// 内/外圈余量、软限位）全部复用 —— 因为那些风险与"走位方式"无关，
        /// 而是与"目标位在哪儿"有关：转 U 时法兰 XY 停在基准位上，所以离原点的距离就是基准位半径。
        ///
        /// 额外的检查是<b>角度跨度</b>：跨度太小的话，定圆圆心的标准差会被放大几十倍
        /// （这是个纯几何事实，与数据质量无关），所以必须在发车前就拦住。
        /// </summary>
        public static SamplePlan BuildRotation(PlanBuildRequest req, IList<double> absoluteAngles,
            double minimumSpanDeg = 30.0)
        {
            if (req == null)
            {
                throw new ArgumentNullException("req");
            }

            if (req.Topology == null)
            {
                throw new ArgumentException("PlanBuildRequest.Topology 不能为空", "req");
            }

            CalibTopology topo = req.Topology;
            var poses = new List<MotionPose>();
            var angles = new List<double>();
            if (absoluteAngles != null)
            {
                for (int i = 0; i < absoluteAngles.Count; i++)
                {
                    poses.Add(new MotionPose(topo.BasePosXY.X, topo.BasePosXY.Y, topo.WorkZ, absoluteAngles[i]));
                    angles.Add(absoluteAngles[i]);
                }
            }

            return BuildRotationAt(req, poses, angles, CalibChainKind.RotationCenter, minimumSpanDeg);
        }

        /// <summary>
        /// ★ 更一般的旋转采样计划：每个角度的目标位由调用方给出。
        ///
        /// 偏心链要它：转 U 时法兰 XY 不能一直停在基准位 —— 每个角度都得先把工具尖对到
        /// 基准特征上（对针），于是每个角度的 XY 都不同，"绕着基准位转"这句话在那里不成立。
        /// 但角度跨度、Z 已解析这些检查与"走位方式"无关，所以全部保留。
        /// </summary>
        public static SamplePlan BuildRotationAt(PlanBuildRequest req, IList<MotionPose> poses,
            IList<double> absoluteAngles, CalibChainKind chain, double minimumSpanDeg = 30.0)
        {
            if (req == null)
            {
                throw new ArgumentNullException("req");
            }

            if (req.Topology == null)
            {
                throw new ArgumentException("PlanBuildRequest.Topology 不能为空", "req");
            }

            CalibTopology topo = req.Topology;
            var plan = new SamplePlan
            {
                Chain = chain,
                BaseXy = topo.BasePosXY,
                Z = topo.WorkZ,
                U = topo.CalibU0,
                StepX = 0.0,
                StepY = 0.0,
                Order = null,
                ImageShortSidePx = req.ImageShortSidePx
            };

            if (!topo.BasePosKnown)
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.BaseNotSet,
                    "标定基准位尚未确定 —— 旋转采样要绕着它转，没有它就没有圆心可言。"));
            }

            if (double.IsNaN(plan.Z) || double.IsInfinity(plan.Z))
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.ZNotResolved,
                    "工作 Z 未解析（NaN）。转 U 时 Z 必须锁定，否则 Mark 会跟着上下跑。"));
            }

            if (poses == null || poses.Count == 0)
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.StepNonPositive,
                    "没有给出旋转采样点。旋转中心至少需要 3 个非同角度的采样点才能定圆。"));
                plan.Signature = "rotation|empty";
                return plan;
            }

            var order = new int[poses.Count];
            for (int i = 0; i < poses.Count; i++)
            {
                var s = new PlannedSample
                {
                    Index = i + 1,
                    Offset = poses[i].Xy - plan.BaseXy,
                    Target = poses[i],
                    OriginRadiusMm = poses[i].Xy.Length
                };

                // ★ 顺序按给定序列（要扫描的次序是调用方的语义，不能替他重排）
                plan.Samples.Add(s);
                order[i] = s.Index;

                if (!poses[i].IsFinite)
                {
                    plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.ZNotResolved,
                        "第 " + (i + 1) + " 个旋转目标位含 NaN/Inf。", s.Index));
                }
            }

            plan.Order = order;

            // 角度跨度检查（用 360 − 最大空隙，不能用 max−min）
            double span = RotationCircleSolver.SpanDeg(ToArray(absoluteAngles));
            if (span < minimumSpanDeg)
            {
                plan.AddIssue(PlanIssue.Blocker("ROTATION_SPAN_TOO_SMALL",
                    string.Format(CultureInfo.InvariantCulture,
                        "旋转跨度只有 {0:F1}°（下限 {1:F1}°）：这么窄的弧段上定圆，圆心标准差会被放大几十倍，"
                        + "解出来的旋转中心不可用。请把旋转范围加大（有条件就扫满一圈）。",
                        span, minimumSpanDeg)));
            }
            else
            {
                plan.AddIssue(PlanIssue.Info("ROTATION_SPAN",
                    string.Format(CultureInfo.InvariantCulture, "旋转跨度 {0:F1}°，{1} 个采样点。",
                        span, poses.Count)));
            }

            plan.CenterRadiusMm = plan.BaseXy.Length;
            plan.NearestCornerRadiusMm = plan.CenterRadiusMm;

            double maxRadius = 0.0;
            int maxIndex = 0;
            for (int i = 0; i < plan.Samples.Count; i++)
            {
                double r = plan.Samples[i].Target.Xy.Length;
                if (r > maxRadius)
                {
                    maxRadius = r;
                    maxIndex = plan.Samples[i].Index;
                }
            }

            if (topo.MaxRadiusXy > 0.0 && maxRadius > topo.MaxRadiusXy)
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.OuterLimitExceeded,
                    string.Format(CultureInfo.InvariantCulture,
                        "最远采样点 #{0} 距原点 {1:F3} mm，超过 |XY| 上限 {2:F3} mm。",
                        maxIndex, maxRadius, topo.MaxRadiusXy), maxIndex));
            }

            if (topo.ZMin.HasValue && topo.ZMax.HasValue
                && (plan.Z < topo.ZMin.Value || plan.Z > topo.ZMax.Value))
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.ZOutOfRange,
                    string.Format(CultureInfo.InvariantCulture,
                        "工作 Z={0:F3} 超出允许范围 [{1:F3}, {2:F3}]。",
                        plan.Z, topo.ZMin.Value, topo.ZMax.Value)));
            }

            plan.Signature = string.Format(CultureInfo.InvariantCulture, "rotation|{0}|{1}",
                poses.Count, span.ToString("F3", CultureInfo.InvariantCulture));
            return plan;
        }

        private static double[] ToArray(IList<double> v)
        {
            if (v == null)
            {
                return new double[0];
            }

            var a = new double[v.Count];
            for (int i = 0; i < v.Count; i++)
            {
                a[i] = v[i];
            }

            return a;
        }

        /// <summary>默认走访顺序：九点用螺旋（#5 中心先走，便于先确认中心与视野）。</summary>
        public static int[] DefaultOrder(CalibChainKind chain)
        {
            switch (chain)
            {
                case CalibChainKind.NinePoint:
                case CalibChainKind.RotationCenter:
                case CalibChainKind.ToolOffset:
                    return (int[])NinePointGrid.SpiralOrder.Clone();
                default:
                    return (int[])NinePointGrid.RowMajorOrder.Clone();
            }
        }

        public static SamplePlan Build(PlanBuildRequest req)
        {
            if (req == null)
            {
                throw new ArgumentNullException("req");
            }

            if (req.Topology == null)
            {
                throw new ArgumentException("PlanBuildRequest.Topology 不能为空", "req");
            }

            CalibTopology topo = req.Topology;
            var plan = new SamplePlan
            {
                Chain = req.Chain,
                BaseXy = topo.BasePosXY,
                Z = topo.WorkZ,
                U = topo.CalibU0,
                StepX = topo.StepX,
                StepY = topo.StepY,
                Order = req.Order == null ? DefaultOrder(req.Chain) : (int[])req.Order.Clone(),
                ImageShortSidePx = req.ImageShortSidePx
            };

            // ── ① 基准位必须已知 ──
            if (!topo.BasePosKnown)
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.BaseNotSet,
                    "标定基准位尚未确定。基准位是九个点的中心，没有它就没有网格可言 —— "
                    + "请先移动到目标位置并「设为基准位」。"));
            }

            // ── ② 步长合法性 ──
            if (plan.StepX <= 0.0 || plan.StepY <= 0.0)
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.StepNonPositive,
                    string.Format(CultureInfo.InvariantCulture,
                        "步长必须为正：当前 {0:F3} × {1:F3} mm。步长为 0 会让九个点重叠，矩阵必然奇异。",
                        plan.StepX, plan.StepY)));
            }
            else if (plan.StepX < req.MinStepMm || plan.StepY < req.MinStepMm)
            {
                plan.AddIssue(PlanIssue.Warn(PlanIssueCode.StepTooSmall,
                    string.Format(CultureInfo.InvariantCulture,
                        "步长偏小（{0:F3} × {1:F3} mm，建议 ≥ {2:F2} mm）：点太密会让矩阵形状退化（σ1/σ2 变差），"
                        + "随机噪声被放大到解里。",
                        plan.StepX, plan.StepY, req.MinStepMm)));
            }

            // ── ③ Z 必须已解析 ──
            if (double.IsNaN(plan.Z) || double.IsInfinity(plan.Z))
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.ZNotResolved,
                    "工作 Z 未解析（NaN）。H 是 2D 单应，但像素尺度取决于拍摄高度 —— "
                    + "Z 错了整张 H 都错。请确认 NozzleAlignZ / BasePosZ / CalibZ 三级回退能取到值。"));
            }
            else if (topo.ZMin.HasValue && topo.ZMax.HasValue
                     && (plan.Z < topo.ZMin.Value || plan.Z > topo.ZMax.Value))
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.ZOutOfRange,
                    string.Format(CultureInfo.InvariantCulture,
                        "工作 Z={0:F3} 超出允许范围 [{1:F3}, {2:F3}]。",
                        plan.Z, topo.ZMin.Value, topo.ZMax.Value)));
            }

            // ── ④ 造点（即便上面有阻断也照样造出来：界面要能把"错在哪"画给用户看）──
            BuildSamples(plan, req);

            // ── ⑤ 半径 / 余量几何 ──
            AnalyzeRadii(plan, req);

            // ── ⑥ 像素尺度判据（点会不会跑出视野 / 步长够不够）──
            AnalyzePixelScale(plan, req);

            plan.Signature = BuildSignature(plan, req);
            return plan;
        }

        private static void BuildSamples(SamplePlan plan, PlanBuildRequest req)
        {
            Vec2[] offsets = NinePointGrid.BuildOffsets(plan.StepX, plan.StepY, plan.MirroredX);

            for (int i = 1; i <= NinePointGrid.PointCount; i++)
            {
                Vec2 off = offsets[i - 1];
                Vec2 xy = plan.BaseXy + off;
                var sample = new PlannedSample
                {
                    Index = i,
                    Offset = off,
                    Target = new MotionPose(xy.X, xy.Y, plan.Z, plan.U),
                    OriginRadiusMm = xy.Length
                };

                if (req.KnownH.HasValue && req.KnownH.Value.IsFinite)
                {
                    // H 语义：像素 → 世界（法兰命令位域）。反投影用逆矩阵。
                    try
                    {
                        Vec2 px = req.KnownH.Value.Invert().Transform(xy);
                        sample.PredictedPixel = px;
                        sample.HasPredictedPixel = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // det≈0：矩阵不可逆，预测就免了（后续质量门禁会单独拦）
                        sample.HasPredictedPixel = false;
                    }
                }

                plan.Samples.Add(sample);
            }
        }

        private static void AnalyzeRadii(SamplePlan plan, PlanBuildRequest req)
        {
            CalibTopology topo = req.Topology;

            plan.CenterRadiusMm = plan.BaseXy.Length;
            plan.NearestCornerRadiusMm = NinePointGrid.NearestCornerRadius(plan.BaseXy, plan.StepX, plan.StepY);

            // 内圈：最近角点半径 < 0 ⇒ 那个角点已经越过机器人原点，必被拒
            if (plan.NearestCornerRadiusMm < 0.0)
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.InnerRadiusNegative,
                    string.Format(CultureInfo.InvariantCulture,
                        "最近角点半径 {0:F2} mm < 0：基准位离原点只有 {1:F2} mm，而步长 {2:F2}×{3:F2} mm 已经把它推过原点了。"
                        + "内圈第一个杀手就是它 —— 缩小步长或把基准位往外挪。",
                        plan.NearestCornerRadiusMm, plan.CenterRadiusMm, plan.StepX, plan.StepY)));
            }
            else if (plan.NearestCornerRadiusMm < req.MarginWarnMm)
            {
                plan.AddIssue(PlanIssue.Warn(PlanIssueCode.InnerRadiusTight,
                    string.Format(CultureInfo.InvariantCulture,
                        "内圈余量偏薄：最近角点半径 {0:F2} mm，比告警线 {1:F2} mm 还小。"
                        + "真机上这个点很容易被控制器判不可达，建议缩小步长。",
                        plan.NearestCornerRadiusMm, req.MarginWarnMm)));
            }

            // 外圈：最远角点半径 vs 行程上限
            double maxRadius = 0.0;
            int maxIndex = 0;
            for (int i = 0; i < plan.Samples.Count; i++)
            {
                double r = plan.Samples[i].Target.Xy.Length;
                if (r > maxRadius)
                {
                    maxRadius = r;
                    maxIndex = plan.Samples[i].Index;
                }
            }

            if (topo.MaxRadiusXy > 0.0 && maxRadius > topo.MaxRadiusXy)
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.OuterLimitExceeded,
                    string.Format(CultureInfo.InvariantCulture,
                        "最远角点 #{0} 距原点 {1:F3} mm，超过 |XY| 上限 {2:F3} mm。",
                        maxIndex, maxRadius, topo.MaxRadiusXy), maxIndex));
            }
            else if (topo.MaxRadiusXy > 0.0 && topo.MaxRadiusXy - maxRadius < req.MarginWarnMm)
            {
                plan.AddIssue(PlanIssue.Warn(PlanIssueCode.OuterRadiusTight,
                    string.Format(CultureInfo.InvariantCulture,
                        "外圈余量偏薄：最远角点 #{0} 距原点 {1:F3} mm，距上限 {2:F3} mm 只剩 {3:F3} mm。",
                        maxIndex, maxRadius, topo.MaxRadiusXy, topo.MaxRadiusXy - maxRadius), maxIndex));
            }

            // 软限位矩形（只读校验；权威在控制器侧）
            if (topo.XMin.HasValue && topo.XMax.HasValue && topo.YMin.HasValue && topo.YMax.HasValue)
            {
                for (int i = 0; i < plan.Samples.Count; i++)
                {
                    PlannedSample s = plan.Samples[i];
                    Vec2 p = s.Target.Xy;
                    if (p.X < topo.XMin.Value || p.X > topo.XMax.Value
                        || p.Y < topo.YMin.Value || p.Y > topo.YMax.Value)
                    {
                        plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.SoftLimitExceeded,
                            string.Format(CultureInfo.InvariantCulture,
                                "目标 ({0:F3}, {1:F3}) 超出软限位矩形 X[{2:F3},{3:F3}] Y[{4:F3},{5:F3}]。"
                                + "★ 注意：软限位的权威在控制器侧，上位机改不了它 —— 请去 RC+ 的[设置]-[机器人]-[范围]确认。",
                                p.X, p.Y, topo.XMin.Value, topo.XMax.Value, topo.YMin.Value, topo.YMax.Value),
                            s.Index));
                    }
                }
            }

            // 网格退化：步长太小或基准位恰好落在原点附近使网格几乎共点
            double span = Math.Sqrt(plan.StepX * plan.StepX + plan.StepY * plan.StepY);
            if (span > 0.0 && plan.CenterRadiusMm > 0.0 && span / plan.CenterRadiusMm < 1e-4)
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.GridDegenerate,
                    string.Format(CultureInfo.InvariantCulture,
                        "网格退化：步长对角线 {0:F4} mm 相对中心半径 {1:F1} mm 小到 1e-4 量级以下，"
                        + "九个点在数值上几乎重合。", span, plan.CenterRadiusMm)));
            }
        }

        private static void AnalyzePixelScale(SamplePlan plan, PlanBuildRequest req)
        {
            if (req.ImageShortSidePx <= 0 || req.MmPerPixel <= 0.0)
            {
                plan.AddIssue(PlanIssue.Info(PlanIssueCode.FovUnknown,
                    "图像尺寸或像素尺度未知 → 跳过「点会不会跑出视野」的判据。"
                    + "这条判据是：步长换算成像素后，整张网格的跨度必须明显小于图像短边。"));
                return;
            }

            plan.StepSpanPx = Math.Sqrt(plan.StepX * plan.StepX + plan.StepY * plan.StepY) / req.MmPerPixel;
            if (plan.StepSpanPx < req.MinStepSpanPx)
            {
                plan.AddIssue(PlanIssue.Warn(PlanIssueCode.StepSpanTooSmall,
                    string.Format(CultureInfo.InvariantCulture,
                        "步长换算到图像只有 {0:F1} px（建议 ≥ {1:F1} px）：点太密，"
                        + "亚像素级提取误差会被放大成明显的矩阵形状失真。",
                        plan.StepSpanPx, req.MinStepSpanPx)));
            }

            // 整张网格在图像上的跨度（对角线）：这是"角点会不会跑出视野"的判据
            double spanXmm = 2.0 * Math.Abs(plan.StepX);
            double spanYmm = 2.0 * Math.Abs(plan.StepY);
            plan.GridSpanPx = Math.Sqrt(spanXmm * spanXmm + spanYmm * spanYmm) / req.MmPerPixel;

            double limitPx = req.ImageShortSidePx * req.MaxGridSpanRatio;
            if (plan.GridSpanPx > limitPx)
            {
                plan.AddIssue(PlanIssue.Blocker(PlanIssueCode.StepTooLargePixelSpan,
                    string.Format(CultureInfo.InvariantCulture,
                        "网格在图像上的跨度 {0:F0} px 超过图像短边 {1} px 的 {2:P0}（上限 {3:F0} px）："
                        + "最外圈的点会把 Mark 推出视野，那几个点必然提不到特征。请缩小步长。",
                        plan.GridSpanPx, req.ImageShortSidePx, req.MaxGridSpanRatio, limitPx)));
            }

            // 推荐步长：FOV 的 1/3 ~ 1/2（换算成 mm）
            double fovShortMm = req.ImageShortSidePx * req.MmPerPixel;
            plan.RecommendedStepMinMm = fovShortMm / 3.0;
            plan.RecommendedStepMaxMm = fovShortMm / 2.0;

            double stepDiag = Math.Sqrt(plan.StepX * plan.StepX + plan.StepY * plan.StepY);
            if (stepDiag < plan.RecommendedStepMinMm * 0.5 || stepDiag > plan.RecommendedStepMaxMm * 2.0)
            {
                plan.AddIssue(PlanIssue.Info(PlanIssueCode.StepAdvice,
                    string.Format(CultureInfo.InvariantCulture,
                        "经验参考：当前步长对角线 {0:F2} mm，而视野反推的推荐区间是 {1:F2} ~ {2:F2} mm"
                        + "（经验值 = FOV 的 1/3 ~ 1/2）。偏差大不一定是错，但值得看一眼。",
                        stepDiag, plan.RecommendedStepMinMm, plan.RecommendedStepMaxMm)));
            }
        }

        /// <summary>
        /// 计划签名：任一影响几何的输入变了就变。
        /// 用途：拖动基准位/改步长时判断"要不要重算绿区"，避免无谓重算。
        /// </summary>
        public static string BuildSignature(SamplePlan plan, PlanBuildRequest req)
        {
            var sb = new System.Text.StringBuilder(96);
            sb.Append(plan.Chain).Append('|');
            sb.Append(plan.BaseXy.X.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(plan.BaseXy.Y.ToString("F6", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(plan.Z.ToString("F6", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(plan.U.ToString("F6", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(plan.StepX.ToString("F6", CultureInfo.InvariantCulture)).Append('x');
            sb.Append(plan.StepY.ToString("F6", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(plan.MirroredX ? 'M' : 'N').Append('|');
            if (plan.Order != null)
            {
                for (int i = 0; i < plan.Order.Length; i++)
                {
                    sb.Append(plan.Order[i]);
                }
            }

            if (req != null)
            {
                sb.Append('|').Append(req.ImageShortSidePx).Append('|');
                sb.Append(req.MmPerPixel.ToString("F8", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 判断是否需要重算计划（签名变了才重算）。★ 这是"拖动时不重算"的性能判据。
        /// </summary>
        public static bool NeedsRebuild(SamplePlan existing, PlanBuildRequest req)
        {
            if (existing == null)
            {
                return true;
            }

            string fresh = BuildSignature(existing, req);
            return !string.Equals(existing.Signature, fresh, StringComparison.Ordinal);
        }
    }
}
