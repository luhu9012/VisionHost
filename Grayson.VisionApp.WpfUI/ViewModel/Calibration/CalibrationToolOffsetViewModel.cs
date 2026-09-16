//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationToolOffsetViewModel.cs
// 说 明: P4 对针补偿会话（2026-09-05 一期 = 单次对针、固定姿态、平移补偿）。
//        数学（v1 文档 6.1）：
//          ① 工具头真实作用点对准特征 P → 读机械坐标 M_tool
//          ② 移开工具头 → 抓拍（P 露出）→ 自动画 理论像点 A = H⁻¹(M_tool)（MapWorldToPixel 现成）
//          ③ 图上点选 P 真实像素 A'（覆盖层通道）→ δ_px=A'−A；δ_world = M_tool − H(A')（MapPixelToWorld）
//          ④ 写 CalibrationProfile.ToolOffsetWx/Wy（IsToolOffsetCalibrated=true）→ 发布后
//             MapPixelToWorld(q)+ToolOffset 机械域叠加。
//        布局前提：固定相机(EyeToHand/吸放式)下"工具头对准 P → 移开 → P 露出可拍"才成立；
//        EyeInHand 工位工具头与相机随动无法同帧，窗口引导用户现场核对/走吸放式方案。
//        符号：v1 原式 δ=M_tool−H(A')，镜像/符号按现场轴约定核对（镜像公式 x_go=−wx+Offset 配套）。
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
    public class CalibrationToolOffsetViewModel : ViewModelBase
    {
        public enum ToolOffsetStep
        {
            AlignAndLock = 1,  // ① 工具头对准特征并锁定 M_tool
            PhotoAndMark = 2,  // ② 移开工具头→抓拍→自动画理论点 A
            PickReal = 3,      // ③ 图上点选真实 A' → δ 预览
            Done = 4           // ④ 已写入 ToolOffset
        }

        private readonly ICalibrationService _calibService;
        private readonly IDevicePool _devicePool;
        private readonly HalconImageRenderService _renderService = new HalconImageRenderService();
        private CalibrationMotionFacade _facade;
        private bool _awaitingTheoryFrame;

        // ---- 相机取流会话状态（2026-09-06 自愈，与标定校验台同款约定：自动连接+连续模式+首帧看门狗） ----
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private TaskCompletionSource<bool> _firstFrameTcs;   // 首帧信号
        private bool _weStartedGrabbing;                     // 本会话自己发起且正在运行的取流（收尾时停）
        private bool _streamActive;                          // 处于"已请求出帧"（自开或搭车外部流）
        private int? _triggerModeToRestore;                  // 打开前触发模式（临时切连续，收尾恢复）
        private DateTime _lastLiveRenderAt = DateTime.MinValue; // 实时帧上屏节流（~20fps）
        private bool _sessionFirstFramePending;              // 本次取流首帧必须上屏（节流不拦首帧）
        private bool _reStampMarkers;                        // 定格后有迟到帧换底图 → 自动重贴 A/A' 标记

        /// <summary>取流过程中禁用设备下拉切换（防中途换相机导致会话状态错乱）</summary>
        public bool IsDeviceSwitchEnabled => !IsBusy;

        public CalibrationProfile Profile { get; }
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
                    _facade = null;
                    if (value != null)
                    {
                        _facade = new CalibrationMotionFacade(value,
                            Profile.BindXAxisIndex, Profile.BindYAxisIndex, Profile.BindZAxisIndex,
                            Profile.BindRotationAxisIndex,
                            50f, AppendLog);
                    }
                    RaiseCanExecutes();
                }
            }
        }

        /// <summary>切换相机：退订旧相机帧事件并清理其取流会话（设备连接保留——设备池共享），再订阅新相机。</summary>
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
                if (_weStartedGrabbing)
                {
                    RestoreTriggerMode(oldCamera);
                }
            }
            _weStartedGrabbing = false;
            _streamActive = false;
            _triggerModeToRestore = null;
            _firstFrameTcs = null;
            _sessionFirstFramePending = false;
            _awaitingTheoryFrame = false;
            _reStampMarkers = false;

            if (newCamera == null) return;
            newCamera.FrameReceived -= OnCameraFrameReceived;
            newCamera.FrameReceived += OnCameraFrameReceived;
            AppendLog("相机已选: " + DisplayName(newCamera)
                      + (newCamera.State == DeviceState.Connected ? "（已连接，抓拍即出图）" : "（未连接——抓拍时会自动连接）"));
        }

        // ==================== 矩阵与步进 ====================
        public string MatrixPath { get; private set; }
        public bool IsMatrixReady => !string.IsNullOrEmpty(MatrixPath);

        public double[] StepSizeOptions => new[] { 0.1, 0.5, 1.0, 2.0, 5.0, 10.0 };
        private double _stepSize = 1.0;
        public double StepSize { get => _stepSize; set => Set(ref _stepSize, value); }

        // ==================== 状态 ====================
        private ToolOffsetStep _step = ToolOffsetStep.AlignAndLock;
        public ToolOffsetStep Step
        {
            get => _step;
            private set { if (Set(ref _step, value)) { OnPropertyChanged(nameof(StepHintText)); RaiseCanExecutes(); } }
        }

        public string StepHintText
        {
            get
            {
                switch (Step)
                {
                    case ToolOffsetStep.AlignAndLock:
                        return "① 用下方低速步进把工具头(吸嘴/打点笔)真实作用点对准工件特征 P，点「📌 锁定 M_tool」。";
                    case ToolOffsetStep.PhotoAndMark:
                        return "② 请把工具头移开(特征露出、相机/工件不动)，然后点「📷 抓拍画理论点」——将自动标注理论像点 A。";
                    case ToolOffsetStep.PickReal:
                        return "③ 拖动黄色 B 点到特征 P 的真实像素位置松开写入，或直接在 P 上点击——面板实时显示 δ_px 与 δ_world。";
                    default:
                        return "④ 已完成对针。可「重新对针」或关闭窗口(结果已随方案保存)。";
                }
            }
        }

        private double _mToolX;
        private double _mToolY;
        private double _mToolZ;   // 锁定时刻 Z（压住高度，2026-09-10 新增）
        private double _mToolU;   // 锁定时刻 U（姿态角）
        private bool _hasMTool;
        public bool HasMTool { get => _hasMTool; set { if (Set(ref _hasMTool, value)) RaiseCanExecutes(); } }

        private string _mToolText = "尚未锁定";
        public string MToolText { get => _mToolText; set => Set(ref _mToolText, value); }

        private string _currentPosText = "--";
        public string CurrentPosText { get => _currentPosText; set => Set(ref _currentPosText, value); }

        private string _deltaText = "--";
        public string DeltaText { get => _deltaText; set => Set(ref _deltaText, value); }

        private string _sessionNote = "对针补偿准备中…";
        public string SessionNote { get => _sessionNote; set => Set(ref _sessionNote, value); }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) { OnPropertyChanged(nameof(IsDeviceSwitchEnabled)); RaiseCanExecutes(); } } }

        private double _aRow, _aCol, _bRow, _bCol; // 理论 A 与实测 A'(图像坐标 row=Y, col=X)

        public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();

        // ==================== 命令 ====================
        public ICommand StepMoveCommand { get; }
        public ICommand LockMToolCommand { get; }
        public ICommand CaptureAndMarkCommand { get; }
        public ICommand WriteToolOffsetCommand { get; }
        public ICommand RedoCommand { get; }

        public CalibrationToolOffsetViewModel(CalibrationProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _calibService = new CalibrationService();
            _devicePool = App.StationHostRuntime != null ? App.StationHostRuntime.DevicePool : null;
            ImageDisplay = new ImageDisplayVm(_renderService);

            StepMoveCommand = new RelayCommand(p => StepMove(p), _ => _facade != null && !IsBusy);
            LockMToolCommand = new RelayCommand(_ => LockMTool(), _ => _facade != null && !IsBusy);
            CaptureAndMarkCommand = new RelayCommand(CaptureAndMark, () => CanCaptureAndMark());
            WriteToolOffsetCommand = new RelayCommand(_ => WriteToolOffset(), _ => _step == ToolOffsetStep.PickReal && _hasPicked && !IsBusy);
            RedoCommand = new RelayCommand(_ => ResetToAlign(), _ => !IsBusy);

            MatrixPath = ResolveMatrixPath();
            LoadDevices();

            if (!IsMatrixReady)
            {
                SessionNote = "该方案尚无标定矩阵 —— 无法对针。请先完成标定并保存。";
                AppendLog("对针不可用: 未找到矩阵文件。");
            }
            else if (Profile.EyeMode == EyeMode.EyeInHand)
            {
                SessionNote = "⚠ 本方案为眼在手上(EyeInHand)：工具头与相机随动，无法同帧完成『对准→移开→拍 P』对针。固定相机(吸放式/EyeToHand)才是本流程布局；如确需对针请现场核对符号或改用 EyeToHand 方案。仍可继续操作(高级)。";
                AppendLog("注意: EyeInHand 工位对针流程需现场核对。");
            }
            else
            {
                SessionNote = "对针就绪: 工具头对准特征 → 锁定 → 移开抓拍 → 点真实点 A'。";
                AppendLog("对针台就绪, 矩阵: " + MatrixPath);
            }
            AppendLog("提示: 先手动把工具头大致移到工件上方, 再用步进微调对准。");
        }

        // ==================== 设备加载 ====================
        private void LoadDevices()
        {
            if (_devicePool == null)
            {
                AppendLog("警告: DevicePool 未初始化(演示/建议模式)。");
                return;
            }
            foreach (var cam in _devicePool.GetAllDevices().OfType<ICamera>()) CameraDeviceList.Add(cam);
            foreach (var m in _devicePool.GetAllDevices().OfType<IMotionCard>()) MotionDeviceList.Add(m);

            SelectedCamera = CameraDeviceList.FirstOrDefault(c => IsBoundDevice(c, Profile.CameraId)) ?? CameraDeviceList.FirstOrDefault();
            SelectedMotion = MotionDeviceList.FirstOrDefault(x => IsBoundDevice(x, Profile.AxisId)) ?? MotionDeviceList.FirstOrDefault();
            if (SelectedCamera == null) AppendLog("提示: 无在线相机 —— 抓拍不可用。");
            if (SelectedMotion == null) AppendLog("提示: 无在线运动卡 —— 步进/锁定不可用。");
        }

        // ==================== ① 对针采点 ====================
        private void StepMove(object param)
        {
            string dir = param as string;
            if (_facade == null || string.IsNullOrEmpty(dir)) return;
            double s = StepSize;
            double dx = 0, dy = 0, dz = 0, du = 0;
            switch (dir)
            {
                case "-x": dx = -s; break;
                case "+x": dx = s; break;
                case "-y": dy = -s; break;
                case "+y": dy = s; break;
                case "-z": dz = -s; break;
                case "+z": dz = s; break;
                case "-u": du = -s; break;
                case "+u": du = s; break;
                default: return;
            }
            IsBusy = true;
            try
            {
                bool ok;
                if (dx != 0 || dy != 0)
                {
                    ok = _facade.MoveBy(dx, dy);
                }
                else if (dz != 0)
                {
                    ok = _facade.MoveByZ(dz);
                }
                else
                {
                    ok = _facade.MoveByU(du);
                }
                AppendLog(ok ? $"步进 {dir}({s:F2}) 完成。" : "步进失败: " + (_facade.LastError ?? "未知"));
                RefreshCurrentPos();
            }
            catch (Exception ex)
            {
                AppendLog("步进异常: " + ex.Message);
            }
            finally { IsBusy = false; }
        }

        private void RefreshCurrentPos()
        {
            if (_facade == null)
            {
                CurrentPosText = "--";
                return;
            }
            var fx = _facade.GetAxisFeedback(Profile.BindXAxisIndex);
            var fy = _facade.GetAxisFeedback(Profile.BindYAxisIndex);
            if (fx.Success && fy.Success)
            {
                CurrentPosText = $"X={fx.Data:F3}  Y={fy.Data:F3}";
            }
            else
            {
                CurrentPosText = "读取失败";
            }
        }

        private void LockMTool()
        {
            if (_facade == null) return;
            var fx = _facade.GetAxisFeedback(Profile.BindXAxisIndex);
            var fy = _facade.GetAxisFeedback(Profile.BindYAxisIndex);
            if (!fx.Success || !fy.Success)
            {
                AppendLog("锁定失败: 读取轴反馈失败。");
                return;
            }
            _mToolX = fx.Data;
            _mToolY = fy.Data;
            // 2026-09-10：连同 Z/U 一起锁定（压住高度 + 姿态角），供对针后复核与姿态归位参考
            var fz = _facade.GetAxisFeedback(Profile.BindZAxisIndex);
            var fu = _facade.GetAxisFeedback(Profile.BindRotationAxisIndex);
            _mToolZ = fz.Success ? fz.Data : double.NaN;
            _mToolU = fu.Success ? fu.Data : double.NaN;
            HasMTool = true;
            MToolText = $"M_tool = ({_mToolX:F3}, {_mToolY:F3}) mm"
                      + (double.IsNaN(_mToolZ) ? "" : $"  Z={_mToolZ:F1}")
                      + (double.IsNaN(_mToolU) ? "" : $"  U={_mToolU:F1}°")
                      + " —— 工具头已对准 P";
            Step = ToolOffsetStep.PhotoAndMark;
            AppendLog($"已锁定 M_tool = ({_mToolX:F3}, {_mToolY:F3}) mm"
                      + (double.IsNaN(_mToolZ) ? "" : $"  Z={_mToolZ:F1}")
                      + (double.IsNaN(_mToolU) ? "" : $"  U={_mToolU:F1}°")
                      + "。请移开工具头后抓拍。");
        }

        // ==================== ② 抓拍画理论点（自愈取流：自动连接+连续模式+首帧看门狗） ====================
        private bool CanCaptureAndMark()
        {
            return SelectedCamera != null && IsMatrixReady && HasMTool
                   && _step == ToolOffsetStep.PhotoAndMark && !IsBusy;
        }

        private async Task CaptureAndMark()
        {
            if (IsBusy) return;
            if (SelectedCamera == null)
            {
                AppendLog("未选择相机，无法抓拍。");
                return;
            }
            IsBusy = true;
            try
            {
                ClearMarkers();
                _awaitingTheoryFrame = true; // 首帧上屏后自动标注理论点 A 并定格画面
                bool ok = await OpenCameraStreamAsync("对针抓拍");
                if (!ok)
                {
                    _awaitingTheoryFrame = false; // 无帧/失败：不悬挂等待标记
                }
            }
            catch (Exception ex)
            {
                AppendLog("抓拍异常: " + ex.Message);
            }
            finally { IsBusy = false; }
        }

        /// <summary>
        /// 自愈开流（对针抓拍专用；与校验台同款约定，状态机在 UI 线程，连接/开流后台执行不卡界面）：
        /// ①未连接 → 自动 Connect ②触发模式非连续 → 临时切连续(收尾恢复) ③开流(被占用则搭车等帧)
        /// ④5 秒首帧看门狗：无帧给排查结论并自动停流复位，不静默。
        /// 首帧的"定格+理论点标注"由收帧路径(StampTheoryOrMarkers)完成，本方法只负责把流打开。
        /// </summary>
        private async Task<bool> OpenCameraStreamAsync(string action)
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
                if (!ReferenceEquals(cam, SelectedCamera)) return false; // 等待期间被切换
                if (conn == null || !conn.Success)
                {
                    string msg = conn?.Message ?? "未知原因";
                    AppendLog($"{action}失败: 相机连接失败 —— {msg}");
                    SessionNote = "⚠ 相机连接失败：" + msg + " —— 请检查相机电源/网线/驱动，先在「硬件调试」确认能出图再回来。";
                    return false;
                }
                AppendLog($"{action}: 相机连接成功。");
            }

            // 触发模式非连续时 SDK 不会自动出帧（向导采样后常残留软触发/外触发）
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
                    AppendLog($"{action}警告: 切换连续采集模式失败({setRes?.Message})——触发模式下可能收不到帧。");
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

            // 首帧看门狗：5 秒内收到帧才算成功
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstFrameTcs = tcs;
            _sessionFirstFramePending = true; // 本次取流首帧必须上屏（节流不拦）
            try
            {
                var winner = await Task.WhenAny(tcs.Task, Task.Delay(5000, _lifetimeCts.Token));
                if (_lifetimeCts.IsCancellationRequested)
                {
                    return false; // 窗口已关闭：Cleanup 已做收尾
                }
                bool gotFrame = winner == tcs.Task;
                if (gotFrame)
                {
                    return true; // 标注/定格由收帧路径完成
                }

                _streamActive = false;
                AppendLog($"{action}失败: 5 秒内未收到相机帧。请检查：①曝光设置（过小/过大/自动） ②相机是否处于外触发等待信号 ③相机连接与驱动（可先在「硬件调试」验证出图）。");
                if (_weStartedGrabbing)
                {
                    StopOwnedGrabbing(action);
                }
                SessionNote = "⚠ 相机开流后 5 秒无帧 —— 排查方向见会话日志；确认相机能出图后重新点击即可。";
                return false;
            }
            finally
            {
                if (ReferenceEquals(_firstFrameTcs, tcs)) _firstFrameTcs = null;
            }
        }

        /// <summary>
        /// 相机帧回调（相机 SDK 线程）：marshal 到 UI 线程转 HImage 上屏（节流 ~20fps，首帧不拦），
        /// 上下文按固定 NodeId 走 AddOrUpdateImageContext 安全释放上一帧；
        /// 对针流程中每帧后补一次标记调度——标记须排在底图 Display 之后(否则被 ClearScene 吞掉)。
        /// </summary>
        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            if (e?.Buffer == null || e.Width <= 0 || e.Height <= 0) return;
            if (!ReferenceEquals(sender, SelectedCamera)) return; // 旧相机残留事件丢弃
            var app = Application.Current;
            if (app == null) return;

            var tcs = _firstFrameTcs; // 首帧信号：先于 UI 上屏把"有帧了"交付给等待者
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
                        DisplayName(SelectedCamera), "ToolOffsetLive");
                    if (context == null) return;
                    ImageDisplay.AddOrUpdateImageContext(context);

                    // 对针抓拍/标记重贴：Display 已入队(默认 Normal)，标记调度排其后 → 必在底图之上
                    if (_awaitingTheoryFrame || _reStampMarkers)
                    {
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { StampTheoryOrMarkers(); }
                            catch (Exception ex2) { System.Diagnostics.Debug.WriteLine("对针标记异常: " + ex2.Message); }
                        }));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("对针帧处理异常: " + ex.Message);
                }
            }), DispatcherPriority.Background);
            tcs?.TrySetResult(true);
        }

        /// <summary>
        /// 标记调度(UI 线程)：抓拍首帧 → 停流定格 + 标注理论点 A；定格后若仍有迟到帧换底图，
        /// 自动按当前步骤重贴 A/A' 标记，保证标记不被新底图 ClearScene 吞掉。
        /// </summary>
        private void StampTheoryOrMarkers()
        {
            if (_awaitingTheoryFrame)
            {
                _awaitingTheoryFrame = false;
                // 本会话开流 → 停流定格，让理论点标记停留在静止画面上（搭车外部流则停不了，靠重贴保持）
                if (_weStartedGrabbing)
                {
                    StopOwnedGrabbing("对针抓拍");
                    _streamActive = false;
                }
                DrawTheoryPointA();
                _reStampMarkers = _step == ToolOffsetStep.PickReal;
                return;
            }
            if (_reStampMarkers)
            {
                RedrawPickMarkers();
            }
        }

        /// <summary>按当前步骤重贴标记：PickReal/Done = A 理论(黄)；拖拽/已点选 = 追加 A' 实际(绿)+δ 文本。</summary>
        private void RedrawPickMarkers()
        {
            var host = _displayHost;
            if (host == null) return;
            host.ClearMarkers();
            if (_step == ToolOffsetStep.PickReal || _step == ToolOffsetStep.Done)
            {
                host.AddMarkerCross(_aRow, _aCol, 42, "yellow", "B 可拖拽");
                if (_hasPicked || _draggingB)
                {
                    double dPx = _bCol - _aCol;
                    double dPy = _bRow - _aRow;
                    host.AddMarkerCross(_bRow, _bCol, 46, "green", "A' 实际");
                    host.AddMarkerText($"δ_px=({dPx:F1},{dPy:F1})px", Math.Max(0, _bRow - 80), Math.Max(0, _bCol - 80), "green");
                }
            }
        }

        /// <summary>停止本会话自开的取流（定格/无帧复位/收尾共用）。</summary>
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

        /// <summary>恢复相机打开前的触发模式（仅本会话改过才恢复）。</summary>
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

        /// <summary>窗口关闭收尾：停自开流/恢复触发模式/退订事件/清空显示（不断开连接——设备由池共享）。</summary>
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
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("对针收尾停流异常: " + ex.Message); }
                    RestoreTriggerMode(cam);
                }
            }
            _weStartedGrabbing = false;
            _streamActive = false;
            _triggerModeToRestore = null;
            _firstFrameTcs = null;
            _sessionFirstFramePending = false;
            _awaitingTheoryFrame = false;
            _reStampMarkers = false;
            try
            {
                ImageDisplay.Clear(); // 释放本窗口 HImage 句柄
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("对针清空显示异常: " + ex.Message);
            }
        }

        private void DrawTheoryPointA()
        {
            // A = H⁻¹(M_tool)：矩阵认为"工具作用点"此刻在图像上的位置
            var a = _calibService.MapWorldToPixel(MatrixPath, _mToolX, _mToolY);
            if (!a.Success)
            {
                AppendLog("理论点计算失败(可能 M_tool 超出矩阵工作域): " + a.Message);
                SessionNote = "⚠ " + a.Message + " —— 请把工具头移回标定工作区内再锁定。";
                Step = ToolOffsetStep.AlignAndLock;
                return;
            }
            _aRow = a.Data.PixelY; // row = Y
            _aCol = a.Data.PixelX; // col = X
            ClearMarkers();
            var host = _displayHost;
            if (host != null)
            {
                host.AddMarkerCross(_aRow, _aCol, 42, "yellow", "B 可拖拽");
                host.AddMarkerText($"B = H⁻¹(M_tool) @ ({_aCol:F1},{_aRow:F1})", Math.Max(0, _aRow - 70), Math.Max(0, _aCol - 90), "yellow");
            }
            _hasPicked = false;
            _draggingB = false;
            OnPropertyChanged(nameof(HasPickedText));
            Step = ToolOffsetStep.PickReal;
            DeltaText = "拖动黄色 B 点到特征 P 的真实位置后松开写入；也可直接在 P 上点击。";
            AppendLog($"理论像点 B = H⁻¹(M_tool) = ({_aCol:F1}, {_aRow:F1}) 已标注(黄，可拖拽)。拖到实际对针像素松开即写入 δ；点击 P 亦可。");
        }

        // ==================== ③ 点选 A' / 拖拽 B 点 ====================
        private bool _hasPicked;
        private bool _draggingB;   // B 点拖拽中（预览态：A' 十字实时跟随，尚未提交）
        public string HasPickedText => _hasPicked ? "已点选 A'，可写入" : "尚未点选 A'";

        /// <summary>
        /// 判断 (row,col) 是否命中理论点 A（即"可拖拽的 B 点"）附近，用于启动拖拽。
        /// 阈值 40 图像像素（缩放后仍够宽，便于抓取）。
        /// </summary>
        public bool IsHitTheoryPoint(double row, double col)
        {
            if (_step != ToolOffsetStep.PickReal) return false;
            double dr = row - _aRow;
            double dc = col - _aCol;
            return Math.Sqrt(dr * dr + dc * dc) <= 40.0;
        }

        /// <summary>开始拖拽 B 点（进入预览态，不清除已有选中）。</summary>
        public void BeginDragB() => _draggingB = true;

        /// <summary>结束拖拽 B 点（退出预览态，提交 δ）。</summary>
        public void EndDragB(double row, double col)
        {
            _draggingB = false;
            ApplyPickReal(row, col);
        }

        /// <summary>
        /// 拖拽 B 点过程中实时预览（不提交）：重算 δ 并重画标记、更新文本，
        /// 但不置 _hasPicked 最终态、不提示"可写入"。松开鼠标后由 ApplyPickReal 提交。
        /// </summary>
        public void PreviewPickReal(double row, double col)
        {
            if (_step != ToolOffsetStep.PickReal) return;
            if (!IsMatrixReady) return;
            ComputeAndShowDelta(row, col, commit: false);
        }

        /// <summary>覆盖层点选/拖拽松开(视图调用)：A' = 特征 P 真实像素 → 计算 δ（提交）。</summary>
        public void ApplyPickReal(double row, double col)
        {
            if (_step != ToolOffsetStep.PickReal)
            {
                AppendLog("当前不在『点选真实点』步骤。");
                return;
            }
            if (!IsMatrixReady)
            {
                AppendLog("无矩阵, 无法换算。");
                return;
            }
            ComputeAndShowDelta(row, col, commit: true);
        }

        /// <summary>点选/拖拽共用：算 δ（像素/机械域）→ 重画标记 → 更新 DeltaText。commit=true 时置已选态并通知写按钮。</summary>
        private void ComputeAndShowDelta(double row, double col, bool commit)
        {
            _bRow = row;
            _bCol = col;

            // δ_px = A' − A（像素，机器无关便于核对）
            double dPx = _bCol - _aCol;
            double dPy = _bRow - _aRow;

            // δ_world = M_tool − H(A')（机械域偏移）
            var h = _calibService.MapPixelToWorld(MatrixPath, _bCol, _bRow);
            if (!h.Success)
            {
                DeltaText = $"A'=({_bCol:F1},{_bRow:F1}) 换算失败: {h.Message}";
                if (commit) AppendLog("δ_world 换算失败: " + h.Message);
                return;
            }
            double dWx = _mToolX - h.Data.WorldX;
            double dWy = _mToolY - h.Data.WorldY;

            // 标记统一走重贴通道（与迟到帧重贴同源，防新旧底图叠加）
            RedrawPickMarkers();

            DeltaText = $"δ_px = ({dPx:F1}, {dPy:F1}) px   →   δ_world = ({dWx:F3}, {dWy:F3}) mm（M_tool − H(A')）";
            if (commit)
            {
                _hasPicked = true;
                OnPropertyChanged(nameof(HasPickedText));
                AppendLog($"A'=({_bCol:F1},{_bRow:F1})  δ_px=({dPx:F1},{dPy:F1})px  δ_world=({dWx:F3},{dWy:F3})mm");
                (WriteToolOffsetCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        // ==================== ④ 写入 ====================
        private void WriteToolOffset()
        {
            if (!_hasPicked) return;
            var h = _calibService.MapPixelToWorld(MatrixPath, _bCol, _bRow);
            if (!h.Success) return;
            double dWx = _mToolX - h.Data.WorldX;
            double dWy = _mToolY - h.Data.WorldY;

            Profile.ApplyToolOffset(dWx, dWy);

            // ★2026-09-11：把锁定 M_tool 时刻的 Z（工具尖压住特征 P 的高度）一并回写档案 NozzleAlignZ。
            //   理由：校验台【低速到位】的 Z 下压目标就是这个"压住高度"——不回写的话档案里恒为 null，
            //   到校验台点选后只有 XY 动、Z 悬在高位，吸嘴碰不到工件面，开真空也吸不住。
            bool zSaved = !double.IsNaN(_mToolZ);
            if (zSaved)
            {
                Profile.NozzleAlignZ = _mToolZ;
            }
            string zPart = zSaved ? $"，压住高度 Z={_mToolZ:F1}mm 已回写档案" : "";

            Step = ToolOffsetStep.Done;
            SessionNote = $"✅ 对针补偿已写入: ToolOffset=({dWx:F3}, {dWy:F3}) mm{zPart}。关闭窗口后随方案保存；发布后引导坐标=Map+ToolOffset。";
            AppendLog($"ToolOffset 已写入 profile: ({dWx:F3}, {dWy:F3}) mm, 时间 {Profile.ToolOffsetCalibTime:HH:mm:ss}。");
            if (zSaved)
            {
                AppendLog($"[压住高度] NozzleAlignZ={_mToolZ:F1}mm 已写入档案 —— 校验台【低速到位】将下压到该高度（吸嘴尖触工件面，可开真空吸住）。");
            }
            else
            {
                AppendLog("⚠ 锁定 M_tool 时读不到 Z 轴反馈，未能回写压住高度 NozzleAlignZ —— 校验台到位时将按标定基准 Z 兜底下压，建议重新锁定一次。");
            }
            AppendLog("验收建议: 到「标定校验台」打点验收——点图上目标应精确到位(点哪去哪)。");
        }

        private void ResetToAlign()
        {
            ClearMarkers();
            _hasPicked = false;
            _awaitingTheoryFrame = false;
            _reStampMarkers = false;
            HasMTool = false;
            MToolText = "尚未锁定";
            DeltaText = "--";
            Step = ToolOffsetStep.AlignAndLock;
            SessionNote = "已重置 —— 请重新对针。";
            AppendLog("对针已重置。");
            (WriteToolOffsetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        // ==================== 标记宿主接线 ====================
        // 由视图在 InitializeComponent 后注入（XAML 不能直接暴露 HalconImageDisplayHost）
        private HalconImageDisplayHost _displayHost;
        public void AttachDisplayHost(HalconImageDisplayHost host)
        {
            _displayHost = host;
        }

        private void ClearMarkers()
        {
            var host = _displayHost;
            if (host != null) host.ClearMarkers();
        }

        // ==================== 工具 ====================
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
                Grayson.Vision.Contracts.Infrastructure.Logging.LogBus.Info("CalibrationToolOffset", msg);
            }
            catch { /* 日志镜像失败不影响主流程 */ }

            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (LogLines.Count > 200) LogLines.RemoveAt(0);
        }

        private void RaiseCanExecutes()
        {
            (StepMoveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LockMToolCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CaptureAndMarkCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (WriteToolOffsetCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RedoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
