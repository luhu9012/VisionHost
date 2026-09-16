using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 相机装调助手 · 垂直度快检 + 三点对焦快调闭环（2026-09-06 工程工具首个工具）。
    ///
    /// 原理（与《双滑台工位标定向导_设计.md》§7.2 方法 2 同口径，工具化版）：
    ///   光轴垂直工面 ⟺ 对焦平面平行工面 ⟺ 相机(随 Z)在固定高度时，工面各处到相机的距离相等。
    ///   做法：把纹理靶(点阵纸/产品纹理)分别放到视野 左/中/右(或前/后) 三个拉开的位置登记，
    ///   每个位置自动做一段 Z 轴扫描：每步软触发采一帧 → 图像清晰度评分(Tenengrad 梯度能量)
    ///   → 抛物线拟合找"最清晰 Z"。三点最清晰 Z 一致 ⇒ 垂直；不一致 ⇒ 差量与间距给出倾角与垫高量。
    ///   调整 → 复测 → 收敛(ΔZ ≤ 容差) 即完成快调闭环。
    ///
    /// 物理前提（界面有引导文案）：① 相机随 Z 轴升降或 Z 轴可动；② 靶面布满纹理；
    /// ③ 三点尽量拉开构成三角、靶全程在视野内。
    /// </summary>
    public class CameraTuneViewModel : ViewModelBase
    {
        private readonly IDevicePool _devicePool;
        private readonly HalconImageRenderService _renderService;

        // ================= 预览与采帧 =================
        private readonly AutoResetEvent _frameArrivedEvent = new AutoResetEvent(false);
        private volatile FrameEventArgs _latestFrame;
        private DateTime _lastRenderTime = DateTime.MinValue;
        private volatile bool _scanAbortRequested;

        /// <summary>九点/Mark 提取与健康检查服务(模式②网格走位快检复用标定同款算法)</summary>
        private readonly ICalibrationService _calib = new CalibrationService();

        /// <summary>页面已清理(Unloaded 后置位,防补拉重试/异步回调触碰已销毁对象)</summary>
        private bool _cleaned;

        // ================= v2 单帧实时垂直度引导(A/B/C 方案,2026-09-08) =================
        private static readonly PerpMeasureOptions PerpOpts = new PerpMeasureOptions { MaxAnalysisWidth = 1280 };
        private volatile bool _guideAbortRequested;
        private long _lastGuideFrame = -1;
        private bool _emaInited;
        private double _emaPitch, _emaRoll, _emaOrtho;
        private double _guideMinTilt = double.MaxValue;
        private int _guideStuckFrames;
        private double _guideStuckMin;

        // ================= 取流/触发模式与预览状态(2026-09-10 重做) =================
        /// <summary>
        /// 相机当前触发模式:-1 未知 / 0 连续 / 1 软触发。
        /// 这是本页原先"完全没法用"的根因:软触发模式下 StartGrabbing() 只起流、不出图,
        /// 必须先发触发才有帧。旧版一连上就 SetTriggerMode(1),而"开始预览""实时引导"
        /// 都只调 StartGrabbing() → 预览永久黑屏、引导永久"等待相机帧"。
        /// 现在的约定:预览/实时引导走连续(0);采帧/扫描前才切软触发(1),采完自动切回。
        /// </summary>
        private int _triggerMode = -1;

        /// <summary>有采帧正在等新帧:帧回调只在此时整幅克隆 Buffer(GigE 大帧整幅克隆很贵,不该白烧)</summary>
        private volatile bool _captureWaitActive;

        /// <summary>引导线程尚未消费上一帧:限制克隆/分析速率,避免追不上帧率</summary>
        private volatile bool _guideFramePending;

        /// <summary>用户是否要求开着预览(采帧临时切软触发后据此自动恢复连续预览)</summary>
        private bool _previewRequested;

        /// <summary>预览渲染总开关(扫描/走位期间关掉,把 UI 线程让给流程)</summary>
        private volatile bool _renderEnabled = true;

        private int _fpsFrames;
        private DateTime _fpsWindowStart = DateTime.Now;

        public CameraTuneViewModel()
        {
            _devicePool = App.StationHostRuntime?.DevicePool
                          ?? throw new InvalidOperationException("DevicePool not initialized");
            _renderService = new HalconImageRenderService();
            CameraDisplayVm = new ImageDisplayVm(_renderService);

            InitCommands();
            LoadDevices();
            AppendLog("相机装调助手就绪:绑相机 → 开预览 → 摆靶登记三点 → 自动扫描 → 看处方调平 → 复测收敛。");
        }

        /// <summary>预览渲染 VM(HalconImageDisplayHost 绑定)</summary>
        public ImageDisplayVm CameraDisplayVm { get; }

        // ================= 设备与绑定 =================
        public ObservableCollection<ICamera> CameraDeviceList { get; } = new ObservableCollection<ICamera>();
        public ObservableCollection<IMotionCard> MotionDeviceList { get; } = new ObservableCollection<IMotionCard>();

        private ICamera _selectedCameraDevice;
        public ICamera SelectedCameraDevice
        {
            get => _selectedCameraDevice;
            set
            {
                var old = _selectedCameraDevice;
                if (Set(ref _selectedCameraDevice, value)) OnCameraChanged(old, value);
            }
        }

        private IMotionCard _selectedMotionDevice;
        public IMotionCard SelectedMotionDevice
        {
            get => _selectedMotionDevice;
            set
            {
                var old = _selectedMotionDevice;
                if (Set(ref _selectedMotionDevice, value)) OnMotionChanged(old, value);
            }
        }

        public List<int> AxisNumbers { get; } = new List<int> { 0, 1, 2, 3, 4, 5 };

        private int _bindX = 0;
        public int BindX { get => _bindX; set => Set(ref _bindX, value); }

        private int _bindY = 1;
        public int BindY { get => _bindY; set => Set(ref _bindY, value); }

        private int _bindZ = 2;
        public int BindZ { get => _bindZ; set => Set(ref _bindZ, value); }

        private bool _isCameraConnected;
        public bool IsCameraConnected { get => _isCameraConnected; private set => Set(ref _isCameraConnected, value); }

        private bool _isGrabbing;
        public bool IsGrabbing
        {
            get => _isGrabbing;
            set { if (Set(ref _isGrabbing, value)) { UpdateCamStatus(); RefreshCommands(); } }
        }

        // ================= 顶部状态条(一眼看出"能不能用") =================
        private string _camStatusText = "相机:未连接";
        /// <summary>相机链路状态一句话:未连接 / 已连接·未取流 / 取流中(连续) / 取流中(软触发)</summary>
        public string CamStatusText { get => _camStatusText; private set => Set(ref _camStatusText, value); }

        private Brush _camStatusBrush = Brushes.Gray;
        /// <summary>状态灯颜色:灰=未连接 绿=取流中 金=已连接未取流 橙=异常</summary>
        public Brush CamStatusBrush { get => _camStatusBrush; private set => Set(ref _camStatusBrush, value); }

        private string _frameInfoText = "—";
        /// <summary>帧信息:分辨率 / 像素格式 / 实测帧率(取流中才有意义)</summary>
        public string FrameInfoText { get => _frameInfoText; private set => Set(ref _frameInfoText, value); }

        private bool _isBusyScanning;
        public bool IsBusyScanning
        {
            get => _isBusyScanning;
            private set
            {
                if (Set(ref _isBusyScanning, value)) RefreshCommands();
            }
        }

        // ================= 调机参数(字符串直绑,现场手输) =================
        private string _moveStepText = "10";
        public string MoveStepText { get => _moveStepText; set => Set(ref _moveStepText, value); }

        private string _scanRangeText = "2.0";
        public string ScanRangeText { get => _scanRangeText; set => Set(ref _scanRangeText, value); }

        private string _scanStepText = "0.25";
        public string ScanStepText { get => _scanStepText; set => Set(ref _scanStepText, value); }

        private string _tolText = "0.05";
        public string TolText { get => _tolText; set => Set(ref _tolText, value); }

        private string _mountText = "50";
        public string MountText { get => _mountText; set => Set(ref _mountText, value); }

        private double ScanRange => ParseD(ScanRangeText, 2.0);
        private double ScanStep => ParseD(ScanStepText, 0.25);
        private double FocusTol => ParseD(TolText, 0.05);
        private double MountSpacing => ParseD(MountText, 50.0);
        private double MoveStep => ParseD(MoveStepText, 10.0);

        // ===== 模式② 网格走位比对参数 =====
        private string _gridStepText = "10";
        public string GridStepText { get => _gridStepText; set => Set(ref _gridStepText, value); }

        public List<string> GridPatterns { get; } = new List<string> { "3×3 九点(推荐)", "十字五点" };
        public List<string> FeatureKinds { get; } = new List<string> { "十字 Mark", "圆点 Mark" };

        private int _gridPatternIndex;
        public int GridPatternIndex { get => _gridPatternIndex; set => Set(ref _gridPatternIndex, value); }

        private int _featureTypeIndex;
        public int FeatureTypeIndex { get => _featureTypeIndex; set => Set(ref _featureTypeIndex, value); }

        // ================= 成像与打光体检(成像 Tab,2026-09-06 整合) =================
        /// <summary>曝光时间 us,滑杆即改即下发;置位 _paramSyncFlag 防程序回写反向覆盖。</summary>
        private double _exposureUs = 8000;
        public double ExposureUs
        {
            get => _exposureUs;
            set
            {
                if (!Set(ref _exposureUs, value)) return;
                if (!_paramSyncFlag && IsCameraConnected && SelectedCameraDevice != null)
                    SelectedCameraDevice.SetExposureTime(value);
            }
        }

        /// <summary>增益,滑杆即改即下发(约 0~24)。</summary>
        private double _gainVal = 2.0;
        public double GainVal
        {
            get => _gainVal;
            set
            {
                if (!Set(ref _gainVal, value)) return;
                if (!_paramSyncFlag && IsCameraConnected && SelectedCameraDevice != null)
                    SelectedCameraDevice.SetGain(value);
            }
        }

        private bool _paramSyncFlag;

        private string _frameGradeText = "尚无采样帧——连相机后点『📷 采一帧并评分』。";
        public string FrameGradeText { get => _frameGradeText; set => Set(ref _frameGradeText, value); }

        private string _imagingReportText = "尚无体检结果。调曝光/增益(滑杆即时下发)后:采帧看评分徽章 → 点『生成体检报告』得逐项判定与打光处方。";
        public string ImagingReportText { get => _imagingReportText; set => Set(ref _imagingReportText, value); }

        // ================= 登记点 / 复测历史 / 状态 =================
        public ObservableCollection<TunePoint> Points { get; } = new ObservableCollection<TunePoint>();
        public ObservableCollection<TuneRunRow> Runs { get; } = new ObservableCollection<TuneRunRow>();

        private string _progressText = "就绪";
        public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

        private string _resultText = "尚无结果。登记 ≥2 个点并完成扫描后在此给出倾角与垫高处方。";
        public string ResultText { get => _resultText; set => Set(ref _resultText, value); }

        public ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();

        // ================= v2 引导:模式 / 实时指标显示 =================
        /// <summary>测量方案下拉(0=A 点阵靶 1=B 矩形特征 2=C 正交边)。</summary>
        public List<string> PerpModeNames { get; } = new List<string>
        {
            "A · 点阵靶(单帧,最稳)",
            "B · 矩形特征(工件边)",
            "C · 正交安装边"
        };

        private int _perpModeIndex;
        public int PerpModeIndex
        {
            get => _perpModeIndex;
            set { if (Set(ref _perpModeIndex, value)) ResetGuideEma(); }
        }

        private bool _isGuiding;
        /// <summary>实时引导运行中(启用停止/禁用走位与方案切换)。</summary>
        public bool IsGuiding
        {
            get => _isGuiding;
            private set
            {
                if (Set(ref _isGuiding, value))
                {
                    OnPropertyChanged(nameof(IsNotGuiding));
                    RefreshCommands();
                }
            }
        }
        public bool IsNotGuiding => !_isGuiding;

        private string _guideRxText = "—";
        public string GuideRxText { get => _guideRxText; private set => Set(ref _guideRxText, value); }

        private string _guideRyText = "—";
        public string GuideRyText { get => _guideRyText; private set => Set(ref _guideRyText, value); }

        private string _guideOrthoText = "—";
        public string GuideOrthoText { get => _guideOrthoText; private set => Set(ref _guideOrthoText, value); }

        private string _guideVerdictText = "点『▶ 开始实时引导』:摆好参照(方案 A/B/C)→ 一边手调相机角度一边看读数,调到≈0 即垂直。";
        public string GuideVerdictText { get => _guideVerdictText; private set => Set(ref _guideVerdictText, value); }

        private Brush _guideVerdictBrush = Brushes.Gray;
        public Brush GuideVerdictBrush { get => _guideVerdictBrush; private set => Set(ref _guideVerdictBrush, value); }

        private string _guideDetailText = "尚未测量";
        public string GuideDetailText { get => _guideDetailText; private set => Set(ref _guideDetailText, value); }

        private string _guideStatusText = "空闲";
        public string GuideStatusText { get => _guideStatusText; private set => Set(ref _guideStatusText, value); }

        // ================= 命令 =================
        public ICommand CmdConnect { get; private set; }
        public ICommand CmdDisconnect { get; private set; }
        public ICommand CmdStartPreview { get; private set; }
        public ICommand CmdStopPreview { get; private set; }
        public ICommand CmdSnap { get; private set; }
        public ICommand CmdMoveXp { get; private set; }
        public ICommand CmdMoveXm { get; private set; }
        public ICommand CmdMoveYp { get; private set; }
        public ICommand CmdMoveYm { get; private set; }
        public ICommand CmdRecordPoint { get; private set; }
        public ICommand CmdClearPoints { get; private set; }
        public ICommand CmdStartScan { get; private set; }
        public ICommand CmdStopScan { get; private set; }
        public ICommand CmdSaveReport { get; private set; }
        public ICommand CmdClearLog { get; private set; }
        public ICommand CmdStartGridCheck { get; private set; }
        public ICommand CmdRefreshDevices { get; private set; }
        public ICommand CmdSnapGrade { get; private set; }
        public ICommand CmdAnalyzeImaging { get; private set; }
        public ICommand CmdResetImagingParams { get; private set; }
        public ICommand CmdStartGuide { get; private set; }
        public ICommand CmdStopGuide { get; private set; }

        private void InitCommands()
        {
            CmdConnect = new RelayCommand(_ => ConnectCamera(), _ => SelectedCameraDevice != null && !IsCameraConnected);
            CmdDisconnect = new RelayCommand(_ => DisconnectCamera(), _ => IsCameraConnected);
            // 预览 = 连续模式取流(软触发模式下 StartGrabbing 不出图,绝不能在这里直接起流)
            CmdStartPreview = new RelayCommand(_ => StartPreview(),
                _ => SelectedCameraDevice != null && !IsBusyScanning && !IsGuiding && !IsPreviewing);
            CmdStopPreview = new RelayCommand(_ => StopPreview(), _ => IsPreviewing);

            CmdSnap = new RelayCommand(_ => RunBackground(() =>
            {
                AppendLog("抓一帧并评清晰度(会临时切软触发,完成后自动恢复预览)…");
                var f = CaptureOnce();
                if (f != null)
                {
                    var s = FocusScore.Evaluate(f);
                    AppendLog($"单帧 {f.Width}x{f.Height} {f.PixelFormat} · 清晰度 {s:F1}");
                    RunUi(() => ProgressText = $"当前帧清晰度 {s:F1}(越大越清晰;对焦时盯这个数)");
                }
                else AppendLog("⚠ 未取到新帧(见日志:触发/取流问题)");
                ResumePreviewIfRequested();
            }), _ => SelectedCameraDevice != null && !IsBusyScanning && !IsGuiding);

            CmdMoveXp = MakeJog(() => BindX, 1); CmdMoveXm = MakeJog(() => BindX, -1);
            CmdMoveYp = MakeJog(() => BindY, 1); CmdMoveYm = MakeJog(() => BindY, -1);

            CmdRecordPoint = new RelayCommand(_ => RecordPoint(), _ => !IsBusyScanning && !IsGuiding && SelectedMotionDevice != null);
            CmdClearPoints = new RelayCommand(_ => RunUi(() =>
            {
                Points.Clear();
                ResultText = "已清空登记点。";
                AppendLog("登记点已清空。");
            }), _ => !IsBusyScanning && !IsGuiding && Points.Count > 0);

            CmdStartScan = new RelayCommand(_ => StartScanAsync(), _ =>
                !IsBusyScanning && !IsGuiding && Points.Count >= 2 && IsCameraConnected && SelectedMotionDevice != null);
            CmdStopScan = new RelayCommand(_ => _scanAbortRequested = true, _ => IsBusyScanning);
            CmdSaveReport = new RelayCommand(_ => SaveReport(), _ => Runs.Count > 0 || Points.Count > 0);
            CmdClearLog = new RelayCommand(_ => RunUi(() => Logs.Clear()), _ => Logs.Count > 0);
            CmdStartGridCheck = new RelayCommand(_ => StartGridAsync(), _ =>
                !IsBusyScanning && !IsGuiding && IsCameraConnected && SelectedMotionDevice != null);
            CmdRefreshDevices = new RelayCommand(_ => ReloadDevices(), _ => !IsBusyScanning);
            CmdSnapGrade = new RelayCommand(_ => RunBackground(SnapAndGrade), _ => !IsBusyScanning && !IsGuiding && SelectedCameraDevice != null);
            CmdAnalyzeImaging = new RelayCommand(_ => RunBackground(AnalyzeImaging), _ => !IsBusyScanning && !IsGuiding && SelectedCameraDevice != null);
            CmdResetImagingParams = new RelayCommand(_ => RunUi(() =>
            {
                _paramSyncFlag = true;
                ExposureUs = 8000;
                GainVal = 2.0;
                _paramSyncFlag = false;
                if (SelectedCameraDevice != null)
                {
                    SelectedCameraDevice.SetExposureTime(8000);
                    SelectedCameraDevice.SetGain(2.0);
                }
                AppendLog("成像参数已重置:曝光 8000us / 增益 2.0,并已下发硬件。");
            }), _ => !IsBusyScanning && !IsGuiding && SelectedCameraDevice != null);
            CmdStartGuide = new RelayCommand(_ => StartGuideAsync(), _ =>
                !IsBusyScanning && !IsGuiding && SelectedCameraDevice != null);
            CmdStopGuide = new RelayCommand(_ =>
            {
                _guideAbortRequested = true;
                AppendLog("实时引导停止请求已发出…");
            }, _ => IsGuiding);
        }

        private RelayCommand MakeJog(Func<int> axisGetter, int dir)
        {
            return new RelayCommand(_ =>
            {
                int axis = axisGetter();
                if (SelectedMotionDevice == null) return;
                double step = MoveStep * dir;
                var r = SelectedMotionDevice.MoveRelative(axis, (float)step, 30f);
                AppendLog(r?.Success == true ? $"轴{axis} {(dir > 0 ? "+" : "-")}{Math.Abs(step):F2}mm 已下发" : $"轴{axis} 移动失败: {r?.Message}");
            }, _ => !IsBusyScanning && SelectedMotionDevice != null);
        }

        // ============ 模式② 走位透视比对 · 网格快检(零 Z 依赖) ============
        /// <summary>
        /// 以登记点(需把 Mark 对到视野中心)为网格原点,按步长自动走 9 点/5 点网格,
        /// 每点采帧 + HALCON Mark 提取(与标定采样同算法),拟合 HomMat 输出健康检查
        /// (两轴当量差/剪切/夹角/RMS,与九点标定发布前体检同口径)。适合无 Z 轴、
        /// 相机固定支架装调期的快速预筛;若 ⚠ 项不消除,转 ④ 三点对焦扫描定量调平。
        /// </summary>
        private async void StartGridAsync()
        {
            var origin = Points.FirstOrDefault(p => !double.IsNaN(p.Px) && !double.IsNaN(p.Py));
            var motion = SelectedMotionDevice;
            if (motion == null)
            {
                AppendLog("请先绑定运动设备(承载走位)。");
                return;
            }
            // 免登记:没登记点就直接以"当前位置"为网格原点(把 Mark 放到视野中心即可),
            // 少一步操作;登记过点则用登记点,便于复现同一网格。
            if (origin == null)
            {
                var fx = motion.GetFeedbackPosition(BindX);
                var fy = motion.GetFeedbackPosition(BindY);
                if (fx?.Success != true || fy?.Success != true)
                {
                    AppendLog("⚠ 读不到当前 X/Y 位置,无法确定网格原点。请先把 Mark 对到视野中心并『登记当前为一点』。");
                    return;
                }
                origin = new TunePoint { Name = "当前位置", Px = fx.Data, Py = fy.Data };
                AppendLog($"网格原点取当前位置 X={fx.Data:F2} Y={fy.Data:F2}(请确认 Mark 就在视野中心)。");
            }
            double step = ParseD(GridStepText, 10.0);
            if (step <= 0.01) { AppendLog("步长须 > 0 mm。"); return; }

            var offsets = new List<(double ox, double oy)>();
            if (GridPatternIndex == 1)
            {
                offsets.Add((0, 0)); offsets.Add((-step, 0)); offsets.Add((step, 0));
                offsets.Add((0, -step)); offsets.Add((0, step));
            }
            else
            {
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                        offsets.Add((i * step, j * step));
            }

            IsBusyScanning = true;
            _scanAbortRequested = false;
            _renderEnabled = false;   // 走位期间关预览渲染:把 UI 线程让给流程,也避免和采帧抢帧
            ResultText = "网格走位比对进行中…";
            var pxList = new List<double>(); var pyList = new List<double>();
            var wxList = new List<double>(); var wyList = new List<double>();
            var kind = FeatureTypeIndex == 1 ? CalibrationFeatureType.CircleMark : CalibrationFeatureType.CrossMark;
            int idx = 0;
            try
            {
                await Task.Run(() =>
                {
                    foreach (var (ox, oy) in offsets)
                    {
                        if (_scanAbortRequested) break;
                        idx++;
                        double wx = origin.Px + ox, wy = origin.Py + oy;
                        RunUi(() => ProgressText = $"走位网格 {idx}/{offsets.Count} · 目标 ({wx:F2}, {wy:F2})");
                        if (!MoveToXY(motion, BindX, BindY, (float)wx, (float)wy, out string rejectReason))
                        {
                            AppendLog($"⚠ 第 {idx} 点走位被控制器拒绝(目标 {wx:F1},{wy:F1})：{rejectReason} —— 该点跳过(网格点或已推出 SCARA 可达环带，可减小步长或平移网格原点)。");
                            continue;
                        }
                        var frame = CaptureOnce();
                        if (frame == null) { AppendLog($"⚠ 第 {idx} 点采图失败,跳过。"); continue; }
                        var ri = _renderService.WrapImage(frame) as HalconRenderImage;
                        var ext = ri == null ? null : _calib.ExtractFeaturePointByType(ri, kind, idx, -1, -1, forceFullImage: true);
                        if (ri != null) { try { ri.Dispose(); } catch { } }
                        if (ext == null || !ext.Success)
                        {
                            AppendLog($"⚠ 第 {idx} 点({wx:F1},{wy:F1}) Mark 提取失败: {ext?.Message ?? "未知"}——可能出视野/特征不完整,试试减小步长或换特征类型。");
                            continue;
                        }
                        pxList.Add(ext.Data.PixelX); pyList.Add(ext.Data.PixelY);
                        wxList.Add(wx); wyList.Add(wy);
                        RunUi(() => AppendLog($"第 {idx} 点 Mark 命中: px={ext.Data.PixelX:F1} py={ext.Data.PixelY:F1} @ ({wx:F2},{wy:F2})"));
                    }

                    RunUi(() =>
                    {
                        if (_scanAbortRequested) { ProgressText = "已中止。"; return; }
                        if (pxList.Count < 4)
                        {
                            ProgressText = "成功点不足 4 个,无法拟合。";
                            ResultText = "⚠ 成功提取点不足 4 个。请确认:① 步长过大导致 Mark 每步后出视野(视野需 ≥2×步长);② 特征类型选对(十字/圆点);③ 画面无大面积反光/干扰。";
                            return;
                        }
                        ProgressText = "网格走位完成,计算健康检查…";
                        var calc = _calib.CalcNinePointHomMat(pxList.ToArray(), pyList.ToArray(), wxList.ToArray(), wyList.ToArray());
                        if (!calc.Success)
                        {
                            ResultText = "矩阵计算失败: " + calc.Message;
                            return;
                        }
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine("【走位透视比对 · 快检】成功点 " + pxList.Count + "/" + offsets.Count + " · 步长 "
                            + step.ToString("F1", CultureInfo.InvariantCulture) + " mm · 特征 " + (kind == CalibrationFeatureType.CircleMark ? "圆点" : "十字"));
                        sb.AppendLine("口径与九点标定健康检查一致;带 ⚠ 的项=相机/轴系未正射:");
                        sb.AppendLine(calc.Data.HealthReport ?? "(无报告文本)");
                        sb.AppendLine("RMS 重投影: " + calc.Data.RmsError.ToString("F4", CultureInfo.InvariantCulture) + " mm");
                        sb.AppendLine("→ 两轴当量差大/剪切/夹角≠90°/残差中心好四角大:相机斜视或轴不垂直。先用 ④ 三点对焦扫描定量调平,再复测本快检至 ⚠ 消除(调不平则该槽考虑 LensDistortion+斜拍语义)。");
                        ResultText = sb.ToString();
                        Runs.Insert(0, new TuneRunRow
                        {
                            Time = DateTime.Now.ToString("HH:mm:ss"),
                            Label = "[走位快检] " + pxList.Count + "点 · RMS " + calc.Data.RmsError.ToString("F3", CultureInfo.InvariantCulture) + "mm",
                            Detail = "健康检查见报告区",
                            Pass = pxList.Count >= offsets.Count - 1
                        });
                    });
                });
            }
            catch (Exception ex)
            {
                AppendLog("走位比对异常: " + ex.Message);
            }
            finally
            {
                IsBusyScanning = false;
                ResumePreviewIfRequested();
            }
        }

        /// <summary>双轴绝对走位(任一轴被拒即返回 false,由调用方跳点)。</summary>
        /// <summary>双轴绝对走位（低速）。被拒时经 rejectReason 透传控制器原始消息（含错误码）。</summary>
        private static bool MoveToXY(IMotionCard motion, int axisX, int axisY, float wx, float wy, out string rejectReason)
        {
            rejectReason = null;
            var rx = motion.MoveAbsolute(axisX, wx, 40f);
            if (rx == null || !rx.Success) { rejectReason = rx?.Message ?? "X 轴无应答"; return false; }
            WaitAxisSettled(motion, axisX, wx);
            var ry = motion.MoveAbsolute(axisY, wy, 40f);
            if (ry == null || !ry.Success) { rejectReason = ry?.Message ?? "Y 轴无应答"; return false; }
            WaitAxisSettled(motion, axisY, wy);
            return true;
        }

        // ================= 设备加载与切换 =================
        /// <summary>相机列表为空(供界面空态提示)</summary>
        public bool CameraEmpty => CameraDeviceList.Count == 0;
        /// <summary>运动设备列表为空(供界面空态提示)</summary>
        public bool MotionEmpty => MotionDeviceList.Count == 0;

        /// <summary>从设备池即时拉取并按接口类型筛选填充(相机 ICamera / 运动 IMotionCard)。</summary>
        private void LoadDevices()
        {
            var all = _devicePool.GetAllDevices()?.ToArray() ?? Array.Empty<IDevice>();

            CameraDeviceList.Clear();
            foreach (var cam in all.OfType<ICamera>()) CameraDeviceList.Add(cam);

            MotionDeviceList.Clear();
            foreach (var m in all.OfType<IMotionCard>()) MotionDeviceList.Add(m);

            OnPropertyChanged(nameof(CameraEmpty));
            OnPropertyChanged(nameof(MotionEmpty));

            SelectedCameraDevice = CameraDeviceList.FirstOrDefault();
            SelectedMotionDevice = MotionDeviceList.FirstOrDefault();
            UpdateCamStatus();
        }

        /// <summary>手动刷新:重新从设备池拉取一次并输出诊断(供『🔄 刷新设备』按钮)。</summary>
        public void ReloadDevices()
        {
            if (_cleaned) return;
            LoadDevices();
            LogDeviceSummary();
            RefreshCommands();
        }

        /// <summary>页面首次呈现时调用:若设备池仍在初始化(启动期秒开页面),自动补拉数次直到出现设备。</summary>
        public async void ReloadDevicesWhenReady()
        {
            if (_cleaned) return;
            for (int i = 0; i < 6 && !_cleaned; i++)
            {
                LoadDevices();
                if (CameraDeviceList.Count > 0 || MotionDeviceList.Count > 0)
                {
                    LogDeviceSummary();
                    return;
                }
                await Task.Delay(400);
            }
            if (!_cleaned) LogDeviceSummary();
        }

        private void LogDeviceSummary()
        {
            var all = _devicePool.GetAllDevices()?.ToArray() ?? Array.Empty<IDevice>();
            if (all.Length == 0)
            {
                AppendLog("⚠ 设备池为空:尚未登记任何设备。请到『核心工程配置 → 设备管理』扫描/登记相机与运动设备,再回本页点『刷新设备』。");
                return;
            }
            if (CameraDeviceList.Count == 0 || MotionDeviceList.Count == 0)
            {
                var missing = new List<string>();
                if (CameraDeviceList.Count == 0) missing.Add("相机(ICamera)");
                if (MotionDeviceList.Count == 0) missing.Add("运动/机器人(IMotionCard)");
                AppendLog($"⚠ 设备池共 {all.Length} 台,但缺少 {string.Join("、", missing)}——请到『设备管理』登记对应类型后点『刷新设备』。" +
                          $"当前命中:相机 {CameraDeviceList.Count} 台 / 运动 {MotionDeviceList.Count} 台。");
                return;
            }
            AppendLog($"已从设备池加载:相机 {CameraDeviceList.Count} 台 / 运动 {MotionDeviceList.Count} 台。");
        }

        private void OnCameraChanged(ICamera oldCam, ICamera newCam)
        {
            if (oldCam != null)
            {
                oldCam.StopGrabbing();
                oldCam.FrameReceived -= OnCameraFrameReceived;
                oldCam.StateChanged -= OnCameraStateChanged;
            }
            _triggerMode = -1;
            _previewRequested = false;
            if (newCam == null)
            {
                IsCameraConnected = false; IsGrabbing = false;
                UpdateCamStatus();
                RefreshCommands();
                return;
            }
            newCam.FrameReceived += OnCameraFrameReceived;
            newCam.StateChanged += OnCameraStateChanged;
            // ★ 关键修复:相机可能已被别处(工站/硬件调试台)连上,此时不会再发 StateChanged。
            //   旧版不主动同步状态 → IsCameraConnected 恒为 false → "开始预览"永久置灰,
            //   现场观感就是"这页完全没法用"。这里按设备实际状态直接对齐。
            IsCameraConnected = newCam.State == DeviceState.Connected;
            IsGrabbing = false;
            UpdateCamStatus();
            RefreshCommands();
        }

        private void OnMotionChanged(IMotionCard oldM, IMotionCard newM)
        {
            if (oldM != null) oldM.StateChanged -= OnMotionStateChanged;
            if (newM != null) newM.StateChanged += OnMotionStateChanged;
            RefreshCommands();
        }

        private void OnCameraStateChanged(object sender, DeviceState state)
        {
            RunUi(() =>
            {
                IsCameraConnected = state == DeviceState.Connected;
                if (!IsCameraConnected) { IsGrabbing = false; _triggerMode = -1; }
                UpdateCamStatus();
                RefreshCommands();
            });
        }

        private void OnMotionStateChanged(object sender, DeviceState state) { }

        /// <summary>是否正在连续预览(取流中且处于连续模式)</summary>
        public bool IsPreviewing => IsGrabbing && _triggerMode == 0;

        /// <summary>确保相机已连接(已连则直接成功;连接动作只在未连时执行)。</summary>
        private bool EnsureCameraConnected(out string message)
        {
            message = null;
            var cam = SelectedCameraDevice;
            if (cam == null) { message = "未选择相机"; return false; }
            if (IsCameraConnected && cam.State == DeviceState.Connected) return true;
            var r = cam.Connect();
            if (r == null || !r.Success) { message = r?.Message ?? "无应答"; return false; }
            IsCameraConnected = true;
            AppendLog($"相机已连接: {cam.DeviceName}");
            return true;
        }

        private void ConnectCamera()
        {
            if (SelectedCameraDevice == null) return;
            if (!EnsureCameraConnected(out var msg))
            {
                AppendLog($"相机连接失败: {msg}");
                MessageBox.Show("相机连接失败: " + msg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                RefreshCommands();
                return;
            }
            RefreshCommands();
            // 连上直接开预览:少一步点击,也顺带证明"链路是通的"
            StartPreview();
        }

        private void DisconnectCamera()
        {
            _previewRequested = false;
            SelectedCameraDevice?.StopGrabbing();
            SelectedCameraDevice?.Disconnect();
            IsGrabbing = false;
            IsCameraConnected = false;
            _triggerMode = -1;
            RunUi(() => CameraDisplayVm.Clear());
            UpdateCamStatus();
            AppendLog("相机已断开。");
            RefreshCommands();
        }

        // ================= 取流:连续预览 / 软触发采集 的切换中枢 =================
        /// <summary>
        /// 切到连续模式并起流(预览 + 实时引导用)。
        /// 必须显式 SetTriggerMode(0):若相机停在软触发,StartGrabbing 只会"武装"不会出图。
        /// </summary>
        private void StartPreview()
        {
            var cam = SelectedCameraDevice;
            if (cam == null) return;
            if (!EnsureCameraConnected(out var msg))
            {
                AppendLog("⚠ 无法开预览:" + msg);
                UpdateCamStatus();
                RefreshCommands();
                return;
            }

            _previewRequested = true;
            if (_triggerMode != 0)
            {
                var mr = cam.SetTriggerMode(0);
                if (mr == null || !mr.Success)
                {
                    AppendLog("⚠ 切连续模式失败:" + (mr?.Message ?? "无应答") + " —— 预览可能不出图,请检查相机是否被占用。");
                }
                _triggerMode = 0;
            }
            if (!IsGrabbing)
            {
                var sr = cam.StartGrabbing();
                IsGrabbing = sr?.Success == true;
                if (!IsGrabbing) AppendLog("⚠ 启动取流失败:" + (sr?.Message ?? "无应答"));
            }
            RunUi(() => _renderEnabled = true);
            UpdateCamStatus();
            if (IsPreviewing) AppendLog("预览已开启(连续取流)。");
            RefreshCommands();
        }

        /// <summary>停预览:只停流,保持连接(随时可再点开预览)。</summary>
        private void StopPreview()
        {
            _previewRequested = false;
            SelectedCameraDevice?.StopGrabbing();
            IsGrabbing = false;
            UpdateCamStatus();
            AppendLog("预览已停止(相机仍保持连接)。");
            RefreshCommands();
        }

        /// <summary>
        /// 切到软触发模式(采帧/扫描用),仅在必要时下发。
        /// 标定向导的教训:SetTriggerMode(1) 只写 TriggerMode/TriggerSource,若相机固件停在
        /// 连续自由流,触发要么被吞、要么等一个随机出帧时刻 —— 必须走 ConfigureSoftwareTrigger()。
        /// </summary>
        private void EnsureTriggered(ICamera cam)
        {
            if (_triggerMode == 1) return;
            var r = cam.ConfigureSoftwareTrigger();
            if (r == null || !r.Success)
            {
                AppendLog("⚠ 软触发配置未完全生效(" + (r?.Message ?? "无应答") + "),退化为基础软触发模式。");
                cam.SetTriggerMode(1);
            }
            _triggerMode = 1;
            UpdateCamStatus();
        }

        /// <summary>采帧结束后:若用户要预览,把流切回连续,避免预览停在黑屏。</summary>
        private void ResumePreviewIfRequested()
        {
            if (!_previewRequested || _cleaned) return;
            if (_triggerMode != 0 || !IsGrabbing)
            {
                var cam = SelectedCameraDevice;
                if (cam == null) return;
                cam.SetTriggerMode(0);
                _triggerMode = 0;
                if (!IsGrabbing)
                {
                    var sr = cam.StartGrabbing();
                    IsGrabbing = sr?.Success == true;
                }
            }
            RunUi(() => _renderEnabled = true);
            UpdateCamStatus();
        }

        /// <summary>把"相机/取流/模式/帧率"汇总成一行状态,界面顶部常显(旧版这些信息全藏在日志里)。</summary>
        private void UpdateCamStatus()
        {
            string link;
            Brush brush;
            if (!IsCameraConnected) { link = "未连接"; brush = Brushes.Gray; }
            else if (!IsGrabbing) { link = "已连接 · 未取流"; brush = Brushes.Gold; }
            else if (_triggerMode == 1) { link = "取流中 · 软触发(采帧)"; brush = Brushes.DeepSkyBlue; }
            else { link = "取流中 · 连续预览"; brush = Brushes.LimeGreen; }

            string text = "相机:" + (SelectedCameraDevice?.DeviceName ?? "—") + " · " + link;
            // 本方法可能被后台线程(采帧/扫描)调用,属性写入一律回 UI 线程,避免跨线程绑定异常
            RunUi(() =>
            {
                CamStatusText = text;
                CamStatusBrush = brush;
                OnPropertyChanged(nameof(IsPreviewing));
            });
        }

        // ================= 帧回调:登记最新帧 + 限帧上屏 =================
        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            if (e?.Buffer == null || e.Width <= 0 || e.Height <= 0) return;

            // ---- 帧率统计:约 2 秒刷一次状态条(旧版连"有没有在出图"都看不出来) ----
            var now = DateTime.Now;
            _fpsFrames++;
            if ((now - _fpsWindowStart).TotalSeconds >= 2.0)
            {
                double fps = _fpsFrames / (now - _fpsWindowStart).TotalSeconds;
                _fpsFrames = 0; _fpsWindowStart = now;
                string info = $"{e.Width}×{e.Height} {e.PixelFormat} · 约 {fps:F1} fps";
                RunUi(() => FrameInfoText = info);
            }

            // ---- 只在"确实有人在等这一帧"时克隆。
            //      整幅克隆很贵(5MP Mono8 ≈ 5MB/帧),预览时每帧克隆纯属白烧 CPU;
            //      但采帧/引导必须拿副本 —— 回调线程与扫描线程并发,SDK 会复用 Buffer。
            bool needForCapture = _captureWaitActive;
            bool needForGuide = IsGuiding && !_guideFramePending;
            if (needForCapture || needForGuide)
            {
                _latestFrame = new FrameEventArgs
                {
                    Buffer = (byte[])e.Buffer.Clone(),
                    Width = e.Width,
                    Height = e.Height,
                    PixelFormat = e.PixelFormat,
                    Timestamp = e.Timestamp,
                    FrameNum = e.FrameNum,
                    NativePointer = e.NativePointer
                };
                if (needForGuide) _guideFramePending = true;
                _frameArrivedEvent.Set();
            }

            // ---- 上屏(约 20 FPS 限帧);扫描/走位期间关渲染,把 UI 线程让给流程 ----
            if (!_renderEnabled) return;
            if ((now - _lastRenderTime).TotalMilliseconds < 50) return;
            _lastRenderTime = now;

            var frame = e;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var renderImage = _renderService.WrapImage(frame);
                    if (renderImage == null) return;
                    var ctx = new WpfImageRenderContext
                    {
                        NodeId = "camera-tune",
                        NodeName = "相机实时图像",
                        Image = renderImage
                    };
                    var old = CameraDisplayVm.ActiveImageContext;
                    CameraDisplayVm.ActiveImageContext = ctx;
                    old?.Dispose();
                }
                catch { /* 单帧渲染失败忽略 */ }
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        // ================= 单帧采集(不吃旧帧:切软触发 → 触发 → 等新帧) =================
        /// <summary>
        /// 取一帧"刚刚触发出来的"新图。语义与标定向导一致:
        ///   ① 相机侧先 ConfigureSoftwareTrigger(),确保真在软触发且关掉帧率限制;
        ///   ② 起流(软触发模式下 StartGrabbing 只是"武装",不出图);
        ///   ③ Reset 事件 → 软触发 → 等帧;首帧给 2500ms 容忍 GigE 冷启动,重试 800ms 快速重触发
        ///      (丢帧的那一帧不会再来,干等无意义);
        ///   ④ 失败宁可返回 null,绝不沿用上一张旧图 —— 走位后旧图 = 上一位置的坐标。
        /// </summary>
        private FrameEventArgs CaptureOnce()
        {
            var cam = SelectedCameraDevice;
            if (cam == null) return null;
            if (!EnsureCameraConnected(out var connMsg))
            {
                AppendLog("相机连接失败:" + connMsg);
                return null;
            }

            EnsureTriggered(cam);

            if (!IsGrabbing)
            {
                var sr = cam.StartGrabbing();
                IsGrabbing = sr?.Success == true;
                if (!IsGrabbing)
                {
                    AppendLog("⚠ 启动采集流失败:" + (sr?.Message ?? "无应答") + "(相机可能被其它页面占用)");
                    return null;
                }
            }

            const int maxAttempts = 5;
            const int firstWaitMs = 2500;
            const int retryWaitMs = 800;
            _captureWaitActive = true;
            try
            {
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    int waitMs = attempt == 1 ? firstWaitMs : retryWaitMs;
                    _frameArrivedEvent.Reset();
                    _latestFrame = null;

                    var trig = cam.SoftTrigger();
                    if (trig == null || !trig.Success) trig = cam.SoftwareTrigger();
                    if (trig == null || !trig.Success)
                    {
                        if (attempt == maxAttempts)
                            AppendLog($"⚠ 软触发指令失败({trig?.Message ?? "无应答"}):相机未武装或未起流。");
                        Thread.Sleep(150);
                        continue;
                    }

                    if (_frameArrivedEvent.WaitOne(waitMs) && _latestFrame != null) return _latestFrame;
                    if (attempt < maxAttempts) Thread.Sleep(80);
                }
            }
            finally
            {
                _captureWaitActive = false;
            }

            AppendLog($"⚠ 软触发 {maxAttempts} 次均未等到新帧 —— 本次采图放弃(不沿用旧图,避免坐标错乱)。");
            return null;
        }

        // ================= 成像与打光体检:采帧评分 / 报告处方 =================
        private void SnapAndGrade()
        {
            AppendLog("采帧评分…");
            var f = CaptureOnce();
            if (f == null)
            {
                RunUi(() => FrameGradeText = "⚠ 未取到新帧(见日志):检查相机连接/触发/光源频闪。");
                ResumePreviewIfRequested();
                return;
            }
            var fs = FocusScore.Evaluate(f);
            var m = ImagingMetrics.Analyze(f);
            string grade = $"清晰度 {fs:F0} · 亮度均值 {m.Mean:F1} · 过曝 {m.OverPct:F1}% · 欠曝 {m.UnderPct:F1}% · 对比σ {m.Std:F1} · 均匀CV {m.BlockCv:F1}%";
            RunUi(() =>
            {
                FrameGradeText = grade;
                ProgressText = $"成像采样完成:清晰度 {fs:F0}(对焦参照) · 亮度均值 {m.Mean:F1}(目标≈中灰 100~180)";
            });
            AppendLog($"采样 {f.Width}x{f.Height}:清晰度 {fs:F0} / 均值 {m.Mean:F1} / 过曝 {m.OverPct:F1}% / 欠曝 {m.UnderPct:F1}% / 分块CV {m.BlockCv:F1}%");
            ResumePreviewIfRequested();
        }

        private void AnalyzeImaging()
        {
            AppendLog("成像体检:采帧并分析…");
            var f = CaptureOnce();
            if (f == null)
            {
                RunUi(() => ImagingReportText = "⚠ 未取到新帧,无法分析。请先确认相机可正常采图。");
                ResumePreviewIfRequested();
                return;
            }
            var fs = FocusScore.Evaluate(f);
            var m = ImagingMetrics.Analyze(f);
            var report = BuildImagingReport(f, fs, m);
            RunUi(() => ImagingReportText = report);
            AppendLog("成像体检完成:逐项判定与打光处方已生成(见体检报告)。");
            ResumePreviewIfRequested();
        }

        /// <summary>把当前帧指标转成逐项 ✅/⚠ 判定 + 可操作处方(面向打光/曝光调试,不给模糊结论)。</summary>
        private static string BuildImagingReport(FrameEventArgs f, double fs, ImagingMetrics.Report m)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"成像与打光体检 {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 帧 {f.Width}x{f.Height}");
            sb.AppendLine("── 亮度与曝光 ──");
            sb.AppendLine(Line($"亮度均值 {m.Mean:F0}", m.Mean < 60 ? "偏暗:工件/背景欠亮,先增曝光或加强光源" :
                                         m.Mean > 200 ? "偏亮:接近饱和,先减曝光或减弱光源" : "适中 ✅"));
            sb.AppendLine(Line($"过曝占比 {m.OverPct:F1}%(灰阶≥245,无细节区)", m.OverPct > 2 ? "偏高 ⚠:降曝光或减光强/压暗高光面,过曝区无特征可用" : "正常 ✅"));
            sb.AppendLine(Line($"欠曝占比 {m.UnderPct:F1}%(灰阶≤10)", m.UnderPct > 5 ? "偏多 ⚠:增曝光或加强光,否则暗部噪点大、Mark 易丢" : "正常 ✅"));
            sb.AppendLine("── 对比度(σ) ──");
            sb.AppendLine(Line($"σ={m.Std:F0}", m.Std < 28 ? "反差弱 ⚠:工件与背景灰度太近或打光过平,调角度造阴影/用背光" :
                                         m.Std > 90 ? "反差过硬 ⚠:高光与死黑并存,注意边缘过曝" : "适中 ✅"));
            sb.AppendLine("── 均匀性(3×3 分块亮度,打光是否铺匀) ──");
            double avg = (m.BlockMeans[0] + m.BlockMeans[4] + m.BlockMeans[8]) / 3.0 + 1e-9;
            double col0 = (m.BlockMeans[0] + m.BlockMeans[3] + m.BlockMeans[6]) / 3.0;
            double col2 = (m.BlockMeans[2] + m.BlockMeans[5] + m.BlockMeans[8]) / 3.0;
            double row0 = (m.BlockMeans[0] + m.BlockMeans[1] + m.BlockMeans[2]) / 3.0;
            double row2 = (m.BlockMeans[6] + m.BlockMeans[7] + m.BlockMeans[8]) / 3.0;
            double lr = (col0 - col2) / Math.Max(1e-6, (col0 + col2) / 2.0) * 100.0;
            double tb = (row0 - row2) / Math.Max(1e-6, (row0 + row2) / 2.0) * 100.0;
            sb.AppendLine(Line($"分块CV {m.BlockCv:F1}%", m.BlockCv > 8 ? "不均 ⚠:打光没铺匀(先看左右/前后差定位暗侧)" : "均匀 ✅"));
            if (Math.Abs(lr) >= 8) sb.AppendLine($"  左右差 {lr:+0;-0;0:F1}% → 画面{(lr > 0 ? "右" : "左")}侧偏暗:调该侧光源强度/角度,或整体往暗侧偏移布光;检查工件自身阴影。");
            if (Math.Abs(tb) >= 8) sb.AppendLine($"  前后差 {tb:+0;-0;0:F1}% → 画面上{(tb > 0 ? "下" : "上")}侧偏暗:同上处理;确认无镜筒暗角/遮挡。");
            sb.AppendLine("── 清晰度与杂项 ──");
            sb.AppendLine($"  梯度能量 {fs:F0}:与『📐 垂直度装调』三点对焦的峰值口径一致;若调光后明显下降,查对焦/曝光时间过短拖影/频闪。");
            sb.AppendLine("── 操作建议(照顺序) ──");
            sb.AppendLine("  1) 均值明显偏暗/偏亮 → 先调曝光(优先)再增益,边调边采帧看均值到 100~180;");
            sb.AppendLine("  2) 过曝/欠曝超阈值 → 按上面判定加减光或曝光,过曝区无特征必须消除;");
            sb.AppendLine("  3) 均匀性差 → 按左右/前后差方向补光或调角度,复测至 CV ≤ 8%;");
            sb.AppendLine("  4) 都达标后再回『📐 垂直度装调』跑走位快检/三点对焦,确认光学与机械两关都过才进标定。");
            return sb.ToString();
        }

        private static string Line(string label, string verdict) => "  · " + label + " → " + verdict;

        // ================= 登记点 =================
        private void RecordPoint()
        {
            if (SelectedMotionDevice == null) return;
            var px = SelectedMotionDevice.GetFeedbackPosition(BindX);
            var py = SelectedMotionDevice.GetFeedbackPosition(BindY);
            var pz = SelectedMotionDevice.GetFeedbackPosition(BindZ);
            double x = px?.Success == true ? px.Data : double.NaN;
            double y = py?.Success == true ? py.Data : double.NaN;
            double z = pz?.Success == true ? pz.Data : double.NaN;
            string name = "点" + (Points.Count + 1);
            var pt = new TunePoint
            {
                Name = name,
                Px = x,
                Py = y,
                Pz = z,
                BestZ = double.NaN,
                PeakScore = double.NaN,
                CurveSummary = "未扫描"
            };
            Points.Add(pt);
            AppendLog($"登记 {name}: X={x:F3} Y={y:F3} Z={z:F3}(当前)。建议把靶移到与上一点拉开的位置(构成三角/覆盖视野两侧)。");
            if (Points.Count >= 3) AppendLog("三点已齐(≥2 点即可扫,3 点可同时给左右/前后双向倾角)。可点『开始三点扫描』。");
        }

        // ================= 三点 Z 扫描(后台线程,全程不卡 UI) =================
        private async void StartScanAsync()
        {
            IsBusyScanning = true;
            _scanAbortRequested = false;
            _renderEnabled = false;   // 扫描期间关预览渲染(每步都在采图,渲染只会拖慢并抢帧)
            var motion = SelectedMotionDevice;
            int zAxis = BindZ;
            double half = Math.Max(ScanRange, ScanStep);
            int k = (int)Math.Max(1, Math.Round(half / ScanStep));
            double step = (half / k);

            ResultText = "扫描进行中…";
            ProgressText = "准备扫描…";
            try
            {
                var snapshot = Points.ToList();
                await Task.Run(() =>
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        if (_scanAbortRequested) break;
                        var p = snapshot[i];
                        RunUi(() => ProgressText = $"扫描 {p.Name}({i + 1}/{snapshot.Count})…读取当前 Z");

                        float z0 = ReadAxisPos(motion, zAxis);
                        var curve = new List<(double z, double s)>();
                        for (int sIdx = -k; sIdx <= k; sIdx++)
                        {
                            if (_scanAbortRequested) break;
                            double target = z0 + sIdx * step;
                            RunUi(() => ProgressText = $"扫描 {p.Name} · Z {target:F3} (步 {sIdx + k + 1}/{2 * k + 1})");
                            if (Math.Abs(target - ReadAxisPos(motion, zAxis)) > 0.01)
                            {
                                var mv = motion.MoveAbsolute(zAxis, (float)target, 25f);
                                if (mv == null || !mv.Success)
                                {
                                    AppendLog($"⚠ {p.Name} 走到 Z={target:F3} 被拒({mv?.Message});该步跳过。");
                                    continue;
                                }
                                WaitAxisSettled(motion, zAxis, (float)target);
                            }
                            var f = CaptureOnce();
                            if (f == null) continue;
                            double score = FocusScore.Evaluate(f);
                            curve.Add((target, score));
                        }

                        if (_scanAbortRequested) break;

                        // 抛物线峰值(峰值邻域 ±2 点最小二乘),端点峰提示扩范围
                        double bestZ = z0, bestScore = -1;
                        int bestIdx = -1;
                        for (int ci = 0; ci < curve.Count; ci++)
                        {
                            if (curve[ci].s > bestScore)
                            {
                                bestScore = curve[ci].s;
                                bestZ = curve[ci].z;
                                bestIdx = ci;
                            }
                        }
                        bool atEdge = curve.Count > 0 && (bestIdx <= 0 || bestIdx >= curve.Count - 1);
                        if (curve.Count >= 3 && !atEdge)
                        {
                            int bi = bestIdx;
                            var win = curve.Skip(Math.Max(0, bi - 2)).Take(Math.Min(5, curve.Count)).ToList();
                            var pk = FitParabola(win);
                            if (pk != null && !double.IsNaN(pk.Value.z) && pk.Value.z >= z0 - half - step && pk.Value.z <= z0 + half + step)
                            {
                                bestZ = pk.Value.z;
                            }
                        }
                        var final = p;
                        RunUi(() =>
                        {
                            final.BestZ = bestZ;
                            final.PeakScore = bestScore;
                            final.CurveSummary = $"{curve.Count} 帧 · 峰清晰度 {bestScore:F0}";
                            if (atEdge && curve.Count > 0) final.CurveSummary += " ⚠ 峰在扫描端点,建议加大范围";
                            AppendLog($"{final.Name} 完成:最清晰 Z={bestZ:F3},清晰度 {bestScore:F0}" + (atEdge ? " ⚠峰在端点" : ""));
                        });
                    }

                    // 汇总
                    RunUi(() =>
                    {
                        if (_scanAbortRequested)
                        {
                            ProgressText = "已中止(已扫点仍参与结果)。";
                            AppendLog("扫描被中止。");
                        }
                        else
                        {
                            ProgressText = "扫描完成。";
                            AppendLog("全部点扫描完成。");
                        }
                        BuildReport();
                    });
                });
            }
            catch (Exception ex)
            {
                AppendLog("扫描异常: " + ex.Message);
                Debug.WriteLine($"[CameraTune] scan exception: {ex}");
            }
            finally
            {
                IsBusyScanning = false;
                ResumePreviewIfRequested();
            }
        }

        /// <summary>窗口内点最小二乘抛物线 y=a·z²+b·z+c,返回顶点 z。点数不足/病态返回 null。</summary>
        private static (double z, double a)? FitParabola(List<(double z, double s)> win)
        {
            if (win.Count < 3) return null;
            double sx = 0, sy = 0, sxx = 0, sxy = 0, sxxx = 0, sxxy = 0, sxxxx = 0;
            foreach (var (z, s) in win)
            {
                double z2 = z * z;
                sx += z; sy += s; sxx += z2; sxy += z * s;
                sxxx += z2 * z; sxxy += z2 * s; sxxxx += z2 * z2;
            }
            int n = win.Count;
            double det = n * (sxx * sxxxx - sxxx * sxxx) - sx * (sx * sxxxx - sxxx * sxx) + sxx * (sx * sxxx - sxx * sxx);
            if (Math.Abs(det) < 1e-12) return null;
            double a = (sy * (sxx * sxxxx - sxxx * sxxx) - sxy * (sx * sxxxx - sxxx * sxx) + sxxy * (sx * sxxx - sxx * sxx)) / det;
            double b = (n * (sxy * sxxxx - sxxx * sxxy) - sx * (sy * sxxxx - sxxx * sxxy) + sxx * (sy * sxxx - sxx * sxy)) / det;
            if (Math.Abs(a) < 1e-12) return null;
            return (-b / (2 * a), a);
        }

        private float ReadAxisPos(IMotionCard motion, int axis)
        {
            var r = motion.GetFeedbackPosition(axis);
            return r?.Success == true ? r.Data : 0f;
        }

        /// <summary>等待轴到位(优先 IsAxisIdle,回退位置差,最长 8s)。</summary>
        private static void WaitAxisSettled(IMotionCard motion, int axis, float target)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000)
            {
                var idle = motion.IsAxisIdle(axis);
                if (idle?.Success == true && idle.Data) return;
                var pos = motion.GetFeedbackPosition(axis);
                if (pos?.Success == true && Math.Abs(pos.Data - target) < 0.02) return;
                Thread.Sleep(50);
            }
        }

        // ================= 报告:倾角 / 垫高 / 收敛判定 =================
        private void BuildReport()
        {
            var pts = Points.Where(p => !double.IsNaN(p.BestZ) && !double.IsNaN(p.Px) && !double.IsNaN(p.Py)).ToList();
            if (pts.Count < 2)
            {
                ResultText = "可计算的点不足 2 个,请先完成扫描。";
                return;
            }
            double x0 = pts.Average(p => p.Px), y0 = pts.Average(p => p.Py);
            double b = double.NaN, c = double.NaN; // b:Z 随 X 变化率;c:Z 随 Y 变化率
            if (pts.Count >= 3)
            {
                // 最小二乘平面 Z = a + b·dx + c·dy
                double sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
                foreach (var p in pts)
                {
                    double dx = p.Px - x0, dy = p.Py - y0, dz = p.BestZ;
                    sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
                    sxz += dx * dz; syz += dy * dz;
                }
                double det = sxx * syy - sxy * sxy;
                if (Math.Abs(det) > 1e-12)
                {
                    b = (sxz * syy - syz * sxy) / det;
                    c = (syz * sxx - sxz * sxy) / det;
                }
            }
            if (double.IsNaN(b) || double.IsNaN(c))
            {
                // 两点:按主方向退化
                var p1 = pts[0]; var p2 = pts[1];
                double dx = p2.Px - p1.Px, dy = p2.Py - p1.Py, dz = p2.BestZ - p1.BestZ;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1e-6)
                {
                    double along = (dz / len);
                    b = along * (dx / len);
                    c = along * (dy / len);
                }
            }

            double zMin = pts.Min(p => p.BestZ), zMax = pts.Max(p => p.BestZ);
            double dZ = zMax - zMin;
            bool pass = dZ <= FocusTol;
            double tiltXdeg = double.IsNaN(c) ? 0 : Math.Atan(c) * 180.0 / Math.PI; // Y 方向斜率 → 相机绕 X 轴
            double tiltYdeg = double.IsNaN(b) ? 0 : Math.Atan(b) * 180.0 / Math.PI; // X 方向斜率 → 相机绕 Y 轴

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(pass ? "✅ 已垂直:三点最清晰 Z 差在容差内,可进入标定。" : "⚠ 未垂直:需按处方调整后『复测』。");
            sb.AppendLine("── 三点最清晰 Z ──");
            foreach (var p in pts) sb.AppendLine($"  {p.Name}: Z*={p.BestZ:F3}  ({p.CurveSummary})");
            sb.AppendLine($"  最大差 ΔZ = {dZ:F3} mm  (容差 {FocusTol:F2} mm)");
            sb.AppendLine("── 倾角(拟合平面 Z=a+b·X+c·Y) ──");
            sb.AppendLine("  沿 X 向(左右)焦面斜率 b=" + Fmt(b) + " → 倾角 " + tiltYdeg.ToString("F2", CultureInfo.InvariantCulture) + "°");
            sb.AppendLine("  沿 Y 向(前后)焦面斜率 c=" + Fmt(c) + " → 倾角 " + tiltXdeg.ToString("F2", CultureInfo.InvariantCulture) + "°");
            sb.AppendLine("── 调整处方(安装孔距 " + MountSpacing.ToString("F0") + " mm 计) ──");
            if (!double.IsNaN(b) && Math.Abs(b) > 1e-6)
            {
                double shimX = Math.Abs(b) * MountSpacing;
                string hx = b > 0 ? "高" : "低";
                sb.AppendLine("  · 左右向:单边垫厚参考 ≈ " + shimX.ToString("F2", CultureInfo.InvariantCulture)
                    + " mm;X 增大侧 Z 更" + hx + ",垫/压的方向以现场试一圈为准。");
            }
            if (!double.IsNaN(c) && Math.Abs(c) > 1e-6)
            {
                double shimY = Math.Abs(c) * MountSpacing;
                string hy = c > 0 ? "高" : "低";
                sb.AppendLine("  · 前后向:单边垫厚参考 ≈ " + shimY.ToString("F2", CultureInfo.InvariantCulture)
                    + " mm;Y 增大侧 Z 更" + hy + ",同左。");
            }
            sb.AppendLine("  · 操作:松相机安装螺钉 → 按垫厚参考垫/压对应侧 → 锁紧 → 再次点击『三点扫描』复测,直到 ΔZ ≤ 容差。");
            sb.AppendLine("  · 约定:Z 为轴坐标;某点 Z* 偏大 = 该处需更大工作距才最清晰。方向映射以现场为准,数值(垫厚/倾角)是可靠参考。");
            if (double.IsNaN(b) && double.IsNaN(c)) sb.AppendLine("  ⚠ 平面拟合退化(点共线?),可增加第三点改善。");

            Runs.Insert(0, new TuneRunRow
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Label = $"{pts.Count}点 ΔZ={dZ:F3}mm",
                Detail = $"X向倾角 {tiltYdeg:F2}° · Y向倾角 {tiltXdeg:F2}°",
                Pass = pass
            });

            ResultText = sb.ToString();
        }

        private void SaveReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("相机垂直度快调报告 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("设备: " + (SelectedCameraDevice?.DeviceName ?? "-") + " / " + (SelectedMotionDevice?.DeviceName ?? "-"));
            sb.AppendLine();
            sb.AppendLine(ResultText);
            sb.AppendLine();
            sb.AppendLine("── 扫描历史 ──");
            foreach (var r in Runs) sb.AppendLine($"[{r.Time}] {r.Label} | {r.Detail} | {(r.Pass ? "达标" : "未达标")}");
            if (!string.IsNullOrEmpty(ImagingReportText) && ImagingReportText != "尚无体检结果。调曝光/增益(滑杆即时下发)后:采帧看评分徽章 → 点『生成体检报告』得逐项判定与打光处方。")
            {
                sb.AppendLine();
                sb.AppendLine("── 成像与打光体检 ──");
                sb.AppendLine(ImagingReportText);
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "文本报告|*.txt",
                FileName = $"相机垂直度_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };
            if (dlg.ShowDialog() == true)
            {
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString());
                AppendLog("报告已保存: " + dlg.FileName);
            }
        }

        // ================= v2 单帧实时垂直度引导(后台循环,免走位) =================
        private void ResetGuideEma()
        {
            _emaInited = false;
            _guideMinTilt = double.MaxValue;
            _guideStuckFrames = 0;
            _guideStuckMin = 0;
        }

        private async void StartGuideAsync()
        {
            if (IsBusyScanning || IsGuiding) return;
            ResetGuideEma();
            _guideAbortRequested = false;
            _lastGuideFrame = -1;
            IsGuiding = true;
            GuideStatusText = "启动中…";
            _guideFramePending = false;
            // ★ 实时引导靠"连续流"(免走位、边调边看)。软触发模式下裸 StartGrabbing 不会出图,
            //   所以这里必须走连续模式的 StartPreview —— 旧版就是在这里永久"等待相机帧…"。
            StartPreview();
            if (!IsPreviewing)
            {
                RunUi(() =>
                {
                    IsGuiding = false;
                    GuideStatusText = "启动失败";
                    GuideVerdictText = "⚠ 无法启动取流:请先确认相机已连接(看顶部状态条与日志)。";
                    GuideVerdictBrush = Brushes.Orange;
                });
                return;
            }
            var mode = (PerpMeasureMode)PerpModeIndex;
            AppendLog("▶ 实时垂直度引导启动 · 方案 " + PerpModeNames[PerpModeIndex]
                + (mode == PerpMeasureMode.DotGridAuto ? " · 工面需放点阵靶且占视野 ≥50%"
                    : mode == PerpMeasureMode.RectEdges ? " · 需工件/治具矩形边清晰"
                    : " · 需现场两条正交安装边"));
            try
            {
                await Task.Run(() =>
                {
                    int idle = 0;
                    while (!_guideAbortRequested)
                    {
                        if (!_frameArrivedEvent.WaitOne(500))
                        {
                            if (++idle % 4 == 0)
                                RunUi(() => GuideStatusText = "等待相机帧…(若预览未开,点『开始预览』更流畅)");
                            continue;
                        }
                        idle = 0;
                        var f = _latestFrame;
                        // FrameNum 单调递增用于去重;个别相机恒为 0 时退化为"每帧都算"(EMA 平滑,无副作用)
                        bool isNew = f != null && (f.FrameNum == 0 || f.FrameNum != _lastGuideFrame);
                        if (isNew)
                        {
                            _lastGuideFrame = f.FrameNum;
                            ProcessGuideFrame(f, mode);
                        }
                        _guideFramePending = false;
                    }
                });
            }
            catch (Exception ex)
            {
                AppendLog("引导异常: " + ex.Message);
                Debug.WriteLine($"[CameraTune] guide exception: {ex}");
            }
            finally
            {
                RunUi(() =>
                {
                    IsGuiding = false;
                    GuideStatusText = "已停止";
                    ProgressText = "实时引导已停止。可切换 A/B/C 方案重测,或回走位模式做定量验收。";
                });
            }
        }

        /// <summary>后台线程单帧测量:包图 → 调单帧垂直度服务 → UI 更新(处理完立即释放图像)。</summary>
        private void ProcessGuideFrame(FrameEventArgs f, PerpMeasureMode mode)
        {
            HalconRenderImage ri = null;
            try
            {
                ri = _renderService.WrapImage(f) as HalconRenderImage;
                if (ri == null || ri.HImage == null || !ri.HImage.IsInitialized()) return;
                var m = PerpendicularityMeter.Measure(ri.HImage, mode, PerpOpts);
                RunUi(() => UpdateGuideUi(m, mode));
            }
            catch { /* 单帧失败跳过 */ }
            finally
            {
                if (ri != null)
                {
                    try { ri.Dispose(); } catch { }
                }
            }
        }

        private void UpdateGuideUi(PerpMeasurement m, PerpMeasureMode mode)
        {
            if (!m.IsUsable)
            {
                GuideVerdictText = "⚠ 检测不稳: " + (string.IsNullOrEmpty(m.Message) ? "特征识别失败" : m.Message);
                GuideVerdictBrush = Brushes.Orange;
                GuideDetailText = "请对焦清晰/放大参照特征/改善对比度;A 方案需点阵靶占视野 ≥50%。";
                GuideStatusText = "测量中(低置信)";
                return;
            }

            // 指数滑动平均(单帧有噪声,手调看趋势)
            double p = double.IsNaN(m.PitchDeg) ? double.NaN : m.PitchDeg;
            double rr = double.IsNaN(m.RollDeg) ? double.NaN : m.RollDeg;
            double oo = double.IsNaN(m.OrthoDevDeg) ? double.NaN : m.OrthoDevDeg;
            if (!_emaInited)
            {
                _emaPitch = p; _emaRoll = rr; _emaOrtho = oo; _emaInited = true;
            }
            else
            {
                if (!double.IsNaN(p)) _emaPitch = _emaPitch * 0.65 + p * 0.35;
                if (!double.IsNaN(rr)) _emaRoll = _emaRoll * 0.65 + rr * 0.35;
                if (!double.IsNaN(oo)) _emaOrtho = _emaOrtho * 0.65 + oo * 0.35;
            }

            GuideRxText = FmtAngle(_emaPitch);
            GuideRyText = FmtAngle(_emaRoll);
            GuideOrthoText = FmtAngle(_emaOrtho);

            double maxTilt = 0;
            if (!double.IsNaN(_emaPitch)) maxTilt = Math.Max(maxTilt, Math.Abs(_emaPitch));
            if (!double.IsNaN(_emaRoll)) maxTilt = Math.Max(maxTilt, Math.Abs(_emaRoll));
            if (!double.IsNaN(_emaOrtho)) maxTilt = Math.Max(maxTilt, Math.Abs(_emaOrtho));

            // 方向提示(符号约定:正=下/右更近,现场方向以试调为准)
            var dirTips = new List<string>();
            if (!double.IsNaN(_emaPitch) && Math.Abs(_emaPitch) > 0.04)
                dirTips.Add(_emaPitch > 0 ? "画面上小下大→相机下端需抬高(或垫高下方)" : "画面上大下小→相机上端需抬高");
            if (!double.IsNaN(_emaRoll) && Math.Abs(_emaRoll) > 0.04)
                dirTips.Add(_emaRoll > 0 ? "画面左小右大→相机右端需抬高(或垫高右侧)" : "画面左大右小→相机左端需抬高");
            if (mode == PerpMeasureMode.OrthoLines && !double.IsNaN(_emaOrtho) && Math.Abs(_emaOrtho) > 0.04)
                dirTips.Add("两参照边夹角偏离 90° " + (_emaOrtho > 0 ? "+" : "") + _emaOrtho.ToString("0.00", CultureInfo.InvariantCulture) + "°,按缩短/增大夹角方向调整");

            // 已到最小但下不去 → 畸变/基准嫌疑(核心判据:指标最优时无法接近理论值)
            if (maxTilt < _guideMinTilt - 1e-9) { _guideMinTilt = maxTilt; _guideStuckFrames = 0; _guideStuckMin = maxTilt; }
            else if (maxTilt - _guideMinTilt < 0.08) _guideStuckFrames++;
            bool stuck = _guideMinTilt < 999 && _guideStuckFrames > 12 && _guideStuckMin >= 0.20;

            var extras = new List<string>();
            if (!double.IsNaN(m.BowRmsPx) && m.BowRmsPx > 1.5)
                extras.Add("边缘直线弓弯 " + m.BowRmsPx.ToString("0.0", CultureInfo.InvariantCulture) + "px → 疑畸变/靶面不平");
            // 中心vs外缘差:纯倾斜本身就会使其非零且随倾角放大,只在"已基本调平"后才指向畸变/局部不平,
            // 否则倾斜过程中的大值属正常,误报会干扰引导(probe2 实测:纯倾斜 6.6° → CVE≈1.65°)
            if (maxTilt < 0.5 && !double.IsNaN(m.CenterVsEdgeDeg) && m.CenterVsEdgeDeg > 0.30)
                extras.Add("中心与外缘解算差 " + m.CenterVsEdgeDeg.ToString("0.00", CultureInfo.InvariantCulture) + "° → 疑畸变/局部不平");
            if (stuck)
                extras.Add("读数已到最小仍 ≈" + _guideStuckMin.ToString("0.00", CultureInfo.InvariantCulture)
                    + "° 下不去 → 若机械已锁紧,疑镜头畸变或基准(靶/工面)问题,建议标定 LensDistortion 后复测");

            Brush b = Brushes.LimeGreen;
            string verdict;
            if (maxTilt < 0.08)
            {
                verdict = "✅ 已垂直:读数≈0(±0.08° 容差),可锁固相机。";
            }
            else if (maxTilt < 0.4)
            {
                verdict = "◐ 接近垂直:继续微调把读数压到最小——读数下降 = 方向正确。";
                b = Brushes.Gold;
            }
            else
            {
                verdict = "⚠ 倾斜约 " + maxTilt.ToString("0.00", CultureInfo.InvariantCulture)
                    + "°：" + (dirTips.Count > 0 ? string.Join("；", dirTips) : "按读数减小方向微调相机角度");
                b = Brushes.Orange;
            }
            if (extras.Count > 0)
            {
                if (b == Brushes.LimeGreen) b = Brushes.Gold;
                verdict += "（" + string.Join("；", extras) + "）";
            }
            GuideVerdictText = verdict;
            GuideVerdictBrush = b;
            GuideStatusText = "测量中";
            GuideDetailText = m.Message + " · 置信 " + m.Confidence.ToString("0.00", CultureInfo.InvariantCulture)
                + (double.IsNaN(m.BowRmsPx) ? "" : " · 弓弯 " + m.BowRmsPx.ToString("0.00", CultureInfo.InvariantCulture) + "px")
                + (double.IsNaN(m.CenterVsEdgeDeg) ? "" : " · 中心差 " + m.CenterVsEdgeDeg.ToString("0.00", CultureInfo.InvariantCulture) + "°");
        }

        private static string FmtAngle(double v)
        {
            return double.IsNaN(v) ? "—" : (v >= 0 ? "+" : "") + v.ToString("0.00", CultureInfo.InvariantCulture) + "°";
        }

        // ================= 页面离开清理(View.Unloaded 调用) =================
        public void Cleanup()
        {
            _cleaned = true;
            _previewRequested = false;
            _renderEnabled = false;
            _captureWaitActive = false;
            _scanAbortRequested = true;
            _guideAbortRequested = true;
            if (SelectedCameraDevice != null)
            {
                SelectedCameraDevice.StopGrabbing();
                SelectedCameraDevice.FrameReceived -= OnCameraFrameReceived;
                SelectedCameraDevice.StateChanged -= OnCameraStateChanged;
            }
            if (SelectedMotionDevice != null) SelectedMotionDevice.StateChanged -= OnMotionStateChanged;
            IsGrabbing = false;
            _frameArrivedEvent.Dispose();
            RunUi(() => CameraDisplayVm.Clear());
        }

        // ================= 小工具 =================
        private void RefreshCommands()
        {
            (CmdConnect as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdDisconnect as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdStartPreview as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdStopPreview as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdSnap as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdRecordPoint as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdClearPoints as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdStartScan as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdStopScan as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdSaveReport as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdClearLog as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdStartGridCheck as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdRefreshDevices as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdSnapGrade as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdAnalyzeImaging as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdResetImagingParams as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdStartGuide as RelayCommand)?.RaiseCanExecuteChanged();
            (CmdStopGuide as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private static double ParseD(string text, double fallback)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        private static string Fmt(double v)
        {
            return double.IsNaN(v) ? "—" : v.ToString("F5", CultureInfo.InvariantCulture);
        }

        private void AppendLog(string msg)
        {
            RunUi(() =>
            {
                Logs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
                while (Logs.Count > 300) Logs.RemoveAt(Logs.Count - 1);
            });
        }

        private void RunUi(Action action)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) action();
            else d.BeginInvoke(action);
        }

        /// <summary>
        /// 把"会阻塞的采图/扫描"丢到后台线程执行 —— 旧版把这些直接跑在 UI 线程上,
        /// 一次采图最长要等 5×800ms,界面会整段假死(用户观感同样是"没法用")。
        /// </summary>
        private void RunBackground(Action work)
        {
            if (IsBusyScanning) return;
            IsBusyScanning = true;
            Task.Run(() =>
            {
                try { work(); }
                catch (Exception ex)
                {
                    AppendLog("操作异常: " + ex.Message);
                    Debug.WriteLine($"[CameraTune] background exception: {ex}");
                }
                finally { RunUi(() => IsBusyScanning = false); }
            });
        }
    }

    // ===================================================================
    // 登记点(实现属性通知,XAML 逐点刷新)
    // ===================================================================
    public class TunePoint : ViewModelBase
    {
        private string _name;
        public string Name { get => _name; set => Set(ref _name, value); }

        private double _px;
        public double Px { get => _px; set => Set(ref _px, value); }

        private double _py;
        public double Py { get => _py; set => Set(ref _py, value); }

        private double _pz;
        public double Pz { get => _pz; set => Set(ref _pz, value); }

        private double _bestZ = double.NaN;
        public double BestZ { get => _bestZ; set => Set(ref _bestZ, value); }

        private double _peakScore = double.NaN;
        public double PeakScore { get => _peakScore; set => Set(ref _peakScore, value); }

        private string _curveSummary = "未扫描";
        public string CurveSummary { get => _curveSummary; set => Set(ref _curveSummary, value); }

        public string BestZText => double.IsNaN(BestZ) ? "—" : BestZ.ToString("F3", CultureInfo.InvariantCulture);
    }

    /// <summary>一次扫描运行摘要行(复测历史)。</summary>
    public class TuneRunRow
    {
        public string Time { get; set; }
        public string Label { get; set; }
        public string Detail { get; set; }
        public bool Pass { get; set; }
        public string PassText => Pass ? "✅ 达标" : "⚠ 未达标";
    }

    /// <summary>
    /// 纯 C# 图像清晰度评价(对焦峰):Tenengrad = 梯度能量均值 Σ(dx²+dy²)/N。
    /// 支持 Mono8/Gray8 与 RGB/BGR(亮度=三通道均值,单调性对评分足够)。
    /// </summary>
    public static class FocusScore
    {
        public static double Evaluate(FrameEventArgs f)
        {
            if (f?.Buffer == null || f.Width <= 0 || f.Height <= 0) return 0;
            var b = f.Buffer;
            int w = f.Width, h = f.Height;
            string fmt = (f.PixelFormat ?? "").ToUpperInvariant();
            bool color = fmt.Contains("RGB") || fmt.Contains("BGR");
            int ch = color ? 3 : 1;
            if (b.Length < w * h * ch) return 0;

            double sum = 0;
            long n = 0;
            for (int y = 1; y < h - 1; y++)
            {
                int rowBase = y * w * ch;
                int rowUp = (y - 1) * w * ch;
                int rowDn = (y + 1) * w * ch;
                for (int x = 1; x < w - 1; x++)
                {
                    int i = rowBase + x * ch;
                    double gC, gL, gR, gU, gD;
                    if (color)
                    {
                        gC = (b[i] + b[i + 1] + b[i + 2]) / 3.0;
                        gL = (b[i - 3] + b[i - 2] + b[i - 1]) / 3.0;
                        gR = (b[i + 3] + b[i + 4] + b[i + 5]) / 3.0;
                        gU = (b[rowUp + x * ch] + b[rowUp + x * ch + 1] + b[rowUp + x * ch + 2]) / 3.0;
                        gD = (b[rowDn + x * ch] + b[rowDn + x * ch + 1] + b[rowDn + x * ch + 2]) / 3.0;
                    }
                    else
                    {
                        gC = b[i]; gL = b[i - 1]; gR = b[i + 1];
                        gU = b[rowUp + x]; gD = b[rowDn + x];
                    }
                    double dx = gR - gL;
                    double dy = gD - gU;
                    sum += dx * dx + dy * dy;
                    n++;
                }
            }
            return n > 0 ? sum / n : 0;
        }
    }

    /// <summary>
    /// 纯 C# 成像质量指标(成像与打光体检 Tab):亮度均值/过曝欠曝占比/对比度 σ/3×3 分块亮度均匀性。
    /// 与 FocusScore 同通道约定(Mono8 单通道、RGB/BGR 取三通道均值),8bit,零 HALCON 依赖。
    /// </summary>
    public static class ImagingMetrics
    {
        public sealed class Report
        {
            public double Mean;
            public double Std;
            public double OverPct;             // 灰阶 >= 245 占比(%)
            public double UnderPct;            // 灰阶 <= 10 占比(%)
            public double[] BlockMeans = new double[9]; // 3×3 行主序:0左上 4中心 8右下
            public double BlockCv;             // 9 块均值变异系数(%)
        }

        public static Report Analyze(FrameEventArgs f)
        {
            var r = new Report();
            if (f?.Buffer == null || f.Width <= 0 || f.Height <= 0) return r;
            var b = f.Buffer;
            int w = f.Width, h = f.Height;
            string fmt = (f.PixelFormat ?? "").ToUpperInvariant();
            bool color = fmt.Contains("RGB") || fmt.Contains("BGR");
            int ch = color ? 3 : 1;
            if (b.Length < w * h * ch) return r;

            int[] colSplit = { 0, w / 3, w * 2 / 3, w };
            int[] rowSplit = { 0, h / 3, h * 2 / 3, h };
            if (colSplit[1] >= colSplit[2]) { colSplit[1] = w / 2; colSplit[2] = w; }
            if (rowSplit[1] >= rowSplit[2]) { rowSplit[1] = h / 2; rowSplit[2] = h; }

            double[] bSum = new double[9];
            long[] bN = new long[9];
            double sum = 0, sumSq = 0;
            long over = 0, under = 0, total = 0;

            for (int y = 0; y < h; y++)
            {
                int bi = y >= rowSplit[2] ? 2 : y >= rowSplit[1] ? 1 : 0;
                int rowBase = y * w * ch;
                for (int x = 0; x < w; x++)
                {
                    int bj = x >= colSplit[2] ? 2 : x >= colSplit[1] ? 1 : 0;
                    int i = rowBase + x * ch;
                    double g = color ? (b[i] + b[i + 1] + b[i + 2]) / 3.0 : b[i];
                    sum += g; sumSq += g * g;
                    if (g >= 245) over++;
                    else if (g <= 10) under++;
                    total++;
                    int idx = bi * 3 + bj;
                    bSum[idx] += g; bN[idx]++;
                }
            }
            if (total == 0) return r;

            double n = total;
            r.Mean = sum / n;
            double varr = Math.Max(0, sumSq / n - r.Mean * r.Mean);
            r.Std = Math.Sqrt(varr);
            r.OverPct = over * 100.0 / n;
            r.UnderPct = under * 100.0 / n;

            double bAvg = 0; int valid = 0;
            for (int k = 0; k < 9; k++)
            {
                if (bN[k] > 0) { r.BlockMeans[k] = bSum[k] / bN[k]; bAvg += r.BlockMeans[k]; valid++; }
            }
            if (valid > 0)
            {
                bAvg /= valid;
                double bVar = 0;
                for (int k = 0; k < 9; k++) if (bN[k] > 0) bVar += (r.BlockMeans[k] - bAvg) * (r.BlockMeans[k] - bAvg);
                bVar /= valid;
                r.BlockCv = bAvg > 1e-6 ? Math.Sqrt(bVar) / bAvg * 100.0 : 0;
            }
            return r;
        }
    }
}
