using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>
    /// ★ e 链（吸嘴偏心）：e = O − H(p_tip)，再按基准角归一。
    ///
    /// 关键纪律（三条，任何一条松了都会"越校正越偏"）：
    ///   ① <b>减号</b>：e = O − H(p_tip)。H 是"落点式"口径（像素 → 法兰命令位域），
    ///      两个落点相减得到的才是空间位移。真吸嘴偏心 e 与中间量 ToolEccW = −m 是两回事，
    ///      混用会把偏心量算成它的负值。
    ///   ② <b>按角度归一</b>：e 的定义是"<b>基准角下</b>吸嘴尖相对回转中心的偏移"。
    ///      每个角度直接量出来的 O − H(p_k) 都带着那个角度的旋转，混在一起平均等于把 e 抹掉。
    ///      所以逐点转回 U0 再取均值（见 <see cref="ToolOffsetSolver.SolveNormalized"/>）。
    ///   ③ <b>LegacyUnknown 在固定相机下视为过期</b>：固定相机里相机不动、工具尖动，
    ///      旧口径量出的偏心与新工况无关，必须用图像法重标 —— 这里如实拦，不照单全收。
    /// </summary>
    public sealed class ToolOffsetRunner
    {
        private readonly IVisualCalibEnvironment _env;
        private readonly SamplingOrchestrator _sampler;

        /// <summary>工具尖重复定位离散度上限（mm）。均值总是很漂亮，离散度才是"对针稳不稳"的唯一证据。</summary>
        public double MaxTipScatterMm = 1.0;

        /// <summary>各角度解出的 e 方向最大偏差上限（度）—— "是不是同一根吸嘴 / 对针稳不稳"。</summary>
        public double MaxDirectionSpreadDeg = 20.0;

        /// <summary>偏心量量级告警线（mm）：超过它就提示"是不是对错东西了"。</summary>
        public double EccSanityWarnMm = 20.0;

        public ToolOffsetRunner(IVisualCalibEnvironment env, SamplingOrchestrator sampler)
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

        public ChainRunResult Run(SamplingRequest req, IList<double> absoluteAngles,
            HomMat2D h, Vec2 rotCenterWorld, ITipApproachProvider approach, ChainRunOptions options,
            Action<RunProgress> report = null, Func<bool> cancel = null)
        {
            if (options == null)
            {
                options = new ChainRunOptions();
            }

            var ctx = new ChainRunContext(_env.Log, report, cancel);
            ctx.Result.Chain = CalibChainKind.ToolOffset;

            CalibTopology topo = _env.ReadTopology() ?? new CalibTopology();
            ctx.Session = ChainRunnerSupport.NewSession(_env, topo, CalibChainKind.ToolOffset,
                req == null ? null : req.SessionId);
            ctx.Result.Session = ctx.Session;

            // ═══════════ ① 规划 ═══════════
            ctx.Begin(ServiceStage.Plan);

            if (req == null)
            {
                ctx.Fail(ServiceStage.Plan, CalibError.Create(CalibFailureKind.Internal, "采样请求为空"),
                    "偏心链缺少采样请求。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            if (!h.IsFinite)
            {
                ctx.Fail(ServiceStage.Plan, CalibError.Create(CalibFailureKind.SolveFailed, "没有可用的 H"),
                    "偏心 e = O − H(p_tip) 依赖 H。请先完成「相机看得准不准」（九点标定）。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            if (!rotCenterWorld.IsFinite)
            {
                ctx.Fail(ServiceStage.Plan, CalibError.Create(CalibFailureKind.SolveFailed, "没有可用的旋转中心 O"),
                    "偏心 e = O − H(p_tip) 依赖旋转中心。请先完成「吸嘴转到哪」（旋转中心标定）—— "
                    + "O 是 e 的前置，顺序不能颠倒。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            if (absoluteAngles == null || absoluteAngles.Count == 0)
            {
                ctx.Fail(ServiceStage.Plan, CalibError.Create(CalibFailureKind.Internal, "没有旋转角度序列"),
                    "偏心链至少需要基准角一个采样点；给出更多角度才能验证「是不是同一根吸嘴」。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            // 逐角度先求出"吸嘴对到特征上"的机位。★ 这一步的真值来自现实（对针），不是几何。
            var poses = new List<MotionPose>(absoluteAngles.Count);
            var angles = new List<double>(absoluteAngles.Count);
            for (int i = 0; i < absoluteAngles.Count; i++)
            {
                double u = absoluteAngles[i];
                Vec2 xy;
                string note;
                if (approach == null || !approach.TryGetApproachXy(i, u, topo, out xy, out note) || !xy.IsFinite)
                {
                    ctx.Fail(ServiceStage.Plan, CalibError.Create(CalibFailureKind.Internal,
                            "无法确定 U = " + u.ToString("F2", CultureInfo.InvariantCulture) + "° 处的对针机位。"),
                        "每个角度都必须先把工具尖对到基准特征上（对针）。没有对针机位就没法规划走位 —— "
                        + "真机上这一步由操作员把关或由上一版偏心量推算。");
                    ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                    return ctx.Result;
                }

                poses.Add(new MotionPose(xy.X, xy.Y, topo.WorkZ, u));
                angles.Add(u);
                ctx.Log(string.Format(CultureInfo.InvariantCulture,
                    "对针机位 #{0}：U = {1:F2}°，XY = ({2:F3}, {3:F3}){4}",
                    i + 1, u, xy.X, xy.Y, string.IsNullOrEmpty(note) ? string.Empty : "（" + note + "）"));
            }

            var pbr = new PlanBuildRequest
            {
                Chain = CalibChainKind.ToolOffset,
                Topology = topo,
                ImageShortSidePx = _env.Camera == null ? 0 : Math.Min(_env.Camera.Width, _env.Camera.Height),
                MmPerPixel = 0.0
            };

            // 偏心链不做定圆，所以"跨度"不是硬门禁，只作提醒
            req.Plan = SamplePlanBuilder.BuildRotationAt(pbr, poses, angles, CalibChainKind.ToolOffset, 0.0);
            ctx.Result.Plan = req.Plan;
            ctx.AddPlanIssues(req.Plan, ServiceStage.Plan);

            double span = RotationCircleSolver.SpanDeg(ToArray(absoluteAngles));
            if (span < 30.0)
            {
                ctx.Result.Add(ChainIssue.Warn("TIP_ANGLE_SPAN_SMALL",
                    string.Format(CultureInfo.InvariantCulture,
                        "旋转跨度只有 {0:F1}°：单靠一个角度也能算出 e，但无法验证"
                        + "「各角度解出来的偏心是否一致」。建议至少扫 ±45°。", span),
                    ServiceStage.Plan));
            }

            if (req.Plan.HasBlocker)
            {
                var sb = new System.Text.StringBuilder("偏心采样计划存在阻断项：");
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

            // ═══════════ ⑤ 解算 ═══════════
            ctx.Begin(ServiceStage.Solve);

            var tipPixels = new List<Vec2>();
            var tipAngles = new List<double>();
            var obsList = new List<CalibObservation>();
            var usable = NinePointSolver.CollectUsable(ctx.Session.Samples.Samples);
            for (int i = 0; i < usable.Count; i++)
            {
                CalibObservation obs = FindObservation(outcome, usable[i].Index);
                if (obs == null || !obs.IsUsable)
                {
                    continue;
                }

                // ★ 工具尖压住基准特征时，"特征所在像素"就是"工具尖所在像素"
                tipPixels.Add(obs.Pixel);
                tipAngles.Add(usable[i].FeedbackU);
                obsList.Add(obs);
            }

            if (tipPixels.Count == 0)
            {
                ctx.Fail(ServiceStage.Solve, CalibError.Create(CalibFailureKind.SolveFailed,
                        "没有任何一次成功的对针观测，算不出 p_tip。"),
                    "没有对针观测：请确认对针时基准特征确实在视野里且能被提取到。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            Vec2 equivalentTipPixel;
            Vec2 meanEcc;
            double eccScatterMm;
            bool rotateToU0 = NeedsRotateToU0(topo);
            if (!ToolOffsetSolver.SolveNormalized(h, tipPixels, tipAngles, rotCenterWorld, topo.CalibU0,
                    rotateToU0, out equivalentTipPixel, out meanEcc, out eccScatterMm))
            {
                ctx.Fail(ServiceStage.Solve, CalibError.Create(CalibFailureKind.SolveFailed,
                        "按角度归一求偏心失败（H 不可逆或观测非法）。"),
                    "偏心归一化失败：H 可能不可逆。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.Log(rotateToU0
                ? "偏心口径：像面不随法兰滚转（固定相机 / 非随动安装）⇒ 逐点已转回基准角 U0。"
                : "偏心口径：像面随法兰滚转（相机装在法兰上）⇒ O − H(p_tip) 已是基准角口径，不再额外旋转。");

            ToolOffsetMethod method = ResolveMethod(topo.CameraMount);
            ctx.Result.ToolOffset = ToolOffsetSolver.Solve(h, equivalentTipPixel, rotCenterWorld,
                method, topo.CameraMount, tipPixels.Count, eccScatterMm);

            if (!ctx.Result.ToolOffset.Success)
            {
                ctx.Fail(ServiceStage.Solve, ctx.Result.ToolOffset.Error, "偏心解算失败。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.End(ServiceStage.Solve, WizardStepState.Ok, ctx.Result.ToolOffset.Describe());

            // ═══════════ ⑥ 校验 ═══════════
            ctx.Begin(ServiceStage.Verify);
            ToolOffsetResult e = ctx.Result.ToolOffset;

            double spreadDeg = ToolOffsetSolver.EccDirectionSpreadDeg(h, obsList, tipAngles,
                rotCenterWorld, topo.CalibU0, rotateToU0);

            if (tipPixels.Count <= 1)
            {
                ctx.Result.Add(ChainIssue.Warn("ECC_SINGLE_SAMPLE",
                    "只有一个角度的对针观测：单点也能算出 e，但没有任何一致性证据。"
                    + "建议至少在 ±45° 各再对一次针，看看解出来的偏心方向是否一致。",
                    ServiceStage.Verify));
            }
            else if (!double.IsNaN(spreadDeg) && spreadDeg > MaxDirectionSpreadDeg)
            {
                string msg = string.Format(CultureInfo.InvariantCulture,
                    "各角度解出的偏心方向最大偏差 {0:F1}°（上限 {1:F1}°）：这不像同一根吸嘴的对针结果。"
                    + "典型原因：① 某个角度对针没对好（有人碰到吸嘴）；② 特征提取跳到了别的候选；"
                    + "③ 两次对针之间工件动了。请先看逐点明细，把跳变的那次剔掉再解。",
                    spreadDeg, MaxDirectionSpreadDeg);
                ctx.Fail(ServiceStage.Verify, CalibError.Create(CalibFailureKind.QualityGate, msg), msg);
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            if (e.TipSampleCount >= 2 && e.TipScatterMm > MaxTipScatterMm)
            {
                string msg = string.Format(CultureInfo.InvariantCulture,
                    "工具尖重复定位离散度 {0:F4} mm（上限 {1:F4} mm）：几次对针落点不一致。"
                    + "先解决对针稳定性（吸嘴松动 / 工件没固定 / 对针方式本身重复性差），再谈偏心。",
                    e.TipScatterMm, MaxTipScatterMm);
                ctx.Fail(ServiceStage.Verify, CalibError.Create(CalibFailureKind.QualityGate, msg), msg);
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            if (e.EccMagnitudeMm > EccSanityWarnMm)
            {
                ctx.Result.Add(ChainIssue.Warn("ECC_LARGE",
                    string.Format(CultureInfo.InvariantCulture,
                        "偏心量 {0:F3} mm 偏大（告警线 {1:F1} mm）—— 量级不合理时先怀疑「是不是对错东西了」，"
                        + "而不是先去改参数。", e.EccMagnitudeMm, EccSanityWarnMm),
                    ServiceStage.Verify));
            }

            ctx.End(ServiceStage.Verify, WizardStepState.Ok,
                string.Format(CultureInfo.InvariantCulture,
                    "|e| = {0:F3} mm，方向 {1:F1}°，对针离散 {2:F4} mm，方向偏差 {3}°",
                    e.EccMagnitudeMm, e.EccDirectionDeg, e.TipScatterMm,
                    double.IsNaN(spreadDeg) ? "—" : spreadDeg.ToString("F1", CultureInfo.InvariantCulture)));

            // ═══════════ ⑦ 可视化 ═══════════
            ctx.Begin(ServiceStage.Visualize);
            // 可视化素材：工具尖采样点（像素域散布）+ 世界域的 e 矢量 + 旋转中心。
            // 界面直接画"吸嘴尖几次落在哪、e 往哪偏、|e| 多大"，不需要再猜字段。
            ctx.Result.Add(ChainIssue.Info("ECC_VISUAL",
                string.Format(CultureInfo.InvariantCulture,
                    "工具尖像素：({0:F2},{1:F2})；H(p_tip) = ({2:F4},{3:F4})；O = ({4:F4},{5:F4})；"
                    + "e = ({6:F4},{7:F4})（|e| {8:F3} mm，{9}）",
                    e.TipPixel.X, e.TipPixel.Y, e.TipWorld.X, e.TipWorld.Y,
                    e.RotCenterWorld.X, e.RotCenterWorld.Y, e.Ecc.X, e.Ecc.Y,
                    e.EccMagnitudeMm, method),
                ServiceStage.Visualize));
            ctx.End(ServiceStage.Visualize, WizardStepState.Ok,
                string.Format(CultureInfo.InvariantCulture, "e 矢量与工具尖散布已就绪（{0} 次对针）", e.TipSampleCount));

            // ═══════════ ⑧ 导出与发布 ═══════════
            ctx.Begin(ServiceStage.Export);
            var export = new CalibExport
            {
                SourceSessionId = ctx.Session.SessionId,
                Chain = CalibChainKind.ToolOffset,
                StationCode = topo.StationCode,
                CameraSlotKey = topo.CameraSlotKey,
                H = h,
                HasH = true,
                RotCenterWorld = rotCenterWorld,
                HasRotCenter = true,
                Ecc = e.Ecc,
                HasEcc = true,
                TipWorld = e.TipWorld,
                HasTipWorld = true,
                RefU0 = topo.CalibU0,
                Handedness = ChainRunnerSupport.HandednessText(topo.Hand),
                CameraMount = ChainRunnerSupport.MountText(topo.CameraMount),
                ImageSize = _env.Camera == null ? null : new int[] { _env.Camera.Width, _env.Camera.Height },
                ProducedBy = options.ProducedBy
            };

            var diag = new CalibDiagnostics { RmsMm = eccScatterMm };
            diag.Notes.Add(string.Format(CultureInfo.InvariantCulture,
                "e = O − H(p_tip) = ({0:F4}, {1:F4})，|e| {2:F3} mm，方向 {3:F1}°；"
                + "方法 {4}；对针 {5} 次，离散 {6:F4} mm，方向偏差 {7}°。",
                e.Ecc.X, e.Ecc.Y, e.EccMagnitudeMm, e.EccDirectionDeg, method,
                e.TipSampleCount, e.TipScatterMm,
                double.IsNaN(spreadDeg) ? "—" : spreadDeg.ToString("F1", CultureInfo.InvariantCulture)));
            diag.Notes.Add("★ 注意：ToolEccW = −m 是「偏心延伸杆末端」的中间量，不是真吸嘴偏心 e，两者不可混用。");
            diag.Notes.Add(rotateToU0
                ? "角度口径：像面不随法兰滚转（固定相机 / 非随动安装）⇒ 每个角度的 O − H(p_k) 已逐点转回基准角 U0 再取均值。"
                : "角度口径：像面随法兰滚转（相机装在法兰上，眼在手）⇒ 「尖压住特征」相对法兰是刚性状态，"
                  + "O − H(p_k) 本就是基准角口径，未再做额外旋转（多转一次会让 |e| 缩水并伪造方向偏差告警）。");
            export.Diagnostics = diag;

            ChainRunnerSupport.ExportAndPublish(ctx, options, _env, export);
            ctx.End(ServiceStage.Export, WizardStepState.Ok,
                string.IsNullOrEmpty(ctx.Result.ExportDir) ? "未落文件" : ctx.Result.ExportDir);

            ChainRunnerSupport.Finish(ctx, true);
            ctx.Log(ctx.Result.Summary());
            return ctx.Result;
        }

        /// <summary>方法语义：固定相机走图像法；眼在手走间接对针；眼型未知时如实标 LegacyUnknown。</summary>
        private static ToolOffsetMethod ResolveMethod(CameraMountKind mount)
        {
            switch (mount)
            {
                case CameraMountKind.EyeToHand:
                    return ToolOffsetMethod.EyeToHandImage;
                case CameraMountKind.EyeInHand:
                    return ToolOffsetMethod.EyeInHandIndirect;
                default:
                    return ToolOffsetMethod.LegacyUnknown;
            }
        }

        /// <summary>
        /// ★ 要不要把各角度的 O − H(p_k) 再"转回基准角 U0"？
        ///
        /// 判据是<b>像面是否随法兰滚转</b>，不是"想不想更严谨"：
        ///   · 眼在手（相机装在法兰上）⇒ 转 U 时画面与相机一起滚，"吸嘴尖压住基准特征"这一状态
        ///     相对法兰是<b>刚性的</b> ⇒ 观测像素与角度无关 ⇒ O − H(p_k) 本来就是基准角口径。
        ///     此时再乘 R(U0−U) 是<b>双重旋转</b>：报告 |e| 会按 (1+2cosΔ)/3 缩水，
        ///     方向偏差被凭空造出来，最终以"对针不稳"的名义把一个完美的标定拦下来。
        ///     （对称角 −45/0/+45 下这一步恰好是恒等 —— 所以这个坑会被对称角完全掩盖。）
        ///   · 固定相机（相机根本不动）⇒ 观测带着 R(ΔU) ⇒ <b>必须</b>转回，否则 e 会被平均抹掉。
        ///
        /// 所以：眼在手 + 像面随动 ⇒ 不转；固定相机（或显式声明不随动）⇒ 转。
        /// </summary>
        private static bool NeedsRotateToU0(CalibTopology topo)
        {
            if (topo == null)
            {
                return true; // 拓扑未知时保守按"需要归一"处理（这是老口径，不会凭空改变行为）
            }

            if (topo.CameraMount == CameraMountKind.EyeToHand)
            {
                return true;  // 相机不动 ⇒ 谈不上"随法兰滚转" ⇒ 必须归一
            }

            return !topo.CameraRollsWithFlange;
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
    }
}
