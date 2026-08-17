using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Core;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{
    public class CameraDebugViewModel : ViewModelBase
    {
        private readonly IDevicePool _devicePool;

        public CameraDebugViewModel()
        {
            _devicePool = App.StationHostRuntime?.DevicePool ?? throw new InvalidOperationException("DevicePool not initialized");
            InitCommands();
            LoadDevices();
        }

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
                if (res.Success) ReadParamsFromCamera(SelectedCameraDevice);
                else MessageBox.Show($"连接失败: {res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }, _ => SelectedCameraDevice != null && !IsConnected);

            DisconnectCommand = new RelayCommand(_ =>
            {
                SelectedCameraDevice?.StopGrabbing();
                SelectedCameraDevice?.Disconnect();
                IsGrabbing = false;

                // 2. 需求修复：断开连接后清空视图图像与参数
                CameraImageSource = null;
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
            }, _ => IsConnected); // 取消对 CameraImageSource != null 的依赖，只要连接即可点击（或者只判断 IsConnected）
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
                    string fmtStr = (e.PixelFormat ?? "").ToUpperInvariant();

                    // 根据输出格式精确判断 WPF 格式
                    bool isColor = fmtStr.Contains("BGR") || fmtStr.Contains("RGB");

                    PixelFormat format = isColor ? PixelFormats.Bgr24 : PixelFormats.Gray8;
                    int bytesPerPixel = isColor ? 3 : 1;
                    int rawStride = e.Width * bytesPerPixel;

                    WriteableBitmap writeableBitmap = CameraImageSource as WriteableBitmap;

                    if (writeableBitmap == null ||
                        writeableBitmap.PixelWidth != e.Width ||
                        writeableBitmap.PixelHeight != e.Height ||
                        writeableBitmap.Format != format)
                    {
                        writeableBitmap = new WriteableBitmap(
                            e.Width,
                            e.Height,
                            96,
                            96,
                            format,
                            format == PixelFormats.Gray8 ? BitmapPalettes.Gray256 : null);

                        CameraImageSource = writeableBitmap;
                    }

                    int bitmapStride = writeableBitmap.BackBufferStride;

                    writeableBitmap.Lock();
                    IntPtr pBackBuffer = writeableBitmap.BackBuffer;

                    // 逐行拷贝与 4 字节对齐处理
                    if (rawStride == bitmapStride)
                    {
                        Marshal.Copy(e.Buffer, 0, pBackBuffer, Math.Min(e.Buffer.Length, bitmapStride * e.Height));
                    }
                    else
                    {
                        for (int row = 0; row < e.Height; row++)
                        {
                            IntPtr dstRowPtr = pBackBuffer + (row * bitmapStride);
                            int srcOffset = row * rawStride;

                            if (srcOffset + rawStride <= e.Buffer.Length)
                            {
                                Marshal.Copy(e.Buffer, srcOffset, dstRowPtr, rawStride);
                            }
                        }
                    }

                    writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, e.Width, e.Height));
                    writeableBitmap.Unlock();

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