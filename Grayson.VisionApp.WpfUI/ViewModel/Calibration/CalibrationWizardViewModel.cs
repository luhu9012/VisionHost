//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationWizardViewModel.cs
// 功能：标定向导ViewModel，支持九点手眼标定、带旋转中心手眼、棋盘格标定、像素当量标定
// 流程：分步向导4个步骤，自动控制运动平台走位、相机采图、特征提取、矩阵计算、保存标定方案
//===================================================================================
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Models;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.ViewModel.Steps;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 标定向导 视图模型
    /// 支持类型：九点手眼标定、带旋转中心手眼标定、棋盘格畸变标定、像素当量标定
    /// 硬件依赖：工业相机 + 运动控制卡（XY平台+旋转轴）
    /// </summary>
    public class CalibrationWizardViewModel : ViewModelBase, IDisposable
    {
        /// <summary>平台运动默认速度</summary>
        private const float DefaultMoveSpeed = 50f;

        private readonly Window _owner;
        /// <summary>日志字符串缓存，界面绑定LogText展示全部日志</summary>
        private readonly StringBuilder _log = new StringBuilder();
        private readonly IDevicePool _devicePool;
        /// <summary>Halcon图像渲染服务，负责图像转WPF可显示的上下文</summary>
        private readonly HalconImageRenderService _renderService;
        /// <summary>标定方案仓储，读写数据库/本地配置</summary>
        private readonly ICalibrationProfileRepository _calibrationProfileRepository;
        /// <summary>相机帧同步事件：软触发之后等待相机返回一帧图像，超时控制</summary>
        private readonly AutoResetEvent _frameArrivedEvent = new AutoResetEvent(false);

        /// <summary>标定策略策略模式：不同标定类型实现各自逻辑</summary>
        private ICalibrationStepStrategy _strategy;
        /// <summary>最新一帧图像渲染上下文，用于界面图像显示</summary>
        private WpfImageRenderContext _latestFrameContext;
        /// <summary>图像帧序列号，每收到一帧自增</summary>
        private int _frameSequence;

        /// <summary>参数调节防抖计时器：滑块拖动停止 300ms 后才用新参数重试提取，避免拖动过程高频执行 Halcon 算子</summary>
        private readonly System.Windows.Threading.DispatcherTimer _retryExtractTimer;

        /// <summary>九点采样中被操作员主动跳过的点号集合（失败弹窗选"否"产生）：
        /// 自动采集循环不再反复尝试这些点；该点补采成功后从中移除，保证拟合前校验能拦截未采集点</summary>
        private readonly HashSet<int> _skippedPointIndices = new HashSet<int>();

        /// <summary>标定算法服务，底层Halcon封装，计算单应矩阵、像素转世界、畸变矫正</summary>
        public ICalibrationService CalibService { get; }

        /// <summary>当前正在编辑/执行的标定方案对象</summary>
        public CalibrationProfile TargetProfile { get; }

        /// <summary>WPF图像显示控件ViewModel，负责显示相机采集的图片、标定标记点</summary>
        public ImageDisplayVm CalibrationImageDisplay { get; }

        /// <summary>九点标定的标定点集合，一共9组：像素坐标 + 平台机械世界坐标</summary>
        public ObservableCollection<CalibrationPointModel> CalibrationPoints { get; } = new ObservableCollection<CalibrationPointModel>();

        /// <summary>旋转中心标定采样点集合：多个不同角度下标记点像素位置，用于求解旋转中心</summary>
        public ObservableCollection<RotationPointModel> RotationPoints { get; } = new ObservableCollection<RotationPointModel>();

        /// <summary>设备池内全部可用相机列表，界面下拉选择</summary>
        public ObservableCollection<ICamera> CameraDeviceList { get; } = new ObservableCollection<ICamera>();

        /// <summary>设备池内全部可用运动控制卡列表，界面下拉选择</summary>
        public ObservableCollection<IMotionCard> MotionDeviceList { get; } = new ObservableCollection<IMotionCard>();

        /// <summary>可用的镜头畸变矫正方案列表，做手眼标定时可以前置套用畸变参数</summary>
        public ObservableCollection<CalibrationProfile> AvailableDistortionProfiles { get; } = new ObservableCollection<CalibrationProfile>();

        private string _rotationCenterResult = "Cx: --, Cy: --";
        /// <summary>旋转中心结果字符串显示（UI绑定）</summary>
        public string RotationCenterResult
        {
            get => _rotationCenterResult;
            set => Set(ref _rotationCenterResult, value);
        }

        private string _rotationCenterPixelResult = "Px: --, Py: --";
        /// <summary>旋转中心【像素坐标】显示文本</summary>
        public string RotationCenterPixelResult
        {
            get => _rotationCenterPixelResult;
            set => Set(ref _rotationCenterPixelResult, value);
        }

        private string _rotationCenterWorldResult = "Wx: --, Wy: --";
        /// <summary>旋转中心【机械世界坐标】显示文本</summary>
        public string RotationCenterWorldResult
        {
            get => _rotationCenterWorldResult;
            set => Set(ref _rotationCenterWorldResult, value);
        }

        private ICamera _selectedCameraDevice;
        /// <summary>当前选中使用的相机设备</summary>
        public ICamera SelectedCameraDevice
        {
            get => _selectedCameraDevice;
            set
            {
                var oldCamera = _selectedCameraDevice;
                if (Set(ref _selectedCameraDevice, value))
                {
                    // 切换相机：注销旧相机事件，注册新相机帧接收事件
                    OnSelectedCameraChanged(oldCamera, value);
                    UpdateBindingInfo();
                }
            }
        }

        private IMotionCard _selectedMotionDevice;
        /// <summary>当前选中的运动控制卡</summary>
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
        /// <summary>相机是否已经连接成功（UI状态显示）</summary>
        public bool IsCameraConnected
        {
            get => _isCameraConnected;
            private set => Set(ref _isCameraConnected, value);
        }

        private bool _isMotionConnected;
        /// <summary>运动卡是否已经连接成功（UI状态显示）</summary>
        public bool IsMotionConnected
        {
            get => _isMotionConnected;
            private set => Set(ref _isMotionConnected, value);
        }

        private string _hardwareBindingSummary = "未绑定硬件";
        /// <summary>硬件绑定信息摘要文本，界面展示：相机名称 | 运动卡名称</summary>
        public string HardwareBindingSummary
        {
            get => _hardwareBindingSummary;
            set => Set(ref _hardwareBindingSummary, value);
        }

        /// <summary>九点标定网格X步长（毫米），平台每次移动X方向距离</summary>
        public double GridStepX { get; set; } = 100.0;
        /// <summary>九点标定网格Y步长（毫米），平台每次移动Y方向距离</summary>
        public double GridStepY { get; set; } = 100.0;

        private int _checkerboardRows = 7;
        /// <summary>棋盘格标定：内角点行数</summary>
        public int CheckerboardRows
        {
            get => _checkerboardRows;
            set => Set(ref _checkerboardRows, value);
        }

        private int _checkerboardCols = 7;
        /// <summary>棋盘格标定：内角点列数</summary>
        public int CheckerboardCols
        {
            get => _checkerboardCols;
            set => Set(ref _checkerboardCols, value);
        }

        private double _checkerboardSpacingMm = 10.0;
        /// <summary>棋盘格方格实际物理边长 单位mm</summary>
        public double CheckerboardSpacingMm
        {
            get => _checkerboardSpacingMm;
            set => Set(ref _checkerboardSpacingMm, value);
        }

        private double _measuredPixelDistance = 100.0;
        /// <summary>像素当量标定：图像上测量得到像素距离</summary>
        public double MeasuredPixelDistance
        {
            get => _measuredPixelDistance;
            set => Set(ref _measuredPixelDistance, value);
        }

        private double _knownPhysicalDistanceMm = 10.0;
        /// <summary>像素当量标定：物体真实物理距离mm</summary>
        public double KnownPhysicalDistanceMm
        {
            get => _knownPhysicalDistanceMm;
            set => Set(ref _knownPhysicalDistanceMm, value);
        }

        /// <summary>标定输出单应矩阵HomMat文件完整路径</summary>
        public string OutputHomMatPath { get; private set; }

        private double _calculatedRms;
        /// <summary>标定完成RMS重投影误差，单位mm；数值越小标定精度越高</summary>
        public double CalculatedRms
        {
            get => _calculatedRms;
            set => Set(ref _calculatedRms, value);
        }

        /// <summary>
        /// 特征提取算子参数（与 CalibService.ExtractOptions 同一实例）。
        /// 向导"特征配置"步骤滑块直接绑定其属性，调节后通过 ReapplyFeatureExtraction 实时重试。
        /// </summary>
        public FeatureExtractOptions ExtractOptions => CalibService.ExtractOptions;

        /// <summary>日志只读属性，UI文本框绑定</summary>
        public string LogText => _log.ToString();

        private int _currentStep;
        /// <summary>向导当前步骤索引：0、1、2、3，一共4步</summary>
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

        // 向导步骤进度条背景色
        private static readonly SolidColorBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
        private static readonly SolidColorBrush InactiveBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100));
        public SolidColorBrush Step0Background => CurrentStep >= 0 ? ActiveBrush : InactiveBrush;
        public SolidColorBrush Step1Background => CurrentStep >= 1 ? ActiveBrush : InactiveBrush;
        public SolidColorBrush Step2Background => CurrentStep >= 2 ? ActiveBrush : InactiveBrush;
        public SolidColorBrush Step3Background => CurrentStep >= 3 ? ActiveBrush : InactiveBrush;

        /// <summary>当前步骤操作提示文案，由策略返回不同标定类型提示</summary>
        public string StepGuideTip => _strategy != null ? _strategy.GetStepGuideTip(CurrentStep + 1) : string.Empty;

        // 轴选项数据模型
        public class AxisOption
        {
            public int AxisIndex { get; set; }
            public string DisplayName { get; set; }
        }

        // 供 Step 0 下拉绑定的轴集合
        public ObservableCollection<AxisOption> AvailableAxes { get; } = new ObservableCollection<AxisOption>
{
    new AxisOption { AxisIndex = 1, DisplayName = "1号轴：X轴（左右移动）" },
    new AxisOption { AxisIndex = 3, DisplayName = "3号轴：左工位Y轴（前后移动）" },
    new AxisOption { AxisIndex = 2, DisplayName = "2号轴：右工位Y轴（前后移动）" },
    new AxisOption { AxisIndex = 0, DisplayName = "0号轴：Z轴（升降/旋转轴）" }
};



        #region UI命令绑定
        /// <summary>设当前机台绝对位置为标定基准中心命令</summary>
        public ICommand SetCurrentAsBasePosCommand { get; }
        /// <summary>手动采集标定点采样命令</summary>
        public ICommand TriggerSampleCommand { get; }
        /// <summary>旋转中心手动采样命令</summary>
        public ICommand TriggerRotationSampleCommand { get; }
        /// <summary>全自动采集所有点位（平台自动走位+自动采图）</summary>
        public ICommand AutoRunAllCommand { get; }
        /// <summary>执行标定算法计算，求解单应矩阵/畸变参数</summary>
        public ICommand RunCalibrationCommand { get; }
        /// <summary>保存标定结果方案，关闭向导窗口</summary>
        public ICommand SaveResultCommand { get; }
        /// <summary>上一步向导</summary>
        public ICommand PrevStepCommand { get; }
        /// <summary>下一步向导</summary>
        public ICommand NextStepCommand { get; }
        #endregion

        /// <summary>标定向导构造函数</summary>
        /// <param name="owner">所属弹窗窗口</param>
        /// <param name="profile">传入已有标定方案；传null代表新建标定方案</param>
        public CalibrationWizardViewModel(Window owner, CalibrationProfile profile = null)
        {
            _owner = owner;
            _devicePool = App.StationHostRuntime != null ? App.StationHostRuntime.DevicePool : null;
            _renderService = new HalconImageRenderService();
            _calibrationProfileRepository = StorageFactory.CreateCalibrationProfileRepository();
            CalibService = new CalibrationService();
            CalibrationImageDisplay = new ImageDisplayVm(_renderService);

            // 参数调节防抖：停止拖动 300ms 后用新参数重试提取当前帧
            _retryExtractTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _retryExtractTimer.Tick += (s, e) =>
            {
                _retryExtractTimer.Stop();
                // 后台线程重试提取：调参时同样能看到算子逐步上屏的识别过程
                RunSamplingOnBackground(ReapplyFeatureExtractionCore);
            };

            // 如果没有传入旧方案，创建全新标定方案
            TargetProfile = profile ?? new CalibrationProfile { Id = Guid.NewGuid().ToString("N"), Name = "新建标定方案", Type = CalibrationType.NinePointHandEye };

            // 根据标定类型选择对应的策略实现
            SelectStrategy(TargetProfile.Type);
            // 策略初始化标定点集合（初始化9个点/旋转采样点）
            _strategy.InitializePoints(this);

            // 绑定所有UI命令
            TriggerSampleCommand = new RelayCommand(_ => _strategy.TriggerSample(this));
            TriggerRotationSampleCommand = new RelayCommand(_ => CaptureNextRotationPoint());
            AutoRunAllCommand = new RelayCommand(_ => _strategy.AutoRunAll(this));
            RunCalibrationCommand = new RelayCommand(_ => _strategy.ExecuteCalibration(this));
            SaveResultCommand = new RelayCommand(_ => SaveResult());
            PrevStepCommand = new RelayCommand(_ => PrevStep(), _ => CurrentStep > 0);
            NextStepCommand = new RelayCommand(_ => NextStep(), _ => CurrentStep < 3);
            SetCurrentAsBasePosCommand = new RelayCommand(_ => SetCurrentPositionAsBase());

            // 加载设备下拉列表、加载已经保存的畸变矫正方案
            LoadDevices();
            LoadAvailableDistortionProfiles();
            UpdateBindingInfo();

            // 窗口关闭自动调用Dispose释放相机、运动卡资源
            if (_owner != null)
            {
                _owner.Closed += (s, e) => Dispose();
            }

            AppendLog($"标定向导已就绪，标定类型: {TargetProfile.Type}");
        }

        /// <summary>
        /// 退出向导销毁资源：停止相机取流，断开相机、运动卡，释放同步事件、图像上下文
        /// 重要：向导弹窗关闭必须执行Dispose，否则相机句柄会泄漏占用
        /// </summary>
        public void Dispose()
        {
            // 1. 停止相机取流，注销帧接收事件，断开相机
            if (SelectedCameraDevice != null)
            {
                SelectedCameraDevice.FrameReceived -= OnCameraFrameReceived;
                SelectedCameraDevice.StateChanged -= OnCameraStateChanged;
                try
                {
                    SelectedCameraDevice.StopGrabbing();
                    // 向导退出释放相机连接；业务注意：如果外部业务还需要使用相机，则不要Disconnect，只StopGrabbing
                    SelectedCameraDevice.Disconnect();
                }
                catch (Exception ex)
                {
                    AppendLog($"[警告] 释放相机连接失败: {ex.Message}");
                }
            }

            // 2. 释放运动控制卡
            if (SelectedMotionDevice != null)
            {
                SelectedMotionDevice.StateChanged -= OnMotionStateChanged;
                try
                {
                    SelectedMotionDevice.Disconnect();
                }
                catch (Exception ex)
                {
                    AppendLog($"[警告] 释放运动卡连接失败: {ex.Message}");
                }
            }

            // 3.释放同步等待句柄、图像渲染内存资源
            _frameArrivedEvent?.Dispose();
            _latestFrameContext?.Dispose();
        }

        private void SetCurrentPositionAsBase()
        {
            if (SelectedMotionDevice == null)
            {
                MessageBox.Show("请先连接并选择运动控制卡！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 确保连接
            var connectRes = EnsureMotionConnected();
            if (!connectRes.Success)
            {
                MessageBox.Show("运动卡未连接: " + connectRes.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // 从底层运动卡读取当前绑定的 X/Y 轴位置
                var posXRes = SelectedMotionDevice.GetFeedbackPosition(TargetProfile.BindXAxisIndex);
                var posYRes = SelectedMotionDevice.GetFeedbackPosition(TargetProfile.BindYAxisIndex);

                if (posXRes.Success && posYRes.Success)
                {
                    TargetProfile.BasePosX = posXRes.Data;
                    TargetProfile.BasePosY = posYRes.Data;
                    AppendLog($"[基准设置成功] 绑定轴(X:{TargetProfile.BindXAxisIndex}, Y:{TargetProfile.BindYAxisIndex}) -> 基准坐标: X={TargetProfile.BasePosX:F3}, Y={TargetProfile.BasePosY:F3}");
                    MessageBox.Show($"基准位置设定成功！\nX: {TargetProfile.BasePosX:F3} mm\nY: {TargetProfile.BasePosY:F3} mm", "基准更新", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AppendLog($"[警告] 获取轴当前位置失败: {posXRes.Message} / {posYRes.Message}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] 读取轴位置异常: {ex.Message}");
            }
        }

        #region 对外供策略调用的采集接口

        /// <summary>采样任务防重入标志（0=空闲 1=执行中）：防止按钮连点/自动+手动并发驱动运动轴</summary>
        private int _samplingBusy;

        /// <summary>
        /// 把动作调度到 UI 线程执行（后台采样线程调用时使用）。
        /// ObservableCollection 增删、弹窗等 UI 亲和操作必须回到 UI 线程。
        /// </summary>
        public void RunOnUi(Action action)
        {
            if (action == null) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action);
            }
        }

        /// <summary>
        /// 在后台线程执行采样动作（走位 / 采图 / Halcon 提取）。
        /// 🌟 关键线程模型：算子提取绝不能在 UI 线程同步执行 —— UI 线程被占住时
        /// WPF 不泵消息、合成器不刷新，场景"逐步上屏"每一步都看不见（只有最终结果一闪而出），
        /// 轴到位等待的轮询 Sleep 还会卡死整个界面。
        /// 后台线程跑算子 + 每步绘制同步 Invoke 到 UI 线程（RunOnUiSync），UI 线程保持空闲泵消息：
        /// ① 屏幕上算子每执行一步立刻可见（HDevelop 体验）；② VS 断点单步时同样逐步可见；
        /// ③ 走位等待期间界面不卡、拖动缩放正常。
        /// </summary>
        public void RunSamplingOnBackground(Action samplingAction)
        {
            if (samplingAction == null) return;
            if (Interlocked.CompareExchange(ref _samplingBusy, 1, 0) != 0)
            {
                AppendLog("[提示] 已有采样任务正在执行，请等待其完成或中止后再操作。");
                return;
            }
            Task.Run(() =>
            {
                try
                {
                    samplingAction();
                }
                catch (Exception ex)
                {
                    AppendLog($"[错误] 采样流程异常: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _samplingBusy, 0);
                }
            });
        }

        /// <summary>
        /// 采集特征图像（通用）：抓图显示 + 按第二步"特征配置"选择的特征类型
        /// （圆形 Mark 提取圆心 / 十字 Mark 形状匹配）实时提取特征，
        /// 并利用 CalibrationService 注入的视窗句柄把特征标记叠加绘制到图像上。
        /// </summary>
        public bool CaptureFeatureFrame(string actionName)
        {
            bool captured = CaptureAndDisplayFrame(actionName, true);
            if (captured)
            {
                // 后台线程提取：特征预览时算子每步绘制即时上屏（与采样共用防重入锁，避免场景互相覆盖）
                RunSamplingOnBackground(ExtractFeatureAndAnnotate);
            }
            return captured;
        }

        /// <summary>
        /// 按用户选择的特征类型，在最新一帧图像上提取特征，
        /// 由 CalibrationService 通过注入的视窗句柄（DisplayContext）把识别过程与结果绘制到视图窗口。
        /// </summary>
        private void ExtractFeatureAndAnnotate()
        {
            // 读 _latestFrameContext（相机回调线程已登记的最新帧），而非 ActiveImageContext：
            // ActiveImageContext 由 Dispatcher 队列异步刷新，抓图流程在 UI 线程同步 WaitOne 返回后
            // 队列尚未消费，此时读 ActiveImageContext 拿到的是旧帧，特征标记会画错位置。
            var renderImage = _latestFrameContext?.Image;
            if (renderImage == null || renderImage.NativeHandle == null)
            {
                AppendLog("[特征提取] 当前无可用图像帧，跳过特征提取。");
                return;
            }

            string featureName = TargetProfile.FeatureType == CalibrationFeatureType.CrossMark
                ? "十字 Mark (形状匹配)"
                : "圆形 Mark (提取圆心)";

            try
            {
                // CalibrationService 内部解包 IRenderImage.NativeHandle 为 HObject 并提取特征、叠加绘制到视窗
                var res = CalibService.ExtractFeaturePreview(renderImage, TargetProfile.FeatureType);
                if (res.Success)
                {
                    AppendLog($"[特征提取] {featureName} 识别成功 → 中心 ({res.Data.PixelX:F1}, {res.Data.PixelY:F1})，已在视窗叠加标记。");
                    // 预览模式（CurrentStep <= 1）只确认特征、不采集：明确提示，避免操作员误以为坐标已写入列表
                    if (CurrentStep <= 1)
                    {
                        AppendLog("【提示】当前为特征预览模式，识别结果仅用于确认特征，不会写入标定点列表；请点击『下一步』进入数据采集步骤后采样。");
                    }
                }
                else
                {
                    AppendLog($"[特征提取] {featureName} 未识别: {res.Message}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[特征提取] {featureName} 执行异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 特征提取参数调节后的实时重试入口（滑块 ValueChanged 调用）。
        /// 防抖 300ms：连续拖动只触发一次真正的重试，避免高频执行 Halcon 算子卡 UI。
        /// </summary>
        public void ReapplyFeatureExtraction()
        {
            if (_retryExtractTimer == null) return;
            // 无帧时不启动防抖：避免页面初始加载时滑块绑定赋值触发一次无意义的空重试
            if (_latestFrameContext?.Image == null) return;
            _retryExtractTimer.Stop();
            _retryExtractTimer.Start();
        }

        /// <summary>
        /// 用最新参数在当前帧上重新提取特征并叠加标注（所见即所得调参）。
        /// 只做预览验证，不写入标定点数据。
        /// </summary>
        private void ReapplyFeatureExtractionCore()
        {
            var renderImage = _latestFrameContext?.Image;
            if (renderImage == null || renderImage.NativeHandle == null)
            {
                AppendLog("[参数重试] 当前无图像帧，请先在特征配置步骤点击『抓图并提取特征』。");
                return;
            }

            string featureName = TargetProfile.FeatureType == CalibrationFeatureType.CrossMark
                ? "十字 Mark (形状匹配)"
                : "圆形 Mark (提取圆心)";

            try
            {
                var res = CalibService.ExtractFeaturePreview(renderImage, TargetProfile.FeatureType);
                AppendLog(res.Success
                    ? $"[参数重试] 新参数识别成功 → 中心 ({res.Data.PixelX:F1}, {res.Data.PixelY:F1})，已更新视窗叠加标记。"
                    : $"[参数重试] 新参数下仍未识别: {res.Message}");
            }
            catch (Exception ex)
            {
                AppendLog($"[参数重试] 执行异常: {ex.Message}");
            }
        }

        /// <summary>棋盘格标定采集一张棋盘格图片</summary>
        public bool CaptureCheckerboardSample()
        {
            return CaptureAndDisplayFrame("棋盘格采样", true);
        }

        /// <summary>像素当量标定采集图像</summary>
        public bool CapturePixelScaleSample()
        {
            return CaptureAndDisplayFrame("像素当量采样", true);
        }

        /// <summary>
        /// 采集下一个九点标定点（支持断点续采 / 失败重试 / 失败跳过）：
        /// 1.计算平台XY目标机械坐标
        /// 2.控制平台移动到目标位置（内部等待轴到位）
        /// 3.相机软触发采图
        /// 4.Halcon 提取特征点（参考位置 ROI 失败自动降级全图搜索）——提取过程中
        ///   每个算子的结果即时上屏呈现：绿 ROI 框 → 蓝阈值层 → 紫连通域 → 橙候选 →
        ///   青 XLD 轮廓 → 红拟合圆 → 黑描边绿十字醒目标记（识别中心）
        /// 5.填充 CalibrationPoints 的世界坐标、像素坐标，并把所有已采集点以小绿十字
        ///   阵列叠加到视图窗口——识别正确时 9 个十字应构成与走位网格一致的规则 3x3
        ///   阵列，误检点表现为十字重叠/缺失/阵列畸变，操作员目视即可发现哪个像素点取错
        /// 失败交互：此时视图窗口已绘制失败诊断场景（绿 ROI 框 / 蓝阈值层 / 红色提示文字），
        /// 弹窗三选 —— 是=原地重试当前点（调光源/特征参数后）、否=跳过该点继续后面的点（稍后可补采）、
        /// 取消=中止采集。跳过的点不写数据，拟合前校验会拦截；调整后再次点击采集可从断点补采。
        /// </summary>
        public bool CaptureNextCalibrationPoint()
        {
            // 0. 校验基准位置已设置：基准坐标可能恰为 0（机台原点），不能以 BasePosX==0 判断。
            //    未设基准时第一点目标 = (-GridStepX, -GridStepY)，可能走出视野导致首点提取失败、坐标不回填。
            if (!TargetProfile.IsBasePosSet)
            {
                RunOnUi(() => MessageBox.Show("尚未设置标定基准位置！请返回步骤 1，点击『设当前轴位置为基准』，或在基准坐标框中手动输入 X/Y。",
                    "基准未设置", MessageBoxButton.OK, MessageBoxImage.Warning));
                return false;
            }

            int startIndex = 1;
            while (true)
            {
                // 从 startIndex 起找第一个"未采集且未被跳过"的点：天然支持断点续采
                var targetPoint = CalibrationPoints
                    .FirstOrDefault(p => !p.IsCaptured && p.Index >= startIndex && !_skippedPointIndices.Contains(p.Index));
                if (targetPoint == null)
                {
                    bool allDone = CalibrationPoints.All(p => p.IsCaptured);
                    RunOnUi(() => MessageBox.Show(allDone
                        ? "所有标定点位均已完成采集！"
                        : "剩余未采集的点均已跳过，请调整光源 / 特征参数 / 步长后重新点击采集以补采跳过的点。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information));
                    return false;
                }

                int index = targetPoint.Index;
                int row = (index - 1) / 3;
                int col = (index - 1) % 3;

                // 计算相对于基准点的网格偏移量 (-1, 0, 1) * Step
                double offsetX = (col - 1) * GridStepX;
                double offsetY = (row - 1) * GridStepY;

                // 目标绝对坐标计算：
                // EyeInHand(眼在手上): 相机动标定板静止，向右看Mark需要相机向左走 (X反向偏移)；
                // EyeToHand(眼在手外): 相机静止工作台动，工作台向右走Mark向右走 (正向偏移)。
                double posX = TargetProfile.EyeMode == EyeMode.EyeInHand ? (TargetProfile.BasePosX - offsetX) : (TargetProfile.BasePosX + offsetX);
                double posY = TargetProfile.BasePosY + offsetY;

                // 1. 平台驱动指定工位轴就地九点走位（内部等待两轴到位后再返回）
                MovePlatformTo(posX, posY);

                // 2. 相机抓图
                bool captured = CaptureAndDisplayFrame("九点采样", true);
                if (!captured)
                {
                    RunOnUi(() => MessageBox.Show($"第 {index} 点相机采图超时或失败！", "采图异常", MessageBoxButton.OK, MessageBoxImage.Error));
                    return false;
                }

                // 3. Halcon 提取特征点（失败时 EstimateFeaturePoint 内部已自动全图重试过一次）
                //    本方法在后台线程执行：UI 线程保持泵消息，提取过程中每个算子的绘制即时上屏可见
                //    期望位置由已采集点线性外推（传走位目标世界坐标），不再使用上一采集点的过期像素
                var feature = EstimateFeaturePoint(index, posX, posY);
                if (feature == null)
                {
                    // 视图窗口此刻就是失败诊断画面：绿色 ROI 框、蓝色阈值层、橙色候选、红色提示文字。
                    // 操作员可对照画面判断是 Mark 出视野、ROI 截断还是成像质量问题，再选择处理方式。
                    MessageBoxResult choice = MessageBoxResult.Cancel;
                    RunOnUi(() => choice = MessageBox.Show(
                        $"第 {index} 点未识别到有效 Mark 特征（已自动全图重试仍失败）。\n\n" +
                        "请对照视图窗口的失败诊断画面检查：\n" +
                        "· Mark 是否移出视野 / 半出视野（减小步长，或将基准位置设到 Mark 视野居中处）\n" +
                        "· 绿色 ROI 框是否截断了 Mark（增大特征配置里的搜索半径）\n" +
                        "· 光源 / 曝光 / 对焦是否在视野边缘劣化\n\n" +
                        "【是】原地重试当前点（调整光源 / 特征参数后）\n" +
                        "【否】跳过该点，继续采集后面的点（稍后可补采）\n" +
                        "【取消】中止本次采集",
                        "Mark 未检出", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning));
                    if (choice == MessageBoxResult.Yes)
                    {
                        AppendLog($"[第 {index} 点] 操作员选择原地重试。");
                        continue; // startIndex 不变 → 重新走位采图提取当前点
                    }
                    if (choice == MessageBoxResult.No)
                    {
                        AppendLog($"[第 {index} 点] 操作员选择跳过该点，继续后续点（拟合前需补采该点）。");
                        _skippedPointIndices.Add(index);
                        startIndex = index + 1;
                        continue;
                    }
                    AppendLog($"[第 {index} 点] 操作员中止采集流程，已完成 {CalibrationPoints.Count(p => p.IsCaptured)}/9 点。");
                    return false;
                }

                // 4. 识别结果呈现（不打断流程）：提取过程中每个算子的结果已即时上屏
                //    （绿 ROI → 蓝阈值 → 紫连通域 → 橙候选 → 青 XLD → 红拟合圆 →
                //    黑描边绿十字醒目标记），操作员对照视图窗口即可判断识别是否正确，
                //    无需弹框确认；发现误检可在采集完成后对该点重新采样覆盖。
                var capturedPixel = feature.Value;

                // 5. 回填真实机械绝对坐标与图像像素坐标，并标记该点已采集（UI 线程变更绑定属性）
                RunOnUi(() =>
                {
                    targetPoint.WorldX = posX;
                    targetPoint.WorldY = posY;
                    targetPoint.PixelX = capturedPixel.PixelX;
                    targetPoint.PixelY = capturedPixel.PixelY;
                    targetPoint.IsCaptured = true;
                });
                _skippedPointIndices.Remove(index); // 若该点此前被跳过过，补采成功即销账

                // 6. 已采集点标记叠加：把目前所有已成功采集的点以小绿十字阵列追加到
                //    当前场景（不清屏，叠加在刚完成的算子结果之上）。识别正确时十字
                //    阵列与走位网格一致（规则 3x3）；误检点表现为十字重叠（如两个不同
                //    世界坐标的点识别出几乎相同的像素位置）/ 缺失 / 阵列畸变，
                //    操作员目视即可发现哪个像素点取错。
                var capturedMarks = CalibrationPoints.Where(p => p.IsCaptured).ToList();
                CalibService.AppendCapturedMarks(
                    capturedMarks.Select(p => p.Index).ToArray(),
                    capturedMarks.Select(p => p.PixelX).ToArray(),
                    capturedMarks.Select(p => p.PixelY).ToArray());

                AppendLog($"[第 {index} 点] 轴(X:{TargetProfile.BindXAxisIndex}, Y:{TargetProfile.BindYAxisIndex}) 走位到 (X:{posX:F3}, Y:{posY:F3}) -> 识别像素 Px:{targetPoint.PixelX:F1}, Py:{targetPoint.PixelY:F1}");
                return true;
            }
        }

        /// <summary>
        /// 旋转中心采样：移动旋转轴到指定角度，拍照采样标记点像素位置
        /// 多个不同角度的像素点，用来拟合求解旋转中心
        /// </summary>
        public bool CaptureNextRotationPoint()
        {
            var targetPoint = RotationPoints.FirstOrDefault(p => !p.IsCaptured);
            if (targetPoint == null)
            {
                RunOnUi(() => MessageBox.Show("旋转采样点已全部完成。", "提示", MessageBoxButton.OK, MessageBoxImage.Information));
                return false;
            }

            // 旋转轴转到设定角度
            MoveRotationTo(targetPoint.AngleDeg);
            CaptureAndDisplayFrame("旋转采样", true);
            // 模拟提取旋转标记点像素（实际替换为图像特征检测）
            var feature = EstimateRotationFeaturePoint(targetPoint.AngleDeg);
            if (feature.HasValue)
            {
                var rot = feature.Value;
                RunOnUi(() =>
                {
                    targetPoint.PixelX = rot.PixelX;
                    targetPoint.PixelY = rot.PixelY;
                    targetPoint.IsCaptured = true;
                });
            }

            AppendLog($"[旋转 {targetPoint.AngleDeg:F1}°] 采样像素 Px:{targetPoint.PixelX:F1}, Py:{targetPoint.PixelY:F1}");
            // 采样完更新旋转中心计算结果UI（回 UI 线程）
            RunOnUi(UpdateRotationCenterResults);
            return true;
        }

        /// <summary>全自动采集全部九点标定点：循环直到全部9点采集完成（被跳过的点除外）；
        /// 某点识别失败时 CaptureNextCalibrationPoint 会弹窗交由操作员决策（重试/跳过/中止），
        /// 返回 false（中止/无点可采）时停止循环，已完成的数据保留供断点续采</summary>
        public void AutoCollectNinePointSamples()
        {
            while (CalibrationPoints.Any(p => !p.IsCaptured && !_skippedPointIndices.Contains(p.Index)))
            {
                if (!CaptureNextCalibrationPoint())
                {
                    break;
                }
            }
        }

        /// <summary>全自动采集全部旋转角度采样点</summary>
        public void AutoCollectRotationSamples()
        {
            while (RotationPoints.Any(p => !p.IsCaptured))
            {
                if (!CaptureNextRotationPoint())
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 更新旋转中心结果
        /// 逻辑：多个角度标记点像素坐标求平均，得到旋转中心像素；
        /// 如果已经有标定矩阵，则把像素中心转换为机械世界坐标
        /// ⚠️注意：简易平均算法；工程高精度场景需要用圆拟合算法替代简单Average
        /// </summary>
        public void UpdateRotationCenterResults()
        {
            var sampled = RotationPoints.Where(p => p.IsCaptured).ToList();
            if (sampled.Count == 0)
            {
                return;
            }

            // 当前实现：所有采样点像素求平均作为旋转中心（简易版本）
            double centerPx = sampled.Average(p => p.PixelX);
            double centerPy = sampled.Average(p => p.PixelY);

            // 保存到标定方案模型
            TargetProfile.ToolCenterPx = centerPx;
            TargetProfile.ToolCenterPy = centerPy;

            RotationCenterResult = $"Cx: {centerPx:F2}, Cy: {centerPy:F2}";
            RotationCenterPixelResult = $"Px: {centerPx:F2}, Py: {centerPy:F2}";

            // 如果已经生成单应矩阵，把像素中心点映射为平台机械世界坐标
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
        #endregion

        #region 硬件设备加载、切换设备事件

        /// <summary>从全局设备池加载相机、运动卡，填充下拉列表；自动匹配上次绑定的设备</summary>
        private void LoadDevices()
        {
            CameraDeviceList.Clear();
            MotionDeviceList.Clear();
            if (_devicePool == null)
            {
                AppendLog("[警告] DevicePool 尚未初始化，无法加载硬件列表。");
                return;
            }

            // 筛选全部相机、运动卡设备
            foreach (var camera in _devicePool.GetAllDevices().OfType<ICamera>())
            {
                CameraDeviceList.Add(camera);
            }
            foreach (var motion in _devicePool.GetAllDevices().OfType<IMotionCard>())
            {
                MotionDeviceList.Add(motion);
            }

            // 优先选中标定方案历史绑定的相机、运动卡；没有就取列表第一个
            SelectedCameraDevice = CameraDeviceList.FirstOrDefault(c => IsBoundDevice(c, TargetProfile.CameraId)) ?? CameraDeviceList.FirstOrDefault();
            SelectedMotionDevice = MotionDeviceList.FirstOrDefault(m => IsBoundDevice(m, TargetProfile.AxisId)) ?? MotionDeviceList.FirstOrDefault();
        }

        /// <summary>判断设备是否匹配标定方案保存的设备ID；兼容DeviceKey / DeviceId / DeviceName三种匹配</summary>
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

        /// <summary>加载已经保存的镜头畸变标定方案，手眼标定可以前置套用畸变矫正</summary>
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
                    // 只加载镜头畸变类型方案，排除当前正在编辑的方案
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

        /// <summary>根据标定类型选择对应的策略实现（策略模式）</summary>
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

        /// <summary>切换选中相机：注销旧相机事件订阅，注册新相机帧、状态事件</summary>
        private void OnSelectedCameraChanged(ICamera oldCamera, ICamera newCamera)
        {
            if (oldCamera != null)
            {
                oldCamera.FrameReceived -= OnCameraFrameReceived;
                oldCamera.StateChanged -= OnCameraStateChanged;
                try
                {
                    oldCamera.StopGrabbing();
                    oldCamera.Disconnect();
                }
                catch { }
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

        /// <summary>切换运动控制卡，注册状态变更事件</summary>
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

        /// <summary>相机设备状态变更回调；切Dispatcher到UI线程更新绑定属性</summary>
        private void OnCameraStateChanged(object sender, DeviceState state)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsCameraConnected = state == DeviceState.Connected;
                UpdateBindingInfo();
            }));
        }

        /// <summary>运动卡状态变更回调，UI线程更新状态</summary>
        private void OnMotionStateChanged(object sender, DeviceState state)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsMotionConnected = state == DeviceState.Connected;
                UpdateBindingInfo();
            }));
        }

        /// <summary>更新标定方案绑定硬件信息：记录当前相机、运动卡ID，更新界面显示字符串</summary>
        private void UpdateBindingInfo()
        {
            TargetProfile.CameraId = SelectedCameraDevice != null ? (string.IsNullOrWhiteSpace(SelectedCameraDevice.DeviceKey) ? SelectedCameraDevice.DeviceId : SelectedCameraDevice.DeviceKey) : null;
            TargetProfile.AxisId = SelectedMotionDevice != null ? (string.IsNullOrWhiteSpace(SelectedMotionDevice.DeviceKey) ? SelectedMotionDevice.DeviceId : SelectedMotionDevice.DeviceKey) : null;
            TargetProfile.BoundDeviceId = TargetProfile.CameraId;
            TargetProfile.BindingInfo = string.Format("相机: {0} | 运动卡: {1}", GetDeviceDisplayName(SelectedCameraDevice), GetDeviceDisplayName(SelectedMotionDevice));
            HardwareBindingSummary = TargetProfile.BindingInfo;
        }

        /// <summary>获取设备友好显示名称，优先DeviceName，其次DeviceKey，最后DeviceId</summary>
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
        #endregion

        #region 相机采集逻辑

        /// <summary>
        /// 相机采集一张图片，软触发，等待图像帧，渲染到UI图像控件
        /// waitForFrame=true 会等待AutoResetEvent收到帧信号，1200ms超时
        /// </summary>
        private bool CaptureAndDisplayFrame(string actionName, bool waitForFrame)
        {
            if (SelectedCameraDevice == null)
            {
                RunOnUi(() => MessageBox.Show("请先在 Step 0 绑定相机设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning));
                return false;
            }

            var connectRes = EnsureCameraConnected();
            if (!connectRes.Success)
            {
                RunOnUi(() => MessageBox.Show("相机连接失败：" + connectRes.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error));
                return false;
            }

            // 设置相机软触发模式，开始取流
            SelectedCameraDevice.SetTriggerMode(1);
            var startRes = SelectedCameraDevice.StartGrabbing();
            if (!startRes.Success)
            {
                AppendLog($"[警告] {actionName} 启动采集流失败: {startRes.Message}");
            }

            _frameArrivedEvent.Reset();
            // 软触发拍照，兼容两种API方法名 SoftwareTrigger / SoftTrigger
            var triggerRes = SelectedCameraDevice.SoftwareTrigger();
            if (!triggerRes.Success)
            {
                triggerRes = SelectedCameraDevice.SoftTrigger();
            }
            if (!triggerRes.Success)
            {
                AppendLog($"[警告] {actionName} 软触发失败: {triggerRes.Message}");
                // 触发失败，如果当前画面还有旧图，允许继续往下走
                return CalibrationImageDisplay.ActiveImageContext != null;
            }

            // 等待图像到达，最多等待1200ms
            bool signaled = !waitForFrame || _frameArrivedEvent.WaitOne(1200);
            AppendLog(signaled ? $"[{actionName}] 已完成抓图并刷新显示。" : $"[{actionName}] 已触发抓图，暂未收到新帧，保留当前画面。");
            return signaled || CalibrationImageDisplay.ActiveImageContext != null;
        }

        /// <summary>确保相机处于已连接状态；未连接则执行Connect()</summary>
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

        /// <summary>确保运动卡处于已连接状态</summary>
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
        #endregion

        #region 平台运动控制

        /// <summary>
        /// 平台XY轴绝对移动
        /// 轴定义注释：
        //AxisIndex = 控制器内部轴编号，AxisName = 轴实际物理含义/安装位置
        //AxisList.Add(new AxisInfoModel { AxisIndex = 1, AxisName = "1号轴：X轴（左右运动）" });
        //AxisList.Add(new AxisInfoModel { AxisIndex = 2, AxisName = "2号轴：右侧Y轴（前后运动）" });
        //AxisList.Add(new AxisInfoModel { AxisIndex = 3, AxisName = "3号轴：左侧Y轴（前后运动）" });
        //AxisList.Add(new AxisInfoModel { AxisIndex = 0, AxisName = "0号轴：Z轴（升降/旋转轴）" });
        /// 当前标定使用：轴1 = X方向；轴3 = Y方向
        /// </summary>
        /// <summary>
        /// 平台驱动指定轴绝对移动
        /// </summary>
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

            // 动态驱动方案绑定的 X 轴与 Y 轴
            SelectedMotionDevice.MoveAbsolute(TargetProfile.BindXAxisIndex, (float)worldX, DefaultMoveSpeed);
            SelectedMotionDevice.MoveAbsolute(TargetProfile.BindYAxisIndex, (float)worldY, DefaultMoveSpeed);

            // 等待两轴实际到位后再采图（替代原先固定 Thread.Sleep(200)）：
            // MoveAbsolute 是异步下发指令，步长大 / 速度低时 200ms 内轴仍在运动或减速震荡，
            // 拍到的 Mark 处于拖尾 / 模糊状态 → 圆度骤减被几何筛掉，表现为"后面几个点识别不了"。
            WaitAxesIdle(TargetProfile.BindXAxisIndex, TargetProfile.BindYAxisIndex);
        }

        /// <summary>
        /// 轮询等待指定轴全部空闲（到位），到位后再留 100ms 稳定时间消除残余震荡。
        /// 运动卡不支持 IsAxisIdle 查询（返回失败）或超时（默认 8s）时降级为固定等待并记日志。
        /// </summary>
        private void WaitAxesIdle(params int[] axes)
        {
            const int timeoutMs = 8000;
            if (SelectedMotionDevice == null || axes == null || axes.Length == 0)
            {
                return;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    bool allIdle = true;
                    foreach (var axis in axes)
                    {
                        var idleRes = SelectedMotionDevice.IsAxisIdle(axis);
                        if (!idleRes.Success)
                        {
                            // 查询接口不可用：降级固定等待，不再空转超时
                            AppendLog($"[提示] 运动卡不支持轴 [{axis}] 到位查询，降级固定等待 500ms。");
                            Thread.Sleep(500);
                            return;
                        }
                        if (!idleRes.Data)
                        {
                            allIdle = false;
                            break;
                        }
                    }
                    if (allIdle)
                    {
                        Thread.Sleep(100); // 到位后稳定片刻（消除减速末端残余震荡）
                        return;
                    }
                    Thread.Sleep(20);
                }
                AppendLog($"[警告] 等待轴({string.Join("/", axes)})到位超时({timeoutMs}ms)，继续执行采图。");
            }
            catch (Exception ex)
            {
                AppendLog($"[警告] 轴到位查询异常({ex.Message})，降级固定等待 500ms。");
                Thread.Sleep(500);
            }
        }


        /// <summary>旋转轴绝对运动，轴0为旋转轴，角度单位度</summary>
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
            // 等待旋转轴到位后再采图，避免拍到运动中的 Mark
            WaitAxesIdle(0);
        }
        #endregion


        #region 特征点提取（委托 HalconWrapper.Calibration 服务）

        /// <summary>
        /// 提取九点标定特征标记点像素坐标（严谨 Halcon 真实提取，不使用假数据）
        /// </summary>
        /// <param name="index">当前采样点编号（1~9）</param>
        /// <param name="worldX">当前采样点目标世界坐标 X（走位目标）</param>
        /// <param name="worldY">当前采样点目标世界坐标 Y（走位目标）</param>
        private (double PixelX, double PixelY)? EstimateFeaturePoint(int index, double worldX, double worldY)
        {
            // 用 _latestFrameContext（最新帧），理由同 ExtractFeatureAndAnnotate
            var imageObj = _latestFrameContext?.Image;
            if (imageObj == null)
            {
                AppendLog($"[错误] 第 {index} 点提取失败：当前图像上下文为空。");
                return null;
            }

            // 期望位置 = 由"已采集点"按轴位移率线性外推当前点的像素位置。
            // ⚠ 不能用"上一个采集点的像素"——走位后 Mark 已位移 步长/像素当量 个像素
            // （通常数百 px，远超 SearchRadius），旧位置 ± SearchRadius 的 ROI 根本不含
            // 真实 Mark，"离旧位置最近"反而会把反光点等伪特征选回来（此前 8 点全偏的根因）。
            // 外推依据：像素≈世界的线性关系正是待标定量，但用已采集点在线估计位移率即可
            // 预测后续点（3,5~9 点均有预测；1/2/4 点缺样本自动全图搜索）。
            var predicted = PredictExpectedPixel(worldX, worldY);
            double seedPx = predicted?.Px ?? -1;
            double seedPy = predicted?.Py ?? -1;

            // 调用底层 Halcon 服务真实提取（按第二步配置的特征类型路由：圆 Mark / 十字 Mark，
            // 保证采样与预览使用同一套算法——此前这里写死圆算法，选十字 Mark 时采样必然失败）
            var extractRes = CalibService.ExtractFeaturePointByType(imageObj, TargetProfile.FeatureType, index, seedPx, seedPy);
            if (!extractRes.Success && (seedPx > 0 || seedPy > 0))
            {
                // 参考位置 ROI 搜索失败 → 自动降级全图搜索重试一次（仍携带 seed）：
                // seed 只是"缩小搜索范围"的引导，不应成为失败原因：局部 ROI 常因
                // 走位步长的像素位移大于 SearchRadius、或 Mark 靠近视野边缘被 ROI 截断而错过目标。
                // 全图搜索跳过 ROI 裁剪但保留 seed 做多候选择近——若不带 seed，
                // 全图多候选时取第一个极易选中伪特征（此前 RMS 过大的直接根因之一）。
                AppendLog($"[第 {index} 点] 参考位置 ROI 搜索未命中，自动降级全图搜索重试（建议检查 SearchRadius 是否覆盖走位步长的像素位移）...");
                extractRes = CalibService.ExtractFeaturePointByType(imageObj, TargetProfile.FeatureType, index, seedPx, seedPy, forceFullImage: true);
                if (extractRes.Success)
                {
                    AppendLog($"[第 {index} 点] 全图搜索重试识别成功。");
                }
            }
            if (extractRes.Success)
            {
                // 预测可用时打印"预测 vs 识别"偏差：偏差小说明外推与识别互相印证；
                // 偏差大（如超 200px）提示该点可能选中伪特征，操作员应目视确认视图窗口
                if (predicted.HasValue)
                {
                    double dev = Math.Sqrt(
                        DistanceSq(extractRes.Data.PixelX - predicted.Value.Px, extractRes.Data.PixelY - predicted.Value.Py));
                    AppendLog(dev > 200
                        ? $"[第 {index} 点] ⚠ 识别像素({extractRes.Data.PixelX:F0},{extractRes.Data.PixelY:F0}) 偏离预测({predicted.Value.Px:F0},{predicted.Value.Py:F0}) {dev:F0}px，请目视确认是否伪特征！"
                        : $"[第 {index} 点] 识别像素({extractRes.Data.PixelX:F0},{extractRes.Data.PixelY:F0}) 与预测偏差 {dev:F1}px");
                }
                return (extractRes.Data.PixelX, extractRes.Data.PixelY);
            }

            AppendLog($"[警告] 第 {index} 点特征提取失败: {extractRes.Message}");
            return null;
        }

        /// <summary>平方距离（避免开方）</summary>
        private static double DistanceSq(double dx, double dy)
        {
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// 用已采集点（世界坐标→像素坐标）线性外推目标点的期望像素位置。
        /// 逐轴估计位移率：取世界坐标差最大的已采集点对（最稳定），
        /// dPixel/dWorld 各 4 个分量（Px/Wx、Py/Wx、Px/Wy、Py/Wy）。
        /// 目标点在某轴上相对参考点有位移、但该轴还没有任何位移样本时无法预测 → 返回 null（全图搜索）。
        /// 预测值明显出视野（负坐标）也返回 null。
        /// </summary>
        private (double Px, double Py)? PredictExpectedPixel(double worldX, double worldY)
        {
            var captured = CalibrationPoints.Where(p => p.IsCaptured).ToList();
            if (captured.Count == 0)
            {
                return null;
            }

            var refPoint = captured[0];
            double dPxPerWx = EstimateAxisRate(captured, p => p.WorldX, p => p.PixelX);
            double dPyPerWx = EstimateAxisRate(captured, p => p.WorldX, p => p.PixelY);
            double dPxPerWy = EstimateAxisRate(captured, p => p.WorldY, p => p.PixelX);
            double dPyPerWy = EstimateAxisRate(captured, p => p.WorldY, p => p.PixelY);

            double dx = worldX - refPoint.WorldX;
            double dy = worldY - refPoint.WorldY;
            const double Eps = 1e-6;
            bool needX = Math.Abs(dx) > Eps;
            bool needY = Math.Abs(dy) > Eps;

            // 目标点在 X/Y 轴上有位移，但该轴还没有任何已采集样本 → 无法预测
            if (needX && double.IsNaN(dPxPerWx)) return null;
            if (needY && double.IsNaN(dPxPerWy)) return null;

            double px = refPoint.PixelX + (needX ? dx * dPxPerWx : 0) + (needY ? dy * dPxPerWy : 0);
            double py = refPoint.PixelY + (needX ? dx * dPyPerWx : 0) + (needY ? dy * dPyPerWy : 0);

            // 预测位置明显出视野 → 外推不可信，退回全图搜索
            if (px < 0 || py < 0)
            {
                return null;
            }
            return (px, py);
        }

        /// <summary>
        /// 估计某世界轴 → 某像素分量的位移率 dPixel/dWorld：
        /// 遍历所有已采集点对，取世界坐标差最大的一对计算（差值越大除法越稳定）。
        /// 无有效点对（仅 1 个采集点 / 该轴世界坐标全部相同）返回 NaN。
        /// </summary>
        private static double EstimateAxisRate(
            List<CalibrationPointModel> points,
            Func<CalibrationPointModel, double> worldValue,
            Func<CalibrationPointModel, double> pixelValue)
        {
            double bestAbsDw = 0;
            double rate = double.NaN;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    double dw = worldValue(points[i]) - worldValue(points[j]);
                    if (Math.Abs(dw) > bestAbsDw && Math.Abs(dw) > 1e-6)
                    {
                        bestAbsDw = Math.Abs(dw);
                        rate = (pixelValue(points[i]) - pixelValue(points[j])) / dw;
                    }
                }
            }
            return rate;
        }

        /// <summary>
        /// 提取旋转采样标记点像素坐标（真实提取）
        /// </summary>
        private (double PixelX, double PixelY)? EstimateRotationFeaturePoint(double angleDeg)
        {
            // 用 _latestFrameContext（最新帧），理由同 ExtractFeatureAndAnnotate
            var imageObj = _latestFrameContext?.Image;
            if (imageObj == null)
            {
                AppendLog($"[错误] 旋转 {angleDeg:F1}° 采样失败：当前图像上下文为空。");
                return null;
            }

            // 真实提取，不硬编码估算（按第二步配置的特征类型路由：圆 Mark / 十字 Mark）
            var extractRes = CalibService.ExtractRotationFeaturePoint(imageObj, angleDeg, TargetProfile.FeatureType, -1, -1);
            if (extractRes.Success)
            {
                return (extractRes.Data.PixelX, extractRes.Data.PixelY);
            }

            AppendLog($"[警告] 旋转 {angleDeg:F1}° 特征提取失败: {extractRes.Message}");
            return null;
        }

        #endregion

        /// <summary>
        /// 相机帧接收事件回调：
        /// 收到相机原始帧，创建渲染上下文，交给WPF图像控件显示；
        /// 设置AutoResetEvent信号，通知采集函数：图像已经到达
        /// </summary>
        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            var context = _renderService.CreateRenderContextFromFrame(e, GetDeviceDisplayName(SelectedCameraDevice), "CalibrationFrame_" + Interlocked.Increment(ref _frameSequence));
            if (context == null)
            {
                return;
            }

            // 关键：必须在相机回调线程（非 UI 线程）直接登记最新帧并唤醒等待采集的线程。
            // 若把 Set() 放进 Dispatcher.BeginInvoke，而采集线程正在 UI 线程上同步
            // WaitOne(1200) 阻塞，Dispatcher 队列永远不会被消费，Set() 永远不执行，
            // 导致每次采集都等满 1200ms 超时（captured 只能靠旧图兜底）。
            var oldContext = Interlocked.Exchange(ref _latestFrameContext, context);

            // 同步更新 ActiveImageContext（在回调线程直接赋值，而非 BeginInvoke）：
            // 1) setter 仅触发 PropertyChanged / OnRequestRender / 日志，均线程安全
            //    （WPF 绑定与 HalconImageDisplayHost.Display 内部都会 marshal 回 UI 线程）；
            // 2) 这样"图像渲染任务"（DispObj 底图）立即入队 Dispatcher，
            //    特征叠加绘制（DrawOnWindow 的 BeginInvoke）必然排在其后执行，
            //    不会被渲染任务覆盖 —— 否则表现为"叠加画了但看不到"。
            CalibrationImageDisplay.ActiveImageContext = context;

            // 唤醒等待采集的线程
            _frameArrivedEvent.Set();

            // 旧帧内存释放放回 UI 线程，避免在相机回调线程释放 Halcon 对象
            if (oldContext != null && !ReferenceEquals(oldContext, context))
            {
                var toDispose = oldContext;
                Application.Current.Dispatcher.BeginInvoke(new Action(() => toDispose.Dispose()));
            }
        }

        /// <summary>
        /// 标定算法计算完成回调入口
        /// res：标定算法返回结果对象，包含单应矩阵文件路径、RMS重投影误差
        /// RMS重投影误差含义：把标定点像素代入矩阵反算世界坐标，对比真实机械坐标的平均误差；单位mm，越小精度越高
        /// </summary>
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
                // 回填标定方案属性
                TargetProfile.IsCalibrated = true;
                TargetProfile.RmsError = CalculatedRms;
                TargetProfile.HomMatFilePath = OutputHomMatPath;
                TargetProfile.UpdatedAt = DateTime.Now;
                // 如果是带旋转标定，计算旋转中心世界坐标
                UpdateRotationCenterResults();

                AppendLog($"[计算成功] RMS 拟合误差: {CalculatedRms:F5} mm");
                if (CalculatedRms > 1.0)
                {
                    // RMS 超过 1mm 几乎必然有数据质量问题——提示用户查日志定位坏点
                    AppendLog($"⚠ RMS={CalculatedRms:F2}mm 过大！常见原因："
                        + "\n  1) 某些点 Mark 识别命中了错误位置（中心亮边缘暗→阈值分割到噪声而非 Mark）"
                        + "\n  2) 程序未重新生成（旧代码有坐标偏移 bug / 未做灰度转换 / 第三步写死圆算法）"
                        + "\n  3) 步长 100mm 超出相机视野——Mark 走出半视野时圆度骤降被筛掉，兜底动态阈值可能抓到噪声"
                        + "\n  → 请对照上方逐点数据，检查哪个点的 Pixel 坐标明显偏离网格规律");
                    MessageBox.Show(
                        $"标定计算完成，但 RMS={CalculatedRms:F2}mm 过大（正常应 <0.1mm）！\n\n"
                        + "常见原因：\n"
                        + "1. 程序未重新生成——旧代码有坐标偏移 bug、未做灰度转换、第三步写死圆算法\n"
                        + "2. 某些点 Mark 识别命中了噪声而非真正的 Mark（中心亮边缘暗→全局阈值失效）\n"
                        + "3. 步长 100mm 可能超出相机视野——Mark 走出半视野时识别不准\n\n"
                        + "请查看向导日志中「九点标定拟合数据」的逐点 Pixel 坐标，"
                        + "检查哪个点的像素坐标明显偏离 3×3 网格规律。",
                        "RMS 过大警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show($"标定计算成功！\nRMS 重投影误差: {CalculatedRms:F5} mm", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else
            {
                AppendLog($"[计算失败] {res.Message}");
                MessageBox.Show("计算失败：" + res.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>保存标定方案到仓储（数据库/配置文件）</summary>
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
                // 存在则更新，不存在插入
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

        /// <summary>保存标定结果，关闭标定向导弹窗</summary>
        private void SaveResult()
        {
            // 像素当量标定不需要HomMat矩阵文件；其余标定必须要有矩阵文件
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

        /// <summary>刷新向导步骤进度UI属性通知</summary>
        private void RefreshStepBackgrounds()
        {
            OnPropertyChanged(nameof(Step0Background));
            OnPropertyChanged(nameof(Step1Background));
            OnPropertyChanged(nameof(Step2Background));
            OnPropertyChanged(nameof(Step3Background));
        }

        /// <summary>向导上一步</summary>
        private void PrevStep()
        {
            if (CurrentStep > 0)
            {
                CurrentStep--;
            }
        }

        /// <summary>向导下一步</summary>
        private void NextStep()
        {
            if (CurrentStep < 3)
            {
                CurrentStep++;
            }
        }

        /// <summary>写入日志，自动带上时间，通知UI更新LogText</summary>
        public void AppendLog(string message)
        {
            // 线程安全：采样流程在后台线程跑，日志 UI 更新统一回 UI 线程
            RunOnUi(() =>
            {
                _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                OnPropertyChanged(nameof(LogText));
            });
        }

        #region 标定显示上下文（句柄模式）

        private ICalibrationDisplayContext _calibrationDisplayContext;

        /// <summary>
        /// 标定显示上下文（Halcon 视图窗口句柄）。
        /// 由 View 在 HalconImageDisplayHost 控件加载完成后注入（该控件实现 ICalibrationDisplayContext）。
        /// 注入后同步给 CalibrationService，使其提取特征时可直接用 Halcon 原生算子
        /// 把 ROI / 候选区域 / 拟合圆 / 十字 / 文字标注精细绘制到标定图像窗口。
        /// </summary>
        public ICalibrationDisplayContext CalibrationDisplayContext
        {
            get => _calibrationDisplayContext;
            set
            {
                if (Set(ref _calibrationDisplayContext, value) && CalibService != null)
                {
                    CalibService.DisplayContext = value;
                    AppendLog(value != null ? "标定显示上下文已注入：特征识别过程将实时叠加到视图窗口。" : "标定显示上下文已解除。");
                }
            }
        }

        #endregion
    }
}