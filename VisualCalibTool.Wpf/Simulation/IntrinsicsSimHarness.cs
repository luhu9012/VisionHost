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
    /// <summary>一次内参仿真闭环的运行现场（供断言与复盘用）。</summary>
    public sealed class IntrinsicsSimRun
    {
        public BoardModel Board;
        public SimulatedBoardCamera Camera;
        public SimulatedEnvironment Env;
        public AutoYesUserPrompt Prompt;
        public ChainRunResult Result;
        public string StoreRoot;
        public double TrueKappa;
        public double TrueFocusM;
        public List<string> GeometryTrace = new List<string>();

        /// <summary>
        /// 逐帧可视化载荷（内参链"边摆边画"那条路真正抛出来的东西）。
        /// ★ 留着它才能断言"界面上那片叠加不是空的"—— 只断言"链跑通了"证明不了这件事。
        /// </summary>
        public List<CalibVisualizationRequest> Overlays;
    }

    /// <summary>
    /// ★★ 内参链的<b>端到端仿真验收</b>：走产品代码的那条路
    /// （<c>BoardModel → HalconBoardDetector → IntrinsicsRunner → 覆盖度判据 → 导出</c>），
    /// 但图是 <c>sim_caltab</c> 合成的、姿态是已知的、κ 是我们自己注入的。
    ///
    /// ★ 与开发期探针 <c>IntrinsicsLab</c> 的分工（两者都要留）：
    ///   · <b>探针</b>验的是"HALCON 这套算子能不能用、坑在哪" —— 面向算子；
    ///   · <b>本闭环</b>验的是"<b>我们写的那条链</b>对不对、判据拦不拦得住" —— 面向产品。
    ///   探针只落在开发期运行器里；本闭环落在被测类库里，跟着自检一起跑，
    ///   所以<b>产品代码从此有了可离线复跑的回归</b>。
    ///
    /// 验收口径直接对齐设计文档 §14 第 9 步，但把那个量纲写错的验收值改正过来：
    ///   · 文档写的是 <c>k1 = −0.15</c>（归一化口径）；
    ///   · HALCON 要的是 <c>κ = k1 / f² ≈ −1070</c>（1/m²，见 <see cref="IntrinsicsGeometry.KappaFromNormalized"/>）。
    ///   这里<b>两个口径都断言</b>：注入 κ 解回 κ，再换算回 k1 与文档值比对 ——
    ///   这样「文档的验收标准」和「实际能跑的口径」同时对得上，不会再退化成自证。
    /// </summary>
    public static class IntrinsicsSimHarness
    {
        /// <param name="baseDistanceM">
        /// 基准工作距离（米）。★ <b>不能随手挑</b>：HALCON 找板有个“视在尺寸”下限 ——
        /// 实测本相机在 231 mm 时板宽只占画面 49%，已经贴着临界；再远 9 mm（240 mm，47.6%）
        /// 检出率就从 98% <b>断崖式掉到 0</b>。所以这里取 170 mm（板宽约占 62%），
        /// 让脚本要求的 ±25 mm 整体落在包络内 —— 见 <c>IntrinsicsLab.WorkingRangeSweep</c> 的实测曲线。
        /// </param>
        public static IntrinsicsSimRun Run(SelfCheckReport report, double baseDistanceM = 0.170)
        {
            if (report == null)
            {
                throw new ArgumentNullException("report");
            }

            var run = new IntrinsicsSimRun();

            // ── ① 板模型：先把「尺子」验了 ──
            BoardModel board = BoardModel.Calplate("calplate_40mm.cpd");
            run.Board = board;

            report.Check(board.IsAvailable,
                "内参仿真：40 mm 官方标定板模型能解析到（找不到就整条链没法验）",
                board.Describe());

            if (!board.IsAvailable)
            {
                return run;
            }

            report.Check(board.MarkRows == 27 && board.MarkCols == 31 && board.ExpectedMarks == 837,
                "内参仿真：板模型的行列数与理论 mark 总数对得上（27 × 31 = 837）",
                string.Format(CultureInfo.InvariantCulture,
                    "rows={0} cols={1} marks={2}，间距 {3:F5} mm，外形 {4:F2} mm",
                    board.MarkRows, board.MarkCols, board.ExpectedMarks, board.SpacingMm, board.PlateSizeMm));

            // ★ 间距必须来自板文件里写明的键行，不能拿"板宽 ÷ (列数−1)"去算
            report.Check(System.Math.Abs(board.SpacingMm - 1.29032) < 0.001,
                "★ 内参仿真：mark 间距取的是板文件里的权威值（不是板宽反算的近似）",
                string.Format(CultureInfo.InvariantCulture,
                    "间距 {0:F5} mm（板文件真值 1.29032）；若按板宽/列数−1 会算成 {1:F5} mm —— "
                    + "数字看着也像那么回事，所以必须断言",
                    board.SpacingMm, board.PlateSizeMm > 0 ? board.PlateSizeMm / (board.MarkCols - 1) : 0.0));

            // ── ② 铺仿真：真值相机 + 已知姿态 ──
            run.TrueFocusM = 0.01184;
            run.TrueKappa = IntrinsicsGeometry.KappaFromNormalized(-0.15, run.TrueFocusM);

            var cam = new SimulatedBoardCamera(board, 1280, 1024, run.TrueFocusM, run.TrueKappa, 3.45e-6);
            List<double[]> plan = SimulatedBoardCamera.DefaultPosePlan(baseDistanceM);
            for (int i = 0; i < plan.Count; i++)
            {
                double[] p = plan[i];
                cam.AddPose(p[0], p[1], p[2], p[3], p[4], p[5]);
            }

            run.Camera = cam;

            string storeRoot;
            try
            {
                storeRoot = Path.Combine(Path.GetTempPath(),
                    "vct_intr_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(storeRoot);
            }
            catch (Exception)
            {
                storeRoot = Path.Combine(Path.GetTempPath(), "vct_intr");
            }

            run.StoreRoot = storeRoot;

            var prompt = new AutoYesUserPrompt();
            run.Prompt = prompt;

            var env = new SimulatedEnvironment(new SimulatedWorld(), cam,
                new FileSystemCalibStore(storeRoot), new SimpleCalibLog(), prompt);
            run.Env = env;

            // 起点故意与真值不同（标称焦距 + 图像中心 + 零畸变），否则「解回来」没有说服力
            var guess = CameraIntrinsicsGuess.ForImage(1280, 1024, 12.0, 3.45);
            var opt = new IntrinsicsRunOptions
            {
                Board = board,
                Guess = guess,
                Common = new ChainRunOptions
                {
                    ExportFiles = true,
                    PublishToHost = false,      // 仿真里没有宿主 → 只落文件
                    ProducedBy = "IntrinsicsSimHarness"
                }
            };

            // ★ 收集逐帧可视化载荷：内参链"边摆边画"这条路必须被真的走一遍。
            //   只断言"链跑通了"是不够的 —— 界面上那片叠加完全可能是空的，
            //   而空叠加与"没接线"在操作员眼里长得一模一样。
            var overlays = new List<CalibVisualizationRequest>();
            opt.Visualize = req => overlays.Add(req);
            run.Overlays = overlays;

            // ── ③ 跑产品代码那条路 ──
            ChainRunResult r;
            try
            {
                r = new IntrinsicsRunner(env).Run(opt);
            }
            catch (Exception ex)
            {
                report.Check(false, "内参仿真：链能跑完不抛异常",
                    "抛异常：" + ex.GetType().Name + "：" + ex.Message);
                return run;
            }

            run.Result = r;

            report.Check(r.Success,
                "内参仿真：整条链跑通（摆板引导 → 找板 → 标定 → 覆盖度 → 导出）",
                r.Success ? r.Summary() : DescribeFailure(r));

            if (!r.Success)
            {
                return run;
            }

            IntrinsicsSolveOutcome o = r.IntrinsicsOutcome;
            report.Check(o != null, "内参仿真：解算产物被带出来（界面与产品都要用）",
                o == null ? "IntrinsicsOutcome 为 null" : o.Describe());

            if (o == null || o.Intrinsics == null)
            {
                return run;
            }

            run.GeometryTrace.AddRange(o.GeometryTrace);

            // ── ④ 覆盖度：9 张姿态里 6 张倾斜 ≥20°、跨度 50 mm、4 个方向 ──
            IntrinsicsCoverage cov = o.Coverage;
            report.Check(cov != null && cov.Sufficient,
                "★ 内参仿真：覆盖度判据判定「够」（工具让用户摆的那套动作，自己得判得过）",
                cov == null ? "没有覆盖度结果" : cov.Describe());

            report.Check(o.Intrinsics.UsedPoseCount == plan.Count,
                "内参仿真：按脚本拍的每一张都进了解算（没有静默丢掉）",
                string.Format(CultureInfo.InvariantCulture, "进解算 {0} / 脚本 {1} 张，取图 {2} 帧",
                    o.Intrinsics.UsedPoseCount, plan.Count, cam.GrabCount)
                + (o.Intrinsics.UsedPoseCount == plan.Count
                    ? string.Empty
                    : "\n      计划（注入的位姿）：" + string.Join(" ", PoseTexts(cam).ToArray())
                      + "\n      没进解算的原因：" + (o.ViewErrors.Count == 0
                          ? "<没记录到原因 —— 这本身是缺陷：失败必须留话>"
                          : "\n        " + string.Join("\n        ", o.ViewErrors.ToArray()))));

            // ── ④b 视在尺寸：脚本要求的每一张都必须让板够大 ──
            // ★ 这条是从一次真实故障里长出来的：基准位取 231 mm 时，+25 mm 那两张
            //   （256 mm）全部找不着板、报 #8397，而所有数值指标都正常 —— 只有覆盖度
            //   说“斜的张数不够”。把“板够不够大”单独断言之后，这类失败会当场指到尺寸上，
            //   而不是让人去怀疑摆板手法。
            double worstFrac = 1.0;
            string worstLabel = "-";
            for (int i = 0; i < o.Views.Count; i++)
            {
                double fr = o.Views[i].ApparentWidthFraction(cam.Width);
                if (fr > 0.0 && fr < worstFrac)
                {
                    worstFrac = fr;
                    worstLabel = o.Views[i].Label;
                }
            }

            report.Check(worstFrac >= IntrinsicsRunner.MinApparentWidthFraction,
                "★ 内参仿真：脚本要求的每一张都让板足够大（视在宽度 ≥ "
                + IntrinsicsRunner.MinApparentWidthFraction.ToString("P0", CultureInfo.InvariantCulture)
                + "，低于它找板会断崖式失效）",
                string.Format(CultureInfo.InvariantCulture,
                    "最小的那一张「{0}」板宽占画面 {1:P0}；判据下限 {2:P0}（实测 47.6% 时检出率掉到 0）",
                    worstLabel, worstFrac, IntrinsicsRunner.MinApparentWidthFraction));

            // ── ④c 逐帧可视化：内参链"边摆边画"这条路真的被走到 ──
            // ★ 为什么单独断言：叠加是这套工具唯一"操作员当场能看懂"的输出，
            //   而它整条路（runner → 回调 → 宿主绘制）断在任何一环，界面上都是**静默空白** ——
            //   "没接线"与"这一帧没检出"在屏幕上长得一模一样，只有断言分得开。
            VerifyBoardOverlays(report, run, o, cam);

            // ── ⑤ 数值还原：κ 与焦距 ──
            double dk = System.Math.Abs(o.KappaMetric - run.TrueKappa) / System.Math.Abs(run.TrueKappa);
            report.Check(dk < 0.05,
                "★★ 内参仿真：注入的 κ 能被解回来（相对 5% 以内）—— 这是内参链的离线验收标准",
                string.Format(CultureInfo.InvariantCulture,
                    "注入 κ = {0:F2}（= 归一化 k1 −0.15）→ 解出 {1:F2}，相对差 {2:F2}%",
                    run.TrueKappa, o.KappaMetric, dk * 100.0));

            double k1Back = IntrinsicsGeometry.NormalizedFromKappa(o.KappaMetric, o.FocusM);
            report.Check(System.Math.Abs(k1Back - (-0.15)) < 0.02,
                "★ 内参仿真：换算回归一化口径后，与设计文档 §14 第 9 步写的 k1 = −0.15 相符",
                string.Format(CultureInfo.InvariantCulture,
                    "解出的 κ = {0:F2}（1/m²）÷ f² 换算得 k1 = {1:F4}（文档要求 ≈ −0.15）",
                    o.KappaMetric, k1Back));

            double df = System.Math.Abs(o.FocusM - run.TrueFocusM) / run.TrueFocusM;
            report.Check(df < 0.02,
                "内参仿真：焦距能解回来（相对 2% 以内）",
                string.Format(CultureInfo.InvariantCulture,
                    "真值 {0:F6} m → 解出 {1:F6} m，偏差 {2:F3}%（平面目标下焦距只能约束到 1% 量级，这是方法的极限）",
                    run.TrueFocusM, o.FocusM, df * 100.0));

            double dcx = System.Math.Abs(o.Intrinsics.PrincipalPointPx[0] - cam.TrueCx);
            double dcy = System.Math.Abs(o.Intrinsics.PrincipalPointPx[1] - cam.TrueCy);
            report.Check(dcx < 3.0 && dcy < 3.0,
                "内参仿真：主点能解回来（故意把真值主点偏离图像中心，仍能还原到 3 px 内）",
                string.Format(CultureInfo.InvariantCulture,
                    "真值 ({0:F1}, {1:F1}) → 解出 ({2:F1}, {3:F1})，差 ({4:F2}, {5:F2}) px",
                    cam.TrueCx, cam.TrueCy, o.Intrinsics.PrincipalPointPx[0], o.Intrinsics.PrincipalPointPx[1],
                    dcx, dcy));

            report.Check(o.RmsePx < 0.3,
                "内参仿真：整体重投影 RMSE < 0.3 px（与「重拍」判据同口径）",
                string.Format(CultureInfo.InvariantCulture, "RMSE {0:F4} px，单张最差 {1:F4} px（{2}）",
                    o.RmsePx, o.MaxPoseRmsPx, o.WorstPoseName));

            // ── ⑥ 产物：口径必须写清楚 ──
            CalibExport e = r.Export;
            report.Check(e != null && e.Distortion != null && e.Distortion.Length > 0
                         && System.Math.Abs(e.Distortion[0] - o.KappaMetric) < 1e-6,
                "内参仿真：产物里的 distortion 是 HALCON 口径的 κ（消费端要拿它喂 campar）",
                e == null || e.Distortion == null
                    ? "产物或 distortion 为空"
                    : string.Format(CultureInfo.InvariantCulture, "distortion[0] = {0:F2}，κ = {1:F2}",
                        e.Distortion[0], o.KappaMetric));

            report.Check(e != null
                         && System.Math.Abs(e.DistortionNormalizedK1 - k1Back) < 1e-9
                         && e.FocalLengthPx != null && e.PrincipalPointPx != null,
                "★ 内参仿真：产物同时写出归一化口径 k1（两个口径并存，防下游按错口径用）",
                e == null
                    ? "产物为空"
                    : string.Format(CultureInfo.InvariantCulture,
                        "distortionNormalizedK1 = {0:F4}；focalPx = ({1:F1}, {2:F1})；主点 = ({3:F1}, {4:F1})",
                        e.DistortionNormalizedK1, e.FocalLengthPx[0], e.FocalLengthPx[1],
                        e.PrincipalPointPx[0], e.PrincipalPointPx[1]));

            report.Check(o.DistortionShiftPx(800.0) > 1.0,
                "★ 内参仿真：畸变量级是可感知的（画面角上位移 > 1 px，不是「等于没有畸变」）",
                string.Format(CultureInfo.InvariantCulture,
                    "半径 800 px 处位移 {0:F2} px —— 若换成把归一化 k1 直接当 κ 用，这个数会变成 0.001 px 级别",
                    o.DistortionShiftPx(800.0)));

            // ── ⑥b 「去畸变后会好多少」必须是个毫米数（决策 3 的交付物）──
            // ★ 为什么单列一条：κ = −1070 对操作员毫无意义，"边角偏 0.35 mm"才能拿去
            //   和工位精度要求比。这个数以前只有一句声明（DistortionImpactAssessment 类），
            //   从来没被算出来过 —— 声明了却不算，等于没有。
            DistortionImpactAssessment imp = e == null || e.Diagnostics == null
                ? null
                : e.Diagnostics.DistortionImpact;

            report.Check(imp != null && imp.Measured,
                "★★ 内参仿真：产物带上「去畸变后会好多少」的量化（毫米级，决策 3 的证据）",
                imp == null
                    ? "产物里没有 distortionImpact —— 声明了却没算，等于没有"
                    : (imp.Measured ? imp.Verdict : "没测出来：" + imp.Reason));

            // ── ⑥d 量化结果必须能回到 outcome（界面那一屏全靠它取数）──
            // ★ 这条专治"只落盘、不回挂"：数算得再准、文件写得再全，
            //   UI 拿不到就等于没算 —— 对比视图曾经就是这么断的。
            DistortionImpactAssessment back = o.Export == null || o.Export.Diagnostics == null
                ? null
                : o.Export.Diagnostics.DistortionImpact;

            report.Check(back != null && back.Measured,
                "★★ 内参仿真：量化结果回挂到 outcome（界面「去畸变后会好多少」靠它取数）",
                o.Export == null
                    ? "outcome.Export 为空 —— 只落盘没回挂，界面那条路是断的"
                    : (back == null ? "Export 里没有诊断 / 量化" : "已回挂：" + back.Verdict));

            if (imp != null && imp.Measured)
            {
                report.Check(imp.MaxShiftMm > 0.0 && imp.MaxShiftPx > 0.0
                             && imp.CurveRadiusPx != null && imp.CurveShiftMm != null
                             && imp.CurveRadiusPx.Length == imp.CurveShiftMm.Length
                             && "board".Equals(imp.Source, StringComparison.Ordinal),
                    "★ 内参仿真：量化既有毫米数也有像素数，且带「位移随半径」曲线（源 = 标定板自身当尺子）",
                    string.Format(CultureInfo.InvariantCulture,
                        "覆盖半径 {0:F0} px ≈ {1:F1} mm，最大位移 {2:F2} px ≈ {3:F3} mm，标度 {4:F5} mm/px，曲线 {5} 点",
                        imp.WorstRadiusPx, imp.WorkRadiusMm, imp.MaxShiftPx, imp.MaxShiftMm,
                        imp.ScaleMmPerPx, imp.CurveRadiusPx.Length));

                // 标度必须与"板自身"算出来的一致 —— 防止拿错尺子（例如用检出外接框估板宽）
                double sum = 0.0;
                int cnt = 0;
                for (int i = 0; i < o.Views.Count; i++)
                {
                    if (o.Views[i].MarkCount > 0 && o.Views[i].ApparentSpacingPx > 0.0)
                    {
                        sum += board.SpacingMm / o.Views[i].ApparentSpacingPx;
                        cnt++;
                    }
                }

                double meanScale = cnt > 0 ? sum / cnt : 0.0;
                report.Check(meanScale > 0.0 && System.Math.Abs(imp.ScaleMmPerPx - meanScale) / meanScale < 0.10,
                    "★ 内参仿真：像素→毫米的标度取的是「mark 间距」这一局部量（不是检出外接框）",
                    string.Format(CultureInfo.InvariantCulture,
                        "量化用 {0:F6} mm/px，逐图按 mark 间距算的均值 {1:F6} mm/px（差 {2:F2}%）",
                        imp.ScaleMmPerPx, meanScale,
                        meanScale > 0.0 ? System.Math.Abs(imp.ScaleMmPerPx - meanScale) / meanScale * 100.0 : -1.0));
            }

            // ── ⑥c 米制口径 + intrinsics.json：宿主能不能直接拼出 campar ──
            report.Check(e != null && e.FocalLengthM > 0.0 && e.PixelPitchM > 0.0,
                "★ 内参仿真：产物同时给出米制焦距与像元（否则宿主拼不出 HALCON campar）",
                e == null
                    ? "产物为空"
                    : string.Format(CultureInfo.InvariantCulture,
                        "focalLengthM = {0:F6} m、pixelPitchM = {1:E3} m（像素口径 {2:F1} px 是喂不进 area_scan_division 的）",
                        e.FocalLengthM, e.PixelPitchM, o.GetFocalPx()));

            string[] intrinsicFiles = new string[0];
            string intrinsicText = string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(run.StoreRoot))
                {
                    intrinsicFiles = System.IO.Directory.GetFiles(
                        run.StoreRoot, "*.intrinsics.json", System.IO.SearchOption.AllDirectories);
                }
            }
            catch (Exception)
            {
            }

            if (intrinsicFiles.Length > 0)
            {
                try
                {
                    intrinsicText = System.IO.File.ReadAllText(intrinsicFiles[0]);
                }
                catch (Exception)
                {
                }
            }

            report.Check(intrinsicFiles.Length > 0
                         && intrinsicText.Contains("\"campar\"")
                         && intrinsicText.Contains("camera_intrinsics")
                         && intrinsicText.Contains("halcon_area_scan_division_kappa_1_per_m2"),
                "★★ 内参仿真：intrinsics.json 落盘，且把 campar 按 HALCON 顺序排好（宿主可直接消费）",
                intrinsicFiles.Length == 0
                    ? "没找到 *.intrinsics.json —— 宿主拿不到相机模型，也就没法自己去畸变"
                    : intrinsicFiles[0] + "（" + intrinsicText.Length.ToString(CultureInfo.InvariantCulture) + " 字符）");

            // ── ⑦ 位姿读回轨迹：每张"读回来的几何"必须真的不一样 ──
            // ★ 这条断言是补出来的：曾经出现过"图确实是九个不同姿态，但读回来的位姿全一样"，
            //   于是覆盖度判"全都不斜"。光看覆盖度结论分不出"用户没斜着拍"和"我们读错了"，
            //   所以把原始位姿也端出来，并且断言相邻两帧的位姿不能相同。
            if (run.GeometryTrace.Count > 1)
            {
                bool allSame = true;
                for (int i = 1; i < run.GeometryTrace.Count; i++)
                {
                    if (!string.Equals(run.GeometryTrace[i], run.GeometryTrace[0], StringComparison.Ordinal))
                    {
                        allSame = false;
                        break;
                    }
                }

                report.Check(!allSame,
                    "★★ 内参仿真：每张读回来的板位姿确实各不相同（不是读到了同一个位姿）\n      帧→位姿轨迹："
                    + string.Join(" ", cam.FrameTrace.ToArray())
                    + "\n      注入的位姿（应与读回对账）："
                    + string.Join(" ", PoseTexts(cam)),
                    allSame
                        ? "九张读回完全一致 —— 说明位姿读回或姿态脚本有问题，覆盖度判据不可信"
                        : "读回轨迹：\n      " + string.Join("\n      ", run.GeometryTrace.ToArray()));
            }

            // ── ⑧ 引导确实发生了（不是闷头跑完）──
            int confirms = 0;
            for (int i = 0; i < prompt.Transcript.Count; i++)
            {
                if (prompt.Transcript[i].StartsWith("CONFIRM:", StringComparison.Ordinal))
                {
                    confirms++;
                }
            }

            report.Check(confirms >= plan.Count,
                "内参仿真：每个姿态都先问过操作员再取图（人工摆板链不能自作主张连拍）",
                string.Format(CultureInfo.InvariantCulture,
                    "提示次数 {0}，姿态数 {1}", confirms, plan.Count));

            return run;
        }

        private static List<string> PoseTexts(SimulatedBoardCamera cam)
        {
            var list = new List<string>();
            for (int i = 0; i < cam.PoseCount; i++)
            {
                list.Add(cam.PoseText(i));
            }

            return list;
        }

        private static string DescribeFailure(ChainRunResult r)
        {
            if (r == null)
            {
                return "没有结果";
            }

            if (r.Error != null)
            {
                return r.Error.ToString();
            }

            return r.Cancelled ? "被取消" : "未知原因（见日志）";
        }

        /// <summary>
        /// 逐帧可视化载荷的验收（内参链"边摆边画"）。
        ///
        /// 断的是四件事，缺一件这条路就可能是断的：
        ///   ① <b>真的抛了</b>（数量 ≥ 成功找板的张数）—— 没抛就等于界面永远空白；
        ///   ② <b>带着原始帧</b>（长度对、且画面不是常量）—— 没帧的话操作员看不到自己拍了什么，
        ///      而"板太小"这件事只能靠"看画面"和"看检出点"对照才能判定；
        ///   ③ <b>marks 数与解算看到的一致</b> —— 叠加画的如果和真解算用的不是同一批点，
        ///      那它就是一张会骗人的图（比不画更糟）；
        ///   ④ <b>判据口径一致</b>（TooSmall ⇔ 板宽占比低于下限）—— 两处各判一次必然漂移。
        /// </summary>
        private static void VerifyBoardOverlays(SelfCheckReport report, IntrinsicsSimRun run,
            IntrinsicsSolveOutcome o, SimulatedBoardCamera cam)
        {
            List<CalibVisualizationRequest> all = run.Overlays;
            if (all == null)
            {
                report.Check(false, "① 内参链抛出了逐帧可视化载荷",
                    "回调压根没被挂上（run.Overlays 为 null）。");
                return;
            }

            var framed = new List<BoardOverlayPayload>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].Board != null)
                {
                    framed.Add(all[i].Board);
                }
            }

            report.Check(framed.Count >= o.Views.Count && framed.Count > 0,
                "① ★ 内参链逐帧抛出可视化载荷（界面「边摆边画」靠它，抛不出来就是一片静默空白）",
                string.Format(CultureInfo.InvariantCulture,
                    "载荷 {0} 份，其中带板检出 {1} 份；成功找板 {2} 张",
                    all.Count, framed.Count, o.Views.Count));

            if (framed.Count == 0)
            {
                return;
            }

            // ── ② 每份都带真帧 ──
            int withFrame = 0;
            int constantFrame = 0;
            for (int i = 0; i < framed.Count; i++)
            {
                BoardOverlayPayload b = framed[i];
                if (b.RawGray != null && b.RawGray.Length == b.Width * b.Height
                    && b.Width == cam.Width && b.Height == cam.Height)
                {
                    withFrame++;

                    // 画面不能是常量：常量图＝没真取到帧（等价于"显示了一张白板"，
                    // 而白板上当然找不到板 —— 会把"取图失败"误诊成"找板算法不行"）
                    bool same = true;
                    for (int k = 1; k < b.RawGray.Length && same; k++)
                    {
                        if (b.RawGray[k] != b.RawGray[0])
                        {
                            same = false;
                        }
                    }

                    if (same)
                    {
                        constantFrame++;
                    }
                }
            }

            report.Check(withFrame == framed.Count && constantFrame == 0,
                "② ★ 每份载荷都带<b>真的</b>原始帧（尺寸对、且不是常量图）",
                string.Format(CultureInfo.InvariantCulture,
                    "带帧 {0}/{1} 份，其中常量图（等于没取到帧）{2} 份",
                    withFrame, framed.Count, constantFrame));

            // ── ③ marks 与解算看到的是同一批点 ──
            int matched = 0;
            string firstMismatch = null;
            for (int i = 0; i < o.Views.Count && i < framed.Count; i++)
            {
                BoardOverlayPayload b = framed[i];
                int shown = b.Marks == null ? 0 : b.Marks.Count;
                int solved = o.Views[i].MarkCount;
                if (shown == solved)
                {
                    matched++;
                }
                else if (firstMismatch == null)
                {
                    firstMismatch = string.Format(CultureInfo.InvariantCulture,
                        "第 {0} 张「{1}」：叠加画了 {2} 个点，解算用了 {3} 个",
                        i + 1, o.Views[i].Label, shown, solved);
                }
            }

            report.Check(matched == o.Views.Count,
                "③ ★★ 叠加画的 mark 与解算真正用的<b>是同一批点</b>（数量逐张一致）",
                firstMismatch ?? string.Format(CultureInfo.InvariantCulture,
                    "{0} 张逐一对上（叠加不是另算一套，画出来的就是算进去的）", matched));

            // ── ④ 判据口径一致 + 外接范围自洽 ──
            bool verdictOk = true;
            bool extentsOk = true;
            bool coverageOk = true;
            bool thresholdOk = true;
            string why = null;

            for (int i = 0; i < framed.Count; i++)
            {
                BoardOverlayPayload b = framed[i];

                if (string.IsNullOrEmpty(b.Verdict) || b.Verdict.IndexOf("检出", StringComparison.Ordinal) < 0)
                {
                    verdictOk = false;
                    why = why ?? ("第 " + (i + 1) + " 份载荷没有可读结论");
                }

                // 阈值口径：TooSmall 必须等价于"占比低于下限"（两处各判一次必然漂移）
                bool want = b.ApparentWidthFraction > 0.0
                    && b.ApparentWidthFraction < IntrinsicsRunner.MinApparentWidthFraction;
                if (b.TooSmall != want)
                {
                    thresholdOk = false;
                    why = why ?? string.Format(CultureInfo.InvariantCulture,
                        "第 {0} 份：TooSmall={1} 但占比 {2:P0} 与下限 {3:P0} 对不上",
                        i + 1, b.TooSmall, b.ApparentWidthFraction, IntrinsicsRunner.MinApparentWidthFraction);
                }

                if (b.HasMarks)
                {
                    double minC = double.MaxValue, maxC = double.MinValue;
                    double minR = double.MaxValue, maxR = double.MinValue;
                    for (int k = 0; k < b.Marks.Count; k++)
                    {
                        // ★ 约定：X = 列，Y = 行。写反了图上会整体旋转 90°，
                        //   而"看起来还在画面里"会让这个错一直不被发现。
                        if (b.Marks[k].X < minC) { minC = b.Marks[k].X; }
                        if (b.Marks[k].X > maxC) { maxC = b.Marks[k].X; }
                        if (b.Marks[k].Y < minR) { minR = b.Marks[k].Y; }
                        if (b.Marks[k].Y > maxR) { maxR = b.Marks[k].Y; }
                    }

                    if (Math.Abs(minC - b.MinCol) > 1e-9 || Math.Abs(maxC - b.MaxCol) > 1e-9
                        || Math.Abs(minR - b.MinRow) > 1e-9 || Math.Abs(maxR - b.MaxRow) > 1e-9)
                    {
                        extentsOk = false;
                        why = why ?? ("第 " + (i + 1) + " 份：外接范围与 marks 实际范围不一致");
                    }

                    if (b.MinCol < 0.0 || b.MaxCol > b.Width || b.MinRow < 0.0 || b.MaxRow > b.Height)
                    {
                        extentsOk = false;
                        why = why ?? ("第 " + (i + 1) + " 份：检出范围跑到画面外面去了（坐标轴可能反了）");
                    }
                }

                if (!double.IsNaN(b.MarkCoverage)
                    && (b.MarkCoverage <= 0.0 || b.MarkCoverage > 1.0))
                {
                    coverageOk = false;
                    why = why ?? ("第 " + (i + 1) + " 份：检出率 " + b.MarkCoverage.ToString("F3", CultureInfo.InvariantCulture));
                }
            }

            report.Check(verdictOk, "④ 每份载荷都带一句人话结论（不只是一堆数字）", why ?? "全部可读");
            report.Check(thresholdOk, "④ ★ 「板偏小」判据与 owner 侧同一口径（TooSmall ⇔ 占比 < 下限）",
                why ?? "口径一致");
            report.Check(extentsOk, "④ ★ marks 的 (X=列, Y=行) 约定自洽，且外接范围落在画面内",
                why ?? "全部自洽（写反会把整张图转 90° 且不易察觉）");
            report.Check(coverageOk, "④ 检出率落在 (0, 1] 内", why ?? "全部合理");

            // ── ⑤ 板太小这件事必须真的被标出来过（否则这条判据从没生效过）──
            int tooSmall = 0;
            for (int i = 0; i < framed.Count; i++)
            {
                if (framed[i].TooSmall)
                {
                    tooSmall++;
                }
            }

            report.Check(tooSmall == 0,
                "⑤ 本套脚本不该出现「板偏小」（校验判据与脚本互不矛盾）",
                tooSmall == 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        "0 份被判偏小；最小一张占比 {0:P0} ≥ 下限 {1:P0}",
                        worstFracOf(framed), IntrinsicsRunner.MinApparentWidthFraction)
                    : tooSmall + " 份被判偏小 —— 脚本要求的最小视在尺寸与判据下限冲突了");
        }

        private static double worstFracOf(List<BoardOverlayPayload> framed)
        {
            double worst = 1.0;
            for (int i = 0; i < framed.Count; i++)
            {
                double f = framed[i].ApparentWidthFraction;
                if (f > 0.0 && f < worst)
                {
                    worst = f;
                }
            }

            return worst;
        }
    }
}
