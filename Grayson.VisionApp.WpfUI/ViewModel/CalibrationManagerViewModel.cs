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
using Grayson.Vision.Contracts.Calibration.Chain;
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

        /// <summary>
        /// 当前打开的标定校验台窗口引用（2026-09-06 非模态化防重入，同向导样板）。
        /// 校验台原 ShowDialog 模态打开会禁掉主窗与机械臂调试等并行窗口；改 Show() 后
        /// 与主界面并行操作，故同样以单例防重复点开共享同一相机/轴卡。窗口 Closed 时置 null。
        /// </summary>

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

        /// <summary>是否全库态（页头「定位工位」下拉仅全库态可见）</summary>
        public bool IsGlobalScope => !IsStationScope;

        //---------------------------------------------------------------------
        // 页头「定位工位」下拉（2026-09-30 补）
        //   ★ 修 Bug：直接点侧栏进入本页时是"全库态"，而链向导/链校验台/真机验证台
        //     都要求绑定具体工位 ⇒ 三个入口全被"请先定位到工位"挡死，只有从工位配置页
        //     跳进来才能用。这里给一条"就地定位"的路：选一个工位 = 等价于从工位工作台进来。
        //---------------------------------------------------------------------

        /// <summary>可定位工位清单（来自工位档案 StationProfileRepository）</summary>
        public ObservableCollection<StationPickItem> StationPickList { get; } = new ObservableCollection<StationPickItem>();

        private StationPickItem _pickedStation;
        /// <summary>页头选中的工位 → 立即进入定位态（等价于从工位工作台跳入）</summary>
        public StationPickItem PickedStation
        {
            get => _pickedStation;
            set
            {
                if (Set(ref _pickedStation, value) && value != null && !string.IsNullOrWhiteSpace(value.StationCode))
                    ApplyStationScope(new StationNavigationContext { StationCode = value.StationCode, StationName = value.StationName });
            }
        }

        public bool HasStationPicks => StationPickList.Count > 0;

        /// <summary>无工位档案时的提示（下拉换成一句话，不让操作员对着空框发呆）</summary>
        public bool HasNoStationPickHint => StationPickList.Count == 0;

        /// <summary>重新枚举工位档案（全库态下可点「↻」刷新）</summary>
        public void ReloadStationPicks()
        {
            StationPickList.Clear();
            try
            {
                var repo = new StationProfileRepository();
                foreach (var pr in repo.ListAll())
                {
                    if (pr == null || string.IsNullOrWhiteSpace(pr.StationCode)) continue;
                    if (StationPickList.Any(x => string.Equals(x.StationCode, pr.StationCode, StringComparison.OrdinalIgnoreCase)))
                        continue;   // 同工位多档案（多吸嘴）→ 下拉只出现一次
                    StationPickList.Add(new StationPickItem { StationCode = pr.StationCode, StationName = pr.StationName });
                }
            }
            catch (Exception ex)
            {
                ScopeNoteText = "⚠ 枚举工位档案失败：" + ex.Message;
            }
            OnPropertyChanged(nameof(HasStationPicks));
            OnPropertyChanged(nameof(HasNoStationPickHint));
        }

        /// <summary>回全库浏览（解除工位钉）</summary>
        public ICommand BackToGlobalCommand { get; private set; }

        /// <summary>重新枚举页头「定位工位」下拉（在别处新建工位档案后可刷新）</summary>
        public ICommand ReloadStationPicksCommand { get; private set; }

        /// <summary>左侧列表实际展示集（定位态=只显示该工位方案；全库=全部）。主集仍为 CalibrationProfiles。</summary>
        public ObservableCollection<CalibrationProfile> VisibleProfiles { get; } = new ObservableCollection<CalibrationProfile>();

        #endregion

        #region 相机槽候选清单

        /// <summary>★2026-09-29：范式1「档案标定计划 → 候选勾选 → 批量创建 CalibrationProfile」已整体退役
        /// （含 CalibrationCandidate / Candidates / CreateCandidatesCommand / DismissCandidatesCommand）。
        /// 范式2 唯一入口 = 链向导，产物唯一 = Calib\Chain.json。本 region 暂留作占位以便检索。</summary>

            #endregion

        #region P3 任务卡视图（2026-09-05：旧「三平行大按钮」收敛为按量任务卡 + 卡内动作）

        /// <summary>
        /// 任务卡行 VM：包一张派生卡（CalibrationCardModel）并为行模板暴露展示/状态刷子。
        /// 动作由卡主命令（RunToolOffsetForCardCommand / PublishCardCommand）以 CommandParameter=本行执行。
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
                return $"该方案拆 {DerivedCards.Count} 张任务卡，动作直接挂在卡上（引导/校验/对针/发布/重标），不再有全局平行按钮{tNote}。依赖缺位或已过期会置灰并给原因。"
                     + "★几何标定进度以页头「链状态」为准（链标定产物 = Chain.json，不回写本卡片状态）——本区卡片承担发布留痕与矩阵分发。";
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
            (RunToolOffsetForCardCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PublishCardCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
                    if (s != null && s.IsDisabled) continue; // ★2026-09-28：停用槽不进候选
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
                        if (s != null && s.IsDisabled) continue; // ★2026-09-28：停用槽不进下拉/提示
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
        public ICommand OpenToolOffsetCommand { get; }
        public ICommand SaveMatrixCommand { get; }
        public ICommand TestMapCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand ExportCommand { get; }

        /// <summary>链向导（范式2 Chain.json）入口 —— 2026-09-27 R3</summary>
        public ICommand OpenChainWizardCommand { get; }
        public ICommand OpenChainVerifyCommand { get; }

        /// <summary>★L3 真机「指哪打哪」验证台入口 —— 2026-09-29（回答"标定完能不能投产"）</summary>
        public ICommand OpenChainLiveVerifyCommand { get; }

        // ---- P3 任务卡动作（卡内命令；CommandParameter = ArtifactTaskCardVm）----
        /// <summary>引导/重新标定（H/e/s 卡）</summary>
        /// <summary>校验台（仅 H 卡、数据在）</summary>
        /// <summary>对针（仅 t 卡、EyeToHand；复用对针窗，首标/重标同入口）</summary>
        public ICommand RunToolOffsetForCardCommand { get; private set; }
        /// <summary>一键顺序标定：H→e→t 链式自动推进（2026-09-08；每段独立向导，段间弹窗确认可中止）</summary>
        /// <summary>发布/旁路发布（发布门禁 + 留痕，拍板④）</summary>
        public ICommand PublishCardCommand { get; private set; }

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
            OpenToolOffsetCommand = new RelayCommand(_ => OpenToolOffset(), _ => CanOpenVerifier());
            SaveMatrixCommand = new RelayCommand(_ => SaveMatrix());
            TestMapCommand = new RelayCommand(_ => ExecuteTestMap());
            ImportCommand = new RelayCommand(_ => ImportMatrixFile(), _ => SelectedCalibrationProfile != null);
            ExportCommand = new RelayCommand(_ => ExportMatrixFile(), _ => SelectedCalibrationProfile != null);
            OpenChainWizardCommand = new RelayCommand(_ => OpenChainWizard());
            OpenChainVerifyCommand = new RelayCommand(_ => OpenChainVerify());
            OpenChainLiveVerifyCommand = new RelayCommand(_ => OpenChainLiveVerify());

            RunToolOffsetForCardCommand = new RelayCommand(o => RunToolOffsetForCard(o as ArtifactTaskCardVm),
                o => CanRunToolOffsetForCard(o as ArtifactTaskCardVm));
            PublishCardCommand = new RelayCommand(o => PublishCard(o as ArtifactTaskCardVm),
                o => CanPublishCard(o as ArtifactTaskCardVm));
            BackToGlobalCommand = new RelayCommand(_ => EnterGlobalScope());
            ReloadStationPicksCommand = new RelayCommand(_ => ReloadStationPicks());

            // 2026-09-15：重绑相机/运动卡（修复"按计划创建"出来的方案绑定是占位值、且界面改不掉）
            RebindDevicesCommand = new RelayCommand(_ => RebindDevices(), _ => SelectedCalibrationProfile != null);

            LoadProfiles();
            RefreshBindingDeviceOptions();
            ReloadStationPicks();
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

            // ★★2026-09-29：范式1「档案标定计划任务卡 → 批量创建 CalibrationProfile」整体退役。
            //   范式2 唯一入口 = 上方「▶ 开始链标定」（链形状由档案事实推导，产物唯一 = Chain.json）。
            //   此处只做「档案有没有几何标定需求」的说明，不再往旧方案仓库写任何东西。
            var specs = CalibrationPlanEngineV2.Derive(archive);
            var executable = specs != null
                ? specs.Where(t => t.Quantity != CalibrationQuantity.LensDistortion).ToList()
                : new List<CalibrationTaskSpec>();
            if (executable.Count > 0)
            {
                ScopeNoteText = $"本工位档案推导出 {executable.Count} 项几何标定需求，"
                    + "请点上方「▶ 开始链标定」进入链向导完成标定（产物写入 Calib\\Chain.json，生产端同源装载）。";
                return;
            }

            // 档案可推导但无几何标定需求（检测/测量/OCR 类相机仅需模板/当量）
            ScopeNoteText = "按档案推导：本工位无几何标定需求（检测/测量/OCR 类相机仅需模板/当量），无需创建标定方案。";
        }

        /// <summary>退出定位态回全库浏览</summary>
        private void EnterGlobalScope()
        {
            _scopeStationCode = string.Empty;
            _scopeStationName = string.Empty;
            ScopeNoteText = string.Empty;
            // 回全库 ⇒ 清空下拉选择（否则会残留上一次定位，再选同一个不会触发 setter）
            if (_pickedStation != null) { _pickedStation = null; OnPropertyChanged(nameof(PickedStation)); }
            RaiseScopeChanged();
            RebuildVisibleProfiles();
            RefreshCameraSlotOptions();
            ReloadStationPicks();
        }

        private void RaiseScopeChanged()
        {
            OnPropertyChanged(nameof(IsStationScope));
            OnPropertyChanged(nameof(IsGlobalScope));
            OnPropertyChanged(nameof(ScopeChipText));
            OnPropertyChanged(nameof(ScopeHintText));
            OnPropertyChanged(nameof(ChainStatusText));
        }

        /// <summary>
        /// 当前定位工位的链审计摘要（范式2：链状态是工位级属性，不是方案级——
        /// 故不进主列表加列，做成顶部 chip，点击进链校验台）。只读、异常吞掉显示"—"。
        /// </summary>
        public string ChainStatusText
        {
            get
            {
                if (!IsStationScope) return "链状态：—（未定位工位）";
                try
                {
                    var rep = ChainAuditor.AuditStation(_scopeStationCode);
                    if (!rep.Loaded) return "🔗 链：未落盘（fail-closed 待补链）";
                    int bad = rep.GlobalIssues.Count + rep.Rows.Count(r => !r.Ok);
                    string text = bad == 0 ? "🔗 链：✓ 就绪（" + rep.EdgeCount + " 边）"
                                           : "🔗 链：✗ " + bad + " 项问题";
                    // ★2026-09-29 台账轻量版（P0-3 配套）：拼上最近一次 L3 留痕——
                    //   "上次验证什么时候、过没过、偏差多少"不用开窗就能看到
                    //   （此前验证结论关窗即丢，页面上更是无处可见）。
                    var last = ChainLiveVerifier.ReadLastHistory(_scopeStationCode);
                    if (last != null)
                    {
                        string ts = last.Timestamp;
                        DateTime t;
                        if (DateTime.TryParse(last.Timestamp, out t)) ts = t.ToString("MM-dd HH:mm");
                        string errTxt = last.MaxErrorMm.HasValue
                            ? last.MaxErrorMm.Value.ToString("F3") + "mm" : "—";
                        text += " · 上次验证 " + ts + " " + (last.Passed ? "✓" : "✗")
                              + " maxErr " + errTxt;
                    }
                    return text;
                }
                catch { return "🔗 链：—"; }
            }
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
                // ★2026-09-28：停用槽不参与眼模式判定（它不进链）
                var slot = req.CameraSlots?.FirstOrDefault(x => x != null && !x.IsDisabled);
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
                // ★2026-09-28：停用槽不参与"是否随 Z"判定
                var slot = req.CameraSlots?.FirstOrDefault(x => x != null && !x.IsDisabled);
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

            // ★★2026-09-29：原此处硬编码一条「工位1_Top相机九点标定 / 工位: ST_01 / 平台1」的假方案——
            //   它没有任何"示例"标识，用户编辑即真实落库，且会把空库伪装成"已有一条标定"。
            //   现改为诚实的空状态：不注入任何假数据，由 EmptyProfilesHint 引导用户走链标定主线。
            if (CalibrationProfiles.Count == 0)
            {
                ScopeNoteText = "本机尚无任何标定方案。新版标定流程不再需要预先建方案——"
                    + "点上方「▶ 开始链标定」进链向导即可（产物写入工位目录 Calib\\Chain.json）。";
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
        /// 打开链向导（范式2，R3）：采集点对 → ChainFitter 拟合 → 组装 StationCalibGraph →
        /// G0~G4 校验 → 写 Recipes\Workstations\{工位}\Calib\Chain.json。
        /// 工位码取当前定位工位；★2026-09-29 起未定位时不再静默回落 ST_002（会把标定写到别的工位），
        /// 改为明确提示先定位工位。
        /// 独立窗口、模态：落盘前必须走完校验，避免半成品链图被生产端 fail-closed 拒收后无处排查。
        /// </summary>
        private void OpenChainWizard()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_scopeStationCode))
                {
                    MessageBox.Show(
                        "链标定必须绑定一个具体工位（产物要写进该工位的 Calib\\Chain.json）。\n\n"
                        + "请用页头「定位工位」下拉先选一个工位（或从工位工作台进入本页），选好后再点本按钮。",
                        "请先定位到工位", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var win = new ChainWizardWindow(_scopeStationCode)
                {
                    Owner = Application.Current.MainWindow
                };
                win.ShowDialog();
                // ★2026-09-29 P0-2：向导可能刚落盘/改链——关窗即重算页头 chip，不再要求操作员退出重进页面
                RaiseScopeChanged();
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开链向导失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 打开链校验台（范式2 L1/L2，只读）：审计 Recipes\Workstations\{工位}\Calib\Chain.json——
        /// 门禁可视（与生产端同尺）+ 数学校验。真机动作归 L3【OpenChainLiveVerify】，本台不驱动机器。
        /// 未落盘 ≠ 报错：明确提示"fail-closed 待补链"（指向链向导）。
        /// </summary>
        private void OpenChainVerify()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_scopeStationCode))
                {
                    MessageBox.Show(
                        "链校验台需要一个具体工位（要审的 Chain.json 就放在该工位目录下）。\n\n"
                        + "请用页头「定位工位」下拉先选一个工位（或从工位工作台进入本页），选好后再点本按钮。",
                        "请先定位到工位", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var win = new ChainVerifyWindow(_scopeStationCode)
                {
                    Owner = Application.Current.MainWindow
                };
                win.ShowDialog();
                // ★2026-09-29 P0-2：校验台只读，但关窗后同步刷新一次（口径统一：任何链窗口关闭都重算）
                RaiseScopeChanged();
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开链校验台失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 打开 L3 真机验证台（★2026-09-29 新建）：用真机走「相机看到点 → 链求值 → 逆解 → 到位 → 复测」
        /// 的完整闭环，在世界域量偏差，给出"能不能投产"的放行/拦截结论。
        /// 这是 L1（门禁可视）/L2（数学校验）之外唯一能证明"标定真的能打准"的环节。
        /// </summary>
        private void OpenChainLiveVerify()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_scopeStationCode))
                {
                    MessageBox.Show(
                        "真机验证需要一个具体工位（验证的是该工位 Calib\\Chain.json 的产物）。\n\n"
                        + "请用页头「定位工位」下拉先选一个工位（或从工位工作台进入本页），选好后再点本按钮。",
                        "请先定位到工位", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var win = new ChainLiveVerifyWindow(_scopeStationCode)
                {
                    Owner = Application.Current.MainWindow
                };
                win.ShowDialog();
                // ★2026-09-29 P0-2/P0-3：真机验证可能刚写了留痕（LiveVerify_History.json）——
                //   关窗即重算 chip（chip 会拼上"上次验证"摘要，见 ChainStatusText）
                RaiseScopeChanged();
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开真机验证台失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                    // 旧口径校验台已退役（R4）：验收口径 = 链校验台（与生产同尺审计 Chain.json）
                    OpenChainVerify();
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
                // ★★2026-09-28 R6c：范式1「旋转几何（O/U0/e）发布到工位业务配置」的旁路整体退役。
                //   旋转几何的唯一落盘出口 = 链图 Recipes\Workstations\{工位}\Calib\Chain.json
                //   （工具节点的 Offset / URotationCenter，由链标定向导产出）；本页只做发布留痕。
                MessageBox.Show(doneInfo + "\n旋转几何的落盘出口 = 链图（Chain.json）。如需更新 O / U0 / e，请进『链标定向导』重跑对应段。",
                    "发布完成", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (q == CalibrationQuantity.ToolOffset)
            {
                // ★★2026-09-28 R6c：同上——对针/工具偏移不再写工位过程配置，真源 = 链图工具节点 Offset。
                MessageBox.Show(doneInfo + "\n对针/工具偏移的落盘出口 = 链图（Chain.json 工具节点 Offset）。如需更新，请进『链标定向导』。",
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

    /// <summary>页头「定位工位」下拉项（全库态下就地定位，不必先绕工位工作台）</summary>
    public class StationPickItem
    {
        public string StationCode { get; set; }
        public string StationName { get; set; }

        public string Display
        {
            get
            {
                return string.IsNullOrWhiteSpace(StationName)
                       || string.Equals(StationName, StationCode, StringComparison.OrdinalIgnoreCase)
                    ? StationCode
                    : StationCode + "（" + StationName + "）";
            }
        }
    }
}
