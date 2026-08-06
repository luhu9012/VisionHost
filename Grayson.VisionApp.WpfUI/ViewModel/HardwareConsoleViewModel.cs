using Grayson.Vision.Contracts.Communication;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class HardwareConsoleViewModel : ViewModelBase
    {
        public HardwareConsoleViewModel()
        {
            InitCommands();
            LoadDevicesFromPool();
        }

        #region 设备池下拉与级联属性

        public ObservableCollection<ICamera> CameraDeviceList { get; set; } = new ObservableCollection<ICamera>();

        private ICamera _selectedCameraDevice;
        public ICamera SelectedCameraDevice
        {
            get => _selectedCameraDevice;
            set
            {
                var oldCamera = _selectedCameraDevice;
                if (Set(ref _selectedCameraDevice, value))
                {
                    OnCameraDeviceChanged(oldCamera, value);
                    RefreshAllCanExecute();
                }
            }
        }

        // 运动卡与 IO 设备保留声明
        public ObservableCollection<DeviceInfoModel> MotionDeviceList { get; set; } = new ObservableCollection<DeviceInfoModel>();
        public ObservableCollection<DeviceInfoModel> IoDeviceList { get; set; } = new ObservableCollection<DeviceInfoModel>();

        #endregion

        #region 调试参数属性

        private BitmapSource _cameraImageSource;
        public BitmapSource CameraImageSource
        {
            get => _cameraImageSource;
            set => Set(ref _cameraImageSource, value);
        }

        private bool _isGrabbing;
        public bool IsGrabbing
        {
            get => _isGrabbing;
            set
            {
                if (Set(ref _isGrabbing, value))
                {
                    (StartGrabCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (StopGrabCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (SaveImageCommand as RelayCommand<string>)?.RaiseCanExecuteChanged();
                }
            }
        }

        private double _exposureTime = 8000;
        public double ExposureTime
        {
            get => _exposureTime;
            set
            {
                if (Set(ref _exposureTime, value) && IsConnected && SelectedCameraDevice != null)
                {
                    SelectedCameraDevice.SetExposureTime(value);
                }
            }
        }

        private double _gainValue = 2.0;
        public double GainValue
        {
            get => _gainValue;
            set
            {
                if (Set(ref _gainValue, value) && IsConnected && SelectedCameraDevice != null)
                {
                    SelectedCameraDevice.SetGain(value);
                }
            }
        }

        private int _selectedTriggerMode = 0; // 0: 连续, 1: 软触发, 2: 硬触发 Line0
        public int SelectedTriggerMode
        {
            get => _selectedTriggerMode;
            set
            {
                if (Set(ref _selectedTriggerMode, value) && IsConnected && SelectedCameraDevice != null)
                {
                    SelectedCameraDevice.SetTriggerMode(value != 0);
                }
            }
        }

        private double _acquisitionFrameRate = 10;
        public double AcquisitionFrameRate
        {
            get => _acquisitionFrameRate;
            set
            {
                if (Set(ref _acquisitionFrameRate, value) && IsConnected && SelectedCameraDevice != null)
                {
                    var res = SelectedCameraDevice.SetParam("AcquisitionFrameRate", value);
                    if (!res.Success)
                    {
                        System.Diagnostics.Debug.WriteLine($"[HardwareConsole] 设置目标帧率失败: {res.Message}");
                    }
                }
            }
        }

        private double _resultingFrameRate;
        public double ResultingFrameRate
        {
            get => _resultingFrameRate;
            private set => Set(ref _resultingFrameRate, value);
        }

        private int _imageWidth;
        public int ImageWidth
        {
            get => _imageWidth;
            private set => Set(ref _imageWidth, value);
        }

        private int _imageHeight;
        public int ImageHeight
        {
            get => _imageHeight;
            private set => Set(ref _imageHeight, value);
        }

        private string _pixelFormat = "未知";
        public string PixelFormat
        {
            get => _pixelFormat;
            private set => Set(ref _pixelFormat, value);
        }

        #region 状态属性（带真正的 Field 存储）

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set => Set(ref _isConnected, value);
        }

        private string _deviceStatusText = "未选择";
        public string DeviceStatusText
        {
            get => _deviceStatusText;
            private set => Set(ref _deviceStatusText, value);
        }

        #endregion

        #endregion

        #region 命令声明

        public ICommand ConnectCommand { get; private set; }
        public ICommand DisconnectCommand { get; private set; }
        public ICommand SoftwareTriggerCommand { get; private set; }
        public ICommand StartGrabCommand { get; private set; }
        public ICommand StopGrabCommand { get; private set; }
        public ICommand SaveImageCommand { get; private set; }

        #endregion

        #region 数据加载与相机事件订阅

        public void LoadDevicesFromPool()
        {
            CameraDeviceList.Clear();
            var cameras = DevicePoolManager.Instance.GetAllDevices().OfType<ICamera>();
            foreach (var cam in cameras)
            {
                CameraDeviceList.Add(cam);
            }
            SelectedCameraDevice = CameraDeviceList.FirstOrDefault();
        }

        private void RefreshAllCanExecute()
        {
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartGrabCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StopGrabCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SoftwareTriggerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveImageCommand as RelayCommand<string>)?.RaiseCanExecuteChanged();
        }

        private void OnCameraDeviceChanged(ICamera oldCamera, ICamera newCamera)
        {
            // 1. 先解绑旧相机的事件（防止内存泄漏与重复订阅）
            if (oldCamera != null)
            {
                oldCamera.FrameReceived -= OnCameraFrameReceived;
                oldCamera.StateChanged -= OnCameraStateChanged;
            }

            if (newCamera == null)
            {
                IsConnected = false;
                IsGrabbing = false;
                DeviceStatusText = "未选择";
                RefreshAllCanExecute();
                return;
            }

            // 2. 绑定新相机的事件
            newCamera.FrameReceived += OnCameraFrameReceived;
            newCamera.StateChanged += OnCameraStateChanged;

            // 3. 极其优雅地初始化当前状态！
            OnCameraStateChanged(newCamera, newCamera.State);

            if (newCamera.State == DeviceState.Connected)
            {
                ReadParamsFromCamera(newCamera);
            }
        }
        /// <summary>
        /// 核心：相机的状态只要发生变化（无论是调用连接、断开，还是硬件异常断开），自动走这里！
        /// </summary>
        private void OnCameraStateChanged(object sender, DeviceState state)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsConnected = state == DeviceState.Connected;
                DeviceStatusText = state.ToString();

                // 断开连接时同步重置采集标志
                if (!IsConnected)
                {
                    IsGrabbing = false;
                }

                // 自动刷新所有 RelayCommand 的 CanExecute 按钮可用状态
                RefreshAllCanExecute();
            });
        }

        private void ReadParamsFromCamera(ICamera camera)
        {
            if (camera == null || camera.State != DeviceState.Connected) return;

            // 读取曝光
            var expRes = camera.GetExposureTime();
            if (expRes.Success && double.TryParse(expRes?.ToString(), out double exp))
            {
                ExposureTime = exp;
            }

            // 读取增益
            var gainRes = camera.GetGain();
            if (gainRes.Success && double.TryParse(gainRes?.ToString(), out double gain))
            {
                GainValue = gain;
            }

            // 读取触发模式
            var trigRes = camera.GetParam("TriggerModeSelect");
            if (trigRes.Success && int.TryParse(trigRes?.ToString(), out int mode))
            {
                SelectedTriggerMode = mode;
            }

            // 读取目标帧率
            var acqFpsRes = camera.GetParam("AcquisitionFrameRate");
            if (acqFpsRes.Success && double.TryParse(acqFpsRes?.ToString(), out double acqFps))
            {
                AcquisitionFrameRate = acqFps;
            }

            // 读取实际帧率（只读）
            var resFpsRes = camera.GetParam("ResultingFrameRate");
            if (resFpsRes.Success && double.TryParse(resFpsRes?.ToString(), out double resFps))
            {
                ResultingFrameRate = resFps;
            }

            // 读取分辨率与像素格式
            var widthRes = camera.GetParam("Width");
            if (widthRes.Success && int.TryParse(widthRes?.ToString(), out int w))
            {
                ImageWidth = w;
            }

            var heightRes = camera.GetParam("Height");
            if (heightRes.Success && int.TryParse(heightRes?.ToString(), out int h))
            {
                ImageHeight = h;
            }

            var pixelFormatRes = camera.GetParam("PixelFormat");
            if (pixelFormatRes.Success && pixelFormatRes.Data != null)
            {
                PixelFormat = pixelFormatRes.Data.ToString();
            }
        }

        #region 图像回调与 WPF 渲染绑定

        /// <summary>
        /// 相机图像接收事件回调（响应 ICamera.FrameReceived）
        /// </summary>
        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            if (e == null || e.Buffer == null || e.Width <= 0 || e.Height <= 0) return;

            // 切回 UI 线程更新像素与 WPF 数据绑定
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    // 将 Raw 字节流转换为 WPF 能够直接渲染的 BitmapSource
                    BitmapSource bitmap = CreateBitmapSourceFromRaw(e.Buffer, e.Width, e.Height, e.PixelFormat);

                    // 冻结 BitmapSource，允许跨线程安全传参与渲染
                    if (bitmap.CanFreeze)
                    {
                        bitmap.Freeze();
                    }

                    // 1. 赋给前端 Image 控件绑定的属性
                    CameraImageSource = bitmap;

                    // 2. 标志位设为 true，隐藏 "相机未开流或无实时图像信号" 占位提示
                    IsGrabbing = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HardwareConsole] 渲染图像失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 辅助函数：根据海康 Raw 缓冲区快速生成 WPF BitmapSource
        /// </summary>
        private BitmapSource CreateBitmapSourceFromRaw(byte[] buffer, int width, int height, string pixelType)
        {
            PixelFormat format = PixelFormats.Gray8; // 默认黑白相机 Mono8

            // 判断是否为 BGR/RGB 彩色格式
            if (pixelType.Contains("RGB") || pixelType.Contains("BGR"))
            {
                format = PixelFormats.Bgr24;
            }

            int stride = (width * format.BitsPerPixel + 7) / 8;

            return BitmapSource.Create(
                width,
                height,
                96, 96, // DPI 96
                format,
                format == PixelFormats.Gray8 ? BitmapPalettes.Gray256 : null,
                buffer,
                stride);
        }

        #endregion

        #endregion

        #region 命令回调实现

        private void InitCommands()
        {
            // 连接相机
            ConnectCommand = new RelayCommand(_ =>
            {
                if (SelectedCameraDevice == null) return;

                var res = SelectedCameraDevice.Connect();
                if (res.Success)
                {
                    ReadParamsFromCamera(SelectedCameraDevice);
                }
                else
                {
                    MessageBox.Show($"连接相机失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }, _ => SelectedCameraDevice != null && !IsConnected); // 直接判断 ViewModel 的 IsConnected 属性即可！

            // 断开相机
            DisconnectCommand = new RelayCommand(_ =>
            {
                if (SelectedCameraDevice == null) return;
                SelectedCameraDevice.StopGrabbing();
                SelectedCameraDevice.Disconnect();
                IsGrabbing = false;
            }, _ => SelectedCameraDevice != null && IsConnected);

            // 开启连续采集
            StartGrabCommand = new RelayCommand(_ =>
            {
                if (SelectedCameraDevice == null) return;
                var res = SelectedCameraDevice.StartGrabbing();
                if (res.Success)
                {
                    IsGrabbing = true;
                }
                else
                {
                    MessageBox.Show($"开启采集失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }, _ => IsConnected && !IsGrabbing);

            // 停止采集
            StopGrabCommand = new RelayCommand(_ =>
            {
                if (SelectedCameraDevice == null) return;
                SelectedCameraDevice.StopGrabbing();
                IsGrabbing = false;
            }, _ => IsConnected && IsGrabbing);

            // 软触发一次
            SoftwareTriggerCommand = new RelayCommand(_ =>
            {
                if (SelectedCameraDevice == null) return;

                // 确保当前处于软触发模式，否则先切换
                if (SelectedTriggerMode != 1)
                {
                    var setModeRes = SelectedCameraDevice.SetTriggerMode(true);
                    if (!setModeRes.Success)
                    {
                        MessageBox.Show($"切换到软触发模式失败: {setModeRes.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    SelectedTriggerMode = 1;
                }

                // 软触发前若未取流，需要先开启取流（海康 SDK 要求软触发命令配合已开启的取流）
                if (!IsGrabbing)
                {
                    var grabRes = SelectedCameraDevice.StartGrabbing();
                    if (!grabRes.Success)
                    {
                        MessageBox.Show($"软触发前开启取流失败: {grabRes.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    IsGrabbing = true;
                }

                var res = SelectedCameraDevice.SoftwareTrigger();
                if (!res.Success)
                {
                    MessageBox.Show($"发送软触发失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }, _ => IsConnected);

            // 保存当前帧图像
            SaveImageCommand = new RelayCommand<string>(format =>
            {
                if (SelectedCameraDevice == null || !IsConnected || !IsGrabbing)
                {
                    MessageBox.Show("相机未连接或未取流，无法保存图像", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string f = (format ?? "bmp").ToLowerInvariant();
                string ext;
                switch (f)
                {
                    case "jpg":
                    case "jpeg": ext = "jpg"; break;
                    case "png": ext = "png"; break;
                    case "tif":
                    case "tiff": ext = "tif"; break;
                    case "bmp":
                    default: ext = "bmp"; break;
                }

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = $"{ext.ToUpper()} 图像|*.{ext}",
                    DefaultExt = ext,
                    FileName = $"Camera_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}"
                };

                if (dialog.ShowDialog() == true)
                {
                    var res = SelectedCameraDevice.SaveImageFile(dialog.FileName, ext);
                    if (!res.Success)
                    {
                        MessageBox.Show($"保存图像失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    else
                    {
                        MessageBox.Show($"图像已保存: {dialog.FileName}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }, _ => IsConnected && IsGrabbing);
        }

        #endregion

        #region 通信日志监视属性与数据结构

        public ObservableCollection<IDevice> AllCommunicationDevices { get; set; } = new ObservableCollection<IDevice>();
        public ObservableCollection<CommunicationMessage> CommLogs { get; set; } = new ObservableCollection<CommunicationMessage>();

        private IDevice _selectedCommDevice;
        public IDevice SelectedCommDevice
        {
            get => _selectedCommDevice;
            set
            {
                if (Set(ref _selectedCommDevice, value))
                {
                    // 可在此切换选中的通信日志监听
                }
            }
        }

        public ICommand ClearCommLogCommand { get; private set; }
        public ICommand SendCustomRawCommand { get; private set; }

        private string _sendBufferText;
        public string SendBufferText
        {
            get => _sendBufferText;
            set => Set(ref _sendBufferText, value);
        }
        // 是否开启通信监控（仅当 Tab 选中时设为 true）
        private bool _isCommTabActive;
        public bool IsCommTabActive
        {
            get => _isCommTabActive;
            set
            {
                if (Set(ref _isCommTabActive, value))
                {
                    ToggleCommunicationMonitoring(value);
                }
            }
        }

        // 结构化发送参数
        public ObservableCollection<string> CommandTypeList { get; } = new ObservableCollection<string>
    {
        "ReadBool", "WriteBool", "ReadInt32", "WriteInt32", "ReadFloat", "WriteFloat", "ReadString"
    };

        private string _selectedCmdType = "ReadInt32";
        public string SelectedCmdType
        {
            get => _selectedCmdType;
            set => Set(ref _selectedCmdType, value);
        }

        private string _targetAddress = "D100";
        public string TargetAddress
        {
            get => _targetAddress;
            set => Set(ref _targetAddress, value);
        }

        private string _writeValue = "123";
        public string WriteValue
        {
            get => _writeValue;
            set => Set(ref _writeValue, value);
        }

        // 日志加入逻辑（UI 限制 200 条）
        public void AppendCommLog(string direction, string content, bool isSuccess, string remark)
        {
            if (!IsCommTabActive) return; // 离开界面直接忽略，零 CPU/内存损耗！

            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (CommLogs.Count >= 200)
                {
                    CommLogs.RemoveAt(0); // 超过 200 条丢弃最老的一条
                }
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

        private void ToggleCommunicationMonitoring(bool enable)
        {
            if (SelectedCommDevice is ICommunicationObservable observable)
            {
                if (enable)
                    observable.MessageTransmitted += OnMessageTransmitted;
                else
                    observable.MessageTransmitted -= OnMessageTransmitted;
            }
        }

        private void OnMessageTransmitted(object sender, CommunicationMessage e)
        {
            AppendCommLog(e.Direction, e.Content, e.IsSuccess, e.Remark);
        }
        #endregion
        #region 数字量 IO 完善逻辑

        public ObservableCollection<IoPointModel> InputIOList { get; set; } = new ObservableCollection<IoPointModel>();
        public ObservableCollection<IoPointModel> OutputIOList { get; set; } = new ObservableCollection<IoPointModel>();

        private DeviceInfoModel _selectedIoDevice;
        public DeviceInfoModel SelectedIoDevice
        {
            get => _selectedIoDevice;
            set
            {
                if (Set(ref _selectedIoDevice, value))
                {
                    RefreshIoPointsForSelectedDevice();
                }
            }
        }

        private void InitIoAndCommCommands()
        {
            ClearCommLogCommand = new RelayCommand(_ => CommLogs.Clear());

            SendCustomRawCommand = new RelayCommand(_ =>
            {
                if (string.IsNullOrWhiteSpace(SendBufferText)) return;

                // 手动测试收发报文逻辑
                CommLogs.Add(new CommunicationMessage
                {
                    Direction = "TX",
                    Content = SendBufferText,
                    IsSuccess = true,
                    Remark = "手动指令发送"
                });
            });
        }

        private void RefreshIoPointsForSelectedDevice()
        {
            InputIOList.Clear();
            OutputIOList.Clear();

            // 示例模拟数据填充（实际开发中从 SelectedIoDevice 读取硬件通道列表）
            for (int i = 0; i < 8; i++)
            {
                InputIOList.Add(new IoPointModel { ChannelIndex = i, Name = $"DI_{i:D2} (感应器{i + 1})", IsActive = false });

                var doPoint = new IoPointModel { ChannelIndex = i, Name = $"DO_{i:D2} (电磁阀{i + 1})", IsActive = false };
                // 绑定 DO 切换事件通知底层硬件
                doPoint.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(IoPointModel.IsActive))
                    {
                        // TODO: 调用底层 IO 板卡写 DO 接口: WriteOutputBit(doPoint.ChannelIndex, doPoint.IsActive);
                    }
                };
                OutputIOList.Add(doPoint);
            }
        }

        #endregion

    }

    #region 辅助数据模型
    public class DeviceInfoModel
    {
        public string DeviceId { get; set; }
        public string DisplayName { get; set; }
    }

    public class AxisInfoModel
    {
        public int AxisIndex { get; set; }
        public string AxisName { get; set; }
    }

    public class IoPointModel : ViewModelBase
    {
        public int ChannelIndex { get; set; }
        public string Name { get; set; }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (Set(ref _isActive, value))
                {
                    OnPropertyChanged(nameof(StateText));
                }
            }
        }

        public string StateText => IsActive ? "ON" : "OFF";
    }
    #endregion
}