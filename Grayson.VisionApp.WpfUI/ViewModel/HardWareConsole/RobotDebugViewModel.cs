using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Model;
using Grayson.Vision.WpfUI.ViewModel.Steps;
using Newtonsoft.Json;
using Plugins.Robot.Epson; // WpfUI 已 ProjectReference 插件项目：类型判断调用 EpsonRobot 专属方法
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{
    /// <summary>
    /// 机械手调试台 ViewModel（HardwareConsole 独立 Tab）。
    ///
    /// 与控制卡轴调试（AxisControlView）的定位差异：
    /// AxisControlView 面向 ZMC 运动卡语义（DPOS/MPOS、限位、脉冲当量、逐轴参数下发）；
    /// 机械手是整机语义 —— 四轴 X/Y/Z/U 坐标同屏、PTP/CP 整点走位、示教点记录、
    /// 真空阀输出测试。Epson 的 GetMotionParam/GetSoftLimits/GetAxisStatus 均不支持
    /// （参数由 RC+ 工程管理），塞进 AxisControlView 只会得到一堆失效控件，
    /// 故独立成 Tab，交互与业务流（吸取/放置点位）直接对齐。
    ///
    /// 设备识别：从设备池筛选 BrandName 含 "Epson" 的 IMotionCard 设备；
    /// 选中后按接口拆箱 —— IMotionCard（运动）/ IIoDevice（真空阀）/ EpsonRobot（PTP）。
    /// 示教点持久化：{exe}\Data\RobotTeachPoints\{DeviceKey}.teachpoints.json，
    /// 为下一步工位业务流（吸料/放料/拍照位）直接提供点位数据。
    /// </summary>
    public class RobotDebugViewModel : ViewModelBase
    {
        private readonly IDevicePool _devicePool;
        private DispatcherTimer _pollTimer;

        // 选中设备按能力拆箱（均可能为 null）
        private IMotionCard _motion;
        private IIoDevice _io;
        private EpsonRobot _epson;

        // ---- 健壮性状态（2026-09-02）----
        /// <summary>轮询重入保护：上一轮还没跑完就跳过本轮（防 async 重入叠加）</summary>
        private bool _polling;
        /// <summary>连续轮询失败计数：超过阈值判定断线，自动置断开并提示一次</summary>
        private int _pollFailCount;
        /// <summary>断线已提示标志（避免每轮弹窗刷屏）</summary>
        private bool _disconnectNotified;
        /// <summary>命令门闩：运动类命令执行期间禁止再发（防连点导致指令交错/协议错位）</summary>
        private bool _commandBusy;
        /// <summary>
        /// RC+ 示教点同步：批量读点重入保护（连点按钮不会打两轮，两轮会互相抢 _ioLock
        /// 并让"停轮询/恢复轮询"配不成对）。
        /// </summary>
        private bool _rcLoadBusy;

        /// <summary>连续失败阈值：250ms×12 ≈ 3s 连续无应答/断线 → 判定通信中断</summary>
        private const int PollFailThreshold = 12;

        #region 设备列表与连接状态

        public ObservableCollection<IDevice> RobotDeviceList { get; } = new ObservableCollection<IDevice>();

        private IDevice _selectedRobot;
        public IDevice SelectedRobot
        {
            get => _selectedRobot;
            set
            {
                var old = _selectedRobot;
                if (Set(ref _selectedRobot, value))
                {
                    OnSelectedRobotChanged(old, value);
                }
            }
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (Set(ref _isConnected, value))
                {
                    ConnectionStatusText = _isConnected ? "已连接" : "未连接";
                    RefreshCommandStates();
                }
            }
        }

        private string _connectionStatusText = "未连接";
        public string ConnectionStatusText
        {
            get => _connectionStatusText;
            private set => Set(ref _connectionStatusText, value);
        }

        private bool _isServoOn;
        public bool IsServoOn
        {
            get => _isServoOn;
            private set => Set(ref _isServoOn, value);
        }

        private bool _isMoving;
        public bool IsMoving
        {
            get => _isMoving;
            private set => Set(ref _isMoving, value);
        }

        /// <summary>传输层标识（TCP 脚本协议 / RC+ SDK / 离线仿真），连接成功后由设备回读填充</summary>
        private string _transportText = "—";
        public string TransportText
        {
            get => _transportText;
            private set => Set(ref _transportText, value);
        }

        private bool _isActive = true;
        /// <summary>当前机械臂调试 Tab 是否显示/激活。非激活时停止轮询，避免后台占用 RC+ 会话。</summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (Set(ref _isActive, value))
                {
                    if (_isActive) _pollTimer?.Start();
                    else _pollTimer?.Stop();
                }
            }
        }

        #endregion

        #region 实时坐标（轮询 250ms）

        private float _posX;
        public float PosX { get => _posX; set => Set(ref _posX, value); }

        private float _posY;
        public float PosY { get => _posY; set => Set(ref _posY, value); }

        private float _posZ;
        public float PosZ { get => _posZ; set => Set(ref _posZ, value); }

        private float _posU;
        public float PosU { get => _posU; set => Set(ref _posU, value); }

        #endregion

        #region 目标点位与运动模式

        // ★ 目标框/速度框用 string 而非 float 绑定（2026-09-02）：
        //   之前是 float 属性 + TextBox 双向绑定，中文输入法输入 "40。"（全角句号）时
        //   WPF ConvertBack 解析失败刷 Error 7 且输入不生效（用户"跳转坐标不行"的 UI 侧根因）。
        //   改 string 后无类型转换，走位时经 TryParseCoord 容错解析（全角。、→半角 . ,）。
        private string _targetX = "0";
        public string TargetX { get => _targetX; set => Set(ref _targetX, value); }

        private string _targetY = "0";
        public string TargetY { get => _targetY; set => Set(ref _targetY, value); }

        private string _targetZ = "0";
        public string TargetZ { get => _targetZ; set => Set(ref _targetZ, value); }

        private string _targetU = "0";
        public string TargetU { get => _targetU; set => Set(ref _targetU, value); }

        private string _speedPct = "30";
        /// <summary>走位速度（TCP 模式为 1~100 百分比口径，适配层内部钳位）</summary>
        public string SpeedPct
        {
            get => _speedPct;
            set => Set(ref _speedPct, value);
        }

        private int _moveModeIndex; // 0 = PTP（Go 关节插补，Epson 专属）；1 = CP（Move 直线插补）
        public int MoveModeIndex
        {
            get => _moveModeIndex;
            set => Set(ref _moveModeIndex, value);
        }

        #endregion

        #region 单轴步进（按住连续 / 单击单步）

        public IReadOnlyList<double> StepSizeOptions { get; } = new double[] { 0.1, 0.5, 1, 2, 5, 10 };

        private double _selectedStepSize = 1;
        /// <summary>单轴步进步长（mm；U 轴同值解释为角度°）</summary>
        public double SelectedStepSize
        {
            get => _selectedStepSize;
            set => Set(ref _selectedStepSize, value);
        }

        private string _lastAxisStepText = "";
        /// <summary>最近一次单轴步进结果（成功/被拒原因），显示在按钮区做即时反馈</summary>
        public string LastAxisStepText
        {
            get => _lastAxisStepText;
            private set => Set(ref _lastAxisStepText, value);
        }

        #endregion

        #region 示教点管理

        public ObservableCollection<TeachPointModel> TeachPoints { get; } = new ObservableCollection<TeachPointModel>();

        private TeachPointModel _selectedTeachPoint;
        public TeachPointModel SelectedTeachPoint
        {
            get => _selectedTeachPoint;
            set
            {
                if (Set(ref _selectedTeachPoint, value))
                {
                    // 【回放走位】【删除】可用性随选中点变化 → 必须手动刷新：
                    // RelayCommand 不挂 CommandManager.RequerySuggested，只在 RaiseCanExecuteChanged 时 UI 才重查。
                    PlayTeachPointCommand?.RaiseCanExecuteChanged();
                    DeleteTeachPointCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        private string _teachPointName = "";
        public string TeachPointName
        {
            get => _teachPointName;
            set => Set(ref _teachPointName, value);
        }

        #endregion

        #region RC+ 示教点同步（只读，2026-09-11 新增）

        /// <summary>从 RC+ 点文件读回的点位（只读快照，不落盘、不影响控制器）</summary>
        public ObservableCollection<TeachPointModel> RcPoints { get; } = new ObservableCollection<TeachPointModel>();

        private TeachPointModel _selectedRcPoint;
        public TeachPointModel SelectedRcPoint
        {
            get => _selectedRcPoint;
            set
            {
                if (Set(ref _selectedRcPoint, value))
                {
                    // 【回放走位】【导入】可用性随选中点变化 → 必须手动刷新（同 SelectedTeachPoint）
                    PlayRcPointCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        private string _rcPointCountText = "10";
        /// <summary>要同步的点号个数（从 P0 起算，1~100）</summary>
        public string RcPointCountText
        {
            get => _rcPointCountText;
            set => Set(ref _rcPointCountText, value);
        }

        private string _rcPointsStatusText = "未同步（连接后自动读一次，或点「读取」）";
        public string RcPointsStatusText
        {
            get => _rcPointsStatusText;
            private set => Set(ref _rcPointsStatusText, value);
        }

        #endregion

        #region 可达域可视化与九点预演（2026-09-11 新增）

        // ★ 这一段解决的是"标定九点怎么走才不撞机 / 不被控制器拒绝"。
        //
        // 【为什么画实测多边形，而不是画理想圆环】
        //   真实可达域 = 圆环 ∩ 关节限位区域，只有 J1 全周且 J2 全范围时才退化成圆环。
        //   用圆环画会犯"画在环内其实够不着"的假安全错误 —— 那正是撞机的来源。
        //
        // 【数据从哪来】.workbuddy/map_workspace.py 或本页「零运动实测」——
        //   只发只读 CHECK（内部走 SPEL+ TargetOK），机械手原地不动，不驱动电机。
        //
        // 【分工】VM 负责"算"（取景范围 / 绿区栅格 / 9 点判定 / 文案），View 只负责"画"。
        //   ReachMapGeometry 是纯计算，不依赖任何 WPF 绘图类型，可离线自检。

        /// <summary>实测可达域。null = 尚未载入（画布显示引导提示）</summary>
        private ReachMapData _reachMap;
        public ReachMapData ReachMap
        {
            get => _reachMap;
            private set => Set(ref _reachMap, value);
        }

        /// <summary>可行基准位栅格位图（绿区）。只在数据/步长/margin 变化时重建；拖动基准位不重算</summary>
        private FeasibleBitmap _reachFeasible;
        public FeasibleBitmap ReachFeasible
        {
            get => _reachFeasible;
            private set => Set(ref _reachFeasible, value);
        }

        // 画布取景范围（世界坐标 mm）—— 由 RecalcReachGeometry 统一设置，View 直接读取
        private double _reachXMin = -450, _reachXMax = 450, _reachYMin = -450, _reachYMax = 450;
        public double ReachXMin { get => _reachXMin; private set => Set(ref _reachXMin, value); }
        public double ReachXMax { get => _reachXMax; private set => Set(ref _reachXMax, value); }
        public double ReachYMin { get => _reachYMin; private set => Set(ref _reachYMin, value); }
        public double ReachYMax { get => _reachYMax; private set => Set(ref _reachYMax, value); }

        private string _reachBaseX = "0";
        /// <summary>九点网格中心（基准位）X mm。画布上按住拖动会实时改写这里</summary>
        public string ReachBaseX
        {
            get => _reachBaseX;
            set { if (Set(ref _reachBaseX, value)) RecalcReachVerdict(); }
        }

        private string _reachBaseY = "0";
        public string ReachBaseY
        {
            get => _reachBaseY;
            set { if (Set(ref _reachBaseY, value)) RecalcReachVerdict(); }
        }

        private string _reachStepMm = "10";
        /// <summary>九点平移走位步长 mm（与标定向导的 GridStep 同口径）</summary>
        public string ReachStepMm
        {
            get => _reachStepMm;
            set { if (Set(ref _reachStepMm, value)) RecalcReachGeometry(); }
        }

        private string _reachMarginMm = "10";
        /// <summary>安全余量 mm：九点内圈离可达内边界、外圈离可达外边界都要留出的距离</summary>
        public string ReachMarginMm
        {
            get => _reachMarginMm;
            set { if (Set(ref _reachMarginMm, value)) RecalcReachGeometry(); }
        }

        private int _reachEyeModeIndex;   // 0 = EyeInHand(眼在手上)；1 = EyeToHand(眼在手外)
        /// <summary>眼型：决定九点相对基准的偏移方向（与标定档案的 EyeMode 同口径）</summary>
        public int ReachEyeModeIndex
        {
            get => _reachEyeModeIndex;
            set { if (Set(ref _reachEyeModeIndex, value)) RecalcReachGeometry(); }
        }

        private int _reachModeIndex;      // 0 = 中心优先螺旋；1 = 传统逐行扫描
        /// <summary>走位方式。只改变访问次序，不改变网格位置（与标定向导同一张顺序表）</summary>
        public int ReachModeIndex
        {
            get => _reachModeIndex;
            set
            {
                if (Set(ref _reachModeIndex, value))
                {
                    RaisePropertyChanged(nameof(ReachTraverseOrder));
                    RaisePropertyChanged(nameof(ReachVerdictText));
                }
            }
        }

        private bool _reachInvertX;
        public bool ReachInvertX
        {
            get => _reachInvertX;
            set { if (Set(ref _reachInvertX, value)) RecalcReachGeometry(); }
        }

        private bool _reachInvertY;
        public bool ReachInvertY
        {
            get => _reachInvertY;
            set { if (Set(ref _reachInvertY, value)) RecalcReachGeometry(); }
        }

        private bool _showReachDomain = true;
        public bool ShowReachDomain { get => _showReachDomain; set => Set(ref _showReachDomain, value); }

        private bool _showMarginBand = true;
        public bool ShowMarginBand { get => _showMarginBand; set => Set(ref _showMarginBand, value); }

        private bool _showReachGrid = true;
        public bool ShowReachGrid { get => _showReachGrid; set => Set(ref _showReachGrid, value); }

        private bool _showFeasibleZone = true;
        public bool ShowFeasibleZone { get => _showFeasibleZone; set => Set(ref _showFeasibleZone, value); }

        private string _reachVerdictText = "尚未载入可达域数据 —— 连上控制器后点「📡 零运动实测」；没有设备时可先点「👁 示意」预览界面。";
        /// <summary>九点判定结论（带"还差多少 mm"）</summary>
        public string ReachVerdictText
        {
            get => _reachVerdictText;
            private set => Set(ref _reachVerdictText, value);
        }

        private int _reachVerdictLevel;   // 0 中性 / 1 安全(绿) / 2 警告(橙) / 3 不可行(红)
        public int ReachVerdictLevel
        {
            get => _reachVerdictLevel;
            private set => Set(ref _reachVerdictLevel, value);
        }

        private string _reachActionText = "";
        /// <summary>载入/扫描/开走前校验的操作反馈</summary>
        public string ReachActionText
        {
            get => _reachActionText;
            private set => Set(ref _reachActionText, value);
        }

        private string _reachScanButtonText = "📡 零运动实测";
        public string ReachScanButtonText
        {
            get => _reachScanButtonText;
            private set => Set(ref _reachScanButtonText, value);
        }

        // ---- 零运动扫描参数（2026-09-11 增强：支持指定 Z/U、方向数、进度条、落盘 JSON）----

        private string _reachScanZ = "";
        /// <summary>扫描平面 Z（mm）。留空 = 用当前机械手 Z；填了 = 按此 Z 扫</summary>
        public string ReachScanZ
        {
            get => _reachScanZ;
            set => Set(ref _reachScanZ, value);
        }

        private string _reachScanU = "";
        /// <summary>扫描平面 U（度）。留空 = 用当前机械手 U；填了 = 按此 U 扫</summary>
        public string ReachScanU
        {
            get => _reachScanU;
            set => Set(ref _reachScanU, value);
        }

        private string _reachScanDirs = "24";
        /// <summary>扫描方向数（越多越细，越慢）。默认 24，可调 12~72</summary>
        public string ReachScanDirs
        {
            get => _reachScanDirs;
            set => Set(ref _reachScanDirs, value);
        }

        private double _reachScanProgress;   // 0~100
        public double ReachScanProgress
        {
            get => _reachScanProgress;
            private set => Set(ref _reachScanProgress, value);
        }

        private bool _reachScanProgressVisible;
        public bool ReachScanProgressVisible
        {
            get => _reachScanProgressVisible;
            private set => Set(ref _reachScanProgressVisible, value);
        }

        public IReadOnlyList<string> ReachEyeModeOptions { get; } =
            new[] { "眼在手上 EyeInHand", "眼在手外 EyeToHand" };

        public IReadOnlyList<string> ReachTraverseModeOptions { get; } =
            new[] { "中心优先螺旋(推荐)", "传统逐行扫描" };

        /// <summary>九点走位次序（与标定向导 NinePointTraverseOrder 同一张表，保证序号标注不漂移）</summary>
        public int[] ReachTraverseOrder => NinePointTraverseOrder.GetOrder(
            _reachModeIndex == 1 ? NinePointTraverseMode.RowScan : NinePointTraverseMode.SpiralCenterFirst);

        /// <summary>扫描取消令牌（非 null = 正在扫描；再点一次按钮即取消）</summary>
        private System.Threading.CancellationTokenSource _reachScanCts;

        public RelayCommand LoadReachMapCommand { get; private set; }
        public RelayCommand LoadSyntheticReachMapCommand { get; private set; }
        public RelayCommand ScanReachMapCommand { get; private set; }
        public RelayCommand UseCurrentPosAsBaseCommand { get; private set; }
        public RelayCommand UseBestBaseCommand { get; private set; }
        public RelayCommand PrecheckGridCommand { get; private set; }

        #endregion

        #region 真空阀（双吸嘴）

        private bool _vaccum1On;
        public bool Vaccum1On
        {
            get => _vaccum1On;
            private set => Set(ref _vaccum1On, value);
        }

        private bool _vaccum2On;
        public bool Vaccum2On
        {
            get => _vaccum2On;
            private set => Set(ref _vaccum2On, value);
        }

        #endregion

        #region 命令

        public RelayCommand RefreshRobotsCommand { get; private set; }
        public RelayCommand ConnectCommand { get; private set; }
        public RelayCommand DisconnectCommand { get; private set; }
        public RelayCommand ToggleServoCommand { get; private set; }
        public RelayCommand HomeCommand { get; private set; }
        public RelayCommand StopCommand { get; private set; }
        public RelayCommand EmergencyStopCommand { get; private set; }
        public RelayCommand MoveToPointCommand { get; private set; }
        public RelayCommand SaveTeachPointCommand { get; private set; }
        public RelayCommand PlayTeachPointCommand { get; private set; }
        public RelayCommand DeleteTeachPointCommand { get; private set; }
        public RelayCommand ToggleVaccum1Command { get; private set; }
        public RelayCommand ToggleVaccum2Command { get; private set; }
        /// <summary>从 RC+ 点文件读取前 N 个示教点（只读）</summary>
        public RelayCommand LoadRcPointsCommand { get; private set; }
        /// <summary>把选中的 RC+ 点写入目标框并走位（与手动走位同链路）</summary>
        public RelayCommand PlayRcPointCommand { get; private set; }
        /// <summary>把已同步的 RC+ 点批量并入本机示教点列表（同名覆盖，供业务流消费）</summary>
        public RelayCommand ImportRcPointsCommand { get; private set; }
        /// <summary>单轴步进（参数 "X+" / "X-" / "Y+" …；按住连续/单击单步，后台 IO）</summary>
        public RelayCommand<string> StepAxisCommand { get; private set; }
        /// <summary>相机实时画面弹窗（复用 CameraLiveWindow，非模态，边动边看）</summary>
        public RelayCommand ShowCameraLiveCommand { get; private set; }

        #endregion

        public RobotDebugViewModel()
        {
            _devicePool = App.StationHostRuntime?.DevicePool ?? throw new InvalidOperationException("DevicePool not initialized");
            InitCommands();
            InitPollTimer();
            LoadRobots();
        }

        private void InitPollTimer()
        {
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _pollTimer.Tick += async (s, e) =>
            {
                // ⚠ UI 防卡死铁律：机械手 IO 全部放后台线程。
                // DispatcherTimer 的 Tick 在 UI 线程——若直接同步调 GetPositionsAll，
                // 底层 SendCommand 会阻塞等应答（最长 5~20s），脚本忙/异常时界面直接冻结。
                // 改为 async：IO 在 Task.Run 后台执行，await 恢复后（仍在 UI 线程）更新属性。
                if (_polling) return;               // 上一轮未完成则跳过本轮
                _polling = true;
                try
                {
                    await PollStatusCoreAsync();
                }
                finally
                {
                    _polling = false;
                }
            };
            _pollTimer.Start();
        }

        private void InitCommands()
        {
            RefreshRobotsCommand = new RelayCommand(LoadRobots);

            ConnectCommand = new RelayCommand(async () =>
            {
                if (SelectedRobot == null) return;
                var res = await Task.Run(() => SelectedRobot.Connect());
                if (!res.Success)
                {
                    MessageBox.Show($"机械手连接失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                IsConnected = true;
                _disconnectNotified = false; // 重新连接成功 → 复位断线提示标志
                // EpsonRobot.Connect 成功后自动伺服上电（适配层已内置）
                IsServoOn = true;
                RefreshTransportText();
                LoadTeachPoints();

                // ★ RC+ 示教点同步（2026-09-11）：连接后自动读一次前 N 个点号。
                //   只读、不发运动指令；通道不支持（SDK/仿真）时内部会给出可读提示并退出。
                await LoadRcPointsCoreAsync();

                // ★ 安全默认（2026-09-02）：连接后把当前位置填入目标框——
                //   默认目标 0,0,0,0 对 SCARA 是动作区域外点（4001/4007），
                //   用户不填直接点走位会触发控制器拒绝。填入当前位置后，
                //   首次走位按钮=原地不动，必须先改目标值才真正运动。
                if (_epson != null)
                {
                    var all = _epson.GetPositionsAll();
                    if (all.Success && all.Data != null && all.Data.Length == 4)
                    {
                        TargetX = FormatCoord(all.Data[0]);
                        TargetY = FormatCoord(all.Data[1]);
                        TargetZ = FormatCoord(all.Data[2]);
                        TargetU = FormatCoord(all.Data[3]);
                    }
                }
            }, () => SelectedRobot != null && !IsConnected);

            DisconnectCommand = new RelayCommand(async () =>
            {
                if (SelectedRobot == null) return;
                try
                {
                    await Task.Run(() => SelectedRobot.Disconnect());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RobotDebug] 断开异常: {ex.Message}");
                }
                IsConnected = false;
                IsServoOn = false;
                IsMoving = false;
                TransportText = "—";
            }, () => SelectedRobot != null && IsConnected);

            ToggleServoCommand = new RelayCommand(async () =>
            {
                if (!IsConnected || _motion == null) return;
                bool next = !IsServoOn;
                try
                {
                    // 后台 IO：Epson 伺服切换 = MOTOR ON/OFF（TCP 等应答，最长 20s）→ 绝不上 UI 线程
                    var res = await Task.Run(() => _motion.SetAxisEnable(0, next));
                    if (res.Success) IsServoOn = next;
                    else MessageBox.Show($"伺服切换失败: {res.Message}", "硬件错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"伺服切换内部异常: {ex.Message}", "硬件错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, () => IsConnected && _motion != null);

            // 运动类命令：统一走 RunSafeAsync（后台 IO + 异常兜底 + 门闩防重入）
            HomeCommand = new RelayCommand(
                async () => await RunSafeAsync("回零", () => _motion.Home(0, 4)),
                () => IsConnected && _motion != null && !_commandBusy);

            StopCommand = new RelayCommand(
                async () => await RunSafeAsync("停止", () => _motion.StopAxis(0)),
                () => IsConnected && _motion != null && !_commandBusy);

            EmergencyStopCommand = new RelayCommand(
                async () => await RunSafeAsync("急停", () => _motion.RapidStop()),
                () => IsConnected && _motion != null);

            // 整点走位：PTP（Epson Go 关节插补）或 CP（Move 直线插补，接口通用兜底）
            MoveToPointCommand = new RelayCommand(async () =>
            {
                if (!IsConnected || _motion == null) return;

                // ★ 容错解析目标框（string → float）：全角句号。逗号，自动归一，
                //   任一轴非法则明确提示哪一轴，绝不发车（防误走/防 4001）
                if (!TryResolveTargets(out float tx, out float ty, out float tz, out float tu, out float spd))
                {
                    return; // 提示已弹
                }

                Result res;
                if (MoveModeIndex == 0 && _epson != null)
                {
                    res = await RunBusyCoreAsync(() => _epson.MoveToPtp(tx, ty, tz, tu, spd));
                }
                else
                {
                    res = await RunBusyCoreAsync(() => _motion.LineInterpolation(
                        new[] { 0, 1, 2, 3 },
                        new[] { tx, ty, tz, tu },
                        spd, true));
                }

                if (res != null && !res.Success)
                    MessageBox.Show($"走位失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }, () => IsConnected && _motion != null && !_commandBusy);

            SaveTeachPointCommand = new RelayCommand(() =>
            {
                if (!IsConnected || _motion == null) return;
                string name = string.IsNullOrWhiteSpace(TeachPointName)
                    ? $"P{TeachPoints.Count + 1}"
                    : TeachPointName.Trim();

                // 同名覆盖（示教重复操作时的自然预期）
                var existing = TeachPoints.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                var pt = new TeachPointModel { Name = name, X = PosX, Y = PosY, Z = PosZ, U = PosU };
                if (existing != null)
                {
                    int idx = TeachPoints.IndexOf(existing);
                    TeachPoints[idx] = pt;
                }
                else
                {
                    TeachPoints.Add(pt);
                }

                SaveTeachPoints();
                TeachPointName = "";
                SelectedTeachPoint = pt; // 自动选中刚存/覆盖的点 → 回放走位/删除立即可用（setter 内已刷新命令）
            }, () => IsConnected && _motion != null);

            PlayTeachPointCommand = new RelayCommand(() =>
            {
                if (SelectedTeachPoint == null || !IsConnected || _motion == null) return;
                // 回放：把示教点写入目标框并立即走位（与手动走位同链路）
                TargetX = FormatCoord(SelectedTeachPoint.X);
                TargetY = FormatCoord(SelectedTeachPoint.Y);
                TargetZ = FormatCoord(SelectedTeachPoint.Z);
                TargetU = FormatCoord(SelectedTeachPoint.U);
                MoveToPointCommand.Execute(null);
            }, () => SelectedTeachPoint != null && IsConnected && _motion != null);

            DeleteTeachPointCommand = new RelayCommand(() =>
            {
                if (SelectedTeachPoint == null) return;
                TeachPoints.Remove(SelectedTeachPoint);
                SaveTeachPoints();
                SelectedTeachPoint = null; // 移除后清空选中（setter 刷新回放/删除可用性），防残留引用已删点
            }, () => SelectedTeachPoint != null);

            // 真空阀：业务输出 0 = 吸嘴1 真空（SPEL+ OUT15）、业务输出 1 = 吸嘴2 真空（SPEL+ OUT14）；
            // 物理端口映射在 EpsonIoMap（Plugins.Robot.Epson/EpsonSdkAdapters.cs），换机接线只改那里。
            // ⚠ 后台 IO + 异常兜底：WriteDo → OUT 指令等应答最长 20s，绝不在 UI 线程同步执行。
            ToggleVaccum1Command = new RelayCommand(async () =>
            {
                if (_io == null || !IsConnected) return;
                bool next = !Vaccum1On;
                try
                {
                    var res = await Task.Run(() => _io.WriteDo(0, next));
                    if (res.Success) Vaccum1On = next;
                    else MessageBox.Show($"吸嘴1 真空阀操作失败: {res.Message}", "硬件错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"吸嘴1 真空阀内部异常: {ex.Message}", "硬件错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, () => _io != null && IsConnected);

            ToggleVaccum2Command = new RelayCommand(async () =>
            {
                if (_io == null || !IsConnected) return;
                bool next = !Vaccum2On;
                try
                {
                    var res = await Task.Run(() => _io.WriteDo(1, next));
                    if (res.Success) Vaccum2On = next;
                    else MessageBox.Show($"吸嘴2 真空阀操作失败: {res.Message}", "硬件错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"吸嘴2 真空阀内部异常: {ex.Message}", "硬件错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, () => _io != null && IsConnected);

            // ★ 单轴步进（2026-09-02）：参数 "X+" / "X-" / "Y+"…（首字符轴名，尾字符方向）。
            //   EpsonRobot.MoveRelative(axis,±step) = 读四轴当前位置仅累加目标轴 → 组整点发 MOVE，
            //   每步经脚本 SafeGo 安全钳位（超 XY/Z/U 范围返回 ERR 不发车），命令失败即机械手未动。
            //   按住 RepeatButton 连续触发由 _commandBusy 门闩自然限速（上一步应答后才发下一步）。
            StepAxisCommand = new RelayCommand<string>(async p =>
            {
                if (!IsConnected || _motion == null || string.IsNullOrWhiteSpace(p) || p.Length < 2) return;
                char axis = char.ToUpperInvariant(p[0]);
                int idx = axis == 'X' ? 0 : axis == 'Y' ? 1 : axis == 'Z' ? 2 : axis == 'U' ? 3 : -1;
                if (idx < 0) return;
                int sign = p.EndsWith("+") ? 1 : p.EndsWith("-") ? -1 : 0;
                if (sign == 0) return;

                float dist = (float)(SelectedStepSize * sign);
                float spd = 30f;
                float parsed;
                if (TryResolveCoord("速度", SpeedPct, out parsed)) spd = parsed;

                // ⚠ Z 轴行程安全钳位（实机实测行程 -150mm ~ 0：Home 顶端 Z≈0、向下为负，与脚本 SafeGo 同源）。
                //   此前 Z+ 一刀切硬禁止导致"点了不动"——实际上行（如 -100 → -20）是正常示教动作，
                //   只有越过顶端 0 才有撞顶(4001)风险。现改为：目标越界 → 截断到边界；
                //   已停在边界仍按同方向 → 拒绝并提示。TCP 脚本模式 SafeGo 仍是最终兜底，
                //   SDK 直连（无 SafeGo 脚本）靠本层钳位保护。
                if (axis == 'Z')
                {
                    if (sign > 0 && PosZ + dist > 0f)
                    {
                        if (PosZ >= 0f)
                        {
                            LastAxisStepText = "⚠ 已到 Z 顶端(0mm)，无法再上行";
                            return;
                        }
                        dist = 0f - PosZ; // 截断步长：恰好走到顶端
                    }
                    else if (sign < 0 && PosZ + dist < -150f)
                    {
                        if (PosZ <= -150f)
                        {
                            LastAxisStepText = "⚠ 已到 Z 底端(-150mm)，无法再下行";
                            return;
                        }
                        dist = -150f - PosZ; // 截断步长：恰好走到底端
                    }
                }

                var res = await RunBusyCoreAsync(() => _motion.MoveRelative(idx, dist, spd));
                if (res != null && res.Success)
                {
                    LastAxisStepText = $"{axis}{(sign > 0 ? "+" : "-")} {FormatCoord(dist)} 完成";
                }
                else if (res != null)
                {
                    LastAxisStepText = $"{axis}{(sign > 0 ? "+" : "-")} 被拒: {res.Message}";
                }
                // res == null = 门闩拦截（上一步仍在执行）→ 静默跳过，等待下次触发
            }, p => IsConnected && _motion != null && !_commandBusy && !string.IsNullOrWhiteSpace(p));

            // ---- RC+ 示教点同步（只读，2026-09-11）----
            // 只读点文件，不发运动指令、不写控制器 → 不占 _commandBusy；
            // 但共用同一 TCP 行协议（一发一收由适配层 _ioLock 串行），IO 仍必须在后台线程。
            LoadRcPointsCommand = new RelayCommand(async () =>
            {
                if (!IsConnected || _epson == null) return;
                await LoadRcPointsCoreAsync();
            }, () => IsConnected && _epson != null);

            PlayRcPointCommand = new RelayCommand(() =>
            {
                if (SelectedRcPoint == null || !IsConnected || _motion == null) return;
                // ★ 占位行（未定义/读取失败）坐标是 0,0,0,0，发出去就是动作区域外点 → 直接挡掉
                if (SelectedRcPoint.IsUnreadable)
                {
                    RcPointsStatusText = $"「{SelectedRcPoint.Name}」没有可用坐标，不能回放；请先在 RC+ 里示教该点。";
                    return;
                }
                // 回放：写入目标框后立即走位（与既有【回放走位】同链路，不新增运动路径）
                TargetX = FormatCoord(SelectedRcPoint.X);
                TargetY = FormatCoord(SelectedRcPoint.Y);
                TargetZ = FormatCoord(SelectedRcPoint.Z);
                TargetU = FormatCoord(SelectedRcPoint.U);
                MoveToPointCommand.Execute(null);
            }, () => SelectedRcPoint != null && !SelectedRcPoint.IsUnreadable
                     && IsConnected && _motion != null && !_commandBusy);

            ImportRcPointsCommand = new RelayCommand(() =>
            {
                int n = 0;
                foreach (var p in RcPoints.ToList())
                {
                    if (p.IsUnreadable) continue;      // 占位行不导入（0,0,0,0 会污染业务流）
                    if (string.IsNullOrWhiteSpace(p.Name)) continue;
                    var copy = new TeachPointModel { Name = p.Name, X = p.X, Y = p.Y, Z = p.Z, U = p.U };
                    var existing = TeachPoints.FirstOrDefault(t =>
                        string.Equals(t.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) TeachPoints[TeachPoints.IndexOf(existing)] = copy;
                    else TeachPoints.Add(copy);
                    n++;
                }
                SaveTeachPoints();
                SelectedTeachPoint = TeachPoints.LastOrDefault();
                RcPointsStatusText = n > 0
                    ? $"已把 {n} 个 RC+ 点位并入上方「示教点管理」（同名覆盖，已落盘）"
                    : "没有可导入的 RC+ 点位（当前列表里的点都是「未定义/读取失败」占位行）";
            }, () => RcPoints.Any(p => !p.IsUnreadable));

            // 相机实时画面弹窗（与相机/轴调试同款 CameraLiveWindow；非模态可边动边看）
            ShowCameraLiveCommand = new RelayCommand(() => ShowCameraLiveWindow(), () => true);

            // 可达域可视化与九点预演（2026-09-11）
            InitReachCommands();
        }

        /// <summary>相机实时画面弹窗单例引用（防重复打开；窗口关闭即置空）</summary>
        private static System.Windows.Window _cameraLiveWindow;

        private static void ShowCameraLiveWindow()
        {
            if (_cameraLiveWindow != null)
            {
                _cameraLiveWindow.Activate();
                return;
            }

            try
            {
                _cameraLiveWindow = new Grayson.Vision.WpfUI.View.HardwareConsole.CameraLiveWindow
                {
                    Owner = System.Windows.Application.Current?.MainWindow
                };
                _cameraLiveWindow.Closed += (s, e) => _cameraLiveWindow = null;
                _cameraLiveWindow.Show();
            }
            catch (Exception ex)
            {
                _cameraLiveWindow = null;
                MessageBox.Show($"打开相机实时画面失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================================================================
        // 可达域可视化与九点预演 —— 命令实现与几何联动
        // ==================================================================

        private void InitReachCommands()
        {
            LoadReachMapCommand = new RelayCommand(LoadReachMapFromFile);

            LoadSyntheticReachMapCommand = new RelayCommand(() =>
            {
                // 理想圆环示意数据：只为把界面先跑起来看效果，明确标注"非实测"。
                ApplyReachMap(ReachMapData.CreateSynthetic(232, 418),
                    "已载入【示意数据】（理想圆环 r=232~418）—— 仅用于预览界面，真实可达域请点「📡 零运动实测」");
            });

            ScanReachMapCommand = new RelayCommand(async () =>
            {
                if (_reachScanCts != null) { _reachScanCts.Cancel(); return; }   // 扫描中再点一次 = 取消
                await ScanReachMapCoreAsync();
            }, () => _reachScanCts != null || (IsConnected && _epson != null));

            UseCurrentPosAsBaseCommand = new RelayCommand(() =>
            {
                ReachBaseX = FormatCoord(PosX);
                ReachBaseY = FormatCoord(PosY);
            });

            UseBestBaseCommand = new RelayCommand(() =>
            {
                var fb = ReachFeasible;
                if (fb?.BestCenter == null) return;
                ReachBaseX = FormatCoord((float)fb.BestCenter.Value.X);
                ReachBaseY = FormatCoord((float)fb.BestCenter.Value.Y);
            }, () => ReachFeasible?.BestCenter != null);

            PrecheckGridCommand = new RelayCommand(async () => await PrecheckGridCoreAsync(),
                () => IsConnected && _epson != null && _reachMap != null);
        }

        /// <summary>把可达域数据装进界面并重算全部派生量</summary>
        private void ApplyReachMap(ReachMapData map, string actionText)
        {
            ReachMap = (map != null && map.IsEmpty) ? null : map;
            ReachActionText = actionText ?? string.Empty;
            RecalcReachGeometry();
        }

        /// <summary>
        /// 重算取景范围 + 绿区栅格 + 九点判定。
        /// 代价约 15ms（448×448 栅格 × 9 次平移取交）—— 只在数据/步长/margin/眼型/镜像变化时调用。
        /// 拖动基准位走 <see cref="RecalcReachVerdict"/>（9 次判定，微秒级），所以拖动是流畅的。
        /// </summary>
        private void RecalcReachGeometry()
        {
            var map = _reachMap;
            if (map == null || map.IsEmpty)
            {
                ReachFeasible = null;
                SetReachBounds(-450, 450, -450, 450);
                RecalcReachVerdict();
                return;
            }

            double step = ParseCoordOr(_reachStepMm, 10);
            double margin = ParseCoordOr(_reachMarginMm, 10);
            double bx = ParseCoordOr(_reachBaseX, 0);
            double by = ParseCoordOr(_reachBaseY, 0);

            // 取景必须同时容下：可达域边界、基准位、当前机器位置（后者可能在可达域外）
            var extra = new[] { new Pt2(bx, by), new Pt2(PosX, PosY) };
            double xMin, xMax, yMin, yMax;
            ReachMapGeometry.ComputeBounds(map, margin, extra, out xMin, out xMax, out yMin, out yMax);
            SetReachBounds(xMin, xMax, yMin, yMax);

            var offs = BuildOffsets(step, step);
            const int bitmapSize = 448;
            ReachFeasible = ReachMapGeometry.ComputeFeasibleBitmap(
                map, margin, offs, xMin, xMax, yMin, yMax, bitmapSize, bitmapSize);

            UseBestBaseCommand?.RaiseCanExecuteChanged();   // 绿区重算 → "用最稳位置"可用性可能变化
            RecalcReachVerdict();
        }

        private void SetReachBounds(double xMin, double xMax, double yMin, double yMax)
        {
            ReachXMin = xMin;
            ReachXMax = xMax;
            ReachYMin = yMin;
            ReachYMax = yMax;
        }

        private Pt2[] BuildOffsets(double stepX, double stepY)
            => ReachMapGeometry.GridOffsets(stepX, stepY, _reachInvertX, _reachInvertY, _reachEyeModeIndex == 0);

        private Pt2[] BuildCurrentGrid()
        {
            double step = ParseCoordOr(_reachStepMm, 10);
            double bx = ParseCoordOr(_reachBaseX, 0);
            double by = ParseCoordOr(_reachBaseY, 0);
            return ReachMapGeometry.BuildGrid(bx, by, step, step,
                _reachInvertX, _reachInvertY, _reachEyeModeIndex == 0);
        }

        /// <summary>
        /// 九点逐点判定并生成结论文案。
        /// 文案要给到"第几点、哪条不满足、还差多少 mm"——这是现场能直接用的信息。
        /// </summary>
        private void RecalcReachVerdict()
        {
            var map = _reachMap;
            if (map == null || map.IsEmpty)
            {
                ReachVerdictText = "尚未载入可达域数据 —— 连上控制器后点「📡 零运动实测」；没有设备时可先点「👁 示意」预览界面。";
                ReachVerdictLevel = 0;
                return;
            }

            double margin = ParseCoordOr(_reachMarginMm, 10);
            double step = ParseCoordOr(_reachStepMm, 10);
            var pts = BuildCurrentGrid();

            int bad = 0, unsafeCnt = 0;
            double worst = double.MaxValue;
            var badDesc = new List<string>();

            for (int i = 0; i < pts.Length; i++)
            {
                var v = ReachMapGeometry.Evaluate(map, pts[i].X, pts[i].Y, margin);
                if (v.State == ReachPointState.Safe)
                {
                    if (v.Slack < worst) worst = v.Slack;
                }
                else
                {
                    bad++;
                    if (v.State == ReachPointState.Unsafe) unsafeCnt++;
                    if (badDesc.Count < 4) badDesc.Add($"第{i + 1}点 {v.Text}");
                }
            }

            var fb = _reachFeasible;
            bool zoneOk = fb != null && fb.Count > 0;
            string zoneText = zoneOk
                ? (fb.BestCenter.HasValue
                    ? $"｜绿区 {fb.Count} 像素，最稳基准位 ({fb.BestCenter.Value.X:F1}, {fb.BestCenter.Value.Y:F1})、整体余量 {fb.BestClearanceMm:F1} mm"
                    : $"｜绿区 {fb.Count} 像素")
                : "｜⚠ 该步长/margin 下不存在可行基准位";

            if (bad == 0)
            {
                ReachVerdictLevel = 1;
                ReachVerdictText = $"✅ 九点全部安全（最小余量 {worst:F1} mm）{zoneText}";
            }
            else if (zoneOk)
            {
                // 当前基准位不行，但绿区里有能行的 —— 属"可救"，用橙色而不是红色吓人
                ReachVerdictLevel = 2;
                ReachVerdictText = $"⚠ {bad}/9 点越界：{string.Join("；", badDesc)}" +
                                   $"\n→ 把基准位拖进绿区即可，推荐 ({fb.BestCenter?.X:F1}, {fb.BestCenter?.Y:F1})";
            }
            else
            {
                ReachVerdictLevel = 3;
                string head = unsafeCnt > 0
                    ? $"⛔ {bad}/9 点越界（其中 {unsafeCnt} 点落在整体不可达方向）：{string.Join("；", badDesc)}"
                    : $"⛔ {bad}/9 点越界：{string.Join("；", badDesc)}";
                ReachVerdictText = head +
                                   $"\n→ 步长 {step:F1} mm 在本工位不可行，请减小步长或放宽 margin（当前 {margin:F1} mm）";
            }
        }

        private void LoadReachMapFromFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择可达域扫描结果",
                Filter = "可达域扫描结果 (*.json)|*.json|所有文件 (*.*)|*.*",
                InitialDirectory = FindVerifyOutDir(),
            };
            if (dlg.ShowDialog() != true) return;

            string err;
            var map = ReachMapData.LoadFromFile(dlg.FileName, out err);
            if (map == null)
            {
                ReachActionText = "载入失败: " + err;
                return;
            }
            ApplyReachMap(map, $"已载入 {Path.GetFileName(dlg.FileName)}：{map.Describe()}");
        }

        /// <summary>从程序目录向上找 .workbuddy/_verify_out（扫描器落盘目录），找不到就用程序目录</summary>
        private static string FindVerifyOutDir()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string p = Path.Combine(dir.FullName, ".workbuddy", "_verify_out");
                if (Directory.Exists(p)) return p;
                dir = dir.Parent;
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// 零运动实测扫描：逐方向径向扫描出真实可达域。
        ///
        /// ★ 全程只用 CheckReach（内部 SPEL+ TargetOK）—— 不驱动电机，
        ///   越界只是返回 False，不会有 CP 直线"突然停止 + 撞击伺服"的风险。
        /// ★ 扫描期间必须停掉 250ms 状态轮询：两者共用同一条 TCP 行协议，
        ///   轮询抢不到锁会累积失败计数，被误判成"断线"而自动断开。
        /// </summary>
        private async Task ScanReachMapCoreAsync()
        {
            var epson = _epson;
            if (epson == null || !IsConnected)
            {
                ReachActionText = "未连接控制器，无法实测扫描。";
                return;
            }

            // 解析扫描平面 Z/U：留空 = 用当前机械手 Z/U；填了 = 按填的值扫
            double z = ParseCoordOr(ReachScanZ, double.NaN);
            double u = ParseCoordOr(ReachScanU, double.NaN);
            if (double.IsNaN(z)) z = PosZ;
            if (double.IsNaN(u)) u = PosU;

            // 方向数：默认 24，钳到 8~72（越细越慢；72 方向约需几分钟）
            int dirs = 24;
            if (!int.TryParse((ReachScanDirs ?? "").Trim(), out dirs) || dirs < 8) dirs = 24;
            if (dirs > 72) dirs = 72;

            var cts = new CancellationTokenSource();
            _reachScanCts = cts;
            ReachScanButtonText = "⏹ 取消扫描";
            ReachScanProgress = 0;
            ReachScanProgressVisible = true;
            _pollTimer?.Stop();
            RefreshCommandStates();

            const double rMin = 20, rMax = 450, coarse = 5, tol = 1.0;

            var rows = new List<ReachMapDirection>();
            int calls = 0;

            try
            {
                ReachActionText = $"扫描中… 0/{dirs} 方向（Z={z:F1} U={u:F1}；机械手原地不动）";

                await Task.Run(() =>
                {
                    for (int i = 0; i < dirs; i++)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        double deg = 360.0 * i / dirs;

                        double? rIn, rOut;
                        int used;
                        ScanOneDirection(epson, deg, z, u, rMin, rMax, coarse, tol, cts.Token,
                                         out rIn, out rOut, out used);
                        calls += used;
                        rows.Add(new ReachMapDirection { Deg = Math.Round(deg, 3), RIn = rIn, ROut = rOut });

                        int done = i + 1, sent = calls;
                        var disp = Application.Current?.Dispatcher;
                        if (disp != null)
                            disp.BeginInvoke(new Action(() =>
                            {
                                ReachScanProgress = Math.Round(100.0 * done / dirs, 1);
                                ReachActionText = $"扫描中… {done}/{dirs} 方向（已发 {sent} 次只读 CHECK）";
                            }));
                    }
                }, cts.Token);

                if (rows.All(r => r.ROut == null))
                {
                    ReachActionText = "⚠ 所有方向都不可达 —— 请检查：Z/U 是否合法、手系设定是否正确、伺服是否上电。";
                    return;
                }

                var map = new ReachMapData
                {
                    ScannedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Host = SelectedRobot?.DeviceKey ?? string.Empty,
                    PlaneZ = z,
                    PlaneU = u,
                    CheckCalls = calls,
                    Directions = rows,
                };

                // ★ 落盘 JSON（与 map_workspace.py 同格式），下次点「📂 载入扫描结果」能直接选它
                string savedPath = TrySaveReachMapJson(map);

                ApplyReachMap(map,
                    $"实测完成：{rows.Count(r => r.ROut != null)}/{dirs} 个方向可达，共 {calls} 次只读 CHECK（零运动）。"
                    + (savedPath != null ? $" 已落盘 {Path.GetFileName(savedPath)}" : ""));
            }
            catch (OperationCanceledException)
            {
                ReachActionText = "已取消扫描（未改动任何数据）。";
            }
            catch (Exception ex)
            {
                ReachActionText = "扫描失败: " + ex.Message;
            }
            finally
            {
                _reachScanCts = null;
                ReachScanButtonText = "📡 零运动实测";
                ReachScanProgressVisible = false;
                _pollTimer?.Start();
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// 把扫描结果落盘到 .workbuddy/_verify_out/workspace_map_*.json，
        /// 字段与 map_workspace.py 对齐（供 ReachMapData.LoadFromFile 直接读）。
        /// 返回落盘绝对路径；失败返回 null（不中断扫描主流程，仅记日志）。
        /// </summary>
        private static string TrySaveReachMapJson(ReachMapData map)
        {
            try
            {
                var dir = FindVerifyOutDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = Path.Combine(dir, $"workspace_map_{ts}.json");

                var root = new Newtonsoft.Json.Linq.JObject();
                root["scanned_at"] = map.ScannedAt;
                root["host"] = map.Host;
                root["hand_reply"] = map.HandReply;
                root["pos_reply"] = map.PosReply;
                root["check_calls"] = map.CheckCalls;
                var plane = new Newtonsoft.Json.Linq.JObject();
                plane["z"] = map.PlaneZ;
                plane["u"] = map.PlaneU;
                root["plane"] = plane;
                var arr = new Newtonsoft.Json.Linq.JArray();
                foreach (var d in map.Directions)
                {
                    var o = new Newtonsoft.Json.Linq.JObject();
                    o["deg"] = d.Deg;
                    o["r_in"] = d.RIn;
                    o["r_out"] = d.ROut;
                    arr.Add(o);
                }
                root["directions"] = arr;

                File.WriteAllText(path, root.ToString(Newtonsoft.Json.Formatting.Indented),
                                  new System.Text.UTF8Encoding(false));
                return path;
            }
            catch
            {
                return null;   // 落盘失败不打断扫描（画布已能显示）
            }
        }

        /// <summary>
        /// 单方向径向扫描：粗扫定位"第一段连续可达区间"，再二分细化内外边界。
        /// 遇到中间空洞直接截断（保守）——不假设"两次可达之间必然可达"。
        /// </summary>
        private static void ScanOneDirection(
            EpsonRobot epson, double deg, double z, double u,
            double rMin, double rMax, double coarse, double tol,
            CancellationToken token,
            out double? rIn, out double? rOut, out int calls)
        {
            rIn = null;
            rOut = null;

            double a = deg * Math.PI / 180.0;
            double ca = Math.Cos(a), sa = Math.Sin(a);

            int n = 0;
            // 局部函数不能捕获 out 参数，故用局部变量计数，最后回写
            bool Probe(double r)
            {
                token.ThrowIfCancellationRequested();
                n++;
                var res = epson.CheckReach((float)(r * ca), (float)(r * sa), (float)z, (float)u);
                return res.Success && res.Data;
            }

            // ---- 粗扫：由内向外找第一段连续可达区间 ----
            double? firstOk = null, lastOk = null;
            for (double r = rMin; r <= rMax + 1e-9; r += coarse)
            {
                bool ok = Probe(r);
                if (ok)
                {
                    if (firstOk == null) firstOk = r;
                    lastOk = r;
                }
                else if (firstOk != null)
                {
                    break;   // 可行段结束
                }
            }

            if (firstOk == null) { calls = n; return; }

            // ---- 内边界：不可行 → 可行 的临界（二分）----
            double lo = Math.Max(rMin, firstOk.Value - coarse), hi = firstOk.Value;
            for (int k = 0; k < 14 && hi - lo > tol; k++)
            {
                double mid = (lo + hi) / 2.0;
                if (Probe(mid)) hi = mid; else lo = mid;
            }
            rIn = hi;

            // ---- 外边界：可行 → 不可行 的临界（二分）----
            if (lastOk.Value >= rMax - 1e-9)
            {
                rOut = rMax;   // 一直到扫描上限都可达，外边界超出本次搜索范围
            }
            else
            {
                double lo2 = lastOk.Value, hi2 = Math.Min(rMax, lastOk.Value + coarse);
                for (int k = 0; k < 14 && hi2 - lo2 > tol; k++)
                {
                    double mid = (lo2 + hi2) / 2.0;
                    if (Probe(mid)) lo2 = mid; else hi2 = mid;
                }
                rOut = lo2;
            }

            calls = n;
        }

        /// <summary>
        /// 开走前二次校验：把 9 个终点交给控制器自己判一次（9 次只读 CHECK，机械手不动）。
        /// 注意语义 —— TargetOK 只管【终点】不管【轨迹】，所以"全部通过"不等于 CP 直线路径也安全。
        /// </summary>
        private async Task PrecheckGridCoreAsync()
        {
            var epson = _epson;
            if (epson == null || !IsConnected || _reachMap == null) return;

            var grid = BuildCurrentGrid();
            double z = PosZ, u = PosU;

            ReachActionText = "开走前校验中…（9 次只读 CHECK，机械手不动）";

            var okFlags = new bool[9];
            try
            {
                await Task.Run(() =>
                {
                    for (int i = 0; i < 9; i++)
                    {
                        var res = epson.CheckReach((float)grid[i].X, (float)grid[i].Y, (float)z, (float)u);
                        okFlags[i] = res.Success && res.Data;
                    }
                });
            }
            catch (Exception ex)
            {
                ReachActionText = "校验失败: " + ex.Message;
                return;
            }

            var bad = new List<string>();
            for (int i = 0; i < 9; i++) if (!okFlags[i]) bad.Add($"第{i + 1}点");

            ReachActionText = bad.Count == 0
                ? $"✅ 控制器裁决：9 点终点全部可达（Z={z:F1}, U={u:F1}）。注意这只管终点，直线路径仍可能中途越界。"
                : $"⛔ 控制器拒绝：{string.Join("、", bad)} —— 请移动基准位或减小步长后重试。";
        }

        /// <summary>容错解析坐标输入框（全角字符 / 半截输入 → 回退默认值）</summary>
        private static double ParseCoordOr(string s, double fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            string t = s.Trim()
                        .Replace('。', '.').Replace('．', '.')
                        .Replace('－', '-').Replace('，', ',');
            double v;
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        /// <summary>
        /// 统一安全执行器（Home/停止）：后台 IO + 异常兜底 + 门闩防重入。
        /// 任何底层异常都不会漏到 async void 事件链导致 WPF 崩溃；失败弹窗提示。
        /// </summary>
        private async Task RunSafeAsync(string actionName, Func<Result> action)
        {
            if (_commandBusy) return;      // 上一条命令未结束 → 本次点击忽略（按钮已禁用，双保险）
            _commandBusy = true;
            RefreshCommandStates();
            try
            {
                Result res = await Task.Run(action);
                if (!res.Success)
                    MessageBox.Show($"{actionName}失败: {res.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{actionName}内部异常: {ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _commandBusy = false;
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// 走位专用执行核心：后台 IO + 异常兜底（不弹窗，错误交由调用方统一提示），
        /// 占用命令门闩防止运动中重复点击导致指令交错。
        /// </summary>
        private async Task<Result> RunBusyCoreAsync(Func<Result> action)
        {
            if (_commandBusy) return null; // 被门闩拦截：调用方按"已忽略"处理
            _commandBusy = true;
            RefreshCommandStates();
            try
            {
                return await Task.Run(action);
            }
            catch (Exception ex)
            {
                return Result.Fail($"内部异常: {ex.Message}", ex: ex);
            }
            finally
            {
                _commandBusy = false;
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// 读取 RC+ 前 N 个示教点（只读，2026-09-11）。
        ///
        /// 【为什么先探一个点】
        /// 只有 TCP 脚本通道支持读点（协议 POINT? n）。SDK/仿真通道会在第一次调用就返回
        /// "仅 TCP 脚本通道支持" —— 先用 P0 探一次即可区分"通道不支持"与"通道正常但点未定义"，
        /// 避免在 SDK 通道下白跑 N 次超时把界面卡住。
        ///
        /// 【P0 未定义不算失败】应答是 POINT 0,UNDEF（Success=true / Defined=false）。
        /// 【为什么要把错误号说出来】脚本 POINT? 现在回 "POINT n,UNDEF,Err"。全是未定义时
        /// 用户看到的就是"点了没反应" —— 状态栏必须给出首例原因（如 2513=标签未注册），
        /// 否则分不清"点真没示教"和"脚本没重新编译"。
        /// 【IO 全在后台线程】每条指令都是一发一收的阻塞式应答，绝不在 UI 线程跑。
        /// </summary>
        private async Task LoadRcPointsCoreAsync()
        {
            var epson = _epson;
            if (epson == null) return;
            if (_rcLoadBusy) return;            // 防重入：连点按钮不会打两轮
            _rcLoadBusy = true;

            int count = 10;
            if (!int.TryParse((RcPointCountText ?? string.Empty).Trim(), out count) || count <= 0)
            {
                count = 10;
            }
            if (count > 100) count = 100;   // 上限：防手填 9999 把界面卡死

            int undefCount = 0;
            var rows = new SortedDictionary<int, TeachPointModel>();   // 点号 → 行（含失败/未定义占位，保 0..N-1 顺序）
            var failed = new List<string>();                            // "P7: xxx"
            string firstUndefReason = null;                             // 未定义点的首例原因（SPEL+ 错误号翻人话）
            string abortReason = null;

            // ★★ 关键修复（2026-09-11）：批量读点期间【必须停状态轮询】。
            // 两者共用同一条 TCP 行协议（适配层 _ioLock 串行），轮询每 250ms 抢一次锁；
            // 抢锁失败曾让 SendCommand 回 null → 单点被判"无应答" → 而原来上层在【第一处失败
            // 就整轮中止】—— 现场表现就是"读到一半没了 / 某个点读不到"，且每次断在不同点号上
            // （实测断在 1/2/3/4/6 都出现过，这正是争锁、而非某个点不可读的决定性证据）。
            // 与 P1「📡 零运动实测」同一口径。
            RcPointsStatusText = "正在读取 RC+ 示教点…（已暂停状态轮询）";
            _pollTimer?.Stop();
            RefreshCommandStates();

            try
            {
                await Task.Run(() =>
                {
                    // ---- ① 能力探测：P0 必须能读回来（无论是否已定义）----
                    // QueryPoint 内部已自带 3 次重试；走到这里仍失败 = 通道/脚本真的不支持读点，
                    // 此时中止才是对的（避免把"通道不支持"刷成 N 条同样的错）。
                    var probe = epson.ReadTeachPoint(0);
                    if (!probe.Success)
                    {
                        abortReason = "P0: " + probe.Message;
                        return;
                    }
                    Consume(probe.Data);

                    // ---- ② 逐点读取：★单点失败【不再整体中止】----
                    // 记下该点并继续读后面的 —— 少一个点，远好过整张表腰斩。
                    for (int i = 1; i < count; i++)
                    {
                        var r = epson.ReadTeachPoint(i);
                        if (!r.Success)
                        {
                            failed.Add($"P{i}: {r.Message}");
                            rows[i] = new TeachPointModel { Name = $"P{i}（读取失败）", IsUnreadable = true };
                            continue;
                        }
                        Consume(r.Data);
                    }
                });
            }
            finally
            {
                _rcLoadBusy = false;
                _pollTimer?.Start();    // 恢复轮询（Cleanup/断开时会被再停掉；重复 Start 无害）
                RefreshCommandStates();
            }

            void Consume(EpsonTeachPoint p)
            {
                if (p == null) return;
                if (!p.Defined)
                {
                    undefCount++;
                    if (firstUndefReason == null) firstUndefReason = $"P{p.Index}: {p.ReasonText}";
                    rows[p.Index] = new TeachPointModel { Name = $"P{p.Index}（未定义）", IsUnreadable = true };
                    return;
                }
                rows[p.Index] = new TeachPointModel
                {
                    Name = string.IsNullOrWhiteSpace(p.Label) ? $"P{p.Index}" : $"P{p.Index} {p.Label}",
                    X = p.X,
                    Y = p.Y,
                    Z = p.Z,
                    U = p.U
                };
            }

            RcPoints.Clear();
            foreach (var kv in rows) RcPoints.Add(kv.Value);
            // 默认选中第一个"可回放"的点，避免落到"（读取失败）"占位行上让回放按钮永远灰着
            SelectedRcPoint = RcPoints.FirstOrDefault(p => !p.IsUnreadable) ?? RcPoints.FirstOrDefault();

            // ★ 修复（2026-09-11）：finally 里的 RefreshCommandStates() 执行在 RcPoints 更新【之前】，
            // 导致 ImportRcPointsCommand 的 CanExecute（依赖 RcPoints.Count）评估的是旧值 —— 初次
            // 加载后按钮一直灰。这里在集合更新后再刷一次，让「📥 并入示教点」按最新数据亮/灰。
            RefreshCommandStates();

            if (abortReason != null)
            {
                RcPointsStatusText = $"读取中止：{abortReason}";
                return;
            }

            // ★"全部未定义/读不到"最容易被误当成"按钮没反应" —— 必须把原因一条条说出来：
            //   未定义（点真没示教）与读取失败（通信/脚本问题）是两回事，分开计数。
            int okCount = rows.Count - failed.Count - undefCount;
            var sb = new StringBuilder();
            sb.Append($"已同步 RC+ 点号 0~{count - 1}：有效 {okCount} 个，未定义 {undefCount} 个，读取失败 {failed.Count} 个");
            if (failed.Count > 0)
            {
                sb.Append("（").Append(string.Join("；", failed.Take(3)));
                if (failed.Count > 3) sb.Append($"；…另 {failed.Count - 3} 个");
                sb.Append("）");
            }
            if (firstUndefReason != null) sb.Append($"，首例未定义原因 {firstUndefReason}");
            sb.Append("（只读，不影响控制器）");
            RcPointsStatusText = sb.ToString();
        }

        /// <summary>
        /// 容错解析 4 个目标框 + 速度框为 float。
        /// 中文输入法下用户常打出全角句号"。"、"，"（Error 7 根因）——这里统一归一为半角后解析。
        /// 任一框非法 → 弹窗明确指出是哪个框 → 返回 false（调用方放弃本次走位）。
        /// </summary>
        private bool TryResolveTargets(out float x, out float y, out float z, out float u, out float speed)
        {
            x = y = z = u = 0f;
            speed = 30f;
            return TryResolveCoord("X", TargetX, out x)
                && TryResolveCoord("Y", TargetY, out y)
                && TryResolveCoord("Z", TargetZ, out z)
                && TryResolveCoord("U", TargetU, out u)
                && TryResolveCoord("速度", SpeedPct, out speed);
        }

        private bool TryResolveCoord(string axisName, string text, out float value)
        {
            value = 0f;
            if (text == null) { MessageBox.Show($"{axisName} 目标为空", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            // 全角字符归一：。→. 、→, ．→. ､→, 全角减号－→-
            string s = text.Trim()
                .Replace('。', '.').Replace('．', '.')
                .Replace('，', ',').Replace('､', ',')
                .Replace('－', '-');

            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                MessageBox.Show($"{axisName} 目标「{text}」不是有效数字（请用半角 . 或全角。均可）",
                    "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        /// <summary>float 坐标格式化为目标框显示文本（去掉多余小数位）</summary>
        private static string FormatCoord(float v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>刷新传输层标识（供 UI 显示当前走的是哪条通信通道）</summary>
        private void RefreshTransportText()
        {
            if (_epson == null) { TransportText = "—"; return; }

            var cs = _epson.GetParam("ConnectionString");
            string connStr = cs.Success ? cs.Data?.ToString() : null;
            bool tcp = connStr != null &&
                       connStr.IndexOf("Protocol=TCP", StringComparison.OrdinalIgnoreCase) >= 0;
            TransportText = tcp
                ? "TCP 脚本协议 (RC+ OpenNet)"
                : "RC+ SDK (RCAPINet / spelnet64)";
        }

        /// <summary>从设备池加载机械手设备（Epson 品牌 IMotionCard）</summary>
        private void LoadRobots()
        {
            RobotDeviceList.Clear();
            var robots = _devicePool.GetAllDevices()
                .Where(d => d is IMotionCard &&
                            d.BrandName != null &&
                            d.BrandName.IndexOf("Epson", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            foreach (var r in robots) RobotDeviceList.Add(r);
            SelectedRobot = RobotDeviceList.FirstOrDefault();
        }

        private void OnSelectedRobotChanged(IDevice oldRobot, IDevice newRobot)
        {
            if (oldRobot != null)
            {
                oldRobot.StateChanged -= OnRobotStateChanged;
                SaveTeachPoints(); // 切换设备前落盘当前设备的示教点
            }

            // 拆箱设备能力
            _motion = newRobot as IMotionCard;
            _io = newRobot as IIoDevice;
            _epson = newRobot as EpsonRobot;

            TeachPoints.Clear();
            SelectedTeachPoint = null;

            if (newRobot == null)
            {
                IsConnected = false;
                IsServoOn = false;
                IsMoving = false;
                TransportText = "—";
                return;
            }

            _disconnectNotified = false; // 切换设备 → 复位断线提示标志
            _pollFailCount = 0;
            newRobot.StateChanged += OnRobotStateChanged;
            IsConnected = newRobot.State == DeviceState.Connected;
            if (IsConnected)
            {
                IsServoOn = true;
                RefreshTransportText();
                LoadTeachPoints();
            }
        }

        private void OnRobotStateChanged(object sender, DeviceState state)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsConnected = state == DeviceState.Connected;
                if (!IsConnected)
                {
                    IsServoOn = false;
                    IsMoving = false;
                }
            });
        }

        /// <summary>
        /// 250ms 轮询核心：IO 在后台线程执行，UI 属性更新在 await 恢复后的 UI 线程。
        /// 连续失败（_pollFailCount ≥ 阈值）判定通信中断：自动置断开并提示一次，
        /// 避免脚本崩/断网后界面停留在"已连接"假象、点按钮全报"控制器未连接"。
        /// </summary>
        private async Task PollStatusCoreAsync()
        {
            if (!IsConnected || _motion == null) return;

            // ---- 后台线程做全部 IO（UI 永不因脚本忙/断线而阻塞）----
            float[] pos = null;
            bool hasIdle = false;
            bool idle = false;
            bool ioOk = true;

            await Task.Run(() =>
            {
                try
                {
                    if (_epson != null)
                    {
                        // ★ 2026-09-16：改走 PollPositions（不抢锁 + 800ms 短超时）。
                        //   原先 GetPositionsAll → POS? 持锁等满 5s：脚本僵死时 250ms 轮询几乎
                        //   连续占住通道，业务命令（关真空/走位）抢锁预算只 400ms ⇒ 全被挤成
                        //   "通道忙未发送"，真因被淹没（现场 2026-09-16 ST_002 就是这么刷屏的）。
                        var all = _epson.PollPositions(out bool channelBusy);
                        if (channelBusy)
                        {
                            // 通道正被业务命令占用 → 本轮跳过：不刷新 UI、也不计入"断线"判定
                            //（否则工位一跑就攒够 12 次失败，误弹"通信中断"）。
                            return;
                        }
                        if (all.Success && all.Data != null && all.Data.Length == 4)
                        {
                            pos = all.Data;
                        }
                        else
                        {
                            ioOk = false; // 断线/脚本停 → 单次失败（连续失败才判定，防抖动）
                        }
                    }
                    else
                    {
                        var r = new float[4];
                        bool ok = true;
                        var rx = _motion.GetCommandPosition(0); if (rx.Success) r[0] = rx.Data; else ok = false;
                        var ry = _motion.GetCommandPosition(1); if (ry.Success) r[1] = ry.Data; else ok = false;
                        var rz = _motion.GetCommandPosition(2); if (rz.Success) r[2] = rz.Data; else ok = false;
                        var ru = _motion.GetCommandPosition(3); if (ru.Success) r[3] = ru.Data; else ok = false;
                        pos = r;
                        ioOk = ok;
                    }

                    if (ioOk && pos != null)
                    {
                        var idleRes = _motion.IsAxisIdle(0);
                        if (idleRes.Success)
                        {
                            hasIdle = true;
                            idle = idleRes.Data;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ioOk = false;
                    System.Diagnostics.Debug.WriteLine($"[RobotDebug] 轮询读取异常: {ex.Message}");
                }
            });

            // ---- await 恢复：UI 线程更新 ----
            if (!ioOk)
            {
                // ★2026-09-11 修：通道被【长事务】占用时轮询本就该失败，不能算"断线"。
                //   命令（MOVE 最长 20s）与批量读点都持着 _ioLock，轮询的 TryEnter 抢不到锁
                //   → GetPositionsAll 失败 → 连续 12 次(3s)就误报"通信中断"弹窗，
                //   用户按提示点「连接」→ 触发一次真重连（现场日志里那对 PING/MOTOR ON 就是它）。
                if (_commandBusy || _rcLoadBusy)
                {
                    _pollFailCount = 0;   // 通道被占用 ≠ 断线，清零防误判
                    return;
                }
                if (++_pollFailCount >= PollFailThreshold)
                {
                    _pollFailCount = 0;
                    if (IsConnected && !_disconnectNotified)
                    {
                        _disconnectNotified = true;
                        IsConnected = false;      // 触发 CanExecute 刷新，所有按钮禁用
                        IsServoOn = false;
                        IsMoving = false;
                        MessageBox.Show(
                            "与机械手通信中断（RC+ 脚本停止运行或网络断开）。\n" +
                            "请确认 RC+ 里 mainTCP 仍在运行，然后重新点击「连接」。",
                            "通信中断", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                return;
            }

            _pollFailCount = 0;
            if (pos != null)
            {
                PosX = pos[0];
                PosY = pos[1];
                PosZ = pos[2];
                PosU = pos[3];
            }
            if (hasIdle) IsMoving = !idle;
        }

        private void RefreshCommandStates()
        {
            ConnectCommand?.RaiseCanExecuteChanged();
            DisconnectCommand?.RaiseCanExecuteChanged();
            ToggleServoCommand?.RaiseCanExecuteChanged();
            HomeCommand?.RaiseCanExecuteChanged();
            StopCommand?.RaiseCanExecuteChanged();
            EmergencyStopCommand?.RaiseCanExecuteChanged();
            MoveToPointCommand?.RaiseCanExecuteChanged();
            SaveTeachPointCommand?.RaiseCanExecuteChanged();
            PlayTeachPointCommand?.RaiseCanExecuteChanged();
            DeleteTeachPointCommand?.RaiseCanExecuteChanged();
            ToggleVaccum1Command?.RaiseCanExecuteChanged();
            ToggleVaccum2Command?.RaiseCanExecuteChanged();
            StepAxisCommand?.RaiseCanExecuteChanged();          // 单轴步进（依赖连接状态 + 命令门闩）
            ShowCameraLiveCommand?.RaiseCanExecuteChanged();    // 相机画面弹窗
            LoadRcPointsCommand?.RaiseCanExecuteChanged();      // RC+ 点位同步
            PlayRcPointCommand?.RaiseCanExecuteChanged();       // RC+ 点位回放走位
            ImportRcPointsCommand?.RaiseCanExecuteChanged();    // RC+ 点位导入示教点
            LoadReachMapCommand?.RaiseCanExecuteChanged();      // 可达域：载入扫描结果
            LoadSyntheticReachMapCommand?.RaiseCanExecuteChanged();
            ScanReachMapCommand?.RaiseCanExecuteChanged();      // 可达域：零运动实测（扫描中可变"取消"）
            UseCurrentPosAsBaseCommand?.RaiseCanExecuteChanged();
            UseBestBaseCommand?.RaiseCanExecuteChanged();       // 依赖绿区是否已算出最稳基准位
            PrecheckGridCommand?.RaiseCanExecuteChanged();      // 开走前 9 次 CHECK 校验
        }

        /// <summary>Tab 隐藏/页面卸载时停止轮询并保存示教点</summary>
        public void Cleanup()
        {
            _pollTimer?.Stop();
            if (SelectedRobot != null)
            {
                SelectedRobot.StateChanged -= OnRobotStateChanged;
            }
            SaveTeachPoints();
        }

        #region 示教点持久化

        private string TeachPointsDir => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Data", "RobotTeachPoints");

        private string TeachPointsFilePath => SelectedRobot == null
            ? null
            : Path.Combine(TeachPointsDir, Sanitize(SelectedRobot.DeviceKey) + ".teachpoints.json");

        private static string Sanitize(string key)
        {
            if (string.IsNullOrEmpty(key)) return "default";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(key.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private void LoadTeachPoints()
        {
            TeachPoints.Clear();
            var path = TeachPointsFilePath;
            if (path == null || !File.Exists(path)) return;
            try
            {
                var list = JsonConvert.DeserializeObject<List<TeachPointModel>>(File.ReadAllText(path));
                if (list != null)
                {
                    foreach (var p in list) TeachPoints.Add(p);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RobotDebug] 示教点加载失败: {ex.Message}");
            }
        }

        private void SaveTeachPoints()
        {
            var path = TeachPointsFilePath;
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(TeachPointsDir);
                File.WriteAllText(path, JsonConvert.SerializeObject(TeachPoints.ToList(), Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RobotDebug] 示教点保存失败: {ex.Message}");
            }
        }

        #endregion
    }

    /// <summary>示教点数据模型（与持久化 JSON 一一对应，供工位业务流直接读取）</summary>
    public class TeachPointModel : ViewModelBase
    {
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float U { get; set; }

        /// <summary>
        /// 占位行标记：该行是"RC+ 点未定义"或"读取失败"的提示行，坐标不可用。
        /// 用途：让"读不到的点"在列表里可见（而不是整张表腰斩或缺行），
        /// 同时禁止回放、导入时跳过 —— 避免把 0,0,0,0 当成真点发出去。
        /// 【不落盘】示教点 JSON 不受影响。
        /// </summary>
        [JsonIgnore]
        public bool IsUnreadable { get; set; }

        public override string ToString()
        {
            // 占位行（未定义/读取失败）没有坐标 —— 不打出 0.0/0.0/0.0/0.0，
            // 否则在列表里会被误当成一个"位于原点"的真点。
            if (IsUnreadable) return Name;
            return $"{Name}   X={X:F1}  Y={Y:F1}  Z={Z:F1}  U={U:F1}";
        }
    }
}
