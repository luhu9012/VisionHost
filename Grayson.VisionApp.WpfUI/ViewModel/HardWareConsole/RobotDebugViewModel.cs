using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Newtonsoft.Json;
using Plugins.Robot.Epson; // WpfUI 已 ProjectReference 插件项目：类型判断调用 EpsonRobot 专属方法
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
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

            // 相机实时画面弹窗（与相机/轴调试同款 CameraLiveWindow；非模态可边动边看）
            ShowCameraLiveCommand = new RelayCommand(() => ShowCameraLiveWindow(), () => true);
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
                        var all = _epson.GetPositionsAll();
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

        public override string ToString()
        {
            return $"{Name}   X={X:F1}  Y={Y:F1}  Z={Z:F1}  U={U:F1}";
        }
    }
}
