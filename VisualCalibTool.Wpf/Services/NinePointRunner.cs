using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>
    /// ★ H 链（九点）：像素 → 世界（法兰命令位域）的 6 参数仿射，产出可被主项目消费的 .tup。
    ///
    /// 八步（见 <see cref="ServiceStage"/>）里最值得强调的是第 6 与第 7 步：
    ///   · 第 6 步 <b>形状优先于 RMS</b>：落点是差分 H(p_tip) − H(u)，仿射吸收不掉的系统性误差
    ///     全进 σ1/σ2，随机误差全进 RMS。真机实测过"失真 22.7% × 跨 84 mm ≈ 19 mm"而 RMS 只有
    ///     0.54 mm —— 也就是说<b>RMS 好看完全不代表落点准</b>。所以唯一硬拦判据是 |σ1/σ2 − 1|。
    ///   · 第 7 步 <b>AR 反投影的优先级高于任何数字</b>：把每个点的世界位经 H⁻¹ 投回图像，
    ///     和实际特征比，"目视重合"才算过。这一项不能只给一个 RMS 了事，
    ///     因为它能区分四种完全不同的根因（系统性偏置 / 越往外越偏 / 内外圈差异 / 随机散布）。
    /// </summary>
    public sealed class NinePointRunner
    {
        private readonly IVisualCalibEnvironment _env;
        private readonly SamplingOrchestrator _sampler;

        public NinePointRunner(IVisualCalibEnvironment env, SamplingOrchestrator sampler)
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

        public ChainRunResult Run(SamplingRequest req, ChainRunOptions options,
            Action<RunProgress> report = null, Func<bool> cancel = null)
        {
            if (options == null)
            {
                options = new ChainRunOptions();
            }

            var ctx = new ChainRunContext(_env.Log, report, cancel);
            ctx.Result.Chain = CalibChainKind.NinePoint;

            CalibTopology topo = _env.ReadTopology() ?? new CalibTopology();
            ctx.Session = ChainRunnerSupport.NewSession(_env, topo, CalibChainKind.NinePoint,
                req == null ? null : req.SessionId);
            ctx.Result.Session = ctx.Session;
            ctx.Result.Plan = req == null ? null : req.Plan;

            // ═══════════ ① 规划 ═══════════
            ctx.Begin(ServiceStage.Plan);
            if (req == null || req.Plan == null)
            {
                ctx.Fail(ServiceStage.Plan, CalibError.Create(CalibFailureKind.Internal, "没有采样计划"),
                    "九点标定缺少采样计划（应由 SamplePlanBuilder 先生成，不要手工拼）。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.AddPlanIssues(req.Plan, ServiceStage.Plan);
            if (req.Plan.HasBlocker)
            {
                var sb = new System.Text.StringBuilder("采样计划存在阻断项，已拒绝开跑：");
                foreach (PlanIssue pi in req.Plan.Blockers())
                {
                    sb.AppendLine().Append("  · ").Append(pi.Message);
                }

                ctx.Fail(ServiceStage.Plan,
                    CalibError.Create(CalibFailureKind.SafetyBlocked, sb.ToString()),
                    sb.ToString());
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
                ctx.Fail(ServiceStage.Precheck, outcome.Error, "零运动校核未通过，已中止（没有验证过可达性就不发车）。");
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
            ctx.Result.Sampling = outcome;

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

            // ── 人工剔点（精确到点）──
            int excluded = ChainRunnerSupport.ApplyExclusions(ctx.Session.Samples, options.ExcludeIndices);
            if (excluded > 0)
            {
                ctx.Log("人工剔除 " + excluded + " 个采样点");
            }

            // ═══════════ ⑤ 解算 ═══════════
            ctx.Begin(ServiceStage.Solve);
            List<CalibSample> usable = NinePointSolver.CollectUsable(ctx.Session.Samples.Samples);
            if (usable.Count < options.MinUsablePoints)
            {
                ctx.Fail(ServiceStage.Solve, CalibError.Create(CalibFailureKind.SolveFailed,
                        string.Format(CultureInfo.InvariantCulture,
                            "可用采样点只有 {0} 个（至少 {1} 个）。仿射有 6 个未知量，点数不够就解不出来；"
                            + "请检查特征提取是否大面积失败、或是否有太多点被判不可达。",
                            usable.Count, options.MinUsablePoints)),
                    "点数不足，无法解算 H。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.Result.NinePoint = NinePointSolver.Solve(ctx.Session.Samples.Samples, topo);
            if (ctx.Result.NinePoint == null || !ctx.Result.NinePoint.Success)
            {
                CalibError err = ctx.Result.NinePoint == null
                    ? CalibError.Create(CalibFailureKind.SolveFailed, "解算没有返回结果")
                    : ctx.Result.NinePoint.Error;
                ctx.Fail(ServiceStage.Solve, err, "九点解算失败。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            HomMat2D h = ctx.Result.NinePoint.H;
            ctx.End(ServiceStage.Solve, WizardStepState.Ok,
                string.Format(CultureInfo.InvariantCulture, "H = {0}，RMS {1:F4} mm（{2} 个可用点）",
                    h, ctx.Result.NinePoint.RmsMm, usable.Count));

            // ═══════════ ⑥ 质量校验 ═══════════
            ctx.Begin(ServiceStage.Verify);
            Vec2? imageCenter = _env.Camera == null
                ? (Vec2?)null
                : new Vec2(_env.Camera.Width / 2.0, _env.Camera.Height / 2.0);

            ctx.Result.Diagnostics = MatrixQualityEvaluator.Evaluate(
                h, ctx.Session.Samples.Samples, topo, imageCenter, options.Thresholds);

            if (ctx.Result.Diagnostics.BadShape && !options.Thresholds.ShapeGateRelaxed)
            {
                string msg = string.Format(CultureInfo.InvariantCulture,
                    "形状非法：各向异性 σ1/σ2 = {0:F4}（容差 {1:P0}，失真量 {2:F1}%）。"
                    + "落点误差 = 失真 × 差分距离，九点 RMS 小根本发现不了，所以这里直接拦住不发车。"
                    + "先跑「直线性探针」：沿世界 +X 走 0/20/40 mm，验三点是否共线 —— "
                    + "弦弧差大说明世界侧轨迹本身就是弧线，与相机无关。"
                    + "（确实只能先用着的话，打开诊断模式 RelaxShapeGate，但报告里会留明确提醒。）",
                    ctx.Result.Diagnostics.SigmaRatio, options.Thresholds.SigmaRatioTolerance,
                    ctx.Result.Diagnostics.AnisotropyPct);

                for (int i = 0; i < ctx.Result.Diagnostics.Notes.Count; i++)
                {
                    ctx.Result.Add(ChainIssue.Warn("DIAG", ctx.Result.Diagnostics.Notes[i], ServiceStage.Verify));
                }

                ctx.Fail(ServiceStage.Verify, CalibError.Create(CalibFailureKind.QualityGate, msg), msg);
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            if (ctx.Result.Diagnostics.MirrorIsBlocker)
            {
                string msg = "固定相机下 det(A) < 0 表示镜像变换 —— 这不是固有物理，请检查轴方向定义与相机成像是否被翻转。";
                ctx.Fail(ServiceStage.Verify, CalibError.Create(CalibFailureKind.QualityGate, msg), msg);
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.End(ServiceStage.Verify, WizardStepState.Ok,
                string.Format(CultureInfo.InvariantCulture,
                    "σ1/σ2 = {0:F4}，剪切 {1:F2}%，det = {2:F6}，LOO {3:F4} mm",
                    ctx.Result.Diagnostics.SigmaRatio, ctx.Result.Diagnostics.ShearRatio * 100.0,
                    ctx.Result.Diagnostics.DetA, ctx.Result.Diagnostics.LooRmsMm));

            // ═══════════ ⑦ 反投影可视化 ═══════════
            ctx.Begin(ServiceStage.Visualize);
            ctx.Result.Reprojection = ReprojectionAnalyzer.Analyze(h, outcome.Observations,
                double.NaN, double.NaN);

            CalibDiagnostics d = ctx.Result.Diagnostics;
            d.ReprojectionRmsPx = ctx.Result.Reprojection.RmsPixelResidualPx;
            d.ReprojectionMaxPx = ctx.Result.Reprojection.MaxPixelResidualPx;
            d.ReprojectionRmsMm = ctx.Result.Reprojection.RmsWorldResidualMm;
            d.ReprojectionWithinTolerance = ctx.Result.Reprojection.AllWithin(options.ReprojectionTolerancePx);
            d.ReprojectionBiasPx = ctx.Result.Reprojection.MeanBiasPxNorm;
            d.ReprojectionRadialTrendMmPerMm = ctx.Result.Reprojection.RadialTrendSlopeMmPerMm;
            d.ReprojectionVerdict = ctx.Result.Reprojection.Verdict(options.ReprojectionTolerancePx);

            // 内外圈余量（九点口径：内圈余量 = 最近角点半径，外圈余量 = 行程上限 − 最远角点半径）
            d.InnerMarginMm = req.Plan.NearestCornerRadiusMm;
            double maxRadius = 0.0;
            for (int i = 0; i < req.Plan.Samples.Count; i++)
            {
                double r = req.Plan.Samples[i].Target.Xy.Length;
                if (r > maxRadius)
                {
                    maxRadius = r;
                }
            }

            d.OuterMarginMm = topo.MaxRadiusXy > 0.0 ? topo.MaxRadiusXy - maxRadius : double.NaN;

            ctx.End(ServiceStage.Visualize, WizardStepState.Ok,
                string.Format(CultureInfo.InvariantCulture,
                    "反投影 像素 RMS {0:F3} px / 最大 {1:F3} px，{2}",
                    d.ReprojectionRmsPx, d.ReprojectionMaxPx,
                    d.ReprojectionWithinTolerance ? "全部点在容差内（目视重合）" : "有超容差点"));

            // ═══════════ ⑧ 导出与发布 ═══════════
            ctx.Begin(ServiceStage.Export);
            var export = new CalibExport
            {
                SourceSessionId = ctx.Session.SessionId,
                Chain = CalibChainKind.NinePoint,
                StationCode = topo.StationCode,
                CameraSlotKey = topo.CameraSlotKey,
                H = h,
                HasH = true,
                RefU0 = topo.CalibU0,
                Handedness = ChainRunnerSupport.HandednessText(topo.Hand),
                CameraMount = ChainRunnerSupport.MountText(topo.CameraMount),
                Diagnostics = d,
                ImageSize = _env.Camera == null ? null : new int[] { _env.Camera.Width, _env.Camera.Height },
                FramesDir = ctx.Result.ExportDir,
                ProducedBy = options.ProducedBy
            };

            ChainRunnerSupport.ExportAndPublish(ctx, options, _env, export);
            ctx.End(ServiceStage.Export, WizardStepState.Ok,
                string.IsNullOrEmpty(ctx.Result.ExportDir) ? "未落文件" : ctx.Result.ExportDir);

            ChainRunnerSupport.Finish(ctx, true);
            ctx.Log(ctx.Result.Summary());
            return ctx.Result;
        }
    }
}
