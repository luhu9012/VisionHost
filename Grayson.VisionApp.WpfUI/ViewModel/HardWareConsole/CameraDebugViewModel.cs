using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{
    public class CameraDebugViewModel : ViewModelBase
    {
        private readonly IDevicePool _devicePool;

        /// <summary>Halcon 图像渲染服务：负责相机帧 → HImage 包装</summary>
        private readonly HalconImageRenderService _renderService;

        public CameraDebugViewModel()
        {
            _devicePool = App.StationHostRuntime?.DevicePool ?? throw new InvalidOperationException("DevicePool not initialized");

            // 图像显示 VM：直接绑定 View 层 Halcon 控件，帧到达时替换 ActiveImageContext 即自动渲染
            _renderService = new HalconImageRenderService();
            CameraDisplayVm = new ImageDisplayVm(_renderService);

            InitCommands();
            LoadDevices();
        }

        /// <summary>
        /// 相机实时画面渲染 VM，供 CameraDebugView 的 HalconImageDisplayHost 绑定。
        /// </summary>
        public ImageDisplayVm CameraDisplayVm { get; }

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

        private bool _isGrabbing;
        public bool IsGrabbing
        {
            get => _isGrabbing;
            set
            {
                if (Set(ref _isGrabbing, value))
                {
                    RefreshAllCanExecute();
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
                    // 同步期间不将 UI 值写入硬件，避免反向覆盖
                    if (!_isSyncingWithHardware)
                    {
                        SelectedCameraDevice.SetExposureTime(value);
                    }
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

        private int _selectedTriggerMode = 0;
        public int SelectedTriggerMode
        {
            get => _selectedTriggerMode;
            set
            {
                if (Set(ref _selectedTriggerMode, value))
                {
                    if (IsConnected && SelectedCameraDevice != null && !_isSyncingWithHardware)
                    {
                        SelectedCameraDevice.SetTriggerMode(value);
                    }
                    // 触发模式切换时刷新所有按钮的 CanExecute 状态
                    RefreshAllCanExecute();
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
                    if (!_isSyncingWithHardware)
                    {
                        SelectedCameraDevice.SetParam("AcquisitionFrameRate", value);
                    }
                }
            }
        }

        private string _pixelFormat = "未知";
        public string PixelFormat
        {
            get => _pixelFormat;
            private set => Set(ref _pixelFormat, value);
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set => Set(ref _isConnected, value);
        }

        private bool _isActive;
        /// <summary>
        /// 表示当前相机调试 Tab 是否处于显示/激活状态
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (Set(ref _isActive, value) && !_isActive)
                {
                    // 离开 Tab 时停止取流，避免后台持续占用带宽
                    if (IsGrabbing)
                    {
                        SelectedCameraDevice?.StopGrabbing();
                        IsGrabbing = false;
                    }
                }
            }
        }

        public ICommand ConnectCommand { get; private set; }
        public ICommand DisconnectCommand { get; private set; }
        public ICommand SoftwareTriggerCommand { get; private set; }
        public ICommand StartGrabCommand { get; private set; }
        public ICommand StopGrabCommand { get; private set; }
        public ICommand SaveImageCommand { get; private set; }

        private void LoadDevices()
        {
            CameraDeviceList.Clear();
            var cameras = _devicePool.GetAllDevices().OfType<ICamera>();
            foreach (var cam in cameras) CameraDeviceList.Add(cam);
            SelectedCameraDevice = CameraDeviceList.FirstOrDefault();
        }

        private void InitCommands()
        {
            ConnectCommand = new RelayCommand(_ =>
            {
                if (SelectedCameraDevice == null) return;
                var res = SelectedCameraDevice.Connect();
                if (res.Success)
                {
                    // 连接后显式把硬件触发模式对齐到 UI 当前选择，
                    // 杜绝「UI 显示连续 / 硬件却停留在 On(上次软触发残留)」导致的零帧无画面。
                    // （BaslerCamera 底层 Open 已有 TriggerMode=Off 兜底，此处再按 UI 精确对齐）
                    SelectedCameraDevice.SetTriggerMode(SelectedTriggerMode);
                    ReadParamsFromCamera(SelectedCameraDevice);
                }
                else MessageBox.Show($"连接失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }, _ => SelectedCameraDevice != null && !IsConnected);

            DisconnectCommand = new RelayCommand(_ =>
            {
                SelectedCameraDevice?.StopGrabbing();
                SelectedCameraDevice?.Disconnect();
                IsGrabbing = false;

                // 2. 需求修复：断开连接后清空视图图像与参数
                CameraDisplayVm.Clear();
                PixelFormat = "未知";
            }, _ => SelectedCameraDevice != null && IsConnected);

            StartGrabCommand = new RelayCommand(_ =>
            {
                var res = SelectedCameraDevice?.StartGrabbing();
                if (res?.Success == true) IsGrabbing = true;
            }, _ => IsConnected && !IsGrabbing && SelectedTriggerMode == 0); // 1. 连续采集按钮仅在连续模式(0)下可用

            StopGrabCommand = new RelayCommand(_ =>
            {
                SelectedCameraDevice?.StopGrabbing();
                IsGrabbing = false;
            }, _ => IsConnected && IsGrabbing);

            SoftwareTriggerCommand = new RelayCommand(_ =>
            {
                if (!IsGrabbing)
                {
                    SelectedCameraDevice?.StartGrabbing();
                    IsGrabbing = true;
                }
                SelectedCameraDevice?.SoftwareTrigger();
            }, _ => IsConnected && SelectedTriggerMode == 1); // 1. 软触发按钮仅在软触发模式(1)下可用

            SaveImageCommand = new RelayCommand<string>(format =>
            {
                string ext = (format ?? "bmp").ToLower();
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = $"{ext.ToUpper()} Image|*.{ext}",
                    FileName = $"Camera_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}"
                };
                if (dialog.ShowDialog() == true)
                {
                    SelectedCameraDevice?.SaveImageFile(dialog.FileName, ext);
                }
            }, _ => IsConnected); // 只要连接即可点击保存（依赖相机自身 SaveImageFile）
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

        /// <summary>
        /// VM 释放或 Tab 隐藏时调用：停止取流并释放事件订阅。
        /// </summary>
        public void Cleanup()
        {
            if (SelectedCameraDevice != null)
            {
                SelectedCameraDevice.StopGrabbing();
                SelectedCameraDevice.FrameReceived -= OnCameraFrameReceived;
                SelectedCameraDevice.StateChanged -= OnCameraStateChanged;
            }
            IsGrabbing = false;

            // 清空 Halcon 显示画面，释放当前帧 HImage 句柄
            CameraDisplayVm.Clear();
        }

        private void OnCameraDeviceChanged(ICamera oldCamera, ICamera newCamera)
        {
            if (oldCamera != null)
            {
                oldCamera.StopGrabbing();
                oldCamera.FrameReceived -= OnCameraFrameReceived;
                oldCamera.StateChanged -= OnCameraStateChanged;
            }
            if (newCamera == null)
            {
                IsConnected = false;
                IsGrabbing = false;
                RefreshAllCanExecute();
                return;
            }
            newCamera.FrameReceived += OnCameraFrameReceived;
            newCamera.StateChanged += OnCameraStateChanged;
            OnCameraStateChanged(newCamera, newCamera.State);

            if (newCamera.State == DeviceState.Connected) ReadParamsFromCamera(newCamera);
        }

        private void OnCameraStateChanged(object sender, DeviceState state)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsConnected = state == DeviceState.Connected;
                if (!IsConnected) IsGrabbing = false;
                RefreshAllCanExecute();
            });
        }

        // 1. 定义硬件同步标志位，防止读取更新属性时触发 Setter 再次写硬件
        private bool _isSyncingWithHardware;

        private void ReadParamsFromCamera(ICamera camera)
        {
            if (camera == null || camera.State != DeviceState.Connected) return;

            _isSyncingWithHardware = true;
            try
            {
                // 曝光时间
                var expRes = camera.GetExposureTime() as Result<object>;
                if (expRes.Success && double.TryParse(expRes.Data?.ToString(), out double exp))
                {
                    ExposureTime = exp;
                }

                // 增益
                var gainRes = camera.GetGain() as Result<object>;
                if (gainRes.Success && double.TryParse(gainRes.Data?.ToString(), out double gain))
                {
                    GainValue = gain;
                }

                // 像素格式
                var pixelRes = camera.GetParam("PixelFormat");
                if (pixelRes.Success)
                {
                    PixelFormat = pixelRes.Data?.ToString();
                }

                // 补全：目标帧率
                var fpsRes = camera.GetParam("AcquisitionFrameRate");
                if (fpsRes.Success && double.TryParse(fpsRes.Data?.ToString(), out double fps))
                {
                    AcquisitionFrameRate = fps;
                }

                // 补全：触发模式（读取硬件 ConfigParams 里的 TriggerModeSelect）
                var modeRes = camera.GetParam("TriggerModeSelect");
                if (modeRes.Success && int.TryParse(modeRes.Data?.ToString(), out int mode))
                {
                    SelectedTriggerMode = mode;
                }
            }
            finally
            {
                _isSyncingWithHardware = false;
            }
        }

        // 
        private DateTime _lastRenderTime = DateTime.MinValue;

        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            // 诊断：帧已到达 VM 层（若能看到此日志但无画面 → 显示层问题；若看不到 → 取流问题）
            System.Diagnostics.Debug.WriteLine(
                $"[CameraDebugView] 收到帧 #{e?.FrameNum} {e?.Width}x{e?.Height} {e?.PixelFormat} buf={e?.Buffer?.Length}");

            if (e?.Buffer == null || e.Width <= 0 || e.Height <= 0) return;

            // 1. UI 限帧 (约 30 FPS)
            if ((DateTime.Now - _lastRenderTime).TotalMilliseconds < 33)
            {
                return;
            }
            _lastRenderTime = DateTime.Now;

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // 2. 相机帧 → Halcon HImage 包装（WrapImage 内部处理 BGR/Gray8 转换）
                    var renderImage = _renderService.WrapImage(e);
                    if (renderImage == null)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[CameraDebugView] WrapImage 返回 null（像素格式不受支持？{e.PixelFormat}），未渲染");
                        return;
                    }

                    // 3. 构造渲染上下文并替换 ActiveImageContext
                    //    HalconImageDisplayHost 通过 DataContext(CameraDisplayVm) 订阅了
                    //    OnRequestRender 事件，替换 ActiveImageContext 即自动触发窗口渲染
                    var context = new WpfImageRenderContext
                    {
                        NodeId = "camera-live",
                        NodeName = "相机实时图像",
                        Image = renderImage
                    };

                    var old = CameraDisplayVm.ActiveImageContext;
                    CameraDisplayVm.ActiveImageContext = context;
                    old?.Dispose(); // 释放上一帧 HImage 句柄，避免内存泄漏

                    IsGrabbing = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CameraDebugView] 图像渲染异常: {ex.Message}");
                }
            }), System.Windows.Threading.DispatcherPriority.Render);
        }
    }
}