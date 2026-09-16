using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;
using VisualCalibTool.Imaging;
using VisualCalibTool.Services;

namespace VisualCalibTool.Simulation
{
    /// <summary>仿真闭环的运行参数（自检默认值即可直接跑）。</summary>
    public sealed class SimulationHarnessOptions
    {
        public double StepX = 10.0;
        public double StepY = 10.0;

        /// <summary>旋转中心链的绝对角序列（度）。★ 用对称角，理由见 <see cref="CalibSimulationHarness"/> 的说明。</summary>
        public double[] RotationAngles = new double[]
        {
            -120.0, -90.0, -60.0, -30.0, 0.0, 30.0, 60.0, 90.0, 120.0
        };

        /// <summary>偏心链的绝对角序列（度）。</summary>
        public double[] TipAngles = new double[] { -45.0, 0.0, 45.0 };

        /// <summary>吸嘴偏心 e 的真值（法兰坐标系，u = U0 时，mm）。</summary>
        public Vec2 TipEcc = new Vec2(8.0, -3.0);

        /// <summary>合成图的灰度噪声幅度（0 = 完全干净）。</summary>
        public double GrayNoise = 6.0;

        /// <summary>采样帧留档（复盘物证）。默认开。</summary>
        public bool ArchiveFrames = true;

        public bool CaptureTrace = true;

        /// <summary>产物根目录。null / 空 = 临时目录。</summary>
        public string StoreRoot;

        /// <summary>是否顺带打印逐点像素 vs 真值（排障时开）。</summary>
        public bool VerbosePerPoint;

        /// <summary>失败时保留产物目录（默认总是保留，方便人去看留下的帧）。</summary>
        public bool KeepArtifacts = true;
    }

    /// <summary>三链仿真闭环的一次完整运行（产物 + 中间结果，供界面/报告复用）。</summary>
    public sealed class SimulationRun
    {
        public SimulatedWorld World;
        public CalibTopology Topology;

        public SimulatedEnvironment Env;
        public SyntheticFrameCamera Camera;
        public SamplingOrchestrator Sampler;

        /// <summary>
        /// ★ 仿真/独立模式的发布器（与 <c>Env.Publisher</c> 是同一个实例）。
        /// 断言"发布这条路径真的被走到过"必须能拿到它 —— 只看 <c>Result.Published</c>
        /// 是分不清"真发布了"和"压根没调发布"的。
        /// </summary>
        public SimulatedCalibrationPublisher Publisher;

        public string StoreRoot;

        /// <summary>H 链（九点）结果。</summary>
        public ChainRunResult NinePoint;

        /// <summary>O 链（旋转中心）结果。</summary>
        public ChainRunResult RotationCenter;

        /// <summary>e 链（吸嘴偏心，像面随法兰滚转 = 眼在手）结果。</summary>
        public ChainRunResult ToolOffset;

        /// <summary>e 链（像面不随法兰滚转 = 非随动安装）结果。</summary>
        public ChainRunResult ToolOffsetFixedCamera;

        /// <summary>错误口径探针：把"已归一的量"再转一次的结果。</summary>
        public Vec2 WrongRegimeEcc;

        /// <summary>错误口径探针的解析预测值 (1/n)Σ R(−Δu)·e_true。</summary>
        public Vec2 WrongRegimePredicted;

        /// <summary>向导编排（人话目标 → 链序列）的端到端结果，供界面复用。</summary>
        public WizardRunOutcome WizardOutcome;

        /// <summary>逐点明细（人话文本，直接写进报告）。</summary>
        public readonly List<string> Transcript = new List<string>();
    }

    /// <summary>
    /// ★ 仿真"对针"提供者：真机上这一步是操作员把吸嘴尖挪到压住基准特征（现实真值，不是几何能算的），
    /// 仿真里我们手里有真值，于是直接按解析式反解法兰该停到哪：
    ///
    ///     tipWorld = P + R(u − U0)·e_true  ⇒  P = Mark − R(u − U0)·e_true
    ///
    /// 接口语义与 <see cref="ManualTipApproachProvider"/> 完全一致（返回"法兰该停到哪"），
    /// 只是答案来源不同 —— 这正是把对针做成接缝的意义。
    /// </summary>
    public sealed class SimulatedTipApproachProvider : ITipApproachProvider
    {
        private readonly Vec2 _markWorld;
        private readonly Vec2 _tipEcc;
        private readonly double _u0Deg;

        public SimulatedTipApproachProvider(Vec2 markWorld, Vec2 tipEcc, double u0Deg)
        {
            _markWorld = markWorld;
            _tipEcc = tipEcc;
            _u0Deg = u0Deg;
        }

        public bool MovesAutomatically
        {
            get { return true; }
        }

        public bool TryGetApproachXy(int stepIndex, double absoluteU, CalibTopology topology,
            out Vec2 xy, out string note)
        {
            xy = _markWorld - _tipEcc.Rotate(absoluteU - _u0Deg);
            note = "仿真：按 e 真值反解出的对针机位";
            return xy.IsFinite;
        }
    }

    /// <summary>
    /// ★★ H / O / e 三条链的<b>端到端仿真闭环</b>（0 硬件，但每一步都是真的）。
    ///
    /// 与 <see cref="CalibSelfCheck"/> 的分工必须说清楚，否则很容易误以为重复：
    ///   · <c>CalibSelfCheck</c> 验的是<b>纯代数</b>：喂"理想投影造出来的 (像素, 世界)"给解算器，
    ///     把相机模型排除在外 —— 它回答"解算器对不对"。
    ///   · 本类验的是<b>整条链</b>：合成真图 → 真 HALCON 提取 → 真解算 → 真导出。走位、软触发、
    ///     留档、预测 ROI、门禁、产物落盘全部真实执行 —— 它回答"这条链能不能通"。
    ///
    /// 两者不可互相替代：代数自检再绿，也发现不了"参考半径记错 → 后面八点全被当伪特征毙掉"
    /// 这类链路级故障；而端到端跑通也说明不了解算器内部的判据是否正确。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// ★ 偏心链为什么必须跑<b>两种像面口径</b>（这段是仿真抓出真问题后补的）
    /// ══════════════════════════════════════════════════════════════════
    /// 眼在手（EIH）下相机与吸嘴都刚性固定在法兰上，所以"吸嘴尖压住基准特征"这个状态
    /// <b>相对法兰是刚性的</b> ⇒ 无论转到哪个角度，拍到的画面<b>完全一样</b>（Mark 的像素恒定）。
    /// 于是 H(p_tip) = O − e（与角度无关），即 O − H(p_k) <b>本来就已是基准角口径</b>。
    ///
    /// 这条链第一次跑的时候就是在这里翻车的：<c>SolveNormalized</c> 无条件再乘一个 R(U0 − U_k)，
    /// 于是报告出来的 e 变成 (1/n)Σ R(−Δu_k)·e_true —— |e| 从 8.544 mm 缩到 6.875 mm，
    /// 方向偏差被凭空造出 45°，最后以"这不像同一根吸嘴的对针结果"的名义把完美标定拦下来。
    /// （解析预测 (6.4379, −2.4142) 与实测 (6.4375, −2.4140) 逐位吻合，所以这不是"调参能调好"的偏差，
    ///   而是口径错误。）
    ///
    /// 修法不是"把容差放大"，而是把物理前提显式化：
    ///   · 像面随法兰滚转（眼在手，<c>CalibTopology.CameraRollsWithFlange = true</c>）
    ///     ⇒ O − H(p_k) 已是基准角口径 ⇒ <b>不再旋转</b>；
    ///   · 像面不随法兰滚转（固定相机 / 非随动安装）⇒ 观测带 R(ΔU) ⇒ <b>必须</b>转回 U0。
    /// 所以这里两种口径各跑一遍 e 链，各自断言 e ≈ e_true；
    /// 另加一个探针，把"口径选错"的代价量化出来（对称角下它是恒等 —— 这正是当年没发现它的原因）。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static class CalibSimulationHarness
    {
        /// <summary>跑完整三链闭环，并把结论写进 <paramref name="report"/>。</summary>
        public static SimulationRun RunAll(SelfCheckReport report, SimulationHarnessOptions opt)
        {
            if (report == null)
            {
                throw new ArgumentNullException("report");
            }

            if (opt == null)
            {
                opt = new SimulationHarnessOptions();
            }

            var run = new SimulationRun();
            var log = new SimpleCalibLog();

            try
            {
                Build(run, opt, log);
            }
            catch (Exception ex)
            {
                report.Check(false, "仿真闭环（装配）",
                    "装配仿真环境失败：" + ex.GetType().Name + "：" + ex.Message);
                return run;
            }

            try
            {
                RunNinePoint(report, run, opt);
            }
            catch (Exception ex)
            {
                report.Check(false, "H 链（九点）执行",
                    "抛出异常：" + ex.GetType().Name + "：" + ex.Message);
            }

            try
            {
                RunRotationCenter(report, run, opt);
            }
            catch (Exception ex)
            {
                report.Check(false, "O 链（旋转中心）执行",
                    "抛出异常：" + ex.GetType().Name + "：" + ex.Message);
            }

            try
            {
                RunToolOffset(report, run, opt);
            }
            catch (Exception ex)
            {
                report.Check(false, "e 链（吸嘴偏心）执行",
                    "抛出异常：" + ex.GetType().Name + "：" + ex.Message);
            }

            try
            {
                ProbeNormalizationPremise(report, run, opt);
            }
            catch (Exception ex)
            {
                report.Check(false, "归一化前提探针",
                    "抛出异常：" + ex.GetType().Name + "：" + ex.Message);
            }

            try
            {
                RunWizardGoals(report, run, opt);
            }
            catch (Exception ex)
            {
                report.Check(false, "向导编排（目标 → 链序列）",
                    "抛出异常：" + ex.GetType().Name + "：" + ex.Message);
            }

            try
            {
                VerifyArtifacts(report, run, opt);
            }
            catch (Exception ex)
            {
                report.Check(false, "产物落盘",
                    "抛出异常：" + ex.GetType().Name + "：" + ex.Message);
            }

            if (log.Lines.Count > 0)
            {
                // 只把最后若干行带进报告，避免把逐点诊断淹掉
                IList<string> lines = log.Lines;
                int from = Math.Max(0, lines.Count - 10);
                for (int i = from; i < lines.Count; i++)
                {
                    run.Transcript.Add("[log] " + lines[i]);
                }
            }

            return run;
        }

        // ══════════════════════════════════════════════════════════════
        // 装配
        // ══════════════════════════════════════════════════════════════

        private static void Build(SimulationRun run, SimulationHarnessOptions opt, ICalibLog log)
        {
            // ★ 装配只有一条代码路径：复用 <see cref="SimulatedRig"/>。
            //   自检与界面各装一套的话，必然出现"离线全绿、界面跑不通"这种最难查的偏差。
            SimulatedRig rig = SimulatedRig.Build(new SimulatedRigOptions
            {
                StepX = opt.StepX,
                StepY = opt.StepY,
                TipEcc = opt.TipEcc,
                GrayNoise = opt.GrayNoise,
                GrabLatencyMs = 0,      // 自检不需要模拟曝光延时
                RenderTip = false,      // 九点/旋转链必须关：工具尖会变成"更深的小圆"干扰提取
                CameraRotatesWithFlange = true,
                U0Deg = 0.0,
                StoreRoot = opt.StoreRoot
            }, log);

            run.World = rig.World;
            run.Topology = rig.Topology;
            run.Env = rig.Env;
            run.Camera = rig.Camera;
            run.Sampler = rig.Sampler;
            run.StoreRoot = rig.StoreRoot;
            run.Publisher = rig.Publisher;
        }

        private static MarkSpec MakeMarkSpec(SimulatedWorld world)
        {
            return new MarkSpec
            {
                Kind = FeatureKind.CircleMark,
                ExpectedRadiusPx = world.MarkRadiusPx
            };
        }

        // ══════════════════════════════════════════════════════════════
        // ① H 链
        // ══════════════════════════════════════════════════════════════

        private static void RunNinePoint(SelfCheckReport report, SimulationRun run, SimulationHarnessOptions opt)
        {
            SimulatedWorld world = run.World;

            var pbr = new PlanBuildRequest
            {
                Chain = CalibChainKind.NinePoint,
                Topology = run.Topology,
                ImageShortSidePx = Math.Min(run.Camera.Width, run.Camera.Height),
                ImageWidthPx = run.Camera.Width,
                ImageHeightPx = run.Camera.Height,
                MmPerPixel = world.MmPerPixel
            };

            SamplePlan plan = SamplePlanBuilder.Build(pbr);

            var req = new SamplingRequest
            {
                Chain = CalibChainKind.NinePoint,
                Plan = plan,
                Mark = MakeMarkSpec(world),
                SessionId = "SIM-NinePoint",
                ArchiveFrames = opt.ArchiveFrames,
                CaptureTrace = opt.CaptureTrace,
                LiftToSafeZ = true,
                LiftBetweenPoints = false,
                PrecheckEnabled = true,
                SkipUnreachable = true
            };

            var options = new ChainRunOptions
            {
                ExportFiles = true,
                PublishToHost = true,
                ReprojectionTolerancePx = 1.0
            };

            run.NinePoint = new NinePointRunner(run.Env, run.Sampler).Run(req, options);

            ChainRunResult r = run.NinePoint;
            RecordSampleDiagnostics(run, r, "H");

            if (r == null || !r.Success)
            {
                report.Check(false, "H 链（九点）跑通",
                    r == null ? "没有返回结果" : r.Summary() + " | " + FirstBlocker(r));
                return;
            }

            report.Check(true, "H 链（九点）跑通（合成图 → 真提取 → 真解算 → 真导出）", r.Session.SessionId);

            if (opt.VerbosePerPoint)
            {
                DumpPerPoint(run, "H");
            }

            // ① 九点全部可用
            int usable = r.Sampling == null ? 0 : r.Sampling.UsableCount;
            report.Check(usable == 9, "九点采样 9/9 点被成功提取",
                string.Format(CultureInfo.InvariantCulture, "可用 {0}/9；提取失败 {1} 次，取图 {2} 次",
                    usable, r.Sampling == null ? -1 : r.Sampling.ExtractFailed,
                    r.Sampling == null ? -1 : r.Sampling.GrabCount));

            // ② 解出的 H ≈ 解析 H
            HomMat2D expected = world.ExpectedH();
            double maxDiff = MaxElementDiff(r.NinePoint.H, expected);
            report.Check(maxDiff < 0.02, "★ 解出的 H ≈ 模型解析 H",
                string.Format(CultureInfo.InvariantCulture,
                    "最大逐元素偏差 {0:E3} mm（尺度项约 {1:F4} mm/px，等价于像素误差 {2:F4} px）",
                    maxDiff, Math.Abs(expected.H11), maxDiff / Math.Abs(expected.H11)));

            // ③ 形状
            CalibDiagnostics d = r.Diagnostics;
            report.Check(d != null && Math.Abs(d.SigmaRatio - 1.0) < 1e-3,
                "★ 形状 σ1/σ2 ≈ 1.000（合成图无几何失真 ⇒ 解出来必须也是 1）",
                d == null ? "没有诊断" : string.Format(CultureInfo.InvariantCulture,
                    "σ1/σ2 = {0:F6}，两轴当量差 {1:F4}%，剪切 {2:F6}",
                    d.SigmaRatio, d.AxisScaleDeviationPct, d.ShearRatio));

            report.Check(d != null && !d.HasBlocker, "无阻断项（含 EyeInHand 下 det<0 不拦截的语义）",
                d == null ? "没有诊断" : string.Format(CultureInfo.InvariantCulture,
                    "det = {0:F6}，镜像 {1}，阻断 {2}", d.DetA, d.MirrorDetected, d.HasBlocker));

            // ④ 反投影：AR 目视判据
            report.Check(r.Reprojection != null && r.Reprojection.RmsPixelResidualPx < 1.0,
                "★ 反投影（AR）：全部点回到原像素且落在 1 px 容差内",
                r.Reprojection == null ? "无反投影报告" : string.Format(CultureInfo.InvariantCulture,
                    "像素 RMS {0:F4} px / 最大 {1:F4} px；世界 RMS {2:F4} mm；判读：{3}",
                    r.Reprojection.RmsPixelResidualPx, r.Reprojection.MaxPixelResidualPx,
                    r.Reprojection.RmsWorldResidualMm, r.Reprojection.Verdict(1.0)));

            // ⑤ 残差
            report.Check(r.NinePoint.RmsMm < 0.05, "世界域残差 RMS < 0.05 mm（毫米级可信）",
                string.Format(CultureInfo.InvariantCulture, "RMS = {0:F5} mm，LOO = {1:F5} mm",
                    r.NinePoint.RmsMm, d == null ? double.NaN : d.LooRmsMm));

            // ⑥ 内圈余量
            report.Check(r.NinePoint.NearestCornerRadiusMm > 0.0, "最近角点半径 > 0（九点没有越内圈）",
                string.Format(CultureInfo.InvariantCulture, "最近角点半径 {0:F2} mm", r.NinePoint.NearestCornerRadiusMm));

            // ⑦ .tup 可被主项目读回
            CheckTupRoundTrip(report, r, "H 链");
        }

        // ══════════════════════════════════════════════════════════════
        // ② O 链
        // ══════════════════════════════════════════════════════════════

        private static void RunRotationCenter(SelfCheckReport report, SimulationRun run, SimulationHarnessOptions opt)
        {
            if (run.NinePoint == null || !run.NinePoint.Success || run.NinePoint.NinePoint == null)
            {
                report.Check(false, "O 链（旋转中心）依赖 H",
                    "H 链没跑通，旋转中心无法进行（这正是「必须先有 H」这条前置的现场体现）。");
                return;
            }

            HomMat2D h = run.NinePoint.NinePoint.H;
            double[] angles = opt.RotationAngles;

            SamplePlan plan = RotationCenterRunner.BuildPlan(
                run.Topology, angles, Math.Min(run.Camera.Width, run.Camera.Height), run.World.MmPerPixel);

            var req = new SamplingRequest
            {
                Chain = CalibChainKind.RotationCenter,
                Plan = plan,
                Mark = MakeMarkSpec(run.World),
                SessionId = "SIM-RotationCenter",
                ArchiveFrames = opt.ArchiveFrames,
                CaptureTrace = opt.CaptureTrace,
                LiftToSafeZ = true,
                LiftBetweenPoints = true,   // ★ 转 U 必须抬刀：侧向摆动是撞机的经典成因
                PrecheckEnabled = true,
                SkipUnreachable = true,
                AbsoluteAngles = new List<double>(angles)
            };

            var options = new ChainRunOptions { ExportFiles = true, PublishToHost = true };

            run.RotationCenter = new RotationCenterRunner(run.Env, run.Sampler).Run(req, h, options);

            ChainRunResult r = run.RotationCenter;
            RecordSampleDiagnostics(run, r, "O");

            if (r == null || !r.Success || r.RotationCenter == null)
            {
                report.Check(false, "O 链（旋转中心）跑通",
                    r == null ? "没有返回结果" : r.Summary() + " | " + FirstBlocker(r));
                return;
            }

            report.Check(true, "O 链（旋转中心）跑通", r.Session.SessionId);

            if (opt.VerbosePerPoint)
            {
                DumpPerPoint(run, "O");
            }

            RotationCenterResult rc = r.RotationCenter;
            Vec2 expected = run.World.ExpectedRotationCenterWorld(run.Topology.BasePosXY);
            double err = (rc.Center - expected).Length;

            report.Check(err < 0.05, "★ O 的数值 = 基准特征的世界位置（模型解析真值）",
                string.Format(CultureInfo.InvariantCulture,
                    "O = ({0:F4}, {1:F4})，真值 = ({2:F4}, {3:F4})，偏差 {4:F4} mm",
                    rc.Center.X, rc.Center.Y, expected.X, expected.Y, err));

            report.Check(rc.FittedAtMapped, "定圆在映射域完成（FittedAtMapped = true）",
                "FittedAtMapped = " + rc.FittedAtMapped);

            report.Check(rc.MappedPointCount >= 6, "参与定圆的映射点 ≥ 6",
                string.Format(CultureInfo.InvariantCulture, "映射点 {0} 个，跨度 {1:F1}°，残差 RMS {2:F5} mm / 最大 {3:F5} mm",
                    rc.MappedPointCount, rc.AngularSpanDeg, rc.RmsMm, rc.ResidualMaxMm));

            report.Check(rc.RmsMm < 0.05, "映射域圆残差 RMS < 0.05 mm",
                string.Format(CultureInfo.InvariantCulture, "RMS = {0:F5} mm", rc.RmsMm));

            // 半径必须被排除在产物之外（只是诊断量）
            report.Check(r.Export != null && r.Export.HasRotCenter && RadiusNotShipped(r.Export),
                "★ 产物只带走圆心 O，拟合半径没有写进产物（半径会被延伸杆/偏心污染）",
                string.Format(CultureInfo.InvariantCulture,
                    "HasRotCenter = {0}，拟合半径 {1:F4} mm（|Mark−法兰轴| = {2:F4} mm）只留在诊断文字里",
                    r.Export != null && r.Export.HasRotCenter, rc.FittedRadiusMm,
                    (run.World.MarkWorldXy - run.Topology.BasePosXY).Length));

            // 像素域 vs 映射域：本配置是各向同性（mm/px 两轴相同）⇒ 差应≈0；
            // 一旦有人把 mm/px 改成两轴不同，这个数字就会站起来 —— 那时它是"不能在像素域定圆"的现场证据。
            report.Check(!double.IsNaN(rc.PixelVsMappedCenterMm),
                "像素域定圆的对照已如实报出（各向同性配置下应为 ≈0）",
                string.Format(CultureInfo.InvariantCulture,
                    "像素域圆心映射回世界后与映射域圆心差 {0:F5} mm", rc.PixelVsMappedCenterMm));
        }

        // ══════════════════════════════════════════════════════════════
        // ③ e 链
        // ══════════════════════════════════════════════════════════════

        private static void RunToolOffset(SelfCheckReport report, SimulationRun run, SimulationHarnessOptions opt)
        {
            if (run.NinePoint == null || !run.NinePoint.Success || run.NinePoint.NinePoint == null
                || run.RotationCenter == null || !run.RotationCenter.Success || run.RotationCenter.RotationCenter == null)
            {
                report.Check(false, "e 链（吸嘴偏心）依赖 H + O",
                    "前置链没跑通 —— 这正是「e = O − H(p_tip)，H 与 O 缺一不可」的现场体现。");
                return;
            }

            HomMat2D h = run.NinePoint.NinePoint.H;
            Vec2 oSolved = run.RotationCenter.RotationCenter.Center;

            // ── (a) 像面随法兰滚转（眼在手，默认物理）──
            run.World.CameraRotatesWithFlange = true;
            run.ToolOffset = RunOneToolOffsetChain(
                run, opt, h, oSolved, opt.TipAngles, new List<double>(opt.TipAngles), "SIM-ToolOffset",
                new SimulatedTipApproachProvider(run.World.MarkWorldXy, opt.TipEcc, run.Topology.CalibU0));

            AssessToolOffset(run, report, run.ToolOffset, opt, oSolved, "e/roll",
                "e 链（像面随法兰滚转 / 眼在手）");

            // ── (b) 像面不随法兰滚转（非随动安装 / 固定相机口径）──
            //     ★ 同一份数据、同一套算法，只因物理前提不同，"要不要转回 U0"就相反。
            //       两边都必须给出 e 真值 —— 这才叫口径被真正区分开了。
            run.World.CameraRotatesWithFlange = false;
            run.Topology.CameraRollsWithFlange = false;
            run.World.RenderTip = true;

            double[] skewAngles = new double[] { -30.0, 0.0, 45.0 };   // ★ 故意非对称
            run.ToolOffsetFixedCamera = RunOneToolOffsetChain(
                run, opt, h, oSolved, skewAngles, new List<double>(skewAngles), "SIM-ToolOffset-NoRoll",
                new SimulatedTipApproachProvider(run.World.MarkWorldXy, opt.TipEcc, run.Topology.CalibU0));

            AssessToolOffset(run, report, run.ToolOffsetFixedCamera, opt, oSolved, "e/noroll",
                "e 链（像面不随法兰滚转 / 非随动安装，非对称角 −30/0/+45）");

            // 收尾：把拓扑恢复成眼在手默认值，避免后续其它断言被带偏
            run.Topology.CameraRollsWithFlange = true;
            run.World.CameraRotatesWithFlange = true;
        }

        private static ChainRunResult RunOneToolOffsetChain(SimulationRun run, SimulationHarnessOptions opt,
            HomMat2D h, Vec2 oSolved, double[] angles, List<double> requestedAngles, string sessionId,
            ITipApproachProvider approach)
        {
            // ★ 偏心链必须"看得见工具尖"：合成图里把尖画出来（半径 6 px vs Mark 18 px）
            run.World.RenderTip = true;

            var req = new SamplingRequest
            {
                Chain = CalibChainKind.ToolOffset,
                Mark = MakeMarkSpec(run.World),
                SessionId = sessionId,
                ArchiveFrames = opt.ArchiveFrames,
                CaptureTrace = opt.CaptureTrace,
                LiftToSafeZ = true,
                LiftBetweenPoints = true,
                PrecheckEnabled = true,
                SkipUnreachable = true,
                AbsoluteAngles = requestedAngles
            };

            var options = new ChainRunOptions { ExportFiles = true, PublishToHost = true };

            return new ToolOffsetRunner(run.Env, run.Sampler).Run(
                req, angles, h, oSolved, approach, options);
        }

        private static void AssessToolOffset(SimulationRun run, SelfCheckReport report, ChainRunResult r,
            SimulationHarnessOptions opt, Vec2 oSolved, string tag, string title)
        {
            RecordSampleDiagnostics(run, r, tag);

            if (r == null || !r.Success || r.ToolOffset == null)
            {
                report.Check(false, title + " 跑通",
                    r == null ? "没有返回结果" : r.Summary() + " | " + FirstBlocker(r));
                return;
            }

            report.Check(true, title + " 跑通（对针 → 提取 → e = O − H(p_tip) → 导出）", r.Session.SessionId);

            if (opt.VerbosePerPoint)
            {
                DumpOne(run, r, tag);
            }

            ToolOffsetResult e = r.ToolOffset;

            // ★ 把"上一链 O 的解算误差"扣掉，才能单独考察 e 链本身
            Vec2 oError = oSolved - run.World.MarkWorldXy;
            Vec2 residual = e.Ecc - opt.TipEcc - oError;
            report.Check(residual.Length < 0.05,
                title + "：★ e 与真值一致（已扣掉 O 的解算误差）",
                string.Format(CultureInfo.InvariantCulture,
                    "e = ({0:F4}, {1:F4})，真值 = ({2:F4}, {3:F4})，|e| {4:F5} mm（真值 {5:F5} mm）；"
                    + "O 解算误差 ({6:F4}, {7:F4}) 已扣除，剩余 {8:F5} mm",
                    e.Ecc.X, e.Ecc.Y, opt.TipEcc.X, opt.TipEcc.Y, e.EccMagnitudeMm, opt.TipEcc.Length,
                    oError.X, oError.Y, residual.Length));

            report.Check(e.TipScatterMm < 0.02, title + "：对针离散度 ≈ 0",
                string.Format(CultureInfo.InvariantCulture,
                    "对针 {0} 次，离散 {1:F6} mm，尖像素 ({2:F2}, {3:F2})",
                    e.TipSampleCount, e.TipScatterMm, e.TipPixel.X, e.TipPixel.Y));

            report.Check(r.Export != null && r.Export.HasEcc && r.Export.HasTipWorld
                         && (r.Export.TipWorld - (oSolved - e.Ecc)).Length < 1e-9,
                title + "：产物里的 e 与 TipWorld 自洽（TipWorld = O − e）",
                string.Format(CultureInfo.InvariantCulture,
                    "e = ({0:F4}, {1:F4})，H(p_tip) = ({2:F4}, {3:F4})，O = ({4:F4}, {5:F4})",
                    e.Ecc.X, e.Ecc.Y, e.TipWorld.X, e.TipWorld.Y, e.RotCenterWorld.X, e.RotCenterWorld.Y));
        }

        // ══════════════════════════════════════════════════════════════
        // ④ 向导编排（把"人话目标"翻译成"跑哪几条链"）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// ★ 向导编排器的端到端验证。
        ///
        /// 为什么要专门验它：向导是用户唯一会碰的入口，而它的正确性有两块完全不同的内容 ——
        ///   ① <b>前置关系</b>：e 依赖 O、O 依赖 H。错了的表现是"界面说能跑，一点就报错"。
        ///   ② <b>已完成的复用</b>：点"吸嘴偏了多少"时不该把 H、O 重标一遍
        ///      （真机上重标一次就是好几分钟 + 一次撞机风险）。
        ///
        /// 所以这里用<b>同一个 coordinator</b> 连续跑三个目标，断言第二条、第三条只各跑一条链；
        /// 再用一个<b>全新的 coordinator</b> 跑"眼在手全链"，断言它老老实实跑满三条。
        ///
        /// 另外钉住两件"诚实性"：
        ///   · 没实现的链（内参/畸变）必须<b>如实失败</b>，不许返回假成功；
        ///   · 没有对针方式时偏心链必须<b>在开跑前</b>就被拦下并说明原因，而不是跑到一半才崩。
        /// </summary>
        private static void RunWizardGoals(SelfCheckReport report, SimulationRun run, SimulationHarnessOptions opt)
        {
            if (run.NinePoint == null || !run.NinePoint.Success || run.NinePoint.NinePoint == null)
            {
                report.Check(false, "向导编排（前置 H）", "H 链没跑通，向导编排无法验证。");
                return;
            }

            // ★ RunToolOffset 会把像面口径改成 false 以跑第二遍 e 链，这里必须还原，
            //   否则后面所有断言都是在另一个物理前提下跑的（这正是"共享可变状态"的典型陷阱）。
            run.World.CameraRotatesWithFlange = true;
            run.Topology.CameraRollsWithFlange = true;

            SimulatedWorld world = run.World;
            HomMat2D expectedH = world.ExpectedH();
            Vec2 expectedO = world.ExpectedRotationCenterWorld(world.BaseFlangeXy);

            var approach = new SimulatedTipApproachProvider(world.MarkWorldXy, opt.TipEcc, world.U0Deg);
            var wopt = new WizardRunOptions
            {
                ArchiveFrames = false,
                CaptureTrace = false,
                ExportFiles = true,
                PublishToHost = true,
                SessionPrefix = "SIM-WIZ",
                TipAngles = opt.TipAngles
            };

            var mark = new MarkSpec { Kind = FeatureKind.CircleMark, ExpectedRadiusPx = world.MarkRadiusPx };

            // ── 纯函数：目标 → 链序列（不依赖环境，先钉住映射本身）──
            report.Check(
                WizardCoordinator.PlanChains(WizardGoal.CameraAccuracy).Count == 1
                && WizardCoordinator.PlanChains(WizardGoal.NozzleRotationCenter).Count == 2
                && WizardCoordinator.PlanChains(WizardGoal.NozzleOffset).Count == 3
                && WizardCoordinator.PlanChains(WizardGoal.HandEye).Count == 3,
                "★ 目标 → 链序列的映射固定（看得准 1 条 / 转到哪 2 条 / 偏多少 3 条）",
                "CameraAccuracy=1，NozzleRotationCenter=2，NozzleOffset=3，HandEye=3 —— "
                + "前置链在前、目标链在最后，界面据此画「共 N 步」");

            var co = new WizardCoordinator(run.Env, run.Sampler, approach);
            var progress = new List<RunProgress>();
            Action<RunProgress> collect = p =>
            {
                if (p != null)
                {
                    progress.Add(p);
                }
            };

            // ── ① 看得准：只跑 1 条 ──
            WizardRunOutcome a = co.Run(WizardGoal.CameraAccuracy, mark, wopt, collect);
            report.Check(a.Success && a.Chains.Count == 1 && a.PrimaryChain == CalibChainKind.NinePoint,
                "① 向导「相机看得准不准」→ 只跑 1 条链",
                a.Summary() + "；链 = " + string.Join(" → ", a.ChainLabels.ToArray()));

            if (a.Success)
            {
                double maxDiff = MaxElementDiff(co.HandEye.Value, expectedH);
                report.Check(maxDiff < 0.02,
                    "① 向导跑出的 H 与解析 H 一致（结果真的落进了 Vault）",
                    string.Format(CultureInfo.InvariantCulture, "最大逐元素偏差 {0:E3} mm", maxDiff));
            }
            else
            {
                report.Check(false, "① 向导跑出的 H 与解析 H 一致（结果真的落进了 Vault）", a.Message);
            }

            // ── ② 转到哪：H 复用 → 只跑 1 条 ──
            WizardRunOutcome b = co.Run(WizardGoal.NozzleRotationCenter, mark, wopt, collect);
            report.Check(b.Success && b.Chains.Count == 1 && b.SkippedLabels.Count == 1
                         && b.Chains[0].Chain == CalibChainKind.RotationCenter,
                "★ ② 向导「吸嘴转到哪」→ 复用已有的 H，只跑 1 条链",
                b.Summary() + "；跳过 = " + string.Join(" / ", b.SkippedLabels.ToArray()));

            if (b.Success && b.Chains.Count > 0 && b.Chains[0].RotationCenter != null)
            {
                Vec2 o = b.Chains[0].RotationCenter.Center;
                report.Check((o - expectedO).Length < 0.05,
                    "② 向导跑出的 O 与模型解析真值一致",
                    string.Format(CultureInfo.InvariantCulture,
                        "O = ({0:F4}, {1:F4})，真值 = ({2:F4}, {3:F4})，偏差 {4:E2} mm",
                        o.X, o.Y, expectedO.X, expectedO.Y, (o - expectedO).Length));
            }
            else
            {
                report.Check(false, "② 向导跑出的 O 与模型解析真值一致", b.Message);
            }

            // ── ③ 偏多少：H + O 都复用 → 只跑 1 条 ──
            WizardRunOutcome c = co.Run(WizardGoal.NozzleOffset, mark, wopt, collect);
            report.Check(c.Success && c.Chains.Count == 1 && c.SkippedLabels.Count == 2
                         && c.Chains[0].Chain == CalibChainKind.ToolOffset,
                "★ ③ 向导「吸嘴偏了多少」→ 复用已有的 H 与 O，只跑 1 条链",
                c.Summary() + "；跳过 = " + string.Join(" / ", c.SkippedLabels.ToArray()));

            if (c.Success && c.Chains.Count > 0 && c.Chains[0].ToolOffset != null)
            {
                ToolOffsetResult e = c.Chains[0].ToolOffset;

                // e = O − H(p_tip)，而 H(p_tip) = O − e_true ⇒ 真值口径下 e 应当就是 e_true。
                // 但 O 本身有解算误差，所以用"本次实际用的 O"扣掉它，剩下的才是真正的残差。
                Vec2 oUsed = co.RotationCenterWorld ?? expectedO;
                Vec2 eTrue = opt.TipEcc;
                double residual = (e.Ecc - eTrue).Length;
                report.Check(residual < 0.02,
                    "③ 向导跑出的 e 与真值一致（已用本次实际解出的 O 扣掉其误差）",
                    string.Format(CultureInfo.InvariantCulture,
                        "e = ({0:F4}, {1:F4})，|e| {2:F6} mm，真值 {3:F6} mm，剩余 {4:E2} mm；本次 O = ({5:F4}, {6:F4})",
                        e.Ecc.X, e.Ecc.Y, e.EccMagnitudeMm, opt.TipEcc.Length, residual,
                        oUsed.X, oUsed.Y));
            }
            else
            {
                report.Check(false, "③ 向导跑出的 e 与真值一致（已扣掉 O 的解算误差）", c.Message);
            }

            report.Check(co.HandEyeKnown && co.RotationCenterKnown && co.ToolOffsetKnown,
                "★ 三次运行后 Vault 三项齐备（界面顶部的「已就绪」徽章据此显示）",
                co.VaultText());

            // ── ④ 全新会话跑"眼在手全链"：必须老实跑满 3 条 ──
            var co2 = new WizardCoordinator(run.Env, run.Sampler, approach);
            progress.Clear();
            WizardRunOutcome d = co2.Run(WizardGoal.HandEye, mark, wopt, collect);
            report.Check(d.Success && d.Chains.Count == 3 && d.SkippedLabels.Count == 0
                         && co2.HandEyeKnown && co2.RotationCenterKnown && co2.ToolOffsetKnown,
                "★ ④ 全新会话跑「相机看到的 = 机械手在哪」→ 三条链按序跑满，不跳步",
                d.Summary() + "；链 = " + string.Join(" → ", d.ChainLabels.ToArray()));

            // ── ⑤ 进度：单调不减、终值 = 1、步骤名带链名 ──
            bool monotone = true;
            double prev = -1.0;
            for (int i = 0; i < progress.Count; i++)
            {
                if (progress[i].Fraction < prev - 1e-12)
                {
                    monotone = false;
                    break;
                }

                prev = progress[i].Fraction;
            }

            bool labeled = progress.Count > 0 && progress[0].StepTitle.Contains("·");
            double last = progress.Count == 0 ? 0.0 : progress[progress.Count - 1].Fraction;

            report.Check(monotone && labeled && progress.Count >= 3 * ServiceStage.Total && Math.Abs(last - 1.0) < 1e-9,
                "★ 进度回调：整次向导的进度单调不减、终值 1.0、步骤名带链名前缀",
                string.Format(CultureInfo.InvariantCulture,
                    "回调 {0} 次（≥ 3 链 × 8 步 = {1}），末值 {2:F4}，首条标题「{3}」",
                    progress.Count, 3 * ServiceStage.Total, last,
                    progress.Count == 0 ? "-" : progress[0].StepTitle));

            // ── ⑤b 采样期的"成功 / 失败"必须随进度一起报出来（"卡哪说哪"的数据源）──
            int bestTotal = 0;
            int bestOk = -1;
            for (int i = 0; i < progress.Count; i++)
            {
                // ★ 用 >=（取最后一条）：预检阶段也会以同样的总数上报，
                //   那时还没采点，成功数当然是 0 —— 取"最后一条"才是采样收尾的那次。
                if (progress[i].SampleTotal >= bestTotal)
                {
                    bestTotal = progress[i].SampleTotal;
                    bestOk = progress[i].SampleOk;
                }
            }

            report.Check(bestTotal == 9 && bestOk == 9,
                "★ 采样进度里带着「成功 / 失败」计数（界面才能显示「3/9 点，成功 2、失败 1」）",
                string.Format(CultureInfo.InvariantCulture,
                    "采样期报出的最大点数 {0}，其中成功 {1}（期望 9 / 9）", bestTotal, bestOk));

            // ── ⑥ 内参链已接通：不再被"还没实现"拦住 ──
            // ★ 原来这里断言的是"未实现的链必须如实失败"。内参链落地后，这条断言的对象变了：
            //   现在要防的是**反向的错** —— 一条已经实现的链被旧的拦截逻辑挡住（用户看到按钮永远灰着），
            //   或者反过来说"能跑"却在没有板图的环境里给出假成功。
            //   真正的内参链验收在 IntrinsicsSimHarness（合成标定板图 → 注入 κ → 解回）；
            //   这里只管"接线对不对"：不再被拦，且在本环境（相机只会画特征 mark、给不出标定板）
            //   里必须**如实失败**，不能返回假成功。
            string lensWhy = co2.BlockedReason(WizardGoal.LensDistortion);
            report.Check(string.IsNullOrEmpty(lensWhy),
                "★ ⑥ 内参链（镜头畸变）已接通：不再被「还没实现」拦住",
                lensWhy == null ? "BlockedReason = null（可跑）" : "仍被拦：" + lensWhy);

            WizardRunOutcome f = co2.Run(WizardGoal.LensDistortion, mark, wopt, collect);
            report.Check(!f.Success && f.Chains.Count == 1,
                "★ ⑥ 内参链在本环境（无标定板图）如实失败，不返回假成功",
                "Success = " + f.Success + "，链数 = " + f.Chains.Count
                + "，原因：" + (f.Message ?? "<空>"));

            // ── ⑦ 没有对针方式 → 开跑前就被拦下 ──
            var coNoTip = new WizardCoordinator(run.Env, run.Sampler, null);
            string why = coNoTip.BlockedReason(WizardGoal.NozzleOffset);
            WizardRunOutcome g = coNoTip.Run(WizardGoal.NozzleOffset, mark, wopt, collect);
            report.Check(!string.IsNullOrEmpty(why) && !g.Success && g.Chains.Count == 0,
                "★ ⑦ 没有「对针方式」时，偏心链在开跑前被拦下并说明原因（不是跑到一半才崩）",
                "BlockedReason = " + (why ?? "<空>") + "；实际链数 = " + g.Chains.Count);

            // ── ⑧ 目标链永远重跑（不能因为 Vault 里已有就跳过）──
            WizardRunOutcome h2 = co2.Run(WizardGoal.CameraAccuracy, mark, wopt, collect);
            report.Check(h2.Success && h2.Chains.Count == 1 && h2.SkippedLabels.Count == 0
                         && h2.Chains[0].Chain == CalibChainKind.NinePoint,
                "★ ⑧ 用户目标那条链永远重跑（Vault 里已有 H 也不跳过）",
                h2.Summary() + "；链 = " + string.Join(" / ", h2.ChainLabels.ToArray()));

            // ── ⑨ ★★ 九点工作区的畸变影响：接线 + 回挂 + 诚实性 ──
            //   这条接缝原先的状态：`DistortionImpactAnalyzer.MeasureNinePoint`（入口 ②）
            //   **只在实验室里被调过**，产品路径上永远算不出来。原因不是"没实现"，而是三样原料
            //   （H + 九点采样集 + 相机参数）分属两条链，而 `PlanChains` 里
            //   **没有任何目标会同时跑这两条链**；更要命的是内参链的产物从来没被 `Harvest` 接住。
            //   现在：协调器把两条链的产物都攒下来，收尾时自动量化并挂在 outcome 上。
            //
            //   ① 先验"缺料时必须明说缺什么" —— 沉默的 null 会被读成"没有影响"。
            report.Check(h2.NinePointDistortionImpact == null
                         && !string.IsNullOrEmpty(h2.NinePointDistortionSkipReason)
                         && h2.NinePointDistortionSkipReason.IndexOf("内参", StringComparison.Ordinal) >= 0,
                "★★ 九点口径的畸变影响：缺内参时不许静默 —— 必须写清「缺的是内参」"
                + "（本项目已经在「畸变影响没能量化」上吃过一次：留空会被读成没有影响）",
                h2.NinePointDistortionImpact == null
                    ? "影响 = null；原因 = " + (h2.NinePointDistortionSkipReason ?? "<空>")
                    : "意外算出来了（本环境内参链是如实失败的，不该有相机参数）");

            //   ② 把相机模型补上（模拟"宿主已经带进来一个内参"）⇒ 同一个九点结果必须立刻算得出数，
            //      并且**挂在本次 outcome 上**（只落盘不回传 = 等于没算）。
            co2.AdoptCameraModel(MakeCameraModelProbe(run, -6000.0));
            WizardRunOutcome h3 = co2.Run(WizardGoal.CameraAccuracy, mark, wopt, collect);
            DistortionImpactAssessment imp9 = h3.NinePointDistortionImpact;
            report.Check(imp9 != null && imp9.Measured && imp9.Source == "ninepoint"
                         && !double.IsNaN(imp9.MaxShiftMm) && imp9.MaxShiftMm > 0.0,
                "★★ 九点工作区的畸变影响必须真的算出来并【回挂到本次结果】—— "
                + "κ 给足量时位移必须是个有意义的正数（不是 NaN、不是 0）",
                imp9 == null
                    ? "影响 = null（跳过原因 " + (h3.NinePointDistortionSkipReason ?? "<空>") + "）"
                    : string.Format(CultureInfo.InvariantCulture,
                        "Measured={0}，来源={1}，最大位移 {2:F3} mm / {3:F1} px",
                        imp9.Measured, imp9.Source, imp9.MaxShiftMm, imp9.MaxShiftPx));

            //   ③ 超过门槛就必须在"卡哪说哪"里看得见（决策 3 要的那句话）
            report.Check(imp9 != null && imp9.Measured
                         && imp9.MaxShiftMm >= DistortionImpactAnalyzer.NotableShiftMm
                         && HasIssue(h3, "DISTORTION_IMPACT_9P"),
                "★★ 位移超过「值得处理」门槛时，必须提一条 Warn（决策 3 的结论要能在界面上看见）",
                imp9 == null
                    ? "影响 = null"
                    : string.Format(CultureInfo.InvariantCulture,
                        "最大位移 {0:F3} mm，门槛 {1:F2} mm，告警 {2}",
                        imp9.MaxShiftMm, DistortionImpactAnalyzer.NotableShiftMm,
                        HasIssue(h3, "DISTORTION_IMPACT_9P") ? "已提" : "★ 没提（界面会漏掉这个决定）"));

            //   ④ 反面：κ≈0（没有畸变）时必须"测过、位移 0"，且**不许**报警 —— 防过度拒绝
            co2.AdoptCameraModel(MakeCameraModelProbe(run, 0.0));
            WizardRunOutcome h4 = co2.Run(WizardGoal.CameraAccuracy, mark, wopt, collect);
            report.Check(h4.NinePointDistortionImpact != null
                         && h4.NinePointDistortionImpact.Measured
                         && h4.NinePointDistortionImpact.MaxShiftMm == 0.0
                         && !HasIssue(h4, "DISTORTION_IMPACT_9P"),
                "★★ κ≈0 时必须「测过、位移 0」且**不许**报畸变告警"
                + "（把「没测」与「测到 0」分开，同时防过度拒绝 —— 乱报会让人不再看告警）",
                h4.NinePointDistortionImpact == null
                    ? "影响 = null（跳过原因 " + (h4.NinePointDistortionSkipReason ?? "<空>") + "）"
                    : string.Format(CultureInfo.InvariantCulture,
                        "Measured={0}，MaxShiftMm={1:F3}，告警={2}",
                        h4.NinePointDistortionImpact.Measured,
                        h4.NinePointDistortionImpact.MaxShiftMm,
                        HasIssue(h4, "DISTORTION_IMPACT_9P") ? "★ 报了（不该报）" : "未报"));

            if (d.Success && d.Chains.Count > 0 && d.Chains[0].NinePoint != null)
            {
                if (opt.VerbosePerPoint)
                {
                    DumpOne(run, d.Chains[0], "WIZ-H");
                }
            }

            // ══════════════════════════════════════════════════════════════════════════
            // ── ⑩ ★★ 「卡哪说哪」：失败必须真的进进度流（这条接缝原先一次都没被走过）──
            //
            //   修前实测：`ChainRunContext.ProgressError` 全仓**零调用**（只有声明），
            //   而它是 `RunProgress.Error` 的**唯一**生产者（另 5 处 `new RunProgress` 都不填 Error）
            //   ⇒ 界面 `CalibWizardViewModel.ApplyProgress` 里那段「卡住：…」是**死分支** ——
            //     "卡哪说哪"只是文档里的承诺，用户在界面上只看到进度条停住、说不出卡在哪。
            //
            //   修法不是在 41 个失败点上各补一次（那种改法必然漏），而是在**咽喉点**
            //   `ChainRunContext.Fail` 上接一次 —— 所有失败收口都走它。
            // ══════════════════════════════════════════════════════════════════════════

            //   ⑩-a 正向：链在运行中失败 ⇒ 进度流的【最后一帧】必须带 Error，
            //        且这一帧要自洽：StepKey ↔ StageIndex、Fraction 落在该步【起点】。
            int nBefore = progress.Count;
            WizardRunOutcome k = co2.Run(WizardGoal.LensDistortion, mark, wopt, collect);
            RunProgress lastFrame = progress.Count > nBefore ? progress[progress.Count - 1] : null;

            double priorMax = 0.0;
            for (int i = nBefore; i < progress.Count - 1; i++)
            {
                if (progress[i].Fraction > priorMax)
                {
                    priorMax = progress[i].Fraction;
                }
            }

            bool errFrame = lastFrame != null && lastFrame.Error != null && !lastFrame.Cancelled
                            && lastFrame.StageIndex >= ServiceStage.Plan && lastFrame.StageIndex <= ServiceStage.Export
                            && string.Equals(lastFrame.StepKey, ServiceStage.Key(lastFrame.StageIndex), StringComparison.Ordinal)
                            && Math.Abs(lastFrame.Fraction - ServiceStage.FractionAt(lastFrame.StageIndex, 0.0)) < 1e-12
                            && lastFrame.Fraction < ServiceStage.FractionAt(lastFrame.StageIndex, 1.0);

            report.Check(!k.Success && errFrame,
                "★★ 「卡哪说哪」：链失败时进度流里必须出现带 Error 的一帧，且它是【最后一帧】、"
                + "Fraction 落在失败步骤的【起点】（界面据此显示「卡住：<原因>」）",
                lastFrame == null
                    ? "★ 本次运行一帧进度都没发 —— 分母为 0，此断言无意义"
                    : string.Format(CultureInfo.InvariantCulture,
                        "链成功 = {0}；末帧 StageIndex = {1}({2})，Error = {3}，"
                        + "Fraction = {4:F4}（该步起点 {5:F4} / 终点 {6:F4}）",
                        k.Success, lastFrame.StageIndex, lastFrame.StepKey,
                        lastFrame.Error == null ? "<空>" : lastFrame.Error.ToString(),
                        lastFrame.Fraction,
                        ServiceStage.FractionAt(lastFrame.StageIndex, 0.0),
                        ServiceStage.FractionAt(lastFrame.StageIndex, 1.0)));

            //   ⑩-a′ 同一件事的 **durable** 版本：终态文字里必须说清【卡在第几步】。
            //        修前 `CalibError` 只带 Kind/RawCode/SampleIndex/Message，**没有步骤号**
            //        ⇒ `ChainRunResult.Summary()` 只说「失败 —— SolveFailed: …」：
            //        说了"什么错了"，没说"卡在哪一步"。而进度流那一帧是瞬时的
            //        （失败即 break 返回，界面立刻被「未完成：…」覆盖）⇒ 用户抬头看到的
            //        那句里恰恰缺了定位信息。这是"卡哪说哪"真正缺的一块。
            ChainRunResult failedChain = k.Chains.Count > 0 ? k.Chains[k.Chains.Count - 1] : null;
            bool durableOk = failedChain != null
                             && failedChain.FailedStage >= ServiceStage.Plan
                             && failedChain.FailedStage <= ServiceStage.Export
                             && failedChain.Summary().IndexOf(
                                    "失败在第 " + failedChain.FailedStage + " 步", StringComparison.Ordinal) >= 0
                             && failedChain.Summary().IndexOf(
                                    ServiceStage.Title(failedChain.FailedStage), StringComparison.Ordinal) >= 0;

            report.Check(durableOk,
                "★★ 「卡哪说哪」的【终态】版本：链失败后 Summary() 必须写明卡在第几步 + 步名"
                + "（进度流那一帧是瞬时的，用户抬头能看见的是这句）",
                failedChain == null
                    ? "★ 本次运行没有产生链结果"
                    : string.Format(CultureInfo.InvariantCulture,
                        "FailedStage = {0}（应为 1~8）；Summary = {1}",
                        failedChain.FailedStage, failedChain.Summary()));

            //   ⑩-b 反面对照 1：**成功**路径一帧都不许带 Error。
            //        （不设这条的话，"到处都报错"也能让 ⑩-a 全绿 —— 乱报会让人不再看告警。）
            int nOk = progress.Count;
            WizardRunOutcome k2 = co2.Run(WizardGoal.CameraAccuracy, mark, wopt, collect);
            int okFra = progress.Count - nOk;
            int okErr = 0;
            for (int i = nOk; i < progress.Count; i++)
            {
                if (progress[i].Error != null)
                {
                    okErr++;
                }
            }

            // ★ 并且成功的链**不许**留下步骤号（未测到的量不留假值：不许编一个"第 1 步"出来）
            int okStage = k2.Chains.Count > 0 ? k2.Chains[0].FailedStage : -1;

            report.Check(k2.Success && okFra > 0 && okErr == 0 && okStage == 0,
                "★★ 反面对照：成功路径一帧都不许带 Error、链结果上也不许留步骤号"
                + "（否则界面会一路显示「卡住」而其实跑得好好的）",
                string.Format(CultureInfo.InvariantCulture,
                    "链成功 = {0}；本次进度帧 {1} 条（带 Error 的 {2} 条）；成功链的 FailedStage = {3}（应为 0）",
                    k2.Success, okFra, okErr, okStage));

            //   ⑩-c 反面对照 2：**取消 ≠ 卡住**（所有取消路径都只置 Result.Cancelled，不走 Fail）。
            //        ★ 分母必须非 0：`WizardCoordinator.Run` 在链【之前】检查取消会一条链都不跑，
            //          那样"零个 Error"是空断言。所以取消点要落在链【内部】——
            //          用"第二次被问才回答是"的谓词（第 1 次是协调器的循环顶检查）。
            int nCancel = progress.Count;
            int asked = 0;
            Func<bool> cancelAtSecondAsk = () => (++asked) >= 2;
            WizardRunOutcome k3 = co2.Run(WizardGoal.CameraAccuracy, mark, wopt, collect, cancelAtSecondAsk);
            int canFra = progress.Count - nCancel;
            int canErr = 0;
            for (int i = nCancel; i < progress.Count; i++)
            {
                if (progress[i].Error != null)
                {
                    canErr++;
                }
            }

            report.Check(k3.Cancelled && canFra > 0 && canErr == 0,
                "★★ 反面对照：取消 ≠ 卡住 —— 被取消的运行里一条带 Error 的进度都不许有"
                + "（取消走 Cancelled 通道，只有真失败才走 Error 通道）",
                string.Format(CultureInfo.InvariantCulture,
                    "取消 = {0}；本次进度帧 {1} 条（分母必须非 0），其中带 Error 的 {2} 条",
                    k3.Cancelled, canFra, canErr));

            run.WizardOutcome = d;

            // ── ⑪ ★★ `.tup` 的双路交叉验证：手写编解码器 vs HALCON 真算子 ──
            //   ★★ 这条接缝此前**从没被执行过**：`Imaging/HalconTupleIO` 全仓（含自检工程）
            //      **零调用**，而它自己的注释写着「两条路径的输出必须逐位一致……
            //      这条断言已进离线自检」—— 那句是**假的**。
            //      形状属于"验证器写好了、没接上"（不是重复路径 ⇒ 不能删：
            //      删掉就没有了唯一的**独立**对照实现）。
            VerifyTupCross(report, run);
        }

        // ══════════════════════════════════════════════════════════════
        // ⑤ 口径选错的代价（探针）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// ★ 把"口径选错"的代价量化出来并钉住。
        ///
        /// 探针取眼在手（像面随法兰滚转）下的观测 —— 此时 O − H(p_tip) <b>已经是</b>基准角口径 ——
        /// 却故意让解法按"固定相机口径"再转一次（<c>rotateToU0: true</c>）。解析结果是：
        ///
        ///     错误报告值 = (1/n) Σ R(−Δu_k) · e_true
        ///
        /// 于是两件事同时被证明：
        ///   ① 对称角（−45/0/+45）下它是<b>恒等</b>：|e| 一字不差 —— 这个坑会被对称角完全掩盖；
        ///   ② 非对称角（−30/0/+45）下它明显偏离：|e| 缩水、方向出现假偏差，
        ///      并最终以"对针不稳"的名义把完美标定拦掉。
        /// 断言的是"实现行为 = 解析预测"，即这条链路的行为被彻底钉住（不是"看起来差不多"）。
        /// </summary>
        private static void ProbeNormalizationPremise(SelfCheckReport report, SimulationRun run,
            SimulationHarnessOptions opt)
        {
            if (run.ToolOffset == null || !run.ToolOffset.Success || run.ToolOffset.ToolOffset == null
                || run.NinePoint == null || !run.NinePoint.Success || run.NinePoint.NinePoint == null)
            {
                report.Check(false, "口径选错的代价探针", "e 链没跑通，无法做探针。");
                return;
            }

            HomMat2D h = run.NinePoint.NinePoint.H;
            Vec2 oSolved = run.RotationCenter.RotationCenter.Center;
            Vec2 tipPixel = run.ToolOffset.ToolOffset.TipPixel;

            // 眼在手 + 尖压住特征 ⇒ 像素与角度无关，所以同一个像素可以配上任意角序列来提问
            double[] probeAngles = new double[] { -30.0, 0.0, 45.0 };
            var pixels = new List<Vec2>(probeAngles.Length);
            var us = new List<double>(probeAngles.Length);
            for (int i = 0; i < probeAngles.Length; i++)
            {
                pixels.Add(tipPixel);
                us.Add(probeAngles[i]);
            }

            Vec2 eqPixel;
            Vec2 wrong;
            double scatter;
            if (!ToolOffsetSolver.SolveNormalized(h, pixels, us, oSolved, run.Topology.CalibU0,
                    true, out eqPixel, out wrong, out scatter))
            {
                report.Check(false, "口径选错的代价探针（非对称角 −30/0/+45）", "SolveNormalized 返回失败。");
                return;
            }

            Vec2 sum = Vec2.Zero;
            for (int i = 0; i < probeAngles.Length; i++)
            {
                sum = sum + opt.TipEcc.Rotate(run.Topology.CalibU0 - probeAngles[i]);
            }

            Vec2 predicted = sum / probeAngles.Length;

            run.WrongRegimeEcc = wrong;
            run.WrongRegimePredicted = predicted;

            double gap = (wrong - predicted).Length;
            report.Check(gap < 5e-4,
                "★ 口径选错的代价 = (1/n)Σ R(−Δu)·e_true（解析预测与实测吻合，已被钉住）",
                string.Format(CultureInfo.InvariantCulture,
                    "角度 −30/0/+45：错误口径给出 ({0:F4}, {1:F4})（|e| {2:F4} mm），"
                    + "解析预测 ({3:F4}, {4:F4})（|e| {5:F4} mm），差 {6:E2} mm；"
                    + "正确口径给出 e 真值 ({7:F4}, {8:F4})（|e| {9:F4} mm）"
                    + " ⇒ |e| 缩水 {10:F1}%、方向被凭空拧出偏差，最终会被读成「对针不稳」。"
                    + "★ 结论：口径必须按「像面是否随法兰滚转」分流，不能无条件归一。",
                    wrong.X, wrong.Y, wrong.Length, predicted.X, predicted.Y, predicted.Length, gap,
                    opt.TipEcc.X, opt.TipEcc.Y, opt.TipEcc.Length,
                    (1.0 - wrong.Length / opt.TipEcc.Length) * 100.0));
        }

        // ══════════════════════════════════════════════════════════════
        // ⑤ 产物落盘
        // ══════════════════════════════════════════════════════════════

        private static void VerifyArtifacts(SelfCheckReport report, SimulationRun run, SimulationHarnessOptions opt)
        {
            if (string.IsNullOrEmpty(run.StoreRoot))
            {
                report.Check(false, "产物落盘", "没有存储根目录。");
                return;
            }

            string exports = Path.Combine(run.StoreRoot, "exports");
            string frames = Path.Combine(run.StoreRoot, "frames");

            int exportFiles = Directory.Exists(exports) ? Directory.GetFiles(exports, "*.calib.json").Length : 0;
            int tupFiles = Directory.Exists(exports) ? Directory.GetFiles(exports, "*.tup").Length : 0;
            int frameFiles = Directory.Exists(frames) ? Directory.GetFiles(frames, "*", SearchOption.AllDirectories).Length : 0;

            report.Check(exportFiles >= 3 && tupFiles >= 1,
                "三条链各自落了中性产物（.calib.json ≥ 3，.tup ≥ 1）",
                string.Format(CultureInfo.InvariantCulture,
                    "导出目录 {0}：json {1} 个，tup {2} 个；留帧 {3} 个 PGM，根目录 {4}",
                    exports, exportFiles, tupFiles, frameFiles, run.StoreRoot));

            VerifyPublish(report, run);
        }

        // ══════════════════════════════════════════════════════════════
        // ⑤b 发布接缝（第 11 步：ICalibrationPublisher）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// ★ 第 11 步的验收：<b>发布这条路径必须真的被走到过</b>。
        ///
        /// 在这之前 <c>env.Publisher</c> 恒为 null，于是
        /// <c>ChainRunnerSupport.ExportAndPublish</c> 里那段"发布 + 发布前体检"
        /// 从来没执行过 —— 离线断言全绿也证明不了它是对的。
        /// 所以这里断的不是"返回值好看"，而是"确实调到了、门禁确实拦住了、
        /// 抛异常的那条兜底路确实走得通"。
        /// </summary>
        private static void VerifyPublish(SelfCheckReport report, SimulationRun run)
        {
            SimulatedCalibrationPublisher pub = run.Publisher;

            if (pub == null || run.Env == null || run.Env.Publisher == null)
            {
                report.Check(false, "① 仿真模式挂上发布器",
                    "env.Publisher 仍是 null —— 发布路径与发布前体检都不会被走到（这正是第 11 步要堵的洞）。");
                return;
            }

            report.Check(ReferenceEquals(run.Env.Publisher, pub),
                "① 仿真模式挂上发布器（env.Publisher 与 rig.Publisher 是同一实例）",
                "env.Publisher = " + run.Env.Publisher.Name);

            // ── ② 三条链确实调到了发布，且产物留档 ──
            int publishedChains = 0;
            publishedChains += CountPublished(report, run.NinePoint, pub, "H 链（九点）");
            publishedChains += CountPublished(report, run.RotationCenter, pub, "O 链（旋转中心）");
            publishedChains += CountPublished(report, run.ToolOffset, pub, "e 链（吸嘴偏心）");

            report.Check(publishedChains == 3 && pub.Published.Count >= 3,
                "② 三条链真的走完了发布（发布器里留了档）",
                string.Format(CultureInfo.InvariantCulture,
                    "本次留档 {0} 份；最近一次：{1}", pub.Published.Count, pub.LastMessage));

            // ── ③ 次序：先落文件、再发布 ──
            //   判据是"发布成功的那些链，导出目录里确实有文件" ——
            //   只有落到盘上才算物证，只发布不落盘等于把现场唯一的证据丢了。
            bool orderOk = true;
            string orderWhy = "三条链的导出文件都在（发布之前就落好了盘）";
            orderOk &= HasExportedFile(run.NinePoint, ref orderWhy);
            orderOk &= HasExportedFile(run.RotationCenter, ref orderWhy);
            orderOk &= HasExportedFile(run.ToolOffset, ref orderWhy);

            report.Check(orderOk, "③ ★ 先落文件再发布（发布成功 ⇒ 导出的 .calib.json 确实在盘上）", orderWhy);

            // ── ④ 发布门禁真的拦得住 ──
            //   ★ 门禁只有在"真的有发布器"时才会被走到。造两个本该被拒的产物，
            //     不是看返回值好看，而是看它有没有真拦、拦完有没有计数。
            int before = pub.Published.Count;
            int rejectedBefore = pub.RejectedCount;

            string m1;
            bool ok1 = pub.Publish(new CalibExport
            {
                Chain = CalibChainKind.NinePoint,
                Diagnostics = new CalibDiagnostics { HasBlocker = true }
            }, out m1);

            string m2;
            bool ok2 = pub.Publish(new CalibExport
            {
                Chain = CalibChainKind.NinePoint,
                Diagnostics = new CalibDiagnostics { Sigma1 = 1.25, Sigma2 = 1.0, SigmaRatio = 1.25, AnisotropyPct = 25.0, BadShape = true }
            }, out m2);

            string m3;
            bool ok3 = pub.Publish(null, out m3);

            bool gateOk = !ok1 && !ok2 && !ok3
                && pub.RejectedCount == rejectedBefore + 3
                && pub.Published.Count == before;

            report.Check(gateOk,
                "④ ★ 发布前体检真的拦得住（阻断项 / 形状非法 / 空产物 一律拒绝）",
                string.Format(CultureInfo.InvariantCulture,
                    "阻断项:{0}｜形状:{1}｜空:{2}｜累计拒绝 {3} 次｜留档仍为 {4} 份",
                    ok1, ok2, ok3, pub.RejectedCount, pub.Published.Count));

            report.Check(m2.IndexOf("1.250", StringComparison.Ordinal) >= 0,
                "④b 拒绝理由说清了数字（σ1/σ2 直接进消息，不是一句「失败」）",
                "消息：" + m2);

            // ── ⑤ 发布器抛异常时，文件必须仍然在（ChainRunnerSupport 的兜底）──
            VerifyPublishThrowStillKeepsFile(report, run);

            // ── ⑥ 完全没有发布器（独立打包）时，仍要如实降级、不假成功 ──
            VerifyNoPublisherDegradation(report, run);
        }

        /// <summary>某条链是否真的发布了，且消息里点名了发布者。</summary>
        private static int CountPublished(SelfCheckReport report, ChainRunResult r,
            SimulatedCalibrationPublisher pub, string label)
        {
            if (r == null)
            {
                return 0;
            }

            bool called = r.PublishMessage != null
                && r.PublishMessage.IndexOf(pub.Name, StringComparison.Ordinal) >= 0;

            report.Check(r.Published && called,
                "② " + label + " 的发布消息点名了发布者（不是「无宿主」那条分支）",
                "Published=" + r.Published + "｜" + r.PublishMessage);

            return r.Published ? 1 : 0;
        }

        private static bool HasExportedFile(ChainRunResult r, ref string why)
        {
            if (r == null)
            {
                why = "某条链没有结果";
                return false;
            }

            if (string.IsNullOrEmpty(r.ExportDir) || !Directory.Exists(r.ExportDir))
            {
                why = "导出目录不存在：" + r.ExportDir;
                return false;
            }

            if (Directory.GetFiles(r.ExportDir, "*.calib.json").Length == 0)
            {
                why = "导出目录里没有 .calib.json：" + r.ExportDir;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 发布器抛异常时：<b>文件必须还在</b>。
        /// 这是"先落文件再发布"这条次序规则的真正兑现处 ——
        /// 如果某天有人把两件事调换，这条断言会立刻红。
        /// </summary>
        private static void VerifyPublishThrowStillKeepsFile(SelfCheckReport report, SimulationRun run)
        {
            if (run.NinePoint == null || run.NinePoint.Export == null)
            {
                report.Check(false, "⑤ 发布器抛异常时文件仍在", "H 链没有产物，无法验证。");
                return;
            }

            string root = Path.Combine(Path.GetTempPath(),
                "vct_pubthrow_" + DateTime.Now.ToString("HHmmssfff", CultureInfo.InvariantCulture));

            try
            {
                // ★ 这里刻意造一个"必抛"的发布器：ICalibrationPublisher 的约定是
                //   "不该抛"，但宿主实现是外部代码，抛了也不能把已经落好的文件带走。
                var env = new SimulatedEnvironment(run.World, null,
                    new FileSystemCalibStore(root), new SimpleCalibLog(), new AutoYesUserPrompt(),
                    new ThrowingPublisher());

                var ctx = new ChainRunContext(env.Log);
                ChainRunnerSupport.ExportAndPublish(ctx, new ChainRunOptions
                {
                    ExportFiles = true,
                    PublishToHost = true
                }, env, run.NinePoint.Export);

                bool keptFile = !string.IsNullOrEmpty(ctx.Result.ExportDir)
                    && Directory.Exists(ctx.Result.ExportDir)
                    && Directory.GetFiles(ctx.Result.ExportDir, "*.calib.json").Length > 0;

                bool warned = false;
                for (int i = 0; i < ctx.Result.Issues.Count; i++)
                {
                    if (ctx.Result.Issues[i].Code == "PUBLISH_FAILED")
                    {
                        warned = true;
                        break;
                    }
                }

                report.Check(!ctx.Result.Published && keptFile && warned,
                    "⑤ ★ 发布器抛异常时：文件仍在、状态如实为失败、并留下 PUBLISH_FAILED 告警",
                    string.Format(CultureInfo.InvariantCulture,
                        "Published={0}｜文件在={1}｜告警={2}｜{3}",
                        ctx.Result.Published, keptFile, warned, ctx.Result.PublishMessage));
            }
            catch (Exception ex)
            {
                report.Check(false, "⑤ 发布器抛异常时文件仍在",
                    "验证本身出错：" + ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch
                {
                    // 清理失败不影响验收结论
                }
            }
        }

        /// <summary>故意抛异常的发布器：用来验证兜底（文件不许被它带走）。</summary>
        private sealed class ThrowingPublisher : ICalibrationPublisher
        {
            public string Name
            {
                get { return "必抛发布器（仅用于验证兜底）"; }
            }

            public bool Publish(CalibExport export, out string message)
            {
                message = "boom";
                throw new InvalidOperationException("故意制造的宿主发布异常");
            }
        }

        /// <summary>半径是中间量：只允许出现在诊断文字里，不允许成为产物字段。</summary>
        private static bool RadiusNotShipped(CalibExport export)
        {
            if (export == null || export.FocalLengthPx != null || export.Board != null)
            {
                return false;
            }

            // 产物里根本没有"半径"这个字段；再确认诊断文字确实写了"丢弃"，
            // 免得将来有人把 FittedRadiusMm 塞进某个新字段时这里还绿着。
            CalibDiagnostics d = export.Diagnostics;
            if (d == null)
            {
                return false;
            }

            for (int i = 0; i < d.Notes.Count; i++)
            {
                if (d.Notes[i] != null && d.Notes[i].Contains("丢弃"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 独立模式（<c>env.Publisher == null</c>）仍然必须走得通、且如实说明。
        /// ★ 这条与第 11 步不冲突：第 11 步补的是"仿真/独立模式下也要有一个发布器来
        ///   把路径走一遍"，而"宿主与发布器都没有"仍是<b>完全合法</b>的一种部署形态
        ///   （工具单独打包给现场用就是它），所以两条路都得留、都得验。
        /// </summary>
        private static void VerifyNoPublisherDegradation(SelfCheckReport report, SimulationRun run)
        {
            if (run.NinePoint == null || run.NinePoint.Export == null)
            {
                report.Check(false, "⑥ 无发布者时如实降级为「只落文件」", "H 链没有产物，无法验证。");
                return;
            }

            string root = Path.Combine(Path.GetTempPath(),
                "vct_nopub_" + DateTime.Now.ToString("HHmmssfff", CultureInfo.InvariantCulture));

            try
            {
                var env = new SimulatedEnvironment(run.World, null,
                    new FileSystemCalibStore(root), new SimpleCalibLog(), new AutoYesUserPrompt(),
                    null);   // ★ 刻意不挂发布器

                var ctx = new ChainRunContext(env.Log);
                ChainRunnerSupport.ExportAndPublish(ctx, new ChainRunOptions
                {
                    ExportFiles = true,
                    PublishToHost = true
                }, env, run.NinePoint.Export);

                bool saidSo = ctx.Result.PublishMessage != null
                    && ctx.Result.PublishMessage.IndexOf("无宿主", StringComparison.Ordinal) >= 0;

                bool keptFile = !string.IsNullOrEmpty(ctx.Result.ExportDir)
                    && Directory.Exists(ctx.Result.ExportDir)
                    && Directory.GetFiles(ctx.Result.ExportDir, "*.calib.json").Length > 0;

                report.Check(!ctx.Result.Published && saidSo && keptFile,
                    "⑥ 无发布者时如实降级为「只落文件」（不假成功、文件仍在）",
                    string.Format(CultureInfo.InvariantCulture,
                        "Published={0}｜文件在={1}｜消息：{2}",
                        ctx.Result.Published, keptFile, ctx.Result.PublishMessage));
            }
            catch (Exception ex)
            {
                report.Check(false, "⑥ 无发布者时如实降级为「只落文件」",
                    "验证本身出错：" + ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch
                {
                    // 清理失败不影响验收结论
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 辅助
        // ══════════════════════════════════════════════════════════════

        private static void CheckTupRoundTrip(SelfCheckReport report, ChainRunResult r, string stage)
        {
            if (string.IsNullOrEmpty(r.ExportDir))
            {
                report.Check(false, stage + "：.tup 落盘", "没有导出目录。");
                return;
            }

            string[] tups = Directory.GetFiles(r.ExportDir, "*.tup");
            if (tups.Length == 0)
            {
                report.Check(false, stage + "：.tup 落盘", "导出目录里没有 .tup：" + r.ExportDir);
                return;
            }

            try
            {
                HomMat2D back = HomMatIO.ReadTup(tups[0]);
                HomMat2D solved = r.NinePoint.H;
                double[] a = back.ToArray();
                double[] b = solved.ToArray();
                bool identical = true;
                double maxDiff = 0.0;
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i])
                    {
                        identical = false;
                    }

                    maxDiff = Math.Max(maxDiff, Math.Abs(a[i] - b[i]));
                }

                byte[] raw = File.ReadAllBytes(tups[0]);
                string head = System.Text.Encoding.ASCII.GetString(raw, 0, Math.Min(3, raw.Length));

                report.Check(identical && head == "06\n",
                    stage + "：.tup 可被读回且与解出的 H 逐位一致（主项目「导入外部标定矩阵文件」可直接消费）",
                    string.Format(CultureInfo.InvariantCulture,
                        "{0}（{1} 字节，首行 {2}），最大偏差 {3:E2}",
                        Path.GetFileName(tups[0]), raw.Length, Escape(head), maxDiff));
            }
            catch (Exception ex)
            {
                report.Check(false, stage + "：.tup 读回", ex.GetType().Name + "：" + ex.Message);
            }
        }

        private static double MaxElementDiff(HomMat2D a, HomMat2D b)
        {
            double[] x = a.ToArray();
            double[] y = b.ToArray();
            double max = 0.0;
            for (int i = 0; i < x.Length && i < y.Length; i++)
            {
                double d = Math.Abs(x[i] - y[i]);
                if (d > max)
                {
                    max = d;
                }
            }

            return max;
        }

        private static string FirstBlocker(ChainRunResult r)
        {
            if (r == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < r.Issues.Count; i++)
            {
                if (r.Issues[i].Severity == PlanIssueSeverity.Blocker)
                {
                    return "首条阻断：" + r.Issues[i].Message;
                }
            }

            if (r.Error != null)
            {
                return "错误：" + r.Error;
            }

            return string.Empty;
        }

        private static void DumpPerPoint(SimulationRun run, string tag)
        {
            DumpOne(run, run.NinePoint, tag + "-H");
            DumpOne(run, run.RotationCenter, tag + "-O");
            DumpOne(run, run.ToolOffset, tag + "-e");
        }

        /// <summary>
        /// ★ 逐点人话诊断：每个采样点"世界位 / 像素 / 失败原因"都记下来。
        /// 这条信息不能用"可用 0/9"代替 —— 那样的报告在现场只能让人重跑，不能让人判断。
        /// </summary>
        public static void RecordSampleDiagnostics(SimulationRun run, ChainRunResult r, string tag)
        {
            if (run == null || r == null || r.Sampling == null)
            {
                return;
            }

            for (int i = 0; i < r.Sampling.Samples.Count; i++)
            {
                CalibSample s = r.Sampling.Samples[i];
                if (s == null)
                {
                    continue;
                }

                CalibObservation o = FindObservation(r.Sampling, s.Index);
                Vec2 truth = run.World.ProjectAt(s.FeedbackXy, s.FeedbackU);
                string reason = o == null ? "（没有观测记录）"
                    : (string.IsNullOrEmpty(o.RejectReason) ? "ok" : o.RejectReason);

                run.Transcript.Add(string.Format(CultureInfo.InvariantCulture,
                    "[{0}] #{1} {2} 世界({3:F3},{4:F3}) U{5:F1} 像素({6:F3},{7:F3}) 真值({8:F3},{9:F3}) 偏差 {10:F3}px 质量 {11:F0} 候{12} | {13}",
                    tag, s.Index, s.State, s.FeedbackXy.X, s.FeedbackXy.Y, s.FeedbackU,
                    o == null ? double.NaN : o.Pixel.X, o == null ? double.NaN : o.Pixel.Y,
                    truth.X, truth.Y,
                    o == null ? double.NaN : (o.Pixel - truth).Length,
                    o == null ? 0.0 : o.MatchScore,
                    o == null ? -1 : o.CandidateCount,
                    reason));
            }
        }

        private static CalibObservation FindObservation(SamplingOutcome outcome, int index)
        {
            for (int i = 0; i < outcome.Observations.Count; i++)
            {
                if (outcome.Observations[i] != null && outcome.Observations[i].Index == index)
                {
                    return outcome.Observations[i];
                }
            }

            return null;
        }

        private static void DumpOne(SimulationRun run, ChainRunResult r, string tag)
        {
            if (r == null || r.Sampling == null)
            {
                return;
            }

            for (int i = 0; i < r.Sampling.Observations.Count; i++)
            {
                CalibObservation o = r.Sampling.Observations[i];
                if (o == null)
                {
                    continue;
                }

                Vec2 truth = run.World.ProjectAt(o.World, r.Sampling.Samples.Count > 0 ? FindU(r.Sampling, o.Index) : 0.0);
                run.Transcript.Add(string.Format(CultureInfo.InvariantCulture,
                    "[{0}] #{1} 世界 ({2:F3},{3:F4}) 像素 ({4:F3},{5:F3}) 真值 ({6:F3},{7:F3}) 偏差 {8:F4} px 质量 {9:F1}",
                    tag, o.Index, o.World.X, o.World.Y, o.Pixel.X, o.Pixel.Y, truth.X, truth.Y,
                    (o.Pixel - truth).Length, o.MatchScore));
            }
        }

        private static double FindU(SamplingOutcome outcome, int index)
        {
            for (int i = 0; i < outcome.Samples.Count; i++)
            {
                if (outcome.Samples[i] != null && outcome.Samples[i].Index == index)
                {
                    return outcome.Samples[i].FeedbackU;
                }
            }

            return 0.0;
        }

        private static string Escape(string s)
        {
            return s.Replace("\n", "\\n").Replace("\r", "\\r");
        }

        /// <summary>
        /// 造一个"够真实"的相机模型，用来把**九点口径**的畸变量化接上（见 ⑨）。
        ///
        /// ★ 为什么不用真内参链的产物：本环境的相机只会画特征 mark、给不出标定板
        ///   ⇒ 内参链在这里**如实失败**（见 ⑥），拿不到真内参。
        ///   这条用例对应的是"宿主已经带进来一个内参"的情形（<c>AdoptCameraModel</c>），
        ///   也正是产品里"先跑过内参链、再跑九点"那条路的状态。
        /// </summary>
        private static DivisionCameraModel MakeCameraModelProbe(SimulationRun run, double kappa)
        {
            int w = run.Env.Camera == null ? 2448 : run.Env.Camera.Width;
            int h = run.Env.Camera == null ? 2048 : run.Env.Camera.Height;
            var r = new IntrinsicsResult();
            r.PrincipalPointPx = new double[] { w / 2.0, h / 2.0 };
            r.ImageSize = new int[] { w, h };
            r.Distortion = new double[] { kappa };
            return DivisionCameraModel.FromIntrinsics(r, 0.016, 5.0e-6);
        }

        /// <summary>结果里有没有这条 code 的问题（断言"该报警的报了 / 不该报的没报"）。</summary>
        private static bool HasIssue(WizardRunOutcome r, string code)
        {
            if (r == null || r.Issues == null)
            {
                return false;
            }

            for (int i = 0; i < r.Issues.Count; i++)
            {
                if (r.Issues[i] != null && string.Equals(r.Issues[i].Code, code, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// ★★ ⑪ `.tup` 的<b>双路交叉验证</b> —— 手写编解码器（<see cref="HomMatIO"/>）
        /// vs HALCON 真算子（<see cref="HalconTupleIO"/>）。
        ///
        /// 「为什么必须验」：`.tup` 是本工具交给主项目的<b>唯一产物格式</b>
        /// （主项目只把文件路径存进 profile，运行时自己读）。所以
        /// 「我们写出来的文件，HALCON 读出来是不是同一个矩阵」就是整个交付的<b>最后一公里</b>契约。
        ///
        /// ★ 而它此前<b>一次都没被验过</b>：<see cref="HalconTupleIO"/> 全仓零调用（含自检工程），
        /// 尽管它自己的注释写着「这条断言已进离线自检」。这是"注释说的和代码做的相反"的又一种形态：
        /// <b>验证器写好了、没接上</b>（不是重复路径 ⇒ 不能删，删了就没了唯一的<b>独立</b>对照）。
        ///
        /// 「判据分两档，两档都有意义」（不搞"跳过就算绿"）：
        ///   · HALCON 可用 ⇒ 真算子写出的<b>字节</b> / 读回的<b>值</b> / 解算的<b>结果</b>，逐条与手写实现对齐；
        ///   · HALCON 不可用 ⇒ 断言<b>降级行为正确</b>：<c>Try*</c> 一律返回 false + 有原因、<b>绝不抛</b>。
        /// </summary>
        private static void VerifyTupCross(SelfCheckReport report, SimulationRun run)
        {
            string halconVersion = HalconTupleIO.TryGetHalconVersion();
            bool hasHalcon = !string.IsNullOrEmpty(halconVersion);

            report.Check(hasHalcon,
                "★★ `.tup` 双路交叉验证的前提：HALCON 原生库可用（取得到版本号）",
                hasHalcon
                    ? "HALCON 版本 = " + halconVersion
                    : "取不到版本号 —— 本段（写一致 / 读回一致 / 解算对照）无法执行，"
                      + "这里如实报失败，不是静默跳过");

            // ★ `HomMat2D` 是结构体（值类型）⇒ 不能拿 null 当"没有"的哨兵，
            //   用一个显式的 bool 说清"九点链到底跑通没有"。
            bool hasTruth = run.NinePoint != null && run.NinePoint.Success && run.NinePoint.NinePoint != null;
            HomMat2D truth = hasTruth ? run.NinePoint.NinePoint.H : HomMat2D.Identity;

            if (!hasTruth)
            {
                report.Check(false, "★★ `.tup` 双路交叉验证需要九点链解出的 H 作为被写对象",
                    "九点链没跑通（run.NinePoint = "
                    + (run.NinePoint == null ? "null" : run.NinePoint.Summary()) + "）");
                return;
            }

            // ★ 临时目录用完要清，但清理失败**不能影响验收结论**（所以 finally 里吞掉异常）。
            string dir = Path.Combine(Path.GetTempPath(), "vct_tup_cross_" + Guid.NewGuid().ToString("N"));
            string manualPath = Path.Combine(dir, "manual.tup");
            string nativePath = Path.Combine(dir, "native.tup");
            try
            {
                Directory.CreateDirectory(dir);

                // 生产路径（主项目最终读到的就是这份）
                HomMatIO.WriteTup(manualPath, truth);

                string writeErr;
                bool wrote = HalconTupleIO.TryWriteTup(nativePath, truth, out writeErr);

                string readErr;
                HomMat2D back;
                bool read = HalconTupleIO.TryReadTup(manualPath, out back, out readErr);

                bool bitsSame = false;
                string bitsWhy = "真算子没写成功，无法比对字节";
                if (wrote)
                {
                    byte[] a = File.ReadAllBytes(manualPath);
                    byte[] b = File.ReadAllBytes(nativePath);
                    bitsSame = a.Length == b.Length;
                    if (bitsSame)
                    {
                        for (int i = 0; i < a.Length; i++)
                        {
                            if (a[i] != b[i])
                            {
                                bitsSame = false;
                                bitsWhy = string.Format(CultureInfo.InvariantCulture,
                                    "手写 {0} 字节 / 真算子 {1} 字节：第 {2} 字节起不同（手写 {3} / 真算子 {4}）",
                                    a.Length, b.Length, i, (int)a[i], (int)b[i]);
                                break;
                            }
                        }
                    }
                    else
                    {
                        bitsWhy = string.Format(CultureInfo.InvariantCulture,
                            "长度就不同：手写 {0} 字节 / 真算子 {1} 字节", a.Length, b.Length);
                    }

                    if (bitsSame)
                    {
                        bitsWhy = string.Format(CultureInfo.InvariantCulture,
                            "两路产物逐位完全一致（各 {0} 字节）", a.Length);
                    }
                }

                bool readBackSame = read && SameSix(truth, back);

                report.Check(wrote && read && readBackSame,
                    "★★★ `.tup` 的最后一公里：手写编解码器写出的文件，HALCON 真算子必须读回**同一个矩阵**"
                    + "（六个参数逐位相等）—— 主项目正是这样读它的",
                    string.Format(CultureInfo.InvariantCulture,
                        "真算子写 = {0}（{1}）；真算子读回 = {2}（{3}）；六参数逐位相等 = {4}",
                        wrote, writeErr ?? "-", read, readErr ?? "-", readBackSame));

                report.Check(bitsSame,
                    "★★ `.tup` 双路**字节级**逐位一致（HalconTupleIO 注释里宣称的「两条路径的输出必须逐位一致」）",
                    bitsWhy);

                // ── ⑪-d 解算交叉：同一批九点样本，两条**独立**实现 ──
                List<CalibSample> usable = (run.NinePoint != null && run.NinePoint.Sampling != null)
                    ? NinePointSolver.CollectUsable(run.NinePoint.Sampling.Samples)
                    : new List<CalibSample>();

                if (usable.Count < 3)
                {
                    report.Check(false, "★★ 解算交叉验证需要至少 3 个可用九点样本",
                        "可用样本 " + usable.Count + " 个 —— 分母不足时不做空断言");
                }
                else
                {
                    int n = usable.Count;
                    double[] px = new double[n];
                    double[] py = new double[n];
                    double[] wx = new double[n];
                    double[] wy = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        px[i] = usable[i].Pixel.X;
                        py[i] = usable[i].Pixel.Y;
                        wx[i] = usable[i].FeedbackXy.X;
                        wy[i] = usable[i].FeedbackXy.Y;
                    }

                    HomMat2D hNative;
                    string solveErr;
                    bool solved = HalconTupleIO.TrySolveHomMat2D(px, py, wx, wy, out hNative, out solveErr);
                    double maxRel = solved ? MaxRelDiff(truth, hNative) : double.NaN;

                    report.Check(solved && maxRel < 1e-6,
                        "★★ 解算交叉验证：HALCON 的 `vector_to_hom_mat2d` 与我们的九点解算在同一批点上必须一致"
                        + "（两条**独立**实现一致，才说明我们手写的仿射最小二乘没写歪）",
                        string.Format(CultureInfo.InvariantCulture,
                            "真算子解 = {0}（{1}）；最大相对偏差 = {2:E3}（阈值 1E-06）；点数 = {3}",
                            solved, solveErr ?? "-", maxRel, n));
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>六个参数是否**逐位相等**（`==`，不是容差比较 —— 这里要的就是"一模一样"）。</summary>
        private static bool SameSix(HomMat2D a, HomMat2D b)
        {
            return a.H11 == b.H11 && a.H12 == b.H12 && a.H13 == b.H13
                && a.H21 == b.H21 && a.H22 == b.H22 && a.H23 == b.H23;
        }

        /// <summary>
        /// 六参数的**最大相对偏差**（分母取 `max(1, |参考值|)`：既避开除零，
        /// 也不会因为参考值本身很小而把正常的浮点噪声放大成"巨大偏差"）。
        /// </summary>
        private static double MaxRelDiff(HomMat2D a, HomMat2D b)
        {
            double[] va = a.ToArray();
            double[] vb = b.ToArray();
            double worst = 0.0;
            for (int i = 0; i < va.Length && i < vb.Length; i++)
            {
                double denom = Math.Max(1.0, Math.Abs(va[i]));
                double rel = Math.Abs(va[i] - vb[i]) / denom;
                if (rel > worst)
                {
                    worst = rel;
                }
            }

            return worst;
        }
    }
}
