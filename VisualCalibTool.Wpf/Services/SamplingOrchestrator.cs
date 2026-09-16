using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>
    /// ★★ 采样编排器（<b>唯一实现</b>）。
    ///
    /// 设计文档 §6.3 的"介质解耦"就落在这里：上层（三条链的 runner、向导视图模型）
    /// 只说"按这份计划采一批点"，不关心是仿真还是真机、是圆 Mark 还是模板匹配。
    /// 于是<b>同一段采样代码</b>在 0 硬件下就能把 H/e/t 三条链完整跑通 —— 换真机只换环境实现。
    ///
    /// 每个点的固定次序（次序本身就是纪律，不许优化掉任何一步）：
    ///   ① 抬到安全 Z（涉及转 U 或 Z 变化时必做）
    ///   ② 平面走位（XY + U 同步，Z 不动）—— 贴着工件横向挪比"抬起来再压下去"更稳
    ///   ③ 下压到工作 Z
    ///   ④ 稳定，然后读<b>控制器反馈位</b>（★ 真值只认反馈位，不认下发值）
    ///   ⑤ 软触发取一帧（★ 禁连续自由流：连续流下"等新帧"等于等一个随机时刻，
    ///      慢帧必超时，还可能拿到<b>上一个位置</b>的帧 —— 这是历史上最难查的一类标定错误）
    ///   ⑥ 留档（PGM，可被 HALCON/Python 直接打开）
    ///   ⑦ 特征提取（期望位置由 <see cref="ProgressivePixelPredictor"/> 逐点升级）
    ///   ⑧ 记录观测（含过程叠加轨迹）
    /// </summary>
    public sealed class SamplingOrchestrator
    {
        private const double ZEps = 1e-3;
        private const double UEpsDeg = 1e-6;

        private readonly IMotionGateway _motion;
        private readonly ICameraGateway _camera;
        private readonly IFeatureExtractor _extractor;
        private readonly ISafetyGuard _guard;
        private readonly ICalibStore _store;
        private readonly ICalibLog _log;

        public SamplingOrchestrator(
            IMotionGateway motion,
            ICameraGateway camera,
            IFeatureExtractor extractor,
            ISafetyGuard guard,
            ICalibStore store,
            ICalibLog log)
        {
            if (motion == null)
            {
                throw new ArgumentNullException("motion");
            }

            if (camera == null)
            {
                throw new ArgumentNullException("camera");
            }

            if (extractor == null)
            {
                throw new ArgumentNullException("extractor");
            }

            _motion = motion;
            _camera = camera;
            _extractor = extractor;
            _guard = guard;
            _store = store;
            _log = log;
        }

        // ══════════════════════════════════════════════════════════════
        // 可调策略（有默认值，现场可改）
        // ══════════════════════════════════════════════════════════════

        /// <summary>未指定安全 Z 时的抬升量（mm）。</summary>
        public double SafeZLiftMm = 30.0;

        /// <summary>
        /// 控制器不答复零运动校核时，是否也算阻断。
        /// ★ 默认 false：有些固件根本没有 <c>CHECK</c>，直接判死会让工具在这些工位不可用。
        ///   但会留下明确告警 —— "可达性未判定"这件事必须写在明处，不能悄悄放行。
        /// </summary>
        public bool RequireReachAnswer;

        /// <summary>旋转采样的第一段兜底 ROI 半径（px）。</summary>
        public double RotationSeedRadiusPx = 250.0;

        // ══════════════════════════════════════════════════════════════
        // ② 零运动校核（只问合法性，不发车）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 逐个点问控制器"这个点合法吗"。★ 上位机没有臂长与关节限位，
        /// 一律让控制器答；我们只做矩形软限位这种粗筛。
        /// </summary>
        public void PrecheckInto(SamplingRequest req, SamplingOutcome outcome,
            Action<RunProgress> report)
        {
            if (req == null || req.Plan == null)
            {
                return;
            }

            List<PlannedSample> ordered = req.Plan.InExecutionOrder();
            int total = ordered.Count;
            int k = 0;

            for (int i = 0; i < ordered.Count; i++)
            {
                PlannedSample s = ordered[i];
                if (s.Skipped)
                {
                    continue;
                }

                k++;
                Report(report, req, outcome, ServiceStage.Precheck, total > 0 ? (double)k / total : 0.0,
                    total, k, s.Target, "零运动校核 " + s.Short());

                // 上位机粗筛（矩形软限位 / Z 范围 / |XY| 上限）
                if (_guard != null)
                {
                    CalibError g = _guard.Validate(s.Target);
                    if (g != null)
                    {
                        s.SkipReason = "上位机安全校验未通过：" + g.Message;
                        outcome.SkipUnreachableCount++;
                        outcome.Add(ChainIssue.Blocker(PlanIssueCode.SoftLimitExceeded,
                            s.SkipReason, ServiceStage.Precheck, s.Index));
                        continue;
                    }
                }

                ReachCheckResult rc = _motion.CheckTarget(s.Target);
                if (rc == null || !rc.Answered)
                {
                    outcome.CheckUnanswered++;
                    ChainIssue ci = ChainIssue.Warn(PlanIssueCode.ReachUnknown,
                        s.Short() + "：控制器未答复可达性（该指令可能不被支持）。"
                        + "★ 这意味着这一点的可达性<b>没有</b>被验证过，请人工确认后再开跑。",
                        ServiceStage.Precheck, s.Index);
                    if (RequireReachAnswer)
                    {
                        ci.Severity = PlanIssueSeverity.Blocker;
                        s.SkipReason = "可达性未判定（控制器未答复），且当前设置为必须判定";
                        outcome.SkipUnreachableCount++;
                    }

                    outcome.Add(ci);
                    continue;
                }

                if (!rc.IsReachable)
                {
                    outcome.CheckNg++;
                    s.SkipReason = "控制器零运动校核返回 NG（该目标点不可达）";
                    outcome.SkipUnreachableCount++;
                    outcome.Add(ChainIssue.Blocker(PlanIssueCode.Unreachable,
                        s.SkipReason + "：" + s.Short(), ServiceStage.Precheck, s.Index));

                    if (!req.SkipUnreachable)
                    {
                        outcome.Error = rc.ToError(s.Index);
                        return;
                    }

                    continue;
                }

                outcome.CheckOk++;
            }

            Log("预检：校核 OK " + outcome.CheckOk + " / NG " + outcome.CheckNg
                + " / 未答复 " + outcome.CheckUnanswered
                + "，跳过 " + outcome.SkipUnreachableCount + " 点");
        }

        // ══════════════════════════════════════════════════════════════
        // ③ 准备
        // ══════════════════════════════════════════════════════════════

        /// <summary>CP 速度 + 软触发 + 参考半径 + 安全抬升。</summary>
        public void PrepareInto(SamplingRequest req, SamplingOutcome outcome)
        {
            // ★ CP 速度（管 Move）与 PTP 速度（管 Go）是两套参数，而且会被 INIT / MOTOR ON 复位，
            //   所以每次开跑都要重设，不能"上次设过就算了"。
            OpResult sp = _motion.ApplyCpSpeed(req.MoveSpeedMmPerSec, req.MoveAccelMmPerSec2);
            if (!sp.Ok)
            {
                outcome.Error = sp.ToError(CalibFailureKind.SafetyBlocked);
                outcome.Add(ChainIssue.Blocker("SPEED_SETUP_FAILED",
                    "CP 速度设置失败：" + sp, ServiceStage.Prepare));
                return;
            }

            OpResult tr = _camera.ConfigureSoftwareTrigger();
            if (!tr.Ok)
            {
                outcome.Error = tr.ToError(CalibFailureKind.Internal);
                outcome.Add(ChainIssue.Blocker("TRIGGER_SETUP_FAILED",
                    "软触发配置失败：" + tr, ServiceStage.Prepare));
                return;
            }

            // ★ 参考半径是跨调用状态（首点自动写入，此后用于滤掉半径不符的伪特征）。
            //   每轮开始必须重置，否则"上一块板子的半径"会拿来卡这一轮的候选。
            _extractor.ResetMarkReference();

            if (req.LiftToSafeZ)
            {
                MotionPose cur = _motion.GetPosition();
                double safeZ = ResolveSafeZ(req, cur.Z);
                if (cur.IsFinite && cur.Z < safeZ - ZEps)
                {
                    OpResult up = MoveTo(req, new MotionPose(cur.X, cur.Y, safeZ, cur.U), outcome);
                    if (!up.Ok)
                    {
                        outcome.Error = up.ToError(CalibFailureKind.MotionRejected);
                        outcome.Add(ChainIssue.Blocker("SAFE_LIFT_FAILED",
                            "开跑前抬升失败：" + up, ServiceStage.Prepare));
                        return;
                    }
                }
            }

            Log("准备完成：环境 " + _motion.EnvironmentKind + "，像素 " + _camera.Width + "×" + _camera.Height
                + "，CP 速度 " + req.MoveSpeedMmPerSec.ToString("F1", CultureInfo.InvariantCulture) + " mm/s");
        }

        // ══════════════════════════════════════════════════════════════
        // ④ 采样主循环
        // ══════════════════════════════════════════════════════════════

        public SamplingOutcome Run(SamplingRequest req, Action<RunProgress> progress, Func<bool> isCancelled)
        {
            var outcome = new SamplingOutcome();

            if (req == null || req.Plan == null)
            {
                outcome.Error = CalibError.Create(CalibFailureKind.Internal, "没有采样计划，无法开跑。");
                return outcome;
            }

            if (!req.DryRunNoMotion && !req.Plan.IsViable && req.Plan.HasBlocker)
            {
                outcome.Error = CalibError.Create(CalibFailureKind.SafetyBlocked,
                    "采样计划存在阻断项，拒绝开跑（详见计划告警）。");
                return outcome;
            }

            // ① 预检
            if (req.PrecheckEnabled)
            {
                Report(progress, req, outcome, ServiceStage.Precheck, 0.0,
                    req.Plan.Samples.Count, 0, default(MotionPose), ServiceStage.Title(ServiceStage.Precheck));
                PrecheckInto(req, outcome, progress);
                if (outcome.Error != null)
                {
                    return outcome;
                }
            }

            // ② 准备
            Report(progress, req, outcome, ServiceStage.Prepare, 0.0,
                req.Plan.Samples.Count, 0, default(MotionPose), ServiceStage.Title(ServiceStage.Prepare));
            PrepareInto(req, outcome);
            if (outcome.Error != null)
            {
                return outcome;
            }

            // ③ 逐点
            SampleLoop(req, outcome, progress, isCancelled);
            return outcome;
        }

        /// <summary>
        /// 第 4 步主循环（公开，便于 runner 把八步分开记会话步 —— 这样"卡在哪一步"才对称）。
        /// 前置状态：<see cref="PrecheckInto"/> 与 <see cref="PrepareInto"/> 已经跑过。
        /// </summary>
        public void SampleLoop(SamplingRequest req, SamplingOutcome outcome,
            Action<RunProgress> progress, Func<bool> isCancelled)
        {
            if (req == null || req.Plan == null || outcome == null)
            {
                return;
            }

            var predictor = new ProgressivePixelPredictor(EstimateMmPerPixel(req.Plan));
            predictor.RotationMode = IsRotationalChain(req.Chain);
            predictor.RotationSeedRadiusPx = RotationSeedRadiusPx;

            List<PlannedSample> ordered = req.Plan.InExecutionOrder();
            int total = ordered.Count;

            for (int i = 0; i < ordered.Count; i++)
            {
                PlannedSample planned = ordered[i];
                if (planned.Skipped)
                {
                    Log("跳过 " + planned.Short() + "：" + planned.SkipReason);
                    continue;
                }

                if (isCancelled != null && isCancelled())
                {
                    outcome.Cancelled = true;
                    outcome.Add(ChainIssue.Info("CANCELLED", "用户取消，已在第 " + (i + 1) + " 点停下", ServiceStage.Sample));
                    break;
                }

                double frac = total > 0 ? (double)i / total : 0.0;
                Report(progress, req, outcome, ServiceStage.Sample, frac, total, i + 1, planned.Target, planned.Short());

                var rec = new CalibSample
                {
                    Index = planned.Index,
                    PlanXy = planned.Target.Xy,
                    PlanZ = planned.Target.Z,
                    PlanU = planned.Target.U
                };
                outcome.Samples.Add(rec);

                bool angleChanges = HasAngleChange(ordered, i);
                CalibObservation obs = SampleOne(req, planned, rec, predictor, outcome,
                    i == 0 || angleChanges || req.LiftBetweenPoints);

                if (obs != null)
                {
                    outcome.Observations.Add(obs);
                    if (obs.IsUsable)
                    {
                        predictor.Observe(obs.Pixel, obs.World);
                        if (obs.IsLowQuality(0.6))
                        {
                            outcome.Add(ChainIssue.Warn("LOW_QUALITY",
                                string.Format(CultureInfo.InvariantCulture,
                                    "#{0} 质量分 {1:F0}/100 偏低（{2}）—— 低分点会拉低解算，建议剔掉或重采。",
                                    obs.Index, obs.MatchScore, obs.Verdict),
                                ServiceStage.Sample, obs.Index));
                        }
                    }
                }

                Report(progress, req, outcome, ServiceStage.Sample, total > 0 ? (double)(i + 1) / total : 0.0,
                    total, i + 1, planned.Target, planned.Short(), obs);

                if (outcome.Error != null)
                {
                    break;
                }
            }

            // ④ 收尾：抬回安全 Z（不要留一把刀在工作面上）
            if (!req.DryRunNoMotion && req.LiftToSafeZ && !outcome.Cancelled)
            {
                MotionPose cur = _motion.GetPosition();
                double safeZ = ResolveSafeZ(req, cur.Z);
                if (cur.IsFinite && cur.Z < safeZ - ZEps)
                {
                    MoveTo(req, new MotionPose(cur.X, cur.Y, safeZ, cur.U), outcome);
                }
            }

            Log(outcome.Describe());
        }

        /// <summary>
        /// 单点重采（"这一点不行，把它重来一遍"）。
        /// ★ 只重采这一点，<b>不</b>影响其它点的记录 —— 人工剔点必须能精确到点，
        ///   否则一次失误就得整轮重跑，现场没人受得了。
        /// </summary>
        public CalibObservation ResampleOne(SamplingRequest req, PlannedSample planned, CalibSample rec,
            Action<RunProgress> report, bool isFirstOrAngleChange)
        {
            if (req == null || planned == null || rec == null)
            {
                return null;
            }

            var outcome = new SamplingOutcome();

            if (req.PrecheckEnabled)
            {
                ReachCheckResult rc = _motion.CheckTarget(planned.Target);
                if (rc != null && rc.Answered && !rc.IsReachable)
                {
                    outcome.Error = rc.ToError(planned.Index);
                    reportError(report, req, planned, "重采前校核返回 NG：该点不可达");
                    return null;
                }
            }

            PrepareInto(req, outcome);
            if (outcome.Error != null)
            {
                reportError(report, req, planned, "重采准备失败：" + outcome.Error);
                return null;
            }

            var predictor = new ProgressivePixelPredictor(EstimateMmPerPixel(req.Plan));
            predictor.RotationMode = IsRotationalChain(req.Chain);
            predictor.RotationSeedRadiusPx = RotationSeedRadiusPx;

            CalibObservation obs = SampleOne(req, planned, rec, predictor, outcome, isFirstOrAngleChange);
            Log("重采 #" + planned.Index + "：" + (obs == null ? "失败" : obs.ToString()));
            return obs;
        }

        private void reportError(Action<RunProgress> report, SamplingRequest req, PlannedSample planned, string msg)
        {
            if (report == null)
            {
                return;
            }

            report(new RunProgress
            {
                StepKey = ServiceStage.Key(ServiceStage.Sample),
                StepTitle = ServiceStage.Title(ServiceStage.Sample),
                SampleIndex = planned.Index,
                SampleTotal = req.Plan == null ? 0 : req.Plan.Samples.Count,
                StageIndex = ServiceStage.Sample,
                StageTotal = ServiceStage.Total,
                Fraction = ServiceStage.FractionAt(ServiceStage.Sample),
                Error = CalibError.Create(CalibFailureKind.Unreachable, msg).WithSample(planned.Index)
            });
        }

        // ══════════════════════════════════════════════════════════════
        // 单点：走位 → 稳定 → 反馈位 → 软触发 → 留档 → 提取 → 记录
        // ══════════════════════════════════════════════════════════════

        private CalibObservation SampleOne(SamplingRequest req, PlannedSample planned, CalibSample rec,
            ProgressivePixelPredictor predictor, SamplingOutcome outcome, bool liftFirst)
        {
            var sw = Stopwatch.StartNew();

            // ── ① 走位 ──
            if (!req.DryRunNoMotion)
            {
                MotionPose target = planned.Target;

                // 偏心链的"对针"钩子：每个角度都得先把工具尖对到特征上
                if (req.PoseAdjuster != null)
                {
                    try
                    {
                        MotionPose adjusted = req.PoseAdjuster(planned.Index, target);
                        if (adjusted.IsFinite)
                        {
                            target = adjusted;
                        }
                    }
                    catch (Exception ex)
                    {
                        outcome.ExtractFailed++;
                        rec.State = CalibSampleState.Failed;
                        rec.Error = CalibError.Create(CalibFailureKind.Internal,
                            "走位前调整目标位失败：" + ex.Message).WithSample(planned.Index);
                        return null;
                    }
                }

                rec.State = CalibSampleState.Moving;
                OpResult mv = TraverseTo(req, target, liftFirst, outcome);
                if (!mv.Ok)
                {
                    outcome.MoveRejected++;
                    rec.State = CalibSampleState.Failed;
                    rec.Error = mv.ToError(CalibFailureKind.MotionRejected).WithSample(planned.Index);
                    outcome.Add(ChainIssue.Blocker("MOVE_REJECTED",
                        string.Format(CultureInfo.InvariantCulture, "#{0} 运动被拒：{1}",
                            planned.Index, mv), ServiceStage.Sample, planned.Index));
                    return null;
                }
            }

            // ── ② 稳定 + 读反馈位（★ 真值只认反馈位）──
            rec.State = CalibSampleState.Settling;
            MotionPose fb = _motion.GetFeedbackPosition();
            rec.FeedbackXy = fb.Xy;
            rec.FeedbackU = fb.U;

            if (!fb.IsFinite)
            {
                rec.State = CalibSampleState.Failed;
                rec.Error = CalibError.Create(CalibFailureKind.Internal,
                    "控制器反馈位非法（NaN/Inf）—— 没有真值就无法标定。").WithSample(planned.Index);
                return null;
            }

            // ── ③ 软触发取帧 ──
            rec.State = CalibSampleState.Grabbing;
            CalibError grabError;
            byte[] frame = _camera.GrabFrame(req.GrabTimeoutMs, out grabError);
            outcome.GrabCount++;
            if (frame == null || frame.Length < _camera.Width * _camera.Height)
            {
                outcome.GrabFailed++;
                rec.State = CalibSampleState.Failed;
                rec.Error = grabError ?? CalibError.Create(CalibFailureKind.GrabTimeout,
                    "取图失败（帧为空或尺寸不足）").WithSample(planned.Index);
                outcome.Add(ChainIssue.Blocker("GRAB_FAILED",
                    string.Format(CultureInfo.InvariantCulture, "#{0} 取图失败：{1}",
                        planned.Index, rec.Error.Message), ServiceStage.Sample, planned.Index));
                return null;
            }

            // ── ④ 留档 ──
            if (req.ArchiveFrames && _store != null)
            {
                byte[] pgm = PgmCodec.Encode(frame, _camera.Width, _camera.Height);
                string path = _store.SaveFrame(req.SessionId, planned.Index, pgm, ".pgm");
                if (!string.IsNullOrEmpty(path))
                {
                    rec.FramePath = path;
                    outcome.FramesArchived++;
                }
            }

            // ── ⑤ 期望位置 & 特征提取 ──
            Vec2 expected = Vec2.Zero;
            double searchRadius = 0.0;
            bool exact = false;
            bool hasExpectation = false;

            if (!req.DryRunNoMotion)
            {
                hasExpectation = predictor.TryPredict(planned.Target.Xy, out expected, out searchRadius, out exact);
                if (hasExpectation && (!expected.IsFinite || expected.X <= 0.0 || expected.Y <= 0.0))
                {
                    // 落在图像左/上边界之外（或非法）→ 期望值反而会误导 ROI，直接放弃
                    hasExpectation = false;
                }

                if (hasExpectation && (expected.X > _camera.Width || expected.Y > _camera.Height))
                {
                    outcome.Add(ChainIssue.Warn("EXPECT_OUT_OF_IMAGE",
                        string.Format(CultureInfo.InvariantCulture,
                            "#{0} 预测落点 ({1:F0},{2:F0}) 已超出图像 {3}×{4} → 改走全图搜索。",
                            planned.Index, expected.X, expected.Y, _camera.Width, _camera.Height),
                        ServiceStage.Sample, planned.Index));
                    hasExpectation = false;
                }
            }
            else if (req.UsePlanPrediction && planned.HasPredictedPixel)
            {
                expected = planned.PredictedPixel;
                hasExpectation = expected.IsFinite && expected.X > 0.0 && expected.Y > 0.0;
                exact = true;
            }

            MarkSpec spec = req.Mark == null ? new MarkSpec() : req.Mark.Clone();
            if (hasExpectation && !exact && searchRadius > 0.0)
            {
                // ★ 非精确预测时，把 ROI 半径放大到"能覆盖一整步位移"。
                //   方向是未知的，所以只放大半径，**绝不**去猜方向。
                spec.SearchRadiusPx = searchRadius;
            }

            rec.State = CalibSampleState.Extracting;
            ExtractionTrace trace = req.CaptureTrace ? new ExtractionTrace() : null;

            CalibObservation obs;
            try
            {
                obs = _extractor.Extract(frame, _camera.Width, _camera.Height, spec,
                    hasExpectation ? expected.X : 0.0,
                    hasExpectation ? expected.Y : 0.0,
                    planned.Index, trace);
            }
            catch (Exception ex)
            {
                outcome.ExtractFailed++;
                rec.State = CalibSampleState.Failed;
                rec.Error = CalibError.Create(CalibFailureKind.Internal,
                    "特征提取抛出异常：" + ex.Message).WithSample(planned.Index);
                Log("提取异常 #" + planned.Index + "：" + ex.Message);
                return null;
            }

            if (obs == null)
            {
                outcome.ExtractFailed++;
                rec.State = CalibSampleState.Failed;
                rec.Error = CalibError.Create(CalibFailureKind.FeatureNotFound,
                    "提取器没有返回观测。").WithSample(planned.Index);
                return null;
            }

            // ── ⑥ 记录 ──
            obs.Index = planned.Index;
            obs.World = fb.Xy;
            obs.HasWorld = true;
            obs.FeatureKind = spec.Kind;
            obs.FramePath = rec.FramePath;
            obs.Trace = trace;
            obs.ElapsedMs = sw.ElapsedMilliseconds;

            rec.HasPixel = obs.HasPixel;
            rec.Pixel = obs.Pixel;
            rec.Quality = obs.Quality;
            rec.CandidateCount = obs.CandidateCount;
            rec.ElapsedMs = sw.ElapsedMilliseconds;

            if (obs.IsUsable)
            {
                rec.State = CalibSampleState.Ok;
                Log(string.Format(CultureInfo.InvariantCulture,
                    "#{0} 反馈位 {1} → 像素 ({2:F2},{3:F2})，质量 {4:F0}/100 [{5}]",
                    planned.Index, fb, obs.Pixel.X, obs.Pixel.Y, obs.MatchScore, predictor.LastSource));
            }
            else
            {
                outcome.ExtractFailed++;
                rec.State = CalibSampleState.Failed;
                rec.Error = obs.Error ?? CalibError.Create(CalibFailureKind.FeatureNotFound,
                    string.IsNullOrEmpty(obs.RejectReason) ? "特征提取失败" : obs.RejectReason)
                    .WithSample(planned.Index);
                obs.HasPixel = false;

                outcome.Add(ChainIssue.Blocker("FEATURE_NOT_FOUND",
                    string.Format(CultureInfo.InvariantCulture,
                        "#{0} 特征提取失败：{1}。期望位置{2}，候选 {3} 个。",
                        planned.Index,
                        string.IsNullOrEmpty(obs.RejectReason) ? "未知原因" : obs.RejectReason,
                        hasExpectation
                            ? string.Format(CultureInfo.InvariantCulture, "({0:F0},{1:F0})", expected.X, expected.Y)
                            : "无（全图搜索）",
                        obs.CandidateCount),
                    ServiceStage.Sample, planned.Index));
            }

            return obs;
        }

        // ══════════════════════════════════════════════════════════════
        // 运动
        // ══════════════════════════════════════════════════════════════

        private OpResult TraverseTo(SamplingRequest req, MotionPose target, bool liftFirst, SamplingOutcome outcome)
        {
            MotionPose cur = _motion.GetPosition();
            double safeZ = ResolveSafeZ(req, target.Z);

            // ① 抬到安全 Z
            if (liftFirst && cur.Z < safeZ - ZEps)
            {
                OpResult up = MoveTo(req, new MotionPose(cur.X, cur.Y, safeZ, cur.U), outcome);
                if (!up.Ok)
                {
                    return up;
                }

                cur = _motion.GetPosition();
            }

            // ② 平面段：XY 与 U 一起走，Z 不动
            if (Math.Abs(cur.X - target.X) > ZEps
                || Math.Abs(cur.Y - target.Y) > ZEps
                || Math.Abs(cur.U - target.U) > UEpsDeg)
            {
                OpResult planar = MoveTo(req, new MotionPose(target.X, target.Y, cur.Z, target.U), outcome);
                if (!planar.Ok)
                {
                    return planar;
                }

                cur = _motion.GetPosition();
            }

            // ③ 下压段
            if (Math.Abs(cur.Z - target.Z) > ZEps)
            {
                OpResult down = MoveTo(req, new MotionPose(target.X, target.Y, target.Z, target.U), outcome);
                if (!down.Ok)
                {
                    return down;
                }
            }

            return OpResult.Success();
        }

        private OpResult MoveTo(SamplingRequest req, MotionPose pose, SamplingOutcome outcome)
        {
            if (outcome != null)
            {
                outcome.MoveCount++;
            }

            return _motion.MoveLinear(pose, req.MoveSpeedMmPerSec, req.MoveAccelMmPerSec2);
        }

        private double ResolveSafeZ(SamplingRequest req, double fallbackZ)
        {
            double safeZ = double.IsNaN(req.SafeZ) ? fallbackZ + SafeZLiftMm : req.SafeZ;

            // ★ 抬升量算出来超过 Z 上限时夹回上限：本工位 Z ∈ [−144, 0]，"抬到 +30" 是发不出去的。
            //   上限的权威在控制器侧，这里只是拿安全守卫里记着的已知范围兜一下。
            var rect = _guard as RectSafetyGuard;
            if (rect != null && safeZ > rect.ZMax)
            {
                safeZ = rect.ZMax;
            }

            return safeZ;
        }

        private static bool HasAngleChange(IList<PlannedSample> ordered, int index)
        {
            if (index <= 0 || ordered == null || index >= ordered.Count)
            {
                return index <= 0;
            }

            double prev = ordered[index - 1].Target.U;
            double cur = ordered[index].Target.U;
            return Math.Abs(prev - cur) > UEpsDeg || Math.Abs(prev - cur) > 360.0 - UEpsDeg;
        }

        /// <summary>旋转类链：法兰 XY 不动、只转 U（预测器必须切到旋转模式）。</summary>
        public static bool IsRotationalChain(CalibChainKind chain)
        {
            return chain == CalibChainKind.RotationCenter || chain == CalibChainKind.ToolRotation
                   || chain == CalibChainKind.ToolOffset;
        }

        private static double EstimateMmPerPixel(SamplePlan plan)
        {
            if (plan == null || plan.StepSpanPx <= 0.0)
            {
                return 0.0;
            }

            double diag = Math.Sqrt(plan.StepX * plan.StepX + plan.StepY * plan.StepY);
            return diag > 0.0 ? diag / plan.StepSpanPx : 0.0;
        }

        private void Report(Action<RunProgress> report, SamplingRequest req, SamplingOutcome outcome,
            int stage, double within,
            int sampleTotal, int sampleIndex, MotionPose target, string title,
            CalibObservation obs = null)
        {
            if (report == null)
            {
                return;
            }

            report(new RunProgress
            {
                StepKey = ServiceStage.Key(stage),
                StepTitle = title,
                SampleIndex = sampleIndex,
                SampleTotal = sampleTotal,

                // ★ 把"成功 / 失败"一并带上：界面要显示的是"3/9 点，成功 2、失败 1"，
                //   而不是只有"3/9"—— 只报位置不报成败的话，用户看不出"卡在哪一点"。
                SampleOk = outcome == null ? 0 : outcome.UsableCount,
                SampleFailed = outcome == null ? 0 : outcome.ExtractFailed,

                // ★ 把本点的观测带上：界面的"边跑边看"（显示刚采到的那一帧、它的像素与质量分）
                //   全靠它。原来这个字段只有 ChainRunContext.ProgressObservation 会填，
                //   而没有任何地方调用那个方法 —— 于是"实时可视化"整条路都是死的。
                //   这是端到端断言抓出来的第三个真实缺陷。
                LastObservation = obs,

                StageIndex = stage,
                StageTotal = ServiceStage.Total,
                Fraction = ServiceStage.FractionAt(stage, within)
            });
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                _log.Info("[采样] " + message);
            }
        }
    }
}
