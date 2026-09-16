//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationVerifierViewModel.cs
// 说 明: P3 标定校验台会话 ViewModel（2026-09-12 精简）。
//        唯一功能：抓拍定格 → 图上点选实物特征(覆盖层收点击+宿主 TryGetImagePointAt 换图像坐标)
//        → MapPixelToWorld 换算 → 低速到位(CalibrationMotionFacade) 让吸嘴贴合到物理工件
//        → 目视判定 通过/偏差 → 生成 CalibrationVerificationRecord 交回标定中心持久化。
//        ★ 主要测试的是标定结果的消费与应用（不是标定过程本身）：
//          上相机（ETH/EIH）走绝对定位；下相机（仰视）走相对纠偏（见 IsDownCamera）。
//        ★ 2026-09-12 移除「离线残差体检」「分步几何校验」诊断模块（原 Geometry.cs partial 已删），
//          消费公式统一收敛到唯一真源 CalibrationGeometry。
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
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Interfaces;
using Grayson.Vision.Contracts.Devices.Models;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vision.Repository;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Model;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 校验台会话 VM：绑定一个 CalibrationProfile，驱动相机/运动卡做"点哪去哪"验收。
    ///
    /// ★ 2026-09-10 重构：校验方式 = 工位依赖的标定产物组合（不是相机/吸嘴类型）。
    ///   工位依赖哪几种标定结果（H / H+t / H+e / H+e+t），就有哪几种校验方式。
    ///   本 VM 按 Profile 上已标定的产物自动判定组合，只启用对应的换算与走位链路，
    ///   无关的（旋转偏心 e / 对针 t / 旋转中心 O）不参与，界面也按组合动态分型。
    ///
    /// ★ 对针职责彻底剥离：本校验台不再管"记 R_n / 记 p_tip / TCO 结算"，
    ///   只做一件事——用户在图上点像素 → 吸嘴去吸那个点（含已标定的 t/e 补偿）。
    ///   对针三步预置回到对针专窗（CalibrationToolOffsetWindow）或标定向导。
    ///
    /// ★ ETH/EIH 按 Profile.EyeMode 自动判定（唯一真源 CalibrationGeometry）：
    ///   ETH（固定相机）    ：X_obj = H(u)，吸点 = H(u)（工具偏距已吸收进 H，不加 TCO）
    ///   EIH（相机随手走）  ：X_obj = P_photo + O − H(u)，吸点 = X_obj − R(U−U0)·e（有 e 时）
    ///   下相机（仰视二次对位）：相对纠偏 δ = H_down(R_img) − H_down(R_cdown)，吸嘴反向移动 δ 让工件居中
    ///   （见 CalibrationGeometry.DownCameraOffset；与上相机绝对定位正交）。</summary>
    public partial class CalibrationVerifierViewModel : ViewModelBase
    {
        private readonly ICalibrationService _calibService;
        private readonly IDevicePool _devicePool;
        private readonly HalconImageRenderService _renderService = new HalconImageRenderService();
        private CalibrationMotionFacade _facade;

        /// <summary>★相机级消费门面（2026-09-12）：聚合本工位本槽的 H/e/t/s，换算走唯一真源，消除手抄分型公式。</summary>
        private readonly CameraCalibrationBundle _bundle;

        /// <summary>
        /// ★2026-09-15：产物聚合源的自述（档案数 / 源目录 / 失败原因）。
        /// 存在意义：聚合源一旦是空的，e/O 的"未标"就是**假阴性**，而旧代码是静默退化为单档案——
        /// 现场看到"未标"会去补标定，方向全错。所以这条必须显式打进日志。
        /// </summary>
        private string _aggregationSourceNote;

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

        // ==================== 标定产物组合判定（2026-09-10 重构核心） ====================

        /// <summary>
        /// 工位依赖的标定产物组合（校验方式分型依据）。
        /// H=九点矩阵；t=对针偏距；e=旋转偏心。组合决定启用哪条换算/走位链路。
        /// </summary>
        public enum VerifierShape
        {
            /// <summary>仅九点矩阵 H（无对针、无旋转偏心）——点哪吸哪：吸点 = X_obj</summary>
            HOnly = 0,
            /// <summary>九点 + 对针 t（固定偏距）——吸点 = X_obj + TCO</summary>
            HPlusT = 1,
            /// <summary>九点 + 旋转偏心 e——吸点 = X_obj − R(U−U0)·e（任意 U 角精确）</summary>
            HPlusE = 2,
            /// <summary>九点 + 旋转 e + 对针 t——全套补偿</summary>
            HPlusEPlusT = 3
        }

        /// <summary>当前工位依赖的标定产物组合（根据 Profile 已标定产物自动判定）</summary>
        public VerifierShape Shape
        {
            get
            {
                bool hasT = HasT;
                bool hasE = HasE;
                if (hasE && hasT) return VerifierShape.HPlusEPlusT;
                if (hasE) return VerifierShape.HPlusE;
                if (hasT) return VerifierShape.HPlusT;
                return VerifierShape.HOnly;
            }
        }

        /// <summary>是否有九点矩阵 H</summary>
        public bool HasH => IsMatrixReady;

        /// <summary>
        /// 对针偏距 t 是否齐备。
        /// ★2026-09-15：t 是【吸嘴级】产物（独立 t 档案），不一定长在本窗口绑定的 H 档案上，
        ///   故优先读门面聚合结果，再回退本档案——与 e/O 读法同源（否则又会"e 在档案里但界面说未标"）。
        /// </summary>
        public bool HasT => (_bundle?.HasToolOffset ?? false)
                            || (Profile.IsToolOffsetCalibrated
                                && (Math.Abs(Profile.ToolOffsetWx) > 1e-9
                                    || Math.Abs(Profile.ToolOffsetWy) > 1e-9));

        /// <summary>对针偏距 t 的取值与来源（日志用；优先门面聚合到的 t 档案）</summary>
        public string ToolOffsetText
        {
            get
            {
                var t = _bundle?.T;
                if (t != null && (Math.Abs(t.ToolOffsetWx) > 1e-9 || Math.Abs(t.ToolOffsetWy) > 1e-9))
                    return $"({t.ToolOffsetWx:F3},{t.ToolOffsetWy:F3})mm";
                if (Profile.IsToolOffsetCalibrated
                    && (Math.Abs(Profile.ToolOffsetWx) > 1e-9 || Math.Abs(Profile.ToolOffsetWy) > 1e-9))
                    return $"({Profile.ToolOffsetWx:F3},{Profile.ToolOffsetWy:F3})mm";
                return null;
            }
        }

        /// <summary>
        /// 档案名截断（日志用）。★2026-09-15：口径分型取自【门面聚合到的 H 档案】，
        /// 而它可能不是本窗口绑定的那份（聚合选错档案的坑见 CameraCalibrationBundle.Build），
        /// 所以日志要把"到底用了谁的矩阵"写出来——只写"H=已标"是不够的。
        /// </summary>
        private static string ShortName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "无";
            name = name.Trim();
            return name.Length <= 26 ? name : "…" + name.Substring(name.Length - 25);
        }

        /// <summary>
        /// 是否已标定旋转偏心 e（需旋转中心 O + 真吸嘴偏心齐备）。
        /// ★2026-09-12 修复：e/O 是吸嘴级（ToolRotation）产物，存在 e 档案而非本窗口绑定的 H 档案，
        ///   故改读门面聚合结果（HasBundleEcc 已含 O 齐备 + 偏心非零双判据）。
        /// </summary>
        public bool HasE => HasBundleEcc;

        /// <summary>是否眼在手（EIH，相机随机械手走）——否则眼在手外（ETH，固定相机）</summary>
        public bool IsEyeInHand => Profile.EyeMode == EyeMode.EyeInHand;

        /// <summary>
        /// ★2026-09-15：是否需旋转中心 O + 偏心 e 补偿（读门面聚合结果，与 CameraCalibrationBundle 同源）。
        /// 现仅 EIH（相机随机械手走）需 O/e——因为该式含拍照机位 P_photo。
        /// 固定相机+延伸杆（CameraTruthWalk）改按平台约定用对针偏距 t 补：吸点 = H(u) + t。
        /// </summary>
        public bool NeedsOCompensation => _bundle?.NeedsOCompensation ?? IsEyeInHand;

        /// <summary>固定相机 + 杆端域 H（延伸杆辅助标定）：吸点 = H(u) + b（b=杆端 mark→吸嘴尖），不用 P_photo / O</summary>
        public bool IsFixedCameraRodDomain => _bundle?.IsFixedCameraRodDomain ?? false;

        /// <summary>门面聚合后是否已标定真吸嘴偏心 e（读 e 产物，而非仅本窗口绑定的 H 档案）</summary>
        public bool HasBundleEcc => _bundle?.HasEcc ?? false;

        /// <summary>门面聚合后旋转中心 O 是否齐备</summary>
        public bool HasBundleRotationCenter => _bundle?.HasRotationCenter ?? false;

        /// <summary>
        /// ★是否下相机（仰视二次对位，2026-09-12）。
        /// 判据：PrimaryPath 为下相机专属路径（DownCameraWalk 吸件走九点 / DownCameraPixelRotCenter 像素旋转中心）。
        /// 下相机消费语义与上相机正交——不做绝对定位，只做相对纠偏
        /// δ = H_down(R_img) − H_down(R_cdown)，吸嘴反向移动 δ 让工件回到旋转轴正下方。
        /// </summary>
        public bool IsDownCamera =>
            Profile.PrimaryPath == CalibrationAcquirePath.DownCameraWalk
            || Profile.PrimaryPath == CalibrationAcquirePath.DownCameraPixelRotCenter;

        /// <summary>
        /// 下相机像素旋转中心是否已标定（R_cdown 齐备，相对纠偏可用）。
        /// ★★2026-09-15 修正（判据写两遍就会分叉）：此前读【本窗口绑定的 Profile】，
        ///   而换算侧（CameraCalibrationBundle.Solve）读的是归档产物 ⇒ 两个判据在
        ///   "R_cdown 写在 e/Path=9 档案、而窗口绑的是 H 档案"时结论相反：
        ///   这里说"未标"而那边算得出（或反过来），正是"靠巧合正确"的典型。
        ///   现统一走门面聚合结果（唯一入口），与 Solve 同源。
        /// </summary>
        public bool HasDownRotCenter => _bundle != null
            ? _bundle.HasDownRotCenter
            : (Profile.DownRotCenterRow.HasValue && Profile.DownRotCenterCol.HasValue);

        /// <summary>
        /// ★2026-09-15：R_cdown 的【归属档案】——日志要写清"这个数是从谁那里读到的"。
        /// 门面给不出时退回本窗口 Profile（够用于显示）。
        /// </summary>
        private CalibrationProfile DownRotCenterOwner => _bundle?.DownRotCenterProfile ?? Profile;

        /// <summary>组合名称（界面分型标题用）</summary>
        public string ShapeName
        {
            get
            {
                if (IsDownCamera) return "下相机 相对纠偏（H_down + R_cdown）";
                switch (Shape)
                {
                    case VerifierShape.HPlusT: return "九点 + 对针（H+t）";
                    case VerifierShape.HPlusE: return "九点 + 旋转（H+e）";
                    case VerifierShape.HPlusEPlusT: return "九点 + 旋转 + 对针（H+e+t）";
                    default: return "九点（H）";
                }
            }
        }

        /// <summary>布局名称（下相机仰视 / EIH / ETH 提示用）</summary>
        public string LayoutName => IsDownCamera
            ? "下相机 仰视（相对纠偏）"
            : IsEyeInHand ? "眼在手 EIH（相机随机械手走）" : "眼在手外 ETH（固定相机）";

        /// <summary>
        /// 相机槽摘要（2026-09-11）：复合工位上下相机各有一条方案，校验时必须能一眼确认
        /// 本窗口校验的是哪个槽（Cam_A / Cam_C），否则容易拿上相机方案去验收下相机。
        /// </summary>
        public string SlotChipText
        {
            get
            {
                try
                {
                    var slot = CalibrationProfileSessionPlanner.GuessSlotKey(Profile);
                    return string.IsNullOrWhiteSpace(slot) ? "相机槽 未指定" : "相机槽 " + slot;
                }
                catch
                {
                    return "相机槽 未指定";
                }
            }
        }

        // ==================== 消费口径（★2026-09-15 现场排查用） ====================

        /// <summary>口径下拉项（显示文案 + 枚举值）</summary>
        public sealed class SolveModeOption
        {
            public CalibrationSolveMode Mode { get; }
            public string Text { get; }
            public SolveModeOption(CalibrationSolveMode mode, string text) { Mode = mode; Text = text; }
        }

        /// <summary>
        /// ★消费口径可选项（默认"跟随档案声明"）。
        /// 存在意义：现场判定"档案声明对不对"——强制换个口径测同一个点，
        /// 两个吸点差多少一目了然，不必改档案、不必重启向导。
        /// </summary>
        public ObservableCollection<SolveModeOption> SolveModeOptions { get; } = new ObservableCollection<SolveModeOption>
        {
            new SolveModeOption(CalibrationSolveMode.FromProfile, "跟随档案声明（推荐）"),
            new SolveModeOption(CalibrationSolveMode.NozzleDomainDirect, "① 吸嘴域直吸：吸点=H(u)（不叠O、免U项）"),
            new SolveModeOption(CalibrationSolveMode.RodEndNoRotation, "② 杆端域+同轴：O补偿、免U项"),
            new SolveModeOption(CalibrationSolveMode.RodEndWithRotation, "③ 杆端域+偏心：O补偿+U旋转项"),
            // ★2026-09-15：固定相机+延伸杆的【符号 A/B】——b 该加还是该减，现场一次点选判死。
            //   判别法：两个口径各点同一个像素，只有一个是"吸嘴正好落在点上"；
            //   另一个会落在 2|b|（≈213mm）之外的反方向。选错不会安静通过，落差立现。
            new SolveModeOption(CalibrationSolveMode.FixedCameraPlusRodOffset, "④ 固定相机：吸点=H(u)+b（b=杆端→吸嘴）"),
            new SolveModeOption(CalibrationSolveMode.FixedCameraMinusRodOffset, "⑤ 固定相机：吸点=H(u)−b（外部方案口径/反号对照）"),
        };

        private SolveModeOption _selectedSolveMode;
        /// <summary>当前消费口径（决定 X_obj / 吸点算式分型；点选时生效并打对照日志）</summary>
        public SolveModeOption SelectedSolveMode
        {
            get => _selectedSolveMode;
            set
            {
                if (Set(ref _selectedSolveMode, value))
                    OnPropertyChanged(nameof(SolveModeHint));
            }
        }

        /// <summary>口径生效值（未选中时=跟随档案）</summary>
        private CalibrationSolveMode EffectiveSolveMode =>
            _selectedSolveMode?.Mode ?? CalibrationSolveMode.FromProfile;

        /// <summary>档案声明的摘要（界面提示"档案说它是什么域"，与所选口径并列显示）</summary>
        public string SolveModeHint
        {
            get
            {
                if (_bundle == null) return "未聚合到门面";
                // ★2026-09-15：固定相机杆端域的文案正名——不再是"叠 O 补偿"（那是 EIH 的式），
                //   而是"吸点 = H(u) + b（b=杆端 mark→吸嘴尖）"，b 可来自对针 t 或旋转标定 ToolEccW。
                string domain = _bundle.IsNozzleDomainH
                    ? "H=吸嘴域(直吸 H(u))"
                    : (_bundle.IsFixedCameraRodDomain
                        ? (_bundle.HasEthToolOffset
                            ? "H=杆端域(固定相机·b=对针t直量)"
                            : (_bundle.HasRodOffset
                                ? "H=杆端域(固定相机·b=杆端偏心推算)"
                                : "H=杆端域(固定相机·缺b)"))
                        : (_bundle.NeedsOCompensation ? "H=杆端域(需O补偿)" : "H=直接拍工件(直吸)"));
                string coax = !_bundle.NozzleAxisCoaxialDeclared.HasValue
                    ? "同轴=未声明"
                    : (_bundle.NozzleAxisCoaxialDeclared.Value ? "同轴=是" : "同轴=否");
                return $"{domain} · {coax}";
            }
        }

        /// <summary>图像显示（HalconImageDisplayHost 的 DataContext）</summary>
        public ImageDisplayVm ImageDisplay { get; }

        // ==================================================================================
        // ★★2026-09-15：口径的【系统判定】——把"让用户自己勾声明 / 自己选口径"改成"平台自动定"。
        //
        //   现场反馈（原话精神）：让用户在校验界面自己拼出正确的标定转换方式，心智负担太高；
        //   而且拼错了没有任何闸门能发现（分型错只是整体平移几毫米~一个杆长，RMS 抓不到）。
        //   正确形态：平台按【工位档案 + 标定条件】自动定好口径，用户只需【确认】或【一键采纳】；
        //   想反驳也有出口（下面的"高级·反面对照"，只影响本次显示、不影响生产）。
        //
        //   判定入口 = CalibrationConsumptionContract.Resolve —— 与发布链、生产端**同一个函数**。
        // ==================================================================================

        private ConsumptionDecision _systemDecision;
        private string _systemDecisionKey;

        /// <summary>系统自动判定的口径（等价于"跟随档案；档案没声明时按标定条件推断"）</summary>
        public ConsumptionDecision SystemDecision
        {
            get
            {
                if (_bundle == null) return null;
                // 门面重建（换槽/重标）后要重判——用"档案名+槽"当键，避免拿到过期结论
                string key = (_bundle.H?.Name ?? "<no-H>") + "|" + _bundle.SlotKey;
                if (_systemDecision == null || !string.Equals(_systemDecisionKey, key, StringComparison.Ordinal))
                {
                    _systemDecision = _bundle.ResolveDecision(CalibrationSolveMode.FromProfile);
                    _systemDecisionKey = key;
                }
                return _systemDecision;
            }
        }

        /// <summary>系统判定的口径名（界面主显；这就是"标定转换方式"）</summary>
        public string SystemDecisionText => SystemDecision?.KindText ?? "未聚合到门面";

        /// <summary>系统判定的算式（人话）</summary>
        public string SystemDecisionFormula => SystemDecision?.Formula ?? "";

        /// <summary>系统判定的依据链（为什么判成这一档，便于现场反驳而不是盲信）</summary>
        public string SystemDecisionBasis => SystemDecision?.Basis ?? "";

        /// <summary>系统判定的档案声明摘要</summary>
        public string SystemDecisionDecl => SystemDecision?.DeclSummary ?? "";

        /// <summary>系统判定的阻断/告警项（逐行；空串=无）</summary>
        public string SystemDecisionIssues
        {
            get
            {
                var d = SystemDecision;
                if (d == null) return "";
                var lines = new List<string>();
                foreach (var b in d.Blockers) lines.Add("⛔ " + b);
                foreach (var w in d.Warnings) lines.Add("⚠ " + w);
                return string.Join("\n", lines);
            }
        }

        /// <summary>是否有阻断/告警项（界面据此上色/展开）</summary>
        public bool HasSystemIssues => SystemDecision != null
            && (SystemDecision.Blockers.Count > 0 || SystemDecision.Warnings.Count > 0);

        /// <summary>档案里是否还缺口径声明（缺 ⇒ 可一键把"系统判定"写成确定值）</summary>
        public bool CanAdoptSystemDecision
        {
            get
            {
                var h = _bundle?.H;
                if (h == null) return false;
                return !h.HandEyeInNozzleDomain.HasValue || !h.NozzleAxisCoaxial.HasValue;
            }
        }

        /// <summary>一键采纳系统判定（写回档案声明；关窗时随 SolveDeclDirty 一并落库）</summary>
        public ICommand AdoptSystemDecisionCommand { get; }

        /// <summary>重新判定（门面/声明变化后刷新界面）</summary>
        public void RefreshSystemDecision()
        {
            _systemDecision = null;
            _systemDecisionKey = null;
            OnPropertyChanged(nameof(SystemDecision));
            OnPropertyChanged(nameof(SystemDecisionText));
            OnPropertyChanged(nameof(SystemDecisionFormula));
            OnPropertyChanged(nameof(SystemDecisionBasis));
            OnPropertyChanged(nameof(SystemDecisionDecl));
            OnPropertyChanged(nameof(SystemDecisionIssues));
            OnPropertyChanged(nameof(HasSystemIssues));
            OnPropertyChanged(nameof(CanAdoptSystemDecision));
            OnPropertyChanged(nameof(IsRodOffsetCase));
            OnPropertyChanged(nameof(RodOffsetInfoText));
        }

        /// <summary>
        /// 把系统判定出来的"H 落在哪个域 / 吸嘴是否与 U 轴同轴"写成**确定值**。
        /// 为什么需要：声明缺失时平台只能"推断"，推断只活在内存里——写回后发布链、生产端、
        /// 下次打开校验台看到的就是同一个确定值（消掉"每次重新推一遍"的漂移面）。
        /// ⚠ 只写"能由分型确定"的那一项：固定相机档推不出同轴性，就不动用户已有的人工声明。
        /// </summary>
        private void AdoptSystemDecision()
        {
            var d = SystemDecision;
            if (d == null)
            {
                AppendLog("⚠ 暂无系统判定（未聚合到门面），无法采纳。");
                return;
            }

            var notes = new List<string>();
            switch (d.Kind)
            {
                case ConsumptionKind.NozzleDomainDirect:
                    HandEyeInNozzleDomain = true;
                    notes.Add("H 域 → 吸嘴域（直吸 H(u)，不叠 O）");
                    break;
                case ConsumptionKind.EthDirect:
                case ConsumptionKind.FixedCameraRodOffset:
                    HandEyeInNozzleDomain = false;
                    notes.Add("H 域 → 杆端域（固定相机：吸点 = H(u) ± b）");
                    break;
                case ConsumptionKind.EihDirect:
                    HandEyeInNozzleDomain = false;
                    NozzleAxisCoaxial = true;
                    notes.Add("H 域 → 杆端域；吸嘴 → 与 U 轴同轴（免 R(U−U0)·e）");
                    break;
                case ConsumptionKind.EihWithRotation:
                    HandEyeInNozzleDomain = false;
                    NozzleAxisCoaxial = false;
                    notes.Add("H 域 → 杆端域；吸嘴 → 偏心（保留 R(U−U0)·e）");
                    break;
                case ConsumptionKind.DownCameraRelative:
                    AppendLog("ℹ 本槽是【下相机】：口径是相对纠偏（δ = H_down(R_img) − H_down(R_cdown)），"
                              + "没有「域 / 同轴」这两项声明可写。下相机要能用起来，需要的是"
                              + "『下相机像素旋转中心 R_cdown』标定 + 在标定中心对下相机 H 档案发布一次"
                              + "（发布链会把 H_down(R_cdown) 常量写进工位配置）。");
                    return;
            }

            foreach (var n in notes) AppendLog($"  · {n}");
            AppendLog($"💾 已采纳系统判定并写入档案声明（关窗即落库）：{string.Join("；", notes)}。"
                      + "发布到工位时会按这份声明写生产配置——从此校验台与生产端同一口径。");
            if (d.Kind == ConsumptionKind.FixedCameraRodOffset)
            {
                AppendLog("  → 下一步：① 在下方【高级·反面对照】用口径 ④ / ⑤ 各点同一个像素，"
                          + "只有一个能让【吸嘴尖】正好落在点上（另一个会落在 2|b| 之外）；"
                          + "② 记下正确那个的符号，③ 勾选【把 b 带进生产】后到标定中心发布 —— 生产端才会真的补 b。");
            }
            OnPropertyChanged(nameof(SolveModeHint));
            // ★采纳后必须重判：写入声明前判定源是"推断"，写入后判定源是"档案声明"，
            //   依据链(Basis)与可采纳性(CanAdoptSystemDecision)都变了——不重判界面会自相矛盾。
            RefreshSystemDecision();
        }

        // ==================== ★2026-09-15：『把 b 带进生产』的开关 ====================
        // 存在意义（现场反馈："下相机 / b 标定好了，但不知道怎么用"）：
        //   固定相机+杆端域的正确落点是 H(u)+b，但 b 的**符号**必须靠现场 A/B 判定
        //   （选错会偏 2|b|，比不补更糟），所以发布链刻意把"是否把 b 带进生产"做成了显式开关
        //   （档案字段 RodOffsetInProduction）。问题是：这个开关原先【没有任何界面】——
        //   用户在校验台验到压中了，却没有任何地方能把它打开 ⇒ 生产端永远走 H(u)，永远少一个 |b|。
        //   这里补上这个开关：验完就在这一屏勾选，关窗落库，再发布即同口径。
        private bool? _rodOffsetInProduction;
        /// <summary>是否把『杆端→吸嘴偏移 b』带进生产（固定相机+杆端域专用；勾=发，不勾=不发）</summary>
        public bool? RodOffsetInProduction
        {
            get => _rodOffsetInProduction;
            set
            {
                if (_rodOffsetInProduction != value)
                {
                    _rodOffsetInProduction = value;
                    SolveDeclDirty = true;
                    OnPropertyChanged();
                    AppendLog(value == true
                        ? "✔ 已标记【把 b 带进生产】：关闭本窗口后会落库；再到标定中心对该档案发布一次，生产端就会走 X_obj=H(u)+b。"
                        : "已标记【不把 b 带进生产】：生产端仍走 X_obj=H(u)（会比正确落点少一个 |b|）。");
                }
            }
        }

        /// <summary>本槽是否固定相机+杆端域（决定"把 b 带进生产"开关是否该显眼）</summary>
        public bool IsRodOffsetCase => SystemDecision?.Kind == ConsumptionKind.FixedCameraRodOffset;

        /// <summary>显示用的 b（量级与来源），供界面在开关旁标注</summary>
        public string RodOffsetInfoText
        {
            get
            {
                var d = SystemDecision;
                if (d == null || d.Kind != ConsumptionKind.FixedCameraRodOffset) return "";
                var inp = _bundle?.BuildInputs();
                if (inp == null || (Math.Abs(inp.RodOffsetWx) < 1e-9 && Math.Abs(inp.RodOffsetWy) < 1e-9))
                    return "b 未标定 ⇒ 即使打开也补不了（先去旋转标定或对针专窗测出杆端→吸嘴偏移）";
                double mag = Math.Sqrt(inp.RodOffsetWx * inp.RodOffsetWx + inp.RodOffsetWy * inp.RodOffsetWy);
                // ⚠ 这里【不能】读符号值：`RodOffsetSign` 是**工位过程配置**字段（VisionPickPlaceConfig /
                //   MahjongDualNozzleConfig），不是标定档案字段，校验台手里只有 CalibrationProfile。
                //   而且发布链刻意【不写】这个键（防现场改的 −1 被下次发布静默覆盖）⇒ 它的真值只在
                //   工位 ProcessConfigJson 里。所以这里只说清"符号不在这儿"，并把判定出口指给用户，
                //   不猜一个数字出来——猜错正是"差 2|b|"的来源。
                return $"b=({inp.RodOffsetWx:F3},{inp.RodOffsetWy:F3})mm |b|={mag:F3}mm  来源[{inp.RodOffsetSource}]  "
                     + "符号：由工位配置 RodOffsetSign 决定（发布链刻意不写它，防现场改值被覆盖）"
                     + "——用下方【高级·反面对照】的口径 ④ / ⑤ 各点同一个像素判死再勾";
            }
        }

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
        private bool _moveRodTermApplied;    // ★固定相机+杆端域：到位已补 b（吸点=H(u)+b）；U 项本场景不存在

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
            : "相机固定不随 Z：成像与 Z 无关；目视贴面判定须下压至工件面高度（无投影视差）";

        // ---- 消费口径声明（★2026-09-15：H 落在哪个域 / 吸嘴是否与 U 同轴）----
        // 背景：同一份 H，是靠"声明"决定要不要叠 O/e。分错直接差几毫米且 RMS 抓不到，
        // 所以声明必须能由现场勾选，而不是靠猜 PrimaryPath。取值镜像档案字段（bool? 三态），
        // 关窗时由标定中心随相机安装特性同一条链提交落库。

        private bool? _handEyeInNozzleDomain;
        /// <summary>H 是否已在吸嘴域（勾=已消杆→直吸 / 不勾=杆端域→需 O 补偿 / 中间=未声明）</summary>
        public bool? HandEyeInNozzleDomain
        {
            get => _handEyeInNozzleDomain;
            set
            {
                if (_handEyeInNozzleDomain != value)
                {
                    _handEyeInNozzleDomain = value;
                    SolveDeclDirty = true;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SolveModeHint));
                    // ★声明一变，系统判定必须重判：SystemDecision 按 "H.Name|SlotKey" 缓存，
                    //   不显式失效就会继续显示"推断档"，而档案里其实已经有确定声明了。
                    RefreshSystemDecision();
                }
            }
        }

        private bool? _nozzleAxisCoaxial;
        /// <summary>吸嘴是否与 U 回转轴同轴（勾=同轴→免 R(U−U0)·e / 不勾=偏心 / 中间=未声明）</summary>
        public bool? NozzleAxisCoaxial
        {
            get => _nozzleAxisCoaxial;
            set
            {
                if (_nozzleAxisCoaxial != value)
                {
                    _nozzleAxisCoaxial = value;
                    SolveDeclDirty = true;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SolveModeHint));
                    RefreshSystemDecision();   // ★同 HandEyeInNozzleDomain：声明变了就重判，不吃过期缓存
                }
            }
        }

        /// <summary>口径声明是否被本次会话改动（关窗提交链据此落库）</summary>
        public bool SolveDeclDirty { get; private set; }

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

        /// <summary>覆盖层是否收点选（★2026-09-12 精简：校验台只剩在线打点，恒收点选）</summary>
        public bool PickEnabled => true;

        // ==================== 命令 ====================

        public ICommand StartLiveCommand { get; }
        public ICommand CaptureCommand { get; }
        public ICommand MoveToTargetCommand { get; }
        public ICommand VerdictPassCommand { get; }
        public ICommand VerdictFailCommand { get; }
        public ICommand ResetPickCommand { get; }
        public ICommand SaveRecordCommand { get; }
        /// <summary>单轴步进（参数 "X+" / "X-" / "Y+" / "Z+" / "U+"…，与机械臂调试台同款遥控器键位）</summary>
        public ICommand StepMoveCommand { get; }
        public ICommand VacuumOnCommand { get; }
        public ICommand VacuumOffCommand { get; }

        // ==================== 轴操作 + 真空（2026-09-10 从机械臂调试台搬入） ====================

        /// <summary>单轴步进步长选项（mm；U 轴同值解释为角度°）</summary>
        public IReadOnlyList<double> StepSizeOptions { get; } = new double[] { 0.1, 0.5, 1, 2, 5, 10 };

        private double _selectedStepSize = 1;
        /// <summary>当前单轴步进步长</summary>
        public double SelectedStepSize
        {
            get => _selectedStepSize;
            set => Set(ref _selectedStepSize, value);
        }

        private string _lastStepText = "";
        /// <summary>最近一次单轴步进反馈（成功/被拒原因）</summary>
        public string LastStepText
        {
            get => _lastStepText;
            private set => Set(ref _lastStepText, value);
        }

        private bool _vacuumOn;
        public bool VacuumOn
        {
            get => _vacuumOn;
            set { if (Set(ref _vacuumOn, value)) OnPropertyChanged(nameof(VacuumStateText)); }
        }

        /// <summary>真空阀 IO 号（从标定档案读，默认 0）</summary>
        public int VacuumIoIndex => Profile.PickVacuumIoIndex;

        /// <summary>真空开关状态文案</summary>
        public string VacuumStateText => _vacuumOn ? $"真空：已吸住（IO{VacuumIoIndex} ON）" : $"真空：未开启（IO{VacuumIoIndex}）";

        public CalibrationVerifierViewModel(CalibrationProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _calibService = new CalibrationService();
            _devicePool = App.StationHostRuntime != null ? App.StationHostRuntime.DevicePool : null;
            ImageDisplay = new ImageDisplayVm(_renderService);

            // ★ 相机级消费门面：按 (工位, 槽) 从全库聚合 H/e/t/s，换算统一收敛到唯一真源。
            //   仍以本窗口绑定的 Profile 为口径（槽=GuessSlotKey(Profile)），全库只用于补齐同槽的 e/t/s。
            _bundle = BuildBundle();
            _selectedSolveMode = SolveModeOptions[0];   // 默认：跟随档案声明

            StartLiveCommand = new RelayCommand(StartLive, () => CanCamera());
            CaptureCommand = new RelayCommand(CaptureFrame, () => CanCamera() && !IsBusy);
            MoveToTargetCommand = new RelayCommand(_ => MoveToTarget(), _ => CanMove());
            VerdictPassCommand = new RelayCommand(_ => Verdict(true), _ => HasPick && !IsBusy);
            VerdictFailCommand = new RelayCommand(_ => Verdict(false), _ => HasPick && !IsBusy);
            ResetPickCommand = new RelayCommand(_ => ResetPick(), _ => HasPick);
            SaveRecordCommand = new RelayCommand(_ => SaveRecord(), _ => VerificationPoints.Any(p => p.IsVerdicted));
            StepMoveCommand = new RelayCommand<string>(StepMove, _ => CanStepMove());
            VacuumOnCommand = new RelayCommand(_ => SetVacuum(true), _ => CanToggleVacuum());
            VacuumOffCommand = new RelayCommand(_ => SetVacuum(false), _ => CanToggleVacuum());
            // ★2026-09-15：一键采纳系统判定（把"域/同轴"写成确定值，消掉"每次重新推断"的漂移面）
            AdoptSystemDecisionCommand = new RelayCommand(_ => AdoptSystemDecision(), _ => CanAdoptSystemDecision);

            _cameraMovesWithZ = Profile.CameraMovesWithZ;
            CameraMountDirty = false;
            _handEyeInNozzleDomain = Profile.HandEyeInNozzleDomain;   // 口径声明镜像档案
            _nozzleAxisCoaxial = Profile.NozzleAxisCoaxial;
            _rodOffsetInProduction = Profile.RodOffsetInProduction;    // ★"把 b 带进生产"的开关镜像档案
            SolveDeclDirty = false;

            MatrixPath = ResolveMatrixPath();
            LoadDevices();
            if (!IsMatrixReady)
            {
                SessionNote = "该方案尚无标定矩阵(未完成标定或矩阵文件缺失)——无法校验。请先进入标定向导完成标定并保存。";
                AppendLog("校验不可用: 未找到矩阵文件。");
            }
            else
            {
                SessionNote = IsDownCamera
                    ? $"校验就绪【{ShapeName} · {LayoutName}】: 抓拍定格 → 点选下相机工件特征 → 吸嘴反向移动 δ 让工件居中（相对纠偏）→ 目视判定。"
                    : $"校验就绪【{ShapeName} · {LayoutName}】: 抓拍定格 → 点选特征 → 吸嘴去吸那个点 → 目视判定。";
                AppendLog($"校验台就绪, 矩阵: {MatrixPath}");
                if (IsDownCamera)
                {
                    AppendLog($"[产物组合] {ShapeName} · {LayoutName}"
                              + $"  像素旋转中心R_cdown={(HasDownRotCenter ? $"({DownRotCenterOwner.DownRotCenterCol:F1},{DownRotCenterOwner.DownRotCenterRow:F1})px" : "未标")}"
                              + (HasDownRotCenter ? $" 源「{ShortName(DownRotCenterOwner?.Name)}」" : "")
                              + $"  标定Z={(Profile.CalibZ.HasValue ? Profile.CalibZ.Value.ToString("F1") : "未记录")}mm"
                              + $"  RMS={(Profile.RmsError > 1e-9 ? Profile.RmsError.ToString("F3") + "mm" : "未写入")}");
                    if (!HasDownRotCenter)
                    {
                        AppendLog("⚠ 本下相机方案未标定像素旋转中心 R_cdown——相对纠偏不可用，请先完成「下相机像素旋转中心」标定。");
                        AppendLog("   （已查 H 与 e/Path=9 两处归属档案；若刚标完仍是此行，说明 SAVE 没把圆心写回 DownRotCenterCol/Row。）");
                    }
                }
                else
                {
                    AppendLog($"[产物组合] {ShapeName} · {LayoutName}"
                              + $"  H={(HasH ? "已标" : "缺")}[Path={(_bundle.H?.PrimaryPath.HasValue == true ? _bundle.H.PrimaryPath.Value.ToString() : "未记录")}"
                              + $" 源「{ShortName(_bundle.H?.Name)}」]"
                              + $"  t对针={(HasT ? ToolOffsetText : "未标")}"
                              + $"  吸嘴偏心e={(HasBundleEcc ? $"({_bundle.E.ToolOffsetPureWx:F3},{_bundle.E.ToolOffsetPureWy:F3})mm" : "未标")}"
                              + $"  杆端偏移b={(_bundle.HasRodOffset ? $"{Math.Sqrt(_bundle.RodOffsetWx * _bundle.RodOffsetWx + _bundle.RodOffsetWy * _bundle.RodOffsetWy):F3}mm({_bundle.RodOffsetWx:F3},{_bundle.RodOffsetWy:F3})" : "未标")}"
                              + $"  旋转中心O={(HasBundleRotationCenter ? $"({_bundle.RotationCenterWx:F2},{_bundle.RotationCenterWy:F2})" : "未标")}"
                              + $"  U0={(Profile.CalibU0.HasValue ? Profile.CalibU0.Value.ToString("F2") : "null")}°"
                              + $"  标定Z={(Profile.CalibZ.HasValue ? Profile.CalibZ.Value.ToString("F1") : "未记录")}mm"
                              + $"  RMS={(Profile.RmsError > 1e-9 ? Profile.RmsError.ToString("F3") + "mm" : "未写入")}");
                    // ★2026-09-15：拆穿"e偏心=未标"这个措辞歧义（现场据此以为旋转标定白做了）。
                    //   平台的 e 是【吸嘴尖相对回转轴的偏心】(ToolOffsetPureW)，必须物理对针才有值；
                    //   旋转标定产出的是【杆端 mark 相对回转轴的偏心】(ToolEccW=b)，两个量不同名不同值。
                    if (!HasBundleEcc && _bundle.HasRodOffset)
                    {
                        AppendLog("ℹ 「吸嘴偏心e=未标」≠ 旋转标定没做：平台的 e 专指【吸嘴尖相对回转轴的偏心】"
                                  + "(ToolOffsetPureW，需物理对针才有值)；本次旋转标定产出的是【杆端 mark 相对回转轴的偏心】"
                                  + $"ToolEccW=b={Math.Sqrt(_bundle.RodOffsetWx * _bundle.RodOffsetWx + _bundle.RodOffsetWy * _bundle.RodOffsetWy):F3}mm"
                                  + "——固定相机+延伸杆场景要用的正是后者。同心吸嘴的 e 本就应为 0，不需要额外补。");
                    }
                    // 聚合源自述：档案数/空源告警。空源时上面的"未标"是假阴性，这一行负责拆穿它。
                    if (!string.IsNullOrWhiteSpace(_aggregationSourceNote))
                    {
                        AppendLog($"[产物组合·源] {_aggregationSourceNote}");
                    }
                    if (!string.IsNullOrWhiteSpace(_bundle.AggregationNotes))
                    {
                        AppendLog($"[产物组合·借用] {_bundle.AggregationNotes}");
                    }
                    // ★2026-09-15：把"重标时记了什么证据"也摊开——现场不必去翻文件就能看到
                    //   旋转拟合摘要 / 机位是否固定 / 弧覆盖 / 证据 JSON 路径（重标后必然有）。
                    var ev = _bundle.E;
                    if (ev != null && (!string.IsNullOrWhiteSpace(ev.RotationFitSummary) || !string.IsNullOrWhiteSpace(ev.CalibrationEvidence)))
                    {
                        if (!string.IsNullOrWhiteSpace(ev.RotationFitSummary))
                            AppendLog($"[产物组合·证据] 旋转拟合: {ev.RotationFitSummary}");

                        // ★2026-09-15：把五条判据**逐条**摊开（每条归因不同），并给现场"看到什么意味着什么"。
                        //   为什么要在校验台再打一遍：现场排查时人在这里，不该被赶去翻 JSON。
                        double bMagEv = Math.Sqrt(ev.ToolEccWx * ev.ToolEccWx + ev.ToolEccWy * ev.ToolEccWy);
                        double uDevMax = -1.0;
                        if (ev.RotationSamples != null && ev.RotationSamples.Count > 0)
                        {
                            var uS = ev.RotationSamples.Where(s => Math.Abs(s.ReadUDeg) > 1e-9).ToList();
                            if (uS.Count >= 2)
                            {
                                double uRef0 = ev.RotationBaseU ?? 0.0;
                                uDevMax = uS.Max(s => Math.Abs(s.ReadUDeg - uRef0 - s.AngleDeg));
                            }
                        }
                        AppendLog($"[产物组合·证据①] 像素域圆性: 逐点半径残差RMS={(ev.RotationFitRmsPx.HasValue ? ev.RotationFitRmsPx.Value.ToString("F2") + "px" : "未采(旧档)")}"
                                  + "（应<3px；大 ⇒ 有脏点/非圆）");
                        AppendLog($"[产物组合·证据②] 域间互校: 像素半径×当量={(ev.RotationFitRadiusMm.HasValue ? ev.RotationFitRadiusMm.Value.ToString("F3") + "mm" : "当量不可用")}"
                                  + $"  vs  |ToolEccW|=|b|={bMagEv:F3}mm"
                                  + "（两者差应<5%；大 ⇒ H 各向异性/畸变未校正或矩阵与采样不同次，|b| 不可当基准）");
                        AppendLog($"[产物组合·证据③] 机位固定性: 分散={(ev.RotationMotionSpreadMm.HasValue ? ev.RotationMotionSpreadMm.Value.ToString("F3") + "mm" : "未采(旧档)")}"
                                  + "（应<1mm；大 ⇒ 采样期间动过 XY，固定机位模型不成立）");
                        AppendLog($"[产物组合·证据④] 弧覆盖度: {(ev.RotationArcCoverageDeg.HasValue ? ev.RotationArcCoverageDeg.Value.ToString("F0") + "°" : "未采(旧档)")}"
                                  + "（应≥90°；小 ⇒ 圆心沿缺弧方向误差被放大）");
                        AppendLog($"[产物组合·证据⑤] U 到位精度: {(uDevMax < 0 ? "未采到实读U(ReadUDeg全空)" : uDevMax.ToString("F3") + "°")}"
                                  + "（应≤0.5°；大 ⇒ b 的方向被整体转过同样度数，先修 U 闭环）");
                        AppendLog($"[产物组合·证据] 采样基准: U_ref={(ev.RotationBaseU.HasValue ? ev.RotationBaseU.Value.ToString("F2") + "°" : "未记录")}"
                                  + $" 停车位P_f=({ev.RotationBaseX:F3},{ev.RotationBaseY:F3}) 采样点={(ev.RotationSamples != null ? ev.RotationSamples.Count : 0)}"
                                  + "（U_ref 与九点 U0 应一致，否则 b 的参考姿态不对）");
                        AppendLog("  → 现场复核（零风险、不用动轴）：量『吸嘴尖↔杆端 mark』水平距离应≈|b|，方位同上表中的 ToolEccAngleDeg。");
                        if (!string.IsNullOrWhiteSpace(ev.EvidenceFilePath))
                            AppendLog($"[产物组合·证据] 过程证据 JSON/日志: {ev.EvidenceFilePath}（同名 .log.txt 为配套关键日志）");
                    }

                    // ★2026-09-15：把"档案声明的域 + 生效口径 + 会走哪条算式"开机就摊开。
                    //   现场踩坑：H 已消杆（吸嘴域）但仍按杆端域补 O/e ⇒ 双重补偿，结果不对且无闸门拦。
                    //   这条日志就是那道闸门——口径一旦不是预期的，开窗即可见。
                    var effFlags = _bundle.ResolveFlags(EffectiveSolveMode);
                    string effFormula = effFlags.NozzleDomain
                        ? "吸点 = H(u)"
                        : (effFlags.NeedO
                            ? (effFlags.RotationTerm
                                ? "吸点 = P_photo+O−H(u) − R(U−U0)·e"
                                : "吸点 = P_photo+O−H(u)（免 U 项）")
                            : (effFlags.ToolOffsetTerm
                                ? (_bundle.HasEthToolOffset
                                    ? $"吸点 = H(u) + b，b=对针t直量({_bundle.T?.ToolOffsetWx:F3},{_bundle.T?.ToolOffsetWy:F3})"
                                    : (_bundle.HasRodOffset
                                        ? $"吸点 = H(u) + b，b=杆端偏心推算({_bundle.RodOffsetWx:F3},{_bundle.RodOffsetWy:F3})"
                                        : "吸点 = H(u) + b（b 未标 ⇒ 退化为 H(u)，落点偏杆端）"))
                                : "吸点 = H(u)（直接拍工件）"));
                    AppendLog($"[产物组合·口径] {SolveModeHint}"
                              + $"  | 生效口径={(EffectiveSolveMode == CalibrationSolveMode.FromProfile ? "跟随档案" : SelectedSolveMode?.Text ?? "跟随档案")}"
                              + $"  ⇒ {effFormula}");

                    if (effFlags.NeedO && !HasBundleRotationCenter)
                    {
                        AppendLog("⚠ 本方案按当前消费口径需要旋转中心 O，但档案缺 O——落点将退化为视觉直吸（不可信），请先完成旋转标定 + 对针求 e。");
                    }
                    if (effFlags.ToolOffsetTerm && !_bundle.HasEthToolOffset && !_bundle.HasRodOffset)
                    {
                        AppendLog("⚠ 固定相机+延伸杆标定：H 是【杆端域】矩阵，命令到 H(u) 时落在特征上的是【杆端 mark】而不是吸嘴尖"
                                  + "（偏出量 = 杆端偏心 b）。平台约定用 b 补：吸点 = H(u) + b。"
                                  + "b 的两条来源（都在标定里，不必手工算）：① 对针专窗做 EyeToHand 图像对针（直量，最准）；"
                                  + "② 旋转中心标定顺带产出的 ToolEccW（推算；残差≈1~3mm——注意 8mm 那个数是 O 的偏差，不是 b 的）。");
                    }
                    // ★2026-09-15：b 已生效时给"预期+反证"——让操作员一眼知道该看到什么、以及看到别的意味着什么。
                    if (effFlags.ToolOffsetTerm && !effFlags.NozzleDomain && !effFlags.NeedO
                        && (_bundle.HasEthToolOffset || _bundle.HasRodOffset))
                    {
                        double bb = Math.Sqrt(_bundle.RodOffsetWx * _bundle.RodOffsetWx + _bundle.RodOffsetWy * _bundle.RodOffsetWy);
                        string bsrc = _bundle.HasEthToolOffset ? "对针 t（直量）" : "ToolEccW（推算）";
                        AppendLog($"  → 【本次口径·固定相机杆端域】b={bsrc}，|b|={bb:F3}mm（若 b 来自推算，残差≈1~3mm；8mm 是 O 的偏差不是 b 的）。");
                        AppendLog($"  → 预期：点选像素后【吸嘴尖】压中该像素，而【杆端 mark】会偏出约 |b|={bb:F3}mm。");
                        AppendLog($"  → 反面对照：若你看到的是【mark 落点上、吸嘴不在】，说明 b 方向取反——"
                                  + $"切到口径⑤(吸点=H(u)−b) 再点同一像素，两者相差 2|b|≈{2 * bb:F1}mm，一眼可辨。");
                        AppendLog("  → ⚠ 验收前确认：本次低速到位按 U 归 CalibU0 执行；b 与九点同姿态(U0)才成立"
                                  + "（旋转阶段若报过『基准角 U_ref 与九点基准 U0 相差 N°』，先回 U0 重采旋转点）。");
                    }
                    if (_bundle.IsNozzleDomainH && _bundle.HasRotationCenter)
                    {
                        AppendLog("⚠ 档案声明「H 已在吸嘴域」但库里同时存在旋转中心 O——若继续按杆端域补 O/e 就是【双重补偿】。"
                                  + "请确认 H 到底是消杆后的矩阵还是原始矩阵（判据见上机验证单），确认后再定口径。");
                    }
                }
            }
            RefreshSummary();
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
                Profile.BindRotationAxisIndex,
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
            // 安全收尾：若会话中开过真空则关闭（防残留吸住状态）
            if (_vacuumOn && _facade != null)
            {
                try { _facade.SetOutput(VacuumIoIndex, false); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("校验台收尾关真空异常: " + ex.Message); }
                _vacuumOn = false;
            }
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
            _moveRodTermApplied = false;
            PickInfoText = $"像素 ({col:F1}, {row:F1}) → 矩阵换算 w=({_pickWorldX:F3}, {_pickWorldY:F3}) mm（落点定案见下）";
            HasPick = true;
            // 2026-09-09：日志必须自带像素坐标——否则事后复盘时"点了哪个点"无从查证，
            // 无法与 H 正向 / H 反向 / 九点回放的数据相互印证（本次实机复盘就卡在这里）。
            AppendLog($"点选换算(矩阵裸输出 w=H(u)): 像素(col={col:F1}, row={row:F1}) → X={_pickWorldX:F3}, Y={_pickWorldY:F3} mm。");

            // ===== 落点语义定案（2026-09-10 重构：按工位依赖的标定产物组合分型，唯一真源 CalibrationGeometry）=====
            // 组合 → 换算（ETH/EIH 按 Profile.EyeMode 自动判定）：
            //   ETH（固定相机）   ：X_obj = H(u)；吸点 = X_obj + TCO（有 t 时）
            //   EIH（相机随手走） ：X_obj = P_photo + O − H(u)；吸点 = X_obj − R(U−U0)·e（有 e 时）
            try
            {
                var nowPose = TryReadCurrentPose();
                // 成像时刻机位优先（抓拍定格时实读）；未走定格路径（搭车外部流/实时点选）用点选瞬间机位
                var basePose = _photoPose ?? nowPose;
                double u0 = Profile.CalibU0 ?? 0.0;
                bool uKnown = basePose.HasValue && !double.IsNaN(basePose.Value.U);
                double uGo = uKnown ? basePose.Value.U : u0;

                if (basePose.HasValue)
                {
                    string srcTag = _photoPose.HasValue ? "定格实读(成像时刻)" : "点选瞬间(未定格)";
                    AppendLog($"  拍照机位[{srcTag}]: X={basePose.Value.X:F3} Y={basePose.Value.Y:F3} Z={basePose.Value.Z:F1} U={basePose.Value.U:F1}°");

                    // ★ Z 守护（按档案相机安装特性 CameraMovesWithZ 分支）
                    if (Profile.CalibZ.HasValue && !double.IsNaN(basePose.Value.Z))
                    {
                        double dz = basePose.Value.Z - Profile.CalibZ.Value;
                        if (Math.Abs(dz) > 5.0)
                        {
                            if (CameraZLinked)
                            {
                                AppendLog($"  ⚠⚠⚠ 拍照 Z={basePose.Value.Z:F1}mm ≠ 标定高度 {Profile.CalibZ.Value:F1}mm (Δ={dz:F1}mm) —— 相机随 Z 升降:像素当量失真,本次换算不可信!请把 Z 移回标定高度后重新抓拍定格再点选。");
                            }
                            else
                            {
                                // ⚠ 2026-09-11 口径修正：旧文案断言"相机固定不随 Z ⇒ 成像与 Z 无关"，
                                //   该结论只对"靶固定在工作台平面（标定面）"成立；下相机仰视拍摄
                                //   【吸嘴悬持的工件】时，工件离开标定平面越远，视差/缩放偏差越大。
                                AppendLog($"  拍照 Z={basePose.Value.Z:F1}mm（标定 Z={Profile.CalibZ.Value:F1}mm, Δ={dz:F1}mm）——相机固定不随 Z："
                                          + "若标定靶固定在工作台平面则成像与 Z 无关；但若本相机是【下相机仰视拍吸嘴悬持的工件】，"
                                          + "工件离开标定 Z 平面会带来视差/缩放偏差（Δ 越大越不可信），请把工件送回标定 Z 高度后重新抓拍定格再点选。");
                            }
                        }
                    }

                    if (_photoPose.HasValue && nowPose.HasValue
                        && (Math.Abs(nowPose.Value.X - _photoPose.Value.X) > 0.5
                            || Math.Abs(nowPose.Value.Y - _photoPose.Value.Y) > 0.5))
                    {
                        AppendLog($"  ⚠ 定格后机械手已移动 (Δ={nowPose.Value.X - _photoPose.Value.X:F2},{nowPose.Value.Y - _photoPose.Value.Y:F2})mm —— 本点换算不再对应定格画面,请回定格位重选或重拍。");
                    }
                }

                // —— 按组合计算吸点（X_obj=工件特征真位；P_go=吸嘴命令位）——
                // ★2026-09-12 收敛到相机级消费门面 CameraCalibrationBundle.Solve（唯一真源 CalibrationGeometry），
                //   消除这里手抄的下相机/EIH/ETH 三分型公式。门面内部与生产引擎/发布链同源同果。
                double objX, objY, cmdX, cmdY;
                string mode;

                double photoX = basePose?.X ?? 0.0;
                double photoY = basePose?.Y ?? 0.0;
                double uGoNow = uGo;

                var solved = _bundle != null
                    ? _bundle.Solve(_pickCol, _pickRow, photoX, photoY, uGoNow, EffectiveSolveMode)
                    : null;

                if (solved == null)
                {
                    // 门面未构建（理论不发生）→ 退化为矩阵裸输出直吸
                    objX = _pickWorldX; objY = _pickWorldY; cmdX = _pickWorldX; cmdY = _pickWorldY;
                    mode = "⚠ 门面未构建——退化为视觉直吸";
                }
                else if (!solved.Success)
                {
                    objX = _pickWorldX; objY = _pickWorldY; cmdX = _pickWorldX; cmdY = _pickWorldY;
                    mode = "⚠ " + solved.Error;
                }
                else
                {
                    objX = solved.ObjX; objY = solved.ObjY;
                    cmdX = solved.CmdX; cmdY = solved.CmdY;
                    mode = solved.Mode;
                    // 下相机相对纠偏的额外诊断日志（δ 明细）
                    if (IsDownCamera && solved.Success)
                    {
                        AppendLog($"  下相机 R_cdown 像素({DownRotCenterOwner.DownRotCenterCol:F1},{DownRotCenterOwner.DownRotCenterRow:F1})"
                                  + $" 源「{ShortName(DownRotCenterOwner?.Name)}」");
                        AppendLog($"  下相机 R_img 机械({_pickWorldX:F3},{_pickWorldY:F3}) → 吸点=({cmdX:F3},{cmdY:F3})mm");
                    }
                }

                // ★2026-09-15：门面 Trace 整段入日志（口径→输入→矩阵→档案声明→生效口径→结果→反面对照）。
                //   现场"结果不对"时，读这串即可判定是【档案声明错】/【聚合丢产物】/【公式分型错】，
                //   不必再去猜或复现。行首留两空格缩进，便于与主链路日志区分。
                if (solved != null && !string.IsNullOrWhiteSpace(solved.Trace))
                {
                    foreach (var tline in solved.Trace.Split('\n'))
                        AppendLog("  " + tline.TrimEnd());
                }

                // ★2026-09-15：上相机的三口径 A/B/C 对照（同像素、同拍照位）。
                //   判据语义：三点差≈0 ⇒ 口径与落点无关，问题在别处（矩阵/装配/设备）；
                //             差几毫米  ⇒ 口径分型直接决定落点，必须把档案声明改对。
                if (!IsDownCamera && _bundle != null)
                {
                    AppendSolveCompare(photoX, photoY, uGoNow, cmdX, cmdY);
                }

                _moveTargetX = cmdX;
                _moveTargetY = cmdY;
                _moveBaseX = objX;      // 需 O/e 补偿时=特征真位 X_obj（与 U 无关），执行时按实时 U 重算命令位
                _moveBaseY = objY;
                if (!IsDownCamera)
                {
                    // ★2026-09-12：需 O 补偿（EIH）+ e 齐备 → 任意 U 角精确。
                    // ★2026-09-15：改读门面 ResolveFlags（口径唯一入口）——同轴吸嘴/吸嘴域直吸/
                    //   固定相机+杆端域（走 t）时都没有 U 旋转项，此处若仍按 NeedsOCompensation 判定，
                    //   会在到位时多减一次 R(U−U0)·e（判据写第二遍就会在边界分叉，故必须与 Solve 同源）。
                    var effF = _bundle?.ResolveFlags(EffectiveSolveMode)
                               ?? (NozzleDomain: false, NeedO: NeedsOCompensation, RotationTerm: true, ToolOffsetTerm: false);
                    _moveRotReady = effF.NeedO && effF.RotationTerm && HasBundleEcc;
                    // ★2026-09-15：固定相机+杆端域的到位置也带补偿（补 b），别漏报成"没加补偿"。
                    _moveRodTermApplied = effF.ToolOffsetTerm
                                          && (_bundle?.HasEthToolOffset == true || _bundle?.HasRodOffset == true);
                }
                _poseCorrectionApplied = !IsDownCamera && (HasT || _moveRotReady || _moveRodTermApplied);

                PickInfoText = $"像素 ({col:F1}, {row:F1}) → H(u)=({_pickWorldX:F3},{_pickWorldY:F3}) mm\n"
                             + $"吸点 = ({_moveTargetX:F3}, {_moveTargetY:F3}) mm 【{ShapeName} · {LayoutName}】";
                AppendLog($"  落点定案[{ShapeName}|{LayoutName}]: H(u)=({_pickWorldX:F3},{_pickWorldY:F3}) → 吸点=({_moveTargetX:F3},{_moveTargetY:F3})mm  [{mode}]");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("点选读数诊断异常: " + ex.Message);
            }
        }

        /// <summary>
        /// ★2026-09-15：同一像素点走各口径各算一次吸点，列出与"生效口径"的差。
        /// 这是本工位口径之争的**判据本体**（不靠推演）：口径分型对不对，看差值说话。
        /// ④/⑤ 是固定相机+延伸杆的【符号 A/B】：只有一个是"吸嘴正好落在点上"，
        /// 另一个必然落在 2|b|（≈213mm）之外的反方向——所以选错不会安静通过。
        /// </summary>
        private void AppendSolveCompare(double photoX, double photoY, double uGoDeg,
            double refX, double refY)
        {
            var items = new (CalibrationSolveMode Mode, string Tag)[]
            {
                (CalibrationSolveMode.FromProfile,        "跟随档案(生效)"),
                (CalibrationSolveMode.NozzleDomainDirect, "①吸嘴域直吸"),
                (CalibrationSolveMode.RodEndNoRotation,   "②杆端域免U项"),
                (CalibrationSolveMode.RodEndWithRotation, "③杆端域含U项"),
                (CalibrationSolveMode.FixedCameraPlusRodOffset,  "④固定相机+b"),
                (CalibrationSolveMode.FixedCameraMinusRodOffset, "⑤固定相机-b"),
            };
            AppendLog("[口径对照] 同一点位各口径吸点（同像素/同拍照位；Δ=相对生效口径）：");
            foreach (var (m, tag) in items)
            {
                var r = _bundle.Solve(_pickCol, _pickRow, photoX, photoY, uGoDeg, m);
                if (!r.Success)
                {
                    AppendLog($"  {tag}: 不可用 — {r.Error}");
                    continue;
                }
                // ⚠ 退化口径（缺 O 等）返回的吸点等于直吸，Δ 会假性为 0——
                //   必须标出来，否则会把它读成"该口径与直吸等价"（假绿）。
                if (r.Mode != null && r.Mode.Contains("⚠"))
                {
                    AppendLog($"  {tag}: ⚠该口径不可用（退化）— {r.Mode.Replace("⚠", "").Trim()}；此行 Δ 无意义，勿据此判「口径无罪」");
                    continue;
                }
                AppendLog($"  {tag}: ({r.CmdX:F3},{r.CmdY:F3})mm  Δ=({r.CmdX - refX:F3},{r.CmdY - refY:F3})mm");
            }
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

        /// <summary>
        /// 解析【低速到位】的 Z 下压目标 —— "工具尖压住工件特征"的那个 Z（吸住件必须够到的高度）。
        /// 口径定义与兜底链见 <see cref="CalibrationProfile.TryResolvePressDownZ"/>（模型层唯一真源）：
        /// ① NozzleAlignZ 对针压住高度 R_nZ → ② BasePosZ 标定基准点 Z → ③ CalibZ 标定面高度。
        /// ETH（固定相机）工位在旧版对针专窗里不回写 NozzleAlignZ → 只认 ① 会让到位只动 XY。
        /// </summary>
        private (double Z, string Source)? ResolvePressDownZ()
        {
            return Profile.TryResolvePressDownZ(out double z, out string src)
                ? (z, src)
                : ((double Z, string Source)?)null;
        }

        // ==========================================================================================
        // 可达域预检（2026-09-15 新增）
        //
        // 【为什么需要】本工位（固定相机 + 延伸杆）的标定口径是 吸点 = H(u) + b：
        //   H 的域是"杆端 mark 落在该像素时的命令位"，而吸嘴尖恒在杆端的固定位移 b 处
        //   （b=(5.943,132.172)mm、|b|=132.3mm，全在 +Y）。⇒ 现场把工件摆到"机械可达域图上
        //   看着在圈内"的位置，加 b 之后命令点反而被推到 r>400mm 的外圈外 ⇒ 控制器直接 4007。
        //   2026-09-15 现场连踩两次：r=402.9mm（超 3.5mm）、r=421.9mm（超 22.5mm）；
        //   而同一批成功落点 r=385.2mm（余量仅 14.2mm）—— 一直在边界上蹭。
        //
        // 【为什么不拦只提示】判据来自 09-11 的实测扫描（CHECK=TargetOK 的 /L∪/R 并集），
        //   外边界是物理伸展上限（换手系也救不了），内边界是"最宽松的手系"，故内圈判定偏乐观；
        //   且扫描平面是 z=-100/u=0，与走位姿态不完全同平面。⇒ 只作为【预警与定量指引】，
        //   最终仍以控制器裁决为准（4001/4007/2997 原样透传）。拿不到数据时显式说"不判定"，
        //   绝不当成"通过"（量不可得 ⇒ 标判据失效，不许静默放行）。
        // ==========================================================================================

        private ReachMapData _reachMap;
        private bool _reachMapTried;

        /// <summary>可达域扫描结果的候选目录（按优先级）：① 运行目录 Data\ReachMap（现场放置）
        /// ② 仓库 .workbuddy\_verify_out（扫描器与硬件控制台落盘处）</summary>
        private static IEnumerable<string> ReachMapCandidateDirs()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
            yield return Path.Combine(baseDir, "Data", "ReachMap");
            yield return Path.GetFullPath(Path.Combine(baseDir, @"..\..", ".workbuddy", "_verify_out"));
        }

        /// <summary>懒加载最近一次实测可达域（取最新 mtime 且能解析成 ≥3 方向的那份）</summary>
        private ReachMapData EnsureReachMap()
        {
            if (_reachMapTried) return _reachMap;
            _reachMapTried = true;
            foreach (var dir in ReachMapCandidateDirs())
            {
                IEnumerable<FileInfo> files;
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    files = new DirectoryInfo(dir).GetFiles("*.json")
                                .OrderByDescending(f => f.LastWriteTimeUtc).Take(8).ToList();
                }
                catch { continue; }

                foreach (var f in files)
                {
                    try
                    {
                        string err;
                        var map = ReachMapData.LoadFromFile(f.FullName, out err);
                        if (map == null || map.IsEmpty) continue;
                        _reachMap = map;
                        AppendLog($"  [可达域] 预检判据来源: {f.FullName}（{map.Describe()}；"
                                + "来源为 CHECK=TargetOK 的 /L∪/R 并集，外边界=物理伸展上限）");
                        return _reachMap;
                    }
                    catch { /* 单份坏文件不阻断，继续找下一份 */ }
                }
            }
            return null;
        }

        /// <summary>
        /// 走位前预检目标点是否在实测可达域内；越界时给出"往哪收多少 mm"。
        /// 只提示不拦：控制器才是最终裁决者（4007/4001/2997 原样透传）。
        /// </summary>
        private void WarnIfTargetOutsideReach(double x, double y)
        {
            const double WarnMarginMm = 10.0;
            try
            {
                var map = EnsureReachMap();
                if (map == null || map.IsEmpty)
                {
                    AppendLog("  [可达域预检] 无实测可达域数据 ⇒ 本次【不判定】（不静默放行）。取数据："
                            + "硬件控制台 → 可达域 → 扫描（只发只读 CHECK，不发车），"
                            + @"或把 workspace_map_*.json 放进 Data\ReachMap。");
                    return;
                }

                var v = ReachMapGeometry.Evaluate(map, x, y, 0);
                string bandText = v.State == ReachPointState.Unsafe
                    ? "该方向无实测可达带"
                    : $"实测带=[{v.RIn:F2},{v.ROut:F2}]";
                AppendLog($"  [可达域预检] 目标({x:F3},{y:F3}) r={v.R:F2}mm θ={v.Deg:F1}° {bandText} ⇒ {v.Text}");

                if (v.State == ReachPointState.Safe)
                {
                    if (v.Slack < WarnMarginMm)
                    {
                        AppendLog($"  ⚠ 余量仅 {v.Slack:F1}mm（<{WarnMarginMm:F0}mm）：本机外圈边界实测 ~{v.ROut:F0}mm，"
                                + "点选的像素误差或工件稍挪就可能越界 ⇒ 建议把工件再向基座方向收 20mm 以上。");
                    }
                    return;
                }

                double shortBy = -v.Slack;
                if (v.State == ReachPointState.NearOuter)
                {
                    AppendLog($"  ⚠ 预计会被拒（4007 超动作区域）：目标半径已超出外边界 {shortBy:F1}mm。"
                            + $"把工件（特征点）沿【朝基座方向】收进来 ≥{shortBy + 20:F0}mm 再点选 —— "
                            + "注意本工位口径是 吸点=H(u)+b：命令点比【特征点】更靠外"
                            + "（b=(5.943,132.172)mm |b|=132.3mm，分量几乎全在 +Y），"
                            + "故机械可达域图上'看着在圈内'的特征点，加 b 之后仍可能越界。");
                }
                else if (v.State == ReachPointState.NearInner)
                {
                    AppendLog($"  ⚠ 预计会被拒（4001 关节超脉冲 / 2997）：目标在内边界以内 {shortBy:F1}mm"
                            + "（两臂收不拢，或落进 J1 限位扇区）。把工件向外移 "
                            + $"≥{shortBy + 20:F0}mm（或换拍照位 / 改摆放方向）后重试。");
                }
                else
                {
                    AppendLog("  ⚠ 该方向整体不可达（J1 限位扇区 / 无解）⇒ 换拍照位或改工件摆放方向后重试。");
                }
            }
            catch (Exception ex)
            {
                AppendLog("  [可达域预检] 判据失效（异常，已跳过，未判定）：" + ex.Message);
            }
        }

        /// <summary>
        /// 低速到位：XY 平移 → U 归标定角 CalibU0（姿态/偏心补偿一致）→ Z 下压到"工具尖压住工件特征"的高度。
        /// 之前只移动 XY，Z/U 不带入，吸嘴悬在高位碰不到工件表面——现补齐 Z/U 到位，让吸嘴尖真正触到点选特征
        /// （否则开真空也吸不住）。Z 目标取值见 <see cref="ResolvePressDownZ"/>。
        /// ★下相机（仰视二次对位）：不抬 Z 不归 U 不下压——工件已吸在吸嘴上悬空，只需 XY 相对纠偏移动，
        ///   让工件回到像素旋转中心正下方（相对纠偏动作，见 ApplyPick 下相机分支）。
        /// </summary>
        private void MoveToTarget()
        {
            if (!HasPick)
            {
                AppendLog("先点选一个实物特征点。");
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
                // ★ 下相机相对纠偏：只做 XY 相对移动，不动 Z/U（工件悬空已吸住，无抬 Z/下压语义）。
                if (IsDownCamera)
                {
                    AppendLog($"下相机相对纠偏 → X={_moveTargetX:F3}, Y={_moveTargetY:F3} mm（吸嘴反向移动 δ 让工件居中）...");
                    bool ok = _facade.MoveToXY(_moveTargetX, _moveTargetY);
                    if (ok)
                    {
                        AppendLog("  ✓ 相对纠偏到位 —— 目视下相机画面：工件特征应回到像素旋转中心 R_cdown（对准=✅通过；仍偏=❌偏差，说明 H_down 或 R_cdown 标定有误）。");
                    }
                    else
                    {
                        AppendLog("  相对纠偏被拒: " + (_facade.LastError ?? "未知原因"));
                    }
                    return;
                }

                // ★ 执行时按实时回转角求命令位（需 O/e 补偿组合）：_moveBase = 特征真位 X_obj（与 U 无关），
                //   P_go = X_obj − R(U_go − U0)·e。任意 U 角精确；其余组合保持点选时结算结果。
                double fx = _moveTargetX, fy = _moveTargetY;
                if (_poseCorrectionApplied && _moveRotReady)
                {
                    var cur = TryReadCurrentPose();
                    if (cur.HasValue && !double.IsNaN(cur.Value.U))
                    {
                        double u0 = Profile.CalibU0 ?? 0.0;
                        double uGo = cur.Value.U;
                        // ★ 用门面聚合的 e（可能来自吸嘴级 e 产物，而非本窗口绑定的 H 档案）
                        double eccX = _bundle?.E?.ToolOffsetPureWx ?? Profile.ToolOffsetPureWx;
                        double eccY = _bundle?.E?.ToolOffsetPureWy ?? Profile.ToolOffsetPureWy;
                        var cmd = CalibrationGeometry.CommandFor(_moveBaseX, _moveBaseY,
                            eccX, eccY, uGo, u0);
                        fx = cmd.X;
                        fy = cmd.Y;
                        _moveTargetX = fx;
                        _moveTargetY = fy;
                        AppendLog($"  执行时按实时角: U_go={uGo:F1}° (U0={u0:F1}°) → 吸点=X_obj({_moveBaseX:F3},{_moveBaseY:F3})−R(U_go−U0)·e=({fx:F3},{fy:F3})mm");
                    }
                }
                // 1) 先抬 Z 到 SafeZ：确保 XY 平移在高位进行，避免吸嘴低位刮碰工件/治具。
                if (_facade.MoveToZ(Profile.SafeZ))
                {
                    AppendLog($"  抬 Z → SafeZ={Profile.SafeZ:F1}mm（高位平移，防刮碰）");
                }

                // 2) XY 到位（高位平移）
                // ★ 2026-09-15：走位前先做可达域预检。本工位口径要 +b，命令点比特征点更靠外，
                //   机械可达域图上"看着在圈内"仍可能被控制器判 4007（现场连踩两次，见 WarnIfTargetOutsideReach 头注）。
                WarnIfTargetOutsideReach(fx, fy);
                AppendLog($"低速到位 → X={fx:F3}, Y={fy:F3} mm ...【{ShapeName} · {LayoutName}】");
                bool moved = _facade.MoveToXY(fx, fy);
                if (!moved)
                {
                    AppendLog("到位被拒: " + (_facade.LastError ?? "未知原因") + "（以控制器反馈为准：4007 超动作区域=外圈够不着 / 4001 超脉冲=内圈收不拢或 U 姿态 / 2997=Z 超软限）。提示：走位被拒≠换算错误——是目标点超出机械可达域，把工件或拍照位向可达环带中腰收拢后重试即可。");
                }
                else
                {
                    // 3) U 归标定基准角 CalibU0：让点选结算时的偏心补偿姿态成立（ETH 固定相机下，
                    //    命令位是基于 CalibU0 算的；若 U 停错角，偏心 e 退不掉导致吸点偏移）。
                    if (Profile.CalibU0.HasValue)
                    {
                        var curU = TryReadCurrentPose();
                        double uGo = Profile.CalibU0.Value;
                        if (curU.HasValue && !double.IsNaN(curU.Value.U) && Math.Abs(curU.Value.U - uGo) > 0.01)
                        {
                            AppendLog($"  U 归标定角 → {uGo:F2}°（当前 {curU.Value.U:F2}°；偏心补偿姿态一致）");
                            if (!_facade.MoveToU(uGo))
                                AppendLog("  ⚠ U 到位被拒: " + (_facade.LastError ?? "未知") + "（不影响 XY 判定，但偏心补偿可能不准）");
                        }
                        else if (curU.HasValue && !double.IsNaN(curU.Value.U))
                        {
                            AppendLog($"  U 已在标定角 {uGo:F2}°（当前 {curU.Value.U:F2}°），无需调整。");
                        }
                    }

                    // 4) Z 下压到"工具尖压住工件特征"的高度：让吸嘴尖真正触到点选特征表面（贴面目视判定）。
                    //    ★2026-09-11 修复：下压目标不能只认 NozzleAlignZ —— ETH(固定相机)工位的对针在
                    //    【对针专窗】完成，旧实现不回写该字段，档案里恒为 null → 到位只动 XY、Z 悬在高位，
                    //    吸嘴够不到工件面，开真空也吸不住。改按"对针压住高度 → 标定基准 Z → 标定面高度"取值。
                    var zResolved = ResolvePressDownZ();
                    if (zResolved.HasValue)
                    {
                        double zGo = zResolved.Value.Z;
                        AppendLog($"  下压 Z → {zGo:F1}mm（{zResolved.Value.Source}），吸嘴尖触工件表面...");
                        bool zMoved = _facade.MoveToZ(zGo);
                        if (zMoved)
                        {
                            AppendLog($"  ✓ 到位完成 —— 吸嘴尖已下压至 Z={zGo:F1}mm，目视确认是否正对目标特征(对准=✅通过；偏移=❌偏差)，随后可开真空吸住。");
                            if (!Profile.NozzleAlignZ.HasValue)
                            {
                                AppendLog("  ⚠ 本档案未记录对针压住高度 R_nZ（本次走兜底 Z）。建议到【对针专窗】重做一次对针——它会把压住高度回写档案，之后到位即精确复现。");
                            }
                        }
                        else
                        {
                            AppendLog($"  ⚠ Z 下压被拒: {(_facade.LastError ?? "未知原因")} —— XY 已到位但吸嘴未下压，请 JOG 手动下压或检查该 Z 是否超软限。");
                        }
                    }
                    else
                    {
                        AppendLog("  ✓ XY 到位（档案既无对针压住高度 R_nZ、也无标定基准 Z/标定面高度，无法自动下压）—— 请 JOG 手动下压至工件面高度目视。");
                    }
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

        // ==================== 轴操作 + 真空（2026-09-10 从机械臂调试台搬入） ====================

        /// <summary>走位可用：有点选 + 运动卡在位（对针职责已剥离，不再要求 p_tip 锚）</summary>
        private bool CanMove() => HasPick && !IsBusy && _facade != null;

        /// <summary>单轴步进可用：运动卡在位 + 非忙</summary>
        private bool CanStepMove() => _facade != null && !IsBusy;

        /// <summary>真空阀开关可用：运动卡在位 + 非忙</summary>
        private bool CanToggleVacuum() => _facade != null && !IsBusy;

        /// <summary>
        /// 单轴步进（与机械臂调试台 RobotDebugView 同款遥控器键位）：参数 "X+" / "X-" / "Y+" / "Y-" / "Z+" / "Z-" / "U+" / "U-"。
        /// 走 CalibrationMotionFacade 的相对步进原语（读反馈 + 增量 → 低速绝对到位），X/Y/Z/U 均可。
        /// </summary>
        private void StepMove(string p)
        {
            if (_facade == null)
            {
                AppendLog("未绑定运动卡，无法单轴步进。");
                return;
            }
            if (string.IsNullOrWhiteSpace(p) || p.Length < 2)
            {
                return;
            }
            char axis = char.ToUpperInvariant(p[0]);
            int sign = p.EndsWith("+") ? 1 : p.EndsWith("-") ? -1 : 0;
            if (sign == 0) return;
            if (axis != 'X' && axis != 'Y' && axis != 'Z' && axis != 'U') return;

            double dist = SelectedStepSize * sign;
            IsBusy = true;
            try
            {
                bool ok;
                switch (axis)
                {
                    case 'X': ok = _facade.MoveBy(dist, 0); break;
                    case 'Y': ok = _facade.MoveBy(0, dist); break;
                    case 'Z': ok = _facade.MoveByZ(dist); break;
                    case 'U': ok = _facade.MoveByU(dist); break;
                    default: ok = false; break;
                }
                string unit = axis == 'U' ? "°" : "mm";
                LastStepText = ok
                    ? $"{axis}{(sign > 0 ? "+" : "-")} {Math.Abs(dist):0.###}{unit} 完成"
                    : $"{axis}{(sign > 0 ? "+" : "-")} 被拒: {_facade.LastError ?? "未知原因"}";
                AppendLog($"[轴步进] {LastStepText}");
            }
            catch (Exception ex)
            {
                LastStepText = "轴步进异常: " + ex.Message;
                AppendLog("[轴步进] " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>开/关真空阀：吸住/放开（IO 号来自标定档案 PickVacuumIoIndex，与 Pick&Place 同语义）。</summary>
        private void SetVacuum(bool on)
        {
            if (_facade == null)
            {
                AppendLog("未绑定运动卡，无法控制真空阀。");
                return;
            }
            IsBusy = true;
            try
            {
                bool ok = _facade.SetOutput(VacuumIoIndex, on);
                if (ok)
                {
                    VacuumOn = on;
                    AppendLog(on
                        ? $"已开启真空 IO{VacuumIoIndex} —— 吸嘴吸住（到位后用于验证吸点是否对准）。"
                        : $"已关闭真空 IO{VacuumIoIndex} —— 吸嘴放开。");
                }
                else
                {
                    AppendLog("真空控制失败: " + (_facade.LastError ?? "未知原因"));
                }
            }
            catch (Exception ex)
            {
                AppendLog("真空控制异常: " + ex.Message);
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
        }

        // ==================== 工具 ====================

        /// <summary>
        /// 构建相机级消费门面：从全库按 (工位, 槽) 聚合 H/e/t/s。
        /// 槽键用本窗口绑定 Profile 的口径（GuessSlotKey），全库仅补齐同槽的 e/t/s 产物。
        /// 像素→世界映射委托注入 ICalibrationService.MapPixelToWorld。
        /// </summary>
        private CameraCalibrationBundle BuildBundle()
        {
            string station = Profile?.BoundStationCode;
            string slot = CalibrationProfileSessionPlanner.GuessSlotKey(Profile);
            List<CalibrationProfile> all = null;

            // ★2026-09-15 存储统一：产物聚合源 = 标定中心/向导写入的【同一个】JSON 仓库
            //   （Config\Calibrations\*.json）。此前这里读 CalibrationService.GetAllProfiles()，
            //   而写入方走 LiteDB ⇒ 目录为空 ⇒ 聚合退化为单档案 ⇒ e/O 永远"未标"（假阴性）。
            string primaryErr = null;
            try
            {
                var repo = StorageFactory.CreateCalibrationProfileRepository();
                var pos = repo?.GetAll();
                if (pos != null)
                {
                    all = pos.Where(p => p?.Model != null).Select(p => p.Model).ToList();
                    var jsonRepo = repo as Grayson.Vision.Repository.Implementations.JsonCalibrationProfileRepository;
                    primaryErr = jsonRepo?.LastError;   // 部分文件损坏时不静默
                }
            }
            catch (Exception ex) { primaryErr = ex.Message; }

            // 兜底：仍读不到就试 CalibrationService（同目录的另一种读法），两者都空才认"空源"
            string fallbackNote = null;
            if (all == null || all.Count == 0)
            {
                try
                {
                    var r = _calibService?.GetAllProfiles();
                    if (r != null && r.Success && r.Data != null && r.Data.Count > 0)
                    {
                        all = r.Data;
                        fallbackNote = "主源（JSON 仓库）为空，已回退 CalibrationService.GetAllProfiles";
                    }
                }
                catch (Exception ex) { fallbackNote = "兜底读法也失败: " + ex.Message; }
            }

            if (all == null || all.Count == 0)
            {
                // 空源 ≠ "现场没标定"。必须显式区分，否则现场会朝错方向补标定。
                all = new List<CalibrationProfile>();
                if (Profile != null) all.Add(Profile);
                _aggregationSourceNote =
                    "⚠ 产物聚合源为空（" + Grayson.Vision.Repository.Implementations.JsonCalibrationProfileRepository.RootDirectory
                    + " 下没有可读档案）——本次只按【本窗口这一份档案】判定，"
                    + "日志里 e/O 的「未标」不可信（可能是聚合读不到，而不是真的没标）。"
                    + (string.IsNullOrWhiteSpace(primaryErr) ? "" : " 主源错误: " + primaryErr)
                    + (string.IsNullOrWhiteSpace(fallbackNote) ? "" : "  " + fallbackNote);
            }
            else
            {
                int mine = Profile == null ? 0 : all.Count(p => p != null && p.Id == Profile.Id);
                _aggregationSourceNote = "产物聚合源: " + all.Count + " 份档案（JSON 仓库），本档案在其中 " + mine + " 份"
                    + (string.IsNullOrWhiteSpace(primaryErr) ? "" : "；⚠ " + primaryErr)
                    + (string.IsNullOrWhiteSpace(fallbackNote) ? "" : "；" + fallbackNote);
            }

            return CameraCalibrationBundle.Build(all, station, slot, (double px, double py, out double wx, out double wy, out string err) =>
            {
                err = null;
                var res = _calibService.MapPixelToWorld(MatrixPath, px, py);
                if (res == null || !res.Success) { err = res?.Message ?? "映射失败"; wx = 0; wy = 0; return false; }
                wx = res.Data.WorldX; wy = res.Data.WorldY; return true;
            });
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
            (VerdictPassCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (VerdictFailCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ResetPickCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StepMoveCommand as RelayCommand<string>)?.RaiseCanExecuteChanged();
            (VacuumOnCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (VacuumOffCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

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
}
