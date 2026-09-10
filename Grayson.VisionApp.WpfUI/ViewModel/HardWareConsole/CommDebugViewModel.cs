using Grayson.Vision.Contracts.Communication;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{
    public class CommDebugViewModel : ViewModelBase
    {
        private readonly IDevicePool _devicePool;

        public ObservableCollection<IDevice> AllCommunicationDevices { get; set; } = new ObservableCollection<IDevice>();
        public ObservableCollection<CommunicationMessage> CommLogs { get; set; } = new ObservableCollection<CommunicationMessage>();

        private IDevice _selectedCommDevice;
        public IDevice SelectedCommDevice
        {
            get => _selectedCommDevice;
            set
            {
                var oldDevice = _selectedCommDevice;
                if (Set(ref _selectedCommDevice, value))
                {
                    OnSelectedDeviceChanged(oldDevice, value);
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

        public ObservableCollection<string> CommandTypeList { get; } = new ObservableCollection<string>
        {
            "ReadBool", "WriteBool", "ReadInt32", "WriteInt32", "ReadFloat", "WriteFloat", "ReadString"
        };

        private string _selectedCmdType = "ReadInt32";
        public string SelectedCmdType { get => _selectedCmdType; set => Set(ref _selectedCmdType, value); }

        private string _targetAddress = "D100";
        public string TargetAddress { get => _targetAddress; set => Set(ref _targetAddress, value); }

        private string _writeValue = "123";
        public string WriteValue { get => _writeValue; set => Set(ref _writeValue, value); }

        private bool _isActive;
        /// <summary>
        /// 表示当前通信调试视图是否处于显示/激活状态
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (Set(ref _isActive, value))
                {
                    UpdateMessageSubscription();
                }
            }
        }

        private bool _isDetached;
        /// <summary>
        /// 是否已打开独立窗口（2026-09-02）。为 true 时本 Tab 的 View 切走（Unloaded）
        /// 不再自动停日志订阅——独立窗口仍保持刷新，实现"并行操作/观看"。
        /// 由 HardwareConsoleView 在打开/关闭独立窗口时维护。
        /// </summary>
        public bool IsDetached
        {
            get => _isDetached;
            set => Set(ref _isDetached, value);
        }

        public ICommand ConnectCommand { get; private set; }
        public ICommand DisconnectCommand { get; private set; }
        public ICommand ClearCommLogCommand { get; private set; }
        public ICommand SendCustomRawCommand { get; private set; }

        public CommDebugViewModel()
        {
            _devicePool = App.StationHostRuntime?.DevicePool ?? throw new InvalidOperationException("DevicePool not initialized");
            InitCommands();
            LoadDevices();
        }

        private void InitCommands()
        {
            ConnectCommand = new RelayCommand(_ =>
            {
                if (SelectedCommDevice == null) return;
                var res = SelectedCommDevice.Connect();
                if (!res.Success)
                {
                    MessageBox.Show($"连接设备失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }, _ => SelectedCommDevice != null && !IsConnected);

            DisconnectCommand = new RelayCommand(_ =>
            {
                SelectedCommDevice?.Disconnect();
            }, _ => SelectedCommDevice != null && IsConnected);

            ClearCommLogCommand = new RelayCommand(_ => CommLogs.Clear());

            // 发送/执行指令（异步处理硬件通信）
            SendCustomRawCommand = new RelayCommand(async _ =>
            {
                if (SelectedCommDevice == null || !IsConnected) return;

                if (SelectedCommDevice is IPlc plcDevice)
                {
                    await ExecutePlcCommandAsync(plcDevice);
                }
                else
                {
                    MessageBox.Show("当前选中的设备不具备 PLC 读写能力！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, _ => IsConnected);
        }

        /// <summary>
        /// 补全的真正硬件读写逻辑处理
        /// </summary>
        private async Task ExecutePlcCommandAsync(IPlc plcDevice)
        {
            if (string.IsNullOrWhiteSpace(TargetAddress))
            {
                MessageBox.Show("请输入目标点位地址！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                switch (SelectedCmdType)
                {
                    case "ReadBool":
                        {
                            var res = await plcDevice.ReadAsync<bool>(TargetAddress);
                            if (res.Success) WriteValue = res.Data.ToString();
                            break;
                        }

                    case "WriteBool":
                        {
                            if (!bool.TryParse(WriteValue, out bool val))
                            {
                                // 兼容 1/0 转化为 bool
                                if (WriteValue == "1") val = true;
                                else if (WriteValue == "0") val = false;
                                else
                                {
                                    MessageBox.Show("请输入有效的布尔值 (true/false 或 1/0)！", "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                                    return;
                                }
                            }
                            await plcDevice.WriteAsync(TargetAddress, val);
                            break;
                        }

                    case "ReadInt32":
                        {
                            var res = await plcDevice.ReadAsync<int>(TargetAddress);
                            if (res.Success) WriteValue = res.Data.ToString();
                            break;
                        }

                    case "WriteInt32":
                        {
                            if (!int.TryParse(WriteValue, out int val))
                            {
                                MessageBox.Show("请输入有效的 32 位整数！", "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                                return;
                            }
                            await plcDevice.WriteAsync(TargetAddress, val);
                            break;
                        }

                    case "ReadFloat":
                        {
                            var res = await plcDevice.ReadAsync<float>(TargetAddress);
                            if (res.Success) WriteValue = res.Data.ToString("F3");
                            break;
                        }

                    case "WriteFloat":
                        {
                            if (!float.TryParse(WriteValue, out float val))
                            {
                                MessageBox.Show("请输入有效的浮点数！", "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                                return;
                            }
                            await plcDevice.WriteAsync(TargetAddress, val);
                            break;
                        }

                    case "ReadString":
                        {
                            // 默认读取长度 10，如需自定义可以在 UI 增加 Length 输入框
                            ushort length = 10;
                            var res = await plcDevice.ReadStringAsync(TargetAddress, length);
                            if (res.Success) WriteValue = res.Data;
                            break;
                        }

                    default:
                        MessageBox.Show($"未知的指令类型: {SelectedCmdType}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"通讯执行异常: {ex.Message}", "硬件通讯异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadDevices()
        {
            var all = _devicePool.GetAllDevices().OfType<IPlc>();
            foreach (var item in all) AllCommunicationDevices.Add(item);
            SelectedCommDevice = AllCommunicationDevices.FirstOrDefault();
            //// 如果赋初值时 _selectedCommDevice 本身已经是该对象，Set() 不会触发，需要手动补充一次绑定
            //if (SelectedCommDevice != null)
            //{
            //    OnSelectedDeviceChanged(null, SelectedCommDevice);
            //}
        }

        private void OnSelectedDeviceChanged(IDevice oldDevice, IDevice newDevice)
        {
            if (oldDevice != null)
            {
                oldDevice.StateChanged -= OnDeviceStateChanged;
                if (oldDevice is ICommunicationObservable oldObservable)
                {
                    // 切换设备时取消旧设备的日志订阅
                    oldObservable.MessageTransmitted -= OnMessageTransmitted;
                }
            }

            if (newDevice == null)
            {
                IsConnected = false;
                return;
            }

            newDevice.StateChanged += OnDeviceStateChanged;

            // 状态更新与日志订阅管理
            IsConnected = newDevice.State == DeviceState.Connected;
            UpdateMessageSubscription();
            RefreshCommandCanExecute();
        }

        /// <summary>
        /// 根据页面激活状态和选中的设备，动态挂载/卸载日志订阅
        /// </summary>
        private void UpdateMessageSubscription()
        {
            if (SelectedCommDevice is ICommunicationObservable observable)
            {
                // 先统一解绑，防止重复订阅
                observable.MessageTransmitted -= OnMessageTransmitted;

                // 只有在【通信界面处于打开/激活状态】时才挂载日志监听
                if (IsActive)
                {
                    observable.MessageTransmitted += OnMessageTransmitted;
                }
            }
        }

        private void OnDeviceStateChanged(object sender, DeviceState state)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsConnected = state == DeviceState.Connected;
            });
        }

        private void OnMessageTransmitted(object sender, CommunicationMessage e)
        {
            if (IsActive)
            {
                AppendLog(e.Direction, e.Content, e.IsSuccess, e.Remark);
            }
            
        }

        private void AppendLog(string direction, string content, bool isSuccess, string remark)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (CommLogs.Count >= 200) CommLogs.RemoveAt(0);
                CommLogs.Add(new CommunicationMessage
                {
                    Timestamp = DateTime.Now,
                    Direction = direction,
                    Content = content,
                    IsSuccess = isSuccess,
                    Remark = remark
                });
            });
        }

        private void RefreshCommandCanExecute()
        {
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SendCustomRawCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}