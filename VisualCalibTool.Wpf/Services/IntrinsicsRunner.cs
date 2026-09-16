using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;
using VisualCalibTool.Imaging;

namespace VisualCalibTool.Services
{
    /// <summary>内参链的运行选项。</summary>
    public sealed class IntrinsicsRunOptions
    {
        /// <summary>标定板模型（必填 —— 没有"尺子"就量不出镜头）。</summary>
        public BoardModel Board;

        /// <summary>内参起点（标称焦距 + 像元尺寸；主点/畸变留默认）。</summary>
        public CameraIntrinsicsGuess Guess;

        /// <summary>覆盖度判据（默认 3 张倾斜 / 20° / 10 mm / 2 个方向）。</summary>
        public IntrinsicsCoverageCriteria Coverage = new IntrinsicsCoverageCriteria();

        /// <summary>摆板脚本（默认 <see cref="IntrinsicsPoseScript.Build"/>）。</summary>
        public List<PosePrompt> Script;

        /// <summary>
        /// 覆盖度不足时是否硬拦。
        /// ★ 默认 true：覆盖度不够时焦距与畸变分不开，解出来的数会"看着像那么回事"。
        ///   允许操作员显式放行（他会拿到明确告警），但不允许静默地拿一个不可信的产物发布。
        /// </summary>
        public bool BlockWhenCoveragePoor = true;

        /// <summary>取图超时（毫秒）。人工摆板，取图本身很快，超时只兜住相机异常。</summary>
        public int GrabTimeoutMs = 3000;

        /// <summary>单个姿态最多重拍几次（找板失败时）。</summary>
        public int MaxRetriesPerPose = 3;

        /// <summary>整体重投影 RMSE 的硬上限（像素）。超过就说明链路上有系统性错误。</summary>
        public double MaxAcceptableRmsePx = 1.0;

        /// <summary>单张重投影的告警线 / 硬线（像素）。</summary>
        public double PerViewWarnPx = 0.3;
        public double PerViewBlockPx = 0.5;

        /// <summary>是否把每张板图落盘留档。</summary>
        public bool SaveFrames = true;

        /// <summary>共用选项（导出 / 发布 / 署名）。</summary>
        public ChainRunOptions Common = new ChainRunOptions();

        /// <summary>
        /// 每张板图取到并找过板之后回调一次，宿主据此<b>把画面和检出结果显示出来</b>。
        /// null = 不显示（离线自检用不到画面，但会用同一个回调收载荷做断言）。
        ///
        /// ★ 为什么这条链必须有它（其它三链是"跑完再画"，这条链必须"边摆边画"）：
        ///   内参链的输入来自<b>操作员的手</b>，而不是机械手的走位。他必须当场知道
        ///   "板摆得够不够大 / 认出了哪些 mark"，否则只能拍完一整轮、等最后那句
        ///   "板偏小" 才知道白拍了。信息晚一步，代价就是重摆一遍。
        /// </summary>
        public Action<CalibVisualizationRequest> Visualize;
    }

    /// <summary>
    /// ★ 内参链（"镜头有没有把人拍歪"）。
    ///
    /// 与 H / O / e 三条链的根本差别：<b>它不靠机械手走位取图，靠人摆板</b>。
    /// 所以整条链的重心不在"运动安全"，而在：
    ///   · <b>把该摆什么讲清楚</b>（脚本 + 逐张提示 + 失败原因）；
    ///   · <b>摆完之后判"够不够"</b>（几何覆盖度，不看残差 —— 残差好看不代表覆盖度够）；
    ///   · <b>如实说"这个数可信到什么程度"</b>（参数标准差 + 逐张重投影 + 畸变量级）。
    ///
    /// 八步骨架与其它链一致（见 <see cref="ServiceStage"/>），但第 ④ 步"采样"在这里是
    /// "引导摆板取图"，第 ② 步"预检"是"板模型 + 起点体检"。
    /// </summary>
    public sealed class IntrinsicsRunner
    {
        /// <summary>
        /// 板视在宽度至少要占图像宽度的这个比例，否则告警。
        ///
        /// ★ 阈值是<b>实测</b>来的，不是拍的：本相机（f 11.84 mm、1280×1024、像元 3.45 µm）
        ///   在 231 mm 时相邻 mark 间距 19.2 px、板宽占画面 45% → 检出 818 个 mark；
        ///   240 mm 时间距 18.4 px、占 43% → 检出 <b>0</b> 个。断崖就在这中间。
        ///   所以"板宽占画面一半"这条线只剩几个百分点的余量，取 50% 作为告警线：
        ///   低于它时"这张能过、下一张突然全灭"的概率很高，必须在还能拍的时候提醒。
        /// </summary>
        public const double MinApparentWidthFraction = 0.50;

        /// <summary>
        /// 低于这个比例就别只是"提示"了 —— 已经贴到断崖（实测 ~43%），再远一点就拍一张废一张。
        /// </summary>
        public const double MinApparentWidthFractionHard = 0.45;

        private readonly IVisualCalibEnvironment _env;

        public IntrinsicsRunner(IVisualCalibEnvironment env)
        {
            if (env == null)
            {
                throw new ArgumentNullException("env");
            }

            _env = env;
        }

        public ChainRunResult Run(IntrinsicsRunOptions opt, Action<RunProgress> report = null,
            Func<bool> cancel = null)
        {
            if (opt == null)
            {
                opt = new IntrinsicsRunOptions();
            }

            if (opt.Script == null || opt.Script.Count == 0)
            {
                opt.Script = IntrinsicsPoseScript.Build();
            }

            var ctx = new ChainRunContext(_env.Log, report, cancel);
            ctx.Result.Chain = CalibChainKind.Intrinsics;

            CalibTopology topo = _env.ReadTopology() ?? new CalibTopology();
            ctx.Session = ChainRunnerSupport.NewSession(_env, topo, CalibChainKind.Intrinsics, null);
            ctx.Result.Session = ctx.Session;

            // ═══════════ ① 规划 ═══════════
            ctx.Begin(ServiceStage.Plan);
            if (opt.Board == null)
            {
                ctx.Fail(ServiceStage.Plan,
                    CalibError.Create(CalibFailureKind.Internal, "没有标定板模型"),
                    "内参链必须有标定板模型（板就是尺子）。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.End(ServiceStage.Plan, WizardStepState.Ok,
                opt.Board.Describe() + "；共 " + opt.Script.Count + " 个姿态要拍");

            // ═══════════ ② 预检：板模型 + 内参起点 ═══════════
            ctx.Begin(ServiceStage.Precheck);

            if (!opt.Board.IsAvailable)
            {
                string msg = "标定板模型不可用：" + opt.Board.SearchHint;
                ctx.Fail(ServiceStage.Precheck, CalibError.Create(CalibFailureKind.FeatureNotFound, msg), msg);
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            if (_env.Camera == null)
            {
                string msg = "没有相机 → 取不到图，内参无从标定。";
                ctx.Fail(ServiceStage.Precheck, CalibError.Create(CalibFailureKind.Internal, msg), msg);
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            CameraIntrinsicsGuess guess = opt.Guess;
            if (guess == null)
            {
                guess = CameraIntrinsicsGuess.ForImage(_env.Camera.Width, _env.Camera.Height, 12.0, 3.45);
            }
            else
            {
                guess.Width = guess.Width > 0 ? guess.Width : _env.Camera.Width;
                guess.Height = guess.Height > 0 ? guess.Height : _env.Camera.Height;
            }

            ctx.Log("内参起点：" + guess.Describe());
            if (guess.FocalPx < _env.Camera.Width * 0.2)
            {
                ctx.Warn("焦距起点偏小：焦距约 " + guess.FocalPx.ToString("F0", CultureInfo.InvariantCulture)
                    + " px，而画面宽 " + _env.Camera.Width + " px —— 视场会宽得离谱。"
                    + "起点离谱会让迭代不收敛，请核对标称焦距与像元尺寸。");
            }

            ctx.End(ServiceStage.Precheck, WizardStepState.Ok, guess.Describe());

            // ═══════════ ③④ 准备 + 逐姿态取图 ═══════════
            ctx.Begin(ServiceStage.Prepare);
            OpResult trig = _env.Camera.ConfigureSoftwareTrigger();
            if (trig != null && !trig.Ok)
            {
                ctx.End(ServiceStage.Prepare, WizardStepState.Failed, trig.Message);
                ctx.Fail(ServiceStage.Prepare, trig.ToError(),
                    "相机没切到软触发。★ 内参标定必须「本点新帧」：连续自由流下可能拿到上一张姿态的图，"
                    + "而图与姿态错配会让标定结果系统性偏移，且完全查不出来。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.End(ServiceStage.Prepare, WizardStepState.Ok, "软触发已就绪");

            ctx.Begin(ServiceStage.Sample);
            HalconBoardDetector detector = null;
            var framesDir = new List<string>();
            try
            {
                detector = new HalconBoardDetector(opt.Board, guess);
            }
            catch (Exception ex)
            {
                ctx.End(ServiceStage.Sample, WizardStepState.Failed, ex.Message);
                ctx.Fail(ServiceStage.Sample, CalibError.Create(CalibFailureKind.Internal, ex.Message),
                    "初始化标定数据失败。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            // ★ 资源释放必须晚于解算：Solve() 要读 calib_data 里的位姿与观测，
            //   先 Dispose 就会在标定中途把句柄抽掉（表现为 #8451 或空结果）。
            try
            {
                for (int i = 0; i < opt.Script.Count; i++)
                {
                    if (cancel != null && cancel())
                    {
                        ctx.Result.Cancelled = true;
                        ctx.End(ServiceStage.Sample, WizardStepState.Skipped, "用户取消");
                        ChainRunnerSupport.Finish(ctx, false,
                            CalibError.Create(CalibFailureKind.UserCancelled, "用户取消"));
                        return ctx.Result;
                    }

                    PosePrompt p = opt.Script[i];
                    CaptureOne(ctx, opt, detector, p, i, framesDir);
                    if (ctx.Result.Cancelled)
                    {
                        ctx.End(ServiceStage.Sample, WizardStepState.Skipped, "用户取消");
                        ChainRunnerSupport.Finish(ctx, false,
                            CalibError.Create(CalibFailureKind.UserCancelled, "用户取消"));
                        return ctx.Result;
                    }
                }

                return Finish(ctx, opt, detector, topo, guess, framesDir);
            }
            finally
            {
                try
                {
                    detector.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 告诉（仿真的）相机"这一张要拍第几个姿态"。真机上是空操作 —— 板是操作员摆的。
        ///
        /// ★ 重拍时必须重指定<b>同一个</b>序号：否则仿真相机会自己往前走一格，于是
        ///   "提示说的是第 3 张、实际渲染的是第 4 个姿态"。这种错位极难查，因为
        ///   残差、κ、主点全都正常，唯一露出来的地方是覆盖度判据说"一张都没斜"，
        ///   很容易被误读成"操作员没摆好"，然后去改提示文案 —— 方向完全错。
        /// </summary>
        private void PinPose(int index)
        {
            var scripted = _env.Camera as IPoseScriptedCamera;
            if (scripted != null)
            {
                scripted.SelectPose(index);
            }
        }

        /// <summary>
        /// 摆一张、拍一张。★ 这里把"人话提示"做足：告诉操作员怎么摆、失败了为什么、
        /// 要不要重拍。内参链的成败九成在这一步的手感上，不在算法上。
        /// </summary>
        private void CaptureOne(ChainRunContext ctx, IntrinsicsRunOptions opt,
            HalconBoardDetector detector, PosePrompt p, int index,
            List<string> framesDir)
        {
            int total = opt.Script.Count;
            for (int attempt = 1; attempt <= Math.Max(1, opt.MaxRetriesPerPose); attempt++)
            {
                ctx.Progress(ServiceStage.Sample, (index + (attempt - 1.0) / opt.MaxRetriesPerPose) / total,
                    "第 " + p.Index + "/" + total + " 张：" + p.Title, index + 1, total);

                string head = "第 " + p.Index + " / " + total + " 张：" + p.Title
                    + (attempt > 1 ? "（第 " + attempt + " 次尝试）" : string.Empty);

                if (!p.Required)
                {
                    if (!_env.Prompt.Confirm(head, p.Hint + "\n\n这一张是选做的，跳过也不影响结果。摆好了吗？"))
                    {
                        ctx.Log("跳过选拍姿态：" + p.Title);
                        return;
                    }
                }
                else if (!_env.Prompt.Confirm(head, p.Hint + "\n\n摆好了点「确定」，我会立刻取一帧。"))
                {
                    ctx.Log("操作员跳过姿态：" + p.Title);
                    return;
                }

                CalibError grabError;
                PinPose(index);
                byte[] raw = _env.Camera.GrabFrame(opt.GrabTimeoutMs, out grabError);
                if (raw == null)
                {
                    _env.Prompt.Warn("取图失败", grabError == null ? "取图为空。" : grabError.ToString());
                    if (attempt >= opt.MaxRetriesPerPose)
                    {
                        ctx.Add(ChainIssue.Warn("GRAB", "姿态「" + p.Title + "」取图连续失败，已跳过。",
                            ServiceStage.Sample));
                        return;
                    }

                    continue;
                }

                if (opt.SaveFrames && _env.Store != null)
                {
                    try
                    {
                        byte[] pgm = PgmCodec.Encode(raw, _env.Camera.Width, _env.Camera.Height);
                        string saved = _env.Store.SaveFrame(ctx.Session.SessionId, p.Index, pgm, ".pgm");
                        if (!string.IsNullOrEmpty(saved))
                        {
                            framesDir.Add(saved);
                            ctx.Result.ExportDir = ctx.Result.ExportDir ?? System.IO.Path.GetDirectoryName(saved);
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.Warn("留帧失败（不影响标定）：" + ex.Message);
                    }
                }

                string err;
                BoardViewFact fact;
                bool ok = detector.TryAddViewRaw(p.Title, raw, _env.Camera.Width, _env.Camera.Height,
                    out err, out fact);

                if (!ok)
                {
                    ctx.Warn("第 " + p.Index + " 张没成功：" + err);

                    // ★ 失败路径更要显示：操作员这时候唯一的疑问就是"我这张到底拍成什么样"。
                    //   不显示的话，他能看到的只有一句"没找到板"，而画面里可能明明有板 ——
                    //   于是"软件坏了"与"板太小"这两件事在现场无法区分。
                    RaiseBoardNotFoundOverlay(opt, p, raw, err, total);

                    if (attempt >= opt.MaxRetriesPerPose)
                    {
                        ctx.Add(ChainIssue.Warn("BOARD_NOT_FOUND",
                            "姿态「" + p.Title + "」连续 " + attempt + " 次没找到板："
                            + (err ?? "未知原因") + "（这一张不计入标定）", ServiceStage.Sample));
                        _env.Prompt.Inform("这一张先跳过", "已连续 " + attempt + " 次没找到板，" + err
                            + "\n\n建议先解决它；如果只是这张姿态不好摆，继续往下拍也行 —— "
                            + "最后覆盖度会告诉你够不够。");
                        return;
                    }

                    if (!_env.Prompt.Confirm("这一张没找到板，再试一次？", err))
                    {
                        ctx.Add(ChainIssue.Warn("BOARD_NOT_FOUND",
                            "操作员放弃姿态「" + p.Title + "」：" + err, ServiceStage.Sample));
                        return;
                    }

                    continue;
                }

                fact.Label = "第 " + p.Index + " 张·" + p.Title;
                // ★ 这里**不打深度**：板位姿是待优化量，标定之前根本取不到（HALCON #8451），
                //   打出来只会是 NaN。这一刻真正拿得到、且真正有用的是"板在画面里多大" ——
                //   它既是找板成败的直接原因，也是操作员唯一能当场纠正的东西。
                ctx.Log(string.Format(CultureInfo.InvariantCulture,
                    "第 {0} 张 OK：检出 {1} 个 mark，板宽约占画面 {2:P0}（相邻间距 {3:F1} px），{4}",
                    p.Index, fact.MarkCount, fact.ApparentWidthFraction(_env.Camera.Width),
                    fact.ApparentSpacingPx,
                    HalconBoardDetector.OffCenterHint(fact.MeanRow, fact.MeanCol,
                        _env.Camera.Width, _env.Camera.Height)));

                // ★★ 板"太小"的告警 —— 这条不是锦上添花，它是"找不着板"的头号原因。
                //   HALCON 找板时会把"小于期望尺寸的 mark 当噪声整片剔掉"，而期望尺寸
                //   来自它自己猜的板在哪儿。于是板一远，检出率是**断崖式**掉到 0：
                //   实测本相机 231 mm → 板宽 633 px（占画面 49%）检出 818 个；
                //   240 mm → 609 px（47.6%）检出 0 个。用户对此完全无从理解
                //   （图里明明看得见板），所以必须翻译成一句能执行的话。
                double frac = fact.ApparentWidthFraction(_env.Camera.Width);
                if (frac > 0.0 && frac < MinApparentWidthFraction)
                {
                    string advice = string.Format(CultureInfo.InvariantCulture,
                        "板在画面里偏小（宽度只占 {0:F0}%，建议 ≥{1:F0}%）。"
                        + "把板往镜头方向挪近一些再重拍 —— 板太小的时候找板算法会把 mark 当噪声整片剔掉，"
                        + "这张即使现在找到了，后面的姿态也容易整片失败。",
                        frac * 100.0, MinApparentWidthFraction * 100.0);
                    ctx.Warn("第 " + p.Index + " 张" + advice);
                    if (frac < MinApparentWidthFractionHard)
                    {
                        _env.Prompt.Inform("板太小了", advice);
                    }
                }

                // ★ 把"这一帧看得出什么"交给宿主显示 —— 上面那些判断都只是文字，
                //   而操作员真正需要的是"亲眼看到检出结果"，才能分辨"板太小"与"软件没在找"。
                RaiseBoardOverlay(opt, p, raw, fact, total);

                return;
            }

        }

        /// <summary>
        /// 组装并抛出"这一帧板检出"的叠加载荷。
        /// ★ 本方法<b>只做搬运</b>：组装交给 <see cref="BoardOverlayBuilder"/>（纯函数、可离线断言），
        ///   判读只在上面那一处做 —— 任何一处重复判读都会变成两套口径。
        /// </summary>
        private void RaiseBoardOverlay(IntrinsicsRunOptions opt, PosePrompt p,
            byte[] raw, BoardViewFact fact, int total)
        {
            Action<CalibVisualizationRequest> sink = opt.Visualize;
            if (sink == null)
            {
                return;
            }

            int expect = opt.Board == null ? 0 : opt.Board.MarkRows * opt.Board.MarkCols;
            BoardOverlayPayload board = BoardOverlayBuilder.FromFact(fact, expect,
                _env.Camera.Width, _env.Camera.Height, raw, FrameLabel(p, total),
                MinApparentWidthFraction);

            sink(new CalibVisualizationRequest
            {
                Title = board.Label + " · 板检出叠加",
                Board = board
            });
        }

        /// <summary>
        /// 没找到板时也抛一次载荷：<b>画面要显示，只是一个 mark 都不画</b>。
        /// 这时候操作员的疑问不是"检出率多少"，而是"我这张到底拍成了什么" ——
        /// 只给一句"没找到板"，他连"图糊了 / 板出界了 / 板太小"都分不出来。
        /// </summary>
        private void RaiseBoardNotFoundOverlay(IntrinsicsRunOptions opt, PosePrompt p,
            byte[] raw, string error, int total)
        {
            Action<CalibVisualizationRequest> sink = opt.Visualize;
            if (sink == null)
            {
                return;
            }

            BoardOverlayPayload board = BoardOverlayBuilder.NotFound(
                raw, _env.Camera.Width, _env.Camera.Height, FrameLabel(p, total), error);

            sink(new CalibVisualizationRequest
            {
                Title = board.Label + " · 板检出叠加（没找到板）",
                Board = board
            });
        }

        private static string FrameLabel(PosePrompt p, int total)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "第 {0}/{1} 张 · {2}", p.Index, total, p.Title);
        }

        /// <summary>第 ⑤~⑧ 步：解算 / 校验 / 判读 / 导出。</summary>
        private ChainRunResult Finish(ChainRunContext ctx, IntrinsicsRunOptions opt,
            HalconBoardDetector detector, CalibTopology topo, CameraIntrinsicsGuess guess,
            List<string> framesDir)
        {
            ctx.End(ServiceStage.Sample, WizardStepState.Ok,
                "成功找到板 " + detector.AcceptedPosCount + " / " + opt.Script.Count + " 张");

            if (detector.AcceptedPosCount < 3)
            {
                string msg = "成功找到板的只有 " + detector.AcceptedPosCount + " 张（至少要 3 张）。"
                           + "先把「找板」解决掉 —— 常见原因：板文件与实物不是同一块、曝光不合适、"
                           + "板没完整进画面。";
                ctx.Fail(ServiceStage.Solve, CalibError.Create(CalibFailureKind.SolveFailed, msg), msg);
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            // ═══════════ ⑤ 解算 ═══════════
            ctx.Begin(ServiceStage.Solve);
            IntrinsicsSolveOutcome outcome = detector.Solve();
            ctx.Result.Intrinsics = outcome.Success ? outcome.Intrinsics : null;
            ctx.Result.IntrinsicsOutcome = outcome;

            if (!outcome.Success)
            {
                ctx.End(ServiceStage.Solve, WizardStepState.Failed,
                    outcome.Error == null ? "解算失败" : outcome.Error.Message);
                ctx.Fail(ServiceStage.Solve, outcome.Error, "相机标定失败。");
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            outcome.EvaluateResiduals(opt.PerViewWarnPx, opt.PerViewBlockPx);
            outcome.GeometryTrace.AddRange(detector.ViewGeometryTrace);
            outcome.ViewErrors.AddRange(detector.ViewErrors);
            ctx.End(ServiceStage.Solve, WizardStepState.Ok, outcome.Describe());

            // ═══════════ ⑥ 质量校验：覆盖度（几何判据）═══════════
            ctx.Begin(ServiceStage.Verify);
            IntrinsicsCoverage cov = IntrinsicsGeometry.Evaluate(outcome.Views, opt.Coverage);
            outcome.Coverage = cov;
            outcome.Intrinsics.PoseCoverageWarning = cov.Sufficient
                ? null
                : string.Join("；", cov.Hints.ToArray());

            for (int i = 0; i < cov.Hints.Count; i++)
            {
                ctx.Log((cov.Sufficient ? "覆盖度 OK：" : "覆盖度不足：") + cov.Hints[i]);
            }

            if (!cov.Sufficient)
            {
                var sb = new StringBuilder("姿态覆盖度不够，标定出的数不可信：");
                for (int i = 0; i < cov.Hints.Count; i++)
                {
                    sb.AppendLine().Append("  · ").Append(cov.Hints[i]);
                }

                if (opt.BlockWhenCoveragePoor)
                {
                    bool proceed = _env.Prompt.Confirm("覆盖度不够，还要用这份结果吗？",
                        sb.ToString() + "\n\n★ 覆盖度不够时，焦距、畸变、主点之间会互相「顶账」——"
                        + "残差可能依然很小，但每个数都不可信。\n"
                        + "建议补拍后重跑。确实要用（例如只想先看一眼畸变量级）就点确定，产物里会写明告警。");
                    if (!proceed)
                    {
                        string msg = "姿态覆盖度不足，已停止（未产出可发布的结果）。" + sb.ToString();
                        for (int i = 0; i < cov.Hints.Count; i++)
                        {
                            ctx.Result.Add(ChainIssue.Warn("COVERAGE", cov.Hints[i], ServiceStage.Verify));
                        }

                        ctx.Fail(ServiceStage.Verify, CalibError.Create(CalibFailureKind.QualityGate, msg), msg);
                        ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                        return ctx.Result;
                    }

                    ctx.Warn("操作员选择在覆盖度不足的情况下继续 —— 产物会带明确告警。");
                }

                for (int i = 0; i < cov.Hints.Count; i++)
                {
                    ctx.Result.Add(ChainIssue.Warn("COVERAGE", cov.Hints[i], ServiceStage.Verify));
                }
            }

            if (!double.IsNaN(outcome.RmsePx) && outcome.RmsePx > opt.MaxAcceptableRmsePx)
            {
                string msg = string.Format(CultureInfo.InvariantCulture,
                    "整体反投影 RMSE {0:F3} px 超过上限 {1:F3} px —— 这不是「精度差一点」，"
                    + "而是链路上有系统性错误（板文件与实物不符 / 板在拍摄中动过 / mark 配错）。"
                    + "先用单张误差定位是哪几张，再决定重拍还是换板。",
                    outcome.RmsePx, opt.MaxAcceptableRmsePx);
                ctx.Fail(ServiceStage.Verify, CalibError.Create(CalibFailureKind.QualityGate, msg), msg);
                ChainRunnerSupport.Finish(ctx, false, ctx.Result.Error);
                return ctx.Result;
            }

            ctx.End(ServiceStage.Verify, WizardStepState.Ok, cov.Describe());

            // ═══════════ ⑦ 判读（逐张 + 畸变量级）═══════════
            ctx.Begin(ServiceStage.Visualize);

            // ★★ "去畸变后会好多少"必须是<b>算出来的毫米</b>，不是"κ 有多大"：
            //   κ 对操作员毫无意义，毫米才能拿去和工位精度要求比（决策 3）。
            //   尺度取自标定板自身的 mark 间距（物理间距 ÷ 视在像素间距），
            //   所以内参链自己就能给出这个数，不需要等九点数据。
            var camModel = DivisionCameraModel.FromIntrinsics(
                outcome.Intrinsics, outcome.FocusM, outcome.PixelPitchM);
            DistortionImpactAssessment impact = DistortionImpactAnalyzer.MeasureBoard(
                outcome.Views, camModel,
                opt.Board == null ? 0.0 : opt.Board.SpacingMm,
                opt.Board == null ? null : opt.Board.DisplayName);

            ctx.End(ServiceStage.Visualize, WizardStepState.Ok,
                string.IsNullOrEmpty(impact.Verdict) ? "畸变影响：没算出来" : impact.Verdict);

            if (!impact.Measured)
            {
                // ★ 算不出来必须<b>明说</b>，不能留空 —— 留空会被读成"没有影响"。
                ctx.Result.Add(ChainIssue.Warn("DISTORTION_IMPACT_UNMEASURED",
                    "畸变影响没能量化：" + (impact.Reason ?? "未知原因"), ServiceStage.Visualize));
            }

            for (int i = 0; i < outcome.ResidualNotes.Count; i++)
            {
                ctx.Result.Add(ChainIssue.Warn("REPROJ", outcome.ResidualNotes[i], ServiceStage.Visualize));
            }

            // ═══════════ ⑧ 导出与发布 ═══════════
            ctx.Begin(ServiceStage.Export);
            CalibDiagnostics d = ctx.Result.Diagnostics ?? new CalibDiagnostics();
            d.ReprojectionRmsPx = outcome.RmsePx;
            d.ReprojectionMaxPx = outcome.MaxPoseRmsPx;
            d.DistortionImpact = impact;      // ★ 决策 3 的量级证据随产物一起走
            ctx.Result.Diagnostics = d;

            var export = new CalibExport
            {
                SourceSessionId = ctx.Session.SessionId,
                Chain = CalibChainKind.Intrinsics,
                StationCode = topo.StationCode,
                CameraSlotKey = topo.CameraSlotKey,
                Handedness = ChainRunnerSupport.HandednessText(topo.Hand),
                CameraMount = ChainRunnerSupport.MountText(topo.CameraMount),
                Diagnostics = d,

                FocalLengthPx = outcome.Intrinsics.FocalLengthPx,
                PrincipalPointPx = outcome.Intrinsics.PrincipalPointPx,

                // ★ 米制焦距 + 像元：消费端拼 HALCON campar 就要这两个（focus/sx/sy 单位都是米），
                //   光给"焦距 3431 px"是喂不进 area_scan_division 的。
                FocalLengthM = outcome.FocusM,
                PixelPitchM = outcome.PixelPitchM,

                // ★ 存 HALCON 原生口径（1/m²）—— 消费端要拿去喂 area_scan_division 的 campar。
                //   归一化口径另存一份，避免下游有人按 Brown 系数去用它。
                Distortion = outcome.Intrinsics.Distortion,
                DistortionNormalizedK1 = outcome.KappaNormalized,

                ImageSize = outcome.Intrinsics.ImageSize,
                ReprojectionErrorPx = outcome.RmsePx,

                BoardDescription = opt.Board.Describe(),
                Board = opt.Board.Kind,

                FramesDir = framesDir.Count > 0 ? System.IO.Path.GetDirectoryName(framesDir[0]) : null,
                ProducedBy = opt.Common == null ? "VisualCalibTool" : opt.Common.ProducedBy
            };

            // ★ 产物回挂到 outcome：界面要显示"去畸变后会好多少"，只能从这里取。
            //   只落盘不回传的话，量化算得再准 UI 也拿不到（这块曾经就是断的）。
            outcome.Export = export;

            ChainRunnerSupport.ExportAndPublish(ctx, opt.Common, _env, export);
            ctx.End(ServiceStage.Export, WizardStepState.Ok,
                string.IsNullOrEmpty(ctx.Result.ExportDir) ? "未落文件" : ctx.Result.ExportDir);

            WriteIntrinsicsArtifact(ctx, export, opt);
            ChainRunnerSupport.Finish(ctx, true);
            ctx.Log(ctx.Result.Summary());
            return ctx.Result;
        }

        /// <summary>
        /// 附加产物 <c>&lt;session&gt;.intrinsics.json</c>。
        ///
        /// ★ 为什么单独一份：<c>.calib.json</c> 回答"标定产物怎么消费"，
        ///   <c>intrinsics.json</c> 回答"这台相机的模型是什么"（campar 都按 HALCON 顺序排好了）。
        ///   宿主拿到它就能直接调 <c>change_radial_distortion_cam_offline</c> /
        ///   <c>gen_radial_distortion_map</c>，不必再从像素焦距反推像元尺寸。
        ///
        /// ★ 落盘失败只告警：附加产物不写进去，标定结果本身依然有效。
        /// </summary>
        private void WriteIntrinsicsArtifact(ChainRunContext ctx, CalibExport export, IntrinsicsRunOptions opt)
        {
            if (opt != null && opt.Common != null && !opt.Common.ExportFiles)
            {
                return;
            }

            var extras = _env == null ? null : _env.Store as ICalibStoreExtras;
            if (extras == null)
            {
                ctx.Log("当前存储不支持附加产物（未实现 ICalibStoreExtras）—— 跳过 intrinsics.json。");
                return;
            }

            try
            {
                string path = extras.SaveExtraFile(
                    ctx.Session == null ? export.SourceSessionId : ctx.Session.SessionId,
                    "intrinsics.json",
                    CalibExportJson.WriteIntrinsics(export, true));
                if (string.IsNullOrEmpty(path))
                {
                    ctx.Result.Add(ChainIssue.Warn("INTRINSICS_ARTIFACT",
                        "intrinsics.json 没写成功（存储层返回空路径）。", ServiceStage.Export));
                }
                else
                {
                    ctx.Log("内参产物已导出：" + path);
                }
            }
            catch (Exception ex)
            {
                ctx.Result.Add(ChainIssue.Warn("INTRINSICS_ARTIFACT",
                    "intrinsics.json 落盘失败：" + ex.Message, ServiceStage.Export));
            }
        }
    }
}
