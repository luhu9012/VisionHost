using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Service;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{
    public class AxisControlViewModel : ViewModelBase
    {
        #region 属性定义

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

        private double _jogStep = 1.0;
        public double JogStep
        {
            get => _jogStep;
            set => Set(ref _jogStep, value);
        }

        private double _axisSpeed = 10.0;
        public double AxisSpeed
        {
            get => _axisSpeed;
            set => Set(ref _axisSpeed, value);
        }

        private double _axisXPos;
        public double AxisXPos { get => _axisXPos; set => Set(ref _axisXPos, value); }

        private double _axisYPos;
        public double AxisYPos { get => _axisYPos; set => Set(ref _axisYPos, value); }

        private double _axisZPos;
        public double AxisZPos { get => _axisZPos; set => Set(ref _axisZPos, value); }

        #endregion

        #region 命令定义

        public ICommand RefreshDevicesCommand { get; private set; }
        public ICommand ConnectCommand { get; private set; }
        public ICommand DisconnectCommand { get; private set; }
        public ICommand ToggleServoCommand { get; private set; }
        public ICommand HomeAxisCommand { get; private set; }
        public ICommand JogCommand { get; private set; }
        public ICommand StopAxisCommand { get; private set; }
        public ICommand EmergencyStopCommand { get; private set; }

        #endregion

        public AxisControlViewModel()
        {
            InitCommands();
            LoadMotionDevices();
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
                if (res.Success)
                {
                    IsServoOn = nextState;
                }
                else
                {
                    MessageBox.Show($"设置使能失败: {res.Message}", "硬件通信失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, _ => SelectedMotionDevice != null && IsConnected && SelectedAxis != null);

            HomeAxisCommand = new RelayCommand(_ =>
            {
                if (SelectedMotionDevice == null || SelectedAxis == null || !IsConnected) return;

                var res = SelectedMotionDevice.Home(SelectedAxis.AxisIndex, 0);
                if (!res.Success)
                {
                    MessageBox.Show($"触发回零失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }, _ => SelectedMotionDevice != null && IsConnected && SelectedAxis != null);

            JogCommand = new RelayCommand<string>(param =>
            {
                if (SelectedMotionDevice == null || SelectedAxis == null || !IsConnected) return;

                // 下发运动参数
                var paramRes = SelectedMotionDevice.SetMotionParam(SelectedAxis.AxisIndex, new MotionParam { Speed = (float)AxisSpeed });

                int direction = (param != null && param.EndsWith("+")) ? 1 : -1;
                var jogRes = SelectedMotionDevice.JogMove(SelectedAxis.AxisIndex, direction);
                if (!jogRes.Success)
                {
                    MessageBox.Show($"启动 JOG 点动失败: {jogRes.Message}", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var devices = DevicePoolManager.Instance.GetAllDevices().OfType<IMotionCard>();
            foreach (var card in devices)
            {
                MotionDeviceList.Add(card);
            }

            SelectedMotionDevice = MotionDeviceList.FirstOrDefault();
        }

        private void OnSelectedDeviceChanged(IMotionCard oldDevice, IMotionCard newDevice)
        {
            if (oldDevice != null)
            {
                oldDevice.StateChanged -= OnDeviceStateChanged;
            }

            AxisList.Clear();

            if (newDevice == null)
            {
                IsConnected = false;
                return;
            }

            newDevice.StateChanged += OnDeviceStateChanged;
            IsConnected = newDevice.State == DeviceState.Connected;

            // 装载选中板卡的操控轴列表
            LoadAxesForDevice(newDevice);
            RefreshCommandCanExecute();
        }

        private void LoadAxesForDevice(IMotionCard device)
        {
            AxisList.Clear();
            if (device == null) return;

            // 预设通用轴索引，后续可根据硬件属性扩展
            AxisList.Add(new AxisInfoModel { AxisIndex = 0, AxisName = "0号轴 (X轴)" });
            AxisList.Add(new AxisInfoModel { AxisIndex = 1, AxisName = "1号轴 (Y轴)" });
            AxisList.Add(new AxisInfoModel { AxisIndex = 2, AxisName = "2号轴 (Z轴)" });

            SelectedAxis = AxisList.FirstOrDefault();
        }

        private void OnDeviceStateChanged(object sender, DeviceState state)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsConnected = state == DeviceState.Connected;
            });
        }

        private void RefreshCommandCanExecute()
        {
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleServoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (HomeAxisCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (JogCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StopAxisCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EmergencyStopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}