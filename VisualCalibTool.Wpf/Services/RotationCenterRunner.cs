using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>
    /// ★ O 链（旋转中心）：转 U、拍同一个固定基准特征、把观测逐点映射到世界后<b>在映射域定圆</b>。
    ///
    /// 三个必须守住的点（任何一条破了，O 都会"看着能用其实偏几毫米"）：
    ///   ① <b>必须先有 H</b>。世界域的点是正圆，经 H⁻¹（各向异性 + 旋转）拉回像素域变成椭圆，
    ///      对椭圆（尤其弧段）套圆，圆心必偏 —— 真机实测这条路偏 <b>8.46 mm</b>。
    ///      所以没有 H 就直接拒绝，不"先凑一个出来"。
    ///   ② <b>半径必须丢弃</b>。拟合半径 = 基准特征到法兰轴的距离，会被延伸杆长度、吸嘴偏心、
    ///      Mark 到镜头的距离一起污染。它只能当诊断量读，写进产物就是把中间量伪装成真值。
    ///   ③ <b>跨度必须够</b>。角度跨度太小时，圆心标准差会被放大几十倍 —— 这是纯几何事实，
    ///      与数据质量无关，所以要在发车前用跨度门禁拦住。
    /// </summary>
    public sealed class RotationCenterRunner
    {
        private readonly IVisualCalibEnvironment _env;
        private readonly SamplingOrchestrator _sampler;

        /// <summary>残差上限（mm）。超过即判失败，避免把"散点拟合出来的圆心"当结果。</summary>
        public double MaxResidualMm = 0.5;

        /// <summary>角度跨度下限（度）。</summary>
        public double MinimumSpanDeg = 30.0;

        public RotationCenterRunner(IVisualCalibEnvironment env, SamplingOrchestrator sampler)
        {
            if (env == null)
            {
                throw new ArgumentNullException("env");
            }

            if (sampler == null)
            {
                throw new ArgumentNullException("sampler");
            }

            _env = env;
            _sampler = sampler;
        }

        /// <summary>
        /// 便捷：按拓扑 + 角度序列生成旋转采样计划。
        /// ★ 这里的角度是<b>绝对角</b>（<c>MoveRotate</c> 的口径）。现场填表用的是"相对基准角的增量"，
        ///   那个换算只在调用点做一次 —— 别让两套口径流进来。
        /// </summary>
        public static SamplePlan BuildPlan(CalibTopology topo, IList<double> absoluteAngles,
            int imageShortSidePx = 0, double mmPerPixel = 0.0)
        {
            var req = new PlanBuildRequest
            {
                Chain = CalibChainKind.RotationCenter,
                Topology = topo,
                ImageShortSidePx = imageShortSidePx,
                MmPerPixel = mmPerPixel
            };

            return SamplePlanBuilder.BuildRotation(req, absoluteAngles, 30.0);
        }

        public ChainRunResult Run(SamplingRequest req, HomMat2D h, ChainRunOptions options,
            Action<RunProgress> report = null, Func<bool> cancel = null)
        {
            if (options == null)
            {
                options = new ChainRunOptions();
            }

            var ctx = new ChainRunContext(_env.Log, report, cancel);
            ctx.Result.Chain = CalibChainKind.RotationCenter;

            CalibTopology topo = _env.ReadTopology() ?? new CalibTopology();
            ctx.Session = ChainRunnerSupport.NewSession(_env, topo, CalibChainKind.RotationCenter,
                req == null ? null : req.SessionId);
            ctx.Result.Session = ctx.Session;
            ctx.Result.Plan = req == null ? null : req.Plan;

            // ═══════════ ① 规划 ═══════════
            ctx.Begin(ServiceStage.Plan);
            if (req == null || req.Plan == null)
            {
                ctx.Fail(ServiceStage.Plan, CalibError.Create(CalibFailureKind.Internal, "没有旋转采样计划"),
                    "旋转中心标定缺少采样计划（用 RotationCenterRunner.BuildPlan 先生成）。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            if (!h.IsFinite)
            {
                ctx.Fail(ServiceStage.Plan, CalibError.Create(CalibFailureKind.SolveFailed, "没有可用的 H"),
                    "旋转中心必须在映射域定圆，但当前没有可用的 H。"
                    + "请先完成「相机看得准不准」（九点标定）—— 像素域定圆在真机上实测偏 8.46 mm，这条捷径不存在。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.AddPlanIssues(req.Plan, ServiceStage.Plan);
            if (req.Plan.HasBlocker)
            {
                var sb = new System.Text.StringBuilder("旋转采样计划存在阻断项：");
                foreach (PlanIssue pi in req.Plan.Blockers())
                {
                    sb.AppendLine().Append("  · ").Append(pi.Message);
                }

                ctx.Fail(ServiceStage.Plan, CalibError.Create(CalibFailureKind.SafetyBlocked, sb.ToString()), sb.ToString());
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.End(ServiceStage.Plan, WizardStepState.Ok, req.Plan.Describe());

            // ═══════════ ②③④ 预检 / 准备 / 采样 ═══════════
            var outcome = new SamplingOutcome();
            ctx.Result.Sampling = outcome;

            ctx.Begin(ServiceStage.Precheck);
            if (req.PrecheckEnabled)
            {
                _sampler.PrecheckInto(req, outcome, ctx.Reporting);
            }

            ctx.AddSamplingIssues(outcome);
            if (outcome.Error != null)
            {
                ctx.End(ServiceStage.Precheck, WizardStepState.Failed, outcome.Error.Message);
                ctx.Fail(ServiceStage.Precheck, outcome.Error, "零运动校核未通过，已中止。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.End(ServiceStage.Precheck, WizardStepState.Ok,
                string.Format(CultureInfo.InvariantCulture, "OK {0} / NG {1} / 未答复 {2}",
                    outcome.CheckOk, outcome.CheckNg, outcome.CheckUnanswered));

            ctx.Begin(ServiceStage.Prepare);
            _sampler.PrepareInto(req, outcome);
            if (outcome.Error != null)
            {
                ctx.End(ServiceStage.Prepare, WizardStepState.Failed, outcome.Error.Message);
                ctx.Fail(ServiceStage.Prepare, outcome.Error, "设备准备失败。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.End(ServiceStage.Prepare, WizardStepState.Ok, "CP 速度 / 软触发 / 参考半径已就绪");

            ctx.Begin(ServiceStage.Sample);
            _sampler.SampleLoop(req, outcome, ctx.Reporting, cancel);
            ctx.Session.Samples.Samples.AddRange(outcome.Samples);

            if (outcome.Cancelled)
            {
                ctx.Result.Cancelled = true;
                ctx.End(ServiceStage.Sample, WizardStepState.Skipped, "用户取消");
                ChainRunnerSupport.Finish(ctx, false, CalibError.Create(CalibFailureKind.UserCancelled, "用户取消"));
                return ctx.Result;
            }

            if (outcome.Error != null)
            {
                ctx.End(ServiceStage.Sample, WizardStepState.Failed, outcome.Error.Message);
                ctx.Fail(ServiceStage.Sample, outcome.Error, "采样中止。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.End(ServiceStage.Sample, WizardStepState.Ok, outcome.Describe());

            int excluded = ChainRunnerSupport.ApplyExclusions(ctx.Session.Samples, options.ExcludeIndices);
            if (excluded > 0)
            {
                ctx.Log("人工剔除 " + excluded + " 个采样点");
            }

            // ═══════════ ⑤ 解算 ═══════════
            ctx.Begin(ServiceStage.Solve);

            var solveReq = new RotationSolveRequest { H = h, HasH = true, MaxResidualMm = MaxResidualMm };
            solveReq.MinimumAngularSpanDeg = MinimumSpanDeg;

            // ★ 角度取<b>反馈角</b>：下发角与反馈角在 PTP/CP 混用时会不一致，
            //   而"转到哪儿了"这个事实只有反馈角说了算。
            List<CalibSample> usable = NinePointSolver.CollectUsable(ctx.Session.Samples.Samples);
            for (int i = 0; i < usable.Count; i++)
            {
                CalibObservation obs = FindObservation(outcome, usable[i].Index);
                if (obs == null)
                {
                    continue;
                }

                solveReq.Observations.Add(obs);
                solveReq.AbsoluteU.Add(usable[i].FeedbackU);
            }

            if (solveReq.Observations.Count < 3)
            {
                ctx.Fail(ServiceStage.Solve, CalibError.Create(CalibFailureKind.SolveFailed,
                        string.Format(CultureInfo.InvariantCulture,
                            "可用旋转观测只有 {0} 个（至少 3 个非同角度的点才能定圆）。"
                            + "请检查转 U 时 Mark 是否还在视野里（转出去就提不到特征）。",
                            solveReq.Observations.Count)),
                    "点数不足，无法定圆。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.Result.RotationCenter = RotationCircleSolver.Solve(solveReq);
            if (!ctx.Result.RotationCenter.Success)
            {
                ctx.Fail(ServiceStage.Solve, ctx.Result.RotationCenter.Error, "旋转中心解算失败。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.End(ServiceStage.Solve, WizardStepState.Ok, ctx.Result.RotationCenter.Describe());

            // ═══════════ ⑥ 校验 ═══════════
            ctx.Begin(ServiceStage.Verify);
            RotationCenterResult rc = ctx.Result.RotationCenter;

            ctx.Result.Add(ChainIssue.Info("ROTATION_SEMANTICS",
                string.Format(CultureInfo.InvariantCulture,
                    "O 的数值等于<b>基准特征的世界位置</b>（H 的坐标原点就是基准特征），"
                    + "所以「基准特征放在旋转轴上」时 O 与轴重合。拟合半径 {0:F4} mm 仅供诊断，"
                    + "已被排除在产物之外 —— 它会被延伸杆长度与吸嘴偏心污染。",
                    rc.FittedRadiusMm), ServiceStage.Verify));

            if (!rc.FittedAtMapped)
            {
                ctx.Fail(ServiceStage.Verify, CalibError.Create(CalibFailureKind.QualityGate,
                        "结果不是在映射域定圆得到的（FittedAtMapped = false）—— 这是绝不允许的状态。"),
                    "反投影域不对：旋转中心必须在映射域定圆。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            // 跨度复核（解析器内部已拦一次，这里只做记录与告警）
            if (rc.AngularSpanDeg < MinimumSpanDeg * 1.5)
            {
                ctx.Result.Add(ChainIssue.Warn("ROTATION_SPAN_TIGHT",
                    string.Format(CultureInfo.InvariantCulture,
                        "旋转跨度只有 {0:F1}°（下限 {1:F1}°，建议 ≥ {2:F1}° 以提高圆心置信度）。"
                        + "跨度越小，圆心的标准差越大 —— 条件允许就转满一圈。",
                        rc.AngularSpanDeg, MinimumSpanDeg, MinimumSpanDeg * 1.5),
                    ServiceStage.Verify));
            }

            // 像素域 vs 映射域之差：它不是装饰性数字，是"为什么必须在映射域定圆"的现场证据
            if (rc.PixelVsMappedCenterMm > 0.2)
            {
                ctx.Result.Add(ChainIssue.Info("PIXEL_DOMAIN_BIAS",
                    string.Format(CultureInfo.InvariantCulture,
                        "对照：像素域定圆的圆心映射回世界后与映射域圆心差 {0:F3} mm。"
                        + "这个差值就是「不能在像素域定圆」的现场证据（点被仿射拉成椭圆，套圆必偏）；"
                        + "本结果的 {1} 点残差 RMS 为 {2:F4} mm，判据以映射域为准。",
                        rc.PixelVsMappedCenterMm, rc.MappedPointCount, rc.RmsMm),
                    ServiceStage.Verify));
            }

            if (rc.MappedPointCount < 6)
            {
                ctx.Result.Add(ChainIssue.Warn("ROTATION_POINTS_FEW",
                    string.Format(CultureInfo.InvariantCulture,
                        "只有 {0} 个映射点参与定圆（建议 ≥ 6）。点数少时单个坏点对圆心的影响会被放大。",
                        rc.MappedPointCount), ServiceStage.Verify));
            }

            ctx.End(ServiceStage.Verify, WizardStepState.Ok,
                string.Format(CultureInfo.InvariantCulture, "残差 RMS {0:F4} mm / 最大 {1:F4} mm，跨度 {2:F1}°",
                    rc.RmsMm, rc.ResidualMaxMm, rc.AngularSpanDeg));

            // ═══════════ ⑦ 可视化 ═══════════
            ctx.Begin(ServiceStage.Visualize);
            // 旋转链的可视化素材就是结果本身：SamplePoints（各角度映射点）、Center、FittedRadiusMm，
            // 以及对照组 PixelDomainCenter —— 界面直接照着画"点 + 拟合圆 + 圆心 + 像素域圆心"。
            ctx.Result.RotationVisual = rc;
            ctx.End(ServiceStage.Visualize, WizardStepState.Ok,
                string.Format(CultureInfo.InvariantCulture, "映射点 {0} 个，拟合半径 {1:F3} mm（仅诊断）",
                    rc.MappedPointCount, rc.FittedRadiusMm));

            // ═══════════ ⑧ 导出与发布 ═══════════
            ctx.Begin(ServiceStage.Export);
            var export = new CalibExport
            {
                SourceSessionId = ctx.Session.SessionId,
                Chain = CalibChainKind.RotationCenter,
                StationCode = topo.StationCode,
                CameraSlotKey = topo.CameraSlotKey,
                H = h,
                HasH = true,
                RotCenterWorld = rc.Center,
                HasRotCenter = true,
                RefU0 = rc.RefU0Deg,
                Handedness = ChainRunnerSupport.HandednessText(topo.Hand),
                CameraMount = ChainRunnerSupport.MountText(topo.CameraMount),
                ImageSize = _env.Camera == null ? null : new int[] { _env.Camera.Width, _env.Camera.Height },
                ProducedBy = options.ProducedBy,
                Diagnostics = BuildRotationDiagnostics(rc)
            };

            export.FramesDir = ctx.Result.ExportDir;
            ChainRunnerSupport.ExportAndPublish(ctx, options, _env, export);
            ctx.End(ServiceStage.Export, WizardStepState.Ok,
                string.IsNullOrEmpty(ctx.Result.ExportDir) ? "未落文件" : ctx.Result.ExportDir);

            ChainRunnerSupport.Finish(ctx, true);
            ctx.Log(ctx.Result.Summary());
            return ctx.Result;
        }

        /// <summary>把旋转链的质量指标塞进同一个 Diagnostics 结构（产物格式只增不删）。</summary>
        private static CalibDiagnostics BuildRotationDiagnostics(RotationCenterResult rc)
        {
            var d = new CalibDiagnostics();
            d.RmsMm = rc.RmsMm;

            d.Notes.Add(string.Format(CultureInfo.InvariantCulture,
                "旋转中心 O = ({0:F4}, {1:F4})；映射点 {2} 个，跨度 {3:F1}°，残差 RMS {4:F4} / 最大 {5:F4} mm。",
                rc.Center.X, rc.Center.Y, rc.MappedPointCount, rc.AngularSpanDeg, rc.RmsMm, rc.ResidualMaxMm));

            d.Notes.Add(string.Format(CultureInfo.InvariantCulture,
                "拟合半径 {0:F4} mm 已按规矩丢弃（会被延伸杆长度/吸嘴偏心污染），仅作诊断读出。",
                rc.FittedRadiusMm));

            if (rc.PixelVsMappedCenterMm > 0.0)
            {
                d.Notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "像素域定圆（错误做法）映射回世界后与映射域圆心差 {0:F3} mm —— 这就是「不能在像素域定圆」的证据。",
                    rc.PixelVsMappedCenterMm));
            }

            return d;
        }

        private static CalibObservation FindObservation(SamplingOutcome outcome, int index)
        {
            if (outcome == null)
            {
                return null;
            }

            for (int i = 0; i < outcome.Observations.Count; i++)
            {
                if (outcome.Observations[i] != null && outcome.Observations[i].Index == index)
                {
                    return outcome.Observations[i];
                }
            }

            return null;
        }
    }
}
