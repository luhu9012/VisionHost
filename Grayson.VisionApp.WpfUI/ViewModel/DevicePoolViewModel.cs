using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service; // 引入统一服务层
using Grayson.Vision.WpfUI.View;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;


namespace Grayson.Vision.WpfUI.ViewModel
{
    public class DeviceItemViewModel : ViewModelBase
    {
        public IDevice Model { get; }

        /// <summary> 逻辑 Key（如 Cam_Top_01） </summary>
        public string DeviceKey
        {
            get => Model.DeviceKey;
            set
            {
                if (Model.DeviceKey != value)
                {
                    Model.DeviceKey = value;
                    OnPropertyChanged(nameof(DeviceKey));
                }
            }
        }

        /// <summary> 物理 SN 或设备路径 </summary>
        public string DeviceId => Model.DeviceId;
        public string BrandName => Model.BrandName;
        public DeviceCategory Category => Model.Category;

        /// <summary> 通用连接字符串（如 192.168.1.10:102 或 COM1,9600,N,8,1） </summary>
        public string ConnectionString
        {
            get => Model.GetParam("ConnectionString")?.Data?.ToString() ?? string.Empty;
            set
            {
                Model.SetParam("ConnectionString", value);
                OnPropertyChanged(nameof(ConnectionString));
            }
        }

        private DeviceState _state;
        public DeviceState State
        {
            get => _state;
            private set
            {
                if (Set(ref _state, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(IsConnected));
                }
            }
        }

        public bool IsConnected => State == DeviceState.Connected;

        public string StatusText => State == DeviceState.Connected ? "已连接" : "已断开";

        public string CategoryIcon
        {
            get
            {
                string icon;
                switch (Category)
                {
                    case DeviceCategory.Camera: icon = "📷"; break;
                    case DeviceCategory.PLC: icon = "📟"; break;
                    case DeviceCategory.MotionCard: icon = "⚙️"; break;
                    case DeviceCategory.LightController: icon = "💡"; break;
                    default: icon = "🔌"; break;
                }
                return icon;
            }
        }

        public DeviceItemViewModel(IDevice device)
        {
            Model = device ?? throw new ArgumentNullException(nameof(device));
            _state = device.State;

            // 监听底层状态回调，自动切回 UI 线程更新状态
            Model.StateChanged += (s, newState) =>
            {
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
            set => Set(ref _selectedDevice, value);
        }

        // 命令声明
        public ICommand RefreshCommand { get; }
        public ICommand ManualAddDeviceCommand { get; } // 🆕 手动添加
        public ICommand ScanHardwareCommand { get; }    // 🆕 扫描弹窗确认
        public ICommand SaveConfigCommand { get; }
        public ICommand DeleteDeviceCommand { get; }
        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }

        public DevicePoolViewModel()
        {
            RefreshCommand = new RelayCommand(_ => LoadFromManager());
            ManualAddDeviceCommand = new RelayCommand(_ => OnManualAddDevice());
            ScanHardwareCommand = new RelayCommand(async _ => await OnScanHardwareAsync());
            SaveConfigCommand = new RelayCommand(async _ => await OnSaveConfigAsync(), _ => SelectedDevice != null);
            DeleteDeviceCommand = new RelayCommand(_ => OnDeleteDevice(), _ => SelectedDevice != null);

            ConnectCommand = new RelayCommand(_ => OnConnect(), _ => SelectedDevice != null && !SelectedDevice.IsConnected);
            DisconnectCommand = new RelayCommand(_ => OnDisconnect(), _ => SelectedDevice != null && SelectedDevice.IsConnected);

            LoadFromManager();
        }

        /// <summary>
        /// 手动添加设备（PLC、串口卡等不支持枚举的设备）
        /// </summary>
        private async void OnManualAddDevice()
        {
            var dialogVm = new AddDeviceDialogViewModel();
            var dialog = new AddDeviceDialog { DataContext = dialogVm, Owner = Application.Current.MainWindow };

            if (dialog.ShowDialog() == true)
            {
                // 手动配置实例化存入底层
                var res = await DevicePoolManager.Instance.CreateAndSaveManualDeviceAsync(
                    dialogVm.SelectedCategory,
                    dialogVm.SelectedBrand,
                    dialogVm.DeviceKey,
                    dialogVm.ConnectionString);

                if (res.Success)
                {
                    LoadFromManager();
                    SelectedDevice = Devices.FirstOrDefault(d => d.DeviceKey == dialogVm.DeviceKey);
                }
                else
                {
                    MessageBox.Show($"添加失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 扫描在线硬件并在弹窗中让工程师二次确认导入
        /// </summary>
        private async Task OnScanHardwareAsync()
        {
            // 1. 执行扫描
            var scannedInfos = await Task.Run(() => DevicePoolManager.Instance.ScanAllPhysicalDevices());

            if (!scannedInfos.Any())
            {
                MessageBox.Show("未扫描到任何在线的物理硬件设备！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 2. 弹出选择确认窗口
            var dialogVm = new ScanDeviceDialogViewModel(scannedInfos);
            var dialog = new ScanDeviceDialog { DataContext = dialogVm, Owner = Application.Current.MainWindow };

            if (dialog.ShowDialog() == true)
            {
                // 3. 拿到用户勾选并指定的 DeviceKey 列表进行批量入库
                int addedCount = 0;
                foreach (var item in dialogVm.ScannedDevices.Where(x => x.IsSelected))
                {
                    var res = await DevicePoolManager.Instance.AddDeviceToPoolAndSaveAsync(item.RawInfo, item.TargetDeviceKey);
                    if (res.Success)
                    {
                        addedCount++;
                    }
                    else
                    {
                        MessageBox.Show($"设备 [{item.TargetDeviceKey}] 导入失败: {res.Message}", "导入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                if (addedCount > 0)
                {
                    // 4. 重新从底层 Manager 载入最新设备列表并刷 UI
                    LoadFromManager();
                    MessageBox.Show($"成功添加并保存了 {addedCount} 个设备！", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        public void LoadFromManager()
        {
            Devices.Clear();
            foreach (var dev in DevicePoolManager.Instance.GetAllDevices())
            {
                Devices.Add(new DeviceItemViewModel(dev));
            }
            SelectedDevice = Devices.FirstOrDefault();
        }

        private async Task OnSaveConfigAsync()
        {
            if (SelectedDevice == null) return;
            var res = await DevicePoolManager.Instance.UpdateDeviceMappingAsync(SelectedDevice.Model);
            if (res.Success)
                MessageBox.Show("设备配置更新成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show($"保存失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnConnect() => SelectedDevice?.Model.Connect();
        private void OnDisconnect() => SelectedDevice?.Model.Disconnect();

        private void OnDeleteDevice()
        {
            if (SelectedDevice != null && MessageBox.Show($"确定要移除设备 '{SelectedDevice.DeviceKey}' 吗?", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                if (DevicePoolManager.Instance.RemoveDevice(SelectedDevice.DeviceKey))
                {
                    Devices.Remove(SelectedDevice);
                    SelectedDevice = Devices.FirstOrDefault();
                }
            }
        }
    }
}