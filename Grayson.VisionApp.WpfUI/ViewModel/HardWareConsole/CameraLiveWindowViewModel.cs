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
using System.Windows.Threading;

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{
    /// <summary>
    /// 相机实时画面弹窗 VM（轴动监控用）。
    /// 与 CameraDebugViewModel 独立：直接订阅相机 FrameReceived，
    /// 帧到达 → HalconImageRenderService.WrapImage → 替换 ActiveImageContext 自动上屏。
    /// 抓流所有权策略：打开时先探测 800ms，若已有外部取流（如相机调试页）在跑则直接搭车显示，
    /// 关闭时只停止自己发起的取流，不影响其他页面的流。
    /// </summary>
    public class CameraLiveWindowViewModel : ViewModelBase
    {
        private readonly IDevicePool _devicePool;
        private readonly HalconImageRenderService _renderService;

        /// <summary>抓流所有权探测定时器（打开后短暂等待，判断是否已有流）</summary>
        private DispatcherTimer _ownershipProbeTimer;

        /// <summary>探测窗口内是否收到过帧（说明外部已在取流）</summary>
        private bool _receivedFrameDuringProbe;

        /// <summary>本窗口是否自己发起了取流（关闭时才停流）</summary>
        private bool _weStartedGrabbing;

        /// <summary>本窗口是否切换过触发模式（关闭时恢复）</summary>
        private int? _triggerModeToRestore;

        /// <summary>相机实时画面渲染 VM，供 HalconImageDisplayHost 绑定</summary>
        public ImageDisplayVm CameraDisplayVm { get; }

        public ObservableCollection<ICamera> CameraDeviceList { get; } = new ObservableCollection<ICamera>();

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
                }
            }
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set => Set(ref _isConnected, value);
        }

        private bool _isLive;
        /// <summary>是否正在收到实时帧（控制占位提示显隐）</summary>
        public bool IsLive
        {
            get => _isLive;
            private set => Set(ref _isLive, value);
        }

        private string _statusText = "初始化...";
        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        private DateTime _lastRenderTime = DateTime.MinValue;

        public CameraLiveWindowViewModel()
        {
            _devicePool = App.StationHostRuntime?.DevicePool ?? throw new InvalidOperationException("DevicePool not initialized");
            _renderService = new HalconImageRenderService();
            CameraDisplayVm = new ImageDisplayVm(_renderService);

            LoadDevices();
        }

        private void LoadDevices()
        {
            CameraDeviceList.Clear();
            foreach (var cam in _devicePool.GetAllDevices().OfType<ICamera>())
            {
                CameraDeviceList.Add(cam);
            }
            SelectedCameraDevice = CameraDeviceList.FirstOrDefault();
        }

        /// <summary>
        /// 窗口打开时调用：订阅事件并确保相机出图。
        /// </summary>
        public void StartLive()
        {
            if (SelectedCameraDevice == null)
            {
                StatusText = "设备池中未发现相机设备";
                return;
            }

            if (SelectedCameraDevice.State != DeviceState.Connected)
            {
                StatusText = "正在连接相机...";
                var res = SelectedCameraDevice.Connect();
                if (!res.Success)
                {
                    StatusText = $"相机连接失败: {res.Message}";
                    return;
                }
            }

            IsConnected = true;
            StatusText = "已连接，等待图像...";

            // 探测 800ms：若期间收到帧，说明外部（如相机调试页）已在取流，直接搭车显示；
            // 否则由本窗口发起取流（必要时切到连续采集模式），关闭时负责停流。
            _receivedFrameDuringProbe = false;
            _ownershipProbeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _ownershipProbeTimer.Tick += (s, e) =>
            {
                _ownershipProbeTimer.Stop();
                if (!_receivedFrameDuringProbe)
                {
                    TryStartGrabbing();
                }
            };
            _ownershipProbeTimer.Start();
        }

        private void TryStartGrabbing()
        {
            var camera = SelectedCameraDevice;
            if (camera == null || camera.State != DeviceState.Connected) return;

            // 软触发/硬触发模式下不会自动出图，切到连续采集（关闭时恢复）
            var modeRes = camera.GetParam("TriggerModeSelect");
            if (modeRes.Success && int.TryParse(modeRes.Data?.ToString(), out int mode) && mode != 0)
            {
                camera.SetTriggerMode(0);
                _triggerModeToRestore = mode;
            }

            var res = camera.StartGrabbing();
            if (res.Success)
            {
                _weStartedGrabbing = true;
                StatusText = "正在连续采集...";
            }
            else
            {
                StatusText = $"开启采集失败: {res.Message}";
            }
        }

        /// <summary>
        /// 窗口关闭时调用：解绑事件，仅停止自己发起的取流，清空显示。
        /// </summary>
        public void Cleanup()
        {
            _ownershipProbeTimer?.Stop();
            _ownershipProbeTimer = null;

            var camera = SelectedCameraDevice;
            if (camera != null)
            {
                camera.FrameReceived -= OnCameraFrameReceived;
                camera.StateChanged -= OnCameraStateChanged;

                if (_weStartedGrabbing)
                {
                    camera.StopGrabbing();
                    // 恢复打开前的触发模式
                    if (_triggerModeToRestore.HasValue)
                    {
                        camera.SetTriggerMode(_triggerModeToRestore.Value);
                        _triggerModeToRestore = null;
                    }
                }
            }
            _weStartedGrabbing = false;

            IsLive = false;
            CameraDisplayVm.Clear();
        }

        private void OnCameraDeviceChanged(ICamera oldCamera, ICamera newCamera)
        {
            if (oldCamera != null)
            {
                oldCamera.FrameReceived -= OnCameraFrameReceived;
                oldCamera.StateChanged -= OnCameraStateChanged;
                if (_weStartedGrabbing)
                {
                    oldCamera.StopGrabbing();
                    if (_triggerModeToRestore.HasValue)
                    {
                        oldCamera.SetTriggerMode(_triggerModeToRestore.Value);
                        _triggerModeToRestore = null;
                    }
                }
            }
            _weStartedGrabbing = false;
            _receivedFrameDuringProbe = false;
            IsLive = false;

            if (newCamera == null)
            {
                IsConnected = false;
                StatusText = "设备池中未发现相机设备";
                return;
            }

            newCamera.FrameReceived += OnCameraFrameReceived;
            newCamera.StateChanged += OnCameraStateChanged;
            OnCameraStateChanged(newCamera, newCamera.State);
        }

        private void OnCameraStateChanged(object sender, DeviceState state)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsConnected = state == DeviceState.Connected;
                if (!IsConnected)
                {
                    IsLive = false;
                    StatusText = "相机未连接";
                }
            });
        }

        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            if (e?.Buffer == null || e.Width <= 0 || e.Height <= 0) return;
            _receivedFrameDuringProbe = true;

            // UI 限帧（约 30 FPS）
            if ((DateTime.Now - _lastRenderTime).TotalMilliseconds < 33) return;
            _lastRenderTime = DateTime.Now;

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var renderImage = _renderService.WrapImage(e) as HalconRenderImage;
                    if (renderImage == null) return;

                    var context = new WpfImageRenderContext
                    {
                        NodeId = "camera-live-axis",
                        NodeName = "相机实时图像",
                        Image = renderImage
                    };

                    var old = CameraDisplayVm.ActiveImageContext;
                    CameraDisplayVm.ActiveImageContext = context;
                    old?.Dispose(); // 释放上一帧 HImage 句柄

                    if (!IsLive)
                    {
                        IsLive = true;
                        StatusText = "实时预览中";
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CameraLiveWindow] 图像渲染异常: {ex.Message}");
                }
            }), DispatcherPriority.Render);
        }
    }
}
