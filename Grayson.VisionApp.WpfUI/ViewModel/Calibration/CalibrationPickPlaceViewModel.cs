//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationPickPlaceViewModel.cs
// 说 明: 「点哪去哪」轻量校验会话 ViewModel（2026-09-10，为 ST_002 同心吸嘴 + 固定相机的
//        眼在手外(EyeToHand)工位重做）。与旧的 CalibrationVerifierViewModel 相比：
//          · 剥离对针/吸嘴锚点(R_n)/对准像素(p_tip)/平移差分自检/偏心 e/旋转中心 O 等
//            全部复杂逻辑 —— 这些对"同心吸嘴 + 固定相机"而言是多余的。
//          · 只干一件事：抓拍/实时 → 在图上点一个像素点 → H(u) 即吸点机械坐标 →
//            低速到位（吸嘴去吸这个点）→ 双向可视化。
//
//        坐标语义（关键，本工位唯一真源）：
//          同心吸嘴（吸嘴与 Z 旋转轴同心）⇒ 无偏心 e、无旋转中心偏移 O；
//          固定上相机做引导抓取、固定下相机做纠偏 ⇒ 眼在手外(EyeToHand)；
//          九点标定 H 拟合的是「像素 ↔ 机械手命令位」⇒ H(u) 的裸输出 w 就是
//          「让特征成像在像素 u 时，吸嘴应去到的机械 XY」。因此：
//              吸点坐标 = H(u_click) = MapPixelToWorld(u_click)
//          无任何附加修正项。这是与旧校验台「X_obj = P_photo + O − H(u)」的本质区别。
//
//        双向可视化：
//          ① 正向：点像素 u → 显示 H(u) 机械坐标 + 图上画绿十字标注该点；
//          ② 反向：手输机械坐标 (X,Y) → H⁻¹ 反投影成像素 → 图上画青十字，肉眼比对
//             特征是否落在十字上；也支持「读当前位」直接反投影看吸嘴现在该在哪个像素。
//
//        取流自愈：与旧校验台/对针台同款约定（自动连接 + 临时连续模式 + 首帧看门狗 + 收尾恢复）。
//===================================================================================
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Interfaces;
using Grayson.Vision.Contracts.Devices.Models;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.HalconWrapper.Wpf.Controls;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 「点哪去哪」轻量校验会话 VM：绑定一个 CalibrationProfile，驱动相机/运动卡做
    /// 「点像素 → 吸嘴去吸该点」验收。同心吸嘴 + 固定相机(EyeToHand)语义下，
    /// 吸点坐标 = H(u)，无偏心/旋转/对针修正。
    /// </summary>
    public class CalibrationPickPlaceViewModel : ViewModelBase
    {
        private readonly ICalibrationService _calibService;
        private readonly IDevicePool _devicePool;
        private readonly HalconImageRenderService _renderService = new HalconImageRenderService();
        private CalibrationMotionFacade _facade;

        // ---- 相机取流会话状态（与旧校验台/对针台同款自愈约定） ----
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private TaskCompletionSource<bool> _firstFrameTcs;   // 首帧信号
        private bool _weStartedGrabbing;                     // 本会话自己发起且正在运行的取流
        private bool _streamActive;                          // 处于"已请求出帧"
        private int? _triggerModeToRestore;                  // 打开前触发模式（临时切连续，收尾恢复）
        private DateTime _lastLiveRenderAt = DateTime.MinValue; // 实时帧上屏节流（~20fps）
        private bool _sessionFirstFramePending;                 // 本次取流首帧必须上屏
        private (double X, double Y, double Z, double U)? _photoPose; // 抓拍定格瞬间机械位

        /// <summary>取流过程中禁用设备下拉切换</summary>
        public bool IsDeviceSwitchEnabled => !IsBusy;

        public CalibrationProfile Profile { get; }

        /// <summary>图像显示（HalconImageDisplayHost 的 DataContext）</summary>
        public ImageDisplayVm ImageDisplay { get; }

        // ==================== 设备 ====================

        public ObservableCollection<ICamera> CameraDeviceList { get; } = new ObservableCollection<ICamera>();
        public ObservableCollection<IMotionCard> MotionDeviceList { get; } = new ObservableCollection<IMotionCard>();

        private ICamera _selectedCamera;
        public ICamera SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                var old = _selectedCamera;
                if (Set(ref _selectedCamera, value))
                {
                    OnSelectedCameraChanged(old, value);
                    RaiseCanExecutes();
                }
            }
        }

        private IMotionCard _selectedMotion;
        public IMotionCard SelectedMotion
        {
            get => _selectedMotion;
            set
            {
                if (Set(ref _selectedMotion, value))
                {
                    OnSelectedMotionChanged(value);
                    RaiseCanExecutes();
                }
            }
        }

        // ==================== 会话状态 ====================

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (Set(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsDeviceSwitchEnabled));
                    RaiseCanExecutes();
                }
            }
        }

        /// <summary>矩阵文件是否存在（校验可用主开关）</summary>
        public string MatrixPath { get; private set; }
        public bool IsMatrixReady => !string.IsNullOrEmpty(MatrixPath);

        private string _sessionNote;
        /// <summary>顶部状态条</summary>
        public string SessionNote
        {
            get => _sessionNote;
            set => Set(ref _sessionNote, value);
        }

        private string _pickInfoText = "尚未点选 —— 抓拍/实时后，在画面上点击要吸的目标点。";
        /// <summary>点选反馈（像素 + 吸点机械坐标）</summary>
        public string PickInfoText
        {
            get => _pickInfoText;
            set => Set(ref _pickInfoText, value);
        }

        private bool _hasPick;
        public bool HasPick
        {
            get => _hasPick;
            set { if (Set(ref _hasPick, value)) RaiseCanExecutes(); }
        }

        private double _pickCol;
        private double _pickRow;
        private double _pickWorldX;
        private double _pickWorldY;

        private string _currentPosText = "--";
        /// <summary>当前轴位显示</summary>
        public string CurrentPosText
        {
            get => _currentPosText;
            set => Set(ref _currentPosText, value);
        }

        public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();

        // ==================== 反向可视化（机械 → 像素） ====================

        private string _targetXText;
        /// <summary>反向目标机位 X（mm；可手输，也可【读当前位】回填）</summary>
        public string TargetXText
        {
            get => _targetXText;
            set
            {
                if (Set(ref _targetXText, value))
                {
                    RaiseCanExecutes();
                    RedrawReverseOnInputChanged();
                }
            }
        }

        private string _targetYText;
        /// <summary>反向目标机位 Y（mm）</summary>
        public string TargetYText
        {
            get => _targetYText;
            set
            {
                if (Set(ref _targetYText, value))
                {
                    RaiseCanExecutes();
                    RedrawReverseOnInputChanged();
                }
            }
        }

        private string _reverseInfoText = "填目标机械坐标(或【读当前位】)→ 图上画青十字 = 该机械位对应的像素位置。";
        /// <summary>反向可视化结论</summary>
        public string ReverseInfoText
        {
            get => _reverseInfoText;
            set => Set(ref _reverseInfoText, value);
        }

        private double _reversePredictCol = double.NaN;
        private double _reversePredictRow = double.NaN;
        private double _reversePoseX = double.NaN;   // 反投影所用机械位（比对基准）
        private double _reversePoseY = double.NaN;

        // ---- 图上标记（VM 产出 → 视图层宿主绘制） ----

        /// <summary>要绘制的标记（视图层订阅 MarkersInvalidated 后按此集合重画）</summary>
        public ObservableCollection<GeoMarkerItem> Markers { get; } = new ObservableCollection<GeoMarkerItem>();

        /// <summary>标记集合已变更 → 视图层应清空宿主标记并按集合重画</summary>
        public event EventHandler MarkersInvalidated;

        private void InvalidateMarkers()
        {
            var h = MarkersInvalidated;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ==================== 命令 ====================

        public ICommand StartLiveCommand { get; }
        public ICommand CaptureCommand { get; }
        public ICommand MoveToTargetCommand { get; }
        public ICommand ReadCurrentPoseCommand { get; }
        public ICommand PredictReverseCommand { get; }
        public ICommand ClearMarkersCommand { get; }
        public ICommand ResetPickCommand { get; }
        public ICommand VacuumOnCommand { get; }
        public ICommand VacuumOffCommand { get; }

        /// <summary>真空阀 IO 号（从标定档案读，默认 0；业务配置亦可覆盖）</summary>
        public int VacuumIoIndex => Profile.PickVacuumIoIndex;

        /// <summary>真空开关文案（吸住/放开）</summary>
        private string _vacuumStateText = "真空：未开启";
        public string VacuumStateText
        {
            get => _vacuumStateText;
            set => Set(ref _vacuumStateText, value);
        }

        private bool _vacuumOn;
        public bool VacuumOn
        {
            get => _vacuumOn;
            set { if (Set(ref _vacuumOn, value)) OnPropertyChanged(nameof(VacuumStateText)); }
        }

        public CalibrationPickPlaceViewModel(CalibrationProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _calibService = new CalibrationService();
            _devicePool = App.StationHostRuntime != null ? App.StationHostRuntime.DevicePool : null;
            ImageDisplay = new ImageDisplayVm(_renderService);

            StartLiveCommand = new RelayCommand(StartLive, () => CanCamera());
            CaptureCommand = new RelayCommand(CaptureFrame, () => CanCamera() && !IsBusy);
            MoveToTargetCommand = new RelayCommand(_ => MoveToTarget(), _ => CanMove());
            ReadCurrentPoseCommand = new RelayCommand(_ => ReadCurrentPoseIntoTarget(), _ => SelectedMotion != null);
            PredictReverseCommand = new RelayCommand(_ => PredictReverse(), _ => IsMatrixReady && !IsBusy);
            ClearMarkersCommand = new RelayCommand(_ => { Markers.Clear(); InvalidateMarkers(); });
            ResetPickCommand = new RelayCommand(_ => ResetPick(), _ => HasPick);
            VacuumOnCommand = new RelayCommand(_ => SetVacuum(true), _ => CanToggleVacuum());
            VacuumOffCommand = new RelayCommand(_ => SetVacuum(false), _ => CanToggleVacuum());

            MatrixPath = ResolveMatrixPath();
            LoadDevices();

            if (!IsMatrixReady)
            {
                SessionNote = "该方案尚无标定矩阵 —— 无法校验。请先完成九点标定并保存。";
                AppendLog("校验不可用: 未找到矩阵文件。");
            }
            else
            {
                SessionNote = "校验就绪: 抓拍定格/实时预览 → 在画面上点击要吸的目标点 → 低速到位（吸嘴去吸该点）。";
                AppendLog("点哪去哪校验台就绪, 矩阵: " + MatrixPath);
                AppendLog("坐标语义: 同心吸嘴 + 固定相机(EyeToHand) → 吸点 = H(u)，无偏心/旋转/对针修正。");
            }
            RefreshCurrentPos();
        }

        // ==================== 设备生命周期 ====================

        private void LoadDevices()
        {
            if (_devicePool == null)
            {
                AppendLog("警告: DevicePool 未初始化，无法加载硬件列表(仅建议模式可用)。");
                return;
            }
            foreach (var cam in _devicePool.GetAllDevices().OfType<ICamera>()) CameraDeviceList.Add(cam);
            foreach (var motion in _devicePool.GetAllDevices().OfType<IMotionCard>()) MotionDeviceList.Add(motion);

            SelectedCamera = CameraDeviceList.FirstOrDefault(c => IsBoundDevice(c, Profile.CameraId))
                             ?? CameraDeviceList.FirstOrDefault();
            SelectedMotion = MotionDeviceList.FirstOrDefault(m => IsBoundDevice(m, Profile.AxisId))
                             ?? MotionDeviceList.FirstOrDefault();

            if (SelectedCamera == null) AppendLog("提示: 无在线相机——抓拍/实时不可用。");
            if (SelectedMotion == null) AppendLog("提示: 无在线运动卡——低速到位不可用(仅显示坐标)。");
        }

        private void OnSelectedCameraChanged(ICamera oldCamera, ICamera newCamera)
        {
            if (oldCamera != null)
            {
                oldCamera.FrameReceived -= OnCameraFrameReceived;
                if (_weStartedGrabbing || _streamActive)
                {
                    try { oldCamera.StopGrabbing(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("切换相机停流异常: " + ex.Message); }
                }
                if (_weStartedGrabbing) RestoreTriggerMode(oldCamera);
            }
            _weStartedGrabbing = false;
            _streamActive = false;
            _triggerModeToRestore = null;
            _firstFrameTcs = null;
            _sessionFirstFramePending = false;

            if (newCamera == null) return;
            newCamera.FrameReceived -= OnCameraFrameReceived;
            newCamera.FrameReceived += OnCameraFrameReceived;
            AppendLog("相机已选: " + DisplayName(newCamera)
                      + (newCamera.State == DeviceState.Connected ? "（已连接，点预览/抓拍即出图）" : "（未连接——点预览/抓拍会自动连接）"));
        }

        private void OnSelectedMotionChanged(IMotionCard newMotion)
        {
            _facade = null;
            if (newMotion == null) return;
            _facade = new CalibrationMotionFacade(newMotion,
                Profile.BindXAxisIndex, Profile.BindYAxisIndex, Profile.BindZAxisIndex,
                speed: 50f, log: AppendLog);
            AppendLog($"运动卡已选: {DisplayName(newMotion)} (低速到位门面就绪)");
            RefreshCurrentPos();
        }

        // ==================== 相机：实时/抓拍（自愈取流） ====================

        private bool CanCamera() => SelectedCamera != null && IsMatrixReady && !IsBusy;

        private async Task StartLive()
        {
            if (IsBusy) return;
            if (SelectedCamera == null)
            {
                AppendLog("未选择相机，无法实时预览。");
                return;
            }
            if (_streamActive)
            {
                AppendLog("相机已在取流（实时预览或定格中）。可直接在画面上点选目标点。");
                return;
            }
            IsBusy = true;
            try
            {
                await OpenCameraStreamAsync("实时预览", freezeAfterFirstFrame: false);
            }
            catch (Exception ex)
            {
                AppendLog("实时预览异常: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task CaptureFrame()
        {
            if (IsBusy) return;
            ResetPick();
            _photoPose = null; // 新一次抓拍前作废上一轮定格机位
            if (_weStartedGrabbing)
            {
                StopOwnedGrabbing("抓拍定格");
                _streamActive = false;
                TrySnapshotPhotoPose();
                SessionNote = "画面已定格 —— 点击图上要吸的目标点 → 低速到位。";
                AppendLog("画面已定格 —— 请点击图上目标点（可再点「实时预览」恢复）。");
                return;
            }
            if (SelectedCamera == null)
            {
                AppendLog("未选择相机，无法抓拍。");
                return;
            }
            IsBusy = true;
            try
            {
                await OpenCameraStreamAsync("抓拍定格", freezeAfterFirstFrame: true);
            }
            catch (Exception ex)
            {
                AppendLog("抓拍异常: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<bool> OpenCameraStreamAsync(string action, bool freezeAfterFirstFrame)
        {
            var cam = SelectedCamera;
            if (cam == null)
            {
                AppendLog($"{action}: 未选择相机。");
                return false;
            }

            if (cam.State != DeviceState.Connected)
            {
                AppendLog($"{action}: 相机未连接，正在自动连接 {DisplayName(cam)} …");
                var conn = await Task.Run(() => cam.Connect());
                if (!ReferenceEquals(cam, SelectedCamera)) return false;
                if (conn == null || !conn.Success)
                {
                    string msg = conn?.Message ?? "未知原因";
                    AppendLog($"{action}失败: 相机连接失败 —— {msg}");
                    SessionNote = "⚠ 相机连接失败：" + msg + " —— 请检查相机电源/网线/驱动，先在「硬件调试」确认能出图再回来。";
                    return false;
                }
                AppendLog($"{action}: 相机连接成功。");
            }

            var modeRes = cam.GetParam("TriggerModeSelect");
            int curMode = 0;
            if (modeRes.Success && int.TryParse(modeRes.Data?.ToString(), out int m)) curMode = m;
            if (curMode != 0)
            {
                var setRes = cam.SetTriggerMode(0);
                if (setRes != null && setRes.Success)
                {
                    _triggerModeToRestore = curMode;
                    AppendLog($"{action}: 相机原为触发模式({curMode})，已临时切为连续采集（关闭窗口时自动恢复）。");
                }
                else
                {
                    AppendLog($"{action}警告: 切换连续采集模式失败({setRes?.Message})——触发模式下可能一直收不到帧。");
                }
            }

            var start = await Task.Run(() => cam.StartContinuousGrab());
            if (!ReferenceEquals(cam, SelectedCamera))
            {
                if (start != null && start.Success)
                {
                    try { cam.StopGrabbing(); } catch { }
                }
                return false;
            }
            bool opened = start != null && start.Success;
            _weStartedGrabbing = opened;
            _streamActive = true;
            AppendLog(opened
                ? $"{action}: 取流已开启，等待首帧上屏…"
                : $"{action}: 相机已被其他页面取流（{start?.Message}）——自动搭车等待下一帧…");

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstFrameTcs = tcs;
            _sessionFirstFramePending = true;
            try
            {
                var winner = await Task.WhenAny(tcs.Task, Task.Delay(5000, _lifetimeCts.Token));
                if (_lifetimeCts.IsCancellationRequested) return false;

                bool gotFrame = winner == tcs.Task;
                if (gotFrame)
                {
                    if (freezeAfterFirstFrame && _weStartedGrabbing)
                    {
                        StopOwnedGrabbing(action);
                        _streamActive = false;
                        TrySnapshotPhotoPose();
                        SessionNote = "画面已定格 —— 点击图上要吸的目标点 → 低速到位。";
                        AppendLog($"{action}: 画面已定格 —— 请点击图上目标点。");
                    }
                    else if (!freezeAfterFirstFrame)
                    {
                        SessionNote = "实时预览中 —— 可直接点选已见目标；「抓拍定格」可冻结画面。";
                        AppendLog($"{action}: 首帧已上屏，实时预览中。");
                    }
                    else
                    {
                        _streamActive = true;
                        AppendLog($"{action}: 相机由其他页面取流，画面无法真正定格（会持续更新）。可到取流页面停止后重试，或直接在实时画面点选。");
                    }
                    return true;
                }

                _streamActive = false;
                AppendLog($"{action}失败: 5 秒内未收到相机帧。请检查：①曝光设置 ②相机是否处于外触发等待信号 ③相机连接与驱动。");
                if (_weStartedGrabbing) StopOwnedGrabbing(action);
                SessionNote = "⚠ 相机开流后 5 秒无帧 —— 排查方向见会话日志；确认相机能出图后重新点击即可。";
                return false;
            }
            finally
            {
                if (ReferenceEquals(_firstFrameTcs, tcs)) _firstFrameTcs = null;
            }
        }

        private void StopOwnedGrabbing(string why)
        {
            var cam = SelectedCamera;
            if (cam == null || !_weStartedGrabbing) return;
            _weStartedGrabbing = false;
            try
            {
                var st = cam.StopGrabbing();
                if (st != null && !st.Success)
                {
                    AppendLog($"{why}: 停止取流返回未确认({st.Message})。");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"{why}: 停止取流异常: {ex.Message}");
            }
        }

        private void RestoreTriggerMode(ICamera cam)
        {
            if (cam == null || !_triggerModeToRestore.HasValue) return;
            int restore = _triggerModeToRestore.Value;
            _triggerModeToRestore = null;
            try
            {
                var r = cam.SetTriggerMode(restore);
                AppendLog("已恢复相机触发模式: " + restore
                          + (r != null && r.Success ? "" : "（失败: " + r?.Message + "）"));
            }
            catch (Exception ex)
            {
                AppendLog("恢复触发模式异常: " + ex.Message);
            }
        }

        /// <summary>窗口关闭收尾：停自开流/恢复触发模式/退订事件/清空显示（不断开连接）。</summary>
        public void Cleanup()
        {
            _lifetimeCts.Cancel();
            var cam = SelectedCamera;
            if (cam != null)
            {
                cam.FrameReceived -= OnCameraFrameReceived;
                if (_weStartedGrabbing)
                {
                    try { cam.StopGrabbing(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("点哪去哪收尾停流异常: " + ex.Message); }
                    RestoreTriggerMode(cam);
                }
            }
            _weStartedGrabbing = false;
            _streamActive = false;
            _triggerModeToRestore = null;
            _firstFrameTcs = null;
            _sessionFirstFramePending = false;
            // 关闭窗口时若真空仍开着 → 关闭，防带料悬停撞机
            if (_vacuumOn && _facade != null)
            {
                try { _facade.SetOutput(VacuumIoIndex, false); } catch { }
            }
            try
            {
                ImageDisplay.Clear();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("点哪去哪清空显示异常: " + ex.Message);
            }
        }

        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            if (e?.Buffer == null || e.Width <= 0 || e.Height <= 0) return;
            if (!ReferenceEquals(sender, SelectedCamera)) return;
            var app = Application.Current;
            if (app == null) return;

            var tcs = _firstFrameTcs;
            var frame = e;
            var camKey = sender;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!ReferenceEquals(camKey, SelectedCamera)) return;
                    var now = DateTime.Now;
                    if (_sessionFirstFramePending)
                    {
                        _sessionFirstFramePending = false;
                    }
                    else if (_streamActive && (now - _lastLiveRenderAt).TotalMilliseconds < 50)
                    {
                        return;
                    }
                    _lastLiveRenderAt = now;

                    var context = _renderService.CreateRenderContextFromFrame(frame,
                        DisplayName(SelectedCamera), "PickPlaceLive");
                    if (context == null) return;
                    ImageDisplay.AddOrUpdateImageContext(context);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("点哪去哪帧处理异常: " + ex.Message);
                }
            }), DispatcherPriority.Background);
            tcs?.TrySetResult(true);
        }

        // ==================== 点选 → 吸点坐标 ====================

        /// <summary>视图层点选回调(覆盖层鼠标按下 → 视口坐标 → 图像坐标 row/col)</summary>
        public void ApplyPick(double row, double col)
        {
            if (!IsMatrixReady)
            {
                AppendLog("无矩阵, 无法换算吸点坐标。");
                return;
            }
            _pickRow = row;
            _pickCol = col;

            // ★ 同心吸嘴 + 固定相机(EyeToHand)：吸点 = H(u)，无偏心/旋转/对针修正
            var res = _calibService.MapPixelToWorld(MatrixPath, col, row);
            if (!res.Success)
            {
                PickInfoText = $"点选 ({col:F1},{row:F1}) 换算失败: {res.Message}";
                AppendLog("坐标换算失败: " + res.Message);
                HasPick = false;
                return;
            }
            _pickWorldX = res.Data.WorldX;
            _pickWorldY = res.Data.WorldY;
            HasPick = true;

            PickInfoText = $"像素 ({col:F1}, {row:F1}) → 吸点 H(u) = ({_pickWorldX:F3}, {_pickWorldY:F3}) mm";
            AppendLog($"点选吸点: 像素(col={col:F1}, row={row:F1}) → X={_pickWorldX:F3}, Y={_pickWorldY:F3} mm。");

            // 图上画绿十字标注目标点
            Markers.Clear();
            Markers.Add(new GeoMarkerItem { Row = row, Col = col, Size = 40, Color = "green", Label = "目标吸点" });
            InvalidateMarkers();

            // 若相机随 Z 升降（保守口径），提醒回到标定高度
            TryWarnCalibZ();
        }

        /// <summary>相机随 Z 升降时，拍照高度偏离标定高度会致像素当量失真——轻量提示（不改可信度判定）。</summary>
        private void TryWarnCalibZ()
        {
            if (!Profile.CalibZ.HasValue) return;
            var pose = _photoPose ?? TryReadCurrentPose();
            if (!pose.HasValue || double.IsNaN(pose.Value.Z)) return;
            double dz = pose.Value.Z - Profile.CalibZ.Value;
            bool zLinked = Profile.CameraMovesWithZ ?? true; // 未声明 → 保守按随 Z
            if (zLinked && Math.Abs(dz) > 5.0)
            {
                AppendLog($"  ⚠ 拍照 Z={pose.Value.Z:F1}mm ≠ 标定高度 {Profile.CalibZ.Value:F1}mm (Δ={dz:F1}mm)" +
                          $" —— 相机随 Z 升降时像素当量失真，本次换算可能不可信。请把 Z 移回 {Profile.CalibZ.Value:F1}mm 重新抓拍点选。");
            }
        }

        /// <summary>低速到位：吸嘴去吸点选的像素对应的机械坐标（H(u)）。Z 保持当前高度。</summary>
        private void MoveToTarget()
        {
            if (!HasPick)
            {
                AppendLog("先在画面上点选一个目标点。");
                return;
            }
            if (_facade == null)
            {
                AppendLog("未绑定运动卡, 无法到位(仅显示坐标模式)。");
                return;
            }
            IsBusy = true;
            try
            {
                double fx = _pickWorldX;
                double fy = _pickWorldY;
                AppendLog($"低速到位 → X={fx:F3}, Y={fy:F3} mm（吸点 H(u)，同心吸嘴无偏心修正）...");
                bool moved = _facade.MoveToXY(fx, fy);
                AppendLog(moved
                    ? "到位完成 —— 请目视确认吸嘴是否对准目标点。"
                    : "到位被拒: " + (_facade.LastError ?? "未知原因")
                      + "（走位被拒≠换算错误——是目标点超出机械可达域，把工件/拍照位向可达域中腰收拢后重试）。");
            }
            catch (Exception ex)
            {
                AppendLog("到位异常: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ResetPick()
        {
            HasPick = false;
            PickInfoText = "尚未点选 —— 抓拍/实时后，在画面上点击要吸的目标点。";
        }

        // ==================== 真空吸住（点哪去哪验收辅助） ====================

        private bool CanToggleVacuum() => _facade != null && !IsBusy;

        /// <summary>开/关真空阀：到位后开启吸住工件，验证吸嘴能吸住目标点（IO 号来自标定档案 PickVacuumIoIndex）。</summary>
        private void SetVacuum(bool on)
        {
            if (_facade == null)
            {
                AppendLog("未绑定运动卡，无法控制真空阀。");
                return;
            }
            bool ok = _facade.SetOutput(VacuumIoIndex, on);
            if (ok)
            {
                VacuumOn = on;
                VacuumStateText = on ? $"真空：已吸住（IO{VacuumIoIndex} ON）" : $"真空：已放开（IO{VacuumIoIndex} OFF）";
                AppendLog(on
                    ? $"已开启真空 IO{VacuumIoIndex} —— 吸嘴吸住工件（到位后用于验证吸点是否对准）。"
                    : $"已关闭真空 IO{VacuumIoIndex} —— 吸嘴放开。");
            }
            else
            {
                AppendLog("真空控制失败: " + (_facade.LastError ?? "未知原因"));
            }
        }

        // ==================== 反向可视化（机械 → 像素） ====================

        private void ReadCurrentPoseIntoTarget()
        {
            var pose = TryReadCurrentPose();
            if (!pose.HasValue)
            {
                AppendLog("读当前位失败：未选运动卡或轴反馈读取失败。");
                return;
            }
            TargetXText = pose.Value.X.ToString("F3");
            TargetYText = pose.Value.Y.ToString("F3");
            AppendLog($"已读取当前机位 → ({TargetXText}, {TargetYText})");
            PredictReverse();
        }

        /// <summary>输入框变更 → 若已画过反投影十字则立即重画（不抓拍），让"改数字→十字动"立即可见。</summary>
        private void RedrawReverseOnInputChanged()
        {
            if (IsBusy || !IsMatrixReady) return;
            if (double.IsNaN(_reversePredictCol)) return;
            PredictReverse();
        }

        /// <summary>反投影 + 画青色预测十字 + 更新结论文本与日志。</summary>
        private void PredictReverse()
        {
            if (!IsMatrixReady)
            {
                AppendLog("无矩阵，无法反投影。");
                return;
            }
            double mx, my;
            bool hasManual = double.TryParse(TargetXText, out mx) && double.TryParse(TargetYText, out my);
            var pose = TryReadCurrentPose();
            string srcTag;

            double bx, by;
            if (hasManual)
            {
                bx = mx; by = my;
                srcTag = "手输目标位";
                if (pose.HasValue)
                {
                    double d = Math.Sqrt(Math.Pow(pose.Value.X - mx, 2) + Math.Pow(pose.Value.Y - my, 2));
                    if (d > 0.5)
                    {
                        srcTag = $"手输目标位（⚠ 实际机位偏离 {d:F2}mm，未走位时仅供预览）";
                        AppendLog($"  ⚠ 手输目标 ({mx:F3},{my:F3}) 与当前机位 ({pose.Value.X:F3},{pose.Value.Y:F3}) 相差 {d:F2}mm —— 十字按手输位画（预览）。");
                    }
                }
            }
            else if (pose.HasValue)
            {
                bx = pose.Value.X; by = pose.Value.Y;
                srcTag = "当前机位";
            }
            else
            {
                AppendLog("无法反投影：输入框不是合法数字，且读不到当前机位（先【读当前位】或手填坐标）。");
                ReverseInfoText = "⚠ 无法反投影：请填目标 X/Y（或点【读当前位】）。";
                return;
            }

            var bwd = _calibService.MapWorldToPixel(MatrixPath, bx, by);
            if (!bwd.Success)
            {
                AppendLog("反投影失败: " + (bwd.Message ?? "未知原因"));
                ReverseInfoText = "⚠ 反投影失败：" + (bwd.Message ?? "未知原因");
                return;
            }
            _reversePredictCol = bwd.Data.PixelX;
            _reversePredictRow = bwd.Data.PixelY;
            _reversePoseX = bx;
            _reversePoseY = by;

            // 追加青十字（不清空已有绿十字，形成双向对照）
            for (int i = Markers.Count - 1; i >= 0; i--)
            {
                if (Markers[i].Color == "cyan") Markers.RemoveAt(i);
            }
            Markers.Add(new GeoMarkerItem { Row = _reversePredictRow, Col = _reversePredictCol, Size = 36, Color = "cyan", Label = "机械位对应像素" });
            InvalidateMarkers();

            ReverseInfoText =
                $"[{srcTag}] ({bx:F3},{by:F3}) → 像素 (col={_reversePredictCol:F1}, row={_reversePredictRow:F1})（青十字）\n" +
                "★ 肉眼看：该机械位对应的像素是否与实际特征一致？改上方 X/Y 数字会立即重画。";
            AppendLog($"[反向] 机械({bx:F3},{by:F3})[{srcTag}] → 像素(col={_reversePredictCol:F1},row={_reversePredictRow:F1})，已画青十字。");
        }

        // ==================== 工具 ====================

        private void TrySnapshotPhotoPose()
        {
            try { _photoPose = TryReadCurrentPose(); }
            catch { _photoPose = null; }
        }

        private (double X, double Y, double Z, double U)? TryReadCurrentPose()
        {
            var m = SelectedMotion;
            if (m == null) return null;
            var x = m.GetFeedbackPosition(Profile.BindXAxisIndex);
            var y = m.GetFeedbackPosition(Profile.BindYAxisIndex);
            if (!x.Success || !y.Success) return null;
            var z = m.GetFeedbackPosition(Profile.BindZAxisIndex);
            var u = m.GetFeedbackPosition(Profile.BindRotationAxisIndex);
            return (x.Data, y.Data,
                    z.Success ? z.Data : double.NaN,
                    u.Success ? u.Data : double.NaN);
        }

        private void RefreshCurrentPos()
        {
            var pose = TryReadCurrentPose();
            if (pose.HasValue)
            {
                CurrentPosText = $"X={pose.Value.X:F3}  Y={pose.Value.Y:F3}" +
                                 (double.IsNaN(pose.Value.Z) ? "" : $"  Z={pose.Value.Z:F1}") +
                                 (double.IsNaN(pose.Value.U) ? "" : $"  U={pose.Value.U:F1}°");
            }
            else
            {
                CurrentPosText = "读取失败（未连接运动卡）";
            }
        }

        /// <summary>
        /// 解析本档案的矩阵文件路径。
        /// ★2026-09-15 单轨存储：候选取自 CalibrationMatrixStore（工位级
        /// Recipes\Workstations\{工位}\Calib），不再兜底已废弃的设备级目录 Recipes\Devices。
        /// </summary>
        private string ResolveMatrixPath()
        {
            return CalibrationMatrixStore.ResolveMatrixPath(Profile);
        }

        private static string DisplayName(IDevice d) =>
            !string.IsNullOrWhiteSpace(d.DeviceName) ? d.DeviceName
            : !string.IsNullOrWhiteSpace(d.DeviceKey) ? d.DeviceKey
            : d.DeviceId;

        private static bool IsBoundDevice(IDevice device, string profileId)
        {
            if (device == null || string.IsNullOrWhiteSpace(profileId)) return false;
            return string.Equals(device.DeviceKey, profileId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.DeviceId, profileId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.DeviceName, profileId, StringComparison.OrdinalIgnoreCase);
        }

        private void AppendLog(string msg)
        {
            try
            {
                Grayson.Vision.Contracts.Infrastructure.Logging.LogBus.Info("CalibrationPickPlace", msg);
            }
            catch { }

            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (LogLines.Count > 200) LogLines.RemoveAt(0);
        }

        private bool CanMove() => HasPick && !IsBusy && _facade != null;

        private void RaiseCanExecutes()
        {
            (StartLiveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CaptureCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MoveToTargetCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ReadCurrentPoseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PredictReverseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ResetPickCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (VacuumOnCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (VacuumOffCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
