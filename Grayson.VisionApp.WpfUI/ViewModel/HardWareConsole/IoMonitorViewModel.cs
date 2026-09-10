using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Models;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{
    public class IoMonitorViewModel : ViewModelBase
    {
        private readonly IDevicePool _devicePool;
        private CancellationTokenSource _pollingCts;
        private readonly object _ioLock = new object();

        #region UI 绑定属性

        public ObservableCollection<IDevice> IoDeviceList { get; set; } = new ObservableCollection<IDevice>();
        public ObservableCollection<IoPointModel> InputIOList { get; set; } = new ObservableCollection<IoPointModel>();
        public ObservableCollection<IoPointModel> OutputIOList { get; set; } = new ObservableCollection<IoPointModel>();

        private IDevice _selectedIoDevice;
        public IDevice SelectedIoDevice
        {
            get => _selectedIoDevice;
            set
            {
                if (_selectedIoDevice != value)
                {
                    if (_selectedIoDevice != null)
                    {
                        _selectedIoDevice.StateChanged -= OnDeviceStateChanged;
                    }

                    if (Set(ref _selectedIoDevice, value))
                    {
                        if (_selectedIoDevice != null)
                        {
                            _selectedIoDevice.StateChanged += OnDeviceStateChanged;
                        }
                        UpdateConnectionState();
                        RebuildIoPointList();
                    }
                }
            }
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set => Set(ref _isConnected, value);
        }

        private string _connectionStatusText = "未连接";
        public string ConnectionStatusText
        {
            get => _connectionStatusText;
            set => Set(ref _connectionStatusText, value);
        }
        private bool _isActive;
        /// <summary>
        /// 表示当前 IO 监视视图是否处于显示/激活状态
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (Set(ref _isActive, value))
                {
                    UpdatePollingState();
                }
            }
        }

        private bool _isDetached;
        /// <summary>
        /// 是否已打开独立窗口（2026-09-02）。为 true 时本 Tab 的 View 切走（Unloaded）
        /// 不再自动停轮询——独立窗口仍保持实时刷新，实现"并行操作/观看"。
        /// 由 HardwareConsoleView 在打开/关闭独立窗口时维护。
        /// </summary>
        public bool IsDetached
        {
            get => _isDetached;
            set => Set(ref _isDetached, value);
        }


        #endregion
        #region 命令定义

        public RelayCommand ConnectCommand { get; }
        public RelayCommand DisconnectCommand { get; }
        public RelayCommand<IoPointModel> ToggleOutputCommand { get; }

        #endregion

        public IoMonitorViewModel()
        {
            _devicePool = App.StationHostRuntime?.DevicePool ?? throw new InvalidOperationException("DevicePool not initialized");
            ConnectCommand = new RelayCommand(async () => await ConnectAsync(), () => SelectedIoDevice != null && !IsConnected);
            DisconnectCommand = new RelayCommand(async () => await DisconnectAsync(), () => SelectedIoDevice != null && IsConnected);
            ToggleOutputCommand = new RelayCommand<IoPointModel>(async (ioPoint) => await ExecuteToggleOutputAsync(ioPoint));

            LoadIoDevices();
        }

        /// <summary>
        /// VM 释放或 Tab 隐藏时调用：停止轮询并释放所有事件订阅。
        /// </summary>
        public void Cleanup()
        {
            StopIoPolling();
            if (SelectedIoDevice != null)
            {
                SelectedIoDevice.StateChanged -= OnDeviceStateChanged;
            }
        }

        private void UpdateConnectionState()
        {
            if (SelectedIoDevice == null)
            {
                IsConnected = false;
                ConnectionStatusText = "未选择设备";
                StopIoPolling();

                ConnectCommand?.RaiseCanExecuteChanged();
                DisconnectCommand?.RaiseCanExecuteChanged();
                return;
            }

            IsConnected = SelectedIoDevice.State == DeviceState.Connected;
            ConnectionStatusText = IsConnected ? "已连接" : "已断开";

            // 根据连接与激活状态决定轮询
            UpdatePollingState();

            ConnectCommand?.RaiseCanExecuteChanged();
            DisconnectCommand?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 仅当【设备已连接】且【Tab处于激活/可见状态】时才启动轮询，否则立即停止
        /// </summary>
        private void UpdatePollingState()
        {
            if (IsConnected && IsActive)
            {
                StartIoPolling();
            }
            else
            {
                StopIoPolling();
            }
        }

        /// <summary>
        /// 从全局设备池装载 Camera、MotionCard、PLC 设备。
        /// ⚠ 排除 Epson 机械手：它的 IO 是专用真空阀语义（业务 0/1 → SPEL+ OUT15/14，
        /// 映射在 EpsonIoMap），通用轮询会按 0/1/2... 读不存在的输入口（IN? 1/2），
        /// 触发 SPEL+ 2345 IO 越界错误并崩掉 mainTCP 脚本（实测 2026-09-02）。
        /// Epson 的真空阀请在「🤖 机械臂调试」Tab 操作。
        /// </summary>
        public void LoadIoDevices()
        {
            IoDeviceList.Clear();
            var devices = _devicePool.GetAllDevices()
                .Where(d => (d.Category == DeviceCategory.Camera ||
                             d.Category == DeviceCategory.MotionCard ||
                             d.Category == DeviceCategory.Generic ||
                             d.Category == DeviceCategory.PLC) &&
                            !(d.BrandName != null &&
                              d.BrandName.IndexOf("Epson", StringComparison.OrdinalIgnoreCase) >= 0));

            foreach (var dev in devices)
            {
                IoDeviceList.Add(dev);
            }

            if (IoDeviceList.Count > 0)
            {
                SelectedIoDevice = IoDeviceList[0];
            }
        }

        private void OnDeviceStateChanged(object sender, DeviceState state)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                UpdateConnectionState();
            });
        }
        private async Task ConnectAsync()
        {
            if (SelectedIoDevice == null) return;

            await Task.Run(() =>
            {
                var res = SelectedIoDevice.Connect();
                if (!res.Success)
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"设备连接失败: {res.Message}", "错误提示", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            });

            UpdateConnectionState();
        }

        private async Task DisconnectAsync()
        {
            if (SelectedIoDevice == null) return;

            StopIoPolling();
            await Task.Run(() =>
            {
                SelectedIoDevice.Disconnect();
            });

            UpdateConnectionState();
        }



        #region 不同设备类型的 IO 点位列表构建

        private void RebuildIoPointList()
        {
            InputIOList.Clear();
            OutputIOList.Clear();

            if (SelectedIoDevice == null) return;

            switch (SelectedIoDevice.Category)
            {
                case DeviceCategory.Camera:
                    // 工业相机硬件 GPIO 点位
                    InputIOList.Add(new IoPointModel { ChannelIndex = 0, Name = "Line0 (OptoIn 硬触发)", Address = "Line0", IsActive = false });
                    InputIOList.Add(new IoPointModel { ChannelIndex = 1, Name = "Line1 (GPIO 通用输入)", Address = "Line1", IsActive = false });

                    OutputIOList.Add(new IoPointModel { ChannelIndex = 0, Name = "Line2 (Strobe 频闪控制)", Address = "Line2", IsActive = false });
                    OutputIOList.Add(new IoPointModel { ChannelIndex = 1, Name = "Line3 (UserOutput 通用输出)", Address = "Line3", IsActive = false });
                    break;

                case DeviceCategory.MotionCard:
                    // 运动控制卡通用板卡 IO 点位
                     for (int i = 0; i < 16; i++)
                    {
                        InputIOList.Add(new IoPointModel { ChannelIndex = i, Name = $"IN{i:D2} (传感器/原点/限位_{i + 1})", Address = i.ToString(), IsActive = false });
                        OutputIOList.Add(new IoPointModel { ChannelIndex = i, Name = $"OUT{i:D2} (电磁阀/使能_{i + 1})", Address = i.ToString(), IsActive = false });
                    }
                    break;

                //case DeviceCategory.PLC:
                //case DeviceCategory.Generic:
                //    // PLC 逻辑交互与握手点位 (默认以 Modbus/S7 Bit 地址示范)
                //    InputIOList.Add(new IoPointModel { ChannelIndex = 0, Name = "M100 (PLC_Ready 就绪)", Address = "M100", IsActive = false });
                //    InputIOList.Add(new IoPointModel { ChannelIndex = 1, Name = "M101 (Vision_Trigger 拍照请求)", Address = "M101", IsActive = false });
                //    InputIOList.Add(new IoPointModel { ChannelIndex = 2, Name = "M102 (System_Reset 系统复位)", Address = "M102", IsActive = false });

                //    OutputIOList.Add(new IoPointModel { ChannelIndex = 0, Name = "M200 (Vision_Ready 视觉就绪)", Address = "M200", IsActive = false });
                //    OutputIOList.Add(new IoPointModel { ChannelIndex = 1, Name = "M201 (Vision_Busy 视觉运行中)", Address = "M201", IsActive = false });
                //    OutputIOList.Add(new IoPointModel { ChannelIndex = 2, Name = "M202 (Inspect_OK 判定合格)", Address = "M202", IsActive = false });
                //    OutputIOList.Add(new IoPointModel { ChannelIndex = 3, Name = "M203 (Inspect_NG 判定不合格)", Address = "M203", IsActive = false });
                //    break;
                case DeviceCategory.Generic:
                case DeviceCategory.PLC: // 如果 Generic / UniversalPlc 也有固定数字量通道 (0~7)
                    for (int i = 0; i < 8; i++)
                    {
                        InputIOList.Add(new IoPointModel { ChannelIndex = i, Name = $"DI_{i:D2} (输入通道 {i})", Address = i.ToString(), IsActive = false });
                        OutputIOList.Add(new IoPointModel { ChannelIndex = i, Name = $"DO_{i:D2} (输出通道 {i})", Address = i.ToString(), IsActive = false });
                    }
                    break;
            }
        }

        #endregion

        #region 实时状态轮询与强制 DO 输出控制

        private void StartIoPolling()
        {
            StopIoPolling();
            _pollingCts = new CancellationTokenSource();
            var token = _pollingCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && IsConnected)
                {
                    try
                    {
                        PollIoStates();
                        await Task.Delay(50, token); // 100ms 刷新频率
                    }
                    catch (TaskCanceledException) { break; }
                    catch (Exception) { /* 忽略读取异常 */ }
                }
            }, token);
        }

        private void StopIoPolling()
        {

            _pollingCts?.Cancel();
            _pollingCts?.Dispose();
            _pollingCts = null;
        }

        private void PollIoStates()
        {
            if (SelectedIoDevice == null || SelectedIoDevice.State != DeviceState.Connected) return;

            // 1. 优先使用统一的 IIoDevice 抽象接口 (适用于 HikCamera、MotionCard、UniversalPlcDevice)
            if (SelectedIoDevice is IIoDevice ioDevice)
            {
                foreach (var point in InputIOList)
                {
                    var res = ioDevice.ReadDi(point.ChannelIndex);
                    if (res.Success) point.IsActive = res.Data;
                }
                foreach (var point in OutputIOList)
                {
                    var res = ioDevice.ReadDo(point.ChannelIndex);
                    if (res.Success) point.IsActive = res.Data;
                }
            }
            // 2. 传统卡片/PLC 的备用通道 (如使用字符串 Address 点位的逻辑交互)
            else if (SelectedIoDevice is IMotionCard motionCard)
            {
                foreach (var point in InputIOList)
                {
                    var res = motionCard.GetInput(point.ChannelIndex);
                    if (res.Success) point.IsActive = res.Data;
                }
                foreach (var point in OutputIOList)
                {
                    var res = motionCard.GetOutput(point.ChannelIndex);
                    if (res.Success) point.IsActive = res.Data;
                }
            }
            else if (SelectedIoDevice is IPlc plc)
            {
                foreach (var point in InputIOList)
                {
                    var res = plc.ReadBit(point.Address);
                    if (res.Success) point.IsActive = res.Data;
                }
                foreach (var point in OutputIOList)
                {
                    var res = plc.ReadBit(point.Address);
                    if (res.Success) point.IsActive = res.Data;
                }
            }
        }
        private async Task ExecuteToggleOutputAsync(IoPointModel ioPoint)
        {
            if (ioPoint == null || SelectedIoDevice == null || !IsConnected) return;

            // 当前未勾选则目标设为 true，已勾选则目标设为 false
            bool targetState = !ioPoint.IsActive;

            await Task.Run(() =>
            {
                Result res = Result.Fail("设备不支持数字量输出控制");

                if (SelectedIoDevice is IIoDevice ioDevice)
                {
                    res = ioDevice.WriteDo(ioPoint.ChannelIndex, targetState);
                }
                else if (SelectedIoDevice is IMotionCard motionCard)
                {
                    res = motionCard.SetOutput(ioPoint.ChannelIndex, targetState);
                }
                else if (SelectedIoDevice is IPlc plc)
                {
                    res = plc.WriteBit(ioPoint.Address, targetState);
                }

                if (res.Success)
                {
                    // 写入成功后由后台轮询或此处手动更新 UI 状态
                    ioPoint.IsActive = targetState;
                }
                else
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"写入 DO 状态失败: {res.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
            });
        }
        #endregion
    }
}