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

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{
    public class CameraDebugViewModel : ViewModelBase
    {
        public CameraDebugViewModel()
        {
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

        private int _selectedTriggerMode = 0;
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
                    SelectedCameraDevice.SetParam("AcquisitionFrameRate", value);
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

        public ICommand ConnectCommand { get; private set; }
        public ICommand DisconnectCommand { get; private set; }
        public ICommand SoftwareTriggerCommand { get; private set; }
        public ICommand StartGrabCommand { get; private set; }
        public ICommand StopGrabCommand { get; private set; }
        public ICommand SaveImageCommand { get; private set; }

        private void LoadDevices()
        {
            CameraDeviceList.Clear();
            var cameras = DevicePoolManager.Instance.GetAllDevices().OfType<ICamera>();
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
            }, _ => SelectedCameraDevice != null && IsConnected);

            StartGrabCommand = new RelayCommand(_ =>
            {
                var res = SelectedCameraDevice?.StartGrabbing();
                if (res?.Success == true) IsGrabbing = true;
            }, _ => IsConnected && !IsGrabbing);

            StopGrabCommand = new RelayCommand(_ =>
            {
                SelectedCameraDevice?.StopGrabbing();
                IsGrabbing = false;
            }, _ => IsConnected && IsGrabbing);

            SoftwareTriggerCommand = new RelayCommand(_ =>
            {
                if (SelectedTriggerMode != 1)
                {
                    SelectedCameraDevice?.SetTriggerMode(true);
                    SelectedTriggerMode = 1;
                }
                if (!IsGrabbing)
                {
                    SelectedCameraDevice?.StartGrabbing();
                    IsGrabbing = true;
                }
                SelectedCameraDevice?.SoftwareTrigger();
            }, _ => IsConnected);

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
            }, _ => IsConnected && IsGrabbing);
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
            if (oldCamera != null)
            {
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

        private void ReadParamsFromCamera(ICamera camera)
        {
            if (camera == null || camera.State != DeviceState.Connected) return;
            var expRes = camera.GetExposureTime();
            if (expRes.Success && double.TryParse(expRes?.ToString(), out double exp)) ExposureTime = exp;

            var gainRes = camera.GetGain();
            if (gainRes.Success && double.TryParse(gainRes?.ToString(), out double gain)) GainValue = gain;

            var pixelRes = camera.GetParam("PixelFormat");
            if (pixelRes.Success) PixelFormat = pixelRes.Data?.ToString();
        }

        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            if (e?.Buffer == null) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    PixelFormat format = e.PixelFormat.Contains("RGB") || e.PixelFormat.Contains("BGR") ? PixelFormats.Bgr24 : PixelFormats.Gray8;
                    int stride = (e.Width * format.BitsPerPixel + 7) / 8;
                    BitmapSource bitmap = BitmapSource.Create(e.Width, e.Height, 96, 96, format, format == PixelFormats.Gray8 ? BitmapPalettes.Gray256 : null, e.Buffer, stride);
                    if (bitmap.CanFreeze) bitmap.Freeze();
                    CameraImageSource = bitmap;
                    IsGrabbing = true;
                }
                catch { }
            });
        }
    }
}