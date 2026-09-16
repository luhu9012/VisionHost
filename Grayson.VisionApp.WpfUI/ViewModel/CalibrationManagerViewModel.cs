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
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Services;
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

        /// <summary>
        /// 对针补偿窗口单例（2026-09-10 非模态化后与校验台同款防重入；共享同一相机/轴卡）。
        /// 窗口 Closed 时置 null。
        /// </summary>
        private CalibrationToolOffsetWindow _activeToolOffsetWindow;

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

            /// <summary>来源计划任务（v2 引擎 CalibrationPlanEngineV2 产出的 CalibrationTaskSpec）</summary>
            public CalibrationTaskSpec Task { get; set; }
            /// <summary>物理量徽标短词（相机H / 旋转偏心e / 下相机H / 下相机像素旋转中心…）</summary>
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
            public EyeMode EyeMode { get; set; }
            /// <summary>★v2 物理量（创建 profile 时写入 Quantity）</summary>
            public CalibrationQuantity Quantity { get; set; }
            /// <summary>★v2 采集路径（创建 profile 时写入 PrimaryPath）</summary>
            public CalibrationAcquirePath PrimaryPath { get; set; }
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
                    : (p.PrimaryPath == CalibrationAcquirePath.PickPlaceReturn
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
            public CalibrationQuantity Type { get; set; }
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
            new CalibrationTypeOption { Type = CalibrationQuantity.HandEye, DisplayName = "手眼 H（九点/吸放/下相机）", Description = "像素↔机械平面 2D 仿射映射。采集方式由 PrimaryPath 决定：上固定走位九点、下固定吸件走位九点、吸放式。" },
            new CalibrationTypeOption { Type = CalibrationQuantity.ToolRotation, DisplayName = "旋转中心 e", Description = "U 轴旋转采样拟合圆：求旋转中心 + 偏心矢量 + 基准角 U0。上相机=机械域拟合；下相机=像素平面拟合。" },
            new CalibrationTypeOption { Type = CalibrationQuantity.ToolOffset, DisplayName = "对针 t（开发中）", IsAvailable = false, Description = "工具中心偏置 TCO。当前由任务卡/向导自动派生，暂不开放手动新建。" },
            new CalibrationTypeOption { Type = CalibrationQuantity.LensDistortion, DisplayName = "镜头畸变（开发中）", IsAvailable = false, Description = "TODO：待实现真实标定板/圆点阵列角点检测与去畸变后再开放。" },
            new CalibrationTypeOption { Type = CalibrationQuantity.PixelScale, DisplayName = "像素当量 s", IsAvailable = true, Description = "像素当量（mm/px）：已知标距/飞拍测距换算，独立产物。适用于飞拍纠偏、纯当量换算的相机。" }
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
                if (SelectedCalibrationProfile.Quantity == value.Type) return;
                if (!value.IsAvailable)
                {
                    MessageBox.Show($"「{value.DisplayName}」标定尚未实现（TODO），当前不可用。",
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
                    // 2026-09-11：槽/布局在标定中心右侧直接可编辑 —— 选中变化时同步候选与回显
                    RefreshCameraSlotOptions();
                    SyncEyeModeOptionToProfile();
                    // 2026-09-15：设备绑定也可编辑 —— 选中变化时重取设备池并按档案现值预选
                    RefreshBindingDeviceOptions();
                    OnPropertyChanged(nameof(ProfileSlotKey));
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

        // ---- 双向转换（2026-09-10）：新增 World → Pixel 反向 ----
        private double _testWorldX;
        public double TestWorldX
        {
            get => _testWorldX;
            set { if (Set(ref _testWorldX, value)) ExecuteTestMapReverse(); }
        }

        private double _testWorldY;
        public double TestWorldY
        {
            get => _testWorldY;
            set { if (Set(ref _testWorldY, value)) ExecuteTestMapReverse(); }
        }

        private string _testPixelResult = "Px: 0.0, Py: 0.0";
        public string TestPixelResult
        {
            get => _testPixelResult;
            set => Set(ref _testPixelResult, value);
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
                    "是否按当前方案（" + GetQuantityDisplayName(cur.Quantity) + " / " + cur.EyeMode + "）为 吸嘴" + nozzle + " 新建一条空方案？\n" +
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
                string baseName = GetDefaultNameForType(cur.Quantity, stationLabel);
                string newName = ApplyNozzleToAutoName(baseName, stationLabel, nozzle);
                var np = new CalibrationProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = newName,
                    Quantity = cur.Quantity,
                    EyeMode = cur.EyeMode,
                    NozzleKey = nozzle,
                    CameraId = string.IsNullOrWhiteSpace(cur.CameraId) ? "Cam_01" : cur.CameraId,
                    // 吸嘴分身必须继承同一相机槽（否则 e 卡落在默认槽、与 H 卡的依赖对不上）
                    CameraSlotKey = cur.CameraSlotKey,
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

        #region 相机槽 / 相机布局（2026-09-11：标定中心右侧直接可编辑）

        /// <summary>左侧「+ 新建」菜单的下发参数：标定类型 + 相机槽（可空=按档案建议）</summary>
        public class NewProfileRequest
        {
            public CalibrationQuantity Type { get; set; } = CalibrationQuantity.HandEye;

            /// <summary>目标相机槽（null/空 = 走 SuggestSlotKeyForStation 建议值）</summary>
            public string SlotKey { get; set; }
        }

        /// <summary>左侧「+ 新建」菜单用：按「类型(+相机槽)」新建方案（复合工位可一次建到 Cam_C）</summary>
        public ICommand NewProfileForSlotCommand { get; }

        /// <summary>相机布局候选项（眼在手外=固定相机 / 眼在手上=随动相机）</summary>
        public class EyeModeOption
        {
            public EyeMode Value { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public override string ToString() => DisplayName;
        }

        public ObservableCollection<EyeModeOption> EyeModeOptions { get; } = new ObservableCollection<EyeModeOption>
        {
            new EyeModeOption { Value = EyeMode.EyeToHand, DisplayName = "眼在手外（相机固定）",
                Description = "相机固定在机架上不动，机械手/工件走位。上下固定相机（俯视/仰视）均属此类。" },
            new EyeModeOption { Value = EyeMode.EyeInHand, DisplayName = "眼在手上（相机随动）",
                Description = "相机装在机械手上随末端运动，工件固定不动。" }
        };

        /// <summary>相机槽候选项：工位档案定义的槽 ∪ 常用槽 ∪ 当前值（当前值排前，保证回显不丢）</summary>
        public ObservableCollection<string> CameraSlotOptions { get; } = new ObservableCollection<string>();

        private string _slotHintText = string.Empty;
        /// <summary>工位档案里各槽的用途说明（帮助现场分清上/下固定相机该选哪个槽）</summary>
        public string SlotHintText
        {
            get => _slotHintText;
            set => Set(ref _slotHintText, value);
        }

        /// <summary>
        /// 方案当前生效的相机槽：CameraSlotKey 优先；旧档没有该字段时按 CameraId(Cam_*)、
        /// 再从方案名里嵌的槽键（如「…_Cam_A_九点手眼」）兜底——旧方案 CameraId 已被物理设备名
        /// （Hikvision_…_UpCamera）覆盖，只靠 CameraId 会把已标定方案判成"槽未指定"。
        /// </summary>
        public static string EffectiveSlotKey(CalibrationProfile p)
        {
            if (p == null) return null;
            if (!string.IsNullOrWhiteSpace(p.CameraSlotKey)) return p.CameraSlotKey.Trim();
            string cam = (p.CameraId ?? string.Empty).Trim();
            if (cam.StartsWith("Cam_", StringComparison.OrdinalIgnoreCase)) return cam;
            var m = System.Text.RegularExpressions.Regex.Match(p.Name ?? string.Empty, @"Cam_[A-Za-z0-9]+");
            return m.Success ? m.Value : null;
        }

        /// <summary>
        /// 当前方案的相机槽（右侧下拉可编辑）。
        /// 写入即落盘并重建任务卡（产物身份是 站|量|槽|吸嘴，改槽=换一条标定产物）；
        /// 已标定方案改槽会弹确认——矩阵属于原槽，改后必须对目标槽重新执行 H 标定。
        /// </summary>
        public string ProfileSlotKey
        {
            get => EffectiveSlotKey(SelectedCalibrationProfile);
            set
            {
                var p = SelectedCalibrationProfile;
                if (p == null) return;
                string v = (value ?? string.Empty).Trim();
                string cur = EffectiveSlotKey(p) ?? string.Empty;
                if (string.Equals(cur, v, StringComparison.OrdinalIgnoreCase)) return;
                // 空值不落库：可编辑 ComboBox 在候选列表重建/失焦瞬间会推一个空文本过来，
                // 若照单全收会静默清空槽（已标定方案还会弹确认框）。要清空请显式选/填别的槽。
                if (string.IsNullOrWhiteSpace(v))
                {
                    OnPropertyChanged(nameof(ProfileSlotKey));
                    return;
                }

                if (p.IsCalibrated || !string.IsNullOrWhiteSpace(p.HomMatFilePath))
                {
                    var ask = MessageBox.Show(
                        "本方案已有标定结果（矩阵属于槽 " + (string.IsNullOrWhiteSpace(cur) ? "未指定" : cur) + "）。\n\n" +
                        "把相机槽改为 " + (string.IsNullOrWhiteSpace(v) ? "（空）" : v) + " 后，现有矩阵不再对应该槽，需对目标槽重新执行 H 标定并发布。\n\n是否继续改槽？",
                        "改槽会使现有标定结果失效", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (ask != MessageBoxResult.Yes)
                    {
                        OnPropertyChanged(nameof(ProfileSlotKey)); // 下拉回滚
                        return;
                    }
                }

                p.CameraSlotKey = v;
                SaveProfileToRepository(p);
                RebuildDerivedCards();
                RefreshCameraSlotOptions();
                OnPropertyChanged(nameof(ProfileSlotKey));
                ScopeNoteText = string.IsNullOrWhiteSpace(v)
                    ? "已清空相机槽——请重新选择本方案对应的槽（复合工位上下相机必须分槽）。"
                    : $"相机槽已改为 {v}（此后在向导里绑定物理相机不再覆盖槽；请对该槽执行 H 标定并发布）。";
            }
        }

        private EyeModeOption _selectedEyeModeOption;
        /// <summary>
        /// 当前方案的相机安装方式（右侧下拉可编辑）：眼在手上/眼在手外。
        /// 改布局=换一套采集路径与 H 推导口径，故落盘后重建任务卡；已标定方案给二次确认。
        /// </summary>
        public EyeModeOption ProfileEyeModeOption
        {
            get => _selectedEyeModeOption;
            set
            {
                if (value == null) return;
                var p = SelectedCalibrationProfile;
                if (p == null) return;
                if (p.EyeMode == value.Value)
                {
                    _selectedEyeModeOption = value;
                    OnPropertyChanged(nameof(ProfileEyeModeOption));
                    return;
                }

                if (p.IsCalibrated || !string.IsNullOrWhiteSpace(p.HomMatFilePath))
                {
                    var ask = MessageBox.Show(
                        "本方案已有标定结果（按「" + (p.EyeMode == EyeMode.EyeInHand ? "眼在手上" : "眼在手外") + "」口径产出）。\n\n" +
                        "改为「" + value.DisplayName + "」后，采集路径与 H 推导口径都会变，现有矩阵需重算——请改完重新执行标定。\n\n是否继续？",
                        "改布局会使现有标定结果失效", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (ask != MessageBoxResult.Yes)
                    {
                        SyncEyeModeOptionToProfile(); // 下拉回滚
                        return;
                    }
                }

                p.EyeMode = value.Value;
                _selectedEyeModeOption = value;
                SaveProfileToRepository(p);
                RebuildDerivedCards();
                OnPropertyChanged(nameof(ProfileEyeModeOption));
                ScopeNoteText = $"相机安装方式已改为「{value.DisplayName}」——请在任务卡上重新执行标定（H）并发布。";
            }
        }

        /// <summary>新建菜单用的槽选项（仅工位档案定义的槽；复合工位会同时列出上/下相机槽）</summary>
        public class SlotChoice
        {
            public string SlotKey { get; set; }
            public string Label { get; set; }
            public override string ToString() => Label;
        }

        /// <summary>取当前定位工位档案定义的相机槽（含用途标签）；无档案/无槽定义返回空表</summary>
        public List<SlotChoice> GetArchiveSlotOptions()
        {
            var list = new List<SlotChoice>();
            try
            {
                var archive = ResolveStationArchive(_scopeStationCode);
                var defs = archive?.Requirement?.CameraSlots;
                if (defs == null) return list;
                foreach (var s in defs)
                {
                    string k = s?.SlotKey?.Trim();
                    if (string.IsNullOrWhiteSpace(k)) continue;
                    string kind = (s.InstallKind ?? string.Empty).Trim();
                    string purpose = (s.Purpose ?? string.Empty).Trim();
                    string label = string.IsNullOrWhiteSpace(kind) ? k : k + "（" + kind + (string.IsNullOrWhiteSpace(purpose) ? "" : "·" + purpose) + "）";
                    list.Add(new SlotChoice { SlotKey = k, Label = label });
                }
            }
            catch { /* 档案不可读 → 菜单退化为不带槽新建 */ }
            return list;
        }

        /// <summary>选中方案变化时同步布局下拉（直接赋字段，不触发改值逻辑）</summary>
        private void SyncEyeModeOptionToProfile()
        {
            var mode = SelectedCalibrationProfile?.EyeMode ?? EyeMode.EyeToHand;
            _selectedEyeModeOption = EyeModeOptions.FirstOrDefault(o => o.Value == mode) ?? EyeModeOptions[0];
            OnPropertyChanged(nameof(ProfileEyeModeOption));
        }

        /// <summary>
        /// 重建相机槽候选 + 档案槽说明。取数优先级：当前方案绑定工位 → 定位工位。
        /// 候选 = 档案槽（带用途）∪ 常用槽 ∪ 当前值（当前值排第一）。
        /// </summary>
        public void RefreshCameraSlotOptions()
        {
            CameraSlotOptions.Clear();
            var hints = new List<string>();
            var slots = new List<string>();

            string station = SelectedCalibrationProfile?.BoundStationCode;
            if (string.IsNullOrWhiteSpace(station)) station = _scopeStationCode;

            try
            {
                var archive = ResolveStationArchive(station);
                var defs = archive?.Requirement?.CameraSlots;
                if (defs != null)
                {
                    foreach (var s in defs)
                    {
                        string k = s?.SlotKey?.Trim();
                        if (string.IsNullOrWhiteSpace(k)) continue;
                        if (!slots.Contains(k)) slots.Add(k);
                        string kind = (s.InstallKind ?? string.Empty).Trim();
                        string purpose = (s.Purpose ?? string.Empty).Trim();
                        string desc = string.IsNullOrWhiteSpace(kind)
                            ? k
                            : k + " " + kind + (string.IsNullOrWhiteSpace(purpose) ? "" : "·" + purpose);
                        hints.Add(desc + (kind.Contains("眼在手上") ? "（随动）" : "（固定）"));
                    }
                }
            }
            catch { /* 档案读取失败不影响编辑 */ }

            foreach (var d in new[] { "Cam_01", "Cam_A", "Cam_B", "Cam_C", "Cam_D" })
            {
                if (!slots.Contains(d)) slots.Add(d);
            }

            string cur = ProfileSlotKey;
            if (!string.IsNullOrWhiteSpace(cur))
            {
                slots.RemoveAll(x => string.Equals(x, cur, StringComparison.OrdinalIgnoreCase));
                slots.Insert(0, cur);
            }
            foreach (var s in slots) CameraSlotOptions.Add(s);

            SlotHintText = hints.Count > 0
                ? "工位档案相机槽：" + string.Join(" ｜ ", hints)
                : (string.IsNullOrWhiteSpace(station) ? string.Empty : "工位档案未定义相机槽——可手填（复合工位建议按上/下相机分槽命名）。");
            OnPropertyChanged(nameof(CameraSlotOptions));
        }

        // ==================== 设备绑定（相机 / 运动卡）可编辑（2026-09-15） ====================
        //
        // 背景：绑定四字段（CameraId/AxisId/BoundDeviceId/BindingInfo）此前**只有向导第一步**会写
        // 真值，标定中心只写占位文本，而界面「绑定工位/设备」是个**只读 TextBlock** ⇒
        // "按计划创建"出来的方案（复合工位 Cam_C 两条即如此）绑定错成
        // CameraId="Cam_C"（槽名被当设备名）/ AxisId="Axis_X"（占位）/ BoundDeviceId=null，
        // 现场既改不掉、也看不出错在哪（BoundDeviceId 为空还会让矩阵落到 Devices\Default\Calib\）。
        //
        // 处置：标定中心给出「重绑相机/运动卡」入口，写回**与向导同一实现**
        // （CalibrationBindingWriter.Apply）—— 同一件事在两处各写一份，分叉迟早出现在边界上。

        private IDevicePool _bindingDevicePool;

        /// <summary>相机设备候选（来自全局设备池，与向导同一数据源）</summary>
        public ObservableCollection<ICamera> BindingCameraOptions { get; } = new ObservableCollection<ICamera>();

        /// <summary>运动卡候选（来自全局设备池）</summary>
        public ObservableCollection<IMotionCard> BindingMotionOptions { get; } = new ObservableCollection<IMotionCard>();

        private ICamera _selectedBindingCamera;
        /// <summary>待绑定的相机（可编辑下拉）。null = 未选择 —— 不替用户猜设备。</summary>
        public ICamera SelectedBindingCamera
        {
            get => _selectedBindingCamera;
            set
            {
                if (!Set(ref _selectedBindingCamera, value)) return;
                OnPropertyChanged(nameof(BindingDeviceHintText));
                OnPropertyChanged(nameof(BindingDirtyText));
                (RebindDevicesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        private IMotionCard _selectedBindingMotion;
        /// <summary>待绑定的运动卡（可编辑下拉）。null = 未选择。</summary>
        public IMotionCard SelectedBindingMotion
        {
            get => _selectedBindingMotion;
            set
            {
                if (!Set(ref _selectedBindingMotion, value)) return;
                OnPropertyChanged(nameof(BindingDeviceHintText));
                OnPropertyChanged(nameof(BindingDirtyText));
                (RebindDevicesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// 绑定栏人话说明：把"档案现值 vs 设备池能否对上"直接写出来。
        /// 避免出现"绑定栏空白 ⇒ 操作员以为没绑，其实是绑了个对不上的占位值"。
        /// </summary>
        public string BindingDeviceHintText
        {
            get
            {
                var p = SelectedCalibrationProfile;
                if (p == null) return "未选择方案";
                if (_bindingDevicePool == null)
                    return "设备池未就绪（工位主机未启动）—— 暂时无法重绑。";

                if (BindingCameraOptions.Count == 0 && BindingMotionOptions.Count == 0)
                    return "设备池里没有设备 —— 请先到「设备池」扫描/注册相机与运动卡。";

                string curCam = string.IsNullOrWhiteSpace(p.CameraId) ? "（空）" : p.CameraId;
                string curMot = string.IsNullOrWhiteSpace(p.AxisId) ? "（空）" : p.AxisId;
                bool camOk = CalibrationBindingWriter.MatchBound(BindingCameraOptions, p.CameraId) != null;
                bool motOk = CalibrationBindingWriter.MatchBound(BindingMotionOptions, p.AxisId) != null;

                string s = "档案现值：相机 " + curCam + (camOk ? " ✓" : " ✗不在设备池")
                         + " ｜ 运动卡 " + curMot + (motOk ? " ✓" : " ✗不在设备池");
                if (string.IsNullOrWhiteSpace(p.BoundDeviceId))
                    s += "　⚠ BoundDeviceId 为空 ⇒ 标定矩阵会落到 Recipes\\Devices\\Default\\Calib\\";
                return s;
            }
        }

        /// <summary>选中项是否与档案不同（给"将改为…"提示）</summary>
        public string BindingDirtyText
        {
            get
            {
                var p = SelectedCalibrationProfile;
                if (p == null) return string.Empty;
                if (SelectedBindingCamera != null
                    && !string.Equals(CalibrationBindingWriter.KeyOf(SelectedBindingCamera), p.CameraId, StringComparison.OrdinalIgnoreCase))
                    return "将把相机改为「" + CalibrationBindingWriter.DisplayName(SelectedBindingCamera) + "」";
                if (SelectedBindingMotion != null
                    && !string.Equals(CalibrationBindingWriter.KeyOf(SelectedBindingMotion), p.AxisId, StringComparison.OrdinalIgnoreCase))
                    return "将把运动卡改为「" + CalibrationBindingWriter.DisplayName(SelectedBindingMotion) + "」";
                return string.Empty;
            }
        }

        /// <summary>重绑相机/运动卡（写回档案四字段，与向导第一步同源）</summary>
        public ICommand RebindDevicesCommand { get; private set; }

        /// <summary>
        /// 刷新设备候选并按档案现值预选。★ **严格匹配、不兜底取第一个**：
        /// 复合工位上/下相机同在池里，兜底会让"下相机档案"静默指向上相机（旧向导的隐患）。
        /// </summary>
        public void RefreshBindingDeviceOptions()
        {
            _bindingDevicePool = App.StationHostRuntime != null ? App.StationHostRuntime.DevicePool : null;

            BindingCameraOptions.Clear();
            BindingMotionOptions.Clear();
            var p = SelectedCalibrationProfile;

            if (_bindingDevicePool != null)
            {
                foreach (var cam in _bindingDevicePool.GetAllDevices().OfType<ICamera>())
                    BindingCameraOptions.Add(cam);
                foreach (var mot in _bindingDevicePool.GetAllDevices().OfType<IMotionCard>())
                    BindingMotionOptions.Add(mot);
            }

            _selectedBindingCamera = CalibrationBindingWriter.MatchBound(BindingCameraOptions, p?.CameraId) as ICamera;
            _selectedBindingMotion = CalibrationBindingWriter.MatchBound(BindingMotionOptions, p?.AxisId) as IMotionCard;
            OnPropertyChanged(nameof(SelectedBindingCamera));
            OnPropertyChanged(nameof(SelectedBindingMotion));
            OnPropertyChanged(nameof(BindingDeviceHintText));
            OnPropertyChanged(nameof(BindingDirtyText));
            (RebindDevicesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void RebindDevices()
        {
            var p = SelectedCalibrationProfile;
            if (p == null) return;
            if (SelectedBindingCamera == null && SelectedBindingMotion == null)
            {
                MessageBox.Show("请先选择要绑定的相机 / 运动卡。", "重绑设备",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string oldCam = string.IsNullOrWhiteSpace(p.CameraId) ? "（空）" : p.CameraId;
            string oldMot = string.IsNullOrWhiteSpace(p.AxisId) ? "（空）" : p.AxisId;
            // ★ 某一栏为 null（未选/没匹配上）时，写回 **不动那一栏**（见 CalibrationBindingWriter.Apply 的 null 语义）。
            //   所以确认框必须如实写"保持不变"，不能显示成"未选择"——否则用户会以为要把它清空。
            string newCam = SelectedBindingCamera != null
                ? CalibrationBindingWriter.DisplayName(SelectedBindingCamera) + "（" + CalibrationBindingWriter.KeyOf(SelectedBindingCamera) + "）"
                : "（保持不变）";
            string newMot = SelectedBindingMotion != null
                ? CalibrationBindingWriter.DisplayName(SelectedBindingMotion) + "（" + CalibrationBindingWriter.KeyOf(SelectedBindingMotion) + "）"
                : "（保持不变）";
            var ask = MessageBox.Show(
                "将把方案【" + p.Name + "】的绑定改为：\n\n"
                + "　相机：" + oldCam + "\n　　　→ " + newCam + "\n"
                + "　运动卡：" + oldMot + "\n　　　→ " + newMot + "\n\n"
                + "★ 相机变了 ⇒ 矩阵产物目录也跟着变（Recipes\\Devices\\<相机>\\Calib\\）。\n"
                + "　若本方案已有标定结果，需对该相机重新执行 H 标定并发布。\n\n是否继续？",
                "重绑相机 / 运动卡", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return;

            // ★ 写回与向导第一步同一实现（CalibrationBindingWriter.Apply）
            CalibrationBindingWriter.Apply(p, SelectedBindingCamera, SelectedBindingMotion);
            SaveProfileToRepository(p);
            RefreshBindingDeviceOptions();
            RebuildDerivedCards();
            ScopeNoteText = "已重绑设备：相机=" + (p.CameraId ?? "（未选）") + " ｜ 运动卡=" + (p.AxisId ?? "（未选）")
                          + "。若该方案已标定，请重新执行 H 标定并发布（矩阵目录随相机变）。";
        }

        #endregion

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
                CalibrationQuantity type = CalibrationQuantity.HandEye;
                if (p is CalibrationQuantity t)
                {
                    type = t;
                }
                CreateNewProfile(type);
            });

            // 2026-09-11：左侧「+ 新建」菜单可带相机槽（复合工位一次建到下相机 Cam_C，不必进向导改）
            NewProfileForSlotCommand = new RelayCommand(p =>
            {
                var req = p as NewProfileRequest;
                if (req == null)
                {
                    if (p is CalibrationQuantity t2) req = new NewProfileRequest { Type = t2 };
                    else req = new NewProfileRequest();
                }
                CreateNewProfile(req.Type, req.SlotKey);
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

            // 2026-09-15：重绑相机/运动卡（修复"按计划创建"出来的方案绑定是占位值、且界面改不掉）
            RebindDevicesCommand = new RelayCommand(_ => RebindDevices(), _ => SelectedCalibrationProfile != null);

            LoadProfiles();
            RefreshBindingDeviceOptions();
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
            RefreshCameraSlotOptions(); // 2026-09-11：进入工位即备好槽候选（含档案槽用途说明）

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
                // 2026-09-11：方案与工位档案口径矛盾时给可操作提示（推荐/历史方案判错的主要表现：
                // 布局被默认成眼在手上、槽落在 Cam_01、复合工位只建了一条相机槽的方案）
                var mismatch = DescribeProfileMismatch(existing);
                if (!string.IsNullOrEmpty(mismatch)) ScopeNoteText = mismatch;
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
                    ? $"工位尚无标定方案，已按默认创建「{GetQuantityDisplayName(type)}」；可在上方标定类型下拉按需调整。"
                    : $"工位尚无标定方案，已按需求档案建议（{reason}）创建「{GetQuantityDisplayName(type)}」；可在上方标定类型下拉按需调整。";
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

            // 档案 → 标定计划任务卡（★2026-09-12 切换 v2 引擎：H/e 拆分，相机级按槽、工具级按吸嘴）
            var specs = CalibrationPlanEngineV2.Derive(archive);
            var executable = specs != null
                ? specs.Where(t => t.Quantity != CalibrationQuantity.LensDistortion).ToList()
                : new List<CalibrationTaskSpec>();
            if (executable.Count > 0)
            {
                BuildCandidates(archive);
                OnPropertyChanged(nameof(CandidatesSummary));
                return;
            }

            // 档案可推导但无几何标定需求（检测/测量/OCR 类相机仅需模板/当量）
            ScopeNoteText = "按档案推导：本工位无几何标定需求（检测/测量/OCR 类相机仅需模板/当量），无需创建标定方案。";
        }

        /// <summary>按档案标定计划构建任务卡清单（★2026-09-12 切换 v2 引擎；相机级/工具级拆分；EyeMode 随任务推导）</summary>
        private void BuildCandidates(StationProfile archive)
        {
            Candidates.Clear();
            var specs = CalibrationPlanEngineV2.Derive(archive);
            foreach (var spec in specs)
            {
                // 畸变预留：不进任务卡
                if (spec.Quantity == CalibrationQuantity.LensDistortion) continue;
                string slotKey = string.IsNullOrWhiteSpace(spec.SlotKey) ? "相机" : spec.SlotKey;
                bool toolLevel = spec.Quantity == CalibrationQuantity.ToolRotation
                                 || spec.Quantity == CalibrationQuantity.ToolOffset;
                string chip = toolLevel ? "吸嘴" + spec.NozzleKey : slotKey;
                string suggestion = spec.Suggestion ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(spec.Note))
                {
                    suggestion += "\n⚠ " + spec.Note;
                }
                // v2 物理量显示名（用 CalibrationCardText 统一文案）
                Candidates.Add(new CalibrationCandidate
                {
                    Task = spec,
                    KindBadge = QuantityBadge(spec.Quantity, spec.PrimaryPath),
                    ChipText = chip,
                    SlotKey = spec.SlotKey,
                    NozzleKey = spec.NozzleKey ?? "1",
                    IsToolLevel = toolLevel,
                    TagText = spec.DisplayName ?? slotKey,
                    TypeDisplay = GetQuantityDisplayName(spec.Quantity),
                    EyeMode = spec.Layout,
                    Suggestion = suggestion,
                    Note = spec.Note,
                    Quantity = spec.Quantity,
                    PrimaryPath = spec.PrimaryPath,
                    IsSelected = spec.IsRequired // 主线必做默认勾选
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
                bool toolLevel = cand.IsToolLevel;
                string slotTok = (cand.SlotKey ?? string.Empty).Trim();
                bool slotKnown = slotTok.Length > 0 && slotTok != "相机";
                string cameraId = slotKnown ? slotTok : "Cam_01";
                // ★★2026-09-15 修正：工具级（e/t）名字必须带槽。旧式 `吸嘴{n}_{类型}` 对复合工位的
                //   【Cam_A 的 e】与【Cam_C 的 e】生成**完全相同的名字**（同为"吸嘴1_旋转中心 e"），
                //   两份档案只有 Id 后缀不同 ⇒ 人工分不清、且 SaveProfile 的 GetByName 回退会互相命中
                //   （同工位继承/删除定位都可能张冠李戴）。槽已知就写进名字，缺槽时保持旧式并留提示。
                string namePart = toolLevel
                    ? (slotKnown ? $"{slotTok}_吸嘴{cand.NozzleKey}_{cand.TypeDisplay}" : $"吸嘴{cand.NozzleKey}_{cand.TypeDisplay}")
                    : $"{cameraId}_{cand.TypeDisplay}";
                string fullName = $"{_scopeStationName}_{namePart}";
                // 同名防重（同一工位重名会让"按名找档案"的回退路径失去鉴别力）：撞名则加序号后缀并留痕
                if (CalibrationProfiles.Any(x => x != null
                        && string.Equals(x.Name, fullName, StringComparison.OrdinalIgnoreCase)))
                {
                    int n = 2;
                    while (CalibrationProfiles.Any(x => x != null
                           && string.Equals(x.Name, fullName + "#" + n, StringComparison.OrdinalIgnoreCase))) n++;
                    AppendLog($"[新建] 方案名「{fullName}」已存在 → 本次改用「{fullName}#{n}」（同名档案会让按名定位失去鉴别力）。");
                    fullName = fullName + "#" + n;
                }
                var np = new CalibrationProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = fullName,
                    Quantity = cand.Quantity,           // ★v2 物理量（唯一权威语义）
                    PrimaryPath = cand.PrimaryPath,     // ★v2 采集路径（含下相机专属）
                    EyeMode = cand.EyeMode,
                    NozzleKey = cand.NozzleKey ?? "1", // 双吸嘴：每吸嘴独立旋转/偏心标定
                    CameraId = cameraId,               // 物理设备占位；向导第 1 步按实际设备覆盖
                    // 2026-09-11：槽独立存（CameraId 会被设备名覆盖→旧逻辑恒猜 Cam_01，上下相机撞槽）
                    CameraSlotKey = cameraId,
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
            RefreshCameraSlotOptions();
        }

        private void RaiseScopeChanged()
        {
            OnPropertyChanged(nameof(IsStationScope));
            OnPropertyChanged(nameof(ScopeChipText));
            OnPropertyChanged(nameof(ScopeHintText));
        }

        /// <summary>按当前钉住工位的需求档案建议标定物理量（兜底；v2 引擎已按槽派生，此方法仅无槽档案兜底）。</summary>
        private CalibrationQuantity SuggestCalibrationTypeForStation(out string reason)
        {
            reason = "工位档案未填完整，默认手眼 H";
            try
            {
                var profile = ResolveStationArchive(_scopeStationCode);
                var req = profile?.Requirement;
                if (req == null) return CalibrationQuantity.HandEye; // 档案缺失 / 问卷未填

                reason = "按工位档案推导手眼 H（像素↔机械平面映射）";
                return CalibrationQuantity.HandEye;
            }
            catch
            {
                return CalibrationQuantity.HandEye;
            }
        }

        /// <summary>
        /// 按工位档案取建议相机槽（2026-09-11）。
        /// 判据序：① 档案里"引导/定位/纠偏"类槽 → ② 第一个槽；
        /// 且【优先挑本工位还没有标定方案的槽】（2026-09-11 补）：复合工位（上 Cam_A + 下 Cam_C）
        /// 上相机标定完后，再新建方案应当落到还没建的 Cam_C，而不是每次都给 Cam_A
        /// ——这正是"推荐方案判错、只能手工新建"的根源。槽全被占完时退回首选槽。
        /// </summary>
        private string SuggestSlotKeyForStation()
        {
            try
            {
                var archive = ResolveStationArchive(_scopeStationCode);
                var slots = archive?.Requirement?.CameraSlots;
                if (slots == null || slots.Count == 0) return null;

                var keys = slots.Select(s => s?.SlotKey?.Trim())
                                .Where(k => !string.IsNullOrWhiteSpace(k))
                                .Distinct()
                                .ToList();
                if (keys.Count == 0) return null;

                string preferred = null;
                foreach (var s in slots)
                {
                    string k = s?.SlotKey?.Trim();
                    if (string.IsNullOrWhiteSpace(k)) continue;
                    string purpose = (s.Purpose ?? string.Empty) + (s.InstallKind ?? string.Empty);
                    if (purpose.Contains("引导") || purpose.Contains("定位") || purpose.Contains("纠偏"))
                    {
                        preferred = k;
                        break;
                    }
                }
                preferred = preferred ?? keys[0];

                // 已建过方案的槽（按方案生效槽判定，含"方案名里嵌的 Cam_*"兜底）
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var q in CalibrationProfiles)
                {
                    if (q == null || !string.Equals(q.BoundStationCode, _scopeStationCode, StringComparison.OrdinalIgnoreCase)) continue;
                    string k = EffectiveSlotKey(q);
                    if (!string.IsNullOrWhiteSpace(k)) used.Add(k.Trim());
                }

                var free = keys.Where(k => !used.Contains(k)).ToList();
                if (free.Count == 0) return preferred;
                return free.Contains(preferred) ? preferred : free[0];
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 方案 vs 工位档案 的口径矛盾提示（2026-09-11）。
        /// 背景：工位跳转过来的推荐方案是"猜"的，猜错时现场此前既看不出来也改不了；
        /// 现在给出三条可操作指引：布局不符、槽不在档案定义内、复合工位还有哪些槽没建方案。
        /// </summary>
        private string DescribeProfileMismatch(CalibrationProfile p)
        {
            try
            {
                var archive = ResolveStationArchive(_scopeStationCode);
                var req = archive?.Requirement;
                if (req == null || p == null) return string.Empty;

                var tips = new List<string>();

                // ① 布局矛盾（最致命：按错布局标定会得到镜像/方向错误的 H）
                //    已标定方案只做"提示"不给"必须改"——现场可能是刻意用另一套布局标的历史方案，
                //    改了反而让现有矩阵失效；措辞指向右侧可编辑的『相机安装方式』下拉。
                string layoutFix = p.IsCalibrated
                    ? "（本方案已标定，改布局会让现有矩阵失效，确认无误再改并重标）"
                    : "";
                if (string.Equals(req.CameraMount, "眼在手外", StringComparison.Ordinal) && p.EyeMode == EyeMode.EyeInHand)
                {
                    tips.Add("⚠ 本方案布局=眼在手上，但工位档案填的是『眼在手外（相机固定）』——"
                             + "请在上方『相机安装方式』下拉改选『眼在手外』，否则 H 会按随动口径推导（镜像/方向错）。" + layoutFix);
                }
                else if (string.Equals(req.CameraMount, "眼在手上", StringComparison.Ordinal) && p.EyeMode == EyeMode.EyeToHand)
                {
                    tips.Add("⚠ 本方案布局=眼在手外，但工位档案填的是『眼在手上（相机随动）』——"
                             + "请在上方『相机安装方式』下拉改选『眼在手上』。" + layoutFix);
                }

                var slots = (req.CameraSlots ?? new List<VisionSlotInfo>())
                    .Select(s => s?.SlotKey?.Trim())
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct()
                    .ToList();

                // 本方案当前生效的槽（含"方案名里嵌的 Cam_*"兜底，避免已标定旧方案被判成未指定）
                string curSlot = EffectiveSlotKey(p);

                // ② 槽无效（不在档案定义内 / 未指定）。复合工位最典型：上下相机都落在 Cam_01。
                //    已标定方案若槽能从名字认出来（如 _Cam_A_）则只提示"核对"而非"错误"，避免误伤。
                bool slotKnownFromName = string.IsNullOrWhiteSpace(p.CameraSlotKey)
                                        && !string.IsNullOrWhiteSpace(curSlot);
                if (slots.Count > 0 && (string.IsNullOrWhiteSpace(curSlot) || !slots.Contains(curSlot)))
                {
                    tips.Add((slotKnownFromName ? "⚠ 本方案未显式记录相机槽（由名称推断为 " + curSlot + "）" : "⚠ 本方案相机槽=" + (string.IsNullOrWhiteSpace(curSlot) ? "未指定" : curSlot))
                             + $"，档案定义的槽为 {string.Join(" / ", slots)}——"
                             + "请在上方『相机槽』下拉（可手填）改对并落盘（复合工位上/下相机必须分槽，否则两条标定产物撞同一身份）。");
                }
                else if (slotKnownFromName && p.IsCalibrated)
                {
                    tips.Add($"ℹ 本方案槽 {curSlot} 由方案名推断（旧档未单独存槽），已自动回填——如与实际相机不符，请在上方『相机槽』下拉改正。");
                }

                // ③ 多槽工位还有哪些槽没建方案
                if (slots.Count >= 2)
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var q in CalibrationProfiles)
                    {
                        if (q == null || !string.Equals(q.BoundStationCode, _scopeStationCode, StringComparison.OrdinalIgnoreCase)) continue;
                        string k = EffectiveSlotKey(q);
                        if (!string.IsNullOrWhiteSpace(k)) used.Add(k.Trim());
                    }
                    var missing = slots.Where(s => !used.Contains(s)).ToList();
                    if (missing.Count > 0)
                    {
                        tips.Add($"ℹ 本工位档案定义了 {slots.Count} 个相机槽（{string.Join(" / ", slots)}），尚未建立方案的槽：{string.Join(" / ", missing)}——"
                                 + "每槽需各自做一次 H 标定：左侧『+ 新建 ▾』里可直接选槽新建，或新建后在上方『相机槽』改成对应槽。");
                    }
                }

                return string.Join("\n", tips);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 按工位档案推导相机安装方式（2026-09-11）。
        /// ⚠ 修复根因：新建方案走 CalibrationProfile 的字段默认值 EyeInHand，而本工位档案
        ///   （ST_002）填的是"眼在手外"（上下相机均固定）→ 推荐出来的方案布局是错的，且旧版界面改不了，
        ///   现场按错误布局标定会得到镜像/方向错误的 H。
        /// 判据序：档案 Requirement.CameraMount → 首槽 InstallKind → 兜底 EyeInHand。
        /// </summary>
        private EyeMode SuggestEyeModeForStation()
        {
            try
            {
                var archive = ResolveStationArchive(_scopeStationCode);
                var req = archive?.Requirement;
                if (req == null) return EyeMode.EyeInHand;
                if (string.Equals(req.CameraMount, "眼在手外", StringComparison.Ordinal)) return EyeMode.EyeToHand;
                if (string.Equals(req.CameraMount, "眼在手上", StringComparison.Ordinal)) return EyeMode.EyeInHand;
                var slot = req.CameraSlots?.FirstOrDefault();
                if (slot != null)
                {
                    var kind = slot.InstallKind ?? string.Empty;
                    bool moving = kind.Contains("眼在手上") || kind.Contains("随执行机构") || kind.Contains("随动");
                    return moving ? EyeMode.EyeInHand : EyeMode.EyeToHand;
                }
                return EyeMode.EyeInHand;
            }
            catch
            {
                return EyeMode.EyeInHand;
            }
        }

        /// <summary>
        /// 按工位档案推导「相机是否随 Z 升降」（2026-09-12）。
        /// ★ G5 轴跟随关系落地：AxisFollows 含 "Z" → 相机随 Z 升降（拍照须回 CalibZ 高度）；
        ///   不含 "Z"（如"跟随XY"）或"固定" → 相机不随 Z（成像与 Z 无关）。
        /// 兜底链：档案首槽 AxisFollows → 空值时返回 null（沿用标定 Profile 默认，由校验台/向导再声明）。
        /// </summary>
        private bool? SuggestCameraMovesWithZForStation()
        {
            try
            {
                var archive = ResolveStationArchive(_scopeStationCode);
                var req = archive?.Requirement;
                if (req == null) return null;
                var slot = req.CameraSlots?.FirstOrDefault();
                var axisFollows = slot?.AxisFollows?.Trim();
                if (string.IsNullOrWhiteSpace(axisFollows)) return null;
                // 含 "Z"（如 跟随XYZU / 跟随XYZ / 跟随XZU）→ 随 Z；否则（固定/跟随XY/跟随X）→ 不随 Z
                return axisFollows.Contains("Z");
            }
            catch
            {
                return null;
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

        /// <summary>为钉住工位创建并持久化一个新方案（名称/归属/建议物理量自动填）</summary>
        private CalibrationProfile CreateProfileForStation(StationNavigationContext context, CalibrationQuantity type)
        {
            var newProfile = new CalibrationProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = GetDefaultNameForType(type, _scopeStationName),
                Quantity = type,
                CameraId = "Cam_01",
                // 2026-09-11：槽独立存并预填工位档案槽（旧逻辑靠 CameraId 猜，绑设备后恒成 Cam_01）
                CameraSlotKey = SuggestSlotKeyForStation(),
                // 2026-09-11：布局按档案推导，不再用字段默认 EyeInHand（固定相机工位会被误判成随动）
                EyeMode = SuggestEyeModeForStation(),
                // 2026-09-12：相机是否随 Z 按档案 AxisFollows 推导（固定相机/跟随XY → 不随 Z），
                // 免去校验台手动声明 CameraMovesWithZ 再广播同步
                CameraMovesWithZ = SuggestCameraMovesWithZForStation(),
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

            // 2026-09-11 旧档回填：CameraSlotKey 是新字段，老方案里为空。
            // 若旧 CameraId 恰好是槽键（"Cam_*"），把它迁到独立槽字段——否则一旦向导绑定物理相机
            // 覆盖 CameraId，槽就永久丢成 Cam_01（复合工位上下相机两条方案会撞同一身份）。
            int slotMigrated = 0;
            foreach (var p in CalibrationProfiles)
            {
                if (p == null || !string.IsNullOrWhiteSpace(p.CameraSlotKey)) continue;
                string cam = (p.CameraId ?? string.Empty).Trim();
                if (cam.StartsWith("Cam_", StringComparison.OrdinalIgnoreCase))
                {
                    p.CameraSlotKey = cam;
                    slotMigrated++;
                    try { SaveProfileToRepository(p); } catch { /* 落盘失败不阻塞加载 */ }
                    continue;
                }
                // 2026-09-11 补充：CameraId 已被物理设备名（Hikvision_…_UpCamera）覆盖的旧方案，
                // 从方案名里嵌的槽键回填（如「引导定位复合工位_Cam_A_九点手眼」→ Cam_A），
                // 否则已标定方案会被判成"槽未指定"，在标定中心/向导里都看不出它属于哪个相机。
                var m = System.Text.RegularExpressions.Regex.Match(p.Name ?? string.Empty, @"Cam_[A-Za-z0-9]+");
                if (m.Success)
                {
                    p.CameraSlotKey = m.Value;
                    slotMigrated++;
                    try { SaveProfileToRepository(p); } catch { /* 落盘失败不阻塞加载 */ }
                }
            }

            if (CalibrationProfiles.Count == 0)
            {
                CalibrationProfiles.Add(new CalibrationProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "工位1_Top相机九点标定",
                    Quantity = CalibrationQuantity.HandEye,
                    BindingInfo = "工位: ST_01 / 平台1",
                    IsCalibrated = false
                });
            }

            SelectedCalibrationProfile = CalibrationProfiles.FirstOrDefault();
            RebuildVisibleProfiles();
            RefreshCameraSlotOptions();
            RefreshPublishRecipeOptions();

            // ★★2026-09-15 单轨存储收口：一次性把历史"设备级"产物（Recipes\Devices\*\Calib\*.tup）
            //   迁到工位级目录，随后把整棵 Recipes\Devices 移入 Archive 归档（不直接删除）。
            //   幂等：目录不存在时零开销；目录被占用（Move 失败）时保留原目录并留痕，不丢数据。
            //   详见 CalibrationMatrixStore.MigrateAndPurgeDeviceScope。
            try
            {
                int migrated;
                int migrateFailed;
                string archivePath;
                if (CalibrationMatrixStore.MigrateAndPurgeDeviceScope(out migrated, out migrateFailed, out archivePath))
                {
                    AppendLog($"[存储收敛] 已废弃设备级标定作用域：迁移 {migrated} 个矩阵到工位级"
                              + (migrateFailed > 0 ? $"，{migrateFailed} 个失败（详见日志）" : "")
                              + (string.IsNullOrEmpty(archivePath)
                                    ? "；目录归档失败 → 原目录保留（下次启动重试）"
                                    : $"；原目录已归档至 {archivePath}"));
                }
            }
            catch (Exception ex)
            {
                AppendLog("[存储收敛] 设备级作用域清理异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 新建标定方案（支持传入标定类型与相机槽）。
        /// slotKey 为空 → 按工位档案建议（复合工位默认第一个槽，多为上相机，需要下相机时请在
        /// 左侧菜单直接选槽、或建完在右侧「相机槽」下拉改）。
        /// </summary>
        private void CreateNewProfile(CalibrationQuantity selectedType = CalibrationQuantity.HandEye, string slotKey = null)
        {
            var opt = CalibrationTypeOptions.FirstOrDefault(o => o.Type == selectedType);
            if (opt != null && !opt.IsAvailable)
            {
                MessageBox.Show($"「{opt.DisplayName}」标定尚未实现（TODO），当前不可新建。",
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
                Quantity = selectedType,
                CameraId = "Cam_01",
                // 2026-09-11：槽与设备解耦——预填工位档案的槽，避免复合工位新建方案恒落在 Cam_01
                // （CameraId 会在向导绑物理相机时被设备名覆盖，槽必须独立存）。
                // 左侧菜单带槽新建时以菜单选择为准（复合工位建下相机方案的关键入口）。
                CameraSlotKey = IsStationScope ? (string.IsNullOrWhiteSpace(slotKey) ? SuggestSlotKeyForStation() : slotKey.Trim()) : null,
                AxisId = "Axis_X",
                UpdatedAt = DateTime.Now
            };

            if (IsStationScope)
            {
                newProfile.BoundStationCode = _scopeStationCode;
                newProfile.BoundDeviceId = SelectedCalibrationProfile?.BoundDeviceId;
                newProfile.BindingInfo = $"工位: {_scopeStationName} ({_scopeStationCode})";
                // 2026-09-11：手动新建同样按档案推导布局（默认 EyeInHand 会把固定相机工位判成随动）
                newProfile.EyeMode = SuggestEyeModeForStation();
            }

            CalibrationProfiles.Add(newProfile);
            SelectedCalibrationProfile = newProfile;
            VisibleProfiles.Add(newProfile);
            SaveProfileToRepository(newProfile);
        }

        /// <summary>选中方案变化时同步下拉选中项（直接赋值字段，不触发切换逻辑）</summary>
        private void SyncTypeOptionToProfile()
        {
            var type = SelectedCalibrationProfile?.Quantity ?? CalibrationQuantity.HandEye;
            _selectedProfileTypeOption = CalibrationTypeOptions.FirstOrDefault(o => o.Type == type);
            OnPropertyChanged(nameof(SelectedProfileTypeOption));
            (PublishEccCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 切换当前方案的标定物理量（2026-09-12：从旧 CalibrationType 切换语义改为 CalibrationQuantity）。
        /// 切换后持久化；原标定结果标记未标定需重跑向导。
        /// </summary>
        private void ChangeProfileType(CalibrationQuantity newType)
        {
            var profile = SelectedCalibrationProfile;
            if (profile == null) return;

            var opt = CalibrationTypeOptions.FirstOrDefault(o => o.Type == newType);
            if (opt != null && !opt.IsAvailable)
            {
                MessageBox.Show($"「{opt.DisplayName}」标定尚未实现（TODO），当前不可切换。",
                    "类型未实现", MessageBoxButton.OK, MessageBoxImage.Warning);
                SyncTypeOptionToProfile();
                return;
            }

            profile.Quantity = newType;

            // 原标定结果不再适用 → 标记未标定（列表红点），提示重新执行
            if (profile.IsCalibrated)
            {
                profile.IsCalibrated = false;
                MessageBox.Show(
                    $"标定物理量已切换为「{GetQuantityDisplayName(newType)}」。\n原标定结果已标记为未标定，请重新运行标定向导完成标定。",
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

        /// <summary>发布条件（详见 CanPublishEcc）：方案带旋转几何证据（O/U0/e）、已绑工位、吸嘴号 1/2。
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

        /// <summary>
        /// 发布条件：方案带有可发布的【旋转几何证据】、已绑工位、吸嘴号 1/2。
        /// ★ 2026-09-10 放开类型门槛：原先硬要求 Type=吸放式(PickPlaceHandEye)——那是"只有吸放式才产出
        /// 旋转几何"时代的假设。现行标定体系里旋转几何（O/U0/e）由 e 段会话独立产出，H 段走位式
        /// （固定相机 + 延伸杆）与吸放式都能给出，故改为按【几何证据】判定而非方案类型：
        ///   · HandEyeWithRotation（九点+旋转）/ PickPlaceHandEye / 带旋转结果的 NinePointHandEye 一律放行；
        ///   · 同心吸嘴 e≈0 也允许发布——把 e=0 与 U0/O 一并写回，顺带清零旧残留值（不再因"e 为零"卡住）。
        /// </summary>
        private bool CanPublishEcc()
        {
            var p = SelectedCalibrationProfile;
            if (p == null || string.IsNullOrWhiteSpace(p.BoundStationCode))
            {
                return false;
            }
            string nozzle = string.IsNullOrEmpty(p.NozzleKey) ? "1" : p.NozzleKey;
            if (nozzle != "1" && nozzle != "2")
            {
                return false; // 业务进程仅支持吸嘴1/2 字段
            }
            return HasRotationGeometry(p) || HasPublishableGeometry(out _, out _);
        }

        /// <summary>
        /// 旋转几何证据（e 段会话产出，与 CalibrationCardDeriver.HasRotationEvidence 同判据）：
        /// 基准角 U0 / 回转中心 O / 真吸嘴偏心 e 任一在位即算"跑过旋转段"。
        /// 用于发布门（判定能否把几何写回工位业务配置），不要求 e 非零（同心吸嘴 e=0 合法）。
        /// </summary>
        private static bool HasRotationGeometry(CalibrationProfile p)
        {
            if (p == null) return false;
            return p.CalibU0.HasValue
                   || p.HasRotationCenter
                   || p.IsNozzleEccCalibrated
                   || Math.Abs(p.ToolCenterWx) > 0.0001 || Math.Abs(p.ToolCenterWy) > 0.0001
                   || Math.Abs(p.ToolOffsetPureWx) > 0.0001 || Math.Abs(p.ToolOffsetPureWy) > 0.0001;
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
                PublishEccStatusText = "⚠ 无可发布几何源：需 已绑工位、吸嘴号 1/2，且该方案已完成【旋转中心标定(e)】（有 O / U0 / 偏心 e 任一）或【物理对针】。";
                return;
            }
            if (!HasPublishableGeometry(out bool hasTco, out bool hasEcc) && !HasRotationGeometry(p))
            {
                PublishEccStatusText = "⚠ 无可发布几何源（回转中心 O / 基准角 U0 / 吸嘴偏心 e 均无有效值）——请先完成旋转中心标定（e 段会话）。";
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
                //   ★★2026-09-15 修正：必须【同相机槽】。复合工位上下相机同工位不同槽，旧谓词
                //   "同工位即可继承"会把 Cam_A 的 O 发布给 Cam_C（上下相机张冠李戴，且发布的
                //   RotCenterW/PhotoBase 直接进生产配置 → 生产按错误旋转中心走，无声无息）。
                string inheritNote = string.Empty;
                if (!p.HasRotationCenter && !string.IsNullOrWhiteSpace(p.BoundStationCode))
                {
                    var sib = CalibrationProfiles
                        .Where(x => x != null && !ReferenceEquals(x, p)
                                    && x.HasRotationCenter
                                    && string.Equals(x.BoundStationCode, p.BoundStationCode, StringComparison.OrdinalIgnoreCase)
                                    && IsSameCameraSlotStrict(x, p))
                        .OrderByDescending(x => x.UpdatedAt)
                        .FirstOrDefault();
                    if (sib != null)
                    {
                        p.ToolCenterWx = sib.ToolCenterWx;
                        p.ToolCenterWy = sib.ToolCenterWy;
                        p.HasRotationCenter = true;
                        inheritNote = $"\n· 旋转中心 O 从同工位同槽档案「{sib.Name}」（槽={SlotText(sib)}）继承 = ({sib.ToolCenterWx:F3},{sib.ToolCenterWy:F3})";
                    }
                }

                var fields = new System.Collections.Generic.Dictionary<string, object>();

                // ★★2026-09-15：本档案是否【下相机】产物。判定放在最前，因为下面的"上相机几何字段写回"
                //   要对它整体跳过——否则发布下相机会把 Cam_A 已发布的 e/U0/O/位点全清零
                //   （工位过程配置只有一套扁平字段，没有相机槽维度），而这一步换不到任何收益：
                //   下相机不走绝对定位、只做相对纠偏，上面那些字段对它毫无意义。
                bool isDownCamArchive = p.PrimaryPath == CalibrationAcquirePath.DownCameraWalk
                                        || p.PrimaryPath == CalibrationAcquirePath.DownCameraPixelRotCenter;

                // ★ 2026-09-08 定案：发布【真吸嘴偏心 e】= ToolOffsetPureW（= O − H(p_tip)），
                //   而不是 ToolEccW（偏心延伸杆 Mark 偏心 = −m，不是吸嘴偏心）。
                bool hasPureEcc = p.IsNozzleEccCalibrated
                                  && (Math.Abs(p.ToolOffsetPureWx) > 0.0001 || Math.Abs(p.ToolOffsetPureWy) > 0.0001);
                if (!hasPureEcc && !string.IsNullOrWhiteSpace(p.BoundStationCode))
                {
                    // 同理：真吸嘴偏心 e 也可能在同工位的另一份档案里（老档案升级场景）
                    // ★★2026-09-15 修正：必须【同相机槽】。上相机 e（RotateCameraView，机械域拟合）与
                    //   下相机 e（DownCameraPixelRotCenter，像素域拟合）是**不同物理量**，跨槽继承不只是
                    //   换个数，而是换了个定义 ⇒ 必须挡掉。
                    var sibE = CalibrationProfiles
                        .Where(x => x != null && !ReferenceEquals(x, p)
                                    && x.IsNozzleEccCalibrated
                                    && string.Equals(x.BoundStationCode, p.BoundStationCode, StringComparison.OrdinalIgnoreCase)
                                    && IsSameCameraSlotStrict(x, p))
                        .OrderByDescending(x => x.UpdatedAt)
                        .FirstOrDefault();
                    if (sibE != null)
                    {
                        p.ToolOffsetPureWx = sibE.ToolOffsetPureWx;
                        p.ToolOffsetPureWy = sibE.ToolOffsetPureWy;
                        p.IsNozzleEccCalibrated = true;
                        hasPureEcc = true;
                        inheritNote += $"\n· 真吸嘴偏心 e 从同工位同槽档案「{sibE.Name}」（槽={SlotText(sibE)}）继承 = ({sibE.ToolOffsetPureWx:F3},{sibE.ToolOffsetPureWy:F3})";
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
                // ★2026-09-12：X_obj 分型从"有 O 就写 EIH"修正为三分型，消除"固定相机+延伸杆标定"
                //   被误当 EIH（会回拍照位）/或误当纯 ETH（漏 O 补偿）的消费错位。
                //   ★2026-09-15 二修：固定相机【不再叠 O】。理由（可证伪）：
                //     O 补偿式 X_obj = P_photo + O − H(u) 里含【拍照机位 P_photo】，只有相机随机械手走
                //     (EIH) 时才成立；固定相机下工件在台面上不动，混进 P_photo 在物理上说不通。
                //     实测反证（本工位）：X_obj=(307.08,−16.85)mm，Y 比工作区(≈97~124)低约 120mm，飞出可达域。
                //     平台自己的约定（CalibrationAcquirePath.CameraTruthWalk）也写明："真值=相机中心，
                //     工具尖偏距需 **t** 补"——补的是 b（杆端→吸嘴），不是 O/e。
                //     故固定相机+杆端域的生产口径 = 吸点 H(u) + b；b 未发布前生产端退化为 H(u)（与本改动前
                //     实际行为一致，零回归），但发布提示会明说"少补了杆端偏心"。
                //   ① EIH 眼在手            → CameraMountEih=true（需回拍照位 + 叠 O，NeedsOCompensation=true）
                //   ② ETH 固定相机+延伸杆标定 → 两者都 false（不回拍照位、不叠 O；杆端偏距靠 b，见下方 RodOffset 字段）
                //   ③ ETH 固定相机直接拍工件   → 两者都 false（直吸 H(u)）
                bool isEih = p.EyeMode == EyeMode.EyeInHand;
                bool rodAssistedEth = p.EyeMode == EyeMode.EyeToHand
                                      && p.PrimaryPath == CalibrationAcquirePath.CameraTruthWalk;
                if (p.HasRotationCenter)
                {
                    fields["RotCenterWx"] = (float)p.ToolCenterWx;
                    fields["RotCenterWy"] = (float)p.ToolCenterWy;
                    fields["PhotoBaseX"] = (float)p.BasePosX;
                    fields["PhotoBaseY"] = (float)p.BasePosY;
                    fields["CameraMountEih"] = isEih;                     // 只有真 EIH 才回拍照位
                    fields["NeedsOCompensation"] = isEih;                 // ★只有 EIH 才叠 O（固定相机叠 O 是错的）
                }
                else
                {
                    fields["CameraMountEih"] = false; // 无 O → 固定相机(ETH)语义 X_obj=H(u)
                    fields["NeedsOCompensation"] = false;
                }

                // ★★2026-09-15：口径判定改为调【消费口径契约】——三方唯一判据。
                //   原先这里是本文件自己的一份 if/else（nozzleDomain / coaxial / rodAssistedEth），
                //   校验台有一份、生产端 PickAnchor 还有一份 ⇒ 三者【靠巧合一致】。
                //   现场症状：校验台反复压中、一上生产就偏一个 |b|（是口径不同，不是精度不够）。
                //   现在发布链、校验台、生产端三方都只调 CalibrationConsumptionContract.Resolve。
                var slotKeyOfP = CalibrationProfileSessionPlanner.GuessSlotKey(p);
                CameraCalibrationBundle slotBundle = null;
                try
                {
                    slotBundle = CameraCalibrationBundle.Build(
                        CalibrationProfiles, p.BoundStationCode, slotKeyOfP,
                        (double px, double py, out double mwx, out double mwy, out string merr) =>
                        {
                            mwx = mwy = 0; merr = null;
                            var mr = _calibService.MapPixelToWorld(p.HomMatFilePath, px, py);
                            if (mr == null || !mr.Success) { merr = mr?.Message ?? "矩阵映射失败"; return false; }
                            mwx = mr.Data.WorldX; mwy = mr.Data.WorldY;
                            return true;
                        });
                }
                catch (Exception ex)
                {
                    // 聚合失败不阻断发布：退化为"只用当前档案判定"，并留痕（绝不静默）
                    AppendLog($"[发布] ⚠ 消费口径聚合失败，本次只用当前档案判定：{ex.Message}");
                }

                var dec = (slotBundle != null && slotBundle.H != null)
                    ? slotBundle.ResolveDecision()
                    : CalibrationConsumptionContract.Resolve(
                        BuildConsumptionInputsFromProfile(p), CalibrationSolveMode.FromProfile);

                AppendLog($"[发布] 口径判定（档案「{p.Name}」槽={slotKeyOfP ?? "未指定"}，"
                          + $"固定相机+延伸杆候选={rodAssistedEth}，"
                          + $"走{(slotBundle != null && slotBundle.H != null ? "槽级聚合" : "单档案")}）：\n"
                          + CalibrationConsumptionContract.DescribeDecision(dec));

                bool nozzleDomain = dec.NozzleDomain;
                bool coaxial = p.NozzleAxisCoaxial == true;

                // 口径由契约判定 ⇒ 这里只负责把它翻成扁平字段（不再自己判分型）
                fields["HandEyeInNozzleDomain"] = nozzleDomain;
                fields["NozzleAxisCoaxial"] = coaxial;
                fields["CameraMountEih"] = dec.NeedO;         // 只有 EIH 才回拍照位
                fields["NeedsOCompensation"] = dec.NeedO;     // ★只有 EIH 才叠 O（固定相机叠 O 是错的）

                if (nozzleDomain)
                {
                    inheritNote += "\n· 口径=①吸嘴域直吸 → 生产 X_obj=H(u)（NeedsOCompensation=false、不回拍照位）";
                    if (p.HasRotationCenter)
                    {
                        PublishEccStatusText = "⚠ 该档案同时声明「H 已在吸嘴域」且带旋转中心 O——本次按吸嘴域直吸发布（不叠 O）。"
                                             + "若 H 其实是未消杆的原始矩阵，请改声明为「杆端域」后重新发布，否则会少补偿一个杆偏心。";
                    }
                }
                if (coaxial)
                {
                    inheritNote += "\n· 档案声明「吸嘴与 U 轴同轴」→ 生产免 R(U−U0)·e 项（U 只决定姿态）";
                }

                // ★2026-09-15：把 b（杆端→吸嘴偏移）发布给生产端 —— 生产 PickAnchor 的③号路径。
                //   b 的来源解析与校验台共用 CalibrationConsumptionContract.ResolveRodOffset（t 优先）。
                // ⚠ 开关默认【关】：b 的**符号**要靠现场 A/B 判定（选错偏 2|b|，比不补更糟）。
                //   把档案声明 RodOffsetInProduction 置 true 并重新发布，生产端才会走 H(u)+b。
                // ★★2026-09-16：b 的符号闸。
                //   符号是口径的一部分，且**选错的代价比不补更大**：偏 2|b|（本工位 264mm）而不是 |b|。
                //   而 RodOffsetSign 全平台此前没有任何界面 ⇒ 现场即使做了 A/B 也写不进去，
                //   只能一直吃默认 +1 —— "判得出、说不进"。这里补上唯一入口：
                //   首次把 b 带进生产时**必须回答符号**，答案写进工位配置；此后尊重现值、不再追问
                //   （保留下方"发布链刻意不写它防覆盖"的原意图）。
                double? explicitRodSign = null;

                if (dec.RodOffsetTerm && !nozzleDomain)
                {
                    // ★★2026-09-15 修正【不同源接缝】：b / t 必须取【槽级 bundle】，不能取被发布档案自身。
                    //   被发布的通常是"H 档案"，而 H 回调（ProcessCalibrationResult）只写 H、从不写 ToolEccW
                    //   ⇒ H 档 ToolEccW 恒 (0,0)；旧写法传 p.ToolEccWx/Wy ⇒ bMag=0 ⇒ enableB 恒 false
                    //   ⇒ 现场即便把 RodOffsetInProduction 置 true，b 也永远发不进工位配置（静默失效）。
                    //   实测（2026-09-15 18:50 档案）：Cam_A「手眼 H」档 ToolEccW=(0,0)，
                    //                                 同槽「旋转中心 e」档 ToolEccW=(5.943162,132.171930)。
                    //   槽级取值 = 与校验台 / 生产端 CameraCalibrationBundle.RodOffsetWx **完全同源**。
                    double bSrcWx = slotBundle != null ? slotBundle.RodOffsetWx : p.ToolEccWx;
                    double bSrcWy = slotBundle != null ? slotBundle.RodOffsetWy : p.ToolEccWy;
                    bool bHasCand = slotBundle != null && slotBundle.HasRodOffset;
                    double tSrcWx = slotBundle != null ? (slotBundle.T?.ToolOffsetWx ?? 0.0) : p.ToolOffsetWx;
                    double tSrcWy = slotBundle != null ? (slotBundle.T?.ToolOffsetWy ?? 0.0) : p.ToolOffsetWy;
                    bool tDirect = slotBundle != null
                        ? slotBundle.HasEthToolOffset
                        : p.ToolOffsetMethod == ToolOffsetMethod.EyeToHandImage;
                    var rod = CalibrationConsumptionContract.ResolveRodOffset(
                        tSrcWx, tSrcWy, tDirect, bSrcWx, bSrcWy, bHasCand);
                    double bx = rod.Bx, by = rod.By;
                    double bMag = Math.Sqrt(bx * bx + by * by);
                    // ★★2026-09-15：开关按【槽】聚合，不取被发布档案自身 ——
                    //   扁平字段下同槽 H/e 各发一次、后者覆盖前者，只看自己会被静默抹回 false。
                    bool rodSwitchOn = slotBundle != null
                        ? slotBundle.RodOffsetInProductionDeclared
                        : p.RodOffsetInProduction == true;
                    bool enableB = rodSwitchOn && bMag > 1.0;
                    fields["HasRodOffset"] = enableB;
                    fields["RodOffsetWx"] = (float)bx;
                    fields["RodOffsetWy"] = (float)by;

                    // ★★2026-09-16：符号闸（首次接入 b 时必须回答，避免"偏 2|b| 撞机"）
                    //
                    // 判据用 RodOffsetSignDeclared，**不是** RodOffsetSign 键在不在 —— 两个原因叠加：
                    //   ① 符号的【值】代码默认就是 1f ⇒ "键缺席取默认"与"现场判定过 +1"在数值上无法区分；
                    //   ② 发布走 SetFields(..., codeDefaults) 的**同值剪枝**：现场选 +1 写进去的值
                    //      与代码默认相同 ⇒ **被静默剪掉、键根本落不了盘**（选 −1 却写得进 ⇒ 症状不对称）。
                    //   ⇒ 用一个默认 false 的布尔当"判定过"的标志：写 true 永远 ≠ 默认，绝不被剪枝。
                    bool rodSignDecided = ProcessConfigOverlay
                        .GetKeys(station.ProcessConfigJson)
                        .Any(k => string.Equals(k, "RodOffsetSignDeclared", StringComparison.OrdinalIgnoreCase));
                    // 兼容老版本发布（只写了 RodOffsetSign、没有标志位）：那种状态**无法区分**
                    //   "现场选的就是 +1" 与 "从没人选过、只是吃了默认" ⇒ 既然分不清就再问一次。
                    //   代价是一次确认；代价不对称（猜错偏 2|b|）。答完补上标志位，从此不再追问。
                    if (enableB && !rodSignDecided)
                    {
                        var sgn = MessageBox.Show(
                            "『杆端→吸嘴偏移 b』的【符号】还没有现场验证过"
                            + "（工位配置里没有 RodOffsetSignDeclared 标志，当前只能吃代码默认 +1）。\n\n"
                            + $"b = ({bx:F3},{by:F3})mm   |b| = {bMag:F3}mm\n"
                            + $"符号选错的后果 = 落点偏 2|b| = {2 * bMag:F3}mm —— 比不补更危险（会撞机）。\n\n"
                            + "判定法（校验台·同一槽）：对本槽做一次 A/B —— 用【口径④ 吸点=H(u)+b】与【口径⑤ 吸点=H(u)−b】"
                            + "各点同一个像素、各低速到位一次，只有一个是「吸嘴尖正好落在点上」。\n\n"
                            + "本次发布写入哪个符号？\n"
                            + "  [是] = +b（H(u)+b）      [否] = −b（H(u)−b）\n"
                            + "  [取消] = 还没判过 ⇒ 先不发布（推荐先去校验台判死）",
                            "b 的符号（未经现场验证）", MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Warning, MessageBoxResult.Cancel);
                        if (sgn == MessageBoxResult.Cancel)
                        {
                            PublishEccStatusText = "已取消发布：b 的符号未判定（避免偏 2|b| 撞机）。"
                                                 + "请先在校验台用口径 ④/⑤ 对同一像素做 A/B 判死，再回来发布。";
                            AppendLog("[发布] 已取消：b 符号未判定。判定法=校验台口径 ④/⑤ 各点同一像素，只有一个让吸嘴尖落在点上。");
                            return;
                        }
                        explicitRodSign = sgn == MessageBoxResult.Yes ? 1.0 : -1.0;
                        fields["RodOffsetSign"] = (float)explicitRodSign.Value;
                        // ★ 关键：标志位默认 false，写 true 永远不等于默认 ⇒ 不会被 SetFields 的同值剪枝吞掉。
                        //   少了这一行，"现场选 +1"就永远落不了盘（而下一次发布又会再问一遍，永不收敛）。
                        fields["RodOffsetSignDeclared"] = true;
                        inheritNote += $"\n· 已把现场选定的 b 符号写入工位配置：RodOffsetSign = "
                                     + $"{(explicitRodSign.Value > 0 ? "+1" : "-1")}，"
                                     + "并标记 RodOffsetSignDeclared = true（写后生产端与校验台同符号）。";
                    }
                    // 注意：只有【符号闸问了并得到答案】时才写 RodOffsetSign 与标志位（见上），其余情况一律不写 ——
                    //   让它保持工位配置里的现值。若这里每次无条件写 1f，现场判出的 −1 会被下一次发布静默覆盖回 +1（A/B 白做）。
                    string bSrcText = rod.Src ?? "（无来源）";
                    if (enableB)
                    {
                        // ⚠ 文案纪律：不许把"取值"写成"已判定"。标志位才是判据（见上面的符号闸）。
                        string signText = explicitRodSign.HasValue
                            ? $"{(explicitRodSign.Value > 0 ? "+1" : "-1")}（本次发布已写入并标记为【已判定】）"
                            : (rodSignDecided
                                ? "沿用工位配置现值（此前已标记【已判定】）"
                                : "⚠ 仍未标记【已判定】—— 生产端口径闸会硬拦");
                        inheritNote += $"\n· b 已接入生产：X_obj = H(u) + b，b=({bx:F3},{by:F3})mm |b|={bMag:F3}mm "
                                     + $"来源[{bSrcText}] 符号={signText} → 生产与校验台同口径。";
                    }
                    else if (bMag > 1.0)
                    {
                        inheritNote += $"\n⚠ 本工位是【固定相机+延伸杆】标定：H 只到杆端 mark。"
                                     + $"b=({bx:F3},{by:F3})mm |b|={bMag:F3}mm 来源[{bSrcText}] 数值已写入工位配置，"
                                     + "但【本槽内没有任何档案声明 RodOffsetInProduction=true】⇒ HasRodOffset 写 false，"
                                     + "生产仍按 X_obj=H(u) 走，会比正确落点少一个 |b|；"
                                     + "而校验台按 H(u)+b 算 ⇒ **校验台压中 ≠ 生产压中**。"
                                     + "请先在校验台验收（反复验到吸嘴压中、最好是量具复核 |b|），"
                                     + "再把档案声明 RodOffsetInProduction 置 true 并重新发布，生产即同口径"
                                     + "（⚠ 同槽 H/e 都要发的话，最后发的那份须带着 true，否则会被覆盖回 false）。";
                    }
                    else
                    {
                        // ★2026-09-15 新增分支（"不同源接缝"的现场症状，必须报红，不许静默发 0）：
                        //   判为固定相机+杆端域，却一个 b 的来源都取不到 ⇒ 发出去的 HasRodOffset 只能是 false。
                        inheritNote += $"\n⛔ b 取不到数值（b=(0,0)，来源[{bSrcText}]）⇒ 本次已写入 HasRodOffset=false，"
                                     + "生产端按 X_obj=H(u) 走，会少一个 |b|。\n"
                                     + "   本工位被判为【固定相机 + 杆端域】，b 应由【同相机槽】的 e（旋转中心）档案的 ToolEccW 提供"
                                     + $"（或对针 t 直量）；本次槽级来源 = 「{slotBundle?.RodOffsetSource ?? "（无 e 档）"}」。\n"
                                     + "   请确认该槽的 e 档案存在且 ToolEccW 非零（口径判据本身正确，缺的是 b 的数值源）。";
                    }
                }
                fields["CalibPlaceU"] = (float)p.PickBaseU;
                fields["TeachMode"] = false;

                // ★★2026-09-15：下相机档案的发布从"纯破坏"变成"真的有用"。
                //   它现在写出生产端相对纠偏所需的常量（这也是"下相机标定好了却用不起来"的正解）：
                //     · R_cdown（像素）           —— 来源：槽内 【下相机像素旋转中心】会话产物
                //     · H_down(R_cdown)（机械位） —— 用下相机 H 矩阵把 R_cdown 映射出来（矩阵不变即常量）
                //     · CalibZ                    —— 下相机九点的标定高度，供生产端预检比对 DownCameraZ
                //   生产端由此能把 δ 从"恒 ≈0 的退化式"换成与校验台同一个算式：
                //     δ = H_down(R_img) − H_down(R_cdown)
                if (isDownCamArchive)
                {
                    var rcp = slotBundle?.DownRotCenterProfile;
                    bool wroteAxis = false;
                    if (rcp != null && rcp.DownRotCenterCol.HasValue && rcp.DownRotCenterRow.HasValue)
                    {
                        fields["DownCameraRotCenterCol"] = (float)rcp.DownRotCenterCol.Value;
                        fields["DownCameraRotCenterRow"] = (float)rcp.DownRotCenterRow.Value;
                        string hPath = slotBundle?.H?.HomMatFilePath;
                        var ar = string.IsNullOrWhiteSpace(hPath)
                            ? null
                            : _calibService.MapPixelToWorld(hPath, rcp.DownRotCenterCol.Value, rcp.DownRotCenterRow.Value);
                        if (ar != null && ar.Success)
                        {
                            fields["DownCameraAxisWx"] = (float)ar.Data.WorldX;
                            fields["DownCameraAxisWy"] = (float)ar.Data.WorldY;
                            wroteAxis = true;
                            inheritNote += $"\n· 下相机轴投影常量已发布：H_down(R_cdown)=({ar.Data.WorldX:F3},{ar.Data.WorldY:F3})mm"
                                         + $"（R_cdown=({rcp.DownRotCenterCol.Value:F2},{rcp.DownRotCenterRow.Value:F2})px，来源「{rcp.Name}」）"
                                         + " → 生产端 δ 改走与校验台同一个算式 δ=H_down(R_img)−H_down(R_cdown)。";
                        }
                        else
                        {
                            inheritNote += $"\n⛔ 像素旋转中心映射失败（下相机 H 矩阵不可用或路径为空）：{ar?.Message ?? "矩阵路径为空"} —— "
                                         + "轴投影常量未发布。";
                        }
                    }
                    if (!wroteAxis)
                    {
                        inheritNote += "\n⛔ 未能发布『下相机轴投影常量』：槽内缺已标定的像素旋转中心 R_cdown（或下相机 H 矩阵不可映射）。"
                                     + "⇒ 生产端位置纠偏仍不会真正生效（δ 退化成恒 ≈0 的旧式）。"
                                     + "处方：先跑一次【下相机像素旋转中心】标定会话，再回到这里发布。";
                    }
                    var hProf = slotBundle?.H ?? p;
                    if (hProf.CalibZ.HasValue && Math.Abs(hProf.CalibZ.Value) > 1e-6)
                    {
                        fields["DownCameraCalibZ"] = (float)hProf.CalibZ.Value;
                        inheritNote += $"\n· 下相机标定高度 CalibZ={hProf.CalibZ.Value:F3} 已发布（供生产端预检比对 DownCameraZ）。";
                    }
                }

                // ★★2026-09-15（第 6 处"同工位≠同相机"）：发布前【覆盖对账】。
                //   工位过程配置是【一套扁平字段】（没有相机槽维度），而复合工位有两台相机各自的档案：
                //   发布 Cam_C（下相机）的档案会把 Cam_A 已发布的 Nozzle1EccX/Y、ToolAlignU、CalibPlaceU
                //   覆盖成 0 —— 这一步会白白毁掉上相机的生产几何。
                //   ★2026-09-15 更新：生产端**已经有**下相机消费了（见 VisionPickPlaceProcess.DownCameraCorrectAsync
                //   的 δ=H_down(R_img)−H_down(R_cdown)），所以发布下相机**不再是无用功**——但有用的只有
                //   下相机那几个常量，上相机口径的字段仍旧与它无关。故这里直接【剔除】上相机字段，
                //   而不是写完再问用户"要不要覆盖"。剩下的多槽相残仍按原纪律弹窗 + 留痕。
                if (isDownCamArchive)
                {
                    // 剔除哪些上相机字段，在这里列全（可审计）；下相机的常量字段一律保留
                    // ★2026-09-16 补入 RodOffsetSign / RodOffsetSignDeclared：下相机档案不消费 b，
                    //   也就没有"符号"可言 —— 上相机口径键的清单必须把它俩也算进去（否则口径键集合
                    //   在"下相机发布"这条路上就不是封闭的，将来加新的 b 相关键还会再漏一次）。
                    var upCamKeys = new[]
                    {
                        eccXField, eccYField, tcoXField, tcoYField,
                        "ToolAlignU", "RotCenterWx", "RotCenterWy", "PhotoBaseX", "PhotoBaseY",
                        "CameraMountEih", "NeedsOCompensation", "HandEyeInNozzleDomain", "NozzleAxisCoaxial",
                        "HasRodOffset", "RodOffsetWx", "RodOffsetWy", "CalibPlaceU",
                        "RodOffsetSign", "RodOffsetSignDeclared",
                    };
                    var dropped = upCamKeys.Where(k => fields.Remove(k)).ToList();
                    AppendLog($"[发布] 本次为【下相机】档案：已跳过 {dropped.Count} 个上相机口径字段"
                              + $"（{string.Join("、", dropped)}）—— 不再清零上相机几何，只写下相机纠偏常量。");
                }

                // ★★把"即将写入的字段"回代成生产端**将读到的口径**，写成留痕标签。
                //   做法：用生产端同一个反读函数 FromPublishedConfig 算一遍 ⇒ 标签与生产端实读必然同源。
                //   之后任何"被别的相机覆盖 / 被手工改库"都会让生产端的对账失败并报 ERROR（口径漂移不许静默）。
                bool wNozzle = fields.TryGetValue("HandEyeInNozzleDomain", out var vN) && vN is bool bN && bN;
                bool wNeedO = (fields.TryGetValue("NeedsOCompensation", out var vO) && vO is bool bO && bO)
                              || (fields.TryGetValue("CameraMountEih", out var vM) && vM is bool bM && bM);
                bool wRod = fields.TryGetValue("HasRodOffset", out var vR) && vR is bool bR && bR;
                double wSign = explicitRodSign ?? 1.0;
                if (explicitRodSign == null)
                {
                    var baseCfg = ProcessConfigOverlay.LoadEffective<VisionPickPlaceConfig>(station.ProcessConfigJson);
                    wSign = baseCfg?.RodOffsetSign ?? 1f;
                }
                var publishedDec = CalibrationConsumptionContract.FromPublishedConfig(
                    isDownCamera: isDownCamArchive,
                    handEyeInNozzleDomain: wNozzle,
                    needsOCompensation: wNeedO,
                    cameraMountEih: wNeedO,
                    hasRodOffset: wRod,
                    nozzleAxisCoaxial: coaxial,
                    rodOffsetSign: wSign);
                fields["ConsumptionTag"] = publishedDec.Tag;
                fields["ConsumptionFormula"] = publishedDec.Formula;
                AppendLog($"[发布] 口径留痕：将写入 ConsumptionTag=[{publishedDec.Tag}] 算式=[{publishedDec.Formula}]"
                          + "（生产端开工时会用它对自己实读的口径做对账，不一致即报 ERROR）。");

                bool multiSlot = CalibrationProfiles
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.CameraSlotKey)
                                && string.Equals(x.BoundStationCode, p.BoundStationCode, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.CameraSlotKey.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
                var ow = DescribePublishOverwrite(station.ProcessConfigJson, codeDefaults, fields);
                if (!string.IsNullOrWhiteSpace(ow.Text))
                {
                    AppendLog($"[发布] 覆盖对账（工位 {p.BoundStationCode}，档案「{p.Name}」槽={SlotText(p)}，多槽={multiSlot}）：\n{ow.Text}");
                    // 只在【真会造成伤害】时打断：多槽工位里把非零现值清零（跨相机覆盖）。
                    //   下相机档案已不再写上相机字段 ⇒ 不再需要为它单独弹窗。
                    if (multiSlot && ow.HasErase)
                    {
                        string head = $"⚠ 本次发布会把工位「{p.BoundStationCode}」已发布的几何值清零/改写"
                                    + "（该工位存在多个相机槽，而过程配置只有一套字段）：\n";
                        var mbr = MessageBox.Show(
                            head + "\n以下字段将被改写（现值 → 将写值）：\n" + ow.Text
                            + "\n\n继续发布？（选\"否\"则不写入任何字段）",
                            "发布覆盖对账", MessageBoxButton.YesNo,
                            MessageBoxImage.Warning, MessageBoxResult.No);
                        if (mbr != MessageBoxResult.Yes)
                        {
                            PublishEccStatusText = "已取消发布（覆盖对账未确认，工位配置未变更）。";
                            return;
                        }
                    }
                }

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
                    // ★★2026-09-16：这里原先硬编码的是 EIH 两行公式
                    //   （"X_obj = 拍照机位 + 旋转中心O − H(像素)" / "P_go = X_obj − R(作业U − U0)·e"），
                    //   对固定相机+杆端域（本工位 Cam_A，|b|≈132mm）**完全是错的**。
                    //   "硬编码文案会替错误背书"：现场照着这行去核对，就会以为生产端走的是 H(u)+b。
                    //   现在改为按本次**实际发布的口径标签**动态生成（与生产端实读同源同一字符串）。
                    $"进程消费口径（与校验台同一真源契约）：\n" +
                    $"  {publishedDec.KindText}：{publishedDec.Formula}\n" +
                    $"  留痕标签 ConsumptionTag = {publishedDec.Tag}\n" +
                    "  ⚠ 本次发布已把口径声明一并写入工位配置；生产端开工时会【反读该声明】并与本标签对账，\n" +
                    "     且会与标定档案再核一次——两侧不一致会直接拦下不许运动（不再静默换档）。\n" +
                    "可到工位工程工作台 S3 业务面板【示教模式/验证落点】实测确认。",
                    "发布标定几何到业务配置", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                PublishEccStatusText = $"❌ 发布失败: {ex.Message}";
                MessageBox.Show($"发布标定几何到业务配置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// ★★2026-09-15：把"当前要发布的这份档案"摊成口径判定输入（槽级聚合不可用时的退化路径）。
        /// 与 <see cref="CameraCalibrationBundle.BuildInputs"/> 一一对应——两处填的是同一个 DTO，
        /// 判定函数（CalibrationConsumptionContract.Resolve）只有一份。
        /// </summary>
        private ConsumptionInputs BuildConsumptionInputsFromProfile(CalibrationProfile p)
        {
            bool hasEcc = p.IsNozzleEccCalibrated
                          && (Math.Abs(p.ToolOffsetPureWx) > 1e-9 || Math.Abs(p.ToolOffsetPureWy) > 1e-9);
            bool hasRod = Math.Abs(p.ToolEccWx) > 1e-9 || Math.Abs(p.ToolEccWy) > 1e-9;
            bool hasTDir = p.ToolOffsetMethod == ToolOffsetMethod.EyeToHandImage
                           && (Math.Abs(p.ToolOffsetWx) > 1e-9 || Math.Abs(p.ToolOffsetWy) > 1e-9);
            var rod = CalibrationConsumptionContract.ResolveRodOffset(
                p.ToolOffsetWx, p.ToolOffsetWy, hasTDir, p.ToolEccWx, p.ToolEccWy, hasRod);

            return new ConsumptionInputs
            {
                IsDownCamera = p.PrimaryPath == CalibrationAcquirePath.DownCameraWalk
                               || p.PrimaryPath == CalibrationAcquirePath.DownCameraPixelRotCenter,
                IsEyeInHand = p.EyeMode == EyeMode.EyeInHand,
                HasTruthWalkPath = p.PrimaryPath == CalibrationAcquirePath.CameraTruthWalk,
                HandEyeInNozzleDomain = p.HandEyeInNozzleDomain,
                NozzleAxisCoaxial = p.NozzleAxisCoaxial,
                RotationFitRadiusMm = p.RotationFitRadiusMm,
                FeatureTemplateName = p.FeatureTemplateName,
                HasRotationCenter = p.HasRotationCenter,
                HasEcc = hasEcc,
                HasToolOffsetDirect = hasTDir,
                HasRodOffsetCandidate = hasRod,
                RodOffsetWx = rod.Bx,
                RodOffsetWy = rod.By,
                RodOffsetSource = rod.Src,
                PhotoBaseX = p.BasePosX,
                PhotoBaseY = p.BasePosY,
                RotCenterWx = p.ToolCenterWx,
                RotCenterWy = p.ToolCenterWy,
                EccX = p.ToolOffsetPureWx,
                EccY = p.ToolOffsetPureWy,
                U0Deg = p.CalibU0 ?? 0.0,
            };
        }

        /// <summary>
        /// ★★2026-09-15：发布前"覆盖对账"——把【当前工位已生效值 → 本次将写入值】逐键列出（只列真正变化的键）。
        /// 现值口径 = 与 <see cref="ProcessConfigOverlay.LoadEffective"/> 一致：代码默认值 + 现有补丁。
        /// 存在意义：工位过程配置是扁平字段（无相机槽维度），而复合工位两台相机各有档案 ⇒
        ///   发布其中一台会把另一台的已发布值改写/清零，且无任何异常可察觉。先把代价摊开再落笔。
        /// </summary>
        /// <returns>Text=变化清单（空串=不会改变任何已生效值）；HasErase=其中是否存在"非零现值被清零"</returns>
        private static (string Text, bool HasErase) DescribePublishOverwrite(
            string currentJson, object codeDefaults, IDictionary<string, object> fields)
        {
            if (fields == null || fields.Count == 0 || codeDefaults == null) return ("", false);
            try
            {
                // 现值 = 默认值对象的深拷贝 + 现有补丁覆盖（与 LoadEffective 同口径，不另起一套）
                object effective = Newtonsoft.Json.JsonConvert.DeserializeObject(
                    Newtonsoft.Json.JsonConvert.SerializeObject(codeDefaults), codeDefaults.GetType());
                if (!string.IsNullOrWhiteSpace(currentJson))
                    Newtonsoft.Json.JsonConvert.PopulateObject(currentJson, effective);

                var t = effective.GetType();
                var lines = new List<string>();
                bool hasErase = false;
                foreach (var kv in fields)
                {
                    var prop = t.GetProperty(kv.Key);
                    if (prop == null || !prop.CanRead)
                    {
                        lines.Add($"  · {kv.Key} = {FmtField(kv.Value)}（配置类无此属性 ⇒ 写进去也不生效，请核对键名）");
                        continue;
                    }
                    object cur = prop.GetValue(effective);
                    if (SameField(cur, kv.Value)) continue;
                    bool erase = !IsZeroish(cur) && IsZeroish(kv.Value);
                    if (erase) hasErase = true;
                    lines.Add($"  ·  {kv.Key}: {FmtField(cur)} → {FmtField(kv.Value)}" + (erase ? "   ← 现值被清零" : ""));
                }
                return (lines.Count == 0 ? "" : string.Join("\n", lines), hasErase);
            }
            catch (Exception ex)
            {
                // 对账失败也要说出来（宁可多弹一次，也不要"看起来对账过了"）
                return ("  （覆盖对账失败：" + ex.Message + "）", true);
            }
        }

        private static bool SameField(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a is bool || b is bool) return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
            try
            {
                return Math.Abs(Convert.ToDouble(a) - Convert.ToDouble(b)) < 1e-6;
            }
            catch
            {
                return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsZeroish(object v)
        {
            if (v == null) return true;
            if (v is bool b) return !b;
            try { return Math.Abs(Convert.ToDouble(v)) < 1e-9; }
            catch { return false; }
        }

        private static string FmtField(object v)
        {
            if (v == null) return "null";
            if (v is bool b) return b ? "true" : "false";
            try { return Convert.ToDouble(v).ToString("F3"); }
            catch { return v.ToString(); }
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

        private string GetQuantityDisplayName(CalibrationQuantity quantity)
        {
            var opt = CalibrationTypeOptions.FirstOrDefault(o => o.Type == quantity);
            return opt != null ? opt.DisplayName : quantity.ToString();
        }

        /// <summary>★2026-09-12 候选徽标（按 v2 物理量 + 采集路径，含下相机专属语义）</summary>
        private static string QuantityBadge(CalibrationQuantity quantity, CalibrationAcquirePath path)
        {
            if (path == CalibrationAcquirePath.DownCameraWalk) return "下相机H·吸件";
            if (path == CalibrationAcquirePath.DownCameraPixelRotCenter) return "下相机像素旋转中心";
            switch (quantity)
            {
                case CalibrationQuantity.HandEye: return "相机H·九点";
                case CalibrationQuantity.ToolRotation: return "旋转中心e";
                case CalibrationQuantity.ToolOffset: return "对针t";
                case CalibrationQuantity.PixelScale: return "像素当量s";
                case CalibrationQuantity.LensDistortion: return "畸变预留";
                default: return quantity.ToString();
            }
        }

        private string GetDefaultNameForType(CalibrationQuantity quantity, string station)
        {
            switch (quantity)
            {
                case CalibrationQuantity.HandEye:
                    return $"{station}_手眼H标定";
                case CalibrationQuantity.ToolRotation:
                    return $"{station}_旋转中心e标定";
                case CalibrationQuantity.ToolOffset:
                    return $"{station}_对针TCO标定";
                case CalibrationQuantity.PixelScale:
                    return $"{station}_像素比例标定";
                case CalibrationQuantity.LensDistortion:
                    return $"{station}_镜头畸变标定";
                default:
                    return $"{station}_标定方案";
            }
        }

        /// <summary>
        /// 管理器侧日志出口（★2026-09-15 新增）：删除这类**破坏性**操作必须留痕，
        /// 否则"删了但没删干净"现场无从复盘。走与向导同一条 LogBus 通道，VS 输出窗口可直接看到。
        /// </summary>
        private static void AppendLog(string message)
        {
            try { Grayson.Vision.Contracts.Infrastructure.Logging.LogBus.Info("CalibrationManager", message); }
            catch { /* 日志镜像失败不影响主流程 */ }
        }

        private void DeleteSelectedProfile()
        {
            if (SelectedCalibrationProfile == null) return;

            var profileToRemove = SelectedCalibrationProfile;

            // ★2026-09-15：删除前把"删不掉的残留"摊给操作员看（旧版只有一句"可能无法正常工作"）。
            //   实测的残留三类：① 本档案矩阵 .tup 仍在 Recipes 目录；② 被同槽档案共享的矩阵**不能删**
            //   （否则把别人一起弄坏）；③ 已发布到工位配置的数值（RotCenter/e/矩阵路径）不会自动回滚。
            string residue = DescribeDeleteResidue(profileToRemove);
            var result = MessageBox.Show(
                $"确定要删除标定方案【{profileToRemove.Name}】吗？{residue}\n\n"
                + "删除后绑定该标定的业务节点可能无法正常工作！",
                "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                AppendLog($"[删除] 已取消删除「{profileToRemove.Name}」。");
                return;
            }

            // ① 档案本体（JSON 单一存储）。★旧版忽略返回值 ⇒ 删除失败时列表照样移除、重启又冒出来（"删不干净"）。
            if (string.IsNullOrWhiteSpace(profileToRemove.Id))
            {
                AppendLog($"[删除] ⚠ 方案「{profileToRemove.Name}」没有 Id，无法定位文件——已仅从列表移除，请检查 Config\\Calibrations 目录。");
            }
            else if (!_profileRepository.Delete(profileToRemove.Id))
            {
                string err = null;
                try { err = (_profileRepository as Grayson.Vision.Repository.Implementations.JsonCalibrationProfileRepository)?.LastError; } catch { }
                MessageBox.Show(
                    $"删除档案失败：磁盘上的文件可能仍在。\n{(string.IsNullOrWhiteSpace(err) ? "" : "原因：" + err + "\n")}"
                    + "\n⚠ 列表已移除，但重启后该方案可能重新出现——请关闭程序后手工删除 "
                    + "Config\\Calibrations 下对应文件。",
                    "删除未完全成功", MessageBoxButton.OK, MessageBoxImage.Warning);
                AppendLog($"[删除] ⚠ 档案删除失败（列表已移除，磁盘可能残留）：{err}");
            }
            else
            {
                AppendLog($"[删除] 档案已删除：{profileToRemove.Name}（Id={profileToRemove.Id.Substring(0, Math.Min(8, profileToRemove.Id.Length))}）");
            }

            // ② 矩阵文件：无他人共享才删（共享时保留，避免删 H 把共享它的 e 一起弄坏）
            RemoveOrphanMatrixFile(profileToRemove);

            CalibrationProfiles.Remove(profileToRemove);
            RebuildVisibleProfiles();
            RebuildDerivedCards();   // ★删除后工作台卡片要跟着重算（旧版没重算，卡会停在"刚才那个方案"的派生结果上）
        }

        /// <summary>
        /// 把"删除后仍会留下的东西"写成给操作员看的清单（空串=没查到残留）。
        /// 判据都来自磁盘/内存实态，不做猜测。
        /// </summary>
        private string DescribeDeleteResidue(CalibrationProfile p)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                // ① 共享矩阵
                string mat = (p.HomMatFilePath ?? string.Empty).Trim();
                if (mat.Length > 0)
                {
                    var sharers = CalibrationProfiles
                        .Where(x => x != null && !ReferenceEquals(x, p)
                                    && !string.IsNullOrWhiteSpace(x.HomMatFilePath)
                                    && string.Equals(x.HomMatFilePath.Trim(), mat, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Name).ToList();
                    if (sharers.Count > 0)
                        sb.Append($"\n\n· 矩阵文件被 {sharers.Count} 个其它方案共享（{string.Join("、", sharers)}）→ 文件将保留。");
                    else if (File.Exists(mat))
                        sb.Append("\n\n· 矩阵文件将一并删除：\n  " + mat);
                }
                // ② 同槽其它档案（提示"槽不会因此变干净"）
                var sameSlot = CalibrationProfiles
                    .Where(x => x != null && !ReferenceEquals(x, p) && IsSameCameraSlotStrict(x, p))
                    .Select(x => x.Name).ToList();
                if (sameSlot.Count > 0)
                    sb.Append($"\n· 同槽（{SlotText(p)}）仍有 {sameSlot.Count} 份方案：{string.Join("、", sameSlot)}");
                if (string.IsNullOrWhiteSpace(p.CameraSlotKey))
                    sb.Append("\n· ⚠ 本方案【槽未指定】，复合工位请务必确认它属于 Cam_A 还是 Cam_C。");
                // ③ 已发布的工位配置
                if (!string.IsNullOrWhiteSpace(p.BoundStationCode))
                    sb.Append($"\n· 已发布到工位 {p.BoundStationCode} 的数值（旋转中心/偏心/矩阵路径）**不会**随删除回滚，"
                              + "生产会继续用上一次发布的值；需要时请重新发布或清空该工位过程配置。");
            }
            catch (Exception ex)
            {
                sb.Append("\n· (残留检查异常：" + ex.Message + ")");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 删除矩阵文件——仅当【没有别的档案共享同一路径】且文件位于本程序目录下（防误删外部路径）。
        /// 共享判定用全量字符串比较（大小写不敏感），与仓库/H 聚合的路径口径一致。
        /// </summary>
        private void RemoveOrphanMatrixFile(CalibrationProfile p)
        {
            try
            {
                string mat = (p?.HomMatFilePath ?? string.Empty).Trim();
                if (mat.Length == 0 || !File.Exists(mat)) return;

                bool shared = CalibrationProfiles.Any(x => x != null && !ReferenceEquals(x, p)
                                && !string.IsNullOrWhiteSpace(x.HomMatFilePath)
                                && string.Equals(x.HomMatFilePath.Trim(), mat, StringComparison.OrdinalIgnoreCase));
                if (shared)
                {
                    AppendLog($"[删除] 矩阵文件被其它方案共享，已保留：{mat}");
                    return;
                }
                string root = AppDomain.CurrentDomain.BaseDirectory;
                if (!Path.GetFullPath(mat).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog($"[删除] ⚠ 矩阵文件不在程序目录内，为安全起见未删除：{mat}");
                    return;
                }
                File.Delete(mat);
                AppendLog($"[删除] 矩阵文件已删除：{mat}");
            }
            catch (Exception ex)
            {
                AppendLog($"[删除] ⚠ 矩阵文件删除失败（档案已删）：{ex.Message}");
            }
        }

        /// <summary>可开校验台：已标定 + 非像素当量(无矩阵) + 有选择</summary>
        private bool CanOpenVerifier()
        {
            var p = SelectedCalibrationProfile;
            return p != null && p.IsCalibrated && p.Quantity != CalibrationQuantity.PixelScale;
        }

        /// <summary>打开 P3 标定校验台（在线打点验收）。关闭后把校验记录写入 profile 并落库。</summary>
        /// <summary>
        /// 打开 P4 对针补偿窗口。
        /// ★ 2026-09-10 非模态化（同校验台/标定向导样板）：Show() 打开 + Owner=主窗，
        ///   对针窗与主界面（机械臂调试等页面/窗口）并行独立操作、互不阻塞；
        ///   窗口单例防重入（同一时间只允许一个对针窗，避免重复点开共享同一相机/轴卡）。
        /// 关窗提交语义：VM 在「✅ 写入 ToolOffset」时已把结果写入 profile（ApplyToolOffset），
        ///   窗口 Closed 事件统一判断 profile.IsToolOffsetCalibrated 是否落库。
        /// </summary>
        private void OpenToolOffset()
        {
            var profile = SelectedCalibrationProfile;
            if (profile == null) return;
            try
            {
                // 非模态单例：已有一个对针窗 → 仅激活，不再新开（相机取流/运动轴被该窗口独占）
                if (_activeToolOffsetWindow != null)
                {
                    if (_activeToolOffsetWindow.IsVisible)
                    {
                        _activeToolOffsetWindow.Activate();
                    }
                    return;
                }

                var win = new CalibrationToolOffsetWindow(profile)
                {
                    Owner = Application.Current.MainWindow
                };
                _activeToolOffsetWindow = win;
                // 关窗即释放单例引用，并判断是否已写入 ToolOffset 落库
                win.Closed += (s, e) =>
                {
                    _activeToolOffsetWindow = null;
                    if (profile.IsToolOffsetCalibrated)
                    {
                        SaveProfileToRepository(profile);
                        RebuildVisibleProfiles();
                        MessageBox.Show(
                            $"对针补偿已保存：ToolOffset = ({profile.ToolOffsetWx:F3}, {profile.ToolOffsetWy:F3}) mm\n\n" +
                            "发布后引导坐标 = 矩阵映射 + ToolOffset。建议到「🔍 标定校验台」做打点验收：图上点哪，工具头精确到哪。",
                            "对针补偿已保存", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                };
                win.Show(); // 非模态：不阻塞主界面（机械臂调试等窗口可并行使用）
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
                // 关窗会话变更收集：①校验记录 ②相机安装特性（2026-09-10 起对针职责已剥离，校验台不再记 R_n/p_tip/TCO）
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
                // ★2026-09-15：消费口径声明落库（决定生产端要不要叠 O / 要不要减 U 旋转项）。
                //   这是"H 到底在哪个域"的唯一可写入口——不写它，生产端就只能猜 PrimaryPath，
                //   而"已消杆的 H + 按杆端域补 O/e"正是本工位校验结果不对的直接原因。
                if (vm.SolveDeclDirty)
                {
                    profile.HandEyeInNozzleDomain = vm.HandEyeInNozzleDomain;
                    profile.NozzleAxisCoaxial = vm.NozzleAxisCoaxial;
                    // ★2026-09-15：'把 b 带进生产'的开关也在这里落库。
                    //   它是"校验台验收 → 生产同口径"闭环的最后一步：不开它，发布链就不会写 HasRodOffset，
                    //   生产端永远走 H(u)，永远少一个 |b| —— 而原先这个字段**没有任何界面**，用户无从打开。
                    profile.RodOffsetInProduction = vm.RodOffsetInProduction;
                    PublishEccStatusText = "💾 已保存消费口径声明：H="
                        + (vm.HandEyeInNozzleDomain == true ? "吸嘴域（直吸 H(u)，不叠 O）"
                            : vm.HandEyeInNozzleDomain == false ? "杆端域（固定相机→吸点=H(u)+b；EIH 才叠 O 补偿）" : "未声明")
                        + "；吸嘴="
                        + (vm.NozzleAxisCoaxial == true ? "与 U 同轴（免 U 旋转项）"
                            : vm.NozzleAxisCoaxial == false ? "偏心（保留 R(U−U0)·e）" : "未声明")
                        + "；b 进生产="
                        + (vm.RodOffsetInProduction == true ? "开（发布后生产端走 H(u)+b）"
                            : vm.RodOffsetInProduction == false ? "关（生产端走 H(u)，会少一个 |b|）" : "未声明")
                        + "。发布到工位时按此写 NeedsOCompensation / 是否免 U 项 / HasRodOffset。";
                }
                if (vm.HasPendingRecord)
                {
                    if (profile.VerificationRecords == null)
                    {
                        profile.VerificationRecords = new System.Collections.Generic.List<CalibrationVerificationRecord>();
                    }
                    profile.VerificationRecords.Add(vm.PendingRecord);
                }
                if (vm.CameraMountDirty || vm.SolveDeclDirty || vm.HasPendingRecord)
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

            // ⚠ 2026-09-12 未实现物理量入口拦截：镜头畸变尚无真实标定板角点检测，拦截并引导改物理量。
            if (SelectedCalibrationProfile.Quantity == CalibrationQuantity.LensDistortion)
            {
                MessageBox.Show(
                    "「镜头畸变」标定尚未实现（缺少真实标定板角点检测），已阻止打开向导，避免产出无效标定数据。\n\n" +
                    "请在上方『标定物理量』下拉中把该方案切换为：手眼 H / 旋转中心 e / 像素当量 s，再重新执行标定。",
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

                // ★ P2 保存语义分叉：仅 H 段产出矩阵（落盘工位级目录 + IsCalibrated）；
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
                var ask = MessageBox.Show(doneInfo + "\n\n是否立即把旋转中心 O / 基准角 U0 / 吸嘴偏心 e 发布到工位业务配置（VisionPickPlace / MahjongDualNozzle 落点补偿）？\n[是] = 发布（缺 e/O 时会从同工位其它档案继承）  [否] = 稍后用『发布偏心到业务配置』",
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
                if (CanPublishEcc()
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
        /// 向导关窗后把矩阵从当前路径（通常为 %TEMP%）复制一份到**工位级唯一归宿**
        /// Recipes\Workstations\{工位码}\Calib（该工位所有配方共享；无绑定工位落 Default）。
        ///
        /// ★★2026-09-15 单轨存储定案：历史"设备级"目录（Recipes\Devices\{设备ID}\Calib）
        ///   已废弃——不再写入、不再读取、存量数据由 CalibrationMatrixStore.MigrateAndPurgeDeviceScope
        ///   迁到工位级后归档清除。本方法**只写工位级一处**，路径真源见 CalibrationMatrixStore。
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
                string targetFile;
                string failMessage;
                bool ok = CalibrationMatrixStore.TryPersistToStationScope(
                    profile.HomMatFilePath,
                    profile.Name,
                    profile.BoundStationCode,
                    out targetFile,
                    out failMessage);

                if (!ok)
                {
                    AppendLog($"[落盘] ⚠ 矩阵落盘工位级目录失败：{failMessage}（保留原路径 {profile.HomMatFilePath}）");
                    return null;
                }

                AppendLog($"[落盘] 矩阵已落盘工位级目录：{targetFile}");
                return targetFile;
            }
            catch (Exception ex)
            {
                MessageBox.Show("矩阵自动落盘异常：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
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
        /// 反向映射校验（2026-09-10）：物理坐标 → 像素坐标。
        /// 与正向 Pixel→World 配对，构成首页「在线坐标映射校验」的双向转换。
        /// </summary>
        private void ExecuteTestMapReverse()
        {
            if (SelectedCalibrationProfile == null || string.IsNullOrEmpty(SelectedCalibrationProfile.HomMatFilePath))
            {
                TestPixelResult = "未选择有效标定文件";
                return;
            }

            var res = _calibService.MapWorldToPixel(SelectedCalibrationProfile.HomMatFilePath, TestWorldX, TestWorldY);
            if (res.Success)
            {
                TestPixelResult = $"Px: {res.Data.PixelX:F1}, Py: {res.Data.PixelY:F1}";
            }
            else
            {
                TestPixelResult = "映射失败: " + res.Message;
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

            string targetFile = Path.Combine(targetDir, CalibrationMatrixStore.GetMatrixFileName(profile.Name));

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
            if (scopeIndex == 1)
            {
                // 特定配方：以选中配方的 RecipeCode 为目录名（无则回退 RecipeId，杜绝 "Default" 混写）
                string recipeKey = !string.IsNullOrWhiteSpace(targetRecipe?.RecipeCode)
                    ? targetRecipe.RecipeCode
                    : (targetRecipe?.RecipeId ?? "Default");
                return CalibrationMatrixStore.GetRecipeCalibDir(recipeKey);
            }

            // 默认 0 工位级：Recipes\Workstations\{工位码}\Calib（跨配方共享；只读入口快照）
            // ★路径真源统一在 CalibrationMatrixStore——分发目标与"持久归宿"必须是同一份口径，
            //   否则发布链与产物落盘会各算一套目录（历史上"设备级/工位级"分叉的根源）。
            return CalibrationMatrixStore.GetStationCalibDir(profile?.BoundStationCode);
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

        /// <summary>
        /// 同相机槽判定（★★2026-09-15，复合工位安全闸）："同工位" ≠ "同相机"。
        /// 复合工位一台设备挂上下两台相机（Cam_A / Cam_C），四份档案 BoundStationCode 全同。
        /// 两侧都必须有【显式槽键】且相等；任一侧缺槽 ⇒ 不继承（缺槽档案应当先补槽，见 LoadProfiles 的槽回填）。
        /// </summary>
        private static bool IsSameCameraSlotStrict(CalibrationProfile a, CalibrationProfile b)
        {
            string sa = (a?.CameraSlotKey ?? string.Empty).Trim();
            string sb = (b?.CameraSlotKey ?? string.Empty).Trim();
            return sa.Length > 0 && sb.Length > 0
                   && string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>槽键的可读文本（日志/提示用；缺槽时点名，便于定位"为什么没继承"）</summary>
        private static string SlotText(CalibrationProfile p)
            => string.IsNullOrWhiteSpace(p?.CameraSlotKey) ? "(槽未指定)" : p.CameraSlotKey.Trim();

        /// <summary>Id 短显示（日志用）</summary>
        private static string ShortId(string id)
            => string.IsNullOrWhiteSpace(id) ? "(空)" : id.Substring(0, Math.Min(8, id.Length));

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
                CalibrationQuantity = profile.Quantity,
                BoundStationCode = profile.BoundStationCode,
                BoundDeviceId = profile.BoundDeviceId,
                IsCalibrated = profile.IsCalibrated,
                Model = profile
            };

            // ★★2026-09-15：按名回退必须【核对 Id】。同名档案（复合工位上下相机的 e 曾同名，见 CreateSelectedCandidates
            //   的修正）会让 GetByName 返回**别人的**档案 ⇒ "存在则更新"的判断失去鉴别力。命中但 Id 不同 ⇒ 这是重名，
            //   不是"同一条" ⇒ 走 Insert（仓库按 Model.Id 落文件名，不会覆盖对方），并留痕让重名当场暴露。
            var existing = _profileRepository.GetById(po.Id);
            if (existing == null)
            {
                var byName = _profileRepository.GetByName(profile.Name);
                if (byName != null && !string.Equals(byName.Id, po.Id, StringComparison.OrdinalIgnoreCase))
                    AppendLog($"[保存] ⚠ 重名检出：「{profile.Name}」已有 Id={ShortId(byName.Id)}，本次 Id={ShortId(po.Id)}"
                              + " → 按新档案写入（建议改名带上槽/吸嘴以区分）。");
                else
                    existing = byName;
            }
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
