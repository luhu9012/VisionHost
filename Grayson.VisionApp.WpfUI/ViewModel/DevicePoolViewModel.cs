using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service; // 引入统一服务层
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class DeviceItemViewModel : ViewModelBase
    {
        public IDevice Model { get; }

        public string DeviceKey
        {
            get => Model.DeviceKey;
            set
            {
                Model.DeviceKey = value;
                OnPropertyChanged(nameof(DeviceKey));
            }
        }

        public string DeviceId => Model.DeviceId;
        public string BrandName => Model.BrandName;
        public DeviceCategory Category => Model.Category;

        // 1. 变成带通知的 Field 属性
        private DeviceState _state;
        public DeviceState State
        {
            get => _state;
            private set
            {
                if (Set(ref _state, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(IsConnected)); // UI 绑定 IsConnected 也能联动
                }
            }
        }

        public bool IsConnected => State == DeviceState.Connected;

        public string StatusText => GetStatusText();

        private string GetStatusText()
        {
            switch (State)
            {
                case DeviceState.Connected: return "已连接";
                case DeviceState.Disconnected: return "已断开";
                default: return "未知";
            }
        }

        public string CategoryIcon
        {
            get
            {
                switch (Category)
                {
                    case DeviceCategory.Camera:
                        return "📷";
                    case DeviceCategory.PLC:
                        return "📟";
                    case DeviceCategory.MotionCard:
                        return "⚙️";
                    case DeviceCategory.LightController:
                        return "💡";
                    default:
                        return "🔌";
                }
            }
        }

        // 绑定真实 IDevice 的通用参数设置字典/属性
        public string IpAddress
        {
            get => Model.GetParam("IP")?.Data?.ToString() ?? string.Empty;
            set => Model.SetParam("IP", value);
        }

        public string Port
        {
            get => Model.GetParam("Port")?.Data?.ToString() ?? string.Empty;
            set => Model.SetParam("Port", value);
        }

        public string ExposureTime
        {
            get => Model.GetParam("Exposure")?.Data?.ToString() ?? string.Empty;
            set => Model.SetParam("Exposure", value);
        }

        public DeviceItemViewModel(IDevice device)
        {
            Model = device ?? throw new ArgumentNullException(nameof(device));
            _state = device.State;

            // 2. 自动监听底层设备的真实状态变化！
            Model.StateChanged += (s, newState) =>
            {
                // 切回 UI 线程同步
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    State = newState;
                });
            };
        }
    }

    public class DevicePoolViewModel : ViewModelBase
    {
        public ObservableCollection<DeviceItemViewModel> Devices { get; } = new ObservableCollection<DeviceItemViewModel>();

        private DeviceItemViewModel _selectedDevice;
        public DeviceItemViewModel SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (Set(ref _selectedDevice, value))
                {
                    OnSelectedDeviceChanged(value);
                }
            }
        }

        private void OnSelectedDeviceChanged(DeviceItemViewModel newDevice)
        {
            if (newDevice != null)
            {
                newDevice.PropertyChanged += OnSelectedDevicePropertyChanged;
            }

            // 切换选中设备后，立即刷新连接/断开按钮的可用状态
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnSelectedDevicePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // 只要底层设备状态变化（State/IsConnected）都刷新命令
            if (e.PropertyName == nameof(DeviceItemViewModel.State) ||
                e.PropertyName == nameof(DeviceItemViewModel.IsConnected))
            {
                (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public ICommand RefreshCommand { get; }      // 刷新列表（同步单例容器）
        public ICommand ScanHardwareCommand { get; } // 扫描物理总线硬件
        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand AddDeviceCommand { get; }
        public ICommand DeleteDeviceCommand { get; }

        public DevicePoolViewModel()
        {
            RefreshCommand = new RelayCommand(_ => LoadFromManager());
            ScanHardwareCommand = new RelayCommand(_ => OnScanPhysicalHardware());
            ConnectCommand = new RelayCommand(_ => OnConnect(), _ => SelectedDevice != null && SelectedDevice.State != DeviceState.Connected);
            DisconnectCommand = new RelayCommand(_ => OnDisconnect(), _ => SelectedDevice != null && SelectedDevice.State == DeviceState.Connected);
            AddDeviceCommand = new RelayCommand(_ => OnScanPhysicalHardware());
            DeleteDeviceCommand = new RelayCommand(_ => OnDeleteDevice(), _ => SelectedDevice != null);

            // 首次打开界面，从 DevicePoolManager 中加载现有配置设备
            LoadFromManager();
        }

        /// <summary>
        /// 从全局 DevicePoolManager 加载数据源
        /// </summary>
        public void LoadFromManager()
        {
            Devices.Clear();
            var poolDevices = DevicePoolManager.Instance.GetAllDevices();
            foreach (var dev in poolDevices)
            {
                Devices.Add(new DeviceItemViewModel(dev));
            }
            SelectedDevice = Devices.FirstOrDefault();
        }

        /// <summary>
        /// 扫描物理总线，自动实例化未添加的驱动设备
        /// </summary>
        public void OnScanPhysicalHardware(bool silent=false)
        {
            var scannedInfos = DevicePoolManager.Instance.ScanAllPhysicalDevices();

            if (!scannedInfos.Any() && !silent)
            {
                MessageBox.Show("未扫描到任何在线的物理硬件设备（相机/PLC/运动卡）！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int addedCount = 0;
            foreach (var info in scannedInfos)
            {
                string autoKey = $"{info.BrandName}_{info.Category}_{info.DeviceId}";

                // 判断是否已在设备池中
                if (DevicePoolManager.Instance.GetDevice<IDevice>(autoKey) == null)
                {
                    try
                    {
                        var newDev = DevicePoolManager.Instance.AddDeviceToPool(info.BrandName, info.Category, info.DeviceId, autoKey);
                        var vm = new DeviceItemViewModel(newDev);
                        Devices.Add(vm);
                        addedCount++;
                    }
                    catch (Exception ex)
                    {
                        if (!silent)
                        {
                            MessageBox.Show($"创建设备 [{info.ModelName}] 失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                       
                    }
                }
            }

            if (addedCount > 0)
            {
                SelectedDevice = Devices.LastOrDefault();
                if (!silent)
                {
                    MessageBox.Show($"成功新增 {addedCount} 个在线物理设备！", "扫描成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                
            }
            else
            {
                if (!silent)
                {
                    MessageBox.Show("所有扫描到的物理设备已存在于设备池中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
               
            }
        }

        private void OnConnect()
        {
            if (SelectedDevice == null) return;

            var res = SelectedDevice.Model.Connect();
            // 显式更新 ViewModel 状态（内部会触发 StatusText 的 OnPropertyChanged）
            //SelectedDevice.State = SelectedDevice.Model.State;

            // 关键：强制 WPF 命令系统刷新 ConnectCommand / DisconnectCommand 的 CanExecute 状态
            CommandManager.InvalidateRequerySuggested();

            if (!res.Success)
            {
                MessageBox.Show($"连接设备失败: {res.Message}", "通信错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnDisconnect()
        {
            if (SelectedDevice == null) return;

            SelectedDevice.Model.Disconnect();
            //SelectedDevice.State = SelectedDevice.Model.State;

            // 强制刷新按钮可用状态
            CommandManager.InvalidateRequerySuggested();
        }

        private void OnDeleteDevice()
        {
            if (SelectedDevice == null) return;

            if (MessageBox.Show($"确定要移除设备 '{SelectedDevice.DeviceKey}' 吗?", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                string key = SelectedDevice.DeviceKey;
                if (DevicePoolManager.Instance.RemoveDevice(key))
                {
                    Devices.Remove(SelectedDevice);
                    SelectedDevice = Devices.FirstOrDefault();
                }
            }
        }
    }
}