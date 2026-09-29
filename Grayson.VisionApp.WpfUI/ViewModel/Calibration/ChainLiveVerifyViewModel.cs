//===================================================================================
// 文件名: ChainLiveVerifyViewModel.cs
// 说 明: 范式2 L3【真机指哪打哪】验证台（2026-09-29 新建）。
//
// 为什么需要它：L1（门禁可视）/L2（数学校验）都只看【落盘数字自洽】，
//   没有任何一项能回答"标定完了，机械手真的能打准吗"。L3 就是那个答案：
//   用真机走一遍「相机看到点 → 链求值 → 逆解 → 到位 → 复测」的完整闭环，
//   在世界域量偏差，给放行/拦截结论。
//
// 会话流程（每点一轮）：
//   ① 走拍照位（EIH 用 PhotoPose；ETH 相机固定不回位）→ 软触发取帧
//   ② 操作员在图上点选特征点 → 像素 (u,v)                ← 这是「指」
//   ③ PlanPoint：ResolvePixelToWorld → ResolveFlangeTarget
//   ④ 抬到安全 Z → 低速 MoveToXY 到法兰目标 → 等到位      ← 这是「走」
//   ⑤ 到位后复测：再次拍图 + 点选同一特征 → 新像素
//      反查该像素对应的实际世界点（沿链求值，用到位时的真实位姿）
//   ⑥ CompletePoint：世界偏差 ≤ 容差 → 绿；否则红并给 x/y 分量  ← 这是「打哪」
//
// 关键纪律（照抄 v2 血泪）：
//   · 偏差必须在【世界域】量：法兰反馈残差 ≠ 标定误差（会把伺服跟随误差算进去）
//   · 降 Z 后必须抬回 SafeZ 再平移（低位平移 = 撞机）
//   · 速度 <= 0 ⇒ ZMC 轴不动且 IsAxisIdle 恒 0（门面已兜底）
//   · 不沿用旧帧：到位后复测必须重新软触发
//===================================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grayson.Vision.Contracts.Calibration.Chain;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>L3 验证点（界面行）：一「指」一「打」的全部数字。</summary>
    public class LiveVerifyPointVm : ViewModelBase
    {
        public LiveVerifyPointResult Model { get; }

        public LiveVerifyPointVm(LiveVerifyPointResult m) { Model = m; }

        public int Index { get { return Model.Index; } }
        public double PixelU { get { return Model.PixelU; } set { Model.PixelU = value; OnPropertyChanged(nameof(PixelU)); } }
        public double PixelV { get { return Model.PixelV; } set { Model.PixelV = value; OnPropertyChanged(nameof(PixelV)); } }

        public string TargetText
        {
            get { return Model.TargetWorldX.ToString("F3") + ", " + Model.TargetWorldY.ToString("F3"); }
        }

        public string ActualText
        {
            get
            {
                return Model.Measured
                    ? Model.ActualWorldX.ToString("F3") + ", " + Model.ActualWorldY.ToString("F3")
                    : "—";
            }
        }

        public string ErrorText
        {
            get
            {
                return Model.Measured
                    ? Model.ErrorMm.ToString("F4")
                    : "—";
            }
        }

        /// <summary>分量文本：x/y 分开列，便于判断是系统性偏移还是随机（现场最常问的）</summary>
        public string ErrorXYText
        {
            get
            {
                if (!Model.Measured) return "—";
                return "Δx " + Model.ErrorX.ToString("+0.0000;-0.0000;0.0000")
                     + " / Δy " + Model.ErrorY.ToString("+0.0000;-0.0000;0.0000");
            }
        }

        /// <summary>到位误差（法兰反馈 − 法兰目标）：伺服/机械跟随能力，不算标定的错。</summary>
        public string ArrivalErrorText
        {
            get
            {
                double dx = Model.ActualFlangeX - Model.TargetFlangeX;
                double dy = Model.ActualFlangeY - Model.TargetFlangeY;
                double d = Math.Sqrt(dx * dx + dy * dy);
                return d.ToString("F4");
            }
        }

        public string StateText
        {
            get
            {
                if (!Model.Measured) return Model.Note ?? "○ 未测";
                return Model.Passed ? "✓ 达标" : "✗ 超差";
            }
        }

        public Brush StateBrush
        {
            get
            {
                if (!Model.Measured) return new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));
                return Model.Passed
                    ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41))
                    : new SolidColorBrush(Color.FromRgb(0xA8, 0x00, 0x00));
            }
        }

        public void Refresh()
        {
            OnPropertyChanged(nameof(TargetText));
            OnPropertyChanged(nameof(ActualText));
            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(ErrorXYText));
            OnPropertyChanged(nameof(ArrivalErrorText));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateBrush));
        }
    }

    /// <summary>
    /// L3 真机验证台 VM：装载链图 → 绑定相机/运动卡 → 逐点「指哪打哪」→ 放行结论。
    /// </summary>
    public class ChainLiveVerifyViewModel : ViewModelBase
    {
        private readonly IDevicePool _devicePool;
        private readonly CalibrationMotionFacade _motion;

        private StationCalibGraph _graph;
        private ICamera _camera;
        private string _cameraId;
        private string _toolId;

        // ---- 轴槽位：与链向导同口径，由操作员在界面确认（档案不含轴槽映射事实）----
        private int _xAxis = 0, _yAxis = 1, _zAxis = 2, _uAxis = 3;
        private float _speed = 30f;
        private double _safeZ = 50.0;          // 平移前必须抬到的高度
        private double _calibZ;                // 标定高度（拍照/验证应在该 Z 附近）

        public ChainLiveVerifyViewModel(string stationCode)
        {
            StationCode = (stationCode ?? string.Empty).Trim();
            try { _devicePool = App.StationHostRuntime?.DevicePool; }
            catch { _devicePool = null; }

            _motion = new CalibrationMotionFacade(
                ResolveMotionCard(), _xAxis, _yAxis, _zAxis, _uAxis,
                speed: _speed, log: m => { Log = m; });

            ToleranceMm = ChainLiveVerifier.DefaultToleranceMm;

            LoadChainCommand = new RelayCommand(_ => LoadChain());
            CaptureAndPickCommand = new RelayCommand(_ => CaptureForPick());
            PlanCommand = new RelayCommand(_ => PlanFromPicked(), _ => _pickedPixel != null);
            ExecuteAllCommand = new RelayCommand(_ => ExecuteAll(), _ => Points.Count > 0);
            ClearCommand = new RelayCommand(_ => { Points.Clear(); RefreshAll(); });

            LoadChain();
        }

        /// <summary>轴槽位（EPSON 默认 X=0/Y=1/Z=2/U=3；改后需重开窗口生效）。</summary>
        public int AxisX { get { return _xAxis; } set { Set(ref _xAxis, value); } }
        public int AxisY { get { return _yAxis; } set { Set(ref _yAxis, value); } }
        public int AxisZ { get { return _zAxis; } set { Set(ref _zAxis, value); } }
        public int AxisU { get { return _uAxis; } set { Set(ref _uAxis, value); } }

        /// <summary>安全 Z：任何 XY 平移前必须抬到的高度（低位平移 = 撞机）。</summary>
        public double SafeZ { get { return _safeZ; } set { Set(ref _safeZ, value); } }

        #region 状态与属性

        public string StationCode { get; }

        private string _log = "就绪。";
        public string Log { get { return _log; } private set { Set(ref _log, value); } }

        private string _verdict = "尚未验证。";
        public string Verdict { get { return _verdict; } private set { Set(ref _verdict, value); } }

        private string _chainSummary = string.Empty;
        public string ChainSummary { get { return _chainSummary; } private set { Set(ref _chainSummary, value); } }

        private double _toleranceMm;
        public double ToleranceMm
        {
            get { return _toleranceMm; }
            set { Set(ref _toleranceMm, value); OnPropertyChanged(nameof(ToleranceText)); }
        }
        public string ToleranceText { get { return _toleranceMm.ToString("F3") + " mm"; } }

        private double _uFinalDeg;
        /// <summary>到位时保持的 U 角（度）。同心工位 U≡0；带角度抓取填实际放料角。</summary>
        public double UFinalDeg { get { return _uFinalDeg; } set { Set(ref _uFinalDeg, value); } }

        public ObservableCollection<LiveVerifyPointVm> Points { get; } = new ObservableCollection<LiveVerifyPointVm>();

        public RelayCommand LoadChainCommand { get; }
        public RelayCommand CaptureAndPickCommand { get; }
        public RelayCommand PlanCommand { get; }
        public RelayCommand ExecuteAllCommand { get; }
        public RelayCommand ClearCommand { get; }

        private LiveVerifyReport _report = new LiveVerifyReport();
        public LiveVerifyReport Report { get { return _report; } }

        private bool _historySaved;
        /// <summary>本次会话结论是否已落盘（防重；窗口 Closed 兜底据此决定是否补写）</summary>
        public bool HistorySaved { get { return _historySaved; } }

        private BitmapSource _frameImage;
        public BitmapSource FrameImage { get { return _frameImage; } private set { Set(ref _frameImage, value); } }

        /// <summary>操作员在图上点选的像素（标定坐标系：col=u, row=v）</summary>
        private double[] _pickedPixel;
        public string PickedText
        {
            get
            {
                return _pickedPixel == null
                    ? "未点选（请在图像上点一个清晰特征点）"
                    : "已选像素 u=" + _pickedPixel[0].ToString("F1") + ", v=" + _pickedPixel[1].ToString("F1");
            }
        }

        #endregion

        #region 装载链图

        private void LoadChain()
        {
            Points.Clear();
            _pickedPixel = null;
            OnPropertyChanged(nameof(PickedText));

            if (string.IsNullOrWhiteSpace(StationCode))
            {
                ChainSummary = "未指定工位。";
                Log = "⚠ 未指定工位 —— 无法装载链图。";
                return;
            }

            string err;
            if (!ChainRuntime.TryLoadValidated(StationCode, out _graph, out err))
            {
                _graph = null;
                ChainSummary = "链图不可用（fail-closed）：" + err;
                Log = "✗ " + err;
                Verdict = "✗ 无法验证：链图未就绪";
                RefreshAll();
                return;
            }

            // 验证对象 = 吸点引导相机 + 主工具（生产实际用的那对）
            var cam = ChainRuntime.FindPickCamera(_graph);
            _cameraId = cam?.CameraId;
            var tool = _graph.Tools.FirstOrDefault(t => t.IsMaster) ?? _graph.Tools.FirstOrDefault();
            _toolId = tool?.ToolId;

            _calibZ = cam?.Meta?.CalibZHeightMm ?? 0;
            var sb = new System.Text.StringBuilder();
            sb.Append("链已装载：工位 ").Append(StationCode)
              .Append("；引导相机 ").Append(_cameraId ?? "(无 PickAnchor 边)")
              .Append("；工具 ").Append(_toolId ?? "(无工具节点)");
            if (_calibZ > 0) sb.Append("；标定高度 Z=").Append(_calibZ.ToString("F2"));
            sb.Append("；相机数 ").Append(_graph.Cameras.Count)
              .Append("，工具数 ").Append(_graph.Tools.Count)
              .Append("，边数 ").Append(_graph.Edges.Count).Append("。");
            ChainSummary = sb.ToString();

            _camera = ResolveCamera(_cameraId);
            if (_camera == null)
                Log = "⚠ 链已装载，但设备池中未找到相机 " + (_cameraId ?? "(空")
                    + " —— 请到硬件页连接该相机后重试取图（仍可手工填像素规划）。";
            else
                Log = "链已装载，相机 " + _camera.DeviceName + " 可用。";

            Verdict = "○ 尚未验证（已就绪）";
            RefreshAll();
        }

        #endregion

        #region 取图 / 点选

        private void CaptureForPick()
        {
            if (_camera == null) { Log = "⚠ 相机不可用：先到硬件页连接 " + (_cameraId ?? "相机") + "。"; return; }
            var frame = CaptureOnce(_camera);
            if (frame == null) { Log = "⚠ 取图失败（详见上一条）。"; return; }
            FrameImage = ChainWizardViewModel.FrameToBitmap(frame);
            _pickedPixel = null;
            OnPropertyChanged(nameof(PickedText));
            Log = FrameImage == null
                ? "⚠ 取到帧但像素格式不支持显示（" + frame.PixelFormat + "）——请改用支持 Mono8/RGB8 的相机格式。"
                : "已取图 " + frame.Width + "×" + frame.Height + "。请在图像上点选一个清晰特征点。";
        }

        /// <summary>由界面在图像被点击时调用（像素坐标 = 标定坐标系 col/row）。</summary>
        public void SetPickedPixel(double u, double v)
        {
            _pickedPixel = new[] { u, v };
            OnPropertyChanged(nameof(PickedText));
            PlanCommand.RaiseCanExecuteChanged();
        }

        #endregion

        #region 规划一个点（「指」）

        private void PlanFromPicked()
        {
            if (_graph == null) { Log = "⚠ 链图未装载。"; return; }
            if (_pickedPixel == null) { Log = "⚠ 尚未点选像素。"; return; }
            if (string.IsNullOrWhiteSpace(_cameraId) || string.IsNullOrWhiteSpace(_toolId))
            {
                Log = "⚠ 链图中缺引导相机或主工具 —— 无法规划（请先在链向导里指派主工具/相机角色）。";
                return;
            }

            try
            {
                var shootPose = new ChainRobotPose { X = PhotoPoseX(_cameraId), Y = PhotoPoseY(_cameraId), U = PhotoPoseU(_cameraId) };
                var p = ChainLiveVerifier.PlanPoint(_graph, StationCode, _cameraId, _toolId,
                                                    _pickedPixel[0], _pickedPixel[1],
                                                    UFinalDeg, shootPose, Points.Count + 1);
                Points.Add(new LiveVerifyPointVm(p));
                Log = "点 #" + p.Index + " 已规划：像素(" + p.PixelU.ToString("F1") + "," + p.PixelV.ToString("F1")
                    + ") → 世界(" + p.TargetWorldX.ToString("F3") + "," + p.TargetWorldY.ToString("F3")
                    + ") → 法兰(" + p.TargetFlangeX.ToString("F3") + "," + p.TargetFlangeY.ToString("F3") + ")。";
                _pickedPixel = null;
                OnPropertyChanged(nameof(PickedText));
                ExecuteAllCommand.RaiseCanExecuteChanged();
            }
            catch (ChainResolveException ex)
            {
                Log = "✗ 规划被拒（fail-closed）：" + ex.Message;
            }
            catch (Exception ex)
            {
                Log = "✗ 规划异常：" + ex.Message;
            }
        }

        #endregion

        #region 执行（「走」+「打」）

        /// <summary>
        /// 逐点执行真机闭环。★关键设计：把「标定误差」与「到位误差」分开量，绝不混算。
        ///
        ///   到位误差 = 法兰实际反馈 − 法兰目标        ← 伺服/机械跟随能力，不是标定的错
        ///   标定误差 = 复拍像素经链回算的世界点 − 世界目标 ← 标定产物本身的准确度 ★这是 L3 要的答案
        ///
        /// 为什么不能只用法兰反馈当"实测世界点"（本类初版就是这么写的，已纠正）：
        ///   FlangeToWorld(实际反馈) 减去 世界目标 = 到位残差 + 标定误差，两项叠在一起分不开。
        ///   一根机械刚度差/加减速抖动大，会让"标定明明是对的"被判成不通过；反之标定错了
        ///   但伺服凑巧补回来，又会被判通过——两种都是假结论。
        ///   故：标定误差必须来自「相机再看一眼」——相机说工件现在在哪，才是真的在哪。
        ///
        /// 复拍不可用（相机离线/在图上自动找不到特征）时，降级为「到位误差」并在点位上标注，
        /// 绝不把降级值当标定误差报出去（判据纪律：会红≠归因）。
        /// </summary>
        private void ExecuteAll()
        {
            if (_graph == null || Points.Count == 0) return;
            _report = new LiveVerifyReport
            {
                StationCode = StationCode,
                CameraId = _cameraId,
                ToolId = _toolId,
                ToleranceMm = ToleranceMm,
            };
            _historySaved = false; // 新一轮会话 ⇒ 留痕重新计

            int moved = 0, failed = 0, degraded = 0;
            foreach (var vm in Points)
            {
                var p = vm.Model;
                try
                {
                    // ① 抬安全 Z（低位平移 = 撞机）
                    if (!MoveSafeZUp()) { p.Note = "抬安全 Z 失败，跳过"; failed++; vm.Refresh(); continue; }

                    // ② 低速平移到位（目标 = 逆解出的法兰点）
                    if (!_motion.MoveToXY(p.TargetFlangeX, p.TargetFlangeY))
                    {
                        p.Note = "到位被拒：" + (_motion.LastError ?? "未知");
                        failed++; vm.Refresh(); continue;
                    }
                    moved++;

                    // ③ 法兰实际反馈 → 到位误差（诊断量，不参与标定判定）
                    var fx = _motion.GetAxisFeedback(_xAxis);
                    var fy = _motion.GetAxisFeedback(_yAxis);
                    p.ActualFlangeX = fx != null && fx.Success ? fx.Data : p.TargetFlangeX;
                    p.ActualFlangeY = fy != null && fy.Success ? fy.Data : p.TargetFlangeY;

                    // ④ 复测「打哪」：相机再看一眼 —— 标定误差的唯一可信来源
                    double awx, awy;
                    if (TryMeasureActualWorld(p, out awx, out awy))
                    {
                        ChainLiveVerifier.CompletePoint(p, awx, awy, ToleranceMm);
                    }
                    else
                    {
                        // 降级：只报到位误差，且明确标注"这不是标定误差"
                        double dxw, dyw;
                        ChainLiveVerifier.FlangeToWorld(_graph, _toolId,
                            p.ActualFlangeX, p.ActualFlangeY, UFinalDeg, out dxw, out dyw);
                        p.ActualWorldX = dxw;
                        p.ActualWorldY = dyw;
                        p.Measured = false;
                        p.Note = "复拍不可用 → 仅测到位误差（未测标定误差，不作放行依据）";
                        degraded++;
                    }

                    _report.Points.Add(p);
                    vm.Refresh();
                }
                catch (Exception ex)
                {
                    p.Note = "异常：" + ex.Message;
                    failed++; vm.Refresh();
                }
            }

            Verdict = _report.VerdictText;
            Log = "执行完毕：移动 " + moved + " 点，失败 " + failed + " 点，复拍降级 " + degraded + " 点。"
                + " 最大偏差 " + (_report.MaxErrorMm.HasValue ? _report.MaxErrorMm.Value.ToString("F4") : "—") + " mm；"
                + " 平均 " + (_report.MeanErrorMm.HasValue ? _report.MeanErrorMm.Value.ToString("F4") : "—") + " mm。"
                + (degraded > 0 ? "★注意：降级点未计标定误差，结论不完整。" : string.Empty);
            TrySaveHistory(); // ★P0-3：结论即落盘，不依赖操作员记得手动留痕
            RefreshAll();
        }

        /// <summary>
        /// ★★P0-3（2026-09-29）：把本次验证结论落盘到工位 Calib\LiveVerify_History.json。
        /// 此前结论只在会话内存、关窗即丢——"能不能投产"的答案必须留得下来。
        /// Try 语义：失败只写日志不弹窗（留痕不能变成新故障点）；已写过则跳过（防重）。
        /// 窗口 Closed 兜底也会调本方法（中途关窗同样留痕）。
        /// </summary>
        public void TrySaveHistory()
        {
            if (_historySaved) return;
            if (_report == null || _report.Points.Count == 0) return;
            string err;
            if (ChainLiveVerifier.AppendHistory(StationCode, _report, UFinalDeg, out err))
            {
                _historySaved = true;
                Log += "\n留痕：结论已写入 Calib\\" + ChainLiveVerifier.HistoryFileName
                     + "（标定中心页头链状态 chip 会显示上次验证摘要）。";
            }
            else
            {
                Log += "\n⚠ 留痕失败：" + err + "（不影响本次验证结论）";
            }
        }

        /// <summary>
        /// 复拍并回算"打到的世界点"。策略：
        ///   EIH（相机随工具动）：到位后（工具已压在目标世界点上方）复拍，特征应仍在视野内；
        ///     用到位时的真实位姿 + H_FC 把新像素链到世界 → 即工具尖端实际落点。
        ///   ETH（相机固定）：复拍后用【同一拍照位姿】链到世界（相机没动，位姿不变）。
        /// 本方法只做"取新帧 + 用操作员框选/复用的像素"；自动特征匹配不在本层（留给视觉工位实现）。
        /// 返回 false ⇒ 调用方降级。
        /// </summary>
        private bool TryMeasureActualWorld(LiveVerifyPointResult p, out double wx, out double wy)
        {
            wx = wy = 0;
            if (_camera == null) return false;
            var frame = CaptureOnce(_camera);
            if (frame == null) return false;

            // 复拍后特征点像素：优先用操作员在复拍图上点的位置；未点则退化为原像素（假设静止重合——
            // 同心/无旋转工位成立，带 U 工位需操作员重点，故此处只作"和原像素的位移"之用）。
            double u = _rePickPixel != null ? _rePickPixel[0] : p.PixelU;
            double v = _rePickPixel != null ? _rePickPixel[1] : p.PixelV;

            var cam = _graph.FindCamera(_cameraId);
            if (cam == null) return false;

            var pose = new ChainRobotPose();
            if (cam.Mount == ChainCameraMount.EyeInHand)
            {
                // 工具现在停在法兰目标上；相机随动 ⇒ 求值位姿 = 当前法兰位姿
                pose.X = p.ActualFlangeX;
                pose.Y = p.ActualFlangeY;
                pose.U = UFinalDeg;
            }
            else
            {
                pose.X = PhotoPoseX(_cameraId);
                pose.Y = PhotoPoseY(_cameraId);
                pose.U = PhotoPoseU(_cameraId);
            }

            ChainEngine.ResolvePixelToWorld(_graph, _cameraId, u, v, pose, out wx, out wy, StationCode);
            _rePickPixel = null;
            return true;
        }

        private double[] _rePickPixel;

        /// <summary>复拍后操作员点选新像素（与首次点选同一坐标系）。</summary>
        public void SetRePickPixel(double u, double v)
        {
            _rePickPixel = new[] { u, v };
        }

        private bool MoveSafeZUp()
        {
            double z = _calibZ > 0 ? Math.Max(_calibZ, _safeZ) : _safeZ;
            return _motion.MoveToZ(z);
        }

        #endregion

        #region 设备/档案解析

        private ICamera ResolveCamera(string cameraId)
        {
            try
            {
                var all = _devicePool?.GetAllDevices();
                if (all == null) return null;
                var cams = all.OfType<ICamera>().ToList();
                if (cams.Count == 0) return null;
                // 优先按设备名/键包含相机 id 匹配（如设备名带 UpCamera 对应 Cam_A）
                if (!string.IsNullOrWhiteSpace(cameraId))
                {
                    var hit = cams.FirstOrDefault(c =>
                        (!string.IsNullOrEmpty(c.DeviceName) && c.DeviceName.IndexOf(cameraId, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (!string.IsNullOrEmpty(c.DeviceKey) && c.DeviceKey.IndexOf(cameraId, StringComparison.OrdinalIgnoreCase) >= 0));
                    if (hit != null) return hit;
                }
                return cams.First();
            }
            catch { return null; }
        }

        private IMotionCard ResolveMotionCard()
        {
            try
            {
                var all = _devicePool?.GetAllDevices();
                return all?.OfType<IMotionCard>().FirstOrDefault();
            }
            catch { return null; }
        }

        private double PhotoPoseX(string camId)
        {
            var cam = _graph?.FindCamera(camId);
            return cam?.PhotoPose != null && cam.PhotoPose.Length >= 3 ? cam.PhotoPose[0] : 0;
        }
        private double PhotoPoseY(string camId)
        {
            var cam = _graph?.FindCamera(camId);
            return cam?.PhotoPose != null && cam.PhotoPose.Length >= 3 ? cam.PhotoPose[1] : 0;
        }
        private double PhotoPoseU(string camId)
        {
            var cam = _graph?.FindCamera(camId);
            return cam?.PhotoPose != null && cam.PhotoPose.Length >= 3 ? cam.PhotoPose[2] : 0;
        }

        #endregion

        #region 取帧（照抄向导纪律：软触发，绝不沿用旧帧）

        private FrameEventArgs CaptureOnce(ICamera cam)
        {
            if (cam.State != DeviceState.Connected)
            {
                var cr = cam.Connect();
                if (cr == null || !cr.Success) { Log = "⚠ 相机连接失败：" + (cr?.Message ?? "无应答"); return null; }
            }
            cam.ConfigureSoftwareTrigger();
            var sr = cam.StartGrabbing();
            if (sr == null || !sr.Success) { Log = "⚠ 启动采集流失败：" + (sr?.Message ?? "无应答"); return null; }

            _latestFrame = null;
            cam.FrameReceived -= OnFrame;
            cam.FrameReceived += OnFrame;
            _captureActive = true;
            try
            {
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    _frameEvent.Reset();
                    _latestFrame = null;
                    var trig = cam.SoftTrigger();
                    if (trig == null || !trig.Success) trig = cam.SoftwareTrigger();
                    if (trig == null || !trig.Success) { Thread.Sleep(150); continue; }
                    if (_frameEvent.WaitOne(attempt == 1 ? 2500 : 800) && _latestFrame != null) return _latestFrame;
                    if (attempt < 5) Thread.Sleep(80);
                }
            }
            finally { _captureActive = false; }
            Log = "⚠ 软触发 5 次均未等到新帧——本次放弃（不沿用旧图）。";
            return null;
        }

        private FrameEventArgs _latestFrame;
        private volatile bool _captureActive;
        private readonly ManualResetEvent _frameEvent = new ManualResetEvent(false);

        private void OnFrame(object sender, FrameEventArgs e)
        {
            if (!_captureActive) return;
            _latestFrame = e;
            _frameEvent.Set();
        }

        #endregion

        private void RefreshAll()
        {
            OnPropertyChanged(nameof(Report));
            foreach (var p in Points) p.Refresh();
        }
    }
}
