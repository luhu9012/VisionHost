//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationWizardViewModel.cs
//===================================================================================
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.ViewModel.Steps;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class CalibrationWizardViewModel : ViewModelBase
    {
        private const float DefaultMoveSpeed = 50f;
        private readonly Window _owner;
        private readonly StringBuilder _log = new StringBuilder();
        private readonly IDevicePool _devicePool;
        private readonly HalconImageRenderService _renderService;
        private readonly ICalibrationProfileRepository _calibrationProfileRepository;
        private readonly AutoResetEvent _frameArrivedEvent = new AutoResetEvent(false);
        private ICalibrationStepStrategy _strategy;
        private WpfImageRenderContext _latestFrameContext;
        private int _frameSequence;

        public ICalibrationService CalibService { get; }
        public CalibrationProfile TargetProfile { get; }
        public ImageDisplayVm CalibrationImageDisplay { get; }
        public ObservableCollection<CalibrationPointModel> CalibrationPoints { get; } = new ObservableCollection<CalibrationPointModel>();
        public ObservableCollection<RotationPointModel> RotationPoints { get; } = new ObservableCollection<RotationPointModel>();
        public ObservableCollection<ICamera> CameraDeviceList { get; } = new ObservableCollection<ICamera>();
        public ObservableCollection<IMotionCard> MotionDeviceList { get; } = new ObservableCollection<IMotionCard>();
        public ObservableCollection<CalibrationProfile> AvailableDistortionProfiles { get; } = new ObservableCollection<CalibrationProfile>();

        private string _rotationCenterResult = "Cx: --, Cy: --";
        public string RotationCenterResult
        {
            get => _rotationCenterResult;
            set => Set(ref _rotationCenterResult, value);
        }

        private string _rotationCenterPixelResult = "Px: --, Py: --";
        public string RotationCenterPixelResult
        {
            get => _rotationCenterPixelResult;
            set => Set(ref _rotationCenterPixelResult, value);
        }

        private string _rotationCenterWorldResult = "Wx: --, Wy: --";
        public string RotationCenterWorldResult
        {
            get => _rotationCenterWorldResult;
            set => Set(ref _rotationCenterWorldResult, value);
        }

        private ICamera _selectedCameraDevice;
        public ICamera SelectedCameraDevice
        {
            get => _selectedCameraDevice;
            set
            {
                var oldCamera = _selectedCameraDevice;
                if (Set(ref _selectedCameraDevice, value))
                {
                    OnSelectedCameraChanged(oldCamera, value);
                    UpdateBindingInfo();
                }
            }
        }

        private IMotionCard _selectedMotionDevice;
        public IMotionCard SelectedMotionDevice
        {
            get => _selectedMotionDevice;
            set
            {
                var oldMotion = _selectedMotionDevice;
                if (Set(ref _selectedMotionDevice, value))
                {
                    OnSelectedMotionChanged(oldMotion, value);
                    UpdateBindingInfo();
                }
            }
        }

        private bool _isCameraConnected;
        public bool IsCameraConnected
        {
            get => _isCameraConnected;
            private set => Set(ref _isCameraConnected, value);
        }

        private bool _isMotionConnected;
        public bool IsMotionConnected
        {
            get => _isMotionConnected;
            private set => Set(ref _isMotionConnected, value);
        }

        private string _hardwareBindingSummary = "未绑定硬件";
        public string HardwareBindingSummary
        {
            get => _hardwareBindingSummary;
            set => Set(ref _hardwareBindingSummary, value);
        }

        public double GridStepX { get; set; } = 10.0;
        public double GridStepY { get; set; } = 10.0;

        private int _checkerboardRows = 7;
        public int CheckerboardRows
        {
            get => _checkerboardRows;
            set => Set(ref _checkerboardRows, value);
        }

        private int _checkerboardCols = 7;
        public int CheckerboardCols
        {
            get => _checkerboardCols;
            set => Set(ref _checkerboardCols, value);
        }

        private double _checkerboardSpacingMm = 10.0;
        public double CheckerboardSpacingMm
        {
            get => _checkerboardSpacingMm;
            set => Set(ref _checkerboardSpacingMm, value);
        }

        private double _measuredPixelDistance = 100.0;
        public double MeasuredPixelDistance
        {
            get => _measuredPixelDistance;
            set => Set(ref _measuredPixelDistance, value);
        }

        private double _knownPhysicalDistanceMm = 10.0;
        public double KnownPhysicalDistanceMm
        {
            get => _knownPhysicalDistanceMm;
            set => Set(ref _knownPhysicalDistanceMm, value);
        }

        public string OutputHomMatPath { get; private set; }

        private double _calculatedRms;
        public double CalculatedRms
        {
            get => _calculatedRms;
            set => Set(ref _calculatedRms, value);
        }

        public string LogText => _log.ToString();

        private int _currentStep;
        public int CurrentStep
        {
            get => _currentStep;
            set
            {
                if (Set(ref _currentStep, value))
                {
                    OnPropertyChanged(nameof(IsNotLastStep));
                    OnPropertyChanged(nameof(IsLastStep));
                    OnPropertyChanged(nameof(StepGuideTip));
                    RefreshStepBackgrounds();
                    (PrevStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (NextStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsNotLastStep => CurrentStep < 3;
        public bool IsLastStep => CurrentStep == 3;
        private static readonly SolidColorBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
        private static readonly SolidColorBrush InactiveBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100));
        public SolidColorBrush Step0Background => CurrentStep >= 0 ? ActiveBrush : InactiveBrush;
        public SolidColorBrush Step1Background => CurrentStep >= 1 ? ActiveBrush : InactiveBrush;
        public SolidColorBrush Step2Background => CurrentStep >= 2 ? ActiveBrush : InactiveBrush;
        public SolidColorBrush Step3Background => CurrentStep >= 3 ? ActiveBrush : InactiveBrush;
        public string StepGuideTip => _strategy != null ? _strategy.GetStepGuideTip(CurrentStep + 1) : string.Empty;

        public ICommand TriggerSampleCommand { get; }
        public ICommand TriggerRotationSampleCommand { get; }
        public ICommand AutoRunAllCommand { get; }
        public ICommand RunCalibrationCommand { get; }
        public ICommand SaveResultCommand { get; }
        public ICommand PrevStepCommand { get; }
        public ICommand NextStepCommand { get; }

        public CalibrationWizardViewModel(Window owner, CalibrationProfile profile = null)
        {
            _owner = owner;
            _devicePool = App.StationHostRuntime != null ? App.StationHostRuntime.DevicePool : null;
            _renderService = new HalconImageRenderService();
            _calibrationProfileRepository = StorageFactory.CreateCalibrationProfileRepository();
            CalibService = new CalibrationService();
            CalibrationImageDisplay = new ImageDisplayVm(_renderService);
            TargetProfile = profile ?? new CalibrationProfile { Id = Guid.NewGuid().ToString("N"), Name = "新建标定方案", Type = CalibrationType.NinePointHandEye };

            SelectStrategy(TargetProfile.Type);
            _strategy.InitializePoints(this);

            TriggerSampleCommand = new RelayCommand(_ => _strategy.TriggerSample(this));
            TriggerRotationSampleCommand = new RelayCommand(_ => CaptureNextRotationPoint());
            AutoRunAllCommand = new RelayCommand(_ => _strategy.AutoRunAll(this));
            RunCalibrationCommand = new RelayCommand(_ => _strategy.ExecuteCalibration(this));
            SaveResultCommand = new RelayCommand(_ => SaveResult());
            PrevStepCommand = new RelayCommand(_ => PrevStep(), _ => CurrentStep > 0);
            NextStepCommand = new RelayCommand(_ => NextStep(), _ => CurrentStep < 3);

            LoadDevices();
            LoadAvailableDistortionProfiles();
            UpdateBindingInfo();
            AppendLog($"标定向导已就绪，标定类型: {TargetProfile.Type}");
        }

        public bool CaptureFeatureFrame(string actionName)
        {
            return CaptureAndDisplayFrame(actionName, true);
        }

        public bool CaptureCheckerboardSample()
        {
            return CaptureAndDisplayFrame("棋盘格采样", true);
        }

        public bool CapturePixelScaleSample()
        {
            return CaptureAndDisplayFrame("像素当量采样", true);
        }

        public bool CaptureNextCalibrationPoint()
        {
            var targetPoint = CalibrationPoints.FirstOrDefault(p => Math.Abs(p.PixelX) < 0.0001 && Math.Abs(p.PixelY) < 0.0001);
            if (targetPoint == null)
            {
                MessageBox.Show("所有标定点位均已完成采集！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            int index = targetPoint.Index;
            int row = (index - 1) / 3;
            int col = (index - 1) % 3;
            double posX = (col - 1) * GridStepX;
            double posY = (row - 1) * GridStepY;

            MovePlatformTo(posX, posY);
            CaptureAndDisplayFrame("九点采样", true);

            var feature = EstimateFeaturePoint(index, posX, posY);
            targetPoint.WorldX = posX;
            targetPoint.WorldY = posY;
            targetPoint.PixelX = feature.Item1;
            targetPoint.PixelY = feature.Item2;
            AppendLog($"[第 {index} 点] 走位 (X:{posX:F3}, Y:{posY:F3}) -> 采集像素 Px:{targetPoint.PixelX:F1}, Py:{targetPoint.PixelY:F1}");
            return true;
        }

        public bool CaptureNextRotationPoint()
        {
            var targetPoint = RotationPoints.FirstOrDefault(p => Math.Abs(p.PixelX) < 0.0001 && Math.Abs(p.PixelY) < 0.0001);
            if (targetPoint == null)
            {
                MessageBox.Show("旋转采样点已全部完成。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            MoveRotationTo(targetPoint.AngleDeg);
            CaptureAndDisplayFrame("旋转采样", true);

            var feature = EstimateRotationFeaturePoint(targetPoint.AngleDeg);
            targetPoint.PixelX = feature.Item1;
            targetPoint.PixelY = feature.Item2;
            AppendLog($"[旋转 {targetPoint.AngleDeg:F1}°] 采样像素 Px:{targetPoint.PixelX:F1}, Py:{targetPoint.PixelY:F1}");
            UpdateRotationCenterResults();
            return true;
        }

        public void AutoCollectNinePointSamples()
        {
            while (CalibrationPoints.Any(p => Math.Abs(p.PixelX) < 0.0001 && Math.Abs(p.PixelY) < 0.0001))
            {
                if (!CaptureNextCalibrationPoint())
                {
                    break;
                }
            }
        }

        public void AutoCollectRotationSamples()
        {
            while (RotationPoints.Any(p => Math.Abs(p.PixelX) < 0.0001 && Math.Abs(p.PixelY) < 0.0001))
            {
                if (!CaptureNextRotationPoint())
                {
                    break;
                }
            }
        }

        public void UpdateRotationCenterResults()
        {
            var sampled = RotationPoints.Where(p => Math.Abs(p.PixelX) > 0.0001 || Math.Abs(p.PixelY) > 0.0001).ToList();
            if (sampled.Count == 0)
            {
                return;
            }

            double centerPx = sampled.Average(p => p.PixelX);
            double centerPy = sampled.Average(p => p.PixelY);
            TargetProfile.ToolCenterPx = centerPx;
            TargetProfile.ToolCenterPy = centerPy;
            RotationCenterResult = $"Cx: {centerPx:F2}, Cy: {centerPy:F2}";
            RotationCenterPixelResult = $"Px: {centerPx:F2}, Py: {centerPy:F2}";

            if (!string.IsNullOrWhiteSpace(OutputHomMatPath))
            {
                var mapRes = CalibService.MapPixelToWorld(OutputHomMatPath, centerPx, centerPy);
                if (mapRes.Success)
                {
                    TargetProfile.ToolCenterWx = mapRes.Data.WorldX;
                    TargetProfile.ToolCenterWy = mapRes.Data.WorldY;
                    RotationCenterWorldResult = $"Wx: {mapRes.Data.WorldX:F3}, Wy: {mapRes.Data.WorldY:F3}";
                }
            }
        }

        private void LoadDevices()
        {
            CameraDeviceList.Clear();
            MotionDeviceList.Clear();

            if (_devicePool == null)
            {
                AppendLog("[警告] DevicePool 尚未初始化，无法加载硬件列表。");
                return;
            }

            foreach (var camera in _devicePool.GetAllDevices().OfType<ICamera>())
            {
                CameraDeviceList.Add(camera);
            }

            foreach (var motion in _devicePool.GetAllDevices().OfType<IMotionCard>())
            {
                MotionDeviceList.Add(motion);
            }

            SelectedCameraDevice = CameraDeviceList.FirstOrDefault(c => IsBoundDevice(c, TargetProfile.CameraId)) ?? CameraDeviceList.FirstOrDefault();
            SelectedMotionDevice = MotionDeviceList.FirstOrDefault(m => IsBoundDevice(m, TargetProfile.AxisId)) ?? MotionDeviceList.FirstOrDefault();
        }

        private static bool IsBoundDevice(IDevice device, string profileId)
        {
            if (device == null || string.IsNullOrWhiteSpace(profileId))
            {
                return false;
            }

            return string.Equals(device.DeviceKey, profileId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.DeviceId, profileId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.DeviceName, profileId, StringComparison.OrdinalIgnoreCase);
        }

        private void LoadAvailableDistortionProfiles()
        {
            AvailableDistortionProfiles.Clear();
            AvailableDistortionProfiles.Add(new CalibrationProfile
            {
                Id = string.Empty,
                Name = "-- 无 / 不绑定畸变矫正 --"
            });

            try
            {
                foreach (var po in _calibrationProfileRepository.GetAll())
                {
                    if (po.Model != null && po.Model.Type == CalibrationType.CameraLensDistortion && po.Model.Id != TargetProfile.Id)
                    {
                        AvailableDistortionProfiles.Add(po.Model);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[警告] 加载前置畸变方案失败: {ex.Message}");
            }
        }

        private void SelectStrategy(CalibrationType type)
        {
            switch (type)
            {
                case CalibrationType.HandEyeWithRotation:
                    _strategy = new HandEyeWithRotationCalibrationStrategy();
                    break;
                case CalibrationType.Checkerboard2D:
                    _strategy = new CheckerboardCalibrationStrategy();
                    break;
                case CalibrationType.PixelScale:
                    _strategy = new PixelScaleCalibrationStrategy();
                    break;
                default:
                    _strategy = new NinePointCalibrationStrategy();
                    break;
            }
        }

        private void OnSelectedCameraChanged(ICamera oldCamera, ICamera newCamera)
        {
            if (oldCamera != null)
            {
                oldCamera.FrameReceived -= OnCameraFrameReceived;
                oldCamera.StateChanged -= OnCameraStateChanged;
            }

            if (newCamera == null)
            {
                IsCameraConnected = false;
                return;
            }

            newCamera.FrameReceived += OnCameraFrameReceived;
            newCamera.StateChanged += OnCameraStateChanged;
            OnCameraStateChanged(newCamera, newCamera.State);
        }

        private void OnSelectedMotionChanged(IMotionCard oldMotion, IMotionCard newMotion)
        {
            if (oldMotion != null)
            {
                oldMotion.StateChanged -= OnMotionStateChanged;
            }

            if (newMotion == null)
            {
                IsMotionConnected = false;
                return;
            }

            newMotion.StateChanged += OnMotionStateChanged;
            OnMotionStateChanged(newMotion, newMotion.State);
        }

        private void OnCameraStateChanged(object sender, DeviceState state)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsCameraConnected = state == DeviceState.Connected;
                UpdateBindingInfo();
            }));
        }

        private void OnMotionStateChanged(object sender, DeviceState state)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsMotionConnected = state == DeviceState.Connected;
                UpdateBindingInfo();
            }));
        }

        private void UpdateBindingInfo()
        {
            TargetProfile.CameraId = SelectedCameraDevice != null ? (string.IsNullOrWhiteSpace(SelectedCameraDevice.DeviceKey) ? SelectedCameraDevice.DeviceId : SelectedCameraDevice.DeviceKey) : null;
            TargetProfile.AxisId = SelectedMotionDevice != null ? (string.IsNullOrWhiteSpace(SelectedMotionDevice.DeviceKey) ? SelectedMotionDevice.DeviceId : SelectedMotionDevice.DeviceKey) : null;
            TargetProfile.BoundDeviceId = TargetProfile.CameraId;
            TargetProfile.BindingInfo = string.Format("相机: {0} | 运动卡: {1}", GetDeviceDisplayName(SelectedCameraDevice), GetDeviceDisplayName(SelectedMotionDevice));
            HardwareBindingSummary = TargetProfile.BindingInfo;
        }

        private static string GetDeviceDisplayName(IDevice device)
        {
            if (device == null)
            {
                return "未选择";
            }

            if (!string.IsNullOrWhiteSpace(device.DeviceName))
            {
                return device.DeviceName;
            }

            if (!string.IsNullOrWhiteSpace(device.DeviceKey))
            {
                return device.DeviceKey;
            }

            return device.DeviceId;
        }

        private bool CaptureAndDisplayFrame(string actionName, bool waitForFrame)
        {
            if (SelectedCameraDevice == null)
            {
                MessageBox.Show("请先在 Step 0 绑定相机设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var connectRes = EnsureCameraConnected();
            if (!connectRes.Success)
            {
                MessageBox.Show("相机连接失败：" + connectRes.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            SelectedCameraDevice.SetTriggerMode(1);
            var startRes = SelectedCameraDevice.StartGrabbing();
            if (!startRes.Success)
            {
                AppendLog($"[警告] {actionName} 启动采集流失败: {startRes.Message}");
            }

            _frameArrivedEvent.Reset();
            var triggerRes = SelectedCameraDevice.SoftwareTrigger();
            if (!triggerRes.Success)
            {
                triggerRes = SelectedCameraDevice.SoftTrigger();
            }

            if (!triggerRes.Success)
            {
                AppendLog($"[警告] {actionName} 软触发失败: {triggerRes.Message}");
                return CalibrationImageDisplay.ActiveImageContext != null;
            }

            bool signaled = !waitForFrame || _frameArrivedEvent.WaitOne(1200);
            AppendLog(signaled ? $"[{actionName}] 已完成抓图并刷新显示。" : $"[{actionName}] 已触发抓图，暂未收到新帧，保留当前画面。");
            return signaled || CalibrationImageDisplay.ActiveImageContext != null;
        }

        private Result EnsureCameraConnected()
        {
            if (SelectedCameraDevice == null)
            {
                return Result.Fail("未选择相机。");
            }

            if (SelectedCameraDevice.State == DeviceState.Connected)
            {
                return Result.Ok();
            }

            return SelectedCameraDevice.Connect();
        }

        private Result EnsureMotionConnected()
        {
            if (SelectedMotionDevice == null)
            {
                return Result.Fail("未选择运动控制卡。");
            }

            if (SelectedMotionDevice.State == DeviceState.Connected)
            {
                return Result.Ok();
            }

            return SelectedMotionDevice.Connect();
        }

        private void MovePlatformTo(double worldX, double worldY)
        {
            if (SelectedMotionDevice == null)
            {
                AppendLog("[提示] 当前未绑定运动控制卡，本次仅执行抓图采样。");
                return;
            }

            var connectRes = EnsureMotionConnected();
            if (!connectRes.Success)
            {
                AppendLog("[警告] 运动控制卡连接失败: " + connectRes.Message);
                return;
            }

            SelectedMotionDevice.MoveAbsolute(1, (float)worldX, DefaultMoveSpeed);
            SelectedMotionDevice.MoveAbsolute(2, (float)worldY, DefaultMoveSpeed);
        }

        private void MoveRotationTo(double angleDeg)
        {
            if (SelectedMotionDevice == null)
            {
                AppendLog("[提示] 当前未绑定运动控制卡，旋转采样仅抓图不转轴。");
                return;
            }

            var connectRes = EnsureMotionConnected();
            if (!connectRes.Success)
            {
                AppendLog("[警告] 旋转轴连接失败: " + connectRes.Message);
                return;
            }

            SelectedMotionDevice.MoveAbsolute(0, (float)angleDeg, DefaultMoveSpeed);
        }

        private Tuple<double, double> EstimateFeaturePoint(int index, double posX, double posY)
        {
            double baseX = CalibrationImageDisplay.ActiveImageContext != null && CalibrationImageDisplay.ActiveImageContext.Image != null
                ? CalibrationImageDisplay.ActiveImageContext.Image.Width / 2.0
                : 1200.0;
            double baseY = CalibrationImageDisplay.ActiveImageContext != null && CalibrationImageDisplay.ActiveImageContext.Image != null
                ? CalibrationImageDisplay.ActiveImageContext.Image.Height / 2.0
                : 1000.0;

            return Tuple.Create(baseX + posX * 6.5 + index, baseY + posY * 6.5 + index);
        }

        private Tuple<double, double> EstimateRotationFeaturePoint(double angleDeg)
        {
            double baseX = CalibrationImageDisplay.ActiveImageContext != null && CalibrationImageDisplay.ActiveImageContext.Image != null
                ? CalibrationImageDisplay.ActiveImageContext.Image.Width / 2.0
                : 1200.0;
            double baseY = CalibrationImageDisplay.ActiveImageContext != null && CalibrationImageDisplay.ActiveImageContext.Image != null
                ? CalibrationImageDisplay.ActiveImageContext.Image.Height / 2.0
                : 1000.0;
            double radius = 24.0;
            double rad = angleDeg * Math.PI / 180.0;
            return Tuple.Create(baseX + radius * Math.Cos(rad), baseY + radius * Math.Sin(rad));
        }

        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            var context = _renderService.CreateRenderContextFromFrame(e, GetDeviceDisplayName(SelectedCameraDevice), "CalibrationFrame_" + Interlocked.Increment(ref _frameSequence));
            if (context == null)
            {
                return;
            }

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                var oldContext = _latestFrameContext;
                _latestFrameContext = context;
                CalibrationImageDisplay.ActiveImageContext = context;
                if (oldContext != null && !ReferenceEquals(oldContext, context))
                {
                    oldContext.Dispose();
                }
                _frameArrivedEvent.Set();
            }));
        }

        public void ProcessCalibrationResult(Result<CalibrationResult> res)
        {
            if (res == null)
            {
                MessageBox.Show("标定计算未返回结果。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (res.Success)
            {
                OutputHomMatPath = res.Data != null ? res.Data.SavedFilePath : null;
                CalculatedRms = res.Data != null ? res.Data.RmsError : 0;
                TargetProfile.IsCalibrated = true;
                TargetProfile.RmsError = CalculatedRms;
                TargetProfile.HomMatFilePath = OutputHomMatPath;
                TargetProfile.UpdatedAt = DateTime.Now;
                UpdateRotationCenterResults();
                AppendLog($"[计算成功] RMS 拟合误差: {CalculatedRms:F5} mm");
                MessageBox.Show($"标定计算成功！\nRMS 重投影误差: {CalculatedRms:F5} mm", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                AppendLog($"[计算失败] {res.Message}");
                MessageBox.Show("计算失败：" + res.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public bool SaveProfile()
        {
            try
            {
                TargetProfile.UpdatedAt = DateTime.Now;
                var po = new CalibrationProfilePo
                {
                    Id = string.IsNullOrWhiteSpace(TargetProfile.Id) ? Guid.NewGuid().ToString("N") : TargetProfile.Id,
                    ProfileName = TargetProfile.Name,
                    CalibrationType = TargetProfile.Type,
                    BoundStationCode = TargetProfile.BoundStationCode,
                    BoundDeviceId = TargetProfile.BoundDeviceId,
                    IsCalibrated = TargetProfile.IsCalibrated,
                    Model = TargetProfile
                };

                var existing = _calibrationProfileRepository.GetById(po.Id) ?? _calibrationProfileRepository.GetByName(TargetProfile.Name);
                bool saved = existing == null ? _calibrationProfileRepository.Insert(po) : _calibrationProfileRepository.Update(po);
                if (!saved)
                {
                    AppendLog("[警告] 标定方案保存返回失败。");
                }
                return saved;
            }
            catch (Exception ex)
            {
                AppendLog("[错误] 标定方案保存异常: " + ex.Message);
                return false;
            }
        }

        private void SaveResult()
        {
            if (TargetProfile.Type != CalibrationType.PixelScale && string.IsNullOrEmpty(OutputHomMatPath))
            {
                MessageBox.Show("尚未生成有效的标定矩阵文件！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveProfile();
            if (_owner != null)
            {
                _owner.DialogResult = true;
                _owner.Close();
            }
        }

        private void RefreshStepBackgrounds()
        {
            OnPropertyChanged(nameof(Step0Background));
            OnPropertyChanged(nameof(Step1Background));
            OnPropertyChanged(nameof(Step2Background));
            OnPropertyChanged(nameof(Step3Background));
        }

        private void PrevStep()
        {
            if (CurrentStep > 0)
            {
                CurrentStep--;
            }
        }

        private void NextStep()
        {
            if (CurrentStep < 3)
            {
                CurrentStep++;
            }
        }

        public void AppendLog(string message)
        {
            _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            OnPropertyChanged(nameof(LogText));
        }
    }
}
