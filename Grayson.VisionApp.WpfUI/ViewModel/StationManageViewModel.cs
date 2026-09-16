//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationManageViewModel.cs
//===================================================================================
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Recipe.Services;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Contracts.Station.Triggers;
using Grayson.Vision.Core.Client;
using Grayson.Vision.Core.Processes;
using Grayson.Vision.Core.Station;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.Repository.Services;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ContractsLineConfig = Grayson.Vision.Contracts.Station.Models.LineConfigModel;
using ContractsStationConfig = Grayson.Vision.Contracts.Station.Models.StationConfigModel;
using HardwareDeviceModel = Grayson.Vision.WpfUI.Model.HardwareDeviceModel;
using RecipeDeviceMappingModel = Grayson.Vision.Contracts.Recipe.Models.RecipeDeviceMappingModel;
using RecipeModel = Grayson.Vision.Contracts.Recipe.Models.RecipeModel;

namespace Grayson.Vision.WpfUI.ViewModel
{
    #region 数据模型 (UI 专用本地模型)

    public class StationModel : ViewModelBase
    {
        private readonly IDevicePool _devicePool;
        public Action OnBoundRecipeChangedAction { get; set; }

        private string _stationId;
        public string StationId
        {
            get => _stationId;
            set => Set(ref _stationId, value);
        }

        private string _stationCode;
        public string StationCode
        {
            get => _stationCode;
            set => Set(ref _stationCode, value);
        }

        private string _stationName;
        public string StationName
        {
            get => _stationName;
            set => Set(ref _stationName, value);
        }

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set => Set(ref _isEnabled, value);
        }

        private int _timeoutMs = 3000;
        public int TimeoutMs
        {
            get => _timeoutMs;
            set => Set(ref _timeoutMs, value);
        }

        private RecipeModel _boundRecipe;
        public RecipeModel BoundRecipe
        {
            get => _boundRecipe;
            set
            {
                if (Set(ref _boundRecipe, value))
                {
                    OnBoundRecipeChangedAction?.Invoke();
                }
            }
        }

        private string _processKey;
        /// <summary>
        /// 工位绑定的业务过程键（空 = 不挂过程，纯视觉/手动调试模式）。
        /// 绑定后工位所有触发入口（启动/单次/PLC 信号）执行完整业务周期（运动+视觉）。
        /// </summary>
        public string ProcessKey
        {
            get => _processKey;
            set => Set(ref _processKey, value);
        }

        /// <summary>
        /// 业务过程参数 JSON（轴号/位置/IO/节拍，按需编辑；
        /// 序列化到 StationConfigModel.ProcessConfigJson 持久化）。
        /// </summary>
        public string ProcessConfigJson { get; set; }

        // ===== 任务模板绑定（stage9-2：任务模板为工位任务唯一入口；引擎由模板绑定层派生） =====

        private string _taskTemplateCode;
        /// <summary>绑定的任务模板代码（TemplateCode；任务模板中心的部署/解绑写此字段）</summary>
        public string TaskTemplateCode
        {
            get => _taskTemplateCode;
            set => Set(ref _taskTemplateCode, value);
        }

        private string _taskTemplateName;
        public string TaskTemplateName
        {
            get => _taskTemplateName;
            set => Set(ref _taskTemplateName, value);
        }

        private string _taskTemplateKindText;
        public string TaskTemplateKindText
        {
            get => _taskTemplateKindText;
            set => Set(ref _taskTemplateKindText, value);
        }

        public ObservableCollection<HardwareDeviceModel> HardwareDevices { get; set; }
        public ObservableCollection<RecipeDeviceMappingModel> RecipeDeviceMappings { get; set; }

        // ===== 触发源配置 =====

        private TriggerSourceType _triggerSourceType = TriggerSourceType.Manual;
        /// <summary>触发源类型（手动/定时器/PLC 位）</summary>
        public TriggerSourceType TriggerSourceType
        {
            get => _triggerSourceType;
            set => Set(ref _triggerSourceType, value);
        }

        private TriggerEdge _triggerEdge = TriggerEdge.Rising;
        /// <summary>边沿检测模式</summary>
        public TriggerEdge TriggerEdge
        {
            get => _triggerEdge;
            set => Set(ref _triggerEdge, value);
        }

        private int _debounceMs;
        /// <summary>防抖时间（毫秒）</summary>
        public int DebounceMs
        {
            get => _debounceMs;
            set => Set(ref _debounceMs, value);
        }

        private DropStrategy _dropStrategy = DropStrategy.DropOldest;
        /// <summary>丢帧策略</summary>
        public DropStrategy DropStrategy
        {
            get => _dropStrategy;
            set => Set(ref _dropStrategy, value);
        }

        private int _timerIntervalMs = 1000;
        /// <summary>定时器周期（毫秒）</summary>
        public int TimerIntervalMs
        {
            get => _timerIntervalMs;
            set => Set(ref _timerIntervalMs, value);
        }

        private string _plcDeviceId;
        /// <summary>PLC 设备 ID（设备池逻辑 Key）</summary>
        public string PlcDeviceId
        {
            get => _plcDeviceId;
            set => Set(ref _plcDeviceId, value);
        }

        private string _plcAddress;
        /// <summary>PLC 触发点位地址</summary>
        public string PlcAddress
        {
            get => _plcAddress;
            set => Set(ref _plcAddress, value);
        }

        private int _pollIntervalMs = 50;
        /// <summary>PLC 轮询间隔（毫秒）</summary>
        public int PollIntervalMs
        {
            get => _pollIntervalMs;
            set => Set(ref _pollIntervalMs, value);
        }

        /// <summary>
        /// 从 UI 属性构建触发源配置模型。
        /// </summary>
        public TriggerSourceConfig ToTriggerSourceConfig()
        {
            return new TriggerSourceConfig
            {
                SourceType = TriggerSourceType,
                Edge = TriggerEdge,
                DebounceMs = DebounceMs,
                DropStrategy = DropStrategy,
                TimerIntervalMs = TimerIntervalMs,
                PlcDeviceId = PlcDeviceId,
                PlcAddress = PlcAddress,
                PollIntervalMs = PollIntervalMs,
                EnableStats = true
            };
        }

        /// <summary>
        /// 从持久化配置模型恢复 UI 属性。
        /// </summary>
        public void FromTriggerSourceConfig(TriggerSourceConfig config)
        {
            if (config == null) config = new TriggerSourceConfig();
            TriggerSourceType = config.SourceType;
            TriggerEdge = config.Edge;
            DebounceMs = config.DebounceMs;
            DropStrategy = config.DropStrategy;
            TimerIntervalMs = config.TimerIntervalMs;
            PlcDeviceId = config.PlcDeviceId ?? "";
            PlcAddress = config.PlcAddress ?? "";
            PollIntervalMs = config.PollIntervalMs;
        }

        /// <summary>
        /// 全局硬件池中的 PLC 设备列表（供触发源配置下拉选择）。
        /// </summary>
        public IEnumerable<HardwareDeviceModel> PlcDeviceOptions =>
            _devicePool.GetAllDevices().OfType<IPlc>()
                .Select(d => new HardwareDeviceModel
                {
                    DeviceId = d.DeviceKey,
                    DeviceName = d.DeviceName,
                    DeviceType = d.BrandName
                }) ?? Enumerable.Empty<HardwareDeviceModel>();

        // ===== 装配就绪徽标（左树显示，由 VM 统一重算后写入）=====
        private string _readyBadgeText = string.Empty;
        /// <summary>左树就绪徽标文本（如 "🆕 空壳" / "🔧 装配中 2/6" / "✅ 就绪"）；由 VM 重算写入</summary>
        public string ReadyBadgeText
        {
            get => _readyBadgeText;
            set => Set(ref _readyBadgeText, value);
        }


        public StationModel()
        {
            _devicePool = App.StationHostRuntime?.DevicePool ?? throw new InvalidOperationException("DevicePool not initialized");
            HardwareDevices = new ObservableCollection<HardwareDeviceModel>();
            RecipeDeviceMappings = new ObservableCollection<RecipeDeviceMappingModel>();
        }
    }

    public class LineModel : ViewModelBase
    {
        public string LineId { get; set; }
        public string LineName { get; set; }
        public ObservableCollection<StationModel> Stations { get; set; }

        public LineModel()
        {
            Stations = new ObservableCollection<StationModel>();
        }
    }

    /// <summary>
    /// 工位"装配旅程"引导步骤（v1.1 用户视角：创建工位后按依赖顺序装配）。
    /// 步骤按用户真实依赖排序，非 Tab 顺序：
    ///   1 业务过程（决定跑什么周期）→ 2 物理硬件（画面/运动/IO 来源）→ 3 产品配方绑定（逻辑设备清单来源）
    ///   → 4 逻辑→物理映射 → 5 触发与运行策略 → 6 视觉资产（模板）→ 7 标定与示教 → 8 保存并同步 Runtime。
    /// 每步状态由当前 SelectedStation 配置实时判定（ReadinessCheck），下一步=首个未完成必做步骤。
    /// </summary>
    public class ReadinessStep
    {
        /// <summary>步骤序号（1 基）</summary>
        public int Index { get; set; }
        /// <summary>图标（emoji）</summary>
        public string Icon { get; set; }
        /// <summary>短标题（chip 显示）</summary>
        public string Title { get; set; }
        /// <summary>说明（ToolTip/详情）</summary>
        public string Description { get; set; }
        /// <summary>归属 Tab 语义名（仅展示）</summary>
        public string TabLabel { get; set; }
        /// <summary>定位目标 TabIndex（TabControl SelectedIndex；ActionKind=1 时忽略）</summary>
        public int TargetTabIndex { get; set; }
        /// <summary>0=点击定位 Tab；1=点击直接触发"保存并同步 Runtime"；2=打开模板工作台页；3=打开标定中心页</summary>
        public int ActionKind { get; set; }
        /// <summary>是否必做（false=建议项：如模板/标定按需，无则跳过）</summary>
        public bool Required { get; set; } = true;
        /// <summary>计算期完成标志（由 BuildSteps 写入；RefreshReadiness 据此设 State）</summary>
        public bool Done { get; set; }
        /// <summary>当前状态：0=未完成 1=进行中(下一步) 2=已完成 3=跳过/不适用</summary>
        public int State { get; set; }
        /// <summary>附加状态文案（如"已绑定 MahjongPick" / "缺少 FOV"）</summary>
        public string Detail { get; set; }

        /// <summary>UI 呈现的状态符号</summary>
        public string StateGlyph
        {
            get
            {
                switch (State)
                {
                    case 2: return "✅";
                    case 1: return "👉";
                    case 3: return "➖";
                    default: return "⬜";
                }
            }
        }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case 2: return "已完成";
                    case 1: return "下一步";
                    case 3: return "可跳过";
                    default: return "待完成";
                }
            }
        }
    }

    #endregion

    // 2. 新增快捷跳转命令
   
    public class StationManageViewModel : ViewModelBase
    {
        private readonly StationRuntimeManager _runtimeManager;
        private readonly StationConfigService _configService;
        private readonly IRecipeStorageService _recipeStorage;

        // ===== 引擎派生与展示（stage9-2：工位引擎键唯一来源=任务模板绑定，ProcessKey 只读展示/由模板保存写回） =====

        private string _engineDetailText = string.Empty;
        /// <summary>当前执行方案详情（适用说明/示教入口提示），第④Tab 展示</summary>
        public string EngineDetailText
        {
            get => _engineDetailText;
            private set => Set(ref _engineDetailText, value);
        }

        // ===== ④执行方案 Tab：引擎参数/示教面板就地挂载（2026-09-06 职责收敛）=====
        // 此前示教面板挂监视页扩展位（StationMonitorExtensionRegistry），监视页被工程操作污染。
        // 现按 09-03 信息架构决策：示教/参数面板改挂「工位工程工作台 → ④ 执行方案」Tab，
        // 与引擎选择同屏（选引擎 → 保存同步 → 就地出现参数表单），监视页回归纯生产看板。
        private object _engineTeachPanel;
        /// <summary>当前工位引擎的 参数/示教 面板（按 ProcessKey 经注册表创建；无注册=null）。</summary>
        public object EngineTeachPanel
        {
            get => _engineTeachPanel;
            private set
            {
                if (Set(ref _engineTeachPanel, value))
                {
                    OnPropertyChanged(nameof(HasEngineTeachPanel));
                }
            }
        }

        /// <summary>是否有引擎参数/示教面板可挂（④Tab「保存并同步后挂出」提示显隐用）。</summary>
        public bool HasEngineTeachPanel => EngineTeachPanel != null;

        private string _engineTeachPanelHint = string.Empty;
        /// <summary>引擎面板区说明文案（由 RefreshEngineTeachNotice 统一刷新）。</summary>
        public string EngineTeachPanelHint
        {
            get => _engineTeachPanelHint;
            private set => Set(ref _engineTeachPanelHint, value);
        }

        private string _engineTeachLogText = string.Empty;
        /// <summary>引擎面板最近一条日志（工作台无日志区，就地展示便于看到保存结果）。</summary>
        public string EngineTeachLogText
        {
            get => _engineTeachLogText;
            private set => Set(ref _engineTeachLogText, value);
        }

        private IStationMonitorExtension _teachExtension;
        private object _teachPanelView;

        /// <summary>按当前选中工位/引擎状态刷新 面板区说明文案（工位切换/引擎变更/保存后调用）。</summary>
        private void RefreshEngineTeachNotice()
        {
            if (EngineTeachPanel != null)
            {
                EngineTeachPanelHint = "以下为本引擎的 参数/示教 表单：填好点面板内【💾 保存】即写回工位配置并热更新（运行中保存则下次启动生效）。";
                return;
            }

            var station = SelectedStation;
            string key = station?.ProcessKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                EngineTeachPanelHint = "未选择执行引擎。从上方目录选引擎并点【💾 保存并同步】后，本引擎的参数/示教表单将就地出现在此处。";
                return;
            }
            if (StationMonitorExtensionRegistry.IsRegistered(key))
            {
                EngineTeachPanelHint = $"已选引擎 [{key}]，但尚未保存到数据库。点【💾 保存并同步】后，本引擎的参数/示教表单将就地出现在此处（保存前先以代码默认运行）。";
                return;
            }
            EngineTeachPanelHint = $"当前引擎 [{key}] 无需参数/示教面板（参数以代码默认或配方节点为准），可直接到【单工位监控】触发试运行。";
        }

        // ===== 任务模板绑定展示（stage9-2：模板=工位任务唯一入口；④ Tab 只读展示 + 跳模板中心） =====

        /// <summary>当前工位是否已绑定任务模板</summary>
        public bool HasTemplateBind => SelectedStation != null && !string.IsNullOrWhiteSpace(SelectedStation.TaskTemplateCode);

        /// <summary>绑定模板摘要（代码 · 名称（类型族））</summary>
        public string BoundTemplateSummaryText
        {
            get
            {
                var s = SelectedStation;
                if (s == null || string.IsNullOrWhiteSpace(s.TaskTemplateCode)) return "未绑定任务模板";
                return $"{s.TaskTemplateCode} · {s.TaskTemplateName ?? "—"}（{s.TaskTemplateKindText ?? "—"}）";
            }
        }

        /// <summary>绑定模板派生的运行引擎（读工位 ProcessKey，经目录给友好名）</summary>
        public string BoundTemplateEngineText
        {
            get
            {
                var s = SelectedStation;
                if (s == null || string.IsNullOrWhiteSpace(s.ProcessKey)) return "未装载运行引擎";
                var opt = Grayson.Vision.WpfUI.Service.StationProcessCatalog.Find(s.ProcessKey);
                return opt != null ? $"{opt.Icon} {opt.Name}（{s.ProcessKey}）" : $"引擎键 {s.ProcessKey}";
            }
        }

        private void RefreshBoundTemplate()
        {
            OnPropertyChanged(nameof(HasTemplateBind));
            OnPropertyChanged(nameof(BoundTemplateSummaryText));
            OnPropertyChanged(nameof(BoundTemplateEngineText));
        }

        public RelayCommand OpenTemplateCenterCommand { get; private set; }

        /// <summary>打开任务模板中心（模板中心/编辑器内「部署到工位」完成绑定/更换/解绑）</summary>
        private void OnOpenTemplateCenter()
        {
            if (SelectedStation == null) return;
            NavigationService.Current?.NavigateTo(PageType.TaskTemplateCenter, SelectedStation.StationCode);
        }

        /// <summary>刷新当前方案详情文案（工位切换/模板绑定变化/保存后调用）</summary>
        private void RefreshEngineDetailText()
        {
            var station = SelectedStation;
            if (station == null)
            {
                EngineDetailText = string.Empty;
                return;
            }
            if (string.IsNullOrWhiteSpace(station.ProcessKey))
            {
                EngineDetailText = "未绑定任务模板：工位触发只跑视觉链（纯视觉/配方驱动）。\n" +
                                   "若本工位需要 定位→吸取→归正→放料 的运动节拍或 DL/测量判据，请到【任务模板中心】新建任务模板并部署到本工位（绑定自动派生引擎）。";
                return;
            }
            var opt = Grayson.Vision.WpfUI.Service.StationProcessCatalog.Find(station.ProcessKey);
            if (opt == null)
            {
                EngineDetailText = $"已派生引擎 [{station.ProcessKey}]（目录外键——引擎已注册但无元数据，请检查注册一致性）。";
                return;
            }
            string teachHint = opt.HasTeachPanel
                ? "参数（位点/IO/角度策略/偏心）在下方「引擎参数与示教」表单就地编辑写回，无需手改 JSON。"
                : "该引擎暂无独立参数面板：参数以引擎代码默认为准（独立视觉引擎按任务模板 VerdictRule 判 OK/NG）。";
            EngineDetailText = $"{opt.Icon} {opt.Name}\n{opt.Summary}\n适用：{opt.ApplicableTo}\n{teachHint}";
        }

        // （旧「档案建议 → 一键采纳」直选引擎 UI 已随 stage9-2 移除；任务模板中心为唯一引擎入口）

        #region 装配旅程引导（v1.1 用户视角）

        /// <summary>装配步骤清单（创建工位后按依赖顺序引导）</summary>
        public ObservableCollection<ReadinessStep> ReadinessSteps { get; } = new ObservableCollection<ReadinessStep>();

        private int _activeTabIndex;
        /// <summary>TabControl 选中索引（步骤点击定位用，TwoWay）</summary>
        public int ActiveTabIndex
        {
            get => _activeTabIndex;
            set => Set(ref _activeTabIndex, value);
        }

        private string _readinessSummaryText = string.Empty;
        /// <summary>装配进度摘要（如"装配进度 3/5 · 下一步：S1② 绑定产品配方"）</summary>
        public string ReadinessSummaryText
        {
            get => _readinessSummaryText;
            private set => Set(ref _readinessSummaryText, value);
        }

        private bool _isAllRequiredDone;
        /// <summary>全部必做步骤已完成（用于显示"去试运行"高亮）</summary>
        public bool IsAllRequiredDone
        {
            get => _isAllRequiredDone;
            private set => Set(ref _isAllRequiredDone, value);
        }

        /// <summary>4 个配置 Tab 头状态文本（●=必做已就绪 / ○=有必做未完成）。模板/标定已独立成页，不占 Tab。</summary>
        public string[] TabHeaderTexts { get; } = new string[4];

        /// <summary>Tab 固定标题（glyph 前缀拼接用；stage9-2：④ 收敛为任务模板唯一入口 + 派生引擎/示教）</summary>
        private static readonly string[] TabTitles =
        {
            "① 硬件领用",
            "② 配方 · 逻辑映射",
            "③ 触发与运行",
            "④ 任务模板 · 引擎"
        };

        /// <summary>配方库是否有可用配方（映射 Tab「去创建配方」显隐依据；由配方加载后通知）</summary>
        public bool HasRecipesAvailable => AvailableRecipes != null && AvailableRecipes.Count > 0;

        private string _stationOverviewText = string.Empty;
        /// <summary>站头一行式信息摘要（配方/硬件/触发/同步），由 RefreshReadiness 重算</summary>
        public string StationOverviewText
        {
            get => _stationOverviewText;
            private set => Set(ref _stationOverviewText, value);
        }

        private string _recipePickerHintText = string.Empty;
        /// <summary>配方选择区/映射区引导文案（空配方库/未绑定/映射进度），由 RefreshReadiness 重算</summary>
        public string RecipePickerHintText
        {
            get => _recipePickerHintText;
            private set => Set(ref _recipePickerHintText, value);
        }

        private bool _isMappingAreaEmpty = true;
        /// <summary>映射区是否需要占位（配方库为空 或 尚未绑定配方时 true，隐藏映射表改显引导）</summary>
        public bool IsMappingAreaEmpty
        {
            get => _isMappingAreaEmpty;
            private set => Set(ref _isMappingAreaEmpty, value);
        }

        /// <summary>步骤点击 → 定位到对应 Tab</summary>
        public RelayCommand<ReadinessStep> GotoReadinessStepCommand { get; }

        /// <summary>保存同步完成后的"去单工位监控试运行"引导</summary>
        public RelayCommand GoMonitorCommand { get; }

        #endregion

        public RelayCommand OpenCalibrationCenterCommand { get; }
        /// <summary>打开模板工作台（独立菜单页；携带当前工位代码自动定位/归属）</summary>
        public RelayCommand OpenTemplateWorkbenchCommand { get; }
        /// <summary>打开视觉流程编辑器（携带当前工位绑定的配方上下文）</summary>
        public RelayCommand OpenFlowEditCommand { get; }
        /// <summary>打开配方管理页（携带当前绑定的配方；配方库为空时同作「去创建配方」入口）</summary>
        public RelayCommand OpenRecipeManageCommand { get; }
        /// <summary>打开当前工位的需求档案（查看/编辑问卷；无档案旧工位=补档空问卷）</summary>
        public RelayCommand OpenProfileCommand { get; }

        public StationManageViewModel(
            StationRuntimeManager runtimeManager = null,
            StationConfigService configService = null,
            IRecipeStorageService recipeStorage = null)
        {
            _runtimeManager = runtimeManager ?? new StationRuntimeManager(App.StationHostRuntime);
            _configService = configService ?? new StationConfigService();
            _recipeStorage = recipeStorage ?? Grayson.Vision.Repository.Services.RecipeStorageFactory.CreateRecipeStorageService();

            GlobalHardwarePool = new ObservableCollection<HardwareDeviceModel>();
            AvailableRecipes = new ObservableCollection<RecipeModel>();
            ProductionLines = new ObservableCollection<LineModel>();

            // Tab 头默认纯标题（装配状态 glyph 由 RefreshReadiness 在选中工位后写入）
            for (int i = 0; i < TabHeaderTexts.Length; i++) TabHeaderTexts[i] = TabTitles[i];

            AddLineCommand = new RelayCommand(_ => OnAddLine());
            AddStationCommand = new RelayCommand(_ => OnAddStation(), _ => SelectedLine != null);
            DeleteNodeCommand = new RelayCommand(_ => OnDeleteNode(), _ => SelectedLine != null || SelectedStation != null);
            AddHardwareCommand = new RelayCommand(_ => OnAddHardware(), _ => SelectedStation != null);
            RemoveHardwareCommand = new RelayCommand(_ => OnRemoveHardware(), _ => SelectedStation != null && SelectedHardware != null);
            SaveStationConfigCommand = new RelayCommand(async _ => await OnSaveStationConfigAsync(), _ => SelectedStation != null);

            InitializeFromServices();

            // initialize per-tab view models
            HardwareAllocationVm = new HardwareAllocationViewModel(this);
            // 初始化快捷跳转命令
            OpenCalibrationCenterCommand = new RelayCommand(
                _ => OnOpenCalibrationCenter(),
                _ => SelectedStation != null
            );
            OpenTemplateWorkbenchCommand = new RelayCommand(
                _ => OnOpenTemplateWorkbench(),
                _ => SelectedStation != null
            );
            OpenTemplateCenterCommand = new RelayCommand(
                _ => OnOpenTemplateCenter(),
                _ => SelectedStation != null
            );
            OpenFlowEditCommand = new RelayCommand(
                _ => OnOpenFlowEdit(),
                _ => SelectedStation?.BoundRecipe != null
            );
            OpenRecipeManageCommand = new RelayCommand(
                _ => OnOpenRecipeManage(),
                _ => SelectedStation != null
            );
            OpenProfileCommand = new RelayCommand(
                _ => OnOpenProfile(),
                _ => SelectedStation != null
            );

            GotoReadinessStepCommand = new RelayCommand<ReadinessStep>(OnGotoReadinessStep);
            GoMonitorCommand = new RelayCommand(_ => OnGoMonitor());

        }
        /// <summary>
        /// 跳转至标定中心页面
        /// </summary>
        private void OnOpenCalibrationCenter()
        {
            if (SelectedStation == null) return;

            // 组装上下文数据传递给标定中心
            var navContext = new StationNavigationContext
            {
                StationCode = SelectedStation.StationCode,
                StationName = SelectedStation.StationName,
                // 将 LogicalDeviceMappings 改为 StationModel 中实际定义的 RecipeDeviceMappings
                DeviceId = SelectedStation.RecipeDeviceMappings?.FirstOrDefault()?.MappedDeviceId
                           ?? SelectedStation.RecipeDeviceMappings?.FirstOrDefault()?.LogicalDeviceId
            };
            NavigationService.Current?.NavigateTo(PageType.CalibrationManage, navContext);
        }

        /// <summary>
        /// 打开视觉流程编辑器（带当前工位绑定配方上下文；与配方管理页同一跳转约定）。
        /// </summary>
        private void OnOpenFlowEdit()
        {
            if (SelectedStation?.BoundRecipe == null) return;
            NavigationService.Current?.NavigateTo(PageType.FlowEdit, SelectedStation.BoundRecipe);
        }

        /// <summary>
        /// 打开配方管理页（配方映射 Tab「去创建配方/管理配方」入口）。
        /// 携带当前绑定的配方对象；配方库为空时配方页展示空态，指引新建。
        /// </summary>
        private void OnOpenRecipeManage()
        {
            if (SelectedStation == null) return;
            NavigationService.Current?.NavigateTo(PageType.RecipeManage, SelectedStation.BoundRecipe);
        }

        /// <summary>
        /// 打开当前工位的需求档案（编辑模式向导）：有档案→预填问卷查看/修改；无档案（向导启用前的旧工位）
        /// → 以当前工位信息构造"补档空壳"进编辑模式，填完保存即补建 Active 档案。
        /// 编辑保存由窗口内完成（回写 JSON），本方法只做导航后刷新装配旅程/徽标。
        /// </summary>
        private void OnOpenProfile()
        {
            var s = SelectedStation;
            if (s == null) return;

            StationProfile profile = null;
            try
            {
                profile = new StationProfileRepository().GetByStationId(s.StationId);
            }
            catch { /* 档案缺失不阻断 */ }

            var editable = profile ?? new StationProfile
            {
                // 补档空壳：无问卷，仅带当前工位关联；保存后以 StationId 命名落 Active
                LineId = SelectedLine?.LineId ?? string.Empty,
                LineName = SelectedLine?.LineName ?? string.Empty,
                StationId = s.StationId,
                StationCode = s.StationCode,
                StationName = s.StationName,
                IsEnabled = s.IsEnabled,
                TimeoutMs = s.TimeoutMs,
                Status = StationProfileStatus.Active
            };
            // 始终以工位当前代码/名称为准（工位站头可改名，避免档案留存旧值）
            editable.StationCode = s.StationCode;
            editable.StationName = s.StationName;

            var win = new View.StationWizardWindow(editable)
            {
                Owner = Application.Current.MainWindow
            };

            if (win.ShowDialog() != true || !(win.DataContext is StationWizardViewModel wvm) || !wvm.IsEditMode)
            {
                return; // 取消/未确认
            }

            // 档案已由窗口落盘（保持 Active）；下游建议类 UI 实时读盘，此处刷新旅程步骤与徽标即可
            RefreshReadiness();
            RefreshAllStationBadges();
            string detail = profile != null ? "已更新" : "已补建（该工位此前无需求档案，本次从空问卷建档）";
            MessageBox.Show(
                $"工位需求档案{detail}。\n装配旅程第 6 步标定建议、标定中心候选清单等将按新档案实时更新；不影响已生效的运行时配置。",
                "📋 需求档案", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 打开独立「模板工作台」页并定位当前工位的模板资产（无则钉住归属，新建即带工位）。
        /// </summary>
        private void OnOpenTemplateWorkbench()
        {
            if (SelectedStation == null) return;
            NavigationService.Current?.NavigateTo(PageType.TemplateManage, SelectedStation.StationCode);
        }

        public HardwareAllocationViewModel HardwareAllocationVm { get; }
 

        #region 属性

        private ObservableCollection<LineModel> _productionLines;
        public ObservableCollection<LineModel> ProductionLines
        {
            get => _productionLines;
            set
            {
                if (Set(ref _productionLines, value))
                {
                    OnPropertyChanged(nameof(HasAnyLine));
                    RefreshEmptyGuide();
                }
            }
        }

        private LineModel _selectedLine;
        public LineModel SelectedLine
        {
            get => _selectedLine;
            set
            {
                if (Set(ref _selectedLine, value))
                {
                    RefreshCommandStates();
                    RefreshEmptyGuide();
                }
            }
        }

        private StationModel _selectedStation;
        public StationModel SelectedStation
        {
            get => _selectedStation;
            set
            {
                if (Set(ref _selectedStation, value))
                {
                    OnStationSelectedChanged();
                    RefreshCommandStates();
                    RefreshReadiness();
                    OnPropertyChanged(nameof(IsStationSelected));
                }
            }
        }

        /// <summary>是否已选中工位（装配引导面板显隐）</summary>
        public bool IsStationSelected => SelectedStation != null;

        /// <summary>是否已存在产线（空态引导用）</summary>
        public bool HasAnyLine => ProductionLines != null && ProductionLines.Count > 0;

        private string _emptyGuideTitle = string.Empty;
        /// <summary>空态引导标题（未选中工位时显示在右侧）</summary>
        public string EmptyGuideTitle
        {
            get => _emptyGuideTitle;
            private set => Set(ref _emptyGuideTitle, value);
        }

        private string _emptyGuideText = string.Empty;
        /// <summary>空态引导正文</summary>
        public string EmptyGuideText
        {
            get => _emptyGuideText;
            private set => Set(ref _emptyGuideText, value);
        }

        /// <summary>供 View 侧低层控件（DataGrid 单元格编辑等）在编辑后触发装配状态重算</summary>
        public void NotifyStationEdited()
        {
            RefreshReadiness();
        }

        private HardwareDeviceModel _selectedHardware;
        public HardwareDeviceModel SelectedHardware
        {
            get => _selectedHardware;
            set
            {
                if (Set(ref _selectedHardware, value))
                {
                    RefreshCommandStates();
                }
            }
        }

        public ObservableCollection<HardwareDeviceModel> GlobalHardwarePool { get; set; }
        public ObservableCollection<RecipeModel> AvailableRecipes { get; set; }

        #endregion

        #region 命令定义

        public RelayCommand AddLineCommand { get; }
        public RelayCommand AddStationCommand { get; }
        public RelayCommand DeleteNodeCommand { get; }
        public RelayCommand AddHardwareCommand { get; }
        public RelayCommand RemoveHardwareCommand { get; }
        public RelayCommand SaveStationConfigCommand { get; }

        #endregion

        #region 私有辅助方法

        /// <summary>步骤/状态相关属性名（StationModel.PropertyChanged 时需重算装配进度；含 ProcessKey=执行方案变更）</summary>
        private static readonly string[] ReadinessSensitiveProps =
        {
            nameof(StationModel.ProcessKey),
            nameof(StationModel.ProcessConfigJson),
            nameof(StationModel.TaskTemplateCode),
            nameof(StationModel.BoundRecipe),
            nameof(StationModel.IsEnabled),
            nameof(StationModel.TriggerSourceType),
            nameof(StationModel.TimerIntervalMs),
            nameof(StationModel.PlcDeviceId),
            nameof(StationModel.PlcAddress),
            nameof(StationModel.PollIntervalMs)
        };

        private StationModel _readinessSubscribedStation;
        private StationProfileRepository _profileRepo;

        /// <summary>装配进度重算（创建/选中工位、配置变化、保存后调用）。纯展示逻辑，不落盘。</summary>
        private void RefreshReadiness()
        {
            var s = SelectedStation;

            // 订阅跟随当前选中工位（退订旧对象防泄漏）
            if (!ReferenceEquals(_readinessSubscribedStation, s))
            {
                if (_readinessSubscribedStation != null)
                {
                    _readinessSubscribedStation.PropertyChanged -= Station_ReadinessPropChanged;
                }
                _readinessSubscribedStation = s;
                if (s != null)
                {
                    s.PropertyChanged += Station_ReadinessPropChanged;
                }
            }

            if (s == null)
            {
                ReadinessSummaryText = string.Empty;
                IsAllRequiredDone = false;
                StationOverviewText = string.Empty;
                RecipePickerHintText = string.Empty;
                IsMappingAreaEmpty = true;
                for (int i = 0; i < TabHeaderTexts.Length; i++) SetTabGlyph(i, string.Empty);
                ReadinessSteps.Clear();
                EngineDetailText = string.Empty;
                RefreshEmptyGuide();
                RefreshBoundTemplate();
                return;
            }

            if (_profileRepo == null) _profileRepo = new StationProfileRepository();
            StationProfile profile = null;
            try
            {
                profile = _profileRepo.GetByStationId(s.StationId)
                          ?? _profileRepo.GetByStationId(s.StationCode);
            }
            catch { /* 档案缺失不阻断引导 */ }

            // 构建 7 步清单（配方单主线：代码型业务过程已从工位页移除；含状态 0/2；"下一步"在下方统一判定）
            var steps = BuildSteps(s, profile);
            foreach (var st in steps)
            {
                st.State = st.Done ? 2 : 0;
            }

            // 建议项（Required=false）：完成=✅ 否则➖可跳过；不参与完成率与"下一步"定位
            bool foundNext = false;
            int doneCount = 0, reqCount = 0;
            foreach (var st in steps)
            {
                if (!st.Required)
                {
                    st.State = st.Done ? 2 : 3;
                    continue;
                }
                reqCount++;
                if (st.State == 2)
                {
                    doneCount++;
                }
                else if (!foundNext && st.State == 0)
                {
                    st.State = 1; // 高亮为"下一步"
                    foundNext = true;
                }
            }

            // 状态 3（跳过）不参与完成率
            ReadinessSteps.Clear();
            foreach (var st in steps) ReadinessSteps.Add(st);

            IsAllRequiredDone = doneCount >= reqCount;
            ReadinessSummaryText = IsAllRequiredDone
                ? $"🎉 装配完成（{doneCount}/{reqCount}）—— 可保存后在【单工位监控】试运行"
                : $"⚡ 装配进度 {doneCount}/{reqCount} · 下一步：{steps.FirstOrDefault(x => x.State == 1)?.Title ?? "检查可跳过项"}";

            // 更新 4 个配置 Tab 头状态点（步骤下标：0 执行方案 / 1 配方 / 2 硬件 / 3 映射 / 4 触发 / 7 保存）
            SetTabGlyph(0, steps[2].State == 2 ? "●" : "○"); // ① 硬件领用
            SetTabGlyph(1, (steps[1].State == 2 && steps[3].State == 2) ? "●" : "○"); // ② 配方 · 逻辑映射
            SetTabGlyph(2, steps[4].State == 2 ? "●" : "○"); // ③ 触发与运行
            SetTabGlyph(3, steps[0].State == 2 ? "●" : steps[0].Required ? "○" : "◇"); // ④ 执行方案（可跳过显示◇）

            RefreshStationOverview(s);
            RefreshRecipePickerState(s);
            RefreshAllStationBadges();
            RefreshEngineDetailText(); // 派生引擎只读详情（随模板绑定/工位切换刷新）
            RefreshEmptyGuide();
            RefreshBoundTemplate();
        }

        /// <summary>重算站头一行摘要（配方/硬件/触发/同步），供顶部信息条展示</summary>
        private void RefreshStationOverview(StationModel s)
        {
            if (s == null)
            {
                StationOverviewText = string.Empty;
                return;
            }

            string recipe = s.BoundRecipe != null
                ? $"配方 · {s.BoundRecipe.RecipeName}"
                : "配方 · 未绑定";

            string engine;
            if (string.IsNullOrWhiteSpace(s.TaskTemplateCode))
            {
                engine = string.IsNullOrWhiteSpace(s.ProcessKey)
                    ? "任务模板 · 未绑定"
                    : $"任务模板 · 未绑定（引擎残留 {s.ProcessKey}）";
            }
            else if (string.IsNullOrWhiteSpace(s.ProcessKey))
            {
                engine = $"任务模板 · {s.TaskTemplateCode}（引擎未同步）";
            }
            else
            {
                var engineOpt = Grayson.Vision.WpfUI.Service.StationProcessCatalog.Find(s.ProcessKey);
                engine = $"任务模板 · {s.TaskTemplateCode} → {(engineOpt != null ? engineOpt.Name : s.ProcessKey)}";
            }

            int hwCount = s.HardwareDevices?.Count ?? 0;
            string hardware = hwCount > 0 ? $"硬件 · 已领用 {hwCount} 台" : "硬件 · 未领用";

            string trigger;
            switch (s.TriggerSourceType)
            {
                case TriggerSourceType.Timer:
                    trigger = s.TimerIntervalMs > 0 ? $"定时触发 · {s.TimerIntervalMs}ms" : "定时触发 · 周期未设";
                    break;
                case TriggerSourceType.PlcBit:
                    trigger = string.IsNullOrWhiteSpace(s.PlcDeviceId)
                        ? "PLC 触发 · 未配设备"
                        : $"PLC 触发 · {s.PlcDeviceId}:{s.PlcAddress}";
                    break;
                default:
                    trigger = "手动触发";
                    break;
            }

            string sync;
            if (!s.IsEnabled)
            {
                sync = "工位已禁用";
            }
            else
            {
                var hostRuntime = App.StationHostRuntime as IStationHostRuntime
                                  ?? StationHostRuntime.GlobalInstance;
                sync = hostRuntime?.GetStationClient(s.StationCode) != null ? "Runtime 已同步" : "Runtime 未同步";
            }

            StationOverviewText = $"{engine}   ｜   {recipe}   ｜   {hardware}   ｜   {trigger}   ｜   {sync}";
        }

        /// <summary>重算映射 Tab 配方选择区/映射区引导（配方库空/未绑定/进度/完成）</summary>
        private void RefreshRecipePickerState(StationModel s)
        {
            if (s == null)
            {
                RecipePickerHintText = string.Empty;
                IsMappingAreaEmpty = true;
                return;
            }

            if (!HasRecipesAvailable)
            {
                RecipePickerHintText = "💡 配方库当前为空 —— 工位的运行逻辑（视觉链）由配方承载。点右侧【＋ 去创建配方】先建一个配方，再回来绑定本工位。";
                IsMappingAreaEmpty = true;
                return;
            }

            if (s.BoundRecipe == null)
            {
                RecipePickerHintText = "💡 尚未绑定产品配方：从上方下拉选择后，下方将列出该配方的【逻辑设备】，供映射到本工位已领用的物理硬件。";
                IsMappingAreaEmpty = true;
                return;
            }

            IsMappingAreaEmpty = false;
            int logicalCount = s.BoundRecipe.LogicalDevices?.Count ?? 0;
            // ★ 2026-09-16：配方无逻辑设备但业务过程有设备需求（如运动卡 CardAlias）时，仍需映射
            if (logicalCount == 0 && GetProcessDeclaredDeviceRequirements(s).Count == 0)
            {
                RecipePickerHintText = $"✅ 配方【{s.BoundRecipe.RecipeName}】不含逻辑设备需求 —— 无需映射，可直接进入「③ 触发与运行」。";
                return;
            }

            var mappings = s.RecipeDeviceMappings;
            int mapped = mappings == null ? 0 : mappings.Count(m => !string.IsNullOrWhiteSpace(m.MappedDeviceId));
            // ★ 2026-09-16：含过程声明设备需求（如业务过程的运动卡 CardAlias）
            int reqTotal = logicalCount + GetProcessDeclaredDeviceRequirements(s).Count;
            RecipePickerHintText = mapped >= reqTotal
                ? $"✅ 配方【{s.BoundRecipe.RecipeName}】共 {reqTotal} 个逻辑设备（含过程需求），已全部映射到物理硬件。"
                : $"⚡ 配方【{s.BoundRecipe.RecipeName}】共 {reqTotal} 个逻辑设备（含过程需求），已映射 {mapped}/{reqTotal} —— 全部映射完成后即可保存同步。";

            // ★★ 同一台物理设备被两条逻辑设备同时映射 = 运行时静默张冠李戴（链条照跑，但影像/动作来自另一台设备）。
            //    必须出声：只显示"✅ 已全部映射"会把这种配错渲染成"配置完成"，等于替错误背书。
            var dupConflicts = DescribeDuplicateDeviceMappings(s);
            if (dupConflicts.Count > 0)
            {
                RecipePickerHintText += " ｜ ⛔ " + string.Join("；", dupConflicts)
                    + " —— 请到下方映射表把每条逻辑设备改绑到它自己的物理设备后再保存。";
            }
        }

        /// <summary>
        /// 列出"同一台物理设备被多个逻辑设备同时映射"的冲突（纯展示判定，不改数据）。
        /// 两条逻辑设备共用一个物理实例时，运行链只会操作其中一台，另一条静默用错硬件：
        /// 表现是"配置写的是下相机，画面却是上相机"这类张冠李戴，且日志里看不出来。
        /// </summary>
        private static List<string> DescribeDuplicateDeviceMappings(StationModel s)
        {
            var result = new List<string>();
            var mappings = s?.RecipeDeviceMappings;
            if (mappings == null || mappings.Count == 0) return result;

            foreach (var g in mappings
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.MappedDeviceId))
                .GroupBy(m => m.MappedDeviceId, StringComparer.OrdinalIgnoreCase))
            {
                if (g.Count() <= 1) continue;

                // ★ 用 LogicalDeviceId 定位：它才是运行时注册键（节点按 CameraAlias=Id 取硬件），也是唯一稳定标识。
                //   工位档案里的 LogicalDeviceName 常是建站时从模板带过来的陈旧值
                //   （本仓 ST_001/ST_002 的它都是 "Top Camera"）⇒ 只打显示名会出现"两个一模一样的名字"，
                //   反而指不出该改哪两行。
                var names = g.Select(m =>
                {
                    string id = string.IsNullOrWhiteSpace(m.LogicalDeviceId) ? "<无Id>" : m.LogicalDeviceId;
                    return string.IsNullOrWhiteSpace(m.LogicalDeviceName) || m.LogicalDeviceName == id
                        ? $"[{id}]"
                        : $"[{id}（显示名 {m.LogicalDeviceName}）]";
                });
                result.Add($"逻辑设备 {string.Join(" / ", names)} 同时映射到同一台物理设备 [{g.Key}]");
            }
            return result;
        }

        /// <summary>重算空态引导文案（未选中工位时右侧区域给方向，避免空白）</summary>
        private void RefreshEmptyGuide()
        {
            if (IsStationSelected)
            {
                // 已选中工位：装配面板接管，空态层隐藏
                EmptyGuideTitle = string.Empty;
                EmptyGuideText = string.Empty;
                return;
            }

            if (!HasAnyLine)
            {
                EmptyGuideTitle = "🏭 还没有产线";
                EmptyGuideText = "产线是工位的物理归属。点击下方【+ 产线】建立第一条产线，或在产线下用【+ 工位】需求向导创建工位。";
                return;
            }

            if (SelectedLine != null)
            {
                EmptyGuideTitle = $"产线「{SelectedLine.LineName}」下尚未选择工位";
                EmptyGuideText = SelectedLine.Stations == null || SelectedLine.Stations.Count == 0
                    ? "该产线下还没有工位。点击【+ 工位】用『需求向导』创建第一个工位（先答需求问卷，系统推导建议链），或从左侧选择其它产线的工位。"
                    : "请在左侧选择该产线下的工位开始装配；或点击【+ 工位】用『需求向导』为产线新增工位。";
            }
            else
            {
                EmptyGuideTitle = "请选择工位";
                EmptyGuideText = "在左侧展开产线并点击一个工位，即可查看并装配它的视觉方案；或在产线下用【+ 工位】需求向导新建。";
            }
        }

        /// <summary>重算左侧树全部工位的就绪徽标文本（仅配置级判定，不读档案）</summary>
        private void RefreshAllStationBadges()
        {
            foreach (var line in ProductionLines)
            {
                if (line?.Stations == null) continue;
                foreach (var st in line.Stations)
                {
                    if (st == null) continue;
                    st.ReadyBadgeText = ComputeBadgeText(st);
                }
            }
        }

        /// <summary>
        /// 左树工位就绪徽标：🆕 空壳（未绑配方/无运行逻辑） / 🔧 装配中 {done}/{req} / ✅ 就绪。
        /// 判定口径与装配步骤一致（配方单主线：代码型业务过程已从工位页移除；配置级判定，不读档案）。
        /// </summary>
        private static string ComputeBadgeText(StationModel st)
        {
            bool recipe = st.BoundRecipe != null;
            bool hardware = st.HardwareDevices != null && st.HardwareDevices.Count > 0;

            if (!recipe)
            {
                return hardware ? "🔧 待绑配方" : "🆕 空壳";
            }

            int logicalCount = st.BoundRecipe?.LogicalDevices?.Count ?? 0;
            // ★ 2026-09-16：映射完成度按【配方逻辑设备 + 过程声明设备】合并计数（过程需求如运动卡 CardAlias）
            int requiredCount = logicalCount + GetProcessDeclaredDeviceRequirements(st).Count;
            bool mappingOk = requiredCount == 0
                || (st.RecipeDeviceMappings != null && st.RecipeDeviceMappings.Count == requiredCount
                    && st.RecipeDeviceMappings.All(m => !string.IsNullOrWhiteSpace(m.MappedDeviceId)));

            bool triggerOk;
            if (st.TriggerSourceType == TriggerSourceType.Timer) triggerOk = st.TimerIntervalMs > 0;
            else if (st.TriggerSourceType == TriggerSourceType.PlcBit)
                triggerOk = !string.IsNullOrWhiteSpace(st.PlcDeviceId) && !string.IsNullOrWhiteSpace(st.PlcAddress);
            else triggerOk = true;

            bool synced = false;
            if (st.IsEnabled)
            {
                var hostRuntime = App.StationHostRuntime as IStationHostRuntime
                                  ?? StationHostRuntime.GlobalInstance;
                synced = hostRuntime?.GetStationClient(st.StationCode) != null;
            }

            bool step1 = recipe, step2 = hardware;
            bool step3 = logicalCount == 0 || mappingOk;   // 无逻辑设备=无需映射→完成
            bool step4 = triggerOk;
            bool step5 = synced || !st.IsEnabled;

            // 与面板 BuildSteps 同口径：必做= 绑配方/领硬件/映射(绑了才必做)/触发/保存
            int req = 0, done = 0;
            AddToBadge(ref req, ref done, step1, true);
            AddToBadge(ref req, ref done, step2, true);
            AddToBadge(ref req, ref done, step3, true);
            AddToBadge(ref req, ref done, step4, true);
            AddToBadge(ref req, ref done, step5, true);

            if (done >= req) return "✅ 就绪";
            return $"🔧 装配中 {done}/{req}";
        }

        private static void AddToBadge(ref int req, ref int done, bool ok, bool required)
        {
            if (required)
            {
                req++;
                if (ok) done++;
            }
        }

        /// <summary>构建工位 8 步装配清单（纯计算，不触 UI；供进度面板与徽标共用）。Done=true 表示该步已满足。</summary>
        /// <summary>构建工位 7 步装配清单（纯计算，不触 UI；供进度面板与徽标共用）。Done=true 表示该步已满足。
        /// 配方单主线：运行逻辑=产品配方（流程编排）；代码型业务过程入口已移除。</summary>
        private static List<ReadinessStep> BuildSteps(StationModel s, StationProfile profile)
        {
            var steps = new List<ReadinessStep>();
            if (s == null) return steps;

            bool hasRecipe = s.BoundRecipe != null;
            bool hasHardware = s.HardwareDevices != null && s.HardwareDevices.Count > 0;
            int logicalCount = s.BoundRecipe?.LogicalDevices?.Count ?? 0;
            // ★ 2026-09-16：同上，映射完成度含过程声明设备（如运动卡 CardAlias）
            int requiredCount = logicalCount + GetProcessDeclaredDeviceRequirements(s).Count;
            bool mappingOk = requiredCount == 0
                || (s.RecipeDeviceMappings != null && s.RecipeDeviceMappings.Count == requiredCount
                    && s.RecipeDeviceMappings.All(m => !string.IsNullOrWhiteSpace(m.MappedDeviceId)));
            bool triggerOk;
            if (s.TriggerSourceType == TriggerSourceType.Timer) triggerOk = s.TimerIntervalMs > 0;
            else if (s.TriggerSourceType == TriggerSourceType.PlcBit)
                triggerOk = !string.IsNullOrWhiteSpace(s.PlcDeviceId) && !string.IsNullOrWhiteSpace(s.PlcAddress);
            else triggerOk = true;
            bool synced = false;
            if (s.IsEnabled)
            {
                var hostRuntime = App.StationHostRuntime as IStationHostRuntime
                                  ?? StationHostRuntime.GlobalInstance;
                synced = hostRuntime?.GetStationClient(s.StationCode) != null;
            }

            // 步骤 ActionKind：0=定位 Tab  1=触发"保存并同步"  2=打开模板工作台页  3=打开标定中心页
            // 装配执行方案（stage9-2：任务模板=工位任务唯一入口——工位跑什么=绑定哪个任务模板，
            // 引导定位/深度学习推理/外观测量 均由模板绑定派生运行引擎；纯视觉/检测工位可跳过（Required=false）。
            bool templateBound = !string.IsNullOrWhiteSpace(s.TaskTemplateCode);
            bool engineBound = !string.IsNullOrWhiteSpace(s.ProcessKey);
            var engineOpt = Grayson.Vision.WpfUI.Service.StationProcessCatalog.Find(s.ProcessKey);
            bool pickPlaceProfile = profile?.Requirement != null
                && (profile.Requirement.TaskType ?? string.Empty).Contains("定位抓取");
            var suggestion = Grayson.Vision.WpfUI.Service.StationProcessCatalog.SuggestFor(profile?.Requirement);
            string engineDetail;
            if (templateBound && engineBound)
            {
                engineDetail = engineOpt != null
                    ? $"已绑定 [{s.TaskTemplateCode} {s.TaskTemplateName}] → 引擎 [{engineOpt.Icon} {engineOpt.Name}]；参数/示教在下方「④ 任务模板·引擎」Tab 就地编辑"
                    : $"已绑定 [{s.TaskTemplateCode} {s.TaskTemplateName}] → 引擎键 [{s.ProcessKey}]（目录外键，检查注册一致性）";
            }
            else if (templateBound)
            {
                engineDetail = $"已绑定模板 [{s.TaskTemplateCode}] 但引擎键未同步 —— 点顶部【💾 保存并同步】由模板派生 ProcessKey；或在模板中心重新「部署到工位」。";
            }
            else if (engineBound)
            {
                engineDetail = $"已挂引擎 [{s.ProcessKey}] 但无任务模板绑定（旧配置残留）。模板=工位任务唯一入口：请到模板中心绑定模板收口，避免直改 ProcessKey 被模板保存覆盖。";
            }
            else if (suggestion != null)
            {
                var sugOpt = Grayson.Vision.WpfUI.Service.StationProcessCatalog.Find(suggestion.EngineKey);
                engineDetail = $"未绑定 —— 📋 档案建议：{sugOpt?.Icon} {sugOpt?.Name ?? suggestion.EngineKey}（到任务模板中心建引导定位模板并部署即走完整取放节拍）";
            }
            else
            {
                engineDetail = pickPlaceProfile
                    ? "未绑定 —— 档案任务=定位抓取但未推导出建议，到「任务模板中心」新建 引导定位 模板并部署到本工位"
                    : "未绑定（纯视觉/配方驱动）：工位触发只跑视觉链，无运动节拍；需要抓放/旋转时序时到模板中心绑定 引导定位 任务模板";
            }
            AddStep(steps, 1, "🧩", "绑定任务模板", "工位任务唯一入口：到任务模板中心新建/绑定任务模板（引导定位→运动节拍引擎；深度学习/外观测量→独立视觉引擎），模板自动派生引擎与判据。纯视觉检测工位可跳过。",
                "④ 任务模板/引擎", 3, 0, pickPlaceProfile, templateBound && engineBound, engineDetail);
            AddStep(steps, 2, "📦", "绑定产品配方", "工位的运行逻辑来自产品配方（含视觉链流程编排）。先在配方管理建配方，再到本步绑定。",
                "② 配方映射", 1, 0, true, hasRecipe,
                hasRecipe ? $"已绑定 [{s.BoundRecipe.RecipeName}]"
                          : "未绑定配方（运行逻辑为空 —— 创建后工位只能手动/纯视觉调试）");
            AddStep(steps, 3, "⚙️", "领用物理硬件", "从全局硬件池把相机/运动/IO 领用到本工位，供配方逻辑设备映射。",
                "① 硬件领用", 0, 0, true, hasHardware,
                hasHardware ? $"已领用 {s.HardwareDevices.Count} 台：{string.Join("、", s.HardwareDevices.Take(4).Select(h => h.DeviceName))}" + (s.HardwareDevices.Count > 4 ? "…" : "") : "未领用任何硬件");
            AddStep(steps, 4, "🔗", "完成逻辑→物理映射", "把配方要求的每个逻辑设备都绑定到已领用的物理硬件。",
                "② 配方映射", 1, 0, true, mappingOk,
                mappingOk ? (logicalCount == 0 ? "配方无逻辑设备需求（自动完成）" : "全部逻辑设备已映射")
                          : $"尚有逻辑设备未映射（{logicalCount} 个逻辑位）");
            AddStep(steps, 5, "🔔", "配置触发与运行", "选择触发源：手动按钮 / 定时器 / PLC 位，并设丢帧策略。",
                "③ 触发运行", 2, 0, true, triggerOk,
                triggerOk ? $"触发源=[{s.TriggerSourceType}]" : "触发配置不完整（Timer 需周期>0；PLC 需设备+地址）");
            AddStep(steps, 6, "🎯", "视觉资产：模板", "按需求在独立模板工作台创建模板（特征点/面随资产存档），供匹配节点引用。",
                "模板工作台页", 0, 2, false, false,
                profile != null && profile.AssetSuggestions != null && profile.AssetSuggestions.Any(a => a.Contains("模板"))
                    ? "向导建议：" + string.Join("；", profile.AssetSuggestions.Where(a => a.Contains("模板")))
                    : "按需（本工位任务如不需要模板可跳过）—— 点击直达模板工作台");
            AddStep(steps, 7, "📐", "标定与示教", "建立 图像↔机械 关系：九点/手眼/旋转中心/示教点（独立标定中心承接）。",
                "标定中心页", 0, 3, false, false,
                BuildCalibrationStepHint(profile));
            AddStep(steps, 8, "💾", "保存并同步 Runtime", "保存工位配置到库并同步 Core Runtime（单工位监控据此连接/运行）。",
                "顶栏保存按钮", -1, 1, true, synced || !s.IsEnabled,
                !s.IsEnabled ? "工位禁用：无需同步 Runtime（启用后需保存同步）" : (synced ? "已同步 Runtime Client" : "尚未保存/同步"));
            return steps;
        }

        /// <summary>旅程第6步（标定）提示：档案有相机槽 → 按槽逐条清单；否则沿用旧单相机建议文案</summary>
        private static string BuildCalibrationStepHint(StationProfile profile)
        {
            var slots = profile?.Requirement?.CameraSlots;
            if (slots != null && slots.Count > 0)
            {
                var items = slots.Where(x => x != null).Select(x =>
                {
                    string tag = string.IsNullOrWhiteSpace(x.SlotKey) ? "相机槽" : x.SlotKey;
                    string inst = ShortenInstallKind(x.InstallKind);
                    string rec = Grayson.Vision.WpfUI.Service.StationProposalEngine.RecommendCalibrationForSlot(x);
                    string shortRec = rec.Contains("：") ? rec.Substring(0, rec.IndexOf("：")) : rec;
                    return $"{tag}({inst})→{shortRec}";
                }).ToList();
                string prefix = items.Count > 2
                    ? string.Join("；", items.Take(2)) + $"…共 {items.Count} 槽"
                    : string.Join("；", items);
                return $"档案相机槽 ×{items.Count}：{prefix}—— 点击直达标定中心";
            }
            return profile != null && !string.IsNullOrWhiteSpace(profile.CalibrationSuggestion)
                ? "向导建议：" + profile.CalibrationSuggestion
                : "按需（需要出坐标/引导时做标定）—— 点击直达标定中心";
        }

        private static string ShortenInstallKind(string install)
        {
            if (string.IsNullOrWhiteSpace(install)) return "安装待定";
            return install.Replace("（随执行机构）", "").Replace("（俯视工面）", "")
                          .Replace("（仰视工面）", "").Replace("（斜视角）", "");
        }

        private void Station_ReadinessPropChanged(object sender, PropertyChangedEventArgs e)
        {
            if (ReadinessSensitiveProps.Contains(e.PropertyName))
            {
                RefreshReadiness();
            }
        }

        private static void AddStep(List<ReadinessStep> list, int index, string icon, string title, string desc,
            string tabLabel, int targetTab, int actionKind, bool required, bool done, string detail)
        {
            list.Add(new ReadinessStep
            {
                Index = index,
                Icon = icon,
                Title = title,
                Description = desc,
                TabLabel = tabLabel,
                TargetTabIndex = targetTab,
                ActionKind = actionKind,
                Required = required,
                Done = done,
                State = done ? 2 : 0,
                Detail = detail ?? string.Empty
            });
        }

        private void SetTabGlyph(int tabIndex, string glyph)
        {
            TabHeaderTexts[tabIndex] = string.IsNullOrEmpty(glyph)
                ? TabTitles[tabIndex]
                : glyph + " " + TabTitles[tabIndex];
            // 数组索引绑定 {Binding TabHeaderTexts[i]} 需整体重取通知
            OnPropertyChanged(nameof(TabHeaderTexts));
        }

        /// <summary>步骤点击：ActionKind 1=保存同步 / 2=模板工作台页 / 3=标定中心页；否则定位到对应 Tab</summary>
        private void OnGotoReadinessStep(ReadinessStep step)
        {
            if (step == null) return;
            switch (step.ActionKind)
            {
                case 1:
                    SaveStationConfigCommand.Execute(null);
                    return;
                case 2:
                    OnOpenTemplateWorkbench();
                    return;
                case 3:
                    OnOpenCalibrationCenter();
                    return;
                default:
                    ActiveTabIndex = step.TargetTabIndex;
                    return;
            }
        }

        private void OnGoMonitor()
        {
            if (SelectedStation == null) return;
            var nav = NavigationService.Current;
            if (nav != null)
            {
                nav.NavigateTo(PageType.StationMonitor, SelectedStation.StationCode);
            }
        }

        /// <summary>
        /// 主动刷新 UI 命令状态
        /// </summary>
        private void RefreshCommandStates()
        {
            AddStationCommand.RaiseCanExecuteChanged();
            DeleteNodeCommand.RaiseCanExecuteChanged();
            AddHardwareCommand.RaiseCanExecuteChanged();
            RemoveHardwareCommand.RaiseCanExecuteChanged();
            SaveStationConfigCommand.RaiseCanExecuteChanged();
            OpenCalibrationCenterCommand.RaiseCanExecuteChanged();
            OpenTemplateWorkbenchCommand.RaiseCanExecuteChanged();
            OpenTemplateCenterCommand?.RaiseCanExecuteChanged();
            OpenFlowEditCommand.RaiseCanExecuteChanged();
            OpenRecipeManageCommand.RaiseCanExecuteChanged();
            OpenProfileCommand.RaiseCanExecuteChanged();
        }

        private void OnStationSelectedChanged()
        {
            // 工位切换先卸载旧引擎面板（含 SelectedStation==null 取消选中场景）
            DetachEngineTeachPanel();

            if (SelectedStation == null) return;

            // 订阅工位的配方切换回调
            SelectedStation.OnBoundRecipeChangedAction = SyncRecipeMappings;

            // 🌟 核心修复（2026-09-10）：只要绑定了配方就重建映射表（以 BoundRecipe.LogicalDevices 为准），
            //   而非仅 RecipeDeviceMappings.Count==0 时。否则改名后持久化的旧映射残留旧 ID，
            //   逻辑映射 Tab 显示旧逻辑设备 ID，导致无法正确映射 / 运行时找不到相机。
            if (SelectedStation.BoundRecipe != null)
            {
                SyncRecipeMappings();
            }

            // ④ 执行方案 Tab：按工位 ProcessKey 就地挂 参数/示教 面板（无注册则留空不显示）
            AttachEngineTeachPanel();
        }

        // ===== 引擎参数/示教面板 挂载（④执行方案 Tab 就地；ProcessKey 需已落库）=====

        private void AttachEngineTeachPanel()
        {
            var station = SelectedStation;
            if (station == null || string.IsNullOrWhiteSpace(station.ProcessKey))
            {
                RefreshEngineTeachNotice();
                return;
            }

            try
            {
                var panel = StationMonitorExtensionRegistry.Create(station.ProcessKey);
                if (panel == null)
                {
                    RefreshEngineTeachNotice(); // 该引擎无注册面板（纯视觉/无需参数）
                    return;
                }

                if (panel.DataContext is IStationMonitorExtension ext)
                {
                    ext.Log += TeachExtension_OnLog;
                    ext.Bind(station.StationCode);
                    _teachExtension = ext;
                }
                _teachPanelView = panel;
                EngineTeachPanel = panel;
                RefreshEngineTeachNotice();
            }
            catch (Exception ex)
            {
                EngineTeachLogText = $"[ERROR] 引擎面板加载失败: {ex.Message}";
                DetachEngineTeachPanel();
            }
        }

        private void DetachEngineTeachPanel()
        {
            if (_teachExtension != null)
            {
                try { _teachExtension.Log -= TeachExtension_OnLog; } catch { }
                try { _teachExtension.Dispose(); } catch { }
                _teachExtension = null;
            }
            _teachPanelView = null;
            EngineTeachPanel = null;
            RefreshEngineTeachNotice();
        }

        private void TeachExtension_OnLog(string level, string message)
        {
            EngineTeachLogText = $"[{level}] {message}";
        }

        /// <summary>页面卸载钩子（View Unloaded 时调用）：退订 worker 事件并释放引擎面板，防事件泄漏。</summary>
        public void NotifyPageUnloaded()
        {
            DetachEngineTeachPanel();
        }

        /// <summary>
        /// 业务过程声明的设备需求（取过程配置的设备别名，如 CardAlias）。
        /// 背景（2026-09-16 实测 ST_002）：配方逻辑设备清单来自【视觉流程节点】的 [LogicalDeviceBinding]，
        /// 而运动卡是【业务过程层】按 CardAlias 消费的 —— 流程里没有机器人节点 ⇒ 映射清单永远缺席
        /// ⇒ 工位上下文无 IMotionCard ⇒ 运行报「未解析到运动控制卡 [EpsonRobot]」。
        /// ★★ 取值必须按【过程真实反序列化】（与生产端同源），不能裸读 JSON 键：
        ///    库里 JSON 常常键缺席（ST_002 实测整库无 "CardAlias" 字节），运行时靠
        ///    VisionPickPlaceConfig.CardAlias 的 C# 默认值 "EpsonRobot" 工作 —— 裸读 JSON 恒为空。
        /// 处置：过程需求与配方逻辑设备【同级】进入映射 Tab，同样从硬件池按类型绑定领用。
        /// </summary>
        private static List<RecipeDeviceMappingModel> GetProcessDeclaredDeviceRequirements(StationModel s)
        {
            var list = new List<RecipeDeviceMappingModel>();
            if (s == null || string.IsNullOrWhiteSpace(s.ProcessKey))
                return list;
            try
            {
                string cardAlias = null;
                switch (s.ProcessKey.Trim())
                {
                    case "VisionPickPlace":
                        var vp = string.IsNullOrWhiteSpace(s.ProcessConfigJson)
                            ? new VisionPickPlaceConfig()
                            : JsonConvert.DeserializeObject<VisionPickPlaceConfig>(s.ProcessConfigJson);
                        cardAlias = vp?.CardAlias;
                        break;
                    case "MahjongDualNozzle":
                        var md = string.IsNullOrWhiteSpace(s.ProcessConfigJson)
                            ? new MahjongDualNozzleConfig()
                            : JsonConvert.DeserializeObject<MahjongDualNozzleConfig>(s.ProcessConfigJson);
                        cardAlias = md?.CardAlias;
                        break;
                    default:
                        // 未注册类型：退回裸键读取（大小写敏感，拿不到就当无需求）
                        if (!string.IsNullOrWhiteSpace(s.ProcessConfigJson))
                        {
                            var jo = Newtonsoft.Json.Linq.JObject.Parse(s.ProcessConfigJson);
                            cardAlias = jo.Value<string>("CardAlias");
                        }
                        break;
                }

                if (!string.IsNullOrWhiteSpace(cardAlias))
                {
                    list.Add(new RecipeDeviceMappingModel
                    {
                        LogicalDeviceId = cardAlias.Trim(),
                        LogicalDeviceName = "过程需求·运动卡",
                        LogicalDeviceType = "MotionCard",
                        RequiredSpec = "业务过程 [" + s.ProcessKey.Trim() + "] 声明的运动控制卡（CardAlias）",
                    });
                }
            }
            catch (Exception ex)
            {
                // 解析失败不阻塞映射页；该行缺席会令就绪清单保持"未完成"，不会静默放行
                System.Diagnostics.Debug.WriteLine("[StationManage] 过程设备需求解析失败: " + ex.Message);
            }
            return list;
        }

        private void SyncRecipeMappings()
        {
            if (SelectedStation?.BoundRecipe == null) return;

            // 🌟 核心修复（2026-09-10）：改名场景下映射表可能残留旧 LogicalDeviceId。
            //   记录旧映射（含用户手动绑定的 MappedDeviceId），重建时按 ID/类型继承，
            //   避免"改个相机逻辑名字 → 逻辑映射 Tab 显示旧 ID / 丢失物理绑定"。
            var oldMappings = SelectedStation.RecipeDeviceMappings?
                .Where(m => m != null && !string.IsNullOrEmpty(m.LogicalDeviceId))
                .ToList()
                ?? new List<RecipeDeviceMappingModel>();

            SelectedStation.RecipeDeviceMappings.Clear();

            // ★★ 2026-09-15 修复（静默张冠李戴）：同一工位内【两个逻辑设备不得落到同一台物理设备】。
            //   旧代码的类型继承兜底 `FirstOrDefault(同类设备)` 不排除"已被别的逻辑设备占走的那台"，
            //   于是第二条 Camera 逻辑设备静默捡到第一台（上相机）。运行时 RegisterDevice 两次注册同一实例，
            //   无异常、无日志，链条照跑 —— 表现为"配的是下固定相机，出来的却是上相机画面"。
            //   现在：已被占用的物理设备不再被自动推断捡走；推断不出来就【留空】（运行时会响亮拦下），绝不猜。
            var claimedDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 物理设备键 -> 占用它的逻辑设备名

            if (SelectedStation.BoundRecipe.LogicalDevices != null)
            {
                foreach (var logical in SelectedStation.BoundRecipe.LogicalDevices)
                {
                    // 1. 优先继承旧映射里同 ID 的物理绑定（未改名）
                    var exact = oldMappings.FirstOrDefault(m =>
                        string.Equals(m.LogicalDeviceId, logical.LogicalDeviceId, StringComparison.OrdinalIgnoreCase));
                    string mappedId = exact?.MappedDeviceId;

                    // 1b. ★ 该物理设备已被同工位另一条逻辑设备占用 ⇒ 视为无效（原先会被静默沿用，病灶永久固化）
                    if (!string.IsNullOrEmpty(mappedId) && claimedDevices.ContainsKey(mappedId))
                    {
                        mappedId = null;
                    }

                    // 2. 改名兜底：同 ID 没找到 → 按类型继承（同类设备），★排除已被占用
                    if (string.IsNullOrEmpty(mappedId))
                    {
                        var sameType = oldMappings.FirstOrDefault(m =>
                            !string.IsNullOrEmpty(m.MappedDeviceId)
                            && !claimedDevices.ContainsKey(m.MappedDeviceId)
                            && string.Equals(m.LogicalDeviceType, logical.LogicalDeviceType, StringComparison.OrdinalIgnoreCase));
                        mappedId = sameType?.MappedDeviceId;
                    }

                    // 3. 默认首选硬件（★同样排除已被占用；★先在本工位找同类型，再扩大到全局池找同类型，
                    //    最后才退到"任意类型"——保证"相机逻辑设备被绑到机械手上"这种明显不同类的兜底排到最后）
                    if (string.IsNullOrEmpty(mappedId))
                    {
                        var preferredHardware = SelectedStation.HardwareDevices
                            .FirstOrDefault(h => h.DeviceType?.Equals(logical.LogicalDeviceType, StringComparison.OrdinalIgnoreCase) == true
                                                 && !claimedDevices.ContainsKey(h.DeviceId))
                            ?? GlobalHardwarePool.FirstOrDefault(h => h.DeviceType?.Equals(logical.LogicalDeviceType, StringComparison.OrdinalIgnoreCase) == true
                                                 && !claimedDevices.ContainsKey(h.DeviceId))
                            ?? SelectedStation.HardwareDevices.FirstOrDefault(h => !claimedDevices.ContainsKey(h.DeviceId))
                            ?? GlobalHardwarePool.FirstOrDefault(h => !claimedDevices.ContainsKey(h.DeviceId));
                        mappedId = preferredHardware?.DeviceId;
                    }

                    // 4. ★ 推断不出来 ⇒ 留空（不猜）。空映射会在 RefreshRecipePickerState 里显示为"未映射"，
                    //    运行时也会因"物理设备不在设备池/未映射"而响亮失败，而不是静默用错硬件。
                    if (!string.IsNullOrEmpty(mappedId))
                    {
                        var ownerName = string.IsNullOrWhiteSpace(logical.LogicalDeviceName)
                            ? logical.LogicalDeviceId
                            : logical.LogicalDeviceName;
                        claimedDevices[mappedId] = ownerName;
                    }

                    SelectedStation.RecipeDeviceMappings.Add(new RecipeDeviceMappingModel
                    {
                        LogicalDeviceId = logical.LogicalDeviceId,
                        LogicalDeviceName = logical.LogicalDeviceName,
                        LogicalDeviceType = logical.LogicalDeviceType,
                        RequiredSpec = logical.RequiredSpec,
                        MappedDeviceId = mappedId
                    });
                }
            }

            // ★★ 2026-09-16：业务过程声明的设备需求（如 VisionPickPlace 的 CardAlias=运动卡）
            //   与配方逻辑设备【同级】参与映射。旧链只从流程节点提取，运动卡永远缺席 ⇒
            //   工位上下文无 IMotionCard ⇒ 运行报"未解析到运动控制卡"。
            //   同样遵守"不猜"纪律：只按声明类型（MotionCard）从池里挑、排除已占用；
            //   推断不出就留空，映射 Tab 显示"未映射"，运行时响亮拦下 —— 绝不做"任意类型"兜底。
            foreach (var req in GetProcessDeclaredDeviceRequirements(SelectedStation))
            {
                if (SelectedStation.RecipeDeviceMappings.Any(m =>
                        string.Equals(m.LogicalDeviceId, req.LogicalDeviceId, StringComparison.OrdinalIgnoreCase)))
                    continue;   // 配方侧已声明同名逻辑设备（罕见），以配方为准

                var exactReq = oldMappings.FirstOrDefault(m =>
                    string.Equals(m.LogicalDeviceId, req.LogicalDeviceId, StringComparison.OrdinalIgnoreCase));
                string mappedReq = exactReq?.MappedDeviceId;
                if (!string.IsNullOrEmpty(mappedReq) && claimedDevices.ContainsKey(mappedReq))
                    mappedReq = null;   // 已被别的逻辑设备占用 ⇒ 视为无效，重新推断

                if (string.IsNullOrEmpty(mappedReq))
                {
                    var hw = SelectedStation.HardwareDevices
                        .FirstOrDefault(h => h.DeviceType?.Equals(req.LogicalDeviceType, StringComparison.OrdinalIgnoreCase) == true
                                             && !claimedDevices.ContainsKey(h.DeviceId))
                        ?? GlobalHardwarePool.FirstOrDefault(h => h.DeviceType?.Equals(req.LogicalDeviceType, StringComparison.OrdinalIgnoreCase) == true
                                             && !claimedDevices.ContainsKey(h.DeviceId));
                    mappedReq = hw?.DeviceId;   // 仍可能为 null ⇒ 留空
                }

                if (!string.IsNullOrEmpty(mappedReq))
                    claimedDevices[mappedReq] = string.IsNullOrWhiteSpace(req.LogicalDeviceName)
                        ? req.LogicalDeviceId : req.LogicalDeviceName;

                SelectedStation.RecipeDeviceMappings.Add(new RecipeDeviceMappingModel
                {
                    LogicalDeviceId = req.LogicalDeviceId,
                    LogicalDeviceName = req.LogicalDeviceName,
                    LogicalDeviceType = req.LogicalDeviceType,
                    RequiredSpec = req.RequiredSpec,
                    MappedDeviceId = mappedReq
                });
            }
            RefreshReadiness();
        }

        #endregion

        #region 纯数据库保存（不涉及 Runtime 操作）

        /// <summary>
        /// 仅保存工位配置到数据库，不涉及 Runtime 操作和硬件同步。
        /// 用于添加/删除硬件后立即保存，避免触发 Runtime 同步导致数据丢失。
        /// </summary>
        private async Task SaveStationToDatabaseAsync()
        {
            if (SelectedStation == null) return;

            try
            {
                // 在仅保存数据库时同样回写映射并持久化配方，避免配方视图状态滞后
                if (!SyncMappingsBackToBoundRecipe(SelectedStation, _recipeStorage))
                {
                    MessageBox.Show("保存配方设备映射信息失败！", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var stationConfig = ToStationConfigModel(SelectedStation);
                if (!_configService.SaveStation(stationConfig))
                {
                    MessageBox.Show("工位配置保存到数据库失败！", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 仅显示保存成功提示，不进行 Runtime 操作
                // (Runtime 操作由用户显式点击"保存工位并同步 Runtime"按钮时触发)
                RefreshReadiness();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存工位配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 把工位侧最新的 RecipeDeviceMappings 同步回写到 BoundRecipe.LogicalDevices，
        /// 并调用 recipe 存储持久化，确保配方管理视图中的 IsBoundToPhysical 状态与工位侧保持一致。
        /// </summary>
        private static bool SyncMappingsBackToBoundRecipe(
            StationModel station,
            IRecipeStorageService recipeStorage)
        {
            var recipe = station?.BoundRecipe;
            if (recipe == null) return true;

            if (recipe.LogicalDevices == null)
            {
                recipe.LogicalDevices = new List<RecipeDeviceMappingModel>();
            }

            var stationMappings = station.RecipeDeviceMappings
                ?.Where(m => !string.IsNullOrEmpty(m.LogicalDeviceId))
                .ToDictionary(m => m.LogicalDeviceId, m => m.MappedDeviceId)
                ?? new Dictionary<string, string>();

            foreach (var logical in recipe.LogicalDevices.Where(l => !string.IsNullOrEmpty(l.LogicalDeviceId)))
            {
                if (stationMappings.TryGetValue(logical.LogicalDeviceId, out var mappedId))
                {
                    logical.MappedDeviceId = mappedId;
                }
            }

            return recipeStorage?.SaveRecipe(recipe) ?? true;
        }

        #endregion

        #region 对接 Core 的保存与运行逻辑

        private async Task OnSaveStationConfigAsync()
        {
            if (SelectedStation == null) return;

            try
            {
                // 1. 将 station 侧更新的设备映射回写到绑定的配方模型并持久化，保证 RecipeManageView 显示一致
                if (!SyncMappingsBackToBoundRecipe(SelectedStation, _recipeStorage))
                {
                    MessageBox.Show("保存配方设备映射信息失败！", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 2. 先持久化配置到数据库（包括 LeaseDeviceIds 和 DeviceMappings）
                var stationConfig = ToStationConfigModel(SelectedStation);
                //Console.WriteLine(JsonConvert.SerializeObject(stationConfig));
                if (!_configService.SaveStation(stationConfig))
                {
                    MessageBox.Show("工位配置保存到数据库失败！", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 3. 同步 Core 运行时：按 BoundRecipeId 加载完整配方，避免 StationConfigPo 过深嵌套
                var boundRecipe = !string.IsNullOrEmpty(stationConfig.BoundRecipeId)
                    ? _recipeStorage.LoadRecipe(stationConfig.BoundRecipeId)
                    : null;

                var hostRuntime = App.StationHostRuntime as IStationHostRuntime
                                  ?? StationHostRuntime.GlobalInstance;

                if (hostRuntime != null && hostRuntime.GetStationClient(SelectedStation.StationCode) != null)
                {
                    hostRuntime.RemoveStation(SelectedStation.StationCode);
                }

                IWorkerClient client = null;
                if (hostRuntime != null)
                {
                    // 装配触发源（传配置到 Core，工位 Start 后自动启动信号监听）
                    client = await hostRuntime.CreateStationWithRecipeAsync(
                        SelectedStation.StationCode,
                        boundRecipe,
                        stationConfig.DeviceMappings,
                        WorkMode.Production,
                        stationConfig.TriggerSource,
                        stationConfig.ProcessKey,
                        stationConfig.ProcessConfigJson,
                        stationConfig.TaskTemplateCode);
                }

                if (client == null)
                {
                    // 3. 如果 Core 站点创建失败，尝试兼容旧路径
                    client = await _runtimeManager.CreateAndConnectStationAsync(
                        SelectedStation.StationCode,
                        WorkerConnectMode.Embedded);

                    if (boundRecipe?.MainProcess != null && client != null)
                    {
                        await client.LoadRecipeAsync(boundRecipe.MainProcess);
                    }
                }

                // 注意：不调用 SyncHardwareFromRuntimeLeases，因为：
                // 1. UI 中的 HardwareDevices 已经通过 LeaseDeviceIds 持久化到数据库
                // 2. Runtime 中的租赁关系是实时的，不应该用来覆盖用户在 UI 中的配置
                // 3. 如果从 Runtime 租赁同步，反而会丢失硬件列表（当 Runtime 中没有租赁时）

                MessageBox.Show($"工位 [{SelectedStation.StationName}] ({SelectedStation.StationCode}) 配置已保存并初始化 Core 站点成功！",
                                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
                // 引擎 ProcessKey 落库成功 → 按最新引擎重挂 参数/示教 面板（就地编辑）
                AttachEngineTeachPanel();
                RefreshReadiness();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存工位或初始化 Core 站点失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private ContractsStationConfig ToStationConfigModel(StationModel station)
        {
            var config = new ContractsStationConfig
            {
                StationId = string.IsNullOrEmpty(station.StationId) ? _configService.GenerateStationId() : station.StationId,
                StationCode = station.StationCode,
                StationName = station.StationName,
                LineId = SelectedLine?.LineId,
                LineName = SelectedLine?.LineName,
                IsEnabled = station.IsEnabled,
                TimeoutMs = station.TimeoutMs,
                BoundRecipeId = station.BoundRecipe?.RecipeId,
                BoundRecipeName = station.BoundRecipe?.RecipeName
            };

            // 保存工位领用的硬件设备 ID 列表
            config.LeaseDeviceIds = new List<string>();
            if (station.HardwareDevices != null)
            {
                foreach (var device in station.HardwareDevices)
                {
                    config.LeaseDeviceIds.Add(device.DeviceId);
                }
            }

            // 保存配方中逻辑设备映射关系
            config.DeviceMappings = new List<RecipeDeviceMappingModel>();
            if (station.RecipeDeviceMappings != null)
            {
                foreach (var mapping in station.RecipeDeviceMappings)
                {
                    config.DeviceMappings.Add(mapping);
                }
            }

            // 保存触发源配置
            config.TriggerSource = station.ToTriggerSourceConfig();

            // 保存业务过程绑定（机器结构级时序，不随产品变化；stage9-2：由任务模板绑定派生）
            config.ProcessKey = string.IsNullOrWhiteSpace(station.ProcessKey) ? null : station.ProcessKey.Trim();
            config.ProcessConfigJson = station.ProcessConfigJson;

            // 保存任务模板绑定（任务模板=工位任务唯一入口）
            config.TaskTemplateCode = string.IsNullOrWhiteSpace(station.TaskTemplateCode) ? null : station.TaskTemplateCode.Trim();
            config.TaskTemplateName = station.TaskTemplateName;
            config.TaskTemplateKindText = station.TaskTemplateKindText;

            return config;
        }

        #endregion

        #region 节点与硬件增删逻辑

        private string GenerateUniqueLineName()
        {
            var existingNames = new HashSet<string>(
                ProductionLines
                    .Select(l => l.LineName)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            var index = 1;
            string candidate;
            do
            {
                candidate = $"新产线_{index:D3}";
                index++;
            }
            while (existingNames.Contains(candidate));

            return candidate;
        }

        private void OnAddLine()
        {
            var newLine = new LineModel
            {
                LineId = _configService.GenerateLineId(),
                LineName = GenerateUniqueLineName()
            };
            ProductionLines.Add(newLine);
            OnPropertyChanged(nameof(HasAnyLine));
            SelectedLine = newLine;
            // 新产线从"零工位"开始：清掉旧选择，右侧进入该产线空态引导（用户视角下一步=向导建工位），
            // 也避免"SelectedStation 属于旧产线 + SelectedLine 指向新产线"的保存错位。
            SelectedStation = null;
        }

        private string GenerateUniqueStationCode()
        {
            var existingCodes = new HashSet<string>(
                ProductionLines
                    .SelectMany(l => l.Stations ?? new ObservableCollection<StationModel>())
                    .Select(s => s.StationCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code)),
                StringComparer.OrdinalIgnoreCase);

            var index = 1;
            string candidate;
            do
            {
                candidate = $"ST_{index:D3}";
                index++;
            }
            while (existingCodes.Contains(candidate));

            return candidate;
        }

        private string GenerateUniqueStationName(LineModel line)
        {
            var existingNames = new HashSet<string>(
                (line?.Stations ?? new ObservableCollection<StationModel>())
                    .Select(s => s.StationName)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            var index = 1;
            string candidate;
            do
            {
                candidate = $"新工位_{index:D3}";
                index++;
            }
            while (existingNames.Contains(candidate));

            return candidate;
        }

        private void OnAddStation()
        {
            if (SelectedLine == null)
            {
                MessageBox.Show("请先在左侧选择需要添加工位的产线！", "操作提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var line = SelectedLine;
            // 需求驱动新建向导（v1.1 §0.2 问卷 + §0.3 实时推导）：取代"直接生成空壳"的旧行为。
            // 建议代码/名称由现有唯一命名规则生成，向导内可改；创建前做唯一性校验。
            var win = new View.StationWizardWindow(line.LineId, line.LineName,
                GenerateUniqueStationCode(), GenerateUniqueStationName(line))
            {
                Owner = Application.Current.MainWindow
            };

            if (win.ShowDialog() != true || !(win.DataContext is StationWizardViewModel wvm))
            {
                return; // 取消
            }

            // 仅存草稿：已落盘 StationProfile(Draft)，不建真实工位（树不插入）
            if (wvm.ExitAsDraft)
            {
                MessageBox.Show(
                    $"方案草稿已保存：{wvm.ResultProfile.StationName}（代码 {wvm.ResultProfile.StationCode}）\n" +
                    $"随时可在向导『载入草稿』续编，或回到列表点【+ 工位】重新进入向导继续创建。",
                    "已存草稿", MessageBoxButton.OK, MessageBoxImage.Information);
                LogBus.Info("StationWizard",
                    $"工位方案草稿保存: 代码={wvm.ResultProfile.StationCode} 名称={wvm.ResultProfile.StationName} " +
                    $"任务={wvm.ResultProfile.Requirement?.TaskType ?? "未填"} 模板={wvm.ResultProfile.IndustryTemplateCode ?? "自由问卷"}");
                return;
            }

            CreateStationFromWizard(wvm.ResultProfile);
        }

        /// <summary>
        /// 向导产物 → 真实工位（替换旧"空壳新建"链路）：
        /// 1) 代码唯一性校验 → 2) 建 StationModel 入树 → 3) 复用 AutoPersistAndRegisterIfEnabledAsync
        ///    （SaveStation 落库 + 注册 Runtime）→ 4) 成功后补 StationId/Status=Active 落 StationProfile 档案。
        /// 保存失败回滚树节点。
        /// </summary>
        private async void CreateStationFromWizard(StationProfile profile)
        {
            var line = SelectedLine;
            if (line == null) return;

            bool dup = line.Stations.Any(s => string.Equals(s.StationCode, profile.StationCode, StringComparison.OrdinalIgnoreCase));
            if (dup)
            {
                MessageBox.Show($"工位代码 [{profile.StationCode}] 已在该产线下存在，请返回向导修改后再创建。",
                    "代码冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var newStation = new StationModel
            {
                StationId = _configService.GenerateStationId(),
                StationCode = profile.StationCode,
                StationName = profile.StationName,
                IsEnabled = profile.IsEnabled,
                TimeoutMs = profile.TimeoutMs
            };
            line.Stations.Add(newStation);
            SelectedStation = newStation;

            bool persisted = await AutoPersistAndRegisterIfEnabledAsync(newStation);
            if (!persisted)
            {
                line.Stations.Remove(newStation);
                SelectedStation = line.Stations.FirstOrDefault();
                return;
            }

            // 关联并存档需求元数据（Active）：后续工作台 S 页读此档案做"待补"提示与建议刷新
            profile.StationId = newStation.StationId;
            profile.StationCode = newStation.StationCode;
            profile.LineId = line.LineId;
            profile.LineName = line.LineName;
            profile.Status = StationProfileStatus.Active;
            bool saved = new StationProfileRepository().Save(profile);

            LogBus.Info("StationWizard",
                $"向导创建工位成功: {profile.StationCode} 名称={profile.StationName} 任务={profile.Requirement?.TaskType ?? "未填"} " +
                $"建议链={profile.SuggestedFlowSkeleton} 档案落盘={saved}");
            MessageBox.Show(
                $"工位 [{profile.StationName}] 创建成功。\n\n" +
                $"建议视觉链：{profile.SuggestedFlowSkeleton}\n" +
                $"建议标定：{profile.CalibrationSuggestion}\n" +
                $"待建资产：\n{string.Join("\n", (profile.AssetSuggestions ?? new System.Collections.Generic.List<string>()).Select(a => "· " + a))}\n\n" +
                "请在右侧工作台逐项确认/细化（S1 硬件绑定 → S2 模板 → S3 标定 → S4 配方）。",
                "创建工位", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task<bool> AutoPersistAndRegisterIfEnabledAsync(StationModel station)
        {
            if (station == null) return false;

            try
            {
                var stationConfig = ToStationConfigModel(station);
                if (!_configService.SaveStation(stationConfig))
                {
                    MessageBox.Show("新增工位自动保存失败！", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // 禁用工位：仅自动保存，不注册 Runtime
                if (!station.IsEnabled)
                {
                    return true;
                }

                var hostRuntime = App.StationHostRuntime as IStationHostRuntime
                                  ?? StationHostRuntime.GlobalInstance;
                if (hostRuntime == null)
                {
                    return true;
                }

                if (hostRuntime.GetStationClient(station.StationCode) != null)
                {
                    hostRuntime.RemoveStation(station.StationCode);
                }

                var boundRecipe = !string.IsNullOrEmpty(stationConfig.BoundRecipeId)
                    ? _recipeStorage.LoadRecipe(stationConfig.BoundRecipeId)
                    : null;

                await hostRuntime.CreateStationWithRecipeAsync(
                    station.StationCode,
                    boundRecipe,
                    stationConfig.DeviceMappings,
                    WorkMode.Production,
                    null,
                    stationConfig.ProcessKey,
                    stationConfig.ProcessConfigJson,
                    stationConfig.TaskTemplateCode);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"新增工位自动保存/注册失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void OnDeleteNode()
        {
            if (SelectedStation != null && SelectedLine != null)
            {
                if (MessageBox.Show($"确定要删除工位 [{SelectedStation.StationName}] 吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    // 1. 同步释放 Core 运行时中该工位领用的设备
                    var hostRuntime = App.StationHostRuntime as IStationHostRuntime
                                      ?? StationHostRuntime.GlobalInstance;
                    hostRuntime?.RemoveStation(SelectedStation.StationCode);

                    // 2. 删除数据库配置
                    _configService.DeleteStation(SelectedStation.StationId);
                    // 3. 联动清理方案档案（Active StationId.json）
                    new StationProfileRepository().Delete(null, SelectedStation.StationId);
                    SelectedLine.Stations.Remove(SelectedStation);
                    SelectedStation = SelectedLine.Stations.FirstOrDefault();
                }
            }
                else if (SelectedLine != null)
            {
                if (MessageBox.Show($"确定要删除产线 [{SelectedLine.LineName}] 及其下属所有工位吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    // 1. 同步释放 Core 运行时中产线下所有工位领用的设备
                    var hostRuntime = App.StationHostRuntime as IStationHostRuntime
                                      ?? StationHostRuntime.GlobalInstance;
                    foreach (var station in SelectedLine.Stations.ToList())
                    {
                        hostRuntime?.RemoveStation(station.StationCode);
                        _configService.DeleteStation(station.StationId);
                        new StationProfileRepository().Delete(null, station.StationId);
                    }

                    // 2. 删除产线本身
                    _configService.DeleteLine(SelectedLine.LineId);
                    ProductionLines.Remove(SelectedLine);
                    OnPropertyChanged(nameof(HasAnyLine));
                    SelectedLine = ProductionLines.FirstOrDefault();
                    SelectedStation = null;
                }
            }
        }

  

        private async void OnAddHardware()
        {
            if (SelectedStation == null) return;

            // 1. 过滤出全局硬件池中尚未被当前工位领用的硬件
            var unassignedDevices = GlobalHardwarePool
                .Where(g => !SelectedStation.HardwareDevices.Any(h => h.DeviceId == g.DeviceId))
                .ToList();

            if (!unassignedDevices.Any())
            {
                MessageBox.Show("【全局硬件池】中的所有设备已全部领用，或当前无可用硬件设备！",
                                "领用提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 2. 实例化弹窗 View & ViewModel
            var selectVm = new HardwareSelectViewModel(unassignedDevices);
            var dialog = new View.HardwareSelectWindow(selectVm)
            {
                Owner = Application.Current.MainWindow
            };

            // 3. 打开模态弹窗并接收选中的硬件列表
            if (dialog.ShowDialog() == true)
            {
                var selectedDevices = selectVm.GetSelectedDevices();
                if (selectedDevices.Count == 0)
                {
                    MessageBox.Show("未勾选任何硬件设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                foreach (var device in selectedDevices)
                {
                    SelectedStation.HardwareDevices.Add(device);
                }

                MessageBox.Show($"已成功将 {selectedDevices.Count} 台硬件设备领用并添加到工位 [{SelectedStation.StationName}]！",
                                "领用成功", MessageBoxButton.OK, MessageBoxImage.Information);

                // 4. 仅保存到数据库，不涉及 Runtime 操作（避免触发 SyncHardwareFromRuntimeLeases 清空数据）
                await SaveStationToDatabaseAsync();
            }
        }
        private async void OnRemoveHardware()
        {
            if (SelectedStation != null && SelectedHardware != null)
            {
                string devName = SelectedHardware.DeviceName;
                SelectedStation.HardwareDevices.Remove(SelectedHardware);
                SelectedHardware = null;

                MessageBox.Show($"硬件设备 [{devName}] 已从本工位领用列表中移除！", "移除成功", MessageBoxButton.OK, MessageBoxImage.Information);

                // 仅保存到数据库，不涉及 Runtime 操作（避免触发 SyncHardwareFromRuntimeLeases 清空数据）
                await SaveStationToDatabaseAsync();
            }
        }

        public void OnSelectedNodeChanged(object selectedItem)
        {
            if (selectedItem is LineModel line)
            {
                SelectedLine = line;
                SelectedStation = null;
            }
            else if (selectedItem is StationModel station)
            {
                SelectedStation = station;
                SelectedLine = ProductionLines.FirstOrDefault(l => l.Stations.Contains(station));
            }
        }

        #endregion

        #region 真实数据初始化

        private void InitializeFromServices()
        {
            GlobalHardwarePool.Clear();
            foreach (var device in _configService.LoadAvailableHardwareDevices())
            {
                GlobalHardwarePool.Add(device);
            }

            AvailableRecipes.Clear();
            foreach (var recipe in _recipeStorage.GetAllRecipes())
            {
                AvailableRecipes.Add(recipe);
            }
            OnPropertyChanged(nameof(HasRecipesAvailable));

            ProductionLines.Clear();
            foreach (var lineConfig in _configService.LoadAllLines())
            {
                ProductionLines.Add(MapFromLineConfig(lineConfig));
            }
            OnPropertyChanged(nameof(HasAnyLine));

            RefreshAllStationBadges();
            // 首帧空态文案（无产线/有产线未选工位），供右侧空态引导层显示
            RefreshEmptyGuide();
        }

        /// <summary>
        /// 根据 Core 设备池实际租赁记录，把 UI 本地工位的 HardwareDevices 同步为租赁设备。
        /// </summary>
        private void SyncHardwareFromRuntimeLeases(IStationHostRuntime hostRuntime, StationModel station)
        {
            if (hostRuntime == null || station == null) return;

            var leasedKeys = hostRuntime.DevicePool?.GetLeasedDeviceKeys(station.StationCode) ?? Enumerable.Empty<string>();
            var leasedDevices = leasedKeys
                .Select(key => GlobalHardwarePool.FirstOrDefault(h => h.DeviceId == key))
                .Where(h => h != null)
                .ToList();

            station.HardwareDevices.Clear();
            foreach (var device in leasedDevices)
            {
                station.HardwareDevices.Add(device);
            }
        }

        private LineModel MapFromLineConfig(ContractsLineConfig lineConfig)
        {
            var line = new LineModel
            {
                LineId = lineConfig.LineId,
                LineName = lineConfig.LineName
            };

            foreach (var stationConfig in lineConfig.Stations ?? new List<ContractsStationConfig>())
            {
                var station = new StationModel
                {
                    StationId = stationConfig.StationId,
                    StationCode = stationConfig.StationCode,
                    StationName = stationConfig.StationName,
                    IsEnabled = stationConfig.IsEnabled,
                    TimeoutMs = stationConfig.TimeoutMs,
                    BoundRecipe = !string.IsNullOrEmpty(stationConfig.BoundRecipeId)
                        ? AvailableRecipes.FirstOrDefault(r => r.RecipeId == stationConfig.BoundRecipeId)
                        : null
                };

                // 从持久化数据恢复工位领用的硬件设备列表
                if (stationConfig.LeaseDeviceIds != null && stationConfig.LeaseDeviceIds.Count > 0)
                {
                    foreach (var deviceId in stationConfig.LeaseDeviceIds)
                    {
                        var hardware = GlobalHardwarePool.FirstOrDefault(h => h.DeviceId == deviceId);
                        if (hardware != null && !station.HardwareDevices.Any(h => h.DeviceId == deviceId))
                        {
                            station.HardwareDevices.Add(hardware);
                        }
                    }
                }

                // 从持久化数据恢复配方中的逻辑设备映射关系
                if (stationConfig.DeviceMappings != null)
                {
                    foreach (var mapping in stationConfig.DeviceMappings)
                    {
                        station.RecipeDeviceMappings.Add(mapping);
                    }
                }

                // 从持久化数据恢复触发源配置
                station.FromTriggerSourceConfig(stationConfig.TriggerSource);

                // 从持久化数据恢复业务过程绑定
                station.ProcessKey = stationConfig.ProcessKey;
                station.ProcessConfigJson = stationConfig.ProcessConfigJson;

                // 从持久化数据恢复任务模板绑定（stage9-2：模板=唯一入口）
                station.TaskTemplateCode = stationConfig.TaskTemplateCode;
                station.TaskTemplateName = stationConfig.TaskTemplateName;
                station.TaskTemplateKindText = stationConfig.TaskTemplateKindText;

                line.Stations.Add(station);
            }

            return line;
        }


        #endregion
    }
}