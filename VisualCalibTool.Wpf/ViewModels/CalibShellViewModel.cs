using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;
using VisualCalibTool.Infrastructure.Mvvm;
using VisualCalibTool.Services;
using VisualCalibTool.Simulation;

namespace VisualCalibTool.ViewModels
{
    /// <summary>
    /// 工具外壳视图模型。
    /// 职责：装配运行环境 → 把"能看见的东西"显示出来 → 提供几个能立刻验证链路的动作。
    ///
    /// ★ 它只认 <see cref="ICalibImageSurface"/> 与 <see cref="IVisualCalibEnvironment"/>，
    ///   既不知道 WPF 控件实现，也不知道 HALCON 存在。
    /// ★ 台架（<see cref="SimulatedRig"/>）是<b>唯一的装配入口</b>，与离线自检共用同一条代码路径 ——
    ///   否则会出现"离线断言全绿、界面一点却 0/9 点"这种最难查的偏差。
    /// </summary>
    public sealed class CalibShellViewModel : ObservableObject, IDisposable
    {
        private const int MaxLogLines = 800;

        private readonly SimpleCalibLog _log = new SimpleCalibLog();

        private SimulatedRig _rig;
        private WizardCoordinator _coordinator;
        private CalibWizardViewModel _wizard;
        private ICalibImageSurface _surface;

        private string _status = "就绪";
        private string _cursorText = "—";
        private string _environmentKind = "未初始化";
        private string _storeRoot = "—";
        private string _matrixText = "（尚未解算）";
        private string _diagnosticsText = "（尚未解算）";
        private string _chainSummaryText = "（尚未跑三链闭环）";
        private string _lastArtifactRoot;
        private string _lastFramePath;
        private string _lastOverlayNote = "（尚未画出叠加）";
        private int _grabCount;

        public CalibShellViewModel()
        {
            LogLines = new ObservableCollection<string>();

            InitCommand = new RelayCommand(() => RunGuarded("初始化", () => Initialize()));
            GrabOnceCommand = new RelayCommand(() => RunGuarded("取图", () => GrabOnce()));
            FitImageCommand = new RelayCommand(() => FitImage());
            RunSelfCheckCommand = new RelayCommand(() => RunSelfCheck());
            RunEndToEndCommand = new RelayCommand(() => RunEndToEnd());
            ExportSampleTupCommand = new RelayCommand(() => ExportSampleTup());
            SimulateMirrorCommand = new RelayCommand(() => ToggleMirror());
            OpenStoreFolderCommand = new RelayCommand(() => OpenStoreFolder());

            _log.Emitted += OnLogEmitted;
        }

        /// <summary>
        /// 环境问题（典型：缺 HALCON 运行时）。由视图层报上来，只写日志与状态栏。
        /// ★ 这类问题<b>不该中断流程，更不该抛出去</b> ——
        ///   现场要的是"照着做就能修好"的一句话，不是一个从 Loaded 里冒出来的英文堆栈。
        /// </summary>
        public void ReportEnvironmentIssue(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return;
            }

            _log.Error(reason);
            Status = "环境未就绪：" + reason;
        }

        /// <summary>
        /// 「去畸变后会好多少」量化好了 —— 转给界面（宿主订阅这个事件）。
        ///
        /// ★ 为什么要在这一层中转，而不是让宿主直接订阅向导：
        ///   向导视图模型是<b>初始化时才建出来的</b>，宿主在构造时拿不到它，
        ///   拿不到稳定的订阅时机。Shell 视图模型从生到死都在，订阅它才不会漏。
        /// </summary>
        public event Action<DistortionImpactAssessment> DistortionImpactMeasured;

        private void OnDistortionImpactMeasured(DistortionImpactAssessment assessment)
        {
            Action<DistortionImpactAssessment> handler = DistortionImpactMeasured;
            if (handler != null)
            {
                handler(assessment);
            }
        }

        /// <summary>
        /// 命令入口统一兜一层：任何一个命令抛异常都不该把界面线程带走。
        /// ★ 尤其是 <c>DllNotFoundException: 无法加载 DLL「halcon」</c> 这种 ——
        ///   编译期、静态检查、XAML 加载全绿，只在第一次建 HImage 时才炸，
        ///   而它从 Loaded 事件里冒出去就是"点一下程序没了"。
        /// </summary>
        private void RunGuarded(string what, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                string msg = what + "失败：" + ex.Message;
                _log.Error(msg);
                Status = msg;
            }
        }

        #region 绑定属性

        public ObservableCollection<string> LogLines { get; private set; }

        public ICommand InitCommand { get; private set; }

        public ICommand GrabOnceCommand { get; private set; }

        public ICommand FitImageCommand { get; private set; }

        public ICommand RunSelfCheckCommand { get; private set; }

        /// <summary>★ H / O / e 三链仿真闭环（合成图 → 真提取 → 真解算 → 真导出）。</summary>
        public ICommand RunEndToEndCommand { get; private set; }

        public ICommand ExportSampleTupCommand { get; private set; }

        public ICommand SimulateMirrorCommand { get; private set; }

        public ICommand OpenStoreFolderCommand { get; private set; }

        /// <summary>
        /// ★ 向导视图模型（右栏第一页的数据源）。
        /// ★ 环境还没装配时为 null —— 界面绑定到 null <b>不会崩，但会留下一片空白</b>。
        ///   所以"空白"这件事必须由 <see cref="WizardNoticeText"/> 补一句人话，
        ///   不能让用户对着空面板猜（2026-09-14 现场就是这么发生的）。
        /// </summary>
        public CalibWizardViewModel Wizard
        {
            get { return _wizard; }
            private set
            {
                if (SetProperty(ref _wizard, value))
                {
                    OnPropertyChanged("WizardNoticeText");
                    OnPropertyChanged("HasWizardNotice");
                }
            }
        }

        /// <summary>
        /// 向导还不能用时，向导页顶部要显示的那句话（空 = 向导可用，不用提示）。
        ///
        /// ★★ 为什么需要它：向导视图模型是在 <see cref="Initialize"/> 里
        ///   （装配仿真台架那一步）才创建的。那一步一旦失败 —— 缺 HALCON 原生库、
        ///   走位被拒、取图抛异常 —— 视图模型就<b>永远不会存在</b>：
        ///   向导页只剩下 XAML 里的字面量文字，看起来就是"什么都没显示出来"，
        ///   而失败原因被写进了另一个页签（诊断）的日志里。
        ///   "空白"本身不携带任何信息，用户只能问"为什么"。
        ///   这里把它变成一句能照着做的话。
        /// </summary>
        public string WizardNoticeText
        {
            get
            {
                if (_wizard != null)
                {
                    return string.Empty;
                }

                return "向导还没就绪 —— 它要等环境装配完才列得出可选项。当前状态：" + _status
                    + "。若一直停在这里：点顶栏「初始化仿真环境」重试，"
                    + "失败原因会写进「诊断」页的日志。";
            }
        }

        public bool HasWizardNotice
        {
            get { return _wizard == null; }
        }

        public string Title
        {
            get { return "VisualCalibTool · 可视化标定工具"; }
        }

        public string VersionText
        {
            get { return "v0.3.0（P0 · 向导 + 仿真三链闭环 H / O / e）"; }
        }

        public string Status
        {
            get { return _status; }
            private set
            {
                if (SetProperty(ref _status, value))
                {
                    // ★ 向导没就绪时，提示语里带着 Status（"当前状态：…"），
                    //   所以 Status 变了它也得跟着变 —— 否则那句提示会停在旧状态上。
                    OnPropertyChanged("WizardNoticeText");
                }
            }
        }

        public string CursorText
        {
            get { return _cursorText; }
            private set { SetProperty(ref _cursorText, value); }
        }

        public string EnvironmentKind
        {
            get { return _environmentKind; }
            private set { SetProperty(ref _environmentKind, value); }
        }

        public string StoreRoot
        {
            get { return _storeRoot; }
            private set { SetProperty(ref _storeRoot, value); }
        }

        public string MatrixText
        {
            get { return _matrixText; }
            private set { SetProperty(ref _matrixText, value); }
        }

        public string DiagnosticsText
        {
            get { return _diagnosticsText; }
            private set { SetProperty(ref _diagnosticsText, value); }
        }

        /// <summary>
        /// ★ 三链闭环的人话结论（H / O / e 三行 + 口径提醒 + 产物位置）。
        /// 它存在的意义：把"数字对不对"压缩成一眼能判的几句话，而不是让人去读几十条断言。
        /// </summary>
        public string ChainSummaryText
        {
            get { return _chainSummaryText; }
            private set { SetProperty(ref _chainSummaryText, value); }
        }

        /// <summary>叠加当前画了什么（AR 是最难作假的判据，得让人知道画的是不是那一层）。</summary>
        public string OverlayNote
        {
            get { return _lastOverlayNote; }
            private set { SetProperty(ref _lastOverlayNote, value); }
        }

        public string Disclaimer
        {
            get
            {
                return _rig == null
                    ? "尚未初始化。点「初始化仿真环境」开始 —— 全流程不需要任何硬件。"
                    : _rig.Env.Disclaimer;
            }
        }

        public bool IsMirrorInjected
        {
            get { return _rig != null && _rig.World.InjectMirror; }
        }

        #endregion

        /// <summary>由视图注入显示面（唯一的显示接缝）。</summary>
        public ICalibImageSurface Surface
        {
            get { return _surface; }
            set
            {
                if (_surface != null)
                {
                    ICalibOverlayTarget oldTarget = _surface.Overlay;
                    if (oldTarget != null)
                    {
                        oldTarget.CursorPixelMoved -= OnCursorPixelMoved;
                    }

                    IRoiPickSurface oldPick = _surface as IRoiPickSurface;
                    if (oldPick != null)
                    {
                        oldPick.SetRoiPickMode(false);
                        oldPick.RoiPicked -= OnSurfaceRoiPicked;
                    }
                }

                _surface = value;

                if (_surface != null)
                {
                    ICalibOverlayTarget target = _surface.Overlay;
                    if (target != null)
                    {
                        target.CursorPixelMoved += OnCursorPixelMoved;
                    }

                    IRoiPickSurface pick = _surface as IRoiPickSurface;
                    if (pick != null)
                    {
                        pick.RoiPicked += OnSurfaceRoiPicked;
                    }
                }
            }
        }

        #region 命令实现

        private void Initialize()
        {
            if (_rig == null)
            {
                // ★ 台架装配只有一条代码路径（与 .workbuddy/_calib_selfcheck 用的是同一个）。
                //   这里给一点曝光延时与噪声：界面上的"等帧"和"有噪声的图"才是现场的常态，
                //   而在自检里这两样都设成 0 以省时间。
                _rig = SimulatedRig.Build(new SimulatedRigOptions
                {
                    GrabLatencyMs = 25,
                    GrayNoise = 6.0,
                    RenderTip = false
                }, _log);

                _coordinator = _rig.CreateCoordinator();

                // ★ 训练器/提取器与采样链共用同一个实例（模板库只有一份），见 SimulatedRig 注释。
                var wizard = new CalibWizardViewModel(_rig.Env, _coordinator, _rig.Trainer, _rig.Extractor,
                    DispatcherOrNull());
                wizard.VisualizationRequested = OnVisualizationRequested;
                wizard.FramePlanted = OnFramePlanted;
                wizard.DistortionImpactMeasured += OnDistortionImpactMeasured;

                // ★ 模板示教的四条回挂：抓到帧→显示；要框选→显示面进框选模式；
                //   框选完成→交回向导；预览就绪→画叠加。
                //   （向导不认识显示控件，只认识委托 —— 画不画、怎么画是宿主的事。）
                wizard.TemplateFrameCaptured = OnTemplateFrameCaptured;
                wizard.TemplateRoiPickRequested = OnTemplateRoiPickRequested;
                wizard.FeaturePreviewReady = OnFeaturePreviewReady;
                Wizard = wizard;

                EnvironmentKind = _rig.Env.Kind;
                StoreRoot = _rig.StoreRoot;

                _log.Info("台架已建立：" + _rig.Env.Kind + "（" + _rig.World.Width + "x" + _rig.World.Height
                    + "，" + _rig.World.MmPerPixel.ToString("F4", CultureInfo.InvariantCulture) + " mm/px）");
                _log.Info("Mark 世界位 (" + _rig.World.MarkWorldXy.X.ToString("F2", CultureInfo.InvariantCulture)
                    + ", " + _rig.World.MarkWorldXy.Y.ToString("F2", CultureInfo.InvariantCulture)
                    + ")；法兰基准位 (" + _rig.Topology.BasePosXY.X.ToString("F2", CultureInfo.InvariantCulture)
                    + ", " + _rig.Topology.BasePosXY.Y.ToString("F2", CultureInfo.InvariantCulture)
                    + ")；步长 " + _rig.Topology.StepX.ToString("F1", CultureInfo.InvariantCulture) + " mm");
                _log.Info("存储根目录：" + _rig.StoreRoot);
                _log.Warn("仿真台架里的「对针」用的是 e 真值反解出的机位 —— 真机必须换成人工对针（ManualTipApproachProvider）。");

                Status = "仿真环境就绪";
                OnPropertyChanged("Disclaimer");
            }

            // 归位到基准位，然后抓第一帧
            CalibTopology topo = _rig.Env.ReadTopology();
            OpResult mv = _rig.Env.Motion.MoveLinear(
                new MotionPose(topo.BasePosXY.X, topo.BasePosXY.Y, topo.WorkZ, topo.CalibU0), 80.0, 400.0);

            LogOp("初始化归位", mv);
            GrabOnce();
        }

        private void GrabOnce()
        {
            if (_rig == null)
            {
                Initialize();
                return;
            }

            CalibError error;
            byte[] raw = _rig.Env.Camera.GrabFrame(2000, out error);
            if (raw == null)
            {
                _log.Error("取图失败：" + (error == null ? "未知原因" : error.ToString()));
                Status = "取图失败";
                return;
            }

            _grabCount++;
            if (_surface != null)
            {
                _surface.ShowFrame(raw, _rig.Camera.Width, _rig.Camera.Height, true);

                // ★ 把"合成图里 Mark 的真值像素"标出来，用来肉眼确认相机模型与显示都对。
                //   这个十字只用于显示自检 —— 它绝不参与标定解算（见 SyntheticFrameCamera 注释）。
                Vec2 truth = _rig.GroundTruthMarkPixel();
                ICalibOverlayTarget ov = _surface.Overlay;
                if (ov != null && ov.IsReady)
                {
                    ov.ResetOverlay();
                    ov.SetColor("green");
                    ov.SetLineWidth(1);
                    ov.DrawCross(truth.Y, truth.X, 26.0);
                    ov.DrawCircle(truth.Y, truth.X, _rig.World.MarkRadiusPx);
                    ov.SetColor("yellow");
                    ov.DrawText(truth.Y - _rig.World.MarkRadiusPx - 30.0, truth.X - 60.0,
                        string.Format(CultureInfo.InvariantCulture, "truth ({0:F1}, {1:F1})", truth.X, truth.Y));
                }
            }

            MotionPose pose = _rig.Env.SimulatedMotion.TruePosition;
            Status = string.Format(CultureInfo.InvariantCulture, "已取图 #{0}  位姿 {1}", _grabCount, pose);
            _log.Info(Status);
        }

        private void FitImage()
        {
            if (_surface != null)
            {
                _surface.FitImage();
            }
        }

        private void RunSelfCheck()
        {
            _log.Step("selfcheck", "=========== 算法层离线自检（纯代数：把相机模型排除在外）===========");
            SelfCheckReport report = CalibSelfCheck.Run();

            DumpReport(report, "selfcheck");

            Status = report.AllPassed
                ? string.Format("算法自检全部通过（{0} 项）", report.Passed)
                : string.Format("算法自检有 {0} 项失败", report.Failed);

            if (_rig != null)
            {
                MatrixText = _rig.World.ExpectedH().ToString();
            }
        }

        /// <summary>
        /// ★★ H / O / e 三链仿真闭环。
        ///
        /// 与「算法自检」的分工：自检只验解算器（喂理想投影造的 (像素, 世界)）；
        /// 这里跑的是整条链 —— 合成真图 → 真 HALCON 提取 → 真解算 → 真导出，
        /// 走位、软触发、留档、预测 ROI、质量门禁、产物落盘全部真实执行。
        ///
        /// 两条链各自能抓到的故障类型不同，所以界面上必须是两个按钮而不是一个。
        /// </summary>
        private void RunEndToEnd()
        {
            _log.Step("e2e", "=========== 三链仿真闭环开始（合成图 → 真提取 → 真解算 → 真导出）===========");

            var opt = new SimulationHarnessOptions
            {
                GrayNoise = 6.0,
                ArchiveFrames = true,
                CaptureTrace = true,
                StoreRoot = _rig == null ? null : Path.Combine(_rig.StoreRoot, "closed_loop")
            };

            var report = new SelfCheckReport();
            SimulationRun run = CalibSimulationHarness.RunAll(report, opt);

            DumpReport(report, "e2e");

            for (int i = 0; i < run.Transcript.Count; i++)
            {
                _log.Info(run.Transcript[i]);
            }

            _lastArtifactRoot = run.StoreRoot;
            if (!string.IsNullOrEmpty(run.StoreRoot))
            {
                StoreRoot = run.StoreRoot;
            }

            if (run.NinePoint != null && run.NinePoint.NinePoint != null && run.NinePoint.NinePoint.Success)
            {
                MatrixText = run.NinePoint.NinePoint.H.ToString();
            }

            ChainSummaryText = BuildChainSummary(run);
            _log.Info(ChainSummaryText.Replace(Environment.NewLine, " | "));

            _log.Step("e2e", string.Format(CultureInfo.InvariantCulture,
                "=========== 闭环结束：通过 {0}，失败 {1} ===========", report.Passed, report.Failed));

            Status = report.AllPassed
                ? string.Format("三链闭环通过（{0} 项断言全绿）", report.Passed)
                : string.Format("三链闭环有 {0} 项失败 —— 先看日志里的 [FAIL] 行", report.Failed);
        }

        /// <summary>把断言清单分流到日志（[FAIL] 走 Error，其余走 Info）。</summary>
        private void DumpReport(SelfCheckReport report, string stepKey)
        {
            for (int i = 0; i < report.Lines.Count; i++)
            {
                string line = report.Lines[i];
                if (line.StartsWith("[FAIL]", StringComparison.Ordinal))
                {
                    _log.Error(line);
                }
                else
                {
                    _log.Info(line);
                }
            }
        }

        /// <summary>把几十条断言压成"一眼能判"的几行结论。</summary>
        private static string BuildChainSummary(SimulationRun run)
        {
            var sb = new System.Text.StringBuilder();
            string nl = Environment.NewLine;

            if (run.NinePoint != null && run.NinePoint.Success && run.NinePoint.NinePoint != null)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "H：RMS {0:F5} mm，σ1/σ2 {1:F6}，反投影 {2:F5} px{nl}",
                    run.NinePoint.NinePoint.RmsMm,
                    run.NinePoint.Diagnostics == null ? double.NaN : run.NinePoint.Diagnostics.SigmaRatio,
                    run.NinePoint.Reprojection == null ? double.NaN : run.NinePoint.Reprojection.RmsPixelResidualPx);
            }
            else
            {
                sb.Append("H：未通过").Append(nl);
            }

            if (run.RotationCenter != null && run.RotationCenter.Success && run.RotationCenter.RotationCenter != null)
            {
                RotationCenterResult rc = run.RotationCenter.RotationCenter;
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "O：({0:F4}, {1:F4})，映射点 {2} 个，残差 RMS {3:F5} mm{nl}",
                    rc.Center.X, rc.Center.Y, rc.MappedPointCount, rc.RmsMm);
            }
            else
            {
                sb.Append("O：未通过").Append(nl);
            }

            AppendEccLine(sb, "e（像面随法兰滚转）", run.ToolOffset);
            AppendEccLine(sb, "e（像面不随滚转）", run.ToolOffsetFixedCamera);

            if (run.WrongRegimeEcc.Length > 0.0
                && run.ToolOffset != null && run.ToolOffset.ToolOffset != null
                && run.ToolOffset.ToolOffset.EccMagnitudeMm > 1e-9)
            {
                double shrink = (1.0 - run.WrongRegimeEcc.Length
                    / run.ToolOffset.ToolOffset.EccMagnitudeMm) * 100.0;
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "口径：选错会让 |e| 缩水 {0:F1}%（对称角会掩盖这个坑）{1}", shrink, nl);
            }

            if (!string.IsNullOrEmpty(run.StoreRoot))
            {
                sb.Append("产物：").Append(run.StoreRoot);
            }

            return sb.ToString();
        }

        private static void AppendEccLine(System.Text.StringBuilder sb, string label, ChainRunResult r)
        {
            if (r != null && r.Success && r.ToolOffset != null)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0}：e = ({1:F4}, {2:F4})，|e| {3:F4} mm，方向 {4:F1}°{5}",
                    label, r.ToolOffset.Ecc.X, r.ToolOffset.Ecc.Y,
                    r.ToolOffset.EccMagnitudeMm, r.ToolOffset.EccDirectionDeg, Environment.NewLine);
            }
            else
            {
                sb.Append(label).Append("：未通过").Append(Environment.NewLine);
            }
        }

        private void ExportSampleTup()
        {
            if (_rig == null)
            {
                Initialize();
                return;
            }

            try
            {
                HomMat2D h = _rig.World.ExpectedH();
                string dir = Path.Combine(_rig.StoreRoot, "exports");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "SIM_001_Cam_Top_ninepoint.tup");
                HomMatIO.WriteTup(path, h);

                long size = new FileInfo(path).Length;
                _log.Info("已导出示例 .tup：" + path + "（" + size + " 字节，可被主项目「导入外部标定矩阵文件」直接消费）");
                Status = "已导出示例 .tup";
                MatrixText = h.ToString();
            }
            catch (Exception ex)
            {
                _log.Error("导出 .tup 失败", ex);
            }
        }

        private void ToggleMirror()
        {
            if (_rig == null)
            {
                Initialize();
            }

            _rig.World.InjectMirror = !_rig.World.InjectMirror;
            OnPropertyChanged("IsMirrorInjected");

            _log.Warn("仿真世界注入镜像（反 Y）已" + (_rig.World.InjectMirror ? "打开" : "关闭")
                + " —— 打开后解出的 det(A) 应为负，诊断必须报镜像并阻止发布。");
            Status = _rig.World.InjectMirror ? "镜像已注入（用于验证检测器）" : "镜像已关闭";

            GrabOnce();
        }

        private void OpenStoreFolder()
        {
            try
            {
                // 优先打开最近一次闭环的产物目录（那才是用户此刻想看的"东西落在哪"）
                string dir = _lastArtifactRoot;

                if (string.IsNullOrEmpty(dir))
                {
                    if (_rig == null)
                    {
                        return;
                    }

                    dir = _rig.StoreRoot;
                }

                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", dir);
            }
            catch (Exception ex)
            {
                _log.Warn("打开目录失败：" + ex.Message);
            }
        }

        #endregion

        #region 向导的两个回挂

        /// <summary>
        /// ★ 第 4 / 5 步的"看见才算通过"：画 AR 反投影叠加。
        ///
        /// 画三样东西：
        ///   ① 绿十字 —— 实测像素（特征提取给出来的，是真的）；
        ///   ② 红圈   —— 把机器人反馈位（世界）用当前 H 反投回去的像素；
        ///   ③ 连线   —— 残差矢量。
        /// 两者重合 ⇒ 标定准；系统性偏向一边 ⇒ 有平移/尺度误差；越往外偏得越多 ⇒ 尺度或畸变问题。
        /// ★ 这一层没法靠调参变好看，所以它的判据优先级高于任何 RMS 数字。
        /// </summary>
        private void OnVisualizationRequested(CalibVisualizationRequest req)
        {
            ICalibOverlayTarget ov = _surface == null ? null : _surface.Overlay;
            if (req == null)
            {
                return;
            }

            if (ov == null || !ov.IsReady)
            {
                OverlayNote = "显示面还没就绪，叠加没法画（先把窗口显示出来）。";
                return;
            }

            // ★ 内参链走另一套：它不是"把世界反投回图像"，而是"把板检出画回图像"。
            //   两者共用同一个回挂通道，但画的内容完全不同 —— 所以在这里分岔，
            //   而不是在同一个方法里揉一起（揉一起以后没人敢改）。
            if (req.Board != null)
            {
                DrawBoardOverlay(ov, req.Board);
                return;
            }

            ov.ResetOverlay();

            HomMat2D? hh = req.H;
            HomMat2D inv = HomMat2D.Identity;
            bool canProject = hh.HasValue && hh.Value.IsFinite;

            if (canProject)
            {
                try
                {
                    inv = hh.Value.Invert();
                }
                catch (InvalidOperationException)
                {
                    canProject = false;
                }
            }

            int drawn = 0;
            double maxResidual = 0.0;

            if (req.Observations != null)
            {
                for (int i = 0; i < req.Observations.Count; i++)
                {
                    CalibObservation o = req.Observations[i];
                    if (o == null || !o.IsUsable)
                    {
                        continue;
                    }

                    // ① 实测像素
                    ov.SetLineWidth(1);
                    ov.SetColor("green");
                    ov.DrawCross(o.Pixel.Y, o.Pixel.X, 12.0);

                    if (!canProject)
                    {
                        continue;
                    }

                    // ② 世界 → 像素（把机器人走到的位反投回图像）
                    Vec2 pred = inv.Transform(o.World);
                    double res = (pred - o.Pixel).Length;
                    if (res > maxResidual)
                    {
                        maxResidual = res;
                    }

                    ov.SetColor("red");
                    ov.DrawCircle(pred.Y, pred.X, 9.0);

                    // ③ 残差矢量
                    ov.SetColor("yellow");
                    ov.DrawArrow(o.Pixel.Y, o.Pixel.X, pred.Y, pred.X, 5.0);
                    drawn++;
                }
            }

            // 旋转链：映射域上的采样点与拟合圆
            if (req.HasRotCenter && req.MappedPoints != null && req.MappedPoints.Count > 0 && canProject)
            {
                ov.SetLineWidth(1);
                ov.SetColor("cyan");
                for (int i = 0; i < req.MappedPoints.Count; i++)
                {
                    Vec2 p = inv.Transform(req.MappedPoints[i]);
                    ov.DrawCross(p.Y, p.X, 6.0);
                    ov.DrawCircle(p.Y, p.X, 3.0);
                }

                Vec2 c = inv.Transform(req.RotCenterWorld);
                ov.SetColor("magenta");
                ov.DrawCross(c.Y, c.X, 18.0);
                ov.DrawCircle(c.Y, c.X, 6.0);

                double rPx = req.FittedRadiusMm / Math.Max(1e-9, Math.Abs(hh.Value.H11));
                ov.SetColor("white");
                ov.DrawCircle(c.Y, c.X, rPx);
            }

            ov.SetColor("yellow");
            ov.DrawText(12.0, 12.0, req.Title);
            if (!double.IsNaN(req.ReprojectionRmsPx))
            {
                ov.DrawText(30.0, 12.0, string.Format(CultureInfo.InvariantCulture,
                    "反投影 RMS {0:F4} px / 最大 {1:F4} px", req.ReprojectionRmsPx, maxResidual));
            }

            ov.Redraw();

            OverlayNote = string.Format(CultureInfo.InvariantCulture,
                "{0}：绿=实测像素，红/黄=用当前标定反投回去的位置与残差（{1} 个点，最大 {2:F3} px）",
                req.Title, drawn, maxResidual);

            _log.Info("叠加已画：" + OverlayNote);
        }

        /// <summary>
        /// 内参链的板检出叠加：<b>先把这一帧显示出来，再把检出结果画回去</b>。
        /// ★ 绘制规则本身在 <see cref="BoardOverlayPainter"/>（纯函数、可离线断言 ——
        ///   row/col 写反、空集画框、该橙没橙这三处错了都不报错，只能靠断言查）。
        /// ★ 顺序不能反：<c>ShowFrame</c> 内部会清掉叠加层（控件接管新图时 Clear），
        ///   先画后送帧等于白画。
        /// </summary>
        private void DrawBoardOverlay(ICalibOverlayTarget ov, BoardOverlayPayload b)
        {
            try
            {
                if (_surface != null && b.RawGray != null && b.Width > 0 && b.Height > 0)
                {
                    // ★ 只在"这一帧是第一张"时适配整图：内参链要连拍十几张，
                    //   每张都重算适配会把用户刚调好的缩放/平移一遍遍抖掉 ——
                    //   而他正盯着的恰恰是"这个角的 mark 认出来了没有"。
                    _surface.ShowFrame(b.RawGray, b.Width, b.Height, !_surface.HasImage);
                }

                OverlayNote = BoardOverlayPainter.Paint(ov, b);
                _log.Info("板检出叠加已画：" + OverlayNote);
            }
            catch (Exception ex)
            {
                // 画不出来绝不能把标定本身带崩 —— 叠加是"说服力"，不是产物
                OverlayNote = "板检出叠加没画出来：" + ex.Message;
                _log.Warn(OverlayNote);
            }
        }

        /// <summary>
        /// ★ 采样期"边跑边看"：采样帧会留档，于是把<b>最新的一帧</b>读回来显示。
        /// 这是真的（不是拿合成图糊弄），代价只是每点一次磁盘读。
        /// </summary>
        private void OnFramePlanted(int sampleIndex, CalibObservation obs)
        {
            try
            {
                if (_surface == null || _rig == null)
                {
                    return;
                }

                string dir = Path.Combine(_rig.StoreRoot, "frames");
                if (!Directory.Exists(dir))
                {
                    return;
                }

                string newest = null;
                DateTime newestUtc = DateTime.MinValue;
                string[] files = Directory.GetFiles(dir, "*.pgm", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    DateTime w = File.GetLastWriteTimeUtc(files[i]);
                    if (w > newestUtc)
                    {
                        newestUtc = w;
                        newest = files[i];
                    }
                }

                if (newest == null || string.Equals(newest, _lastFramePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                byte[] bytes = File.ReadAllBytes(newest);
                byte[] gray;
                int w2;
                int h2;

                // ★ 留档可能正在写（读到的半个文件解不出来）—— 那就跳过这一帧，不报错：
                //   显示是"看得见更好"，不是"看不见就失败"。
                if (!PgmCodec.TryDecode(bytes, out gray, out w2, out h2))
                {
                    return;
                }

                _lastFramePath = newest;
                _surface.ShowFrame(gray, w2, h2, false);
            }
            catch (Exception ex)
            {
                _log.Warn("显示最新留档帧失败（不影响标定）：" + ex.Message);
            }
        }

        #endregion

        #region 模板示教与特征预览的回挂

        /// <summary>向导抓到了示教用帧 → 显示出来（训练、框选、预览看的都是这一帧的同类画面）。</summary>
        private void OnTemplateFrameCaptured(byte[] rawGray, int width, int height)
        {
            if (_surface == null || rawGray == null)
            {
                return;
            }

            // ★ 首帧适配整图，之后保持用户的缩放（他可能正放大盯着一个区域画框）。
            _surface.ShowFrame(rawGray, width, height, !_surface.HasImage);
        }

        /// <summary>向导要框选 → 显示面进入 / 退出框选模式。</summary>
        private void OnTemplateRoiPickRequested(bool active)
        {
            IRoiPickSurface pick = _surface as IRoiPickSurface;
            if (pick == null)
            {
                _log.Warn("当前显示面不支持框选（未实现 IRoiPickSurface），模板示教停在这里。");
                return;
            }

            pick.SetRoiPickMode(active);
        }

        /// <summary>显示面上拖完一个框 → 交回向导（图像坐标 row/col）。</summary>
        private void OnSurfaceRoiPicked(double r1, double c1, double r2, double c2)
        {
            CalibWizardViewModel wizard = _wizard;
            if (wizard != null)
            {
                wizard.OnTemplateRoiCommitted(r1, c1, r2, c2);
            }
        }

        /// <summary>
        /// 特征候选预览结果 → 画叠加（圆点 / 十字 / 模板共用一个入口）。
        /// ★ 顺序：预览帧已经先经 <see cref="OnTemplateFrameCaptured"/> 显示了，
        ///   这里只管画 —— ShowFrame 会清叠加层，"先送帧再画"的纪律不能破。
        /// </summary>
        private void OnFeaturePreviewReady(FeaturePreviewPayload p)
        {
            ICalibOverlayTarget ov = _surface == null ? null : _surface.Overlay;
            if (p == null)
            {
                return;
            }

            if (ov == null || !ov.IsReady)
            {
                OverlayNote = "显示面还没就绪，预览结果画不出来。";
                return;
            }

            ov.ResetOverlay();

            string title = string.IsNullOrEmpty(p.FeatureTitle) ? "特征" : p.FeatureTitle;

            if (p.HasRoi)
            {
                ov.SetColor("orange");
                ov.SetLineWidth(2);
                ov.DrawRectangle(p.RoiR1, p.RoiC1, p.RoiR2, p.RoiC2);
                ov.SetColor("orange");
                ov.DrawText(p.RoiR1 - 6.0, p.RoiC1, "模板来源");
            }

            if (p.Matched)
            {
                // ★ 参考圆：提取器学过参考半径后画出来，让人看见"它认为这个特征有多大" ——
                //   圈的尺寸和 Mark 明显对不上 = 找错了东西，一眼可判，不用读数字。
                if (p.ReferenceRadiusPx > 1.0)
                {
                    ov.SetColor("cyan");
                    ov.SetLineWidth(1);
                    ov.DrawCircle(p.PixelY, p.PixelX, p.ReferenceRadiusPx);
                }

                ov.SetLineWidth(1);
                ov.SetColor("green");
                ov.DrawCross(p.PixelY, p.PixelX, 16.0);
                ov.SetColor("yellow");
                ov.DrawText(p.PixelY + 12.0, p.PixelX + 12.0,
                    string.Format(CultureInfo.InvariantCulture, "命中 {0:F1}%  ({1:F1}, {2:F1})",
                        p.ScorePercent, p.PixelX, p.PixelY));
            }

            ov.SetColor("yellow");
            ov.DrawText(12.0, 12.0, title + "预览：" + (p.Matched ? "命中" : "未命中")
                + (p.FellBack ? "（降级路径）" : string.Empty));
            ov.Redraw();

            OverlayNote = title + "预览：" + p.Message;
            _log.Info("特征预览已画：" + OverlayNote);
        }

        #endregion

        #region 内部

        private static System.Windows.Threading.Dispatcher DispatcherOrNull()
        {
            System.Windows.Application app = System.Windows.Application.Current;
            return app == null ? null : app.Dispatcher;
        }

        private void LogOp(string what, OpResult r)
        {
            if (r.Ok)
            {
                _log.Info(what + "：OK");
            }
            else
            {
                // ★ 保留原始错误码
                _log.Error(what + "失败：" + r);
            }
        }

        private void OnCursorPixelMoved(double row, double col)
        {
            if (_rig == null)
            {
                return;
            }

            // 图像坐标 → 世界坐标（用理想 H 换算，界面上立刻能看到"指哪打哪"）
            Vec2 world = _rig.World.ExpectedH().Transform(new Vec2(col, row));
            CursorText = string.Format(
                CultureInfo.InvariantCulture,
                "像素 (col {0:F1}, row {1:F1})  →  世界 (X {2:F2}, Y {3:F2})",
                col, row, world.X, world.Y);
        }

        private void OnLogEmitted(string level, string message)
        {
            // 日志可能来自非 UI 线程（运动/取图/向导后台线程），统一切回 UI 线程
            var dispatcher = System.Windows.Application.Current == null
                ? null
                : System.Windows.Application.Current.Dispatcher;

            Action append = () =>
            {
                LogLines.Add(string.Format(CultureInfo.InvariantCulture, "[{0}] {1}", level, message));
                while (LogLines.Count > MaxLogLines)
                {
                    LogLines.RemoveAt(0);
                }
            };

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                append();
            }
            else
            {
                dispatcher.BeginInvoke(append);
            }
        }

        public void Dispose()
        {
            _log.Emitted -= OnLogEmitted;
            Surface = null;
        }

        #endregion
    }
}
