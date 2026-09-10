using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Models;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{
    public class AxisControlViewModel : ViewModelBase
    {
        private readonly IDevicePool _devicePool;
        private DispatcherTimer _statusPollTimer;

        #region 属性定义 - 板卡与轴

        public ObservableCollection<IMotionCard> MotionDeviceList { get; set; } = new ObservableCollection<IMotionCard>();
        public ObservableCollection<AxisInfoModel> AxisList { get; set; } = new ObservableCollection<AxisInfoModel>();

        private IMotionCard _selectedMotionDevice;
        public IMotionCard SelectedMotionDevice
        {
            get => _selectedMotionDevice;
            set
            {
                var oldDevice = _selectedMotionDevice;
                if (Set(ref _selectedMotionDevice, value))
                {
                    OnSelectedDeviceChanged(oldDevice, value);
                }
            }
        }

        private AxisInfoModel _selectedAxis;
        public AxisInfoModel SelectedAxis
        {
            get => _selectedAxis;
            set
            {
                if (Set(ref _selectedAxis, value))
                {
                    RefreshCommandCanExecute();
                    LoadSelectedAxisParam();
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
                    RefreshCommandCanExecute();
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
            set => Set(ref _isServoOn, value);
        }

        private bool _isActive = true;
        /// <summary>
        /// 表示当前轴调试 Tab 是否处于显示/激活状态。非激活时停止轮询。
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (Set(ref _isActive, value))
                {
                    if (_isActive)
                    {
                        _statusPollTimer?.Start();
                    }
                    else
                    {
                        _statusPollTimer?.Stop();
                    }
                }
            }
        }

        #endregion

        #region 属性定义 - 通用 MotionParam 运动参数

        // 🌟 参数编辑改造（2026-09-01）：
        // 此前 TextBox 直接绑定 struct 子属性（CurrentMotionParam.Speed）——WPF 对值类型
        // 子属性的 TwoWay 写回链路脆弱，且 LoadSelectedAxisParam 每次选轴都写死默认值，
        // 表现为「界面上改了不生效 / 切轴被重置」。
        // 现改为：VM 标量属性承接编辑（绑定绝对可靠）+ 静态缓存（按 设备Key:轴号，
        // 切轴/重进页面不丢）+ 选轴时优先从控制器回读真实参数（IMotionCard.GetMotionParam，
        // ZMC 支持；Epson 等不支持时回退缓存/默认值）。下发时由 ApplyParamCommand 组装 struct。

        /// <summary>轴参数编辑缓存（key = "设备Key:轴号"）。static：硬件控制台页非缓存单例，
        /// 每次导航重建 VM，缓存跨页面实例存活，编辑值不因切换页面丢失。</summary>
        private static readonly Dictionary<string, MotionParam> _paramEditCache =
            new Dictionary<string, MotionParam>();

        /// <summary>软限位编辑缓存（key = "设备Key:轴号"，[0]=正限位 [1]=负限位）。</summary>
        private static readonly Dictionary<string, float[]> _limitEditCache =
            new Dictionary<string, float[]>();

        private string ParamCacheKey =>
            SelectedMotionDevice == null || SelectedAxis == null
                ? null
                : $"{SelectedMotionDevice.DeviceKey}:{SelectedAxis.AxisIndex}";

        private float _paramSpeed = 50f;
        public float ParamSpeed { get => _paramSpeed; set => Set(ref _paramSpeed, value); }

        private float _paramAccel = 50f;
        public float ParamAccel { get => _paramAccel; set => Set(ref _paramAccel, value); }

        private float _paramDecel = 50f;
        public float ParamDecel { get => _paramDecel; set => Set(ref _paramDecel, value); }

        private float _paramLspeed = 50f;
        public float ParamLspeed { get => _paramLspeed; set => Set(ref _paramLspeed, value); }

        private float _paramUnit = 50f;
        public float ParamUnit { get => _paramUnit; set => Set(ref _paramUnit, value); }

        private float _paramSramp = 50f;
        public float ParamSramp { get => _paramSramp; set => Set(ref _paramSramp, value); }

        private float _paramCreep = 10f;
        public float ParamCreep { get => _paramCreep; set => Set(ref _paramCreep, value); }

        private float _positiveLimit = 99999f;
        public float PositiveLimit
        {
            get => _positiveLimit;
            set => Set(ref _positiveLimit, value);
        }

        private float _negativeLimit = -99999f;
        public float NegativeLimit
        {
            get => _negativeLimit;
            set => Set(ref _negativeLimit, value);
        }

        /// <summary>由 VM 标量属性组装当前编辑的 MotionParam（下发/缓存用）。</summary>
        private MotionParam BuildParamFromProperties() => new MotionParam
        {
            Speed = ParamSpeed,
            Accel = ParamAccel,
            Decel = ParamDecel,
            Lspeed = ParamLspeed,
            Unit = ParamUnit,
            Sramp = ParamSramp,
            CreepSpeed = ParamCreep
        };

        /// <summary>把参数对象回填到 VM 标量属性（选轴加载用）。</summary>
        private void ApplyParamToProperties(MotionParam p)
        {
            ParamSpeed = p.Speed;
            ParamAccel = p.Accel;
            ParamDecel = p.Decel;
            ParamLspeed = p.Lspeed;
            ParamUnit = p.Unit;
            ParamSramp = p.Sramp;
            ParamCreep = p.CreepSpeed;
        }

        #endregion

        #region 属性定义 - 运动模式与定位测试

        private int _moveModeIndex = 0; // 0: JOG 连续, 1: 相对移动, 2: 绝对定位
        public int MoveModeIndex
        {
            get => _moveModeIndex;
            set => Set(ref _moveModeIndex, value);
        }

        private float _targetDistance = 10.0f; // 相对移动距离/绝对目标坐标
        public float TargetDistance
        {
            get => _targetDistance;
            set => Set(ref _targetDistance, value);
        }

        #endregion

        #region 属性定义 - 当前轴实时状态监控

        private float _cmdPos;
        public float CmdPos { get => _cmdPos; set => Set(ref _cmdPos, value); }

        private float _feedbackPos;
        public float FeedbackPos { get => _feedbackPos; set => Set(ref _feedbackPos, value); }

        private float _currentSpeed;
        public float CurrentSpeed { get => _currentSpeed; set => Set(ref _currentSpeed, value); }

        private bool _isFwdLimit;
        public bool IsFwdLimit { get => _isFwdLimit; set => Set(ref _isFwdLimit, value); }

        private bool _isRevLimit;
        public bool IsRevLimit { get => _isRevLimit; set => Set(ref _isRevLimit, value); }

        private bool _isHomeSignal;
        public bool IsHomeSignal { get => _isHomeSignal; set => Set(ref _isHomeSignal, value); }

        private bool _isAlarm;
        public bool IsAlarm { get => _isAlarm; set => Set(ref _isAlarm, value); }

        private bool _isMoving;
        public bool IsMoving { get => _isMoving; set => Set(ref _isMoving, value); }

        #endregion

        #region 命令定义

        public RelayCommand RefreshDevicesCommand { get; private set; }
        public RelayCommand ConnectCommand { get; private set; }
        public RelayCommand DisconnectCommand { get; private set; }
        public RelayCommand ToggleServoCommand { get; private set; }
        public RelayCommand ApplyParamCommand { get; private set; }
        public RelayCommand ZeroPositionCommand { get; private set; }
        public RelayCommand HomeAxisCommand { get; private set; }
        public RelayCommand<string> DirectionMoveCommand { get; private set; }
        public RelayCommand StopAxisCommand { get; private set; }
        public RelayCommand EmergencyStopCommand { get; private set; }
        public RelayCommand ShowCameraLiveCommand { get; private set; }

        #endregion

        public AxisControlViewModel()
        {
            _devicePool = App.StationHostRuntime?.DevicePool ?? throw new InvalidOperationException("DevicePool not initialized");
            InitCommands();
            InitStatusTimer();
            LoadMotionDevices();
        }

        private void InitStatusTimer()
        {
            _statusPollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _statusPollTimer.Tick += (s, e) => PollAxisStatus();
            _statusPollTimer.Start();
        }

        private void InitCommands()
        {
            RefreshDevicesCommand = new RelayCommand(_ => LoadMotionDevices());

            ConnectCommand = new RelayCommand(async _ =>
            {
                if (SelectedMotionDevice == null) return;
                var res = await Task.Run(() => SelectedMotionDevice.Connect());
                if (!res.Success)
                {
                    MessageBox.Show($"连接运动板卡失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }, _ => SelectedMotionDevice != null && !IsConnected);

            DisconnectCommand = new RelayCommand(async _ =>
            {
                if (SelectedMotionDevice == null) return;
                await Task.Run(() => SelectedMotionDevice.Disconnect());
                IsServoOn = false;
            }, _ => SelectedMotionDevice != null && IsConnected);

            ToggleServoCommand = new RelayCommand(_ =>
            {
                if (SelectedMotionDevice == null || SelectedAxis == null || !IsConnected) return;

                bool nextState = !IsServoOn;
                var res = SelectedMotionDevice.SetAxisEnable(SelectedAxis.AxisIndex, nextState);
                if (res.Success) IsServoOn = nextState;
                else MessageBox.Show($"使能切换失败: {res.Message}", "硬件错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }, _ => SelectedMotionDevice != null && IsConnected && SelectedAxis != null);

            // 下发运动参数与软限位
            ApplyParamCommand = new RelayCommand(_ =>
            {
                if (SelectedMotionDevice == null || SelectedAxis == null || !IsConnected) return;

                var param = BuildParamFromProperties();
                var resParam = SelectedMotionDevice.SetMotionParam(SelectedAxis.AxisIndex, param);
                var resLimit = SelectedMotionDevice.SetSoftLimits(SelectedAxis.AxisIndex, PositiveLimit, NegativeLimit);

                // 写入编辑缓存：切换轴/重进页面后仍显示本次下发值
                var key = ParamCacheKey;
                if (key != null)
                {
                    _paramEditCache[key] = param;
                    _limitEditCache[key] = new[] { PositiveLimit, NegativeLimit };
                }

                if (resParam.Success && resLimit.Success)
                    MessageBox.Show("参数与限位下发成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show($"下发失败: {resParam.Message} / {resLimit.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }, _ => SelectedMotionDevice != null && IsConnected && SelectedAxis != null);

            // 指令位置清零 (SetCommandPosition)
            ZeroPositionCommand = new RelayCommand(_ =>
            {
                if (SelectedMotionDevice == null || SelectedAxis == null || !IsConnected) return;
                SelectedMotionDevice.SetCommandPosition(SelectedAxis.AxisIndex, 0f);
            }, _ => SelectedMotionDevice != null && IsConnected && SelectedAxis != null);

            HomeAxisCommand = new RelayCommand(_ =>
            {
                if (SelectedMotionDevice == null || SelectedAxis == null || !IsConnected) return;
                var res = SelectedMotionDevice.Home(SelectedAxis.AxisIndex, 4);
                if (!res.Success) MessageBox.Show($"回零失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }, _ => SelectedMotionDevice != null && IsConnected && SelectedAxis != null);

            // 方向运动 (支持 JOG / 相对 / 绝对)
            DirectionMoveCommand = new RelayCommand<string>(dir =>
            {
                if (SelectedMotionDevice == null || SelectedAxis == null || !IsConnected) return;

                // 运动前确保参数生效（用当前界面编辑值组装，不再依赖 struct 绑定）
                SelectedMotionDevice.SetMotionParam(SelectedAxis.AxisIndex, BuildParamFromProperties());

                int direction = (dir != null && dir.Contains("+")) ? 1 : -1;

                if (MoveModeIndex == 0) // JOG 点动
                {
                    SelectedMotionDevice.JogMove(SelectedAxis.AxisIndex, direction);
                }
                else if (MoveModeIndex == 1) // 相对移动
                {
                    float dist = TargetDistance * direction;
                    SelectedMotionDevice.MoveRelative(SelectedAxis.AxisIndex, dist, ParamSpeed);
                }
                else if (MoveModeIndex == 2) // 绝对定位
                {
                    SelectedMotionDevice.MoveAbsolute(SelectedAxis.AxisIndex, TargetDistance, ParamSpeed);
                }
            }, _ => SelectedMotionDevice != null && IsConnected && SelectedAxis != null);

            StopAxisCommand = new RelayCommand(_ =>
            {
                if (SelectedMotionDevice == null || SelectedAxis == null || !IsConnected) return;
                SelectedMotionDevice.StopAxis(SelectedAxis.AxisIndex);
            }, _ => SelectedMotionDevice != null && IsConnected && SelectedAxis != null);

            EmergencyStopCommand = new RelayCommand(_ =>
            {
                if (SelectedMotionDevice == null || !IsConnected) return;
                SelectedMotionDevice.RapidStop();
            }, _ => SelectedMotionDevice != null && IsConnected);

            // 打开相机实时画面弹窗（非模态，轴动时可动态观察相机视角）
            ShowCameraLiveCommand = new RelayCommand(_ => ShowCameraLiveWindow());
        }

        /// <summary>相机实时画面弹窗单例引用（防重复打开）</summary>
        private static Window _cameraLiveWindow;

        private static void ShowCameraLiveWindow()
        {
            if (_cameraLiveWindow != null)
            {
                // 已打开：激活置前即可
                _cameraLiveWindow.Activate();
                return;
            }

            try
            {
                _cameraLiveWindow = new View.HardwareConsole.CameraLiveWindow
                {
                    Owner = Application.Current?.MainWindow
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

        private void LoadMotionDevices()
        {
            MotionDeviceList.Clear();
            var devices = _devicePool.GetAllDevices().OfType<IMotionCard>();
            foreach (var card in devices) MotionDeviceList.Add(card);
            SelectedMotionDevice = MotionDeviceList.FirstOrDefault();
        }

        /// <summary>
        /// VM 释放或 Tab 隐藏时调用：停止轮询并释放事件订阅。
        /// </summary>
        public void Cleanup()
        {
            _statusPollTimer?.Stop();
            if (SelectedMotionDevice != null)
            {
                SelectedMotionDevice.StateChanged -= OnDeviceStateChanged;
            }
        }

        private void OnSelectedDeviceChanged(IMotionCard oldDevice, IMotionCard newDevice)
        {
            if (oldDevice != null) oldDevice.StateChanged -= OnDeviceStateChanged;

            AxisList.Clear();
            if (newDevice == null)
            {
                IsConnected = false;
                return;
            }

            newDevice.StateChanged += OnDeviceStateChanged;
            IsConnected = newDevice.State == DeviceState.Connected;

            LoadAxesForDevice(newDevice);
            RefreshCommandCanExecute();
        }

        private void LoadAxesForDevice(IMotionCard device)
        {
            AxisList.Clear();
            if (device == null) return;

            // 轴列表按设备品牌自适应（不同控制器轴号含义不同，不能混用）：
            // - Epson SCARA：0=X（大臂）/ 1=Y（小臂）/ 2=Z（上下）/ 3=U（旋转），
            //   单位 mm / deg，与 EpsonRobot 轴号常量一致；
            // - 其他（ZMC 运动卡等）：保持双滑台工位的默认轴布局。
            bool isEpson = device.BrandName != null &&
                           device.BrandName.IndexOf("Epson", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isEpson)
            {
                AxisList.Add(new AxisInfoModel { AxisIndex = 0, AxisName = "0号轴：X（SCARA 大臂，mm）" });
                AxisList.Add(new AxisInfoModel { AxisIndex = 1, AxisName = "1号轴：Y（SCARA 小臂，mm）" });
                AxisList.Add(new AxisInfoModel { AxisIndex = 2, AxisName = "2号轴：Z（吸嘴上下，mm）" });
                AxisList.Add(new AxisInfoModel { AxisIndex = 3, AxisName = "3号轴：U（末端旋转，deg）" });
            }
            else
            {
                // 轴信息配置：AxisIndex = 控制器内部轴编号，AxisName = 轴实际物理含义/安装位置
                // 双工位滑台
                AxisList.Add(new AxisInfoModel { AxisIndex = 1, AxisName = "1号轴：X轴（左右运动）" });
                AxisList.Add(new AxisInfoModel { AxisIndex = 2, AxisName = "2号轴：右侧Y轴（前后运动）" });
                AxisList.Add(new AxisInfoModel { AxisIndex = 3, AxisName = "3号轴：左侧Y轴（前后运动）" });
                AxisList.Add(new AxisInfoModel { AxisIndex = 0, AxisName = "0号轴：Z轴（升降运动）" });
            }

            SelectedAxis = AxisList.FirstOrDefault();
        }

        private void LoadSelectedAxisParam()
        {
            if (SelectedMotionDevice == null || SelectedAxis == null) return;
            var key = ParamCacheKey;

            // 三级加载：编辑缓存（切轴/重进页面保留）→ 控制器回读（真实值）→ 默认值
            if (key != null && _paramEditCache.TryGetValue(key, out var cachedParam))
            {
                ApplyParamToProperties(cachedParam);
                if (_limitEditCache.TryGetValue(key, out var cachedLimit))
                {
                    PositiveLimit = cachedLimit[0];
                    NegativeLimit = cachedLimit[1];
                }
                return;
            }

            var readParam = SelectedMotionDevice.GetMotionParam(SelectedAxis.AxisIndex);
            if (readParam.Success)
            {
                ApplyParamToProperties(readParam.Data);
            }
            else
            {
                // 控制器不支持回读（Epson）/未连接：用安全默认值
                ApplyParamToProperties(new MotionParam
                {
                    Speed = 50f, Accel = 50f, Decel = 50f, Lspeed = 50f,
                    Unit = 50f, Sramp = 50f, CreepSpeed = 10f
                });
            }

            var readLimit = SelectedMotionDevice.GetSoftLimits(SelectedAxis.AxisIndex);
            if (readLimit.Success && readLimit.Data != null && readLimit.Data.Length >= 2)
            {
                PositiveLimit = readLimit.Data[0];
                NegativeLimit = readLimit.Data[1];
            }
            else
            {
                PositiveLimit = 99999f;
                NegativeLimit = -99999f;
            }
        }

        private void PollAxisStatus()
        {
            if (!IsConnected || SelectedMotionDevice == null || SelectedAxis == null) return;

            int axis = SelectedAxis.AxisIndex;

            // 读取 DPOS 与 MPOS
            var dposRes = SelectedMotionDevice.GetCommandPosition(axis);
            if (dposRes.Success) CmdPos = dposRes.Data;

            var mposRes = SelectedMotionDevice.GetFeedbackPosition(axis);
            if (mposRes.Success) FeedbackPos = mposRes.Data;

            var speedRes = SelectedMotionDevice.GetCurrentSpeed(axis);
            if (speedRes.Success) CurrentSpeed = speedRes.Data;

            // 读取轴硬件状态与 Limit 标志
            var statusRes = SelectedMotionDevice.GetAxisStatus(axis);
            if (statusRes.Success)
            {
                var flags = statusRes.Data;
                IsFwdLimit = flags.HasFlag(AxisStatusFlags.FwdLimit);
                IsRevLimit = flags.HasFlag(AxisStatusFlags.RevLimit);
                IsAlarm = flags.HasFlag(AxisStatusFlags.Alarm);
                IsHomeSignal = flags.HasFlag(AxisStatusFlags.HomeSwitch);
            }

            // 读取轴空闲状态
            var idleRes = SelectedMotionDevice.IsAxisIdle(axis);
            if (idleRes.Success) IsMoving = !idleRes.Data;
        }

        private void OnDeviceStateChanged(object sender, DeviceState state)
        {
            Application.Current?.Dispatcher.Invoke(() => IsConnected = state == DeviceState.Connected);
        }

        private void RefreshCommandCanExecute()
        {
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleServoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyParamCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ZeroPositionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (HomeAxisCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DirectionMoveCommand as RelayCommand<string>)?.RaiseCanExecuteChanged();
            (StopAxisCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EmergencyStopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}