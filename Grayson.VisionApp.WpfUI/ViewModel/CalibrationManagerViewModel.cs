//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationManagerViewModel.cs
//===================================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Core.Processes;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.Repository.Services;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.WpfUI.View;

namespace Grayson.Vision.WpfUI.ViewModel
{


 

    /// <summary>
    /// 工位跳转导航参数载体
    /// </summary>
    public class StationNavigationContext
    {
        public string StationCode { get; set; }
        public string StationName { get; set; }
        public string DeviceId { get; set; }
    }

    public class CalibrationManagerViewModel : ViewModelBase, INavigationAware
    {
        private readonly ICalibrationService _calibService;
        private readonly ICalibrationProfileRepository _profileRepository;
        private readonly Grayson.Vision.Contracts.Recipe.Services.IRecipeStorageService _recipeStorage;

        /// <summary>
        /// 当前打开的标定向导窗口引用（2026-09-06 非模态化防重入）。
        /// 非模态窗口不再阻塞主界面，若不加单例，用户可重复点开多个向导共享同一相机/轴卡；
        /// 已有向导打开时新入口只 Activate 已开窗口。窗口 Closed 时置 null。
        /// </summary>
        private CalibrationWizardWindow _activeWizardWindow;

        /// <summary>
        /// 当前打开的标定校验台窗口引用（2026-09-06 非模态化防重入，同向导样板）。
        /// 校验台原 ShowDialog 模态打开会禁掉主窗与机械臂调试等并行窗口；改 Show() 后
        /// 与主界面并行操作，故同样以单例防重复点开共享同一相机/轴卡。窗口 Closed 时置 null。
        /// </summary>
        private CalibrationVerifierWindow _activeVerifierWindow;

        #region 工位定位域（镜像模板工作台：跳入=钉住该工位，可回全库）

        private string _scopeStationCode = string.Empty;
        private string _scopeStationName = string.Empty;
        private string _scopeNote = string.Empty;

        /// <summary>是否处于"某工位"定位态（决定"回全库"按钮显隐 / 列表过滤 / 新建自动归属）</summary>
        public bool IsStationScope => !string.IsNullOrEmpty(_scopeStationCode);

        /// <summary>页头定位胶囊文案</summary>
        public string ScopeChipText => IsStationScope ? $"工位 {_scopeStationCode}" : "全库";

        /// <summary>页头副标题：当前模式的一句话说明</summary>
        public string ScopeHintText => IsStationScope
            ? $"以下展示/新建的方案自动归属工位 [{_scopeStationCode}]；新建会按本工位命名。点右上角「回全库」浏览全部方案。"
            : "全局标定方案中心：从工位装配旅程第 7 步进入会自动按该工位需求档案建议合适的标定方案并钉住定位。";

        /// <summary>定位操作后的瞬时说明（如"已按档案自动创建 XX 方案"）</summary>
        public string ScopeNoteText
        {
            get => _scopeNote;
            private set
            {
                if (Set(ref _scopeNote, value))
                {
                    OnPropertyChanged(nameof(HasScopeNote));
                }
            }
        }

        /// <summary>是否有定位操作说明（ScopeNoteText 非空）</summary>
        public bool HasScopeNote => !string.IsNullOrEmpty(_scopeNote);

        /// <summary>回全库浏览（解除工位钉）</summary>
        public ICommand BackToGlobalCommand { get; private set; }

        /// <summary>左侧列表实际展示集（定位态=只显示该工位方案；全库=全部）。主集仍为 CalibrationProfiles。</summary>
        public ObservableCollection<CalibrationProfile> VisibleProfiles { get; } = new ObservableCollection<CalibrationProfile>();

        #endregion

        #region 相机槽候选清单（2026-09-05 L2：档案含多相机槽 → 逐槽候选，勾选确认后批量创建，不自动落库）

        /// <summary>单槽标定候选（勾选=创建一条该类型的 CalibrationProfile）</summary>
        public class CalibrationCandidate : ViewModelBase
        {
            private bool _isSelected = true;
            /// <summary>是否勾选创建（主线必做默认勾选；可选任务默认不勾）</summary>
            public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

            /// <summary>来源计划任务（相机级/工具级推导的完整上下文）</summary>
            public CalibrationTask Task { get; set; }
            public CalibrationTaskKind Kind { get; set; }
            /// <summary>族徽标短词（相机H·走位 / 工具偏心e / 飞拍纠偏…）</summary>
            public string KindBadge { get; set; }
            /// <summary>相机槽键或吸嘴号（chip 展示文本）</summary>
            public string ChipText { get; set; }
            /// <summary>相机槽键（创建 profile CameraId 用）</summary>
            public string SlotKey { get; set; }
            /// <summary>吸嘴通道键（工具任务 1/2…；相机任务 1）</summary>
            public string NozzleKey { get; set; }
            /// <summary>工具级任务？（旋转e/对针按吸嘴独立）</summary>
            public bool IsToolLevel { get; set; }
            /// <summary>安装 · 用途 / 吸嘴 · 目的 摘要</summary>
            public string TagText { get; set; }
            public string TypeDisplay { get; set; }
            public string Suggestion { get; set; }
            /// <summary>推导备注（现场确认点等）</summary>
            public string Note { get; set; }
            public CalibrationType Type { get; set; }
            public EyeMode EyeMode { get; set; }
        }

        private bool _candidatesVisible;
        /// <summary>候选面板可见（工位定位态 + 档案有相机槽 + 该工位尚无任何方案）</summary>
        public bool CandidatesVisible
        {
            get => _candidatesVisible;
            private set
            {
                if (Set(ref _candidatesVisible, value))
                {
                    OnPropertyChanged(nameof(CandidatesSummary));
                }
            }
        }

        public ObservableCollection<CalibrationCandidate> Candidates { get; } = new ObservableCollection<CalibrationCandidate>();

        /// <summary>勾选统计文案（候选行勾选变化时由 NotifyCandidateCheckChanged 刷新；相机级/工具级分列）</summary>
        public string CandidatesSummary => CandidatesVisible && Candidates.Count > 0
            ? $"将创建 {Candidates.Count(c => c.IsSelected)} / {Candidates.Count} 条标定方案"
              + $"（相机H×{Candidates.Count(c => !c.IsToolLevel)} · 工具偏心e×{Candidates.Count(c => c.IsToolLevel)}）"
            : string.Empty;

        /// <summary>是否至少勾选一项（创建命令可用性）</summary>
        public bool HasCheckedCandidates => Candidates.Any(c => c.IsSelected);

        public RelayCommand CreateCandidatesCommand { get; private set; }

        /// <summary>收起候选面板（暂不批量创建，手动逐个新建亦可）</summary>
        public RelayCommand DismissCandidatesCommand { get; private set; }

        /// <summary>候选行 CheckBox 勾选变化由 code-behind 回调：刷新统计/命令可用性</summary>
        public void NotifyCandidateCheckChanged()
        {
            OnPropertyChanged(nameof(CandidatesSummary));
            CreateCandidatesCommand?.RaiseCanExecuteChanged();
        }

            #endregion

        #region P3 任务卡视图（2026-09-05：旧「三平行大按钮」收敛为按量任务卡 + 卡内动作）

        /// <summary>
        /// 任务卡行 VM：包一张派生卡（CalibrationCardModel）并为行模板暴露展示/状态刷子。
        /// 动作由卡主命令（RunWizardForCardCommand 等）以 CommandParameter=本行执行。
        /// </summary>
        public class ArtifactTaskCardVm
        {
            public CalibrationCardModel Card { get; }
            public ArtifactTaskCardVm(CalibrationCardModel card)
            {
                Card = card;
            }

            public string ArtifactId => Card.ArtifactId;
            public string QuantityBadge => Card.QuantityBadge;
            public string QuantityText => Card.QuantityText;
            public string LayoutText => Card.LayoutText;
            public string ScopeText => Card.ScopeText;
            public string PathText => Card.PathText;
            public CalibrationArtifactState State => Card.State;
            public string StateText => Card.StateText;
            public string StateDetail => Card.StateDetail;
            public string DepText => Card.DepText;
            public bool HasData => Card.HasData;
            public bool IsRequired => Card.IsRequired;
            public bool IsEyeInHandTExpired => Card.EyeInHandTExpired;
            public bool HasDep => !string.IsNullOrWhiteSpace(Card.DepText);
            public bool HasDetail => !string.IsNullOrWhiteSpace(Card.StateDetail);
            public bool HasReason => !string.IsNullOrWhiteSpace(Card.Reason);
            public bool HasHint => HasDep || HasDetail || HasReason;
            public bool IsExpired => Card.State == CalibrationArtifactState.Expired;
            public bool IsPublished => Card.State == CalibrationArtifactState.Published;

            // —— 卡型 → 动作可见性（能否点由卡命令 CanExecute 再闸）——
            public bool IsH => Card.Quantity == CalibrationQuantity.HandEye;
            public bool IsE => Card.Quantity == CalibrationQuantity.ToolRotation;
            public bool IsT => Card.Quantity == CalibrationQuantity.ToolOffset;
            public bool IsS => Card.Quantity == CalibrationQuantity.PixelScale;
            /// <summary>t 卡是否 EyeInHand 布局（间接对针）——2026-09-08 起 EIH t 走向导六步模板</summary>
            public bool IsEihT => IsT && Card.Layout == EyeMode.EyeInHand;
            /// <summary>「🚀 引导/重标」按钮：H/e/s + EIH t（间接对针走向导）；ETH t 走独立对针窗（🎯）</summary>
            public bool ShowWizardAction => IsH || IsE || IsS || IsEihT;
            public bool ShowVerifyAction => IsH;
            /// <summary>「🎯 对针」按钮：仅 EyeToHand t（图像对针独立窗）；EIH t 改走向导（🚀）</summary>
            public bool ShowAlignAction => IsT && !IsEihT;
            public bool ShowPublishAction => IsH || IsE || IsS || IsT;
            public bool IsOptional => !Card.IsRequired;

            /// <summary>状态圆点/徽标刷子（绿发布·蓝已验证·琥珀采样完成·灰草稿·红过期）</summary>
            public Brush StateBrush
            {
                get
                {
                    switch (Card.State)
                    {
                        case CalibrationArtifactState.Published: return new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41));
                        case CalibrationArtifactState.Verified: return new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
                        case CalibrationArtifactState.SampleComplete: return new SolidColorBrush(Color.FromRgb(0x9C, 0x6B, 0x08));
                        case CalibrationArtifactState.Expired: return new SolidColorBrush(Color.FromRgb(0xA8, 0x00, 0x00));
                        default: return new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));
                    }
                }
            }
        }

        /// <summary>当前选中方案拆出的任务卡（H/e/t/s；2026-09-08 起 EyeInHand 偏心工具亦派生 t 卡走向导间接对针）</summary>
        public ObservableCollection<ArtifactTaskCardVm> DerivedCards { get; } = new ObservableCollection<ArtifactTaskCardVm>();

        public bool HasDerivedCards => DerivedCards.Count > 0;

        /// <summary>无可派生卡（占位提示可见）</summary>
        public bool HasNoDerivedCards => !HasDerivedCards;

        /// <summary>任务卡区标题（含方案名）</summary>
        public string CardWorkbenchTitle => SelectedCalibrationProfile == null
            ? "任务卡"
            : $"🗂 任务卡 · {SelectedCalibrationProfile.Name}";

        /// <summary>任务卡区引导文案（解释"为什么这里没有对针/为什么某卡置灰"）</summary>
        public string CardWorkbenchHint
        {
            get
            {
                var p = SelectedCalibrationProfile;
                if (p == null) return "在左侧选择标定方案查看其按物理量拆分的任务卡。";
                if (DerivedCards.Count == 0)
                {
                    return "该方案无可派生任务（棋盘/畸变占位不可执行；或旧档案类型未知且无矩阵）。";
                }
                string tNote = p.EyeMode == EyeMode.EyeInHand
                    ? "；EyeInHand 布局不派生对针 t（相机与吸嘴同体，图像对针不成立）"
                    : (p.Type == CalibrationType.PickPlaceHandEye
                        ? "；吸放式 H 真值已吸收偏距——对针 t 仅当已有结果时出现"
                        : string.Empty);
                return $"该方案拆 {DerivedCards.Count} 张任务卡，动作直接挂在卡上（引导/校验/对针/发布/重标），不再有全局平行按钮{tNote}。依赖缺位或已过期会置灰并给原因。";
            }
        }

        private void RebuildDerivedCards()
        {
            DerivedCards.Clear();
            var p = SelectedCalibrationProfile;
            if (p != null)
            {
                var cards = CalibrationCardDeriver.Derive(p, CalibrationProfiles.ToList());
                foreach (var c in cards)
                {
                    DerivedCards.Add(new ArtifactTaskCardVm(c));
                }
            }
            OnPropertyChanged(nameof(HasDerivedCards));
            OnPropertyChanged(nameof(HasNoDerivedCards));
            OnPropertyChanged(nameof(CardWorkbenchTitle));
            OnPropertyChanged(nameof(CardWorkbenchHint));
            // 卡命令 CanExecute 依赖派生卡状态（含依赖/过期放宽判定）——重建后必须刷新按钮可用态，
            // 否则"Expired 卡已放行重标"等新判定不会反映到已渲染按钮（IsEnabled 停留在上次缓存）。
            (RunWizardForCardCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RunVerifierForCardCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RunToolOffsetForCardCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PublishCardCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanAutoChain));
            (AutoChainCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        #endregion

        /// <summary>双滑台标定向导窗口单例（非模态；向导独占相机事件/取流状态，不允许开两个）</summary>

        #region 绑定属性

        public ObservableCollection<CalibrationProfile> CalibrationProfiles { get; }
            = new ObservableCollection<CalibrationProfile>();

        /// <summary>标定类型下拉选项：类型 + 短名 + 场景说明（切换/新建类型的统一数据源）。
        /// ⚠ 左侧"+新建"菜单与右侧类型下拉必须同源于 CalibrationTypeOptions（2026-09-04 曾因
        /// 新建菜单 XAML 硬编码与下拉分叉：新建侧缺吸放式、残留棋盘格；现已统一由本数组驱动）。</summary>
        public class CalibrationTypeOption
        {
            public CalibrationType Type { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            /// <summary>false = 开发中占位（TODO），仅展示不开放新建/切换</summary>
            public bool IsAvailable { get; set; } = true;
        }

        /// <summary>
        /// 全部标定类型（右侧 KPI 卡下拉 + 左侧新建菜单统一数据源）。
        /// 可用：九点手眼 / 九点+旋转(偏心) / 吸放式 Pick&Place / 像素当量 s（P4 开放：v2 s 会话已支持）。
        /// TODO 占位：棋盘格 2D、相机内参——尚无真实标定板检测（HalconWrapper.DetectCalibrationPoints 为空桩，
        /// 曾用硬编码伪点产出"假标定"入库）。TODO 项 IsAvailable=false，下拉灰显不可选、新建菜单禁用；
        /// 历史遗留方案仍可被 SyncTypeOptionToProfile 选中显示，打开向导由 OpenWizard/Planner 放行/拦截。
        /// </summary>
        public CalibrationTypeOption[] CalibrationTypeOptions { get; } = new[]
        {
            new CalibrationTypeOption { Type = CalibrationType.NinePointHandEye, DisplayName = "九点手眼", Description = "九点平移标定（眼在手/眼在手外）。适用于纯平移对位（如双滑台），吸嘴/工具与旋转轴同心或无需旋转的工位。" },
            new CalibrationTypeOption { Type = CalibrationType.HandEyeWithRotation, DisplayName = "九点+旋转(偏心)", Description = "多点仿射变换：9 点标定 + 旋转中心标定（U 轴旋转采样拟合圆心）。适用于吸嘴/工具与旋转轴不共轴的工位（如 ST002 Epson 双吸嘴，双吸嘴与 ZR 不同轴需偏心补偿）。" },
            new CalibrationTypeOption { Type = CalibrationType.PickPlaceHandEye, DisplayName = "吸放式 Pick&Place", Description = "行业标准吸放式标定：机械臂吸住工件→放到规划网格点→回固定拍照位拍照（9 点求 HomMat）；旋转段吸住转 U→放料→回拍→圆拟合旋转中心+自动导出工具偏心。工件随吸放移动、相机姿态恒定，比工件固定相机走位精度/光照更稳。" },
            new CalibrationTypeOption { Type = CalibrationType.Checkerboard2D, DisplayName = "棋盘格 2D（开发中）", IsAvailable = false, Description = "TODO：待实现真实标定板/圆点阵列角点检测（find_calib_object）与去畸变后再开放。当前禁止新建/切换，避免假标定数据入库。" },
            new CalibrationTypeOption { Type = CalibrationType.CameraLensDistortion, DisplayName = "相机内参（开发中）", IsAvailable = false, Description = "TODO：待实现 calibrate_cam 多姿态标定板内参/畸变标定后再开放。当前禁止新建/切换。" },
            new CalibrationTypeOption { Type = CalibrationType.PixelScale, DisplayName = "像素当量 s", IsAvailable = true, Description = "像素当量（mm/px）：v2 s 会话——已知标距/飞拍测距换算像素当量，独立产物/发布。适用于飞拍纠偏、纯当量换算的相机（如检测/测量类）。正式引导定位仍推荐 九点/九点+旋转/吸放式。" }
        };

        private CalibrationTypeOption _selectedProfileTypeOption;
        /// <summary>
        /// 当前方案的标定类型（KPI 卡下拉双向编辑）：
        /// 切换即持久化到仓库；已标定方案切换类型会重置为未标定（需重新执行向导）；
        /// 名字若是自动生成的旧类型名则同步更新，避免"九点手眼"名配"旋转"类型。
        /// 开发中占位类型（IsAvailable=false）禁止切换——选中即拦截并回滚。
        /// </summary>
        public CalibrationTypeOption SelectedProfileTypeOption
        {
            get => _selectedProfileTypeOption;
            set
            {
                if (!Set(ref _selectedProfileTypeOption, value) || value == null || SelectedCalibrationProfile == null) return;
                if (SelectedCalibrationProfile.Type == value.Type) return;
                if (!value.IsAvailable)
                {
                    MessageBox.Show($"「{value.DisplayName}」标定尚未实现（TODO），当前不可用。\n请选择：九点手眼 / 九点+旋转(偏心) / 吸放式 Pick&Place。",
                        "类型未实现", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SyncTypeOptionToProfile(); // 回滚到当前方案的实际类型
                    return;
                }
                ChangeProfileType(value.Type);
            }
        }

        private CalibrationProfile _selectedCalibrationProfile;
        public CalibrationProfile SelectedCalibrationProfile
        {
            get => _selectedCalibrationProfile;
            set
            {
                if (Set(ref _selectedCalibrationProfile, value))
                {
                    SyncTypeOptionToProfile();
                    OnPropertyChanged(nameof(NozzleKeyText));
                    OnPropertyChanged(nameof(ScopeTargetHint));
                    ExecuteTestMap();
                    (PublishEccCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (OpenVerifierCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (OpenToolOffsetCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    RebuildDerivedCards();
                }
            }
        }

        private double _testPixelX;
        public double TestPixelX
        {
            get => _testPixelX;
            set { if (Set(ref _testPixelX, value)) ExecuteTestMap(); }
        }

        private double _testPixelY;
        public double TestPixelY
        {
            get => _testPixelY;
            set { if (Set(ref _testPixelY, value)) ExecuteTestMap(); }
        }

        private string _testWorldResult = "X: 0.000, Y: 0.000";
        public string TestWorldResult
        {
            get => _testWorldResult;
            set => Set(ref _testWorldResult, value);
        }

        /// <summary>可选吸嘴/工具通道（双吸嘴与 R/U 不同轴工位需每吸嘴一套独立标定）</summary>
        public string[] NozzleKeyOptions => new[] { "1", "2", "3" };

        /// <summary>当前方案所属吸嘴/工具通道（1=单吸嘴默认；2/3=双/多吸嘴各自标定）。
        /// ⚠ 2026-09-06 语义修正：本下拉 = 【切到吸嘴N 的独立方案】，不再"把当前方案改名归属到吸嘴N"——
        /// 原实现会把吸嘴1 已标定的 H/e 数据静默改标签成吸嘴2（数据仍是吸嘴1 的，且不重建卡片，观感"切换无效"），
        /// 两吸嘴共用一条档案还会互相覆盖。每吸嘴=一条独立 CalibrationProfile（H/e/偏心各自一套）：
        ///   同工位已有 吸嘴N 方案 → 载入之（卡片/数值随选中重建）；
        ///   没有 → 确认后按当前方案骨架新建 吸嘴N 空方案（数据留空待标）。</summary>
        public string NozzleKeyText
        {
            get => SelectedCalibrationProfile?.NozzleKey ?? "1";
            set
            {
                var cur = SelectedCalibrationProfile;
                if (cur == null || string.IsNullOrWhiteSpace(value))
                {
                    return;
                }
                string nozzle = value.Trim();
                string old = string.IsNullOrEmpty(cur.NozzleKey) ? "1" : cur.NozzleKey;
                if (string.Equals(old, nozzle))
                {
                    return;
                }

                // 1) 同工位已存在目标吸嘴的独立方案 → 载入（各吸嘴数据独立，e 卡随选中重建即"变换"）
                if (!string.IsNullOrWhiteSpace(cur.BoundStationCode))
                {
                    var existing = CalibrationProfiles.FirstOrDefault(x => x != cur
                        && string.Equals(x.BoundStationCode, cur.BoundStationCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(string.IsNullOrEmpty(x.NozzleKey) ? "1" : x.NozzleKey,
                                         nozzle, StringComparison.Ordinal));
                    if (existing != null)
                    {
                        SelectedCalibrationProfile = existing;
                        ScopeNoteText = $"已切到 吸嘴{nozzle} 的独立方案【{existing.Name}】——每吸嘴 H/e 独立，请分别执行向导与发布。";
                        OnPropertyChanged(nameof(NozzleKeyText));
                        return;
                    }
                }

                // 2) 该工位还没有 吸嘴N 方案 → 询问后按当前方案骨架新建空方案（杜绝把已标数据改标签成另一吸嘴）
                var ask = MessageBox.Show(
                    "当前工位还没有「吸嘴" + nozzle + "」的独立标定方案。\n\n" +
                    "是否按当前方案（" + GetTypeDisplayName(cur.Type) + " / " + cur.EyeMode + "）为 吸嘴" + nozzle + " 新建一条空方案？\n" +
                    "新建后请为 吸嘴" + nozzle + " 单独执行 H/e 向导并单独发布——每吸嘴的旋转中心/偏心/矩阵各自独立存放。",
                    "为吸嘴 " + nozzle + " 新建独立方案", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask != MessageBoxResult.Yes)
                {
                    OnPropertyChanged(nameof(NozzleKeyText)); // 下拉回滚到原吸嘴
                    return;
                }

                string stationCode = cur.BoundStationCode ?? string.Empty;
                string stationLabel = IsStationScope && !string.IsNullOrWhiteSpace(_scopeStationName)
                    ? _scopeStationName
                    : (string.IsNullOrWhiteSpace(stationCode) ? "标定" : stationCode);
                string baseName = GetDefaultNameForType(cur.Type, stationLabel);
                string newName = ApplyNozzleToAutoName(baseName, stationLabel, nozzle);
                var np = new CalibrationProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = newName,
                    Type = cur.Type,
                    EyeMode = cur.EyeMode,
                    NozzleKey = nozzle,
                    CameraId = string.IsNullOrWhiteSpace(cur.CameraId) ? "Cam_01" : cur.CameraId,
                    AxisId = cur.AxisId,
                    BoundStationCode = stationCode,
                    BoundDeviceId = cur.BoundDeviceId,
                    BindingInfo = cur.BindingInfo,
                    BindRotationAxisIndex = cur.BindRotationAxisIndex,
                    BindXAxisIndex = cur.BindXAxisIndex,
                    BindYAxisIndex = cur.BindYAxisIndex,
                    UpdatedAt = DateTime.Now
                };
                CalibrationProfiles.Add(np);
                if (IsStationScope && VisibleProfiles != null
                    && string.Equals(np.BoundStationCode, _scopeStationCode, StringComparison.OrdinalIgnoreCase))
                {
                    VisibleProfiles.Add(np);
                }
                SaveProfileToRepository(np);
                SelectedCalibrationProfile = np; // 触发 RebuildDerivedCards → 派生卡（含 e）整体切到吸嘴N
                ScopeNoteText = $"已为 吸嘴{nozzle} 新建独立方案【{newName}】——请单独执行 H/e 向导（数据与吸嘴{old} 独立）。";
                OnPropertyChanged(nameof(NozzleKeyText));
            }
        }

        /// <summary>自动生成名（"{站名}_xxx标定"）里插入/移除吸嘴号段</summary>
        private static string ApplyNozzleToAutoName(string name, string station, string nozzle)
        {
            if (string.IsNullOrEmpty(station) || !name.StartsWith(station + "_", StringComparison.Ordinal))
            {
                return name;
            }
            string rest = name.Substring(station.Length + 1);
            int seg = rest.IndexOf("吸嘴", StringComparison.Ordinal);
            if (seg >= 0)
            {
                int us = rest.IndexOf('_', seg);
                rest = us >= 0 ? rest.Substring(us + 1) : "";
            }
            return (nozzle == "1" || string.IsNullOrEmpty(nozzle))
                ? station + "_" + rest
                : station + "_吸嘴" + nozzle + "_" + rest;
        }

        private int _selectedScopeIndex;
        /// <summary>
        /// 发布目标域（2026-09-05 收敛）：0=工位级(Workstation，该工位所有配方共享) 1=特定配方(Recipe)。
        /// ⚠ 已移除旧"当前设备(Device)"档——标定矩阵不属于轴卡/相机等设备实例，其消费方是配方流内
        ///   CalibrationApply 节点；发布=把矩阵送到"工位级共享目录"或"某配方专属目录并回写其流节点路径"。
        /// </summary>
        public int SelectedScopeIndex
        {
            get => _selectedScopeIndex;
            set
            {
                if (Set(ref _selectedScopeIndex, value))
                {
                    OnPropertyChanged(nameof(IsRecipeScopeSelected));
                    if (value == 1) RefreshPublishRecipeOptions(); // 切到配方级时确保候选就绪
                }
            }
        }

        /// <summary>配方级发布时是否已选目标配方（按钮/提示用）</summary>
        public bool IsRecipeScopeSelected => SelectedScopeIndex == 1;

        /// <summary>配方级发布的候选配方列表（全库配方；显示名含 RecipeCode 便于区分同名）</summary>
        public ObservableCollection<RecipeModel> PublishRecipeOptions { get; } = new ObservableCollection<RecipeModel>();

        private RecipeModel _selectedPublishRecipe;
        /// <summary>配方级发布当前选中的目标配方</summary>
        public RecipeModel SelectedPublishRecipe
        {
            get => _selectedPublishRecipe;
            set
            {
                if (Set(ref _selectedPublishRecipe, value))
                {
                    OnPropertyChanged(nameof(ScopeTargetHint));
                }
            }
        }

        /// <summary>发布目标描述（工位目录 / 配方目录）——按钮副文案/提示</summary>
        public string ScopeTargetHint => SelectedScopeIndex == 1
            ? (SelectedPublishRecipe == null
                ? "配方级：请先选择目标配方"
                : $"配方级 → {SelectedPublishRecipe.RecipeName}（{SelectedPublishRecipe.RecipeCode}）")
            : (string.IsNullOrWhiteSpace(SelectedCalibrationProfile?.BoundStationCode)
                ? "工位级 → 未绑定工位（将落到 Default 目录，建议先定位工位）"
                : $"工位级 → 工位 {SelectedCalibrationProfile.BoundStationCode} 共享");

        private void RefreshPublishRecipeOptions()
        {
            try
            {
                var storage = RecipeStorageFactory.CreateRecipeStorageService();
                var all = storage.GetAllRecipes() ?? new List<RecipeModel>();
                var keep = SelectedPublishRecipe;
                PublishRecipeOptions.Clear();
                foreach (var r in all.OrderBy(r => r.RecipeName, StringComparer.OrdinalIgnoreCase))
                {
                    PublishRecipeOptions.Add(r);
                }
                SelectedPublishRecipe = keep != null && all.Any(r => r.RecipeId == keep.RecipeId)
                    ? all.First(r => r.RecipeId == keep.RecipeId)
                    : all.FirstOrDefault();
            }
            catch
            {
                PublishRecipeOptions.Clear();
                SelectedPublishRecipe = null;
            }
            OnPropertyChanged(nameof(ScopeTargetHint));
        }

        #endregion

        #region 命令

        public ICommand NewProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand OpenWizardCommand { get; }
        public ICommand OpenVerifierCommand { get; }
        public ICommand OpenToolOffsetCommand { get; }
        public ICommand SaveMatrixCommand { get; }
        public ICommand TestMapCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand ExportCommand { get; }

        /// <summary>把当前方案的工具偏心(ToolEcc) 发布为工位业务配置（MahjongDualNozzle 进程按 U 自动补偿落点）</summary>
        public ICommand PublishEccCommand { get; }

        // ---- P3 任务卡动作（卡内命令；CommandParameter = ArtifactTaskCardVm）----
        /// <summary>引导/重新标定（H/e/s 卡）</summary>
        public ICommand RunWizardForCardCommand { get; private set; }
        /// <summary>校验台（仅 H 卡、数据在）</summary>
        public ICommand RunVerifierForCardCommand { get; private set; }
        /// <summary>对针（仅 t 卡、EyeToHand；复用对针窗，首标/重标同入口）</summary>
        public ICommand RunToolOffsetForCardCommand { get; private set; }
        /// <summary>一键顺序标定：H→e→t 链式自动推进（2026-09-08；每段独立向导，段间弹窗确认可中止）</summary>
        public ICommand AutoChainCommand { get; private set; }
        /// <summary>发布/旁路发布（发布门禁 + 留痕，拍板④）</summary>
        public ICommand PublishCardCommand { get; private set; }

        private string _publishEccStatusText = "";
        /// <summary>偏心发布状态（最近一次操作结果，展示在标定页发布按钮旁）</summary>
        public string PublishEccStatusText
        {
            get => _publishEccStatusText;
            private set => Set(ref _publishEccStatusText, value);
        }

        #endregion

        public CalibrationManagerViewModel(ICalibrationService calibService = null)
        {
            _calibService = calibService ?? new CalibrationService();
            _profileRepository = StorageFactory.CreateCalibrationProfileRepository();
            _recipeStorage = RecipeStorageFactory.CreateRecipeStorageService();

            NewProfileCommand = new RelayCommand(p =>
            {
                CalibrationType type = CalibrationType.NinePointHandEye;
                if (p is CalibrationType t)
                {
                    type = t;
                }
                CreateNewProfile(type);
            });

            DeleteProfileCommand = new RelayCommand(_ => DeleteSelectedProfile(), _ => SelectedCalibrationProfile != null);
            OpenWizardCommand = new RelayCommand(_ => OpenWizard());
            OpenVerifierCommand = new RelayCommand(_ => OpenVerifier(), _ => CanOpenVerifier());
            OpenToolOffsetCommand = new RelayCommand(_ => OpenToolOffset(), _ => CanOpenVerifier());
            SaveMatrixCommand = new RelayCommand(_ => SaveMatrix());
            TestMapCommand = new RelayCommand(_ => ExecuteTestMap());
            ImportCommand = new RelayCommand(_ => ImportMatrixFile(), _ => SelectedCalibrationProfile != null);
            ExportCommand = new RelayCommand(_ => ExportMatrixFile(), _ => SelectedCalibrationProfile != null);
            PublishEccCommand = new RelayCommand(_ => PublishEccToStation(), _ => CanPublishEcc());
            RunWizardForCardCommand = new RelayCommand(o => OpenWizardForCard(o as ArtifactTaskCardVm),
                o => CanOpenWizardForCard(o as ArtifactTaskCardVm));
            RunVerifierForCardCommand = new RelayCommand(o => RunVerifierForCard(o as ArtifactTaskCardVm),
                o => CanRunVerifierForCard(o as ArtifactTaskCardVm));
            RunToolOffsetForCardCommand = new RelayCommand(o => RunToolOffsetForCard(o as ArtifactTaskCardVm),
                o => CanRunToolOffsetForCard(o as ArtifactTaskCardVm));
            PublishCardCommand = new RelayCommand(o => PublishCard(o as ArtifactTaskCardVm),
                o => CanPublishCard(o as ArtifactTaskCardVm));
            AutoChainCommand = new RelayCommand(_ => StartAutoChain(), _ => CanAutoChain);
            BackToGlobalCommand = new RelayCommand(_ => EnterGlobalScope());
            CreateCandidatesCommand = new RelayCommand(_ => CreateSelectedCandidates(), _ => HasCheckedCandidates);
            DismissCandidatesCommand = new RelayCommand(_ =>
            {
                CandidatesVisible = false;
                ScopeNoteText = "已忽略批量创建建议 —— 可在左侧手工新建单个方案（自动归属当前工位）。";
            });

            LoadProfiles();
        }

        #region INavigationAware 接口实现

        public void OnNavigatedTo(object parameter)
        {
            // 缓存单例：每次进入先同步仓库（避免其它入口新建的方案在本页不可见/过期）
            LoadProfiles();

            if (parameter is StationNavigationContext ctx)
            {
                ApplyStationScope(ctx);
            }
            else if (parameter is string code && !string.IsNullOrWhiteSpace(code))
            {
                ApplyStationScope(new StationNavigationContext { StationCode = code, StationName = code });
            }
            else
            {
                EnterGlobalScope();
            }
        }

        public void OnNavigatedFrom()
        {
        }

        #endregion

        #region 工位定位与档案建议（2026-09-05，镜像模板工作台钉住交互 + 按需求档案建方案）

        /// <summary>进入"某工位"定位态：钉住 → 过滤列表 → 命中该工位方案则选中；无则按工位需求档案自动创建合适方案。</summary>
        private void ApplyStationScope(StationNavigationContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.StationCode))
            {
                EnterGlobalScope();
                return;
            }

            _scopeStationCode = context.StationCode;
            _scopeStationName = string.IsNullOrWhiteSpace(context.StationName) ? context.StationCode : context.StationName;
            ScopeNoteText = string.Empty;
            RaiseScopeChanged();

            RebuildVisibleProfiles();

            // 已有该工位方案：选中（多吸嘴时优先 吸嘴1/未标吸嘴 的通用方案），不弹候选
            var existing = VisibleProfiles
                .Where(p => string.Equals(p.BoundStationCode, _scopeStationCode, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => string.IsNullOrEmpty(p.NozzleKey) || p.NozzleKey == "1" ? 0 : 1)
                .ThenBy(p => p.Name)
                .FirstOrDefault();
            if (existing != null)
            {
                SelectedCalibrationProfile = existing;
                CandidatesVisible = false;
                return;
            }

            // 档案自洽校验（2026-09-05 P3：安装方式↔随动标志矛盾自动修正写回，
            // 杜绝"固定相机被按手眼走位式推导"的镜像/方向错标）
            var archive = ResolveStationArchive(_scopeStationCode);
            if (archive == null)
            {
                // 档案缺失：按旧启发兜底自动建（档案无法推导任务）
                var type = SuggestCalibrationTypeForStation(out string reason);
                CreateProfileForStation(context, type);
                ScopeNoteText = reason == null
                    ? $"工位尚无标定方案，已按默认创建「{GetTypeDisplayName(type)}」；可在上方标定类型下拉按需调整。"
                    : $"工位尚无标定方案，已按需求档案建议（{reason}）创建「{GetTypeDisplayName(type)}」；可在上方标定类型下拉按需调整。";
                return;
            }
            var sanity = StationProfileSanityCheck.ApplyAndPersist(archive);
            if (sanity.HasFixes)
            {
                ScopeNoteText = "档案自洽校验：已自动修正并写回 " + sanity.Fixes.Count + " 处矛盾（"
                                + string.Join("；", sanity.Fixes) + "）。";
            }
            else
            {
                ScopeNoteText = string.Empty;
            }

            // 档案 → 标定计划任务卡（H/e 拆分：相机级按槽共享、工具级按吸嘴独立；勾选主线必做批量创建）
            var plan = CalibrationPlanEngine.Build(archive);
            var executable = plan != null
                ? plan.Tasks.Where(t => t.IsExecutable).ToList()
                : new List<CalibrationTask>();
            if (executable.Count > 0)
            {
                BuildCandidates(archive);
                OnPropertyChanged(nameof(CandidatesSummary));
                return;
            }

            // 档案可推导但无几何标定需求（检测/测量/OCR 类相机仅需模板/当量）
            ScopeNoteText = "按档案推导：本工位无几何标定需求（检测/测量/OCR 类相机仅需模板/当量），无需创建标定方案。";
        }

        /// <summary>按档案标定计划构建任务卡清单（相机级/工具级拆分；EyeMode 随任务推导）</summary>
        private void BuildCandidates(StationProfile archive)
        {
            Candidates.Clear();
            var plan = CalibrationPlanEngine.Build(archive);
            var tasks = plan?.Tasks ?? new List<CalibrationTask>();
            foreach (var task in tasks)
            {
                if (!task.IsExecutable) continue; // 畸变前置预留：不进任务卡
                string slotKey = string.IsNullOrWhiteSpace(task.SlotKey) ? "相机" : task.SlotKey;
                string chip = task.IsToolLevel ? "吸嘴" + task.NozzleKey : slotKey;
                string suggestion = task.Suggestion ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(task.Note))
                {
                    suggestion += "\n⚠ " + task.Note;
                }
                Candidates.Add(new CalibrationCandidate
                {
                    Task = task,
                    Kind = task.Kind,
                    KindBadge = CalibrationPlanEngine.KindBadge(task.Kind),
                    ChipText = chip,
                    SlotKey = task.SlotKey,
                    NozzleKey = task.NozzleKey ?? "1",
                    IsToolLevel = task.IsToolLevel,
                    TagText = task.DisplayName ?? slotKey,
                    Type = task.SuggestType,
                    TypeDisplay = GetTypeDisplayName(task.SuggestType),
                    EyeMode = task.SuggestEyeMode,
                    Suggestion = suggestion,
                    Note = task.Note,
                    IsSelected = !task.IsOptional // 主线必做默认勾选
                });
            }
            CandidatesVisible = Candidates.Count > 0;
            OnPropertyChanged(nameof(CandidatesSummary));
            CreateCandidatesCommand?.RaiseCanExecuteChanged();
        }

        /// <summary>按任务卡勾选批量创建（不覆盖已有方案；相机/轴绑定留待标定向导第1步）</summary>
        private void CreateSelectedCandidates()
        {
            var chosen = Candidates.Where(c => c.IsSelected).ToList();
            if (chosen.Count == 0)
            {
                ScopeNoteText = "未勾选任何任务 —— 未创建方案。";
                return;
            }

            var createdNames = new System.Collections.Generic.List<string>();
            CalibrationProfile first = null;
            foreach (var cand in chosen)
            {
                var t = cand.Task;
                bool toolLevel = t != null && t.IsToolLevel;
                string cameraId = string.IsNullOrWhiteSpace(cand.SlotKey) || cand.SlotKey == "相机"
                    ? "Cam_01"
                    : cand.SlotKey;
                string namePart = toolLevel
                    ? $"吸嘴{cand.NozzleKey}_{cand.TypeDisplay}"
                    : $"{cameraId}_{cand.TypeDisplay}";
                var np = new CalibrationProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = $"{_scopeStationName}_{namePart}",
                    Type = cand.Type,
                    EyeMode = cand.EyeMode,
                    NozzleKey = cand.NozzleKey ?? "1", // 双吸嘴：每吸嘴独立旋转/偏心标定
                    CameraId = cameraId,               // 槽位占位；标定向导第1步按实际设备覆盖
                    AxisId = "Axis_X",
                    BoundStationCode = _scopeStationCode,
                    BindingInfo = $"工位: {_scopeStationName} ({_scopeStationCode}) · {cand.TagText}"
                                  + (toolLevel ? $" · 吸嘴{cand.NozzleKey}" : string.Empty),
                    UpdatedAt = DateTime.Now
                };
                CalibrationProfiles.Add(np);
                VisibleProfiles.Add(np);
                SaveProfileToRepository(np);
                createdNames.Add(cand.ChipText + "(" + cand.TypeDisplay + ")");
                first = first ?? np;
            }

            if (first != null) SelectedCalibrationProfile = first;
            CandidatesVisible = false;
            ScopeNoteText = $"已按档案标定计划创建 {createdNames.Count} 条方案（{string.Join("、", createdNames)}）。\n"
                            + "建议执行顺序：先完成相机 H（九点/吸放式），再做工具偏心 e（旋转段依赖 H 提供坐标系）；"
                            + "点选左侧方案 → 右上「开始标定」进入向导绑定相机/轴。";
        }

        /// <summary>退出定位态回全库浏览</summary>
        private void EnterGlobalScope()
        {
            _scopeStationCode = string.Empty;
            _scopeStationName = string.Empty;
            ScopeNoteText = string.Empty;
            CandidatesVisible = false;
            Candidates.Clear();
            RaiseScopeChanged();
            RebuildVisibleProfiles();
        }

        private void RaiseScopeChanged()
        {
            OnPropertyChanged(nameof(IsStationScope));
            OnPropertyChanged(nameof(ScopeChipText));
            OnPropertyChanged(nameof(ScopeHintText));
        }

        /// <summary>按当前钉住工位的需求档案建议标定类型（档案派生建议优先；v1 启发式，标定方案集 P 段再深化）。</summary>
        private CalibrationType SuggestCalibrationTypeForStation(out string reason)
        {
            reason = null;
            try
            {
                var profile = ResolveStationArchive(_scopeStationCode);
                var req = profile?.Requirement;
                if (req == null) return CalibrationType.NinePointHandEye; // 档案缺失 / 问卷未填

                string sug = profile.CalibrationSuggestion ?? string.Empty;
                if (sug.Contains("吸放"))
                {
                    reason = "档案建议吸放式作业";
                    return CalibrationType.PickPlaceHandEye;
                }

                bool eyeInHand = string.Equals(req.CameraMount, "眼在手上", StringComparison.Ordinal);
                bool rotateNeed = req.ConcentricWithRotationAxis == false;
                bool multiTool = !string.IsNullOrWhiteSpace(req.ToolHeadCount) && req.ToolHeadCount != "1";

                if (rotateNeed)
                {
                    reason = eyeInHand ? "相机随动 + 工具与旋转轴偏心（需旋转中心标定）"
                                       : "工具与旋转轴偏心（需旋转中心标定）";
                    return CalibrationType.HandEyeWithRotation;
                }
                if (eyeInHand && multiTool)
                {
                    reason = "相机随动 + 多吸嘴/工具头（需旋转中心标定）";
                    return CalibrationType.HandEyeWithRotation;
                }
                reason = eyeInHand ? "眼在手上（随动相机，纯平移对位）" : "眼在手外（固定相机，像素↔机械平面映射）";
                return CalibrationType.NinePointHandEye;
            }
            catch
            {
                return CalibrationType.NinePointHandEye;
            }
        }

        /// <summary>按工位代码取需求档案（仓库按 StationId 命名检索；兼容 StationCode 字段匹配）</summary>
        private static StationProfile ResolveStationArchive(string stationCode)
        {
            if (string.IsNullOrWhiteSpace(stationCode)) return null;
            try
            {
                var repo = new StationProfileRepository();
                return repo.GetByStationId(stationCode)
                       ?? repo.ListAll().FirstOrDefault(p =>
                           string.Equals(p.StationCode, stationCode, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>为钉住工位创建并持久化一个新方案（名称/归属/建议类型自动填）</summary>
        private CalibrationProfile CreateProfileForStation(StationNavigationContext context, CalibrationType type)
        {
            var newProfile = new CalibrationProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = GetDefaultNameForType(type, _scopeStationName),
                Type = type,
                CameraId = "Cam_01",
                AxisId = "Axis_X",
                BoundStationCode = context.StationCode,
                BoundDeviceId = context.DeviceId,
                BindingInfo = $"工位: {_scopeStationName} ({context.StationCode})",
                UpdatedAt = DateTime.Now
            };
            CalibrationProfiles.Add(newProfile);
            SelectedCalibrationProfile = newProfile;
            VisibleProfiles.Add(newProfile);
            SaveProfileToRepository(newProfile);
            return newProfile;
        }

        /// <summary>主集变化后重建左侧可见列表（定位态只展示该工位方案）。选中项不在可见集时落到可见集首选。</summary>
        private void RebuildVisibleProfiles()
        {
            VisibleProfiles.Clear();
            var source = IsStationScope
                ? CalibrationProfiles.Where(p => string.Equals(p.BoundStationCode, _scopeStationCode, StringComparison.OrdinalIgnoreCase))
                : CalibrationProfiles;
            foreach (var p in source) VisibleProfiles.Add(p);

            if (SelectedCalibrationProfile == null || !VisibleProfiles.Contains(SelectedCalibrationProfile))
            {
                SelectedCalibrationProfile = VisibleProfiles.FirstOrDefault();
            }
            else
            {
                OnPropertyChanged(nameof(SelectedCalibrationProfile));
            }
        }

        #endregion

        private void LoadProfiles()
        {
            CalibrationProfiles.Clear();

            try
            {
                foreach (var po in _profileRepository.GetAll().OrderBy(x => x.ProfileName))
                {
                    if (po.Model != null)
                    {
                        CalibrationProfiles.Add(po.Model);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载标定方案失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            if (CalibrationProfiles.Count == 0)
            {
                CalibrationProfiles.Add(new CalibrationProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "工位1_Top相机九点标定",
                    Type = CalibrationType.NinePointHandEye,
                    BindingInfo = "工位: ST_01 / 平台1",
                    IsCalibrated = false
                });
            }

            SelectedCalibrationProfile = CalibrationProfiles.FirstOrDefault();
            RebuildVisibleProfiles();
            RefreshPublishRecipeOptions();
        }

        /// <summary>
        /// 新建标定方案（支持传入标定类型）
        /// </summary>
        private void CreateNewProfile(CalibrationType selectedType = CalibrationType.NinePointHandEye)
        {
            var opt = CalibrationTypeOptions.FirstOrDefault(o => o.Type == selectedType);
            if (opt != null && !opt.IsAvailable)
            {
                MessageBox.Show($"「{opt.DisplayName}」标定尚未实现（TODO），当前不可新建。\n请选择：九点手眼 / 九点+旋转(偏心) / 吸放式 Pick&Place。",
                    "类型未实现", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 定位态：新方案自动归属当前钉住工位（名称用工位名打头）；全库态维持旧逻辑
            string stationName;
            if (IsStationScope)
            {
                stationName = _scopeStationName;
            }
            else
            {
                stationName = !string.IsNullOrWhiteSpace(SelectedCalibrationProfile?.BoundStationCode)
                    ? SelectedCalibrationProfile.BoundStationCode
                    : "ST_01";
            }

            var newProfile = new CalibrationProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = GetDefaultNameForType(selectedType, stationName),
                Type = selectedType,
                CameraId = "Cam_01",
                AxisId = "Axis_X",
                UpdatedAt = DateTime.Now
            };

            if (IsStationScope)
            {
                newProfile.BoundStationCode = _scopeStationCode;
                newProfile.BoundDeviceId = SelectedCalibrationProfile?.BoundDeviceId;
                newProfile.BindingInfo = $"工位: {_scopeStationName} ({_scopeStationCode})";
            }

            CalibrationProfiles.Add(newProfile);
            SelectedCalibrationProfile = newProfile;
            VisibleProfiles.Add(newProfile);
            SaveProfileToRepository(newProfile);
        }

        /// <summary>选中方案变化时同步下拉选中项（直接赋值字段，不触发切换逻辑）</summary>
        private void SyncTypeOptionToProfile()
        {
            var type = SelectedCalibrationProfile?.Type ?? CalibrationType.NinePointHandEye;
            _selectedProfileTypeOption = CalibrationTypeOptions.FirstOrDefault(o => o.Type == type);
            OnPropertyChanged(nameof(SelectedProfileTypeOption));
            (PublishEccCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 切换当前方案的标定类型（2026-09-03，工位跳转默认九点手眼后无法改类型的补口）：
        /// ST002（Epson 双吸嘴与 ZR 不共轴）需要 HandEyeWithRotation = 9 点仿射 + 旋转中心标定。
        /// 切换后持久化；原标定结果标记未标定需重跑向导。
        /// </summary>
        private void ChangeProfileType(CalibrationType newType)
        {
            var profile = SelectedCalibrationProfile;
            if (profile == null) return;

            var opt = CalibrationTypeOptions.FirstOrDefault(o => o.Type == newType);
            if (opt != null && !opt.IsAvailable)
            {
                MessageBox.Show($"「{opt.DisplayName}」标定尚未实现（TODO），当前不可切换。\n请选择：九点手眼 / 九点+旋转(偏心) / 吸放式 Pick&Place。",
                    "类型未实现", MessageBoxButton.OK, MessageBoxImage.Warning);
                SyncTypeOptionToProfile();
                return;
            }

            profile.Type = newType;

            // 原标定结果不再适用 → 标记未标定（列表红点），提示重新执行
            if (profile.IsCalibrated)
            {
                profile.IsCalibrated = false;
                MessageBox.Show(
                    $"标定类型已切换为「{GetTypeDisplayName(newType)}」。\n原标定结果已标记为未标定，请重新运行标定向导完成标定。",
                    "类型变更", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            // 名字是自动生成的旧类型名 → 跟随更新（保留中文站名前缀，如 "EPSON工作台"）
            if (IsAutoGeneratedName(profile))
            {
                string prefix = StripAutoSuffix(profile.Name);
                profile.Name = GetDefaultNameForType(newType, prefix);
            }

            SaveProfileToRepository(profile);
            RefreshProfileListItem(profile);
            OnPropertyChanged(nameof(SelectedCalibrationProfile));
        }

        /// <summary>发布条件：选中吸放式方案、已绑工位、吸嘴号 1/2，且已有【真吸嘴偏心 e】或【物理对针 p_tip】至少其一。
        /// 双吸嘴：按 NozzleKey(1/2) 发布到对应吸嘴字段；Nozzle3+ 尚无业务进程字段。</summary>
        /// <summary>
        /// 发布几何源判定（2026-09-08 定案：只发布【真吸嘴偏心 e】，旧 TCO 字段一律清零）：
        ///   · e = ToolOffsetPureWx/Wy = O − H(p_tip)（O=旋转中心三点定圆，p_tip=物理对针像素）
        ///       → 发布到 Nozzle{N}EccX/Y；★ 不需要同心短杆；★ ToolEccW（偏心延伸杆）是杆末端偏心，不是 e。
        ///   · 同时发布 O→RotCenterWx/Wy、拍照基准位→PhotoBaseX/Y、相机安装方式→CameraMountEih、U0→ToolAlignU。
        /// 生产引擎消费（唯一真源 CalibrationGeometry，与校验台同一口径）：
        ///   X_obj = P_photo + O − H(u)（EIH）/ H(u)（ETH）；
        ///   C     = X_obj − R(姿态U − U0)·e（吸取 WorkU，放料 U_place，同一式）。
        /// </summary>
        private bool HasPublishableGeometry(out bool hasTco, out bool hasEcc)
        {
            hasTco = false;
            hasEcc = false;
            var p = SelectedCalibrationProfile;
            if (p == null) return false;
            // 2026-09-08 定案：权威量是真吸嘴偏心 e（ToolOffsetPureW = O − H(p_tip)）；
            // hasTco 保留语义=已做过物理对针（有 p_tip，可作 U≈U0 退路）
            hasEcc = p.IsNozzleEccCalibrated
                     && (Math.Abs(p.ToolOffsetPureWx) > 0.0001 || Math.Abs(p.ToolOffsetPureWy) > 0.0001);
            hasTco = p.IsToolOffsetCalibrated
                     && p.ToolOffsetMethod == ToolOffsetMethod.EyeInHandIndirect
                     && (Math.Abs(p.ToolOffsetWx) > 0.0001 || Math.Abs(p.ToolOffsetWy) > 0.0001);
            return hasTco || hasEcc;
        }

        private bool CanPublishEcc()
        {
            var p = SelectedCalibrationProfile;
            if (p == null || p.Type != CalibrationType.PickPlaceHandEye
                || string.IsNullOrWhiteSpace(p.BoundStationCode))
            {
                return false;
            }
            string nozzle = string.IsNullOrEmpty(p.NozzleKey) ? "1" : p.NozzleKey;
            if (nozzle != "1" && nozzle != "2")
            {
                return false; // 业务进程仅支持吸嘴1/2 字段
            }
            return HasPublishableGeometry(out _, out _);
        }

        /// <summary>
        /// 把当前方案的标定几何发布到工位业务配置（MahjongDualNozzle / VisionPickPlace 进程消费），
        /// 替代旧 NozzleToolOffset 人工示教。2026-09-08 定案：发布【三件套 + 相机安装方式】——
        ///   · e = ToolOffsetPureW（= O − H(p_tip)，真吸嘴偏心）→ Nozzle{N}EccX/Y；
        ///     缺 e 时从同工位其它标定档案继承（按 UpdatedAt 取最新），仍无则归零（防旧残留值污染）；
        ///   · O = ToolCenterW（旋转中心）→ RotCenterWx/Wy；缺则从同工位继承；
        ///   · 拍照基准位 → PhotoBaseX/Y；相机安装方式 → CameraMountEih；基准角 U0 → ToolAlignU = CalibU0；
        ///   · 旧 Nozzle{N}TcoX/Y 字段一律清零（v5 消费式已证伪，仿真误差 451mm）。
        /// 进程消费（唯一真源 CalibrationGeometry）：
        ///   X_obj = P_photo + O − H(u)（EIH）/ H(u)（ETH）；C = X_obj − R(姿态U − U0)·e。
        /// </summary>
        private void PublishEccToStation()
        {
            var p = SelectedCalibrationProfile;
            if (p == null)
            {
                return;
            }
            if (!CanPublishEcc())
            {
                PublishEccStatusText = "⚠ 无可发布几何源：需 吸放式(Pick&Place) 方案、已做 旋转中心标定(O) 或 物理对针(e)、绑定工位、吸嘴号 1/2。";
                return;
            }
            if (!HasPublishableGeometry(out bool hasTco, out bool hasEcc))
            {
                PublishEccStatusText = "⚠ 无可发布几何源（旋转中心 O 与吸嘴偏心 e 均无有效值）。";
                return;
            }

            string nozzle = string.IsNullOrEmpty(p.NozzleKey) ? "1" : p.NozzleKey;
            bool nozzle1 = nozzle == "1";
            string eccXField = nozzle1 ? "Nozzle1EccX" : "Nozzle2EccX";
            string eccYField = nozzle1 ? "Nozzle1EccY" : "Nozzle2EccY";
            string tcoXField = nozzle1 ? "Nozzle1TcoX" : "Nozzle2TcoX";
            string tcoYField = nozzle1 ? "Nozzle1TcoY" : "Nozzle2TcoY";
            try
            {
                var cfgSvc = new Grayson.Vision.WpfUI.Service.StationConfigService();
                var station = cfgSvc.LoadAllLines()
                    .SelectMany(l => l.Stations ?? new System.Collections.Generic.List<StationConfigModel>())
                    .FirstOrDefault(s => s.StationCode == p.BoundStationCode);
                if (station == null)
                {
                    PublishEccStatusText = $"⚠ 数据库中找不到工位 '{p.BoundStationCode}'（请先在工位管理中保存该工位）。";
                    return;
                }
                string procKey = station.ProcessKey ?? string.Empty;
                bool isDualNozzle = string.Equals(procKey, "MahjongDualNozzle", StringComparison.OrdinalIgnoreCase);
                bool isVpp = string.Equals(procKey, "VisionPickPlace", StringComparison.OrdinalIgnoreCase);
                if (!isDualNozzle && !isVpp)
                {
                    PublishEccStatusText = $"⚠ 工位 '{p.BoundStationCode}' 过程={procKey}，几何发布仅支持 MahjongDualNozzle / VisionPickPlace。";
                    return;
                }
                object codeDefaults = isDualNozzle ? (object)new MahjongDualNozzleConfig() : new VisionPickPlaceConfig();

                // ★ 同工位继承旋转中心 O：标定体系按"量"分档（H / e / t 可能各自建档），
                //   当前档案不一定带 O。缺 O 时到同工位其它档案里取（最近更新优先）并回填，
                //   让本档案自包含——校验台无仓储访问，靠这里收拢。
                string inheritNote = string.Empty;
                if (!p.HasRotationCenter && !string.IsNullOrWhiteSpace(p.BoundStationCode))
                {
                    var sib = CalibrationProfiles
                        .Where(x => x != null && !ReferenceEquals(x, p)
                                    && x.HasRotationCenter
                                    && string.Equals(x.BoundStationCode, p.BoundStationCode, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(x => x.UpdatedAt)
                        .FirstOrDefault();
                    if (sib != null)
                    {
                        p.ToolCenterWx = sib.ToolCenterWx;
                        p.ToolCenterWy = sib.ToolCenterWy;
                        p.HasRotationCenter = true;
                        inheritNote = $"\n· 旋转中心 O 从同工位档案「{sib.Name}」继承 = ({sib.ToolCenterWx:F3},{sib.ToolCenterWy:F3})";
                    }
                }

                var fields = new System.Collections.Generic.Dictionary<string, object>();

                // ★ 2026-09-08 定案：发布【真吸嘴偏心 e】= ToolOffsetPureW（= O − H(p_tip)），
                //   而不是 ToolEccW（偏心延伸杆 Mark 偏心 = −m，不是吸嘴偏心）。
                bool hasPureEcc = p.IsNozzleEccCalibrated
                                  && (Math.Abs(p.ToolOffsetPureWx) > 0.0001 || Math.Abs(p.ToolOffsetPureWy) > 0.0001);
                if (!hasPureEcc && !string.IsNullOrWhiteSpace(p.BoundStationCode))
                {
                    // 同理：真吸嘴偏心 e 也可能在同工位的另一份档案里（老档案升级场景）
                    var sibE = CalibrationProfiles
                        .Where(x => x != null && !ReferenceEquals(x, p)
                                    && x.IsNozzleEccCalibrated
                                    && string.Equals(x.BoundStationCode, p.BoundStationCode, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(x => x.UpdatedAt)
                        .FirstOrDefault();
                    if (sibE != null)
                    {
                        p.ToolOffsetPureWx = sibE.ToolOffsetPureWx;
                        p.ToolOffsetPureWy = sibE.ToolOffsetPureWy;
                        p.IsNozzleEccCalibrated = true;
                        hasPureEcc = true;
                        inheritNote += $"\n· 真吸嘴偏心 e 从同工位档案「{sibE.Name}」继承 = ({sibE.ToolOffsetPureWx:F3},{sibE.ToolOffsetPureWy:F3})";
                    }
                }
                if (hasPureEcc)
                {
                    fields[eccXField] = (float)p.ToolOffsetPureWx;
                    fields[eccYField] = (float)p.ToolOffsetPureWy;
                }
                else
                {
                    fields[eccXField] = 0f; // 无真 e → 归零，绝不用 ToolEccW(杆偏心)顶替
                    fields[eccYField] = 0f;
                }
                // 旧 TCO 字段已随定案式废弃（消费端不再使用）→ 一律清零，防残留值干扰
                fields[tcoXField] = 0f;
                fields[tcoYField] = 0f;
                fields["ToolAlignU"] = (float)(p.CalibU0 ?? 0.0);   // e 的参考角 U0
                // EIH 附加量：旋转中心 O + 拍照基准位（仅 CameraMountEih=true 时参与）
                if (p.HasRotationCenter)
                {
                    fields["RotCenterWx"] = (float)p.ToolCenterWx;
                    fields["RotCenterWy"] = (float)p.ToolCenterWy;
                    fields["PhotoBaseX"] = (float)p.BasePosX;
                    fields["PhotoBaseY"] = (float)p.BasePosY;
                    fields["CameraMountEih"] = true;
                }
                else
                {
                    fields["CameraMountEih"] = false; // 无 O → 按固定相机(ETH)语义 X_obj=H(u)
                }
                fields["CalibPlaceU"] = (float)p.PickBaseU;
                fields["TeachMode"] = false;

                var json = ProcessConfigOverlay.SetFields(station.ProcessConfigJson, fields, codeDefaults);
                station.ProcessConfigJson = json;
                if (!cfgSvc.SaveStation(station))
                {
                    PublishEccStatusText = "⚠ 工位配置保存失败（数据库写入异常），未变更。";
                    return;
                }

                // 热更新运行中 Worker（仿示教面板机制；运行中只保存不重挂，下次启动生效）
                string hotMsg = "";
                try
                {
                    if (App.StationHostRuntime is Grayson.Vision.Core.Station.StationHostRuntime runtime)
                    {
                        var worker = runtime.GetStationWorker(station.StationCode);
                        if (worker != null)
                        {
                            worker.DesiredProcessConfigJson = json;
                            var st = worker.State.ToString();
                            if (st == "Running" || st == "Paused")
                            {
                                hotMsg = "（工位运行中，配置已保存，停止后再次启动生效）";
                            }
                            else
                            {
                                worker.DetachProcess();
                                worker.EnsureProcessAttached();
                                hotMsg = "（已热更新运行中 Worker）";
                            }
                        }
                    }
                }
                catch (Exception hotEx)
                {
                    hotMsg = $"（热更新失败：{hotEx.Message}；配置已保存，重启后生效）";
                }

                // 2026-09-08 定案：发布的三件套 = e（真吸嘴偏心）+ O（旋转中心）+ U0；旧 TCO 字段已清零。
                string tcoLine = hasPureEcc
                    ? $"吸嘴{nozzle} 真吸嘴偏心 e = ({p.ToolOffsetPureWx:F3}, {p.ToolOffsetPureWy:F3}) mm（= O − H(p_tip)，物理对针）→ {eccXField}/{eccYField}\n"
                    : $"⚠ 本次未发布真吸嘴偏心 e（档案无对针结算结果）：Ecc 字段已归零。请完成【旋转中心标定 → 物理对针】后重新发布；U≈U0 时仍可走差分退路，转 U 作业会不准。\n";
                string eccNote = p.HasRotationCenter
                    ? $"旋转中心 O = ({p.ToolCenterWx:F3}, {p.ToolCenterWy:F3})（三点定圆，半径已丢弃）"
                    : "⚠ 档案无旋转中心 O：按固定相机(ETH)语义发布（X_obj = H(u)）。若本工位是眼在手(EIH)，请先做旋转中心标定。";

                PublishEccStatusText = $"✅ 已发布 吸嘴{nozzle} 几何 e[{(hasPureEcc ? "有" : "无")}] O[{(p.HasRotationCenter ? "有" : "无")}] → {p.BoundStationCode} {hotMsg}";
                MessageBox.Show(
                    $"已将标定几何发布到工位 {p.BoundStationCode}：\n\n" +
                    tcoLine +
                    (hasEcc
                        ? $"吸嘴{nozzle} 真吸嘴偏心 e = ({p.ToolOffsetPureWx:F3}, {p.ToolOffsetPureWy:F3}) mm（U=U0 参考）\n"
                        : $"吸嘴{nozzle} 真吸嘴偏心 e = 未标（字段已归零）\n") +
                    $"{eccNote}\n" +
                    $"参考角 ToolAlignU(U0) = {(p.CalibU0 ?? 0.0):F1}°　" +
                    $"拍照基准位 = ({p.BasePosX:F3}, {p.BasePosY:F3})\n" +
                    $"{hotMsg}{inheritNote}\n\n" +
                    "进程消费（与校验台同一真源）：\n" +
                    "  特征位置 X_obj = 拍照机位 + 旋转中心O − H(像素)\n" +
                    "  走位命令 P_go  = X_obj − R(作业U − U0)·e\n" +
                    "可到工位工程工作台 S3 业务面板【示教模式/验证落点】实测确认。",
                    "发布标定几何到业务配置", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                PublishEccStatusText = $"❌ 发布失败: {ex.Message}";
                MessageBox.Show($"发布标定几何到业务配置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool IsAutoGeneratedName(CalibrationProfile p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Name)) return false;
            string n = p.Name;
            return n.Contains("九点手眼") || n.Contains("含旋转") || n.Contains("棋盘格")
                || n.Contains("畸变") || n.Contains("像素比例");
        }

        /// <summary>去掉名字里自动生成的后缀，保留站名前缀（"EPSON工作台_九点手眼标定" → "EPSON工作台"）</summary>
        private static string StripAutoSuffix(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name ?? "";
            string[] suffixes =
            {
                "_九点手眼标定", "_12/15点含旋转手眼标定", "_2D棋盘格标定",
                "_相机畸变内参标定", "_像素比例标定", "_标定方案"
            };
            foreach (var suf in suffixes)
            {
                if (name.EndsWith(suf, StringComparison.Ordinal))
                {
                    return name.Substring(0, name.Length - suf.Length);
                }
            }
            return name;
        }

        /// <summary>
        /// 右侧标题名称编辑（TextBox LostFocus 时调用）：把用户改后的名称持久化到仓库并刷新列表。
        /// </summary>
        public void PersistProfileName()
        {
            var profile = SelectedCalibrationProfile;
            if (profile == null || string.IsNullOrWhiteSpace(profile.Name)) return;
            SaveProfileToRepository(profile);
            RefreshProfileListItem(profile);
            OnPropertyChanged(nameof(SelectedCalibrationProfile));
        }

        private string GetTypeDisplayName(CalibrationType type)
        {
            var opt = CalibrationTypeOptions.FirstOrDefault(o => o.Type == type);
            return opt != null ? opt.DisplayName : type.ToString();
        }

        private string GetDefaultNameForType(CalibrationType type, string station)
        {
            switch (type)
            {
                case CalibrationType.NinePointHandEye:
                    return $"{station}_九点手眼标定";
                case CalibrationType.HandEyeWithRotation:
                    return $"{station}_12/15点含旋转手眼标定";
                case CalibrationType.PickPlaceHandEye:
                    return $"{station}_吸放式14点标定";
                case CalibrationType.Checkerboard2D:
                    return $"{station}_2D棋盘格标定";
                case CalibrationType.CameraLensDistortion:
                    return $"{station}_相机畸变内参标定";
                case CalibrationType.PixelScale:
                    return $"{station}_像素比例标定";
                default:
                    return $"{station}_标定方案";
            }
        }

        private void DeleteSelectedProfile()
        {
            if (SelectedCalibrationProfile == null) return;

            var result = MessageBox.Show(
                $"确定要删除标定方案【{SelectedCalibrationProfile.Name}】吗？\n删除后绑定该标定的业务节点可能无法正常工作！",
                "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var profileToRemove = SelectedCalibrationProfile;

                if (!string.IsNullOrWhiteSpace(profileToRemove.Id))
                {
                    _profileRepository.Delete(profileToRemove.Id);
                }

                CalibrationProfiles.Remove(profileToRemove);
                RebuildVisibleProfiles();
            }
        }

        /// <summary>可开校验台：已标定 + 非像素当量(无矩阵) + 有选择</summary>
        private bool CanOpenVerifier()
        {
            var p = SelectedCalibrationProfile;
            return p != null && p.IsCalibrated && p.Type != CalibrationType.PixelScale;
        }

        /// <summary>打开 P3 标定校验台（在线打点验收）。关闭后把校验记录写入 profile 并落库。</summary>
        /// <summary>打开 P4 对针补偿窗口。关闭后把 ToolOffset 结果保存到 profile 并刷新。</summary>
        private void OpenToolOffset()
        {
            var profile = SelectedCalibrationProfile;
            if (profile == null) return;
            try
            {
                var win = new CalibrationToolOffsetWindow(profile)
                {
                    Owner = Application.Current.MainWindow
                };
                win.ShowDialog();

                if (profile.IsToolOffsetCalibrated)
                {
                    SaveProfileToRepository(profile);
                    RebuildVisibleProfiles();
                    MessageBox.Show(
                        $"对针补偿已保存：ToolOffset = ({profile.ToolOffsetWx:F3}, {profile.ToolOffsetWy:F3}) mm\n\n" +
                        "发布后引导坐标 = 矩阵映射 + ToolOffset。建议到「🔍 标定校验台」做打点验收：图上点哪，工具头精确到哪。",
                        "对针补偿已保存", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开对针补偿失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 打开 P3 标定校验台（在线打点验收）。
        /// ★ 2026-09-06 非模态化（同标定向导样板）：Show() 打开 + Owner=主窗，
        ///   校验台与主界面（机械臂调试等页面/窗口）并行独立操作、互不阻塞；
        ///   窗口单例防重入（同一时间只允许一个校验台，避免重复点开共享同一相机/轴卡）。
        /// 关窗提交语义：VM 在关闭兜底自动生成待提交记录（存在已判定点未点保存时）→
        ///   窗口 Closed 事件统一执行提交链 CommitVerifierOnClosed（校验记录/吸嘴锚点落库）。
        /// 取消/右上角 X 直接关闭 → 无记录/锚点变更 → 提交链空转不落库。
        /// </summary>
        private void OpenVerifier()
        {
            var profile = SelectedCalibrationProfile;
            if (profile == null) return;
            try
            {
                // 非模态单例：已有一个校验台窗口 → 仅激活，不再新开（相机取流/运动轴被该窗口独占）
                if (_activeVerifierWindow != null)
                {
                    if (_activeVerifierWindow.IsVisible)
                    {
                        _activeVerifierWindow.Activate();
                    }
                    return;
                }

                var win = new CalibrationVerifierWindow(profile)
                {
                    Owner = Application.Current.MainWindow
                };
                _activeVerifierWindow = win;
                // 关窗即释放单例引用，并执行提交链（校验记录/吸嘴锚点 → profile → 落库）
                win.Closed += (s, e) =>
                {
                    _activeVerifierWindow = null;
                    CommitVerifierOnClosed(win, profile);
                };
                win.Show(); // 非模态：不阻塞主界面（机械臂调试等窗口可并行使用）
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开标定校验台失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 校验台窗口关闭后的提交链（由 OpenVerifier 的 Closed 事件调用）。
        /// 关窗会话变更收集：①校验记录 ②吸嘴对准锚点(NozzleAlign,2026-09-06 起校验台可现场示教)——
        /// 任一存在即落库。无任何变更（纯取消/浏览后关闭）则空转，不落库不弹窗。
        /// 提交链会读写页面状态并落库弹窗，故 marshal 回 UI 线程执行（与 CommitWizardSessionOnClosed 同款防线）。
        /// </summary>
        private void CommitVerifierOnClosed(CalibrationVerifierWindow win, CalibrationProfile profile)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => CommitVerifierOnClosed(win, profile));
                return;
            }

            try
            {
                var vm = win.VerifierVm;
                // 关窗会话变更收集：①校验记录 ②吸嘴对准锚点(NozzleAlign,R_n) ③吸嘴对准像素(ToolAlignPixel,
                // v3 差分式消费锚,2026-09-06 起校验台可现场标定)——任一存在即落库
                bool alignChanged = vm.NozzleAlignDirty;
                bool toolAlignChanged = vm.ToolAlignDirty;
                if (alignChanged)
                {
                    profile.NozzleAlignX = vm.NozzleAlignX;
                    profile.NozzleAlignY = vm.NozzleAlignY;
                    profile.NozzleAlignZ = vm.NozzleAlignZ; // 压住高度（2026-09-08：校验台三步预置同源记 Z）
                }
                if (vm.CameraMountDirty)
                {
                    profile.CameraMovesWithZ = vm.CameraMovesWithZ; // 相机安装特性（随 Z / 固定），独立提交
                    // 同一台物理相机 → 同工位所有未声明的方案都同步成同一口径（2026-09-09）
                    var sync = BroadcastCameraMovesWithZ(profile, vm.CameraMovesWithZ);
                    if (sync.Synced > 0)
                    {
                        PublishEccStatusText = $"📡『相机{(vm.CameraMovesWithZ == true ? "随 Z 升降" : "固定不随 Z")}』"
                            + $" 已同步到同工位 {sync.Synced} 个未声明的方案（统一 CalibZ 守护口径）。";
                    }
                    if (sync.Conflicts.Count > 0)
                    {
                        MessageBox.Show(
                            "同工位里有方案显式声明了【相反】的相机安装特性，本次没有覆盖它们：\n\n"
                            + string.Join("\n", sync.Conflicts.Select(n => " · " + n))
                            + $"\n\n现已声明：相机{(vm.CameraMovesWithZ == true ? "随 Z 升降（拍照须回 CalibZ）" : "固定不随 Z（成像与 Z 无关）")}"
                            + "\n\n同一台相机只能有一种口径，请把上面这些方案改成一致，否则 Z 守护结论会互相打架。",
                            "相机安装特性冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                // ToolAlignPixel 由 VM 直接写入 Profile(同一对象引用)，这里无需再拷，只需纳入"有变更→落库"条件
                if (vm.HasPendingRecord)
                {
                    if (profile.VerificationRecords == null)
                    {
                        profile.VerificationRecords = new System.Collections.Generic.List<CalibrationVerificationRecord>();
                    }
                    profile.VerificationRecords.Add(vm.PendingRecord);
                }
                if (alignChanged || toolAlignChanged || vm.CameraMountDirty || vm.HasPendingRecord)
                {
                    SaveProfileToRepository(profile);
                    RebuildVisibleProfiles();
                    // ★ 2026-09-06 非模态化：原模态下"卡重建"由调用点（发布门禁引导等）在 OpenVerifier
                    //   返回后执行；改 Show() 后 OpenVerifier 立即返回，重建时序断裂 → 统一挪到关窗提交链，
                    //   保证任意入口（独立按钮/任务卡/门禁引导）开校验台、关窗后任务卡 State/按钮可用态同步刷新
                    //   （RebuildDerivedCards 内部含 RaiseCanExecuteChanged×4）。
                    RebuildDerivedCards();
                }
                if (vm.HasPendingRecord)
                {
                    var rec = vm.PendingRecord;
                    string verdict = rec.PassRate >= 100
                        ? "全部通过 —— 该矩阵可放心发布使用。"
                        : rec.PassRate >= 80
                            ? "通过率良好，可发布；若追求更高精度建议重标或检查偏差最大点。"
                            : "通过率偏低 —— 建议回到标定向导重新标定（矩阵几何/采样可能有问题）。";
                    MessageBox.Show(
                        $"标定校验记录已保存：判定 {rec.Points.Count(p => p.IsVerdicted)} 点 / 通过 {rec.PassedCount} 点" +
                        $"(通过率 {rec.PassRate:F0}%)。\n\n{verdict}",
                        "校验记录已保存", MessageBoxButton.OK,
                        rec.PassRate >= 80 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("标定校验台提交失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// P2：profile → 本次向导会话 spec（旧"单量直判"泛化为段解析器）。
        /// 旋转混合档案（HandEyeWithRotation / PickPlaceHandEye 一窗含 H+e 两产物）v2 起分段独立标定：
        /// 多条会话时先弹"本次标哪段"（H 段矩阵 / e 段旋转偏心），消除一窗混合执行。
        /// 仅剩一条（九点 H / 像素当量 s / 吸放 H）时不打扰直接进入。
        /// 返回 null = 无可执行段（调用方维持原行为/提示）。
        /// </summary>
        private CalibrationTaskSpec SelectSessionFromPlans(CalibrationProfile profile)
        {
            var plans = CalibrationProfileSessionPlanner.PlanSessions(profile);
            if (plans == null || plans.Count == 0)
            {
                return null;
            }
            if (plans.Count == 1)
            {
                return plans[0];
            }

            // 多条（旋转混合 H+e；或 H+e+可选 t）→ 段选择。惯例排序：H 在前、e 次之、t 末位（可选）。
            // t(对针) 为可选项且保留旧"对针"按钮入口 → 不参与段选择弹窗（避免 YesNoCancel 表达不下第三项）
            var main = plans.Where(s => s.IsRequired).ToList();
            if (main.Count < 2)
            {
                return main.Count == 1 ? main[0] : null;
            }
            string list = string.Join("\n", main.Select((s, i) =>
                $"  {(i == 0 ? "①" : i == 1 ? "②" : "③")} {s.DisplayName} —— {FirstLineOf(s.Reason)}"));
            string hint = plans.Any(s => !s.IsRequired)
                ? "\n\n注：t(对针) 为可选项——若 H 采用吸放式(真值已吸收偏距)则无需执行；重标 t 请关闭本窗后使用『对针』入口。"
                : string.Empty;
            var pick = MessageBox.Show(
                "该方案为「旋转混合」档案（一窗含 H 矩阵与旋转偏心 e 两段产物），v2 起分段独立标定，不再一窗混合执行。\n\n" +
                "本次请选择要标定的段（需要两段都重标请分两次进入）：\n" + list + hint + "\n\n" +
                "[是] = 第①段（H 段）    [否] = 第②段（e 段）    [取消] = 返回",
                "选择本次标定段（StepDef 会话）",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (pick == MessageBoxResult.Yes) return main[0];
            if (pick == MessageBoxResult.No && main.Count >= 2) return main[1];
            return null;
        }

        private static string FirstLineOf(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            int cut = s.IndexOf('；');
            return cut > 0 ? s.Substring(0, cut + 1) : s;
        }

        private void OpenWizard()
        {
            if (SelectedCalibrationProfile == null) return;

            // ⚠ 2026-09-04 未实现类型入口拦截：棋盘格/相机内参尚无真实标定板角点检测（DetectCalibrationPoints 为空桩），
            //    历史遗留的这两种方案直接进向导会产出"硬编码假矩阵"或静默跑九点，一律拦截并引导改类型。
            if (SelectedCalibrationProfile.Type == CalibrationType.Checkerboard2D
                || SelectedCalibrationProfile.Type == CalibrationType.CameraLensDistortion)
            {
                MessageBox.Show(
                    "「棋盘格 2D / 相机内参」标定尚未实现（缺少真实标定板角点检测），已阻止打开向导，避免产出无效标定数据。\n\n" +
                    "请在上方『标定类型』下拉中把该方案切换为：九点手眼 / 九点+旋转(偏心) / 吸放式 Pick&Place，再重新执行标定。",
                    "类型未实现", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var spec = SelectSessionFromPlans(SelectedCalibrationProfile);
                if (spec == null)
                {
                    // 无可用段（Planner 空=占位类型兜底；棋盘/畸变已被上方拦截，理论不可达）
                    return;
                }
                LaunchWizardSession(spec);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开标定向导失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 以指定任务 spec 启动向导会话（旧引导入口与 P3 任务卡入口共用）。
        /// ★ 2026-09-06 非模态化：Show() 打开 + Owner=主窗，向导窗口与主界面（机械臂调试等页面/窗口）
        ///   并行独立操作、互不阻塞；窗口单例防重入（同一时间只允许一个向导，避免重复点开共享同一相机/轴卡）。
        /// 关窗保存语义（P2 分叉）：H 段落盘矩阵+IsCalibrated；s 只标 IsCalibrated；
        /// e/t 只写旋转/偏距字段（matrix 属 H 段），不污染"已标定"整条标志。
        /// 提交触发：VM 保存完成会先置 IsSessionCompleted 再 Close → 窗口 Closed 事件里执行落盘/回写；
        /// 取消/右上角 X 直接关闭 → IsSessionCompleted=false → 不提交（与旧 ShowDialog()!=true 语义一致）。
        /// </summary>
        private void LaunchWizardSession(CalibrationTaskSpec spec)
        {
            if (SelectedCalibrationProfile == null || spec == null) return;
            try
            {
                // 非模态单例：已有一个向导窗口 → 仅激活，不再新开（相机取流/运动轴被该窗口独占）
                if (_activeWizardWindow != null)
                {
                    if (_activeWizardWindow.IsVisible)
                    {
                        _activeWizardWindow.Activate();
                    }
                    return;
                }

                var win = new CalibrationWizardWindow(SelectedCalibrationProfile, spec)
                {
                    Owner = Application.Current.MainWindow
                };
                _activeWizardWindow = win;
                // 关窗即释放单例引用，并依据"是否完成保存"执行提交链（提交内容见 CommitWizardSessionOnClosed）
                win.Closed += (s, e) =>
                {
                    _activeWizardWindow = null;
                    CommitWizardSessionOnClosed(win, spec);
                };
                win.Show(); // 非模态：不阻塞主界面
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开标定向导失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== 链式自动推进 H→e→t（2026-09-08；可选·一键顺序） ====================
        // 语义：任务卡工作台「⚡ 一键顺序标定」→ 依依赖序(H→e→t/吸嘴序) 收集"必做+未发布+可开向导"的卡，
        // 第 1 段立即开窗，其余排队；每段在上一段「完成并保存」提交后弹窗确认再开下一段（选"否"=清队中止）。
        // 队列默认空 → 既有单卡点选行为零变化。
        private readonly Queue<CalibrationTaskSpec> _chainQueue = new Queue<CalibrationTaskSpec>();
        /// <summary>链启动时方案 Id：段间校验档案未切换，切换即清队中止</summary>
        private string _chainProfileId;

        /// <summary>一键顺序可用性：≥2 张可开向导、必做、未发布卡（H/e/t 组合）</summary>
        public bool CanAutoChain => BuildAutoChainSpecs().Count >= 2;

        /// <summary>依依赖序构建链（HandEye→ToolRotation→ToolOffset；同量按 NozzleKey 升序）</summary>
        private List<CalibrationTaskSpec> BuildAutoChainSpecs()
        {
            var list = new List<CalibrationTaskSpec>();
            if (SelectedCalibrationProfile == null || DerivedCards == null || DerivedCards.Count == 0) return list;
            var pool = DerivedCards
                .Where(c => c != null && c.IsRequired && c.State != CalibrationArtifactState.Published
                            && CanOpenWizardForCard(c))
                .ToList();
            if (pool.Count < 2) return list;
            int Rank(ArtifactTaskCardVm c)
            {
                switch (c.Card.Quantity)
                {
                    case CalibrationQuantity.HandEye: return 0;
                    case CalibrationQuantity.ToolRotation: return 1;
                    default: return 2; // ToolOffset（LensDistortion 已被 CanOpenWizardForCard 排除）
                }
            }
            foreach (var c in pool.OrderBy(Rank).ThenBy(c => c.Card.NozzleKey ?? "1", StringComparer.Ordinal))
            {
                var spec = ResolveSpecForCard(c);
                if (spec != null) list.Add(spec);
            }
            return list.Count >= 2 ? list : new List<CalibrationTaskSpec>();
        }

        /// <summary>一键顺序标定：清队→排队(除首段)→确认→启动第 1 段</summary>
        private void StartAutoChain()
        {
            var specs = BuildAutoChainSpecs();
            if (specs.Count < 2)
            {
                MessageBox.Show("当前方案没有可连做的必做任务（至少需 2 张 H/e/t 组合卡），或依赖未就绪。\n\n单段任务请直接用卡上按钮。",
                    "一键顺序标定", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _chainQueue.Clear();
            foreach (var s in specs.Skip(1)) _chainQueue.Enqueue(s);
            _chainProfileId = SelectedCalibrationProfile?.Id;
            string first = ChainSessionLabel(specs[0]);
            string rest = string.Join(" → ", specs.Skip(1).Select(ChainSessionLabel));
            var ask = MessageBox.Show(
                $"按序完成：{first} → {rest}\n\n即将开始第 1 段【{first}】。每段完成后会弹窗确认是否继续下一段（选“否”即中止，可稍后从任务卡手动进入）。\n\n现在开始吗？",
                "一键顺序标定", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (ask != MessageBoxResult.Yes)
            {
                _chainQueue.Clear();
                return;
            }
            LaunchWizardSession(specs[0]);
        }

        /// <summary>段序人读标签（H/e/t + 吸嘴号）</summary>
        private static string ChainSessionLabel(CalibrationTaskSpec s)
        {
            string q = s.Quantity == CalibrationQuantity.HandEye ? "手眼 H"
                : s.Quantity == CalibrationQuantity.ToolRotation ? "回转偏心 e"
                : s.Quantity == CalibrationQuantity.ToolOffset ? "对针 t"
                : s.Quantity == CalibrationQuantity.PixelScale ? "像素当量 s" : "段";
            string nz = string.IsNullOrWhiteSpace(s.NozzleKey) || s.NozzleKey == "1" ? "" : $"·吸嘴{s.NozzleKey}";
            return q + nz;
        }

        /// <summary>段间推进：提交成功后在队列非空时（Background 优先级，避开关窗级联）弹窗确认下一段</summary>
        private void TryAdvanceChain()
        {
            if (_chainQueue.Count == 0) return;
            // 档案一致性守卫：链启动后若切换了标定方案 → 清队中止，防下一段 spec 打到别的档案上
            if (SelectedCalibrationProfile == null || !string.Equals(SelectedCalibrationProfile.Id, _chainProfileId, StringComparison.Ordinal))
            {
                _chainQueue.Clear();
                _chainProfileId = null;
                return;
            }
            var next = _chainQueue.Dequeue();
            string nextLabel = ChainSessionLabel(next);
            string rest = _chainQueue.Count > 0
                ? "\n后续：" + string.Join(" → ", _chainQueue.Select(ChainSessionLabel))
                : string.Empty;
            var ask = MessageBox.Show(
                $"上一段已完成并保存。\n\n下一段【{nextLabel}】是否现在开始？{rest}\n\n[是] 立即打开下一段向导　[否] 中止（后续可从任务卡手动进入）",
                "顺序标定 · 下一段", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (ask != MessageBoxResult.Yes)
            {
                _chainQueue.Clear();
                return;
            }
            LaunchWizardSession(next);
        }

        /// <summary>
        /// 向导窗口关闭后的提交链（由 LaunchWizardSession 的 Closed 事件调用）。
        /// 仅当 VM 已"✔ 完成并保存"（IsSessionCompleted）才执行——取消/右上角 X 关闭不提交，与旧模态语义等价。
        /// </summary>
        private void CommitWizardSessionOnClosed(CalibrationWizardWindow win, CalibrationTaskSpec spec)
        {
            // ★ 2026-09-06 提交链加固：无论向导窗口由哪个线程触发关闭，整个提交/分发链
            //   一律 marshal 回 UI 线程执行。提交链会读写页面发布域状态（SelectedScopeIndex/
            //   SelectedPublishRecipe）、落盘、弹窗并可能调用 SaveMatrix——若从后台线程执行，
            //   会与 UI 线程上的配方列表刷新/页面重建产生 check-then-use 竞态（曾表现为
            //   SaveMatrix 内部守卫通过后 SelectedPublishRecipe 中途被置空 → NullReferenceException，
            //   栈: CommitWizardSessionOnClosed→SaveMatrix 第 1920 行）。
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => CommitWizardSessionOnClosed(win, spec));
                return;
            }

            if (!(win.DataContext is CalibrationWizardViewModel wizardVm) || !wizardVm.IsSessionCompleted)
            {
                return; // 未完成保存（取消/直接关窗）→ 不提交
            }

            try
            {
                SelectedCalibrationProfile = wizardVm.TargetProfile;
                SelectedCalibrationProfile.RmsError = wizardVm.CalculatedRms;

                // ★ P2 保存语义分叉：仅 H 段产出矩阵（落盘设备目录 + IsCalibrated）；
                //   e（旋转偏心）/ t（对针）段只写旋转/偏距字段，不碰矩阵与"已标定"整条标志，
                //   避免"e 会话关窗把没矩阵的 profile 标成已标定"的误导（矩阵仍属 H 段）。
                bool hSession = spec.Quantity == CalibrationQuantity.HandEye;
                bool sSession = spec.Quantity == CalibrationQuantity.PixelScale;
                if (sSession)
                {
                    // 像素当量标定无矩阵文件
                    SelectedCalibrationProfile.IsCalibrated = true;
                }
                else if (hSession)
                {
                    // ★ 2026-09-04 向导关闭即把矩阵落盘"工位级"持久目录（Recipes\Workstations\{工位}\Calib），
                    //   不再只停留在 %TEMP%\hommat_*.tup——杜绝"重启/清临时目录后矩阵文件丢失、
                    //   profile 却仍显示已标定"的空窗。工位级为该工位所有配方共享的归宿；
                    //   用户后续仍可按需点"保存并应用"发布到工位/特定配方目录。
                    //   2026-09-05：落盘目录由旧"设备级"(Recipes\Devices) 改为工位级——标定矩阵
                    //   不属于轴卡/相机等设备实例，发布目标是消费它的配方/工位。
                    string durable = PersistMatrixToStationScope(SelectedCalibrationProfile);
                    SelectedCalibrationProfile.HomMatFilePath = durable ?? wizardVm.OutputHomMatPath;
                    SelectedCalibrationProfile.IsCalibrated = true;
                    if (durable == null && !string.IsNullOrWhiteSpace(wizardVm.OutputHomMatPath))
                    {
                        MessageBox.Show(
                            "警告：标定矩阵自动落盘工位目录失败，当前仅保存于系统临时目录（重启后可能丢失）。\n" +
                            "请点击下方『保存并应用』按钮，把矩阵发布到 工位/特定配方 目录。",
                            "矩阵未持久化", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    // P4 快照对齐：向导自动发布标记的快照记录的是向导内落盘路径；若此处工位级
                    //   持久路径与之不同，需刷新 H 发布快照到最终路径，否则任务卡会误判"数据变更→Expired"。
                    if (HasPublishMarkerFor(SelectedCalibrationProfile, "H"))
                    {
                        string finalPath = durable ?? wizardVm.OutputHomMatPath;
                        if (!string.IsNullOrWhiteSpace(finalPath))
                        {
                            string cur = CalibrationCardDeriver.BuildSnapshotKey(SelectedCalibrationProfile, CalibrationQuantity.HandEye);
                            string finalSnap = "H|" + finalPath;
                            if (!string.Equals(cur, finalSnap, StringComparison.Ordinal))
                            {
                                CalibrationCardDeriver.AppendPublishMarker(SelectedCalibrationProfile,
                                    CalibrationQuantity.HandEye, false, "向导完成后刷新发布快照到工位级矩阵路径");
                            }
                        }
                    }
                }

                SaveProfileToRepository(SelectedCalibrationProfile);
                OnPropertyChanged(nameof(SelectedCalibrationProfile));
                RebuildDerivedCards();
                if (hSession)
                {
                    // 仅 H 段会话重标矩阵后做在线打点自检；e/t 段不动矩阵，跳过以免弹窗干扰
                    ExecuteTestMap();
                    // P4 分发追问：H 会话向导末步已自动发布（AppendPublishMarker）→ 询问是否立即分发矩阵
                    //   （分发会同步刷新 H 发布快照；若否，稍后可用『应用标定矩阵』/卡上发布动作再发）
                    var ask = MessageBox.Show(
                        "H 标定会话完成并已自动发布（留痕）。\n\n是否立即把矩阵分发到当前发布域（工位级共享 / 特定配方）？\n[是] = 按当前发布域分发  [否] = 稍后用『应用标定矩阵』",
                        "发布完成 · 分发矩阵", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask == MessageBoxResult.Yes)
                    {
                        SaveMatrix();
                    }
                }

                // —— 链式自动推进（2026-09-08）：本段完成提交后，队列非空则出队弹窗确认下一段 ——
                // 注：取消/右上角 X 关闭的会话 IsSessionCompleted=false → 上方已 return，链随之中止。
                if (_chainQueue.Count > 0)
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(TryAdvanceChain),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("向导结果提交失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== P3 任务卡动作 ====================

        /// <summary>解析卡对应会话 spec：优先 Planner（H/e/s/t 均覆盖）。
        /// t 卡分流（2026-09-08）：EyeInHand → 向导六步模板（间接对针）；EyeToHand → 对针窗不进向导。</summary>
        private CalibrationTaskSpec ResolveSpecForCard(ArtifactTaskCardVm vm)
        {
            var profile = SelectedCalibrationProfile;
            if (profile == null || vm == null) return null;
            if (vm.Card.Quantity == CalibrationQuantity.ToolOffset && vm.Card.Layout != EyeMode.EyeInHand)
            {
                return null; // EyeToHand t → 对针窗（图像对针，工具落点需固定相机直接观测）
            }

            var plans = CalibrationProfileSessionPlanner.PlanSessions(profile);
            if (plans != null)
            {
                foreach (var s in plans)
                {
                    if (s.Quantity != vm.Card.Quantity) continue;
                    bool slotMatch = s.SlotKey == null || vm.Card.SlotKey == null
                        || string.Equals(s.SlotKey, vm.Card.SlotKey, StringComparison.OrdinalIgnoreCase);
                    bool nzMatch = string.Equals(s.NozzleKey ?? "1", vm.Card.NozzleKey ?? "1", StringComparison.OrdinalIgnoreCase);
                    if (slotMatch && nzMatch) return s;
                }
            }
            // Planner 未命中（如单量 profile 的补标场景）→ 按卡语义现拼
            return new CalibrationTaskSpec
            {
                SpecId = vm.Card.ArtifactId,
                StationCode = profile.BoundStationCode,
                Quantity = vm.Card.Quantity,
                SlotKey = vm.Card.SlotKey,
                NozzleKey = vm.Card.NozzleKey ?? "1",
                Layout = vm.Card.Layout,
                PrimaryPath = vm.Card.PrimaryPath,
                DependentArtifactRef = vm.Card.DependentArtifactId,
                IsRequired = true,
                DisplayName = profile.Name,
                Reason = "从任务卡进入（" + vm.Card.QuantityText + "）"
            };
        }

        /// <summary>
        /// 可开卡向导：非畸变 且 依赖可配合重标。
        /// ★ 2026-09-06 修正两处误禁（"H 重标 → e 过期 → e 重标禁用"死锁）：
        ///   ① 移除 IsExpired 一刀切——Expired = 发布后数据变更（快照不一致），唯一出路就是
        ///      重标（或重发布），重标入口必须可用；② 依赖闸从"依赖卡非 Expired"放宽为
        ///      "依赖 H 矩阵数据在位"——H 卡 Expired(发布快照过期)只代表数据已变更待重发布，
        ///      矩阵文件仍在，此时正是让 e 用新 H 重标的最佳时机（详见 IsDependencyReadyForRerun）。
        /// ★ 2026-09-08：EyeInHand t 卡放行走向导（六步模板间接对针）；EyeToHand t 仍走对针窗(🎯)。
        /// </summary>
        private bool CanOpenWizardForCard(ArtifactTaskCardVm vm)
        {
            if (vm == null || SelectedCalibrationProfile == null) return false;
            if (vm.Card.Quantity == CalibrationQuantity.ToolOffset
                && vm.Card.Layout != EyeMode.EyeInHand) return false;
            if (vm.Card.Quantity == CalibrationQuantity.LensDistortion) return false;
            if (!string.IsNullOrWhiteSpace(vm.Card.DependentArtifactId) && !IsDependencyReadyForRerun(vm))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// 该卡重标所需依赖是否可用（2026-09-06：发布态 ≠ 可标定态，二者分离）：
        ///   依赖 H  Draft/未完成/矩阵被清 → 不可用（先完成 H 段）；
        ///   依赖 H  SampleComplete/Verified/Published → 可用；
        ///   依赖 H  Expired → 矩阵文件在位即视为可用（发布快照过期≠数据缺失；H 刚重标未重发布
        ///      正是需要 e 配合重标的时机），矩阵不在位才拦；
        ///   跨档案依赖（Derive 经 allProfiles 解析、本卡列表无对应卡）→ 沿用 Derive 的 DepOk 结论。
        /// </summary>
        private bool IsDependencyReadyForRerun(ArtifactTaskCardVm vm)
        {
            if (vm == null || string.IsNullOrWhiteSpace(vm.Card.DependentArtifactId)) return true;
            var dep = DerivedCards.FirstOrDefault(c => c != null
                && string.Equals(c.ArtifactId, vm.Card.DependentArtifactId, StringComparison.OrdinalIgnoreCase));
            if (dep == null)
            {
                return vm.Card.DepOk; // 跨档案依赖：Derive 已解析
            }
            switch (dep.State)
            {
                case CalibrationArtifactState.SampleComplete:
                case CalibrationArtifactState.Verified:
                case CalibrationArtifactState.Published:
                    return true;
                case CalibrationArtifactState.Expired:
                    // 依赖 H 过期 = 发布快照不一致（数据刚变更待重发布）；矩阵在位即可配合重标
                    var p = SelectedCalibrationProfile;
                    return p != null && !string.IsNullOrWhiteSpace(p.HomMatFilePath);
                default:
                    return false; // Draft / 其它：依赖 H 未完成
            }
        }

        private void OpenWizardForCard(ArtifactTaskCardVm vm)
        {
            if (!CanOpenWizardForCard(vm)) return;
            var spec = ResolveSpecForCard(vm);
            if (spec == null)
            {
                MessageBox.Show("未能解析该任务的可执行会话定义。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LaunchWizardSession(spec);
        }

        private bool CanRunVerifierForCard(ArtifactTaskCardVm vm)
        {
            // 校验台 = H 卡 + 矩阵数据在（IsCalibrated + 有矩阵文件）
            var p = SelectedCalibrationProfile;
            return vm != null && p != null
                   && vm.Card.Quantity == CalibrationQuantity.HandEye
                   && vm.Card.HasData
                   && !string.IsNullOrWhiteSpace(p.HomMatFilePath);
        }

        private void RunVerifierForCard(ArtifactTaskCardVm vm)
        {
            if (!CanRunVerifierForCard(vm)) return;
            OpenVerifier(); // 校验记录写入 SelectedCalibrationProfile.VerificationRecords 并落库
            RebuildDerivedCards();
        }

        private bool CanRunToolOffsetForCard(ArtifactTaskCardVm vm)
        {
            // 🎯 对针按钮仅服务 EyeToHand t（图像对针独立窗）；EyeInHand t 改走 🚀 向导六步模板（间接对针）
            return vm != null && SelectedCalibrationProfile != null
                   && vm.Card.Quantity == CalibrationQuantity.ToolOffset
                   && vm.Card.Layout == EyeMode.EyeToHand
                   && !vm.IsEyeInHandTExpired;
        }

        private void RunToolOffsetForCard(ArtifactTaskCardVm vm)
        {
            if (!CanRunToolOffsetForCard(vm)) return;
            OpenToolOffset(); // EyeToHand 图像对针：首标/重标同入口（复用对针窗，产品级 UI；EIH 间接对针在向导内）
            RebuildDerivedCards();
        }

        /// <summary>档案是否已有指定量的发布标记（P4：分发快照刷新仅在已发布量上执行，防止旧档案分发被误标 Published）</summary>
        private static bool HasPublishMarkerFor(CalibrationProfile p, string badge)
        {
            if (p == null || p.VerificationRecords == null) return false;
            string token = "q=" + badge;
            foreach (var v in p.VerificationRecords)
            {
                if (v == null) continue;
                bool kind = string.Equals(v.Kind, "Publish", StringComparison.Ordinal)
                            || string.Equals(v.Kind, "PublishBypass", StringComparison.Ordinal);
                if (!kind) continue;
                if (v.Note != null && v.Note.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private bool CanPublishCard(ArtifactTaskCardVm vm)
        {
            if (vm == null || SelectedCalibrationProfile == null) return false;
            if (vm.Card.Quantity == CalibrationQuantity.LensDistortion) return false;
            // 数据可用判定（2026-09-06 放宽 Expired）：Expired = 曾发布 + 当前数据已变更
            // （发布快照不一致），数据实体在位，状态机出路恰为「重标 或 重发布」。重标入口
            // （CanOpenWizardForCard）16:14 已放行；此处对称放行发布入口——否则 e 保存新结果后
            // （旧 q=e 发布标记 → 卡 Expired → HasData=false）【📌 发布】被一刀切禁用，
            // "重发布刷新快照"这第二条出路被锁死（即"e 保存后无法发布"）。发布动作会重写发布
            // 留痕 + 新快照 → 卡状态回 Published，闭环自愈。
            // Draft（从未采样/无数据）仍禁发；EyeInHand t 过期（对针物理不成立）仍禁发。
            bool dataReady = vm.Card.HasData
                             || (vm.Card.State == CalibrationArtifactState.Expired && !vm.IsEyeInHandTExpired);
            if (!dataReady) return false;
            // 依赖闸保留（设计口径 CalibrationWizardViewModel:4728）：e/t 发布须在依赖 H 已发布
            // （或已验证/采样完成）后执行，防"卡 Published 但依赖 ✗"矛盾编排；H 过期先重发布 H。
            if (!vm.Card.DepOk && !string.IsNullOrWhiteSpace(vm.Card.DependentArtifactId)) return false;
            return true;
        }

        /// <summary>
        /// 发布门禁 + 留痕（拍板④）：卡动作「发布」。
        ///  H：未过校验(SampleComplete) → 硬门禁（去校验台 / 「我知道风险」旁路留痕）；
        ///      已过校验(Verified) → 确认发布；已发布 → 重新发布覆盖留痕。
        ///  e/t/s：数据来自已完成的会话（向导末步体检/对针窗）→ 确认后直接发布留痕。
        /// 统一写 VerificationRecords(Kind=Publish|PublishBypass + q=&lt;量&gt;) 后落库并重建卡片。
        /// </summary>
        private void PublishCard(ArtifactTaskCardVm vm)
        {
            var profile = SelectedCalibrationProfile;
            if (!CanPublishCard(vm) || profile == null) return;
            var q = vm.Card.Quantity;
            string qBadge = vm.Card.QuantityBadge;
            bool isH = q == CalibrationQuantity.HandEye;
            bool bypass = false;
            string note = string.Empty;

            // 2026-09-06：Expired（非 EyeInHand t）与已发布同走"重新发布"确认——Expired 的语义
            // = 曾发布 + 数据已变更，重发布覆盖旧留痕并刷新快照即回到 Published（Expired 第二出路）。
            bool republish = vm.IsPublished
                             || (vm.Card.State == CalibrationArtifactState.Expired && !vm.IsEyeInHandTExpired);
            if (republish)
            {
                string prompt = vm.IsPublished
                    ? $"[{qBadge}] 已发布过。若该量结果刚重算（矩阵/偏心/对针已更新），可重新发布覆盖留痕。\n\n[是]=重新发布（覆盖旧发布记录）  [否]=取消"
                    : $"[{qBadge}] 数据已更新（与旧发布记录快照不一致，卡显示过期）。重新发布将覆盖旧留痕并把该量恢复为已发布。\n\n[是]=重新发布（覆盖旧发布记录）  [否]=取消";
                if (MessageBox.Show(prompt, "重新发布", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }
                note = vm.IsPublished ? "重新发布（覆盖旧留痕）" : "重新发布（数据已更新，覆盖旧留痕）";
            }
            else if (isH && vm.Card.State == CalibrationArtifactState.SampleComplete)
            {
                // —— H 硬门禁：未过残差体检/校验 ——
                var pick = MessageBox.Show(
                    $"H 卡尚未通过残差体检/在线校验（发布硬门禁）。\n\n[是] = 旁路发布（我知道风险，留痕）\n[否] = 先开校验台做验收（推荐）\n[取消] = 返回",
                    "发布门禁未过", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (pick == MessageBoxResult.Cancel) return;
                if (pick == MessageBoxResult.No)
                {
                    OpenVerifier();
                    RebuildDerivedCards();
                    return;
                }
                bypass = true;
                note = "未做残差体检/校验，用户确认风险旁路发布";
            }
            else
            {
                if (MessageBox.Show(
                        $"[{qBadge}] 发布（残差体检/数据已就绪）。\n\n[是]=发布并留痕  [否]=取消",
                        "发布确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }
                note = "发布门禁通过";
            }

            CalibrationCardDeriver.AppendPublishMarker(profile, q, bypass, note);
            SaveProfileToRepository(profile);
            RebuildDerivedCards();

            // P4 发布分发并入卡动作：发布成功后按量追问（H→矩阵作用域分发；e→工位偏心业务；t/s→已生效）
            string doneInfo = $"[{qBadge}] 已{(bypass ? "旁路" : "")}发布并留痕（{DateTime.Now:HH:mm:ss}）。"
                              + (bypass ? "\n⚠ 旁路发布仅本次放行，建议尽快补校验/残差体检。" : string.Empty);
            if (isH)
            {
                var ask = MessageBox.Show(doneInfo + "\n\n是否立即把矩阵分发到当前发布域（工位级共享 / 特定配方）？\n[是] = 按当前发布域分发  [否] = 稍后用『应用标定矩阵』",
                    "发布完成 · 分发矩阵", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask == MessageBoxResult.Yes)
                {
                    SaveMatrix(); // 分发后内部刷新 H 发布快照，避免"换目录误判 Expired"
                }
                return;
            }
            if (q == CalibrationQuantity.ToolRotation)
            {
                var ask = MessageBox.Show(doneInfo + "\n\n是否立即把旋转中心 O 发布到工位业务配置（吸放式 MahjongDualNozzle）？\n[是] = 发布（缺 e 时会从同工位其它档案继承）  [否] = 稍后用『发布偏心到业务配置』",
                    "发布完成 · 分发偏心", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask == MessageBoxResult.Yes)
                {
                    PublishEccToStation();
                }
                return;
            }
            if (q == CalibrationQuantity.ToolOffset)
            {
                // ★ 2026-09-08 定案：物理对针结算出【真吸嘴偏心 e = O − H(p_tip)】（U0 参考），
                //   配合旋转中心 O 才能算准落点 → 询问是否立即同步到工位业务配置（覆盖既有偏心值）
                if (profile.Type == CalibrationType.PickPlaceHandEye && CanPublishEcc()
                    && profile.ToolOffsetMethod == ToolOffsetMethod.EyeInHandIndirect)
                {
                    var ask = MessageBox.Show(doneInfo + "\n\n是否立即把【吸嘴偏心 e 与旋转中心 O】同步到工位业务配置（MahjongDualNozzle，覆盖既有偏心值）？\n[是] = 同步  [否] = 稍后用『发布偏心到业务配置』",
                        "发布完成 · 同步偏心", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask == MessageBoxResult.Yes)
                    {
                        PublishEccToStation();
                    }
                    return;
                }
                MessageBox.Show(doneInfo + "\n对针已随发布生效：消费时按 X_obj = P_photo + O − H(u)、P_go = X_obj − R(U−U0)·e。",
                    "发布完成", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MessageBox.Show(doneInfo + "\n像素当量 s 已随发布生效。", "发布完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 向导关窗后把矩阵从当前路径（通常为 %TEMP%）复制一份到"工位级"持久目录
        /// Recipes\Workstations\{工位码}\Calib（该工位所有配方共享的归宿；无绑定工位落 Default）。
        /// 2026-09-05：已移除旧"设备级"落盘（Recipes\Devices\{设备ID}）——标定矩阵不属于轴卡/相机等
        /// 设备实例，其持久归宿是"工位级共享目录"，再按需分发到具体配方。
        /// 幂等：源即目标时直接返回目标路径。失败返回 null（调用方保留原路径并告警）。
        /// </summary>
        private string PersistMatrixToStationScope(CalibrationProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.HomMatFilePath))
            {
                return null;
            }

            try
            {
                string station = string.IsNullOrWhiteSpace(profile.BoundStationCode)
                    ? "Default"
                    : profile.BoundStationCode;
                string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes", "Workstations", station, "Calib");
                Directory.CreateDirectory(targetDir);

                string safeName = SanitizeFileName(profile.Name);
                string targetFile = Path.Combine(targetDir, $"{safeName}_HandEye.tup");

                string sourceFile = profile.HomMatFilePath;
                if (string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
                {
                    return targetFile; // 已在目标位
                }

                var saveRes = _calibService.SaveHomMatFile(sourceFile, targetFile);
                if (!saveRes.Success)
                {
                    // 目标文件可能被在线校验/流程节点短暂读取占用——等待后重试一次
                    System.Threading.Thread.Sleep(300);
                    saveRes = _calibService.SaveHomMatFile(sourceFile, targetFile);
                }
                return saveRes.Success ? targetFile : null;
            }
            catch (Exception ex)
            {
                MessageBox.Show("矩阵自动落盘异常：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        /// <summary>文件名清洗：替换 Windows 非法文件名字符（防标定方案名含冒号/斜杠等导致落盘失败）</summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Calibration";
            }
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }


        private void ExecuteTestMap()
        {
            if (SelectedCalibrationProfile == null || string.IsNullOrEmpty(SelectedCalibrationProfile.HomMatFilePath))
            {
                TestWorldResult = "未选择有效标定文件";
                return;
            }

            var res = _calibService.MapPixelToWorld(SelectedCalibrationProfile.HomMatFilePath, TestPixelX, TestPixelY);
            if (res.Success)
            {
                TestWorldResult = $"X: {res.Data.WorldX:F3}, Y: {res.Data.WorldY:F3}";
            }
            else
            {
                TestWorldResult = "映射失败: " + res.Message;
            }
        }

        /// <summary>
        /// 按发布目标域应用矩阵（2026-09-05 收敛两档语义，方法名由 SaveToDevice 更名去歧义）：
        ///   0 工位级 → 复制到 Recipes\Workstations\{工位}\Calib（该工位所有配方共享，不写配方文件）；
        ///   1 特定配方 → 复制到 Recipes\{配方Code}\Calib，并回写该配方流内 CalibrationApply 节点
        ///                HomMatFilePath（运行时真正消费方；配方级必须选到目标配方，禁盲发）。
        /// 幂等：源即目标时跳过复制；分发后刷新 H 发布快照（防"换目录误判 Expired"）。
        /// </summary>
        private void SaveMatrix()
        {
            if (SelectedCalibrationProfile == null || string.IsNullOrEmpty(SelectedCalibrationProfile.HomMatFilePath))
            {
                MessageBox.Show("当前没有有效的标定矩阵可应用！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 配方级必须选到目标配方（否则无法回写消费节点，禁止盲发）
            if (SelectedScopeIndex == 1 && SelectedPublishRecipe == null)
            {
                MessageBox.Show("已选择「特定配方」发布域，但未选中目标配方。\n请在下拉框选择要把此矩阵应用到的配方。",
                                "请选择目标配方", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ★ 2026-09-06 提交链加固（两轮）：
            //   第一轮仅快照 scope/recipe——实测仍崩（SaveMatrix 收尾 scopeText 三元式，提交链
            //   1536 与任务卡发布 1732 两路同现 NullReferenceException，行号随版本 1920→1943），
            //   证明 收尾解引用 SelectedCalibrationProfile.BoundStationCode 时 profile 本身也会在
            //   分发中途被外部替换/置空（向导非模态期间主界面可交互、模态询问框嵌套消息泵重入、
            //   页面发布域下拉随配方库刷新重置等多重竞态叠加）。
            //   第二轮把 profile 一并快照：文件复制/留痕/配方回写/收尾消息全部只对点击瞬间的
            //   快照实例操作——外部代码无法令局部引用变空，本方法从此结构性免疫该类崩溃；
            //   即便分发中页面选中被清/换新，也按用户点击时的方案完整执行（语义正确）。
            CalibrationProfile profile = SelectedCalibrationProfile;
            int scopeIndex = SelectedScopeIndex;
            RecipeModel targetRecipe = SelectedPublishRecipe;

            string targetDir = GetScopeTargetDirectory(scopeIndex, targetRecipe, profile);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string targetFile = Path.Combine(targetDir, $"{SanitizeFileName(profile.Name)}_HandEye.tup");

            // ⚠ 关键修复 1：首次应用成功后 HomMatFilePath 已指向目标文件，
            // 重复点击会变成 File.Copy(A, A)——Windows 返回共享冲突，
            // 表现为"文件正由另一进程使用"（其实是自己复制自己）。这里做幂等处理。
            string sourceFile = profile.HomMatFilePath;
            bool copied = false;
            if (string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
            {
                copied = true; // 源即目标：文件已在位，无需复制
            }
            else
            {
                var saveRes = _calibService.SaveHomMatFile(sourceFile, targetFile);
                if (!saveRes.Success)
                {
                    // ⚠ 关键修复 2：目标文件可能正被在线校验/流程节点的 ReadTuple 短暂读取，
                    // 覆盖时偶发占用冲突——等待后重试一次
                    System.Threading.Thread.Sleep(300);
                    saveRes = _calibService.SaveHomMatFile(sourceFile, targetFile);
                }

                if (!saveRes.Success)
                {
                    MessageBox.Show("保存矩阵失败：" + saveRes.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                copied = true;
            }

            if (copied)
            {
                // 更新模型（CalibrationProfile 自带 INPC，属性赋值会触发子绑定刷新）
                profile.HomMatFilePath = targetFile;
                // P4 快照同步：分发会把 HomMatFilePath 切到发布域目录——若该 H 已有发布标记，
                //   必须刷新发布快照到新路径，否则任务卡会误判"发布后数据变更 → Expired"。
                //   仅对"已发布过的 H"刷新：旧档案（无标记）分发不得凭空产生发布记录。
                if (HasPublishMarkerFor(profile, "H"))
                {
                    CalibrationCardDeriver.AppendPublishMarker(profile,
                        CalibrationQuantity.HandEye, false, "矩阵分发到发布域目录，刷新发布快照");
                }
                profile.UpdatedAt = DateTime.Now;
                SaveProfileToRepository(profile);

                // 配方级：把矩阵路径写进目标配方（流内 CalibrationApply 节点 + 工艺参数标定路径），
                // 这才是运行时真正消费的位置——旧实现只写死字段 CalibrationDataPath（无消费方）。
                string recipeNote = string.Empty;
                if (scopeIndex == 1)
                {
                    recipeNote = SyncMatrixIntoRecipe(targetRecipe, profile, targetFile);
                }

                // 强制刷新所有 SelectedCalibrationProfile.* 绑定（KPI 卡片、标题等）
                OnPropertyChanged(nameof(SelectedCalibrationProfile));
                // 刷新左侧列表项（绿点/Tag 等以同一实例为源的绑定）
                RefreshProfileListItem(profile);
                // 立即用新矩阵路径重算在线校验结果
                ExecuteTestMap();

                // 2026-09-06 兜底：配方级引用若在分发途中被清（配方库刷新/删除等竞态），
                //   直接取 .RecipeName 会 NRE——改为安全取值并给出可操作提示（矩阵文件已复制，
                //   仅配方回写可能缺失）。★ 2026-09-06 快照化后此处仅在极端场景（入口快照时刻
                //   配方已被外部清除）命中，正常路径不再可达。
                bool recipeRefLost = scopeIndex == 1 && targetRecipe == null;
                string scopeText;
                if (recipeRefLost)
                {
                    recipeNote = string.IsNullOrEmpty(recipeNote)
                        ? "\n⚠ 矩阵已复制，但目标配方引用在分发过程中丢失（配方可能被刷新/删除）——请重新选择目标配方后再点一次『应用标定矩阵』以回写配方节点路径。"
                        : recipeNote + "\n⚠ 目标配方引用在分发过程中丢失，请确认配方节点路径已正确回写。";
                    scopeText = "特定配方【目标配方已被清除】";
                }
                else
                {
                    // 入口守卫已保证 profile 非空、scope==1 时 targetRecipe 非空（快照与守卫同一
                    // 线程同一瞬间读取）；此处 ?. / ?? / 局部 stationText 仅作终极防御——
                    // 即便未来守卫/快照关系被重构破坏，收尾消息也不可能再抛空引用。
                    string stationText = string.IsNullOrWhiteSpace(profile?.BoundStationCode)
                        ? "Default"
                        : profile.BoundStationCode;
                    scopeText = scopeIndex == 1
                        ? $"特定配方【{targetRecipe?.RecipeName ?? "(配方已清除)"}】"
                        : $"工位级（{stationText}）";
                }
                MessageBox.Show(
                    $"标定矩阵已成功保存并应用（{scopeText}）：\n{targetFile}\n{recipeNote}",
                    "应用成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// 把矩阵文件路径写进目标配方（配方级发布核心）：
        ///   遍历配方主流程/子流程全部 CalibrationApply 节点——凡节点的标定方案名匹配当前
        ///   profile（或节点尚未绑定方案名但当前路径与本次源路径一致）即把 HomMatFilePath
        ///   指向新路径，并补写 CalibrationProfileName（巩固绑定）。
        /// ⚠ 2026-09-05：不再同步 ProcessParameters.CalibrationDataPath——该字段已从
        ///   ProcessParameterSet 删除（运行时零消费，真身是节点 HomMatFilePath）。
        /// 返回给调用方的操作说明（含未命中提示）。
        /// </summary>
        private string SyncMatrixIntoRecipe(RecipeModel recipe, CalibrationProfile profile, string matrixFilePath)
        {
            if (recipe == null) return string.Empty;
            try
            {
                // 只读入口快照 profile（SaveMatrix 已把调用方状态全量快照，此处不再碰可变 VM 属性）
                string profileName = profile?.Name;
                string oldPath = profile?.HomMatFilePath;

                // 遍历配方所有流程（主 + 子）中的 CalibrationApply 节点
                int matched = 0;
                if (recipe.MainProcess != null)
                {
                    matched += RewriteCalibrationNodePaths(recipe.MainProcess, profileName, oldPath, matrixFilePath);
                }
                if (recipe.SubProcesses != null)
                {
                    foreach (var sub in recipe.SubProcesses.Values)
                    {
                        matched += RewriteCalibrationNodePaths(sub, profileName, oldPath, matrixFilePath);
                    }
                }

                bool saved = _recipeStorage.SaveRecipe(recipe);
                if (!saved)
                {
                    return "\n⚠ 配方保存失败：矩阵已落盘，但未能写入配方。";
                }
                return matched > 0
                    ? $"\n✓ 已回写 {matched} 个标定节点 → 配方【{recipe.RecipeName}】将直接消费此矩阵。"
                    : "\n⚠ 配方流内未找到引用此方案的标定节点，配方未改动。\n  请先在流程编辑器中把「标定转换」节点的矩阵路径指向上述文件，再重新发布；\n  或放入节点并绑定本标定方案后再次发布。";
            }
            catch (Exception ex)
            {
                return $"\n⚠ 写入配方失败：{ex.Message}";
            }
        }

        /// <summary>
        /// 递归改写一个流程内所有 CalibrationApply 节点的 HomMatFilePath。
        /// 命中条件：节点类型=CalibrationApply，且 节点标定方案名==profileName
        ///   （或节点方案名为空且当前路径==本次源路径 —— 兼容旧节点未绑定方案名的场景）。
        /// 返回改写节点数。
        /// </summary>
        private static int RewriteCalibrationNodePaths(
            Grayson.Vision.Contracts.Flow.Nodes.FlowProcessModel process,
            string profileName,
            string oldSourcePath,
            string newPath)
        {
            if (process?.Nodes == null) return 0;
            int count = 0;
            foreach (var node in process.Nodes)
            {
                if (node == null || node.Type != Grayson.Vision.Contracts.Flow.Enums.NodeType.CalibrationApply)
                {
                    continue;
                }
                var pm = node.ParameterModel;
                if (pm == null) continue;

                // 反射读取方案名/路径（CalibrationApplyParam 属于 Nodes 程序集，运行时强类型；
                // 这里按属性名反射，避免 UI 层与节点参数类型强耦合）
                string nodeProfile = GetStringProperty(pm, "CalibrationProfileName");
                string nodePath = GetStringProperty(pm, "HomMatFilePath");

                bool nameHit = !string.IsNullOrWhiteSpace(profileName)
                               && string.Equals(nodeProfile, profileName, StringComparison.Ordinal);
                bool orphanHit = string.IsNullOrWhiteSpace(nodeProfile)
                                 && !string.IsNullOrWhiteSpace(oldSourcePath)
                                 && string.Equals(nodePath, oldSourcePath, StringComparison.OrdinalIgnoreCase);
                if (!nameHit && !orphanHit) continue;

                SetStringProperty(pm, "HomMatFilePath", newPath);
                if (!string.IsNullOrWhiteSpace(profileName))
                {
                    SetStringProperty(pm, "CalibrationProfileName", profileName);
                }
                count++;
            }
            return count;
        }

        private static string GetStringProperty(object target, string propName)
        {
            try
            {
                var prop = target.GetType().GetProperty(propName);
                return prop?.GetValue(target) as string;
            }
            catch { return null; }
        }

        private static void SetStringProperty(object target, string propName, string value)
        {
            try
            {
                var prop = target.GetType().GetProperty(propName);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(target, value);
                }
            }
            catch { }
        }

        /// <summary>
        /// 根据发布目标域计算矩阵文件的目标目录（2026-09-05 收敛两档；2026-09-06 改由调用方
        /// 传入入口快照 scope/recipe/profile——SaveMatrix 全程不再读可变 VM 属性，杜绝竞态空引用）：
        ///   0 工位级    → Recipes\Workstations\{工位码}\Calib （该工位所有配方共享；无绑工位落 Default）
        ///   1 特定配方  → Recipes\{配方Code}\Calib            （随目标配方走，并回写其流节点矩阵路径）
        /// 已移除旧"设备级"目录（Recipes\Devices）——矩阵不属于设备实例，发布目标是消费它的配方/工位。
        /// </summary>
        private string GetScopeTargetDirectory(int scopeIndex, RecipeModel targetRecipe, CalibrationProfile profile)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            if (scopeIndex == 1)
            {
                // 特定配方：以选中配方的 RecipeCode 为目录名（无则回退 RecipeId，杜绝 "Default" 混写）
                string recipeKey = !string.IsNullOrWhiteSpace(targetRecipe?.RecipeCode)
                    ? targetRecipe.RecipeCode
                    : (targetRecipe?.RecipeId ?? "Default");
                return Path.Combine(baseDir, "Recipes", recipeKey, "Calib");
            }

            // 默认 0 工位级：Recipes\Workstations\{工位码}\Calib（跨配方共享；只读入口快照）
            string station = string.IsNullOrWhiteSpace(profile?.BoundStationCode)
                ? "Default"
                : profile.BoundStationCode;
            return Path.Combine(baseDir, "Recipes", "Workstations", station, "Calib");
        }

        /// <summary>
        /// 让列表（主集 + 可见集）中该 Profile 的行模板重新求值（名称等非 INPC 属性或外部替换场景）
        /// </summary>
        private void RefreshProfileListItem(CalibrationProfile profile)
        {
            if (profile == null) return;
            int index = CalibrationProfiles.IndexOf(profile);
            if (index >= 0)
            {
                CalibrationProfiles[index] = profile;
            }
            int visibleIndex = VisibleProfiles.IndexOf(profile);
            if (visibleIndex >= 0)
            {
                VisibleProfiles[visibleIndex] = profile;
            }
        }

        /// <summary>
        /// 导入外部标定矩阵文件 (.tup) 到当前选中方案
        /// </summary>
        private void ImportMatrixFile()
        {
            if (SelectedCalibrationProfile == null) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入标定矩阵文件",
                Filter = "标定矩阵文件 (*.tup)|*.tup|所有文件 (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedCalibrationProfile.HomMatFilePath = dlg.FileName;
                SelectedCalibrationProfile.IsCalibrated = true;
                SelectedCalibrationProfile.UpdatedAt = DateTime.Now;
                SaveProfileToRepository(SelectedCalibrationProfile);

                OnPropertyChanged(nameof(SelectedCalibrationProfile));
                ExecuteTestMap();
            }
        }

        /// <summary>
        /// 导出当前方案的标定矩阵文件到指定位置
        /// </summary>
        private void ExportMatrixFile()
        {
            if (SelectedCalibrationProfile == null || string.IsNullOrEmpty(SelectedCalibrationProfile.HomMatFilePath))
            {
                MessageBox.Show("当前方案没有可导出的标定矩阵！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出标定矩阵文件",
                Filter = "标定矩阵文件 (*.tup)|*.tup",
                FileName = $"{SelectedCalibrationProfile.Name}_HandEye.tup"
            };

            if (dlg.ShowDialog() == true)
            {
                var res = _calibService.SaveHomMatFile(SelectedCalibrationProfile.HomMatFilePath, dlg.FileName);
                if (res.Success)
                {
                    MessageBox.Show($"标定矩阵已导出：\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("导出矩阵失败：" + res.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ============================================================
        // 相机安装特性 · 同工位一致性（2026-09-09）
        // ============================================================
        // 背景：同一台物理相机在多个标定方案里各存一份 CameraMovesWithZ。老档案根本没有这个字段，
        //       → 向导（t/e 方案）走"未声明=随 Z 升降"、校验台（H 方案）存了"固定不随 Z"，
        //         两个口径互相打架：一边硬阻 ↑↓ Z 守护，一边又报"换算不受影响"。
        // 策略：以【显式值】为准，广播给同工位【未声明】的档案；已显式且相反的档案不覆盖，只报警。
        /// <summary>
        /// 把一个方案的『相机是否随 Z 升降』广播到同工位的其它方案，消掉同一台相机的两种口径。
        /// </summary>
        /// <returns>(同步档案数, 仍冲突的档案名列表)</returns>
        private (int Synced, System.Collections.Generic.List<string> Conflicts) BroadcastCameraMovesWithZ(
            CalibrationProfile source, bool? value)
        {
            var conflicts = new System.Collections.Generic.List<string>();
            if (source == null || value == null) return (0, conflicts);   // Indeterminate 不广播
            try
            {
                if (string.IsNullOrWhiteSpace(source.BoundStationCode)) return (0, conflicts);
                string station = source.BoundStationCode;
                int synced = 0;
                foreach (var po in _profileRepository.GetAll())
                {
                    if (po?.Model == null) continue;
                    if (!string.Equals(po.BoundStationCode, station, StringComparison.OrdinalIgnoreCase)) continue;
                    var sibling = po.Model;
                    if (!string.IsNullOrEmpty(source.Id) && string.Equals(sibling.Id, source.Id, StringComparison.Ordinal)) continue;
                    if (sibling.CameraMovesWithZ == null)
                    {
                        sibling.CameraMovesWithZ = value;          // 只补"未声明"，不覆盖已有选择
                        SaveProfileToRepository(sibling);
                        synced++;
                    }
                    else if (sibling.CameraMovesWithZ.Value != value.Value)
                    {
                        conflicts.Add(po.ProfileName);
                    }
                }
                return (synced, conflicts);
            }
            catch
            {
                return (0, conflicts); // 同步失败不影响主流程
            }
        }

        private void SaveProfileToRepository(CalibrationProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }

            var po = new CalibrationProfilePo
            {
                Id = profile.Id,
                ProfileName = profile.Name,
                CalibrationType = profile.Type,
                BoundStationCode = profile.BoundStationCode,
                BoundDeviceId = profile.BoundDeviceId,
                IsCalibrated = profile.IsCalibrated,
                Model = profile
            };

            var existing = _profileRepository.GetById(po.Id) ?? _profileRepository.GetByName(profile.Name);
            if (existing == null)
            {
                _profileRepository.Insert(po);
            }
            else
            {
                _profileRepository.Update(po);
            }
        }
    }
}
