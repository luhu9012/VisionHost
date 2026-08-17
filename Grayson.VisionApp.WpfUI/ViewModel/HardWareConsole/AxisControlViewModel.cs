using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Models;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;
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

        private MotionParam _currentMotionParam = new MotionParam();
        public MotionParam CurrentMotionParam
        {
            get => _currentMotionParam;
            set => Set(ref _currentMotionParam, value);
        }

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

                var resParam = SelectedMotionDevice.SetMotionParam(SelectedAxis.AxisIndex, CurrentMotionParam);
                var resLimit = SelectedMotionDevice.SetSoftLimits(SelectedAxis.AxisIndex, PositiveLimit, NegativeLimit);

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

                // 运动前确保参数生效
                SelectedMotionDevice.SetMotionParam(SelectedAxis.AxisIndex, CurrentMotionParam);

                int direction = (dir != null && dir.Contains("+")) ? 1 : -1;

                if (MoveModeIndex == 0) // JOG 点动
                {
                    SelectedMotionDevice.JogMove(SelectedAxis.AxisIndex, direction);
                }
                else if (MoveModeIndex == 1) // 相对移动
                {
                    float dist = TargetDistance * direction;
                    SelectedMotionDevice.MoveRelative(SelectedAxis.AxisIndex, dist, CurrentMotionParam.Speed);
                }
                else if (MoveModeIndex == 2) // 绝对定位
                {
                    SelectedMotionDevice.MoveAbsolute(SelectedAxis.AxisIndex, TargetDistance, CurrentMotionParam.Speed);
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

           
            AxisList.Add(new AxisInfoModel { AxisIndex = 1, AxisName = "1号轴 (X轴)" });
            AxisList.Add(new AxisInfoModel { AxisIndex = 2, AxisName = "2号轴 (Y1轴)" });
            AxisList.Add(new AxisInfoModel { AxisIndex = 3, AxisName = "3号轴 (Y2轴)" });
            AxisList.Add(new AxisInfoModel { AxisIndex = 0, AxisName = "0号轴 (Z轴)" });

            SelectedAxis = AxisList.FirstOrDefault();
        }

        private void LoadSelectedAxisParam()
        {
            // 此处可从配置文件或硬件读取当前轴参数，此处赋默认/初始值
            CurrentMotionParam = new MotionParam
            {
                Speed = 50f,
                Accel = 50f,
                Decel = 50f,
                Lspeed = 50f,
                Unit = 50f,
                Sramp = 50f,
                CreepSpeed = 10f
            };
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