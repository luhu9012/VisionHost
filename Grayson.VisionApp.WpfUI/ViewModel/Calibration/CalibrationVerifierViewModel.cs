//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationVerifierViewModel.cs
// 说 明: P3 标定校验台会话 ViewModel（2026-09-05 一期 = 在线打点验收 B 块；二期 = 离线残差体检 A 块）。
//        B 块闭环：抓拍定格 → 图上点选实物特征(覆盖层收点击+宿主 TryGetImagePointAt 换图像坐标)
//        → MapPixelToWorld 建议机械坐标(不动轴) → 低速到位(CalibrationMotionFacade)
//        → 目视判定 通过/偏差(偏移量) → 逐点记录 → 生成 CalibrationVerificationRecord
//        交回标定中心持久化(profile.VerificationRecords)。无矩阵/设备时逐级禁用并提示。
//        A 块闭环：读档案采样快照(profile.Samples)全量重投影(不动轴不抓拍)→ 逐点残差
//        e = 机械真值 − H(像素)（mm）→ RMS/MAX/超差统计 → 行表 + 图上标记(黄=记录像素点、
//        青=H⁻¹(真值)反投影点，差>2px 画两十字呈残差向量)。
//        边界：到位只驱动 XY(拍照面)，Z 不动（EyeInHand 打点验收语义；EyeToHand 需 Z 下探
//        场景留现场扩展）。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Interfaces;
using Grayson.Vision.Contracts.Devices.Models;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>校验台会话 VM：绑定一个 CalibrationProfile，驱动相机/运动卡做"点哪去哪"验收。
    /// 分步几何校验（⓪矩阵体检/①H正向/②H反向/③九点回放/④H+e+t全量）见
    /// CalibrationVerifierViewModel.Geometry.cs（同 partial 类的另一半）。</summary>
    public partial class CalibrationVerifierViewModel : ViewModelBase
    {
        private readonly ICalibrationService _calibService;
        private readonly IDevicePool _devicePool;
        private readonly HalconImageRenderService _renderService = new HalconImageRenderService();
        private CalibrationMotionFacade _facade;

        // ---- 相机取流会话状态（2026-09-06 自愈重构，约定与站监控/模板采集/相机实时一致） ----
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private TaskCompletionSource<bool> _firstFrameTcs;   // 首帧信号（开流/抓拍等待）
        private bool _weStartedGrabbing;                     // 本会话自己发起且正在运行的取流（定格/收尾时停）
        private bool _streamActive;                          // 会话处于"已请求出帧"（自开或搭车外部流）
        private int? _triggerModeToRestore;                  // 打开前触发模式（临时切连续，收尾恢复）
        private DateTime _lastLiveRenderAt = DateTime.MinValue; // 实时帧上屏节流（~20fps）
        private bool _sessionFirstFramePending;                 // 本次取流首帧必须上屏（节流不拦首帧）
        private (double X, double Y, double Z, double U)? _photoPose; // 抓拍定格瞬间的机械位（换算语义定案判据）

        /// <summary>取流过程中禁用设备下拉切换（防中途换相机导致会话状态错乱）</summary>
        public bool IsDeviceSwitchEnabled => !IsBusy;

        public CalibrationProfile Profile { get; }

        /// <summary>图像显示（HalconImageDisplayHost 的 DataContext）</summary>
        public ImageDisplayVm ImageDisplay { get; }

        /// <summary>本次会话产生的校验记录（标定中心在窗口关闭后取走持久化）</summary>
        public CalibrationVerificationRecord PendingRecord { get; private set; }
        public bool HasPendingRecord => PendingRecord != null && PendingRecord.Points.Count > 0;

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
        /// <summary>顶部状态条（矩阵缺失/设备缺失等）</summary>
        public string SessionNote
        {
            get => _sessionNote;
            set => Set(ref _sessionNote, value);
        }

        private string _pickInfoText = "尚未点选特征点";
        /// <summary>点选反馈（像素 + 建议机械坐标，不动轴）</summary>
        public string PickInfoText
        {
            get => _pickInfoText;
            set => Set(ref _pickInfoText, value);
        }

        private bool _hasPick;
        public bool HasPick { get => _hasPick; set { if (Set(ref _hasPick, value)) RaiseCanExecutes(); } }

        private double _pickRow;
        private double _pickCol;
        private double _pickWorldX;
        private double _pickWorldY;
        private bool _moveRotReady;        // O(旋转中心) 与 e(真吸嘴偏心) 齐备 → 执行时可按任意 U 角精确补偿
        private double _moveBaseX;         // 特征 Base 真位 X_obj（= P_photo + O − H(u)，与 U 无关；执行时减 R(U−U0)·e 得命令位）
        private double _moveBaseY;
        private double _moveTargetX;        // 走位目标 X（定案式 P_go = X_obj − R(U_go−U0)·e 算出的机械手命令位）
        private double _moveTargetY;        // 走位目标 Y
        private bool _poseCorrectionApplied; // 走位目标是否已含锚点差式修正（R_n 就绪时=true）

        // ---- 吸嘴对准工件锚点 R_n（2026-09-06 与 BasePos 解耦：BasePos=九点网格中心/相机取景位，
        //      锚点=吸嘴尖压住工件特征时回转中心坐标，换算必需。会话内新记仅存本 VM，关窗后交回标定中心落库）----
        public double NozzleAlignX { get; private set; }
        public double NozzleAlignY { get; private set; }
        public double? NozzleAlignZ { get; private set; }
        public bool NozzleAlignDirty { get; private set; }

        // ---- 相机安装特性（2026-09-08 泛化：本机相机固定不随 Z；其他工位相机可能随 Z 升降）----
        private bool? _cameraMovesWithZ;
        /// <summary>相机是否随 Z 升降（档案字段镜像；三态 CheckBox：勾=随Z / 不勾=固定 / 中间=未声明）</summary>
        public bool? CameraMovesWithZ
        {
            get => _cameraMovesWithZ;
            set
            {
                if (_cameraMovesWithZ != value)
                {
                    _cameraMovesWithZ = value;
                    CameraMountDirty = true;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CameraMountInfoText));
                }
            }
        }
        public bool CameraMountDirty { get; private set; }
        /// <summary>消费口径：相机是否随 Z（null 未声明→保守按 true 守护并提示声明）</summary>
        public bool CameraZLinked => _cameraMovesWithZ ?? true;
        /// <summary>相机安装特性说明文案（三态提示）</summary>
        public string CameraMountInfoText => CameraZLinked
            ? "相机随 Z 升降：拍照/点选/走位须回标定高度 CalibZ（像素当量纪律）"
            : "相机固定不随 Z：成像与 Z 无关；目视贴面判定须下压至 R_nZ（无投影视差）";
        /// <summary>锚点是否可用：本会话刚记 或 档案已有</summary>
        public bool NozzleAlignReady => NozzleAlignDirty || Profile.IsNozzleAlignSet;

        private string _nozzleAlignInfoText = "未记录 —— JOG 吸嘴1 尖对准目标特征后点右侧按钮";
        public string NozzleAlignInfoText
        {
            get => _nozzleAlignInfoText;
            set => Set(ref _nozzleAlignInfoText, value);
        }

        // ---- 吸嘴尖对准像素 p_tip（2026-09-06 v3 差分式消费锚，字段语义见 CalibrationProfile.ToolAlignPixelX）----
        private (double X, double Y, double Z, double U)? _anchorPose;  // 记锚点时刻机位（压住位，差分式 p_tip 的 XY 基准）
        public bool ToolAlignDirty { get; private set; }
        /// <summary>对准像素是否可用：本会话刚记 或 档案已有</summary>
        public bool ToolAlignReady => ToolAlignDirty || Profile.IsToolAlignPixelSet;

        private string _toolAlignInfoText = "未记录 —— 记锚点后抬Z定格点选同一特征，点【📐 记为对准像素】";
        public string ToolAlignInfoText
        {
            get => _toolAlignInfoText;
            set => Set(ref _toolAlignInfoText, value);
        }

        // ---- 平移差分自检状态（2026-09-06：矩阵线性/当量/镜像 与机械自洽的工件无关判据）----
        private int _diffCheckStage;                       // 0=待开始 1=已记第一点待第二点
        private (double X, double Y)? _diffCheckPose1;     // 第一点拍照机位
        private (double X, double Y)? _diffCheckW1;        // 第一点 w=H(u)
        private string _diffCheckInfoText = "平移差分自检未开始 —— 定格点选特征后点【①记点】开始";
        public string DiffCheckInfoText
        {
            get => _diffCheckInfoText;
            set => Set(ref _diffCheckInfoText, value);
        }

        private string _offsetText;
        /// <summary>偏差量输入(mm)（判定"偏差"时可选填）</summary>
        public string OffsetText
        {
            get => _offsetText;
            set => Set(ref _offsetText, value);
        }

        private string _verdictNote;
        /// <summary>判定备注（可选）</summary>
        public string VerdictNote
        {
            get => _verdictNote;
            set => Set(ref _verdictNote, value);
        }

        /// <summary>验证点明细（含未判定/已判定；汇总只统计已判定）</summary>
        public ObservableCollection<CalibrationVerificationPoint> VerificationPoints { get; } = new ObservableCollection<CalibrationVerificationPoint>();

        private string _summaryText = "尚未记录验证点";
        public string SummaryText
        {
            get => _summaryText;
            set => Set(ref _summaryText, value);
        }

        public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();

        // ==================== A 块：离线残差体检（右栏 Tab2） ====================

        /// <summary>残差容差阈值(mm)：|Δ| 在此内标记 ✅，超过标记 ⚠ 超差（体检提示口径，非强制判定）</summary>
        public const double ResidualToleranceMm = 0.5;

        private int _activeTabIndex;
        /// <summary>右栏 Tab 索引：0=在线打点(可点选)，1=残差体检(覆盖层点选关闭)，
        /// 2=分步几何校验 H 单验(可点选)，3=H+e+t 全量(可点选)</summary>
        public int ActiveTabIndex
        {
            get => _activeTabIndex;
            set
            {
                if (Set(ref _activeTabIndex, value))
                {
                    OnPropertyChanged(nameof(PickEnabled));
                    OnPropertyChanged(nameof(IsOnlineTab));
                }
            }
        }

        /// <summary>在线打点 Tab 是否激活（图上层叠覆盖层只有该 Tab 收点选）</summary>
        public bool IsOnlineTab => ActiveTabIndex == 0;

        /// <summary>覆盖层是否收点选：在线打点(0) 与 分步几何校验(2=H单验 / 3=H+e+t全量) 收；
        /// 残差体检(1) 关闭防误点。（分步区点选走 ApplyGeometryPick 分流）</summary>
        public bool PickEnabled => ActiveTabIndex == 0 || ActiveTabIndex == 2 || ActiveTabIndex == 3;

        /// <summary>分步几何校验 Tab 是否激活（2=H 单验 3=全量）</summary>
        public bool IsGeometryTab => ActiveTabIndex == 2 || ActiveTabIndex == 3;

        private string _residualHintText;
        /// <summary>体检区顶部说明（数据源状态/操作引导）</summary>
        public string ResidualHintText
        {
            get => _residualHintText;
            set => Set(ref _residualHintText, value);
        }

        private string _residualSummaryText;
        /// <summary>体检汇总：点数/RMS/MAX/超差计数</summary>
        public string ResidualSummaryText
        {
            get => _residualSummaryText;
            set => Set(ref _residualSummaryText, value);
        }

        /// <summary>残差行明细（全量重投影逐点结果）</summary>
        public ObservableCollection<ResidualRowModel> ResidualRows { get; } = new ObservableCollection<ResidualRowModel>();

        public bool HasResidualRows => ResidualRows.Count > 0;

        // ==================== 命令 ====================

        public ICommand StartLiveCommand { get; }
        public ICommand CaptureCommand { get; }
        public ICommand MoveToTargetCommand { get; }
        public ICommand SaveNozzleAlignCommand { get; }
        public ICommand SaveToolAlignCommand { get; }
        public ICommand DiffCheckCommand { get; }
        public ICommand VerdictPassCommand { get; }
        public ICommand VerdictFailCommand { get; }
        public ICommand ResetPickCommand { get; }
        public ICommand SaveRecordCommand { get; }

        public CalibrationVerifierViewModel(CalibrationProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _calibService = new CalibrationService();
            _devicePool = App.StationHostRuntime != null ? App.StationHostRuntime.DevicePool : null;
            ImageDisplay = new ImageDisplayVm(_renderService);

            StartLiveCommand = new RelayCommand(StartLive, () => CanCamera());
            CaptureCommand = new RelayCommand(CaptureFrame, () => CanCamera() && !IsBusy);
            MoveToTargetCommand = new RelayCommand(_ => MoveToTarget(), _ => CanMove());
            SaveNozzleAlignCommand = new RelayCommand(_ => SaveNozzleAlign(), _ => SelectedMotion != null && !IsBusy);
            SaveToolAlignCommand = new RelayCommand(_ => SaveToolAlignFromPick(), _ => HasPick && !IsBusy);
            DiffCheckCommand = new RelayCommand(_ => DiffCheckStep(), _ => HasPick && !IsBusy);
            VerdictPassCommand = new RelayCommand(_ => Verdict(true), _ => HasPick && !IsBusy);
            VerdictFailCommand = new RelayCommand(_ => Verdict(false), _ => HasPick && !IsBusy);
            ResetPickCommand = new RelayCommand(_ => ResetPick(), _ => HasPick);
            SaveRecordCommand = new RelayCommand(_ => SaveRecord(), _ => VerificationPoints.Any(p => p.IsVerdicted));

            // 吸嘴对准工件锚点：档案已有则回显；未记录则提示操作（换算与走位都依赖它，不再用 BasePos 当锚）
            NozzleAlignX = Profile.NozzleAlignX;
            NozzleAlignY = Profile.NozzleAlignY;
            NozzleAlignZ = Profile.NozzleAlignZ;
            _cameraMovesWithZ = Profile.CameraMovesWithZ;
            CameraMountDirty = false;
            // v3 口径(2026-09-06)：像素差分换算只依赖 p_tip；R_n 退化为 p_tip 标定基准 + 工件位移诊断参照
            string rnZShow = Profile.NozzleAlignZ.HasValue
                ? $" 压住高度 Z={Profile.NozzleAlignZ.Value:F1}mm(目视下压目标)"
                : "";
            NozzleAlignInfoText = Profile.IsNozzleAlignSet
                ? $"已记录基准位 R_n=({Profile.NozzleAlignX:F3},{Profile.NozzleAlignY:F3})mm{rnZShow} —— 作 p_tip 标定基准/工件位移诊断参照(像素差分不再直接依赖它)"
                : "未记录 —— ① JOG 工具头尖压住目标特征 → ② 点【📍 记工具对准锚点】(对针三步预置第一步)";
            ToolAlignInfoText = Profile.IsToolAlignPixelSet
                ? $"已记录对准像素 p_tip=({Profile.ToolAlignPixelX:F1},{Profile.ToolAlignPixelY:F1}) —— 设备常量已就绪,日常校验直接: 放好工件(任意位)→抓拍定格→点选特征→到位;仅拆装相机/工具头后才需重记"
                : _toolAlignInfoText;

            MatrixPath = ResolveMatrixPath();
            LoadDevices();
            if (!IsMatrixReady)
            {
                SessionNote = "该方案尚无标定矩阵(未完成标定或矩阵文件缺失)——无法校验。请先进入标定向导完成标定并保存。";
                AppendLog("校验不可用: 未找到矩阵文件。");
            }
            else
            {
                bool hasTco = Profile.IsToolOffsetCalibrated
                              && (Math.Abs(Profile.ToolOffsetWx) > 1e-6
                                  || Math.Abs(Profile.ToolOffsetWy) > 1e-6);
                SessionNote = ToolAlignReady
                    ? (hasTco
                        ? "校验就绪(定案式): 放好工件(任意位)→ 抓拍定格 → 点选特征 → 到位 → 目视判定。O 与 e 已标——任意回转角到位均自动旋转补偿。"
                        : "校验就绪(v5): 放好工件(任意位)→ 抓拍定格 → 点选特征 → 到位 → 目视判定。p_tip 设备常量已就绪,无需重复三步预置。")
                    : "⚠ 缺对准像素 p_tip —— 对针三步预置仅需一次: ①JOG 工具头尖压住工件特征 → 点【📍 记工具对准锚点】②抬Z离开特征(XY不动,相机固定任意高度/随Z则回标定高度)抓拍定格并点选该特征 ③点【📐 记为对准像素】;此后工件随便摆,直接点选走位。";
                AppendLog("校验台就绪, 矩阵: " + MatrixPath);
                // 诊断辅助(2026-09-06)：把档案几何一次打全，点选判偏差时可直接离线对照，无需另翻档案。
                double eMag = Math.Sqrt(Profile.ToolEccWx * Profile.ToolEccWx + Profile.ToolEccWy * Profile.ToolEccWy);
                string rnZ = Profile.NozzleAlignZ.HasValue
                    ? $" Z={Profile.NozzleAlignZ.Value:F1}mm(压住高度 R_nZ——目视/贴面判定请下压到此)"
                    : "";
                AppendLog($"[档案几何] BasePos(网格取景)=({Profile.BasePosX:F3},{Profile.BasePosY:F3})" +
                          $"  NozzleAlign(工具锚点R_n)={(Profile.IsNozzleAlignSet ? $"({Profile.NozzleAlignX:F3},{Profile.NozzleAlignY:F3}){rnZ}" : "未设置")}" +
                          $"  U0={(Profile.CalibU0.HasValue ? Profile.CalibU0.Value.ToString("F2") : "null")}°" +
                          $"  Ecc=({Profile.ToolEccWx:F2},{Profile.ToolEccWy:F2})mm |e|={eMag:F1}mm" +
                          $"  TCO(ToolOffset)={(hasTco ? $"({Profile.ToolOffsetWx:F3},{Profile.ToolOffsetWy:F3})mm" : "未标")}" +
                          $"  旋转中心World=({Profile.ToolCenterWx:F2},{Profile.ToolCenterWy:F2})" +
                          $"  标定Z={(Profile.CalibZ.HasValue ? Profile.CalibZ.Value.ToString("F1") : "未记录")}mm" +
                          // 2026-09-09：RmsError 为 0 通常是"没写入"（矩阵系复用/导入而来，未经本档案拟合），
                          // 直接打 0.000 会被误读成"完美拟合"。未写入时改指向九点回放的实测值。
                          $"  RMS={(Profile.RmsError > 1e-9 ? Profile.RmsError.ToString("F3") + "mm" : "未写入(以九点回放为准)")}" +
                          (Profile.NozzleAlignZ.HasValue
                              ? "  ★目视/贴面判定请下压 Z 至 R_nZ（尖才够到工件面；无投影视差）"
                              : (CameraZLinked
                                  ? "  ★相机随 Z 升降：拍照/定格须回标定高度 CalibZ，否则像素当量失真"
                                  : "  ★相机固定不随 Z：成像与 Z 无关——目视对准需回工件面高度才无投影视差")));
            }
            InitResidualSection();
            InitGeometrySection();
            RefreshSummary();
        }

        // ==================== A 块：离线残差体检 ====================

        /// <summary>进入体检区时的数据源状态说明（矩阵缺失/无采样快照引导）</summary>
        private void InitResidualSection()
        {
            if (!IsMatrixReady)
            {
                ResidualHintText = "无标定矩阵，无法离线体检（矩阵缺失或方案未完成标定）。";
                return;
            }
            int batch = Profile.Samples?.Count ?? 0;
            int pts = Profile.Samples?.Sum(s => s.Points?.Count(p => p.IsCaptured) ?? 0) ?? 0;
            ResidualHintText = pts > 0
                ? $"离线体检数据源就绪：采样批次 {batch}，已采集标定点 {pts} 个（含机械真值+像素位）。点击下方按钮全量重投影计算逐点残差；图上十字需与标定同一取景才有实物意义。"
                : "该方案档案里没有采样点快照（Samples 为空）。离线体检依赖向导保存标定时的采集点：请用「标定向导」重新完成一次标定并保存（自动写入快照），再回来体检；也可直接用左侧「在线打点」验收。";
        }

        /// <summary>
        /// P3-A 离线残差体检（不动轴不抓拍）：读档案采样快照(profile.Samples) 全量重投影。
        /// 逐点：world_pred = H(像素)（与 ApplyPick 同通道 MapPixelToWorld(px=PixelX,py=PixelY)）；
        ///       残差 e = 机械真值 − world_pred（mm）；反投影像素 = H⁻¹(机械真值)（图上可视化用）。
        /// 填充 ResidualRows / ResidualSummaryText；返回成功换算点数（0 = 无数据/失败）。
        /// 图上叠十字由视图层读取 ResidualRows 调宿主 AddMarkerCross 完成（黄=记录点，青=反投影点）。
        /// </summary>
        public int RunResidualAudit()
        {
            ResidualRows.Clear();
            ResidualSummaryText = null;
            if (!IsMatrixReady)
            {
                ResidualHintText = "无标定矩阵，无法离线体检。";
                return 0;
            }
            var pts = Profile.Samples?
                .SelectMany(s => s.Points ?? new ObservableCollection<CalibrationPointModel>())
                .Where(p => p.IsCaptured)
                .ToList();
            int total = pts?.Count ?? 0;
            if (total == 0)
            {
                InitResidualSection();
                AppendLog("残差体检: 档案无采样点(Samples 为空)——请先用标定向导完成标定并保存。");
                return 0;
            }

            int order = 0, over = 0, failFwd = 0;
            double sumSq = 0, maxErr = 0;
            foreach (var p in pts)
            {
                var fwd = _calibService.MapPixelToWorld(MatrixPath, p.PixelX, p.PixelY);
                if (!fwd.Success)
                {
                    failFwd++;
                    continue;
                }
                order++;
                double predX = fwd.Data.WorldX, predY = fwd.Data.WorldY;
                double backX = double.NaN, backY = double.NaN;
                var bwd = _calibService.MapWorldToPixel(MatrixPath, p.WorldX, p.WorldY);
                if (bwd.Success)
                {
                    backX = bwd.Data.PixelX;
                    backY = bwd.Data.PixelY;
                }
                double dx = p.WorldX - predX;
                double dy = p.WorldY - predY;
                double err = Math.Sqrt(dx * dx + dy * dy);
                sumSq += err * err;
                if (err > maxErr) maxErr = err;
                bool overTol = err > ResidualToleranceMm;
                if (overTol) over++;
                ResidualRows.Add(new ResidualRowModel
                {
                    Order = order,
                    PixelX = p.PixelX,
                    PixelY = p.PixelY,
                    TrueX = p.WorldX,
                    TrueY = p.WorldY,
                    PredX = predX,
                    PredY = predY,
                    BackX = backX,
                    BackY = backY,
                    DxMm = dx,
                    DyMm = dy,
                    AbsErrMm = err,
                    Reliable = p.IsReliable,
                    StatusText = overTol ? "⚠ 超差" : "✅"
                });
            }
            int n = order;
            double rms = n > 0 ? Math.Sqrt(sumSq / n) : 0;
            ResidualSummaryText =
                $"采样 {total} 点 · 成功换算 {n} 点 · RMS {rms:F3} mm · MAX {maxErr:F3} mm" +
                $" · 超差(>{ResidualToleranceMm:0.0}mm) {over} 点" +
                (failFwd > 0 ? $" · 换算失败 {failFwd} 点" : "");
            AppendLog($"残差体检完成: RMS={rms:F3}mm MAX={maxErr:F3}mm 超差 {over}/{n} 点(容差 {ResidualToleranceMm:0.0}mm)。");
            OnPropertyChanged(nameof(HasResidualRows));
            return n;
        }

        // ==================== 设备生命周期 ====================

        private void LoadDevices()
        {
            if (_devicePool == null)
            {
                AppendLog("警告: DevicePool 未初始化，无法加载硬件列表(仅建议模式可用)。");
                return;
            }
            foreach (var cam in _devicePool.GetAllDevices().OfType<ICamera>())
            {
                CameraDeviceList.Add(cam);
            }
            foreach (var motion in _devicePool.GetAllDevices().OfType<IMotionCard>())
            {
                MotionDeviceList.Add(motion);
            }

            // 优先选 profile 历史绑定的设备；否则第一个
            SelectedCamera = CameraDeviceList.FirstOrDefault(c => IsBoundDevice(c, Profile.CameraId))
                             ?? CameraDeviceList.FirstOrDefault();
            SelectedMotion = MotionDeviceList.FirstOrDefault(m => IsBoundDevice(m, Profile.AxisId))
                             ?? MotionDeviceList.FirstOrDefault();

            if (SelectedCamera == null) AppendLog("提示: 无在线相机——抓拍不可用。");
            if (SelectedMotion == null) AppendLog("提示: 无在线运动卡——低速到位不可用(仅建议坐标)。");
        }

        /// <summary>切换相机：退订旧相机帧事件并清理其取流会话（设备连接保留——设备池共享），再订阅新相机。</summary>
        private void OnSelectedCameraChanged(ICamera oldCamera, ICamera newCamera)
        {
            if (oldCamera != null)
            {
                oldCamera.FrameReceived -= OnCameraFrameReceived;
                // 换相机即放弃旧相机的取流会话（若本会话在旧相机上开过流）
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
        }

        // ==================== 相机：实时/抓拍（自愈取流） ====================

        private bool CanCamera() => SelectedCamera != null && IsMatrixReady && !IsBusy;

        /// <summary>
        /// ▶ 实时预览：自愈开流（自动连接 + 连续采集模式 + 开流）并持续上屏；
        /// 5 秒首帧看门狗——收到首帧即成功，超时给排查结论并自动收尾，绝不静默。
        /// 说明：向导采样/工位采图后相机常残留"软触发/外触发"模式，直接开流不出帧，
        /// 必须先把 TriggerModeSelect 切回 0(连续)——这正是"点了没画面"的主因。
        /// </summary>
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
                AppendLog("相机已在取流（实时预览或定格中）。可直接在画面上点选特征。");
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

        /// <summary>
        /// 📷 抓拍定格：本会话已在取流则直接停流定格当前画面；否则自愈开流并在收到首帧后定格，
        /// 该帧即点选基准。相机被其他页面占用时无法真正定格，会明确提示（不静默沿用旧图）。
        /// </summary>
        private async Task CaptureFrame()
        {
            if (IsBusy) return;
            ResetPick();
            _photoPose = null; // 新一次抓拍前作废上一轮定格机位（防旧位误判）
            if (_weStartedGrabbing)
            {
                // 本会话实时取流中 → 停流定格（画面保持最后一帧）
                StopOwnedGrabbing("抓拍定格");
                _streamActive = false;
                TrySnapshotPhotoPose(); // 定格完成瞬间记录机械位（=成像时刻）
                SessionNote = "校验就绪: 画面已定格 —— 点击图上实物特征点 → 低速到位 → 目视判定。";
                AppendLog("画面已定格 —— 请点击图上实物特征点作为目标（可再点「实时预览」恢复）。");
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

        /// <summary>
        /// 自愈开流核心（实时/抓拍共用；状态流转在 UI 线程，仅连接/开流放后台不卡界面）：
        /// ①未连接 → 自动 Connect（失败给原因并写状态条）
        /// ②触发模式非连续 → 临时切连续(TriggerModeSelect≠0 时 SDK 不会自动出帧；收尾恢复原模式)
        /// ③StartContinuousGrab：被其他页面占用失败 → 搭车既有流等帧
        /// ④等首帧：5 秒超时看门狗，无帧则给排查结论并自动停流复位
        /// </summary>
        private async Task<bool> OpenCameraStreamAsync(string action, bool freezeAfterFirstFrame)
        {
            var cam = SelectedCamera;
            if (cam == null)
            {
                AppendLog($"{action}: 未选择相机。");
                return false;
            }

            // ① 自动连接（GigE 相机连接可能耗时数秒 → 后台执行，不冻结界面）
            if (cam.State != DeviceState.Connected)
            {
                AppendLog($"{action}: 相机未连接，正在自动连接 {DisplayName(cam)} …");
                var conn = await Task.Run(() => cam.Connect());
                if (!ReferenceEquals(cam, SelectedCamera)) return false; // 等待期间被切换，放弃
                if (conn == null || !conn.Success)
                {
                    string msg = conn?.Message ?? "未知原因";
                    AppendLog($"{action}失败: 相机连接失败 —— {msg}");
                    SessionNote = "⚠ 相机连接失败：" + msg + " —— 请检查相机电源/网线/驱动，先在「硬件调试」确认能出图再回来。";
                    return false;
                }
                AppendLog($"{action}: 相机连接成功。");
            }

            // ② 确保连续采集模式（软/硬触发下开流不会自动出帧 → "点了没画面"的主因）
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

            // ③ 开流（失败 = 大概率已被其他页面取流 → 搭车等帧）
            var start = await Task.Run(() => cam.StartContinuousGrab());
            if (!ReferenceEquals(cam, SelectedCamera))
            {
                // 等待期间相机被切换：若刚才是我们开成功的流，先停掉再放弃
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

            // ④ 首帧看门狗：5 秒内收到帧才算成功
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
                    if (freezeAfterFirstFrame && _weStartedGrabbing)
                    {
                        StopOwnedGrabbing(action);
                        _streamActive = false;
                        TrySnapshotPhotoPose(); // 定格完成瞬间记录机械位（=成像时刻）
                        SessionNote = "校验就绪: 画面已定格 —— 点击图上实物特征点 → 低速到位 → 目视判定。";
                        AppendLog($"{action}: 画面已定格 —— 请点击图上实物特征点作为目标。");
                    }
                    else if (!freezeAfterFirstFrame)
                    {
                        SessionNote = "校验就绪: 实时预览中 —— 可直接点选已见特征；「📷 抓拍定格」可冻结画面。";
                        AppendLog($"{action}: 首帧已上屏，实时预览中。");
                    }
                    else
                    {
                        // 搭车外部流：不能停别人的流 → 明确告知，不假装定格
                        _streamActive = true;
                        AppendLog($"{action}: 相机由其他页面取流，画面无法真正定格（会持续更新）。可到取流页面停止后重试，或直接在实时画面点选。");
                    }
                    return true;
                }

                // 无帧 → 给排查结论并自愈复位（不留僵尸流）
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
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("校验台收尾停流异常: " + ex.Message); }
                    RestoreTriggerMode(cam);
                }
            }
            _weStartedGrabbing = false;
            _streamActive = false;
            _triggerModeToRestore = null;
            _firstFrameTcs = null;
            _sessionFirstFramePending = false;
            try
            {
                ImageDisplay.Clear(); // 释放本窗口 HImage 句柄
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("校验台清空显示异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 相机帧回调（相机 SDK 线程）：只做两件事——①标记首帧信号（开流等待用）；
        /// ②marshal 到 UI 线程转 HImage 上屏（节流 ~20fps）。上下文按固定 NodeId 走
        /// AddOrUpdateImageContext 复用同一历史槽，旧帧由替换路径安全释放（防 HALCON 句柄泄漏）。
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
                    // 上屏节流：实时流 ~20fps；本次取流首帧不拦（抓拍定格必须把首帧画出来）
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
                        DisplayName(SelectedCamera), "VerifierLive");
                    if (context == null) return;
                    // 固定 NodeId 复用同一历史槽，由 AddOrUpdateImageContext 安全释放上一帧
                    ImageDisplay.AddOrUpdateImageContext(context);
                }
                catch (Exception ex)
                {
                    // 单帧渲染失败丢弃，不影响后续帧
                    System.Diagnostics.Debug.WriteLine("校验台帧处理异常: " + ex.Message);
                }
            }), DispatcherPriority.Background);
            tcs?.TrySetResult(true);
        }

        // ==================== 点选 → 建议坐标 ====================

        /// <summary>视图层点选回调(覆盖层鼠标按下 → 视口坐标 → 图像坐标 row/col)</summary>
        public void ApplyPick(double row, double col)
        {
            // 分步几何校验区（Tab2=H单验 / Tab3=H+e+t全量）：走独立链路，不进 v5 在线打点定案
            if (IsGeometryTab)
            {
                ApplyGeometryPick(row, col);
                return;
            }
            if (!IsMatrixReady)
            {
                AppendLog("无矩阵, 无法换算建议坐标。");
                return;
            }
            _pickRow = row;
            _pickCol = col;

            var res = _calibService.MapPixelToWorld(MatrixPath, col, row); // 参数 (px=x=col, py=y=row)
            if (!res.Success)
            {
                PickInfoText = $"点选 ({col:F1},{row:F1}) 换算失败: {res.Message}";
                AppendLog("坐标换算失败: " + res.Message);
                HasPick = false;
                return;
            }
            _pickWorldX = res.Data.WorldX; // 矩阵裸输出 w=H(u)=工件特征世界坐标（视觉节点 OutputX/Y 同源）
            _pickWorldY = res.Data.WorldY;
            _moveTargetX = _pickWorldX;    // 走位目标默认=裸输出，下方定案块按生产引擎同源公式叠加偏心/对针补偿
            _moveTargetY = _pickWorldY;
            _poseCorrectionApplied = false;
            PickInfoText = $"像素 ({col:F1}, {row:F1}) → 矩阵换算 w=({_pickWorldX:F3}, {_pickWorldY:F3}) mm（落点定案见下）";
            HasPick = true;
            // 2026-09-09：日志必须自带像素坐标——否则事后复盘时"点了哪个点"无从查证，
            // 无法与 H 正向 / H 反向 / 九点回放的数据相互印证（本次实机复盘就卡在这里）。
            AppendLog($"点选换算(矩阵裸输出 w=H(u)): 像素(col={col:F1}, row={row:F1}) → X={_pickWorldX:F3}, Y={_pickWorldY:F3} mm。");

            // ===== 落点语义定案（2026-09-08，唯一真源 CalibrationGeometry，与生产引擎同式）=====
            // 根因：九点标定 H 的语义 = 机械手走命令位拍固定特征、拟合像素↔命令位，
            //       所以 H(u) 裸输出 w 是【命令位域】（= 让基准特征成像在 u 时机械手该停的位置），
            //       既不是工件真位、也不是拍照位，直接拿 w 当落点必然差一个固定偏移。
            // 定案消费：X_obj = P_photo + O − H(u)；P_go = X_obj − R(U_go − U0)·e
            //   O = 旋转中心（三点定圆只取圆心，半径=杆长，丢弃）   e = 真吸嘴偏心 = O − H(p_tip)
            // ⚠ 已证伪（仿真）：v5「R_go = w − TCO」451mm、v4「P_photo+H(p_tip)−H(u)」缺旋转项 27.8mm
            try
            {
                var nowPose = TryReadCurrentPose();
                // 成像时刻机位优先（抓拍定格时实读）；未走定格路径（搭车外部流/实时点选）用点选瞬间机位
                var basePose = _photoPose ?? nowPose;
                if (basePose.HasValue)
                {
                    string srcTag = _photoPose.HasValue ? "定格实读(成像时刻)" : "点选瞬间(未定格)";
                    string baseInfo = Profile.IsBasePosSet
                        ? $"  机位−BasePos(网格中心)=({basePose.Value.X - Profile.BasePosX:F3},{basePose.Value.Y - Profile.BasePosY:F3})"
                        : "";
                    AppendLog($"  拍照机位[{srcTag}]: X={basePose.Value.X:F3} Y={basePose.Value.Y:F3} Z={basePose.Value.Z:F1} U={basePose.Value.U:F1}°{baseInfo}");

                    // ★ Z 守护（2026-09-08 泛化，按档案相机安装特性 CameraMovesWithZ 分支，不写死单机假设）：
                    //   相机随 Z（true / 未声明 null 保守=随 Z）→ HomMat 是该 Z 高度 2D 仿射，Z≠CalibZ
                    //   像素当量失真（红警阻断可信度，提示回标定高度重拍）；相机固定（false）→ 成像与
                    //   Z 无关（实测同特征 Z=-10/-100/-144 H 输出一致），仅中性记录。
                    if (Profile.CalibZ.HasValue && !double.IsNaN(basePose.Value.Z))
                    {
                        double dz = basePose.Value.Z - Profile.CalibZ.Value;
                        if (Math.Abs(dz) > 5.0)
                        {
                            if (CameraZLinked)
                            {
                                AppendLog($"  ⚠⚠⚠ 拍照 Z={basePose.Value.Z:F1}mm ≠ 标定高度 {Profile.CalibZ.Value:F1}mm (Δ={dz:F1}mm)" +
                                          $" —— 相机随 Z 升降:像素当量失真,本次换算不可信!请把 Z 移回标定高度({Profile.CalibZ.Value:F1}mm)后重新抓拍定格再点选。"
                                          + (Profile.CameraMovesWithZ == null
                                              ? "（档案未声明相机安装特性,已按保守口径;若相机固定不随 Z 请勾掉『相机随 Z 升降』后保存）"
                                              : ""));
                            }
                            else
                            {
                                AppendLog($"  拍照 Z={basePose.Value.Z:F1}mm（标定 Z={Profile.CalibZ.Value:F1}mm, Δ={dz:F1}mm）——相机固定不随 Z,成像与 Z 无关,换算不受影响。");
                            }
                        }
                    }

                    if (_photoPose.HasValue && nowPose.HasValue
                        && (Math.Abs(nowPose.Value.X - _photoPose.Value.X) > 0.5
                            || Math.Abs(nowPose.Value.Y - _photoPose.Value.Y) > 0.5))
                    {
                        AppendLog($"  ⚠ 定格后机械手已移动 (Δ={nowPose.Value.X - _photoPose.Value.X:F2},{nowPose.Value.Y - _photoPose.Value.Y:F2})mm —— 本点换算不再对应定格画面,请回定格位重选或重拍。");
                    }

                    if (ToolAlignReady)
                    {
                        // ===== 消费式（唯一真源 CalibrationGeometry；仿真 200 组误差 0.000000mm）=====
                        // ===== 2026-09-08 定案消费（唯一真源 CalibrationGeometry；仿真 200 组误差 0.000000mm）=====
                        //   X_obj（特征 Base 真位）= P_photo + O − H(u)      ← EIH：相机随机械手 XY 动
                        //   P_go （机械手命令位）= X_obj − R(U_go − U0)·e
                        //   O=旋转中心(三点定圆只取圆心)  e=真吸嘴偏心=O−H(p_tip)
                        // ⚠ 已证伪：v5「R_go = w − TCO」(仿真 451mm)、v4「P_photo+H(p_tip)−H(u)」(缺旋转项 27.8mm)
                        double u0 = Profile.CalibU0 ?? 0.0;
                        double uNow = basePose.Value.U;
                        bool uKnown = !double.IsNaN(uNow);
                        double uGo = uKnown ? uNow : u0;

                        bool hasO = Profile.HasRotationCenter;
                        bool hasE = Profile.IsNozzleEccCalibrated
                                    && (Math.Abs(Profile.ToolOffsetPureWx) > 1e-9
                                        || Math.Abs(Profile.ToolOffsetPureWy) > 1e-9);
                        bool hasTip = Profile.IsToolOffsetCalibrated
                                      && Profile.ToolOffsetMethod == ToolOffsetMethod.EyeInHandIndirect;

                        // p_tip 的 H 映射（无 O 时走差分式需要）
                        double wTipX = 0, wTipY = 0;
                        bool hasTipMap = false;
                        if (hasTip)
                        {
                            var tp = _calibService.MapPixelToWorld(MatrixPath, Profile.ToolAlignPixelX, Profile.ToolAlignPixelY);
                            if (tp.Success) { wTipX = tp.Data.WorldX; wTipY = tp.Data.WorldY; hasTipMap = true; }
                        }

                        _moveRotReady = hasO && hasE;           // 是否支持"任意 U 角到位"
                        _poseCorrectionApplied = true;

                        if (hasO && hasE)
                        {
                            // ★ 完整式：base 存【特征真位 X_obj】（与 U 无关），执行时按实时 U 求命令位
                            var obj = CalibrationGeometry.ObjectBase(
                                _pickWorldX, _pickWorldY, basePose.Value.X, basePose.Value.Y,
                                Profile.ToolCenterWx, Profile.ToolCenterWy, eih: true);
                            _moveBaseX = obj.X;
                            _moveBaseY = obj.Y;
                            var cmd = CalibrationGeometry.CommandFor(obj.X, obj.Y,
                                Profile.ToolOffsetPureWx, Profile.ToolOffsetPureWy, uGo, u0);
                            _moveTargetX = cmd.X;
                            _moveTargetY = cmd.Y;
                            AppendLog($"  落点定案(定案式): X_obj=P_photo+O−H(u)=({basePose.Value.X:F3},{basePose.Value.Y:F3})+({Profile.ToolCenterWx:F3},{Profile.ToolCenterWy:F3})−({_pickWorldX:F3},{_pickWorldY:F3})"
                                      + $"=({_moveBaseX:F3},{_moveBaseY:F3}) → P_go=X_obj−R({uGo:F1}°−{u0:F1}°)·e=({_moveTargetX:F3},{_moveTargetY:F3})mm"
                                      + $"  [e=({Profile.ToolOffsetPureWx:F3},{Profile.ToolOffsetPureWy:F3})mm]");
                            if (uKnown && Math.Abs(uNow - u0) >= 2.0)
                            {
                                AppendLog($"  回转角 U={uNow:F1}° ≠ U0={u0:F1}° —— 已按 R(U−U0)·e 补偿（任意角度到位可用）。");
                            }
                        }
                        else if (hasTipMap && (!uKnown || Math.Abs(uGo - u0) < 2.0))
                        {
                            // 差分退路：无 O/e 但 U≈U0 → P_go = P_photo + H(p_tip) − H(u)（此式在 θ=U0 时精确）
                            _moveBaseX = basePose.Value.X + wTipX - _pickWorldX;
                            _moveBaseY = basePose.Value.Y + wTipY - _pickWorldY;
                            _moveTargetX = _moveBaseX;
                            _moveTargetY = _moveBaseY;
                            AppendLog($"  落点定案(差分退路, U≈U0): P_go=P_photo+H(p_tip)−H(u)=({_moveTargetX:F3},{_moveTargetY:F3})mm"
                                      + $"  [P_photo=({basePose.Value.X:F3},{basePose.Value.Y:F3}) H(p_tip)=({wTipX:F3},{wTipY:F3}) H(u)=({_pickWorldX:F3},{_pickWorldY:F3})]");
                            AppendLog("  ⚠ 档案缺旋转中心 O 或真吸嘴偏心 e：仅 U≈U0 精确。U 要旋转请先补做旋转标定（三点定圆，延伸杆长度不影响）。");
                        }
                        else
                        {
                            _moveBaseX = _pickWorldX;
                            _moveBaseY = _pickWorldY;
                            _moveTargetX = _pickWorldX;
                            _moveTargetY = _pickWorldY;
                            _moveRotReady = false;
                            AppendLog("  ⚠ 既无 O+e 也无可用 p_tip（或 U≠U0）：本次按【视觉直吸】=裸 H 输出，落点不可信。"
                                      + "请先完成：旋转中心标定 → 物理对针（自动算 e），或把 U 转回 U0 后重选。");
                        }

                        string tag = (hasO && hasE)
                            ? $"=X_obj−R(U−U0)·e（X_obj=P_photo+O−H(u)=({_moveBaseX:F3},{_moveBaseY:F3})）"
                            : "（差分/直吸退路，见日志）";
                        PickInfoText = $"像素 ({col:F1}, {row:F1}) → 走位目标 X={_moveTargetX:F3}  Y={_moveTargetY:F3} mm {tag}；裸H坐标：x={_pickWorldX:F3},y={_pickWorldY:F3}";
                        string expectTag = NozzleAlignReady
                            ? $" | R_go−R_n=({_moveTargetX - NozzleAlignX:+0.000;-0.000},{_moveTargetY - NozzleAlignY:+0.000;-0.000})mm = 工件相对记锚点时的位移(未动≈0;非零正常=工件已挪位,不是误差)"
                            : "";
                        AppendLog("  落点定案: 走位目标 P_go=(" + _moveTargetX.ToString("F3") + "," + _moveTargetY.ToString("F3") + ")mm"
                                  + "  [w=H(u_click)=(" + _pickWorldX.ToString("F3") + "," + _pickWorldY.ToString("F3") + ")"
                                  + (hasO ? "  O=(" + Profile.ToolCenterWx.ToString("F3") + "," + Profile.ToolCenterWy.ToString("F3") + ")" : "  O=未标")
                                  + (hasE ? "  e=(" + Profile.ToolOffsetPureWx.ToString("F3") + "," + Profile.ToolOffsetPureWy.ToString("F3") + ")" : "  e=未标")
                                  + "]" + expectTag);

                        // ⚠ 2026-09-09：本校验台固定按 EIH 差分式换算（eih:true），而生产引擎
                        // （MahjongDualNozzle / VisionPickPlace）走各自 ProcessConfig 的 CameraMountEih
                        // （默认 false = ETH，X_obj=H(u)）。两边若不一致，就会出现
                        // "校验台目视对准了、生产跑起来却偏"（或反之）—— 每次换算都显式声明分支。
                        AppendLog("  ℹ 分支声明：本校验台固定按【EIH 眼在手】X_obj=P_photo+O−H(u) 换算；"
                                  + "生产引擎按 ProcessConfig.CameraMountEih（默认 false=ETH → X_obj=H(u)）走，两边必须一致。");

                        // ── 落点可信度 = 矩阵形状 × 参考点距离（2026-09-09 实机复盘新增）──
                        // 九点 RMS 小 ≠ 落点准。RMS 只说明"9 个采样点彼此自洽"；
                        // 而落点用的是【差分】P_go − P_photo = H(p_tip) − H(u)，
                        // 形状失真会按「参考点 p_tip 到目标 u 的世界距离」线性放大：
                        //   误差 ≈ 失真因子 × 该距离  →  这就是"残差 0.5mm、实拍偏 20mm"的成因。
                        var shape = ProbeMatrixShape(_pickCol, _pickRow);
                        double spanMm = Math.Sqrt(Math.Pow(_moveTargetX - basePose.Value.X, 2)
                                                + Math.Pow(_moveTargetY - basePose.Value.Y, 2));
                        if (!double.IsNaN(shape.Aniso) && shape.Ok == false)
                        {
                            double estErr = shape.Distort * spanMm;
                            AppendLog($"  🔴🔴 落点不可信：H 形状非法（各向异性 σ1/σ2={shape.Aniso:F3}，应≈1.000；正交偏差 {shape.Ortho:F2}°，应≈0°）"
                                      + " —— 平面成像 + 方形像素 ⇒ H 必须是「相似+镜像」，现在被拉伸了。");
                            AppendLog($"     本次落点相对拍照机位跨 {spanMm:F1}mm，按失真 {shape.Distort * 100:F1}% 估算可能偏 ±{estErr:F1}mm"
                                      + "（距离越远偏得越多；残差小只是因为九点那 9 点彼此自洽，不代表这一段增量准）。");

                            // 给一句能立刻照做的实测预测
                            var w0 = _calibService.MapWorldToPixel(MatrixPath, basePose.Value.X, basePose.Value.Y);
                            var wX = _calibService.MapWorldToPixel(MatrixPath, basePose.Value.X + 15.0, basePose.Value.Y);
                            var wY = _calibService.MapWorldToPixel(MatrixPath, basePose.Value.X, basePose.Value.Y + 15.0);
                            if (w0.Success && wX.Success && wY.Success)
                            {
                                double px = Math.Sqrt(Math.Pow(wX.Data.PixelX - w0.Data.PixelX, 2) + Math.Pow(wX.Data.PixelY - w0.Data.PixelY, 2));
                                double py = Math.Sqrt(Math.Pow(wY.Data.PixelX - w0.Data.PixelX, 2) + Math.Pow(wY.Data.PixelY - w0.Data.PixelY, 2));
                                AppendLog($"  🔬 决定性实测（2 分钟，不改代码）：本矩阵声称 沿+X走15mm→特征动 {px:F1}px、沿+Y走15mm→特征动 {py:F1}px。"
                                          + " 真的各走一次量出真实像素位移：两数【相等】⇒ 相机各向同性、是九点数据被污染 → 重做九点；"
                                          + "【不等且比值接近上面两个数】⇒ 相机确实斜视/各向异性 → 调相机安装。");
                            }
                            AppendLog("  🔧 处置顺序：① 上面的 XY 实测定性 → ② 重做九点（全程同一 Z、每点确认走位到位、模板别误匹配）"
                                      + " → ③ 再重做对针（p_tip）→ ④ 重开校验台确认体检转 ✅。");
                        }
                        else if (shape.Ok)
                        {
                            AppendLog($"  ✅ 矩阵形状合法（各向异性 {shape.Aniso:F3}、正交偏差 {shape.Ortho:F2}°），差分消费可信；本次落点相对拍照机位跨 {spanMm:F1}mm。");
                        }
                    }
                    else
                    {
                        // 对准像素缺失：无 p_tip 锚 → 走位退化为视觉直吸 w（同心吸嘴时本即正确，此处保留引导）
                        _poseCorrectionApplied = false;
                        PickInfoText = $"像素 ({col:F1}, {row:F1}) → w=({_pickWorldX:F3},{_pickWorldY:F3}) mm —— ⚠ 未标对准像素 p_tip，走位已禁用：请按【📍 记吸嘴对准锚点】→ 抬Z定格点选同一特征 → 【📐 记为对准像素】三步完成标定后重新点选。";
                        AppendLog("  ⚠ 未标对准像素 p_tip——走位缺偏心锚，已禁用。标定流程: ① JOG 吸嘴1 尖压住工件特征点【记吸嘴对准锚点】 ② 抬 Z 让尖离开特征(XY 不动)抓拍定格 ③ 点选该特征后点【记为对准像素】。");
                    }
                }
                else
                {
                    AppendLog("  机械位姿读数失败(运动卡未连接)——无法结算落点,请连接运动卡后重试");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("点选读数诊断异常: " + ex.Message);
            }
        }

        /// <summary>JOG 吸嘴1 尖压住工件特征后调用：记录当前回转中心 XY 为换算锚点 R_n（无需拍照）</summary>
        private void SaveNozzleAlign()
        {
            var pose = TryReadCurrentPose();
            if (!pose.HasValue)
            {
                AppendLog("记锚点失败: 读不到运动卡当前位置——请先连接运动卡。");
                return;
            }
            NozzleAlignX = pose.Value.X;
            NozzleAlignY = pose.Value.Y;
            NozzleAlignZ = pose.Value.Z; // 压住高度 R_nZ（2026-09-08：目视贴面判定下压目标）
            NozzleAlignDirty = true;
            _anchorPose = pose; // 压住位机位（差分式 p_tip 的 XY 基准，SaveToolAlignFromPick 校验用）
            string uHint = "保持 U 在标定姿态(与九点标定时一致),XY 不要动";
            // 2026-09-08 泛化：按档案相机安装特性给抬 Z 指引（随 Z → 回标定高度当量纪律；固定 → 露特征即可）
            string zHint = CameraZLinked
                ? $"请抬 Z 回标定高度 {(Profile.CalibZ.HasValue ? Profile.CalibZ.Value.ToString("F1") : "--")}mm(±5mm 内;{uHint})→抓拍定格→点选该特征→点【📐 记为对准像素】"
                : $"请抬 Z 让尖离开特征(或直接抬到 0;{uHint})→抓拍定格→点选该特征→点【📐 记为对准像素】";
            NozzleAlignInfoText = $"已记录 R_n=({NozzleAlignX:F3},{NozzleAlignY:F3})mm · 压住高度 Z={NozzleAlignZ.Value:F1}mm —— {zHint}";
            AppendLog($"[吸嘴锚点已记] R_n=({NozzleAlignX:F3},{NozzleAlignY:F3})mm Z={NozzleAlignZ.Value:F1}mm —— 差分退路将用它 + 同刻像素生成对准锚;关窗后写回档案。{zHint}");
            OnPropertyChanged(nameof(NozzleAlignX));
            OnPropertyChanged(nameof(NozzleAlignY));
            OnPropertyChanged(nameof(NozzleAlignReady));
            RaiseCanExecutes();
        }

        /// <summary>
        /// 记录吸嘴尖对准像素 p_tip（v3 差分式消费锚，设备常量）。
        /// 前置操作：①JOG 吸嘴1 尖压住工件特征并点【📍 记吸嘴对准锚点】(XY=压住位 R_n)
        ///          ②抬 Z 回标定高度(XY 不动)抓拍定格 ③点选该特征(即本方法的入参来源)。
        /// 校验：当前 XY 须≈压住位(只动过 Z)；p_tip 语义="机位=R_n 时该特征在相机里的像"。
        /// </summary>
        private void SaveToolAlignFromPick()
        {
            if (!HasPick)
            {
                AppendLog("记为对准像素失败: 请先在画面上点选该特征(需先抓拍定格)。");
                return;
            }
            var pose = TryReadCurrentPose();
            if (pose.HasValue && _anchorPose.HasValue
                && (Math.Abs(pose.Value.X - _anchorPose.Value.X) > 2.0
                    || Math.Abs(pose.Value.Y - _anchorPose.Value.Y) > 2.0))
            {
                AppendLog($"⚠ 当前 XY=({pose.Value.X:F1},{pose.Value.Y:F1}) 偏离压住位 R_n=({_anchorPose.Value.X:F1},{_anchorPose.Value.Y:F1}) 超 2mm —— p_tip 必须是\"机位=压住位时特征成像\"，请回锚点 XY(只动 Z)重新定格点选后再记。");
                return;
            }

            Profile.ToolAlignPixelX = _pickCol;
            Profile.ToolAlignPixelY = _pickRow;
            ToolAlignDirty = true;

            string tipInfo = "";
            var wt = _calibService.MapPixelToWorld(MatrixPath, _pickCol, _pickRow);
            if (wt.Success) tipInfo = $" w_tip=H(p_tip)=({wt.Data.WorldX:F3},{wt.Data.WorldY:F3})";
            string uNote = pose.HasValue ? $" U={pose.Value.U:F1}°" : "";
            string uTarget = (Profile.CalibU0 ?? 0.0).ToString("F1");

            // ★ 2026-09-08 v5：EyeInHand 布局下 p_tip 与压住位 R_n 同刻成立，
            //   TCO = H(p_tip) − R_n 可直接结算并随档案落库（与向导 EIH 间接对针同源语义）。
            //   ⚠ 必须用「本次会话刚记的本地 R_n」而非 Profile.NozzleAlignX（档案旧值）——
            //   SaveNozzleAlign 只更新本地 VM 字段、关窗才写回 Profile，会话内直接用 Profile 会错用旧锚。
            string tcoNote = "";
            if (Profile.EyeMode == EyeMode.EyeInHand && NozzleAlignReady)
            {
                if (wt.Success)
                {
                    double tcoX = wt.Data.WorldX - NozzleAlignX;
                    double tcoY = wt.Data.WorldY - NozzleAlignY;
                    Profile.ApplyToolOffset(tcoX, tcoY, ToolOffsetMethod.EyeInHandIndirect);
                    tcoNote = $" TCO=H(p_tip)−R_n=({tcoX:F3},{tcoY:F3})mm 已结算落库(EyeInHandIndirect)";
                }
                else
                {
                    AppendLog("⚠ 记为对准像素时 H(p_tip) 映射失败（矩阵不可用）：仅记 p_tip，TCO/工位 Ecc 发布需先修复 H 矩阵并重记。");
                }
            }

            ToolAlignInfoText = $"已记对准像素 p_tip=({_pickCol:F1},{_pickRow:F1}){tipInfo} —— 定案式消费 P_go=X_obj−R(U_go−U0)·e 生效;关窗写回档案。前提:到位时 U 保持={uTarget}°。{tcoNote}";
            AppendLog($"[对准像素已记] p_tip=({_pickCol:F1},{_pickRow:F1}){tipInfo}{uNote} —— TCO 消费锚就绪(与工件摆放位置无关);关窗后写回档案。前提:到位时 U≈{uTarget}°。{tcoNote}");
            OnPropertyChanged(nameof(ToolAlignReady));
            RaiseCanExecutes();
        }

        /// <summary>
        /// 平移差分自检（2026-09-06，矩阵与机械自洽的工件无关判据）：
        /// 麻将不动 → 机位①定格点选(p1) → JOG 平移已知位移 Δ → 机位②定格点选(p2)；
        /// 断言 Δw=H(p2)−H(p1) ≈ Δ机位（由 w=X_cam+M−X_Q 差分消去 M，理论严格相等）。
        /// 通过 = 矩阵线性/当量/镜像与机械一致(思路A 放行)；不符 = 矩阵或采样有系统错。
        /// 两次按钮：第 1 击记第一点，第 2 击结算。
        /// </summary>
        private void DiffCheckStep()
        {
            if (!HasPick)
            {
                AppendLog("平移差分自检: 请先【抓拍定格】并点选实物特征。");
                return;
            }
            var pose = _photoPose ?? TryReadCurrentPose();
            if (!pose.HasValue)
            {
                AppendLog("平移差分自检: 读不到拍照机位(需抓拍定格实读或运动卡在线)。");
                return;
            }

            if (_diffCheckStage == 0)
            {
                _diffCheckPose1 = (pose.Value.X, pose.Value.Y);
                _diffCheckW1 = (_pickWorldX, _pickWorldY);
                _diffCheckStage = 1;
                DiffCheckInfoText = $"① 已记: 机位=({pose.Value.X:F3},{pose.Value.Y:F3})  w=({_pickWorldX:F3},{_pickWorldY:F3})\n麻将保持不动 → JOG 平移一个已知位移(建议 ≥15mm、整数更佳、只动 XY)→ 回同一拍照高度(相机随 Z 的机型须回 CalibZ)→ 抓拍定格 → 点选同一特征 → 再点此按钮结算。";
                AppendLog($"[平移差分自检 ①] 机位=({pose.Value.X:F3},{pose.Value.Y:F3}) w=({_pickWorldX:F3},{_pickWorldY:F3}) —— 请平移后同特征重拍点选,再点结算。");
                return;
            }

            var p1 = _diffCheckPose1.Value;
            var w1 = _diffCheckW1.Value;
            double dmX = pose.Value.X - p1.X, dmY = pose.Value.Y - p1.Y;
            double dwX = _pickWorldX - w1.X, dwY = _pickWorldY - w1.Y;
            double ex = dwX - dmX, ey = dwY - dmY;
            double mag = Math.Sqrt(ex * ex + ey * ey);
            string verdict = mag <= 0.5
                ? "✓ 自洽: Δw≈Δ机位 —— 矩阵线性/当量/镜像 与机械一致,可作像素差分消费(与工件位置无关)"
                : (mag <= 2.0
                    ? "△ 偏差偏大: Δw 与 Δ机位 差在亚毫米~毫米级 —— 优先怀疑两次点选特征/机位不同或麻将微动;若重复仍差则回标定核对"
                    : "✗ 不符: Δw 与 Δ机位 显著偏离 —— 矩阵当量/镜像/轴向或采样有系统错(思路A),需核对九点标定/特征提取");
            AppendLog($"[平移差分自检 结算] 机位Δ=({dmX:F3},{dmY:F3}) wΔ=({dwX:F3},{dwY:F3}) 差=({ex:F3},{ey:F3}) |Δ|={mag:F3}mm —— {verdict}");
            DiffCheckInfoText = $"机位Δ=({dmX:F3},{dmY:F3})   wΔ=({dwX:F3},{dwY:F3})  →  差=({ex:F3},{ey:F3})mm  [{verdict}]";
            _diffCheckStage = 0;
            _diffCheckPose1 = null;
            _diffCheckW1 = null;
        }

        /// <summary>定格完成时记录机械位（诊断定案用；失败静默置空，点选诊断退化为点选瞬间机位）</summary>
        private void TrySnapshotPhotoPose()
        {
            try
            {
                _photoPose = TryReadCurrentPose();
            }
            catch
            {
                _photoPose = null;
            }
        }

        /// <summary>读当前机械轴位姿(诊断用；失败返回 null)</summary>
        private (double X, double Y, double Z, double U)? TryReadCurrentPose()
        {
            var m = SelectedMotion;
            if (m == null) return null;
            var x = m.GetFeedbackPosition(Profile.BindXAxisIndex);            var y = m.GetFeedbackPosition(Profile.BindYAxisIndex);
            if (!x.Success || !y.Success) return null;
            var z = m.GetFeedbackPosition(Profile.BindZAxisIndex);
            var u = m.GetFeedbackPosition(Profile.BindRotationAxisIndex);
            return (x.Data, y.Data,
                    z.Success ? z.Data : double.NaN,
                    u.Success ? u.Data : double.NaN);
        }

        /// <summary>低速到位(仅建议坐标允许时；Z 保持当前高度)</summary>
        private void MoveToTarget()
        {
            if (!HasPick)
            {
                AppendLog("先点选一个实物特征点。");
                return;
            }
            if (!ToolAlignReady)
            {
                AppendLog("未标对准像素 p_tip——差分式走位缺锚。请按三步标定: ①JOG 吸嘴1 尖压住工件特征点【📍 记吸嘴对准锚点】 ②抬 Z 让尖离开特征(XY 不动)抓拍定格 ③点选该特征后点【📐 记为对准像素】,再重新抓拍点选走位。");
                return;
            }
            if (_facade == null)
            {
                AppendLog("未绑定运动卡, 无法到位(仅建议坐标模式)。");
                return;
            }
            IsBusy = true;
            try
            {
                // ★ 执行时按实时回转角求命令位（2026-09-08 定案式）：
                //   _moveBase = 特征 Base 真位 X_obj（与 U 无关）；P_go = X_obj − R(U_go − U0)·e。
                //   O/e 齐备 → 任意 U 角精确；否则保持点选时结算的退路结果并提示。
                double fx = _moveTargetX, fy = _moveTargetY;
                if (_poseCorrectionApplied && _moveRotReady)
                {
                    var cur = TryReadCurrentPose();
                    if (cur.HasValue && !double.IsNaN(cur.Value.U))
                    {
                        double u0 = Profile.CalibU0 ?? 0.0;
                        double uGo = cur.Value.U;
                        var cmd = CalibrationGeometry.CommandFor(_moveBaseX, _moveBaseY,
                            Profile.ToolOffsetPureWx, Profile.ToolOffsetPureWy, uGo, u0);
                        fx = cmd.X;
                        fy = cmd.Y;
                        _moveTargetX = fx;
                        _moveTargetY = fy;
                        AppendLog($"  执行时按实时角: U_go={uGo:F1}° (U0={u0:F1}°) → P_go=X_obj({_moveBaseX:F3},{_moveBaseY:F3})−R(U_go−U0)·e=({fx:F3},{fy:F3})mm");
                    }
                }
                AppendLog($"低速到位 → X={fx:F3}, Y={fy:F3} mm ..."
                          + (_poseCorrectionApplied
                              ? (_moveRotReady ? "（定案式：P_go=X_obj−R(U−U0)·e）" : "（退路式：差分/直吸，见点选日志）")
                              : "（矩阵裸输出,未含修正）"));
                bool moved = _facade.MoveToXY(fx, fy);
                AppendLog(moved
                    ? "到位完成 —— 请目视确认吸嘴/工具头尖是否对准目标特征(对准=✅通过；偏移=❌偏差)。"
                    : "到位被拒: " + (_facade.LastError ?? "未知原因") + "（以控制器反馈为准：4007 超动作区域=外圈够不着 / 4001 超脉冲=内圈收不拢或 U 姿态 / 2997=Z 超软限）。提示：走位被拒≠换算错误——是目标点超出机械可达域，把工件或拍照位向可达环带中腰收拢(麻将摆太靠外/靠内)后重试即可。");
                if (moved)
                {
                    // 2026-09-08 定案式：P_go=X_obj−R(U_go−U0)·e 已把旋转中心 O 与真吸嘴偏心 e 折进目标
                    // ——到位后【吸嘴尖应正对特征】，目视直接看吸嘴尖与特征是否重合即可。
                    // 目视贴面判定需把 Z 下压至 R_nZ（压住高度）才无投影视差。
                    AppendLog($"  ★判读提示: 到位目标=({fx:F3},{fy:F3})已按定案式折进 O(旋转中心)与 e(真吸嘴偏心),吸嘴尖应正对点选特征。"
                              + (Profile.NozzleAlignZ.HasValue
                                  ? $" 下压至 R_nZ={Profile.NozzleAlignZ.Value:F1}mm(压住高度)再目视,投影无视差。"
                                  : " 未记录 R_nZ(压住高度)——可 JOG 下压至工件面高度目视。"));
                }
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

        // ==================== 判定与记录 ====================

        private void Verdict(bool passed)
        {
            if (!HasPick) return;
            var point = new CalibrationVerificationPoint
            {
                Order = VerificationPoints.Count + 1,
                PixelX = _pickCol,
                PixelY = _pickRow,
                WorldX = _moveTargetX, // 记录走位目标(锚点差式 R_go;锚点缺失时不可能走到这里=命令已禁用)
                WorldY = _moveTargetY,
                Passed = passed,
                Note = VerdictNote
            };
            if (!passed)
            {
                double off;
                if (double.TryParse(OffsetText, out off))
                {
                    point.OffsetMm = off;
                }
                else
                {
                    point.OffsetMm = null; // 无偏移量也可记"偏差"
                }
            }
            VerificationPoints.Add(point);
            AppendLog($"已记录 第{point.Order}点: {(passed ? "✅ 通过" : "❌ 偏差")}"
                      + (point.OffsetMm.HasValue ? $" 偏移≈{point.OffsetMm.Value:F2}mm" : "")
                      + $" @({_moveTargetX:F1},{_moveTargetY:F1})mm");
            ResetPick();
            VerdictNote = null;
            OffsetText = null;
            RefreshSummary();
            (SaveRecordCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>生成校验记录(中心在窗口关闭后取走落库)</summary>
        private void SaveRecord()
        {
            var judged = VerificationPoints.Where(p => p.IsVerdicted).ToList();
            if (judged.Count == 0)
            {
                AppendLog("尚无已判定验证点, 无需保存。");
                return;
            }
            PendingRecord = new CalibrationVerificationRecord
            {
                VerifiedTime = DateTime.Now,
                TotalPoints = VerificationPoints.Count,
                PassedCount = judged.Count(p => p.Passed),
                Note = $"校验台打点验收 {judged.Count} 点"
            };
            PendingRecord.Points.AddRange(VerificationPoints);
            AppendLog($"校验记录已生成: 判定 {judged.Count} 点, 通过 {PendingRecord.PassedCount} 点"
                      + $"(通过率 {PendingRecord.PassRate:F0}%) —— 关闭窗口后写回方案档案。");
            RefreshSummary();
        }

        private void RefreshSummary()
        {
            var judged = VerificationPoints.Where(p => p.IsVerdicted).ToList();
            if (judged.Count == 0)
            {
                SummaryText = VerificationPoints.Count > 0
                    ? $"已记录 {VerificationPoints.Count} 点(均未判定)" : "尚未记录验证点";
                return;
            }
            int pass = judged.Count(p => p.Passed);
            SummaryText = $"已判定 {judged.Count} 点 · 通过 {pass} 点 · 通过率 {pass * 100.0 / judged.Count:F0}%";
        }

        private void ResetPick()
        {
            HasPick = false;
            PickInfoText = "尚未点选特征点";
            VerdictNote = null;
            // 平移差分自检中断清理：半途状态作废，防假结算
            if (_diffCheckStage == 1)
            {
                AppendLog("[平移差分自检] 已中断(点选被重置/判定),本次未结算,请重新开始。");
            }
            _diffCheckStage = 0;
            _diffCheckPose1 = null;
            _diffCheckW1 = null;
        }

        // ==================== 工具 ====================

        private string ResolveMatrixPath()
        {
            var candidates = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(Profile.HomMatFilePath))
            {
                candidates.Add(Profile.HomMatFilePath);
            }
            // 兜底: 向导 EnsureMatrixPersisted 的设备级落盘路径
            string device = string.IsNullOrWhiteSpace(Profile.BoundDeviceId) ? "Default" : Profile.BoundDeviceId;
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes", "Devices", device, "Calib");
            string fileName = SanitizeFileName(Profile.Name) + "_HandEye.tup";
            candidates.Add(Path.Combine(dir, fileName));
            foreach (var p in candidates)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return Path.GetFullPath(p);
                }
                catch { }
            }
            return null;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Calibration";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
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

        /// <summary>追加会话日志行（视图层可调用，如图上标记叠加反馈）；并镜像到 LogBus（VS 输出窗口可见，调试取证用）</summary>
        public void AppendLog(string msg)
        {
            try
            {
                Grayson.Vision.Contracts.Infrastructure.Logging.LogBus.Info("CalibrationVerifier", msg);
            }
            catch { /* 日志镜像失败不影响主流程 */ }

            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (LogLines.Count > 200) LogLines.RemoveAt(0);
        }

        private void RaiseCanExecutes()
        {
            (StartLiveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CaptureCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MoveToTargetCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveNozzleAlignCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveToolAlignCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DiffCheckCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (VerdictPassCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (VerdictFailCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ResetPickCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseGeometryCanExecutes(); // 分步几何校验区命令（partial 实现）
        }

        /// <summary>走位可用：有点选 + 锚点已记(R_n) + 运动卡在位——锚点缺失时禁止走位(旧 C2 公式已弃用)</summary>
        private bool CanMove() => HasPick && !IsBusy && _facade != null && ToolAlignReady;

        /// <summary>
        /// 旋转偏心矢量（世界，U=0 参考）：R(angDeg)·(ex,ey)=(ex·cos−ey·sin, ex·sin+ey·cos)。
        /// 与生产引擎 VisionPickPlaceProcess/MahjongDualNozzleProcess 的 RotEcc 同款，符号保持一致。
        /// </summary>
        private static (double X, double Y) RotEcc(double angDeg, double ex, double ey)
        {
            double r = angDeg * Math.PI / 180.0;
            double c = Math.Cos(r), s = Math.Sin(r);
            return (ex * c - ey * s, ex * s + ey * c);
        }
    }

    /// <summary>残差体检行（P3-A 离线重投影逐点结果；DataGrid 只读展示，无需通知）</summary>
    public sealed class ResidualRowModel
    {
        public int Order { get; set; }

        /// <summary>记录像素 col（标定采集时识别位）</summary>
        public double PixelX { get; set; }

        /// <summary>记录像素 row</summary>
        public double PixelY { get; set; }

        /// <summary>机械真值 X（标定命令位/示教位）</summary>
        public double TrueX { get; set; }
        public double TrueY { get; set; }

        /// <summary>H(像素) 预测 X（世界）</summary>
        public double PredX { get; set; }
        public double PredY { get; set; }

        /// <summary>H⁻¹(真值) 反投影像素 col（图上青十字；NaN=反投影失败）</summary>
        public double BackX { get; set; }
        public double BackY { get; set; }

        /// <summary>残差分量(mm) e = 真值 − 预测</summary>
        public double DxMm { get; set; }
        public double DyMm { get; set; }

        /// <summary>合残差(mm)</summary>
        public double AbsErrMm { get; set; }

        /// <summary>该点采集可靠度（向导识别偏差过大标记）</summary>
        public bool Reliable { get; set; }

        /// <summary>✅ / ⚠ 超差</summary>
        public string StatusText { get; set; }
    }
}
