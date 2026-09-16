// Grayson.Vision.WpfUI.ViewModel/StationMonitorViewModel.cs
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.WorkOrderTracking;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Core.Client;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vision.HalconWrapper.Wpf.Controls;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Newtonsoft.Json;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class StationLogEntry : ViewModelBase
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Level { get; set; } = "INFO";
        public string Message { get; set; }
    }

    public class StationResultEntry : ViewModelBase
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string BatchId { get; set; }
        public string Result { get; set; }
        public string Message { get; set; }
    }

    public class StationMonitorViewModel : ViewModelBase, INavigationAware
    {
        private readonly StationRuntimeManager _runtimeManager;
        private readonly StationConfigService _configService;
        private readonly Grayson.Vision.Contracts.Recipe.Services.IRecipeStorageService _recipeStorage;
        private readonly StationStatisticsService _statsService = new StationStatisticsService();
        private IWorkerClient _activeClient;
        private readonly HalconImageRenderService _renderService;

        /// <summary>最近一次 ConnectToStationAsync 的目标工位编码（判定「是否真的换了工位」：同工位重连不清图像现场）。</summary>
        private string _lastConnectedStationCode;

        /// <summary>日志总线订阅标记（防重复订阅：仅当导航进入本页且未订阅时挂接）。</summary>
        private bool _busLogSubscribed;

        /// <summary>连接请求序号：后发请求应覆盖先发请求，过期请求异步完成时自弃（防 ActiveClient 与下拉选中脱钩）。</summary>
        private int _connectSeq;

        // ===== 业务周期轮询（统计持久化 + 进行中状态 + Live 联动）=====
        // 业务周期（含 50s 机械动作）结束时没有任何事件可订阅——权威数据源是
        // StationWorker.Metrics（内存态、无通知），因此用 500ms 轮询驱动：
        //   · IsProcessBusy 边沿 → LastResult「进行中」/周期结束判定
        //   · Metrics 水位法 → 累计计数并 JSON 持久化（离开界面/重启不丢）
        private readonly DispatcherTimer _pollTimer;
        private bool _lastProcessBusy;
        private readonly Stopwatch _cycleWatch = new Stopwatch();

        /// <summary>最近一次已消费的完整周期定案计数（StationMetrics.FinalizedWorkOrders 单调增）。
        /// 轮询以此检测「新周期定案」，不依赖 IsProcessBusy 边沿（快节奏/背靠背自动节拍可能漏采
        /// false 样本 → LastResult 卡在『进行中』）。工位切换/重启时与当前水位重新对齐。</summary>
        private long _lastAppliedCompletedCount = -1;
        private string _lastAppliedCompletedStation;

        // ===== Live 相机实时画面 =====
        // 行业标准显示策略：吸嘴吸取前看「模板匹配结果+底图」（场景叠加层），
        // 视觉链完成后自动切到相机 Live（吸嘴走位/吸取过程可见），下次触发前自动让出相机。
        private const string LiveNodeId = "live_camera_view";
        private ICamera _liveCamera;
        private volatile bool _liveGrabbing;
        private DateTime _lastLivePushUtc = DateTime.MinValue;
        /// <summary>Live 状态迁移锁：让渡可能来自 UI 线程/执行链线程/相机线程，须互斥。</summary>
        private readonly object _liveSync = new object();

        /// <summary>
        /// 本视图 Halcon 显示控件的预览适配器（IFlowPreviewContext）。
        /// 生产执行链的节点叠加图形（模板匹配贴合轮廓/十字/文本）经它实时画到
        /// 工位监视窗口——适配器只在代码后台创建（不进 XAML，MC1000 约束）。
        /// </summary>
        private HalconDisplayContextAdapter _previewAdapter;

        private int _totalCount;
        public int TotalCount { get => _totalCount; set { if (Set(ref _totalCount, value)) OnPropertyChanged(nameof(YieldText)); } }

        private int _okCount;
        public int OkCount { get => _okCount; set { if (Set(ref _okCount, value)) OnPropertyChanged(nameof(YieldText)); } }

        private int _errorCount;
        /// <summary>
        /// 异常/错误工单数（从 StationStatistics 获取）
        /// </summary>
        public int ErrorCount { get => _errorCount; set { if (Set(ref _errorCount, value)) OnPropertyChanged(nameof(YieldText)); } }

        private int _faultCount;
        /// <summary>
        /// 故障次数（从 StationStatistics 获取）
        /// 用于监控设备可靠性
        /// </summary>
        public int FaultCount { get => _faultCount; set => Set(ref _faultCount, value); }

        private DateTime? _firstDefectTime;
        /// <summary>
        /// 本周期首次缺陷出现时间（从 StationStatistics 获取）
        /// 用于快速定位问题起点
        /// </summary>
        public DateTime? FirstDefectTime { get => _firstDefectTime; set => Set(ref _firstDefectTime, value); }

        private string _lastDefectDescription;
        /// <summary>
        /// 最后一次缺陷描述（从 StationStatistics 获取）
        /// 示例："尺寸超差 5mm", "表面划伤"
        /// </summary>
        public string LastDefectDescription { get => _lastDefectDescription; set => Set(ref _lastDefectDescription, value); }

        private string _runtimeStatusSummary;
        /// <summary>
        /// 运行状态文字摘要（从 StationRuntimeStatus 获取）
        /// 用于快速判断设备健康状态
        /// </summary>
        public string RuntimeStatusSummary { get => _runtimeStatusSummary; set => Set(ref _runtimeStatusSummary, value); }

        private string _lastErrorMessage;
        /// <summary>
        /// 最后错误信息（从 StationRuntimeStatus 获取）
        /// 显示最近发生的问题
        /// </summary>
        public string LastErrorMessage { get => _lastErrorMessage; set => Set(ref _lastErrorMessage, value); }

        private double _averageCycleTimeMs;
        /// <summary>
        /// 平均周期时间（从 StationStatistics 获取）
        /// 用于评估产能和对标标准周期时间
        /// </summary>
        public double AverageCycleTimeMs { get => _averageCycleTimeMs; set => Set(ref _averageCycleTimeMs, value); }

        private int _ngCount;
        public int NgCount { get => _ngCount; set { if (Set(ref _ngCount, value)) OnPropertyChanged(nameof(YieldText)); } }

        private double _cycleTimeMs;
        /// <summary>
        /// 最近一次检测链总耗时 (ms)
        /// </summary>
        public double CycleTimeMs { get => _cycleTimeMs; set => Set(ref _cycleTimeMs, value); }

        private string _lastResult = "--";
        /// <summary>
        /// 最近一次判定结果 (OK / NG / 进行中 / --)。
        /// 业务周期进行中显示「进行中」——视觉链完成（周期中段）不再提前报 OK。
        /// </summary>
        public string LastResult { get => _lastResult; set => Set(ref _lastResult, value); }

        private bool _isCycleRunning;
        /// <summary>
        /// 业务周期是否正在执行（StationWorker.IsProcessBusy）。
        /// 为 true 时【单次触发】按钮禁用（防并发触发撞机，Core 侧互斥锁也会拒绝）。
        /// </summary>
        public bool IsCycleRunning
        {
            get => _isCycleRunning;
            private set
            {
                if (Set(ref _isCycleRunning, value))
                {
                    RefreshCommandStates();
                }
            }
        }

        private bool _isLiveViewOn;
        /// <summary>相机实时画面是否开启（Live 抓流中）。</summary>
        public bool IsLiveViewOn
        {
            get => _isLiveViewOn;
            private set => Set(ref _isLiveViewOn, value);
        }

        public string YieldText
        {
            get
            {
                if (TotalCount <= 0) return "--";
                double yield = (double)OkCount / TotalCount * 100;
                return $"{yield:F1}%";
            }
        }

        public StationMonitorViewModel(
            StationRuntimeManager runtimeManager = null,
            StationConfigService configService = null,
            Grayson.Vision.Contracts.Recipe.Services.IRecipeStorageService recipeStorage = null)
        {
            _runtimeManager = runtimeManager ?? new StationRuntimeManager();
            _configService = configService ?? new StationConfigService();
            _recipeStorage = recipeStorage ?? Grayson.Vision.Repository.Services.RecipeStorageFactory.CreateRecipeStorageService();

            // 1. 初始化图像展示 ViewModel
            _renderService = new HalconImageRenderService();
            ImageDisplayVm = new ImageDisplayVm(_renderService);

            Logs = new ObservableCollection<StationLogEntry>();
            Results = new ObservableCollection<StationResultEntry>();

            StartCommand = new RelayCommand(async _ => await StartAsync(), _ => ActiveClient != null && (State == "Idle" || State == "Stopped"));
            StopCommand = new RelayCommand(async _ => await StopAsync(), _ => ActiveClient != null && (State == "Running" || State == "Paused"));
            PauseCommand = new RelayCommand(async _ => await PauseAsync(), _ => ActiveClient != null && State == "Running");
            ResumeCommand = new RelayCommand(async _ => await ResumeAsync(), _ => ActiveClient != null && State == "Paused");
            TriggerOnceCommand = new RelayCommand(async _ => await TriggerOnceAsync(),
                _ => ActiveClient != null && !IsCycleRunning && (State == "Idle" || State == "Running" || State == "Paused"));
            ResetCommand = new RelayCommand(async _ => await SoftResetAsync(), _ => ActiveClient != null);
            WorkOrderResetCommand = new RelayCommand(async _ => await WorkOrderResetAsync(), _ => ActiveClient != null);
            HardwareResetCommand = new RelayCommand(async _ => await HardwareResetAsync(), _ => ActiveClient != null);
            EmergencyStopCommand = new RelayCommand(async _ => await EmergencyStopAsync(), _ => ActiveClient != null && (State == "Running" || State == "Paused" || State == "Idle"));
            ClearLogsCommand = new RelayCommand(_ => Logs.Clear());
            LiveViewCommand = new RelayCommand(() => ToggleLiveView(), () => ActiveClient != null);
            // 任务引导卡：参数/示教表单入口（迁至工位工作台 ④ 执行方案 Tab）
            GoEngineConfigCommand = new RelayCommand(_ => OnGoEngineConfig(), _ => !string.IsNullOrEmpty(SelectedStationCode));

            // 业务周期轮询：统计持久化 + 「进行中」状态 + Live 画面联动（详见 OnPollTimerTick）
            _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _pollTimer.Tick += (s, e) => OnPollTimerTick();
            _pollTimer.Start();

            LoadAvailableStations();
        }

        /// <summary>
        /// 图像显示 VM，用于直接绑定 View 层的 Halcon 空间
        /// </summary>
        public ImageDisplayVm ImageDisplayVm { get; }

        /// <summary>
        /// 绑定本视图的 Halcon 显示控件为节点实时预览上下文。
        /// 由 View 构造时传入（x:Name 引用）；之后在连接工位/触发自愈点
        /// 反复调 EnsurePreviewAttached 把适配器武装到 Worker。
        /// </summary>
        public void AttachDisplayHost(HalconImageDisplayHost host)
        {
            if (host == null) return;
            host.LogTag = "工位监视页"; // 显示层日志带窗口身份（与编辑器主视图/属性面板并存时才分得清谁画了、谁清了）
            _previewAdapter = new HalconDisplayContextAdapter(host);
            EnsurePreviewAttached();
            AddLog("INFO", "视觉预览已绑定：模板匹配轮廓等节点叠加图形将实时显示在监视窗口。");
        }

        /// <summary>
        /// 把预览适配器注入当前工位 Worker（幂等）。
        /// 放在生产侧触发点反复调用：FlowEdit 打开属性面板调试节点时会把
        /// 共享 Worker 的预览上下文换成编辑器自己的适配器——回到本页
        /// 启动/单次触发前重新武装，保证叠加图形仍画到工位监视窗口。
        /// </summary>
        private void EnsurePreviewAttached()
        {
            try
            {
                if (_previewAdapter != null && ActiveClient != null)
                {
                    ActiveClient.SetPreviewContext(_previewAdapter);
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"绑定视觉预览上下文失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 🌟 兜底(2026-09-09 #2 根治)：把本页 HALCON 预览适配器从【所有其它工位】Worker 上摘除，
        /// 仅保留当前 stationCode 的工位持有。极端异常路径（切换中抛错、_activeClient 记录漂移）
        /// 可能让仍在后台自跑的其它工位(如 005)残留本页预览引用 → 其节点绘制仍画进本页窗口。
        /// DetachClientEvents 已处理常规切换；本方法做全量核对，确保绝不残留。
        /// </summary>
        private void EnsureOnlyThisStationHasPreview(string stationCode)
        {
            try
            {
                if (!(App.StationHostRuntime is Grayson.Vision.Core.Station.StationHostRuntime runtime))
                    return;

                foreach (var otherCode in runtime.GetStationIds())
                {
                    if (string.IsNullOrEmpty(otherCode)) continue;
                    if (string.Equals(otherCode, stationCode, StringComparison.OrdinalIgnoreCase)) continue;
                    var worker = runtime.GetStationWorker(otherCode);
                    if (worker == null) continue;
                    try { worker.SetPreviewContext(null); }
                    catch { /* 摘除失败不阻塞连接 */ }
                }
            }
            catch { /* 兜底清理异常不影响主链路 */ }
        }

        public ObservableCollection<StationOptionItem> StationOptions { get; } = new ObservableCollection<StationOptionItem>();

        /// <summary>工位下拉项：下拉显示「名称 + 编码」，选中值仍为工位编码（SelectedValuePath=Code）。</summary>
        public sealed class StationOptionItem
        {
            public StationOptionItem(string code, string name)
            {
                Code = code;
                Name = string.IsNullOrWhiteSpace(name) ? code : name;
            }

            /// <summary>工位编码（选中值 / 运行时键）。</summary>
            public string Code { get; }

            /// <summary>工位名称。</summary>
            public string Name { get; }

            /// <summary>下拉展示：名称为主、编码为辅（名称=编码时仅显示编码）。</summary>
            public string Display => string.Equals(Name, Code, System.StringComparison.OrdinalIgnoreCase)
                ? Code
                : $"{Name}　[{Code}]";
        }

        private bool StationCodeExists(string code)
        {
            return !string.IsNullOrWhiteSpace(code)
                   && StationOptions.Any(o => string.Equals(o.Code, code, System.StringComparison.OrdinalIgnoreCase));
        }

        private string _selectedStationCode;
        public string SelectedStationCode
        {
            get => _selectedStationCode;
            set
            {
                if (Set(ref _selectedStationCode, value))
                {
                    RefreshStationFactsForCode(value);
                    _ = ConnectToStationAsync(value);
                }
            }
        }

        // ===== 工位信息展示（模板唯一入口；2026-09：站名 / 绑定任务模板 / 派生引擎）=====
        // 监视页职责 = 生产看板：顶栏站名、左卡「任务模板 · 配方」、运行控制、结果回显。
        // 模板部署 / 引擎派生 / 参数示教 均在任务模板中心与工位工程工作台完成——本页零业务引用，纯文本映射。

        private string _selectedStationName = "--";
        /// <summary>当前选中工位的名称（无名称回退为工位编码；顶栏大字）。</summary>
        public string SelectedStationName
        {
            get => _selectedStationName;
            private set => Set(ref _selectedStationName, value);
        }

        private string _templateChipText = "未绑定任务模板";
        /// <summary>任务芯片：绑定模板（类型 · 名称）；未绑定显示引擎友好名或提示。</summary>
        public string TemplateChipText
        {
            get => _templateChipText;
            private set => Set(ref _templateChipText, value);
        }

        private bool _hasBoundTemplate;
        /// <summary>当前工位是否已部署任务模板（左卡样式判定）。</summary>
        public bool HasBoundTemplate
        {
            get => _hasBoundTemplate;
            private set => Set(ref _hasBoundTemplate, value);
        }

        private string _boundTemplateNameText = "--";
        /// <summary>绑定任务模板名称（左卡主行）。</summary>
        public string BoundTemplateNameText
        {
            get => _boundTemplateNameText;
            private set => Set(ref _boundTemplateNameText, value);
        }

        private string _boundTemplateKindText = string.Empty;
        /// <summary>绑定任务模板类型中文（引导定位 / 深度学习推理 / 外观测量…）。</summary>
        public string BoundTemplateKindText
        {
            get => _boundTemplateKindText;
            private set => Set(ref _boundTemplateKindText, value);
        }

        private string _boundTemplateEngineText = string.Empty;
        /// <summary>模板派生的执行引擎友好名（只读派生信息）。</summary>
        public string BoundTemplateEngineText
        {
            get => _boundTemplateEngineText;
            private set => Set(ref _boundTemplateEngineText, value);
        }

        private string _guideSummaryText = string.Empty;
        /// <summary>引导卡摘要（当前任务形态 + 配方状态，指导从哪档开始）。</summary>
        public string GuideSummaryText
        {
            get => _guideSummaryText;
            private set => Set(ref _guideSummaryText, value);
        }

        private string _latestVisionText = "--";
        /// <summary>最近一次视觉链输出 wx/wy/角度（从节点端口实时捕获，纯看板回显）。</summary>
        public string LatestVisionText
        {
            get => _latestVisionText;
            private set => Set(ref _latestVisionText, value);
        }

        /// <summary>跳转 工位工程工作台（模板/引擎/参数配置入口）。</summary>
        public RelayCommand GoEngineConfigCommand { get; private set; }

        /// <summary>最近一次视觉链捕获的世界坐标（Task 引导卡状态用）。</summary>
        private double? _lastVisionWx;
        private double? _lastVisionWy;
        private double? _lastVisionAngle;

        /// <summary>按工位编码刷新站头展示 + 任务信息（含派生引擎与引导摘要；查不到则清空）。</summary>
        private void RefreshStationFactsForCode(string stationCode)
        {
            try
            {
                var station = _configService.LoadAllLines()
                    .SelectMany(l => l.Stations)
                    .FirstOrDefault(s => s.StationCode == stationCode);
                RefreshStationFacts(station);
            }
            catch
            {
                RefreshStationFacts(null);
            }
        }

        /// <summary>统一从工位配置刷新：站名 / 绑定模板 / 派生引擎 / 引导摘要（未选中或查不到时清空）。</summary>
        private void RefreshStationFacts(StationConfigModel station)
        {
            string code = station?.StationCode;
            bool anyStation = !string.IsNullOrWhiteSpace(code);

            SelectedStationName = anyStation
                ? (string.IsNullOrWhiteSpace(station.StationName) ? code : station.StationName)
                : "--";

            bool bound = anyStation && !string.IsNullOrWhiteSpace(station.TaskTemplateCode);
            HasBoundTemplate = bound;
            BoundTemplateNameText = bound
                ? (string.IsNullOrWhiteSpace(station.TaskTemplateName) ? station.TaskTemplateCode : station.TaskTemplateName)
                : "--";
            BoundTemplateKindText = bound ? station.TaskTemplateKindText ?? string.Empty : string.Empty;

            string engineName = null;
            if (bound && !string.IsNullOrWhiteSpace(station.ProcessKey))
            {
                var opt = Grayson.Vision.WpfUI.Service.StationProcessCatalog.Find(station.ProcessKey);
                engineName = opt != null ? $"{opt.Icon} {opt.Name}" : $"引擎 [{station.ProcessKey}]";
            }
            BoundTemplateEngineText = engineName ?? string.Empty;

            if (bound)
            {
                var parts = new[] { BoundTemplateKindText, BoundTemplateNameText }
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                TemplateChipText = string.Join(" · ", parts);
                GuideSummaryText = $"任务 {BoundTemplateNameText} 已部署"
                    + (string.IsNullOrWhiteSpace(engineName) ? string.Empty : $"，执行引擎 {engineName}")
                    + "。点【▶ 启动】或【单次触发】跑任务，判定/结果见右侧 OK-NG 与日志；"
                    + "首次跑通前请到【工位工程工作台】核对模板、配方与触发配置。";
            }
            else
            {
                TemplateChipText = anyStation ? "未绑定任务模板" : "--";
                GuideSummaryText = anyStation
                    ? "本工位尚未部署任务模板：触发仅按绑定配方跑视觉链（若有）。"
                      + "请到【任务模板中心】给工位「部署到工位」，再回【工位工程工作台】保存同步后开跑。"
                    : string.Empty;
            }
        }

        public IWorkerClient ActiveClient
        {
            get => _activeClient;
            private set
            {
                if (Set(ref _activeClient, value))
                {
                    RefreshCommandStates();
                }
            }
        }

        private string _state = "Stopped";
        public string State
        {
            get => _state;
            set
            {
                if (Set(ref _state, value))
                {
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(StateBrushKey));
                    RefreshCommandStates();
                }
            }
        }

        /// <summary>
        /// 显式刷新所有 RelayCommand 的 CanExecute 状态。
        /// WPF 不会自动把 CommandManager.InvalidateRequerySuggested 转发给 VM 中的 ICommand，
        /// 因此必须在 State / ActiveClient 等影响按钮使能的状态变更后，手动调用 RaiseCanExecuteChanged。
        /// </summary>
        private void RefreshCommandStates()
        {
            StartCommand?.RaiseCanExecuteChanged();
            StopCommand?.RaiseCanExecuteChanged();
            PauseCommand?.RaiseCanExecuteChanged();
            ResumeCommand?.RaiseCanExecuteChanged();
            TriggerOnceCommand?.RaiseCanExecuteChanged();
            ResetCommand?.RaiseCanExecuteChanged();
            WorkOrderResetCommand?.RaiseCanExecuteChanged();
            HardwareResetCommand?.RaiseCanExecuteChanged();
            EmergencyStopCommand?.RaiseCanExecuteChanged();
            ClearLogsCommand?.RaiseCanExecuteChanged();
            LiveViewCommand?.RaiseCanExecuteChanged();
            GoEngineConfigCommand?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 状态中文文案（状态机中文映射），供 Banner/卡片等展示层绑定；逻辑判断仍使用 State 英文字符串。
        /// </summary>
        public string StateText => Grayson.Vision.WpfUI.Common.StationStateTexts.ToDisplayText(State);

        /// <summary>
        /// 状态指示灯/文字颜色（Brush 实例，避免字符串→Brush 转换失败导致恒为灰色）。
        /// 从应用资源表按键取 Brush；缺失时回退 Gray。
        /// </summary>
        public System.Windows.Media.Brush StateBrushKey
        {
            get
            {
                string key;
                switch (State)
                {
                    case "Running": key = "SuccessBrush"; break;
                    case "Faulted": key = "DangerBrush"; break;
                    case "Idle": key = "InfoBrush"; break;
                    case "Paused": key = "WarningBrush"; break;
                    default: key = "TextDisabledBrush"; break;
                }
                return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
                       ?? System.Windows.Media.Brushes.Gray;
            }
        }

        private string _currentRecipeName;
        public string CurrentRecipeName
        {
            get => _currentRecipeName;
            set => Set(ref _currentRecipeName, value);
        }

        private string _currentRecipeApprovalText;
        /// <summary>
        /// 当前绑定配方的审批状态中文文案（关注点信息：配方是否可用于生产）。
        /// </summary>
        public string CurrentRecipeApprovalText
        {
            get => _currentRecipeApprovalText;
            set => Set(ref _currentRecipeApprovalText, value);
        }

        // ===== 触发源摘要与节拍统计 =====

        private string _triggerSummaryText = "手动触发";
        /// <summary>触发源摘要文本（如"PLC 位 | PLC_001 | M0.0 | 上升沿"）</summary>
        public string TriggerSummaryText
        {
            get => _triggerSummaryText;
            set => Set(ref _triggerSummaryText, value);
        }

        private string _triggerStatsText = "--";
        /// <summary>节拍统计文本（如"触发: 156 | 执行: 150 | 丢弃: 6 | 平均间隔: 1250ms"）</summary>
        public string TriggerStatsText
        {
            get => _triggerStatsText;
            set => Set(ref _triggerStatsText, value);
        }

        private string _lastTriggerTimeText = "--";
        /// <summary>最近触发时间</summary>
        public string LastTriggerTimeText
        {
            get => _lastTriggerTimeText;
            set => Set(ref _lastTriggerTimeText, value);
        }

        public ObservableCollection<StationLogEntry> Logs { get; set; }
        public ObservableCollection<StationResultEntry> Results { get; set; }

        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand PauseCommand { get; }
        public RelayCommand ResumeCommand { get; }
        public RelayCommand TriggerOnceCommand { get; }
        /// <summary>软复位（ResetCommand 指向软复位）</summary>
        public RelayCommand ResetCommand { get; }
        /// <summary>工单级复位</summary>
        public RelayCommand WorkOrderResetCommand { get; }
        /// <summary>硬件全复位（断连恢复）</summary>
        public RelayCommand HardwareResetCommand { get; }
        /// <summary>急停</summary>
        public RelayCommand EmergencyStopCommand { get; }
        public RelayCommand ClearLogsCommand { get; }
        /// <summary>相机实时画面开关（Live 视图）</summary>
        public RelayCommand LiveViewCommand { get; }

        #region INavigationAware 导航激活刷新

        /// <summary>
        /// 🌟 缓存单例页面的导航激活钩子（2026-09-01 修复）。
        ///
        /// 本页 View 注册为「缓存单例」（App.xaml.cs 工厂缓存），导航离开再进入时
        /// 复用同一 View+VM——若此钩子不做任何事，用户在【工位管理】等页面
        /// 增删改工位/换配方/改业务过程后切回本页，工位列表、配方信息、触发源、
        /// 业务过程声明、扩展面板（示教）全部停留旧快照。
        ///
        /// 刷新策略（ReloadFromConfig 模式，统计/日志/图像历史/连接不清）：
        ///   1. 无条件全量重读工位列表（LiteDB → StationOptions）；
        ///   2. 带 stationCode 参数：跳转定位该工位（setter 触发连接）；
        ///   3. 无参数（侧边栏点入）：
        ///      - 当前工位已被删除 → 自动切到列表首个工位；
        ///      - 工位仍在 → 重跑 ConnectToStationAsync 重读该工位配置派生状态
        ///        （配方/触发源/ProcessKey 声明热更新/扩展面板重挂）。
        /// </summary>
        public void OnNavigatedTo(object parameter)
        {
            try
            {
                // 本页可见期间挂接日志总线：让 独立引擎/节点/引擎错误 的业务日志进入本页日志区
                EnsureLogBusSink(true);

                var previousStation = _selectedStationCode;

                // 1. 全量重读工位列表（其他页面增删工位后回到本页立即可见）
                LoadAvailableStations();

                if (parameter is string stationCode && !string.IsNullOrEmpty(stationCode))
                {
                    _ = InitializeWithStationAsync(stationCode);
                    return;
                }

                if (string.IsNullOrEmpty(previousStation))
                {
                    // 首次进入且无参数：列表已非空时选中首个（setter 自动连接）
                    if (!string.IsNullOrEmpty(_selectedStationCode)) return;
                    SelectedStationCode = StationOptions.FirstOrDefault()?.Code;
                    return;
                }

                if (!StationCodeExists(previousStation))
                {
                    // 当前工位已在其他页面被删除：切到列表首个工位（setter 自动连接）；
                    // 列表也被删空时清空工位展示（无工位可显示）
                    SelectedStationCode = StationOptions.FirstOrDefault()?.Code;
                    if (SelectedStationCode == null)
                    {
                        RefreshStationFacts(null);
                    }
                    return;
                }

                if (previousStation == _selectedStationCode)
                {
                    // 同一工位重进页面：重读配置派生状态。
                    // ConnectToStationAsync 内部先 DetachClientEvents 再重挂（不重复订阅），
                    // 不重启 Worker；ProcessKey/ProcessConfigJson 变更经 AttachProcessDeclaration
                    // 声明到 Worker，扩展面板（示教）Dispose 后按新配置重挂。
                    _ = ConnectToStationAsync(previousStation);
                }
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"导航刷新失败: {ex.Message}");
            }
        }

        public void OnNavigatedFrom()
        {
            // 页面不可见时摘掉日志总线，避免切页后日志仍向本页堆积
            EnsureLogBusSink(false);
        }

        /// <summary>
        /// 日志总线挂接（2026-09-09 修复）：工位监视页日志区此前只吃 Worker 事件，
        /// 而 独立视觉引擎周期日志(StandaloneVision)、DL 节点日志(HalconDlInference)
        /// 全部走 LogBus —— 导致「图像窗格有错误、页面日志区却空白」。
        /// 挂接后按 分类白名单 + 级别 过滤入页（Engine 只收 Warn+，避免逐节点 Info 刷屏）。
        /// </summary>
        private void EnsureLogBusSink(bool subscribe)
        {
            try
            {
                if (subscribe && !_busLogSubscribed)
                {
                    LogBus.OnLogProduced += BusLog_OnLogProduced;
                    _busLogSubscribed = true;
                }
                else if (!subscribe && _busLogSubscribed)
                {
                    LogBus.OnLogProduced -= BusLog_OnLogProduced;
                    _busLogSubscribed = false;
                }
            }
            catch { /* 日志订阅失败不影响页面 */ }
        }

        /// <summary>LogBus → 本页日志区（任意线程触发，一律转 UI 线程）。
        /// 2026-09-09(#2)：本页是「单工位监视」，日志区只应收【当前选中工位】的内容。
        /// LogBus 是全局总线（多工位/引擎共用），故除分类+级别白名单外，还强制按工位归属过滤——
        /// 命中条件：entry.StationId == 选中编码，或消息文本含 "[选中编码]"（生产日志约定
        /// [ST_xxx] 前缀标识工位）。带不上工位标记的全局行（如无归属 DL 节点行）一律丢弃，
        /// 杜绝多工位定时任务并发时把别的工位内容串进本页日志区。</summary>
        private void BusLog_OnLogProduced(LogEntry entry)
        {
            try
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Message)) return;

                string category = entry.Category ?? string.Empty;
                var level = entry.Level;
                string code = SelectedStationCode;

                // 分类白名单 + 级别过滤（Engine 只收 Warn+，避免"节点核心逻辑执行成功"等 Info 刷屏）
                bool categoryPass;
                if (string.Equals(category, "StandaloneVision", StringComparison.OrdinalIgnoreCase))
                    categoryPass = level >= LogLevel.Info;          // 独立任务引擎周期行
                else if (string.Equals(category, "HalconDlInference", StringComparison.OrdinalIgnoreCase))
                    categoryPass = level >= LogLevel.Info;          // DL 节点推理行（成败/校正/异常）
                else if (string.Equals(category, "Engine", StringComparison.OrdinalIgnoreCase))
                    categoryPass = level >= LogLevel.Warn;          // 引擎致命错误/节点运行时异常
                else if (string.Equals(category, "StationWorker", StringComparison.OrdinalIgnoreCase))
                    categoryPass = level >= LogLevel.Warn;          // 工位级异常/告警
                else
                    categoryPass = false;                            // 其它分类不入本页（防刷屏）

                if (!categoryPass) return;

                // 强制按当前选中工位归属：带不上 [工位码] / StationId 的行不入本页（防多工位串台）
                if (!StationMatches(entry, code)) return;

                var levelText = level.ToString().ToUpperInvariant();
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    try { AddLog(levelText, entry.Message); }
                    catch { /* 单条日志展示失败忽略 */ }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { /* 总线回调异常不影响主链路 */ }
        }

        /// <summary>判定一条全局日志是否属于某工位：StationId 相等 或 消息含 "[工位码]"。code 空时视为不属于任何工位。</summary>
        private static bool StationMatches(LogEntry entry, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            if (!string.IsNullOrWhiteSpace(entry.StationId)
                && string.Equals(entry.StationId, code, StringComparison.OrdinalIgnoreCase)) return true;
            if (entry.Message.IndexOf("[" + code + "]", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
        #endregion

        private void LoadAvailableStations()
        {
            StationOptions.Clear();
            var lines = _configService.LoadAllLines();
            foreach (var station in lines.SelectMany(l => l.Stations))
            {
                StationOptions.Add(new StationOptionItem(station.StationCode, station.StationName));
            }

            if (StationOptions.Any() && string.IsNullOrEmpty(SelectedStationCode))
            {
                SelectedStationCode = StationOptions.First().Code;
            }
        }

        public async Task InitializeWithStationAsync(string stationCode)
        {
            if (!StationCodeExists(stationCode))
            {
                StationOptions.Add(new StationOptionItem(stationCode, null));
            }
            SelectedStationCode = stationCode;
            await Task.CompletedTask;
        }

        private async Task ConnectToStationAsync(string stationCode)
        {
            if (string.IsNullOrEmpty(stationCode)) return;

            // 🌟 连接请求串行守卫：后续切换请求会递增序号，本请求异步完成时若已过期则自弃，
            // 防止「快速切 001→004→001」时旧请求晚完成把 ActiveClient 挂到已非下拉项的工位
            //（下拉显示 A、实际连接/画面是 B 的脱钩）。
            var seq = ++_connectSeq;

            // 🌟 工位切换识别：与「最近一次连接目标」不同才算真换工位（同工位重连保留图像现场）
            bool switchingStation = !string.IsNullOrEmpty(_lastConnectedStationCode)
                                    && !string.Equals(_lastConnectedStationCode, stationCode, StringComparison.OrdinalIgnoreCase);
            _lastConnectedStationCode = stationCode;

            DetachClientEvents();

            // 🌟 图像区按工位隔离：换工位必须清空上一工位的帧历史与当前画面——
            // ImageDisplayVm 是页面级缓存（跨导航存活），切工位不清图会让上一工位
            //（如常驻自动循环的 004）的最后一帧残留在主视图；新工位静止无帧时画面
            // 就一直显示旧工位内容，造成「下拉 001、画面 004」的错位。新工位有帧即
            // 自动上屏（AddOrUpdateImageContext 自动跟随），无帧则保持空态。
            if (switchingStation)
            {
                try { ImageDisplayVm.Clear(); }
                catch { /* 清图失败不阻塞连接流程 */ }

                // 🌟 内容区按工位隔离（#2）：切到另一工位时清空上一工位的会话态——
                // 日志/最近结果/巨幕 RESULT/周期字段/Live 与完成计数水位全部归零复位，
                // 避免「下拉 A、列表/日志还残留 B 上一次运行内容」的串台。切到未运行工位
                // 则保持空态（无帧/无结果/无日志），切回曾运行工位由后续 Connect 的
                // RefreshWorkOrderHistory / AccumulateStats 从该工位持久化数据回填。
                ClearStationSessionContent();
            }

            var client = _runtimeManager.GetClient(stationCode);
            if (client == null)
            {
                try
                {
                    client = await _runtimeManager.CreateAndConnectStationAsync(stationCode, WorkerConnectMode.Embedded);
                }
                catch (Exception ex)
                {
                    AddLog("ERROR", $"连接工位 {stationCode} 失败: {ex.Message}");
                    return;
                }
            }

            // 等待期间若用户又切换了工位，本次连接结果作废（新请求会自己连接/订阅）
            if (seq != _connectSeq) return;

            ActiveClient = client;
            AttachClientEvents(client);
            EnsurePreviewAttached();

            // 🌟 兜底(2026-09-09 #2 根治)：把【本页 HALCON 宿主】从所有其它工位 Worker 上摘除，
            // 只保留当前连接工位持有它。多工位并发 + 异常切换路径可能让先前仍在自跑的工位
            //（如 005）Worker 残留本页预览适配器引用——其节点绘制 Preview 非空就会把轮廓/场景
            // 画进本页窗口（选中 004 却冒出 005 叠加）。此处全量核对，杜绝任何残留注入。
            EnsureOnlyThisStationHasPreview(stationCode);

            State = client.CurrentState.ToString();

            var stationConfig = _configService.LoadAllLines()
                .SelectMany(l => l.Stations)
                .FirstOrDefault(s => s.StationCode == stationCode);
            var recipe = !string.IsNullOrEmpty(stationConfig?.BoundRecipeId)
                ? _recipeStorage.LoadRecipe(stationConfig.BoundRecipeId)
                : null;
            CurrentRecipeName = recipe?.RecipeName ?? stationConfig?.BoundRecipeName;
            CurrentRecipeApprovalText = recipe == null
                ? "未绑定配方"
                : $"{ApprovalStatusToText(recipe.ApprovalStatus)} · {recipe.Version}";

            // 加载触发源摘要（从工位配置读取）
            RefreshTriggerInfo(stationConfig?.TriggerSource);

            // 🌟 连接即声明业务过程（2026-09-01 修复）：
            // 把工位配置的 ProcessKey/ProcessConfigJson 登记到 Worker 并装配。
            // 此前只有「工位管理-保存」(CreateStationWithRecipeAsync) 会设置 DesiredProcessKey，
            // 程序重启后直接从监视页连接、或 FlowEdit 重绑共享 worker 后，该声明为空 →
            // 触发前自愈 EnsureProcessAttached 变 no-op → 工位退化纯视觉链（不走位不吸取）。
            // 2026-09-09 补充：任务模板代码同样贯通——独立引擎(StandaloneVision)凭 Worker.TaskTemplateCode
            // 读 Config\TaskLibrary\{Code}.json 的模板级 VerdictRule（如分类 ok/ng 类名前缀判据）；
            // CreateAndConnectStationAsync 只建裸 worker，不设该字段，缺了会导致分类 ok 图也判 NG。
            if (stationConfig != null)
            {
                AttachProcessDeclaration(stationCode, stationConfig.ProcessKey, stationConfig.ProcessConfigJson, stationConfig.TaskTemplateCode);
            }
            else
            {
                AddLog("WARN", $"未读取到工位 {stationCode} 的配置（数据库 LoadAllLines 未返回该工位），业务过程未装配、示教面板将不可用。");
            }

            // 刷新任务引导卡文案（引擎友好名/任务形态/示教提示），不挂业务扩展面板（已迁工作台④）
            // 刷新站头展示：站名 / 绑定任务模板 / 派生引擎 / 引导摘要（模板唯一入口；参数示教仍在工作台④）
            RefreshStationFacts(stationConfig);

            // 从 Core 工单追踪器加载最近工单历史（打通 WorkOrderTracker → UI）
            RefreshWorkOrderHistory();

            AddLog("INFO", $"已连接工位 {stationCode}，当前状态: {State}");
        }

        private void AttachClientEvents(IWorkerClient client)
        {
            if (client == null) return;

            client.OnStateChanged += Client_OnStateChanged;
            client.OnFrameRendered += Client_OnFrameRendered;
            client.OnExecutionCompleted += Client_OnExecutionCompleted;
            client.OnNodeExecuting += Client_OnNodeExecuting;
            client.OnNodeExecuted += Client_OnNodeExecuted;
            client.OnExecutionError += Client_OnExecutionError;
            client.OnLogReceived += Client_OnLogReceived;
        }

        private void DetachClientEvents()
        {
            // 切换/断开工位前先停 Live：旧工位的相机不能继续往本页推流
            // （_liveCamera 指向旧工位设备，不清理会残留直播+占着相机）。
            if (_liveGrabbing) StopLiveGrab(auto: true);

            if (_activeClient == null) return;

            // 🌟 关键(#2 修复二)：切换/断开前撤销本页 Halcon 预览适配器对旧工位 Worker 的注入。
            // 否则后台仍在自跑（触发源/调度器）的旧工位会经其 Worker.PreviewContext 把模板
            // 轮廓/场景叠加画进【本页】的 HALCON 窗口——本页明明是单工位监视(选中 004)，
            // 画面却冒出仍开着自动任务的 005 内容。撤销后旧工位节点绘制 Preview 为 null 自然跳过。
            //（同工位重连：Detach 后 ConnectToStationAsync 会 EnsurePreviewAttached 重新注入，不丢画面。）
            try { _activeClient.SetPreviewContext(null); }
            catch { /* 撤销预览失败不阻塞切换 */ }

            _activeClient.OnStateChanged -= Client_OnStateChanged;
            _activeClient.OnFrameRendered -= Client_OnFrameRendered;
            _activeClient.OnExecutionCompleted -= Client_OnExecutionCompleted;
            _activeClient.OnNodeExecuting -= Client_OnNodeExecuting;
            _activeClient.OnNodeExecuted -= Client_OnNodeExecuted;
            _activeClient.OnExecutionError -= Client_OnExecutionError;
            _activeClient.OnLogReceived -= Client_OnLogReceived;
        }

        private async Task StartAsync()
        {
            if (ActiveClient == null) return;
            try
            {
                // 自愈：若业务过程曾被 FlowEdit 调试卸载，先按配置重挂再启动
                EnsureStationProcessAttached();
                await ActiveClient.StartAsync();
            }
            catch (Exception ex) { AddLog("ERROR", $"启动失败: {ex.Message}"); }
        }

        private async Task StopAsync()
        {
            if (ActiveClient == null) return;
            try { await ActiveClient.StopAsync(); }
            catch (Exception ex) { AddLog("ERROR", $"停止失败: {ex.Message}"); }
        }

        private async Task PauseAsync()
        {
            if (ActiveClient == null) return;
            try { await ActiveClient.PauseAsync(); }
            catch (Exception ex) { AddLog("ERROR", $"暂停失败: {ex.Message}"); }
        }

        private async Task ResumeAsync()
        {
            if (ActiveClient == null) return;
            try { await ActiveClient.ResumeAsync(); }
            catch (Exception ex) { AddLog("ERROR", $"恢复失败: {ex.Message}"); }
        }

        private async Task TriggerOnceAsync()
        {
            if (ActiveClient == null) return;
            if (IsCycleRunning)
            {
                AddLog("WARN", "业务周期正在进行中，忽略本次单次触发（防并发撞机）。");
                return;
            }
            try
            {
                // 相机让渡已精确化（2026-09-01）：不再在触发前停 Live——执行到 AcquireImage
                // 节点时（Client_OnNodeExecuting，节点执行器运行前）才同步停流；
                // 触发后的抬 Z / 移拍照位期间相机空闲，Live 持续直播工台送料过程。

                // 自愈：若业务过程曾被 FlowEdit 调试卸载，先按配置重挂再触发
                EnsureStationProcessAttached();

                // 优先走触发源的手动触发路径（经过丢帧策略+节拍统计）
                var hostRuntime = App.StationHostRuntime as Grayson.Vision.Contracts.Station.Services.IStationHostRuntime;
                if (hostRuntime != null && hostRuntime.GetTriggerSource(SelectedStationCode) != null)
                {
                    hostRuntime.ManualTrigger(SelectedStationCode);
                }
                else
                {
                    // 无触发源时直接调旧路径
                    await ActiveClient.TriggerOnceAsync();
                }
            }
            catch (Exception ex) { AddLog("ERROR", $"单次触发失败: {ex.Message}"); }
        }

        /// <summary>
        /// 把工位配置声明的业务过程（ProcessKey + ProcessConfigJson）与任务模板代码登记到 Worker。
        /// 幂等：Worker 已挂载过程时仅刷新声明；运行中只登记不重挂（防打断时序），
        /// 待停止/下次触发时由 EnsureStationProcessAttached 自愈挂载。
        /// taskTemplateCode：独立引擎判据贯通（缺省则为空，仅当配置提供时覆盖）。
        /// </summary>
        private void AttachProcessDeclaration(string stationCode, string processKey, string processConfigJson, string taskTemplateCode = null)
        {
            try
            {
                if (App.StationHostRuntime is Grayson.Vision.Core.Station.StationHostRuntime runtime)
                {
                    var worker = runtime.GetStationWorker(stationCode);
                    if (worker == null) return;

                    worker.DesiredProcessKey = processKey;
                    worker.DesiredProcessConfigJson = processConfigJson;

                    // 🌟 任务模板代码贯通（2026-09-09）：CreateAndConnectStationAsync 创建的裸 Worker
                    //    不会带 TaskTemplateCode → 独立引擎 TryLoadTemplateVerdict 读不到 Config\TaskLibrary
                    //    \{Code}.json 的模板级判据（分类 ok/ng 类名前缀）→ ok 图会被误判 NG。
                    if (!string.IsNullOrWhiteSpace(taskTemplateCode))
                    {
                        worker.TaskTemplateCode = taskTemplateCode.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(processKey)) return; // 无业务过程：仅刷新声明/模板代码

                    var stateText = worker.State.ToString();
                    if (stateText == "Running" || stateText == "Paused")
                    {
                        AddLog("WARN", $"工位正在运行，业务过程 [{processKey}] 声明已登记，待停止/下次触发时自愈挂载。");
                        return;
                    }

                    if (worker.Process == null)
                    {
                        worker.EnsureProcessAttached();
                        AddLog("INFO", $"已按工位配置装配业务过程 [{processKey}]。");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"登记/挂载业务过程 [{processKey}] 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 触发前自愈：若业务过程被 FlowEdit 编辑器 DetachProcess 卸载，
        /// 按工位配置声明的过程键重新挂载（Core 侧 EnsureProcessAttached）。
        /// 同时重新武装视觉预览上下文（FlowEdit 调试节点时会换成编辑器自己的适配器）。
        /// </summary>
        private void EnsureStationProcessAttached()
        {
            try
            {
                if (App.StationHostRuntime is Grayson.Vision.Core.Station.StationHostRuntime runtime)
                {
                    runtime.EnsureStationProcessAttached(SelectedStationCode);
                }
                EnsurePreviewAttached();
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"自愈挂载业务过程失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 刷新触发源摘要文本。
        /// </summary>
        private void RefreshTriggerInfo(Grayson.Vision.Contracts.Station.Triggers.TriggerSourceConfig config)
        {
            TriggerSummaryText = config?.ToSummary() ?? "手动触发";
            RefreshTriggerStats();
        }

        /// <summary>
        /// 从 Core 运行时读取触发源节拍统计并刷新 UI。
        /// </summary>
        private void RefreshTriggerStats()
        {
            try
            {
                var hostRuntime = App.StationHostRuntime as Grayson.Vision.Contracts.Station.Services.IStationHostRuntime;
                var source = hostRuntime?.GetTriggerSource(SelectedStationCode);
                if (source == null)
                {
                    TriggerStatsText = "未配置触发源";
                    LastTriggerTimeText = "--";
                    return;
                }

                var stats = source.GetStats();
                TriggerStatsText = $"触发: {stats.TotalTriggered} | 执行: {stats.TotalExecuted} | 丢弃: {stats.DroppedCount} | 平均间隔: {stats.AvgIntervalMs:F0}ms";
                LastTriggerTimeText = stats.LastTriggerTime.HasValue
                    ? stats.LastTriggerTime.Value.ToString("HH:mm:ss.fff")
                    : "--";
            }
            catch
            {
                TriggerStatsText = "--";
                LastTriggerTimeText = "--";
            }
        }

        /// <summary>
        /// 工单级复位：终止当前工单并释放本次占用设备，不改变工位全局状态。
        /// </summary>
        private async Task WorkOrderResetAsync()
        {
            if (ActiveClient == null) return;
            try
            {
                await ActiveClient.WorkOrderResetAsync();
                State = ActiveClient.CurrentState.ToString();
                AddLog("INFO", "工单级复位完成，等待下一次触发。");
            }
            catch (Exception ex) { AddLog("ERROR", $"工单级复位失败: {ex.Message}"); }
        }

        /// <summary>
        /// 软复位：停止运行、清空共享变量、重新打开设备（不重建硬件句柄）。
        /// </summary>
        private async Task SoftResetAsync()
        {
            if (ActiveClient == null) return;
            try
            {
                await ActiveClient.SoftResetAsync();
                State = ActiveClient.CurrentState.ToString();
                AddLog("INFO", "工位软复位完成。");
            }
            catch (Exception ex) { AddLog("ERROR", $"软复位失败: {ex.Message}"); }
        }

        /// <summary>
        /// 硬件全复位：关闭所有设备句柄并重新 Open，用于断连后恢复。
        /// </summary>
        private async Task HardwareResetAsync()
        {
            if (ActiveClient == null) return;
            try
            {
                await ActiveClient.HardwareResetAsync();
                State = ActiveClient.CurrentState.ToString();
                AddLog("INFO", "硬件全复位完成，设备已重新连接。");
            }
            catch (Exception ex) { AddLog("ERROR", $"硬件复位失败: {ex.Message}"); }
        }

        /// <summary>
        /// 急停：进入 ErrorLocked，终止所有工单。
        /// </summary>
        private async Task EmergencyStopAsync()
        {
            if (ActiveClient == null) return;
            try
            {
                await ActiveClient.EmergencyStopAsync("UI 急停按钮触发");
                State = ActiveClient.CurrentState.ToString();
                AddLog("ERROR", "急停已触发！工位进入 ErrorLocked。");
            }
            catch (Exception ex) { AddLog("ERROR", $"急停失败: {ex.Message}"); }
        }

        /// <summary>
        /// 从 Core 工单追踪器刷新最近工单历史与缺陷统计（打通 WorkOrderTracker → UI 展示）。
        /// </summary>
        private void RefreshWorkOrderHistory()
        {
            var tracker = ActiveClient?.WorkOrderTracker;
            if (tracker == null) return;

            var snapshots = tracker.GetRecentWorkOrders(20);
            Results.Clear();
            foreach (var snap in snapshots)
            {
                if (snap == null) continue;
                Results.Add(new StationResultEntry
                {
                    Timestamp = snap.CreatedAt.ToLocalTime(),
                    BatchId = !string.IsNullOrEmpty(snap.BatchId)
                        ? snap.BatchId
                        : (snap.WorkOrderId?.Length >= 8 ? snap.WorkOrderId.Substring(0, 8) : snap.WorkOrderId),
                    Result = snap.IsOk == true ? "OK" : snap.IsOk == false ? "NG" : (snap.StatusText ?? "—"),
                    Message = BuildSnapshotMessage(snap)
                });
            }

            // 复活死属性：从真实工单数据统计缺陷/故障指标
            var failed = snapshots.Where(s => s.IsOk == false).ToList();
            ErrorCount = failed.Count;
            FaultCount = snapshots.Count(s => !string.IsNullOrEmpty(s.ErrorMessage));
            FirstDefectTime = failed
                .OrderBy(s => s.CreatedAt)
                .Select(s => (DateTime?)s.CreatedAt.ToLocalTime())
                .FirstOrDefault();
            LastDefectDescription = snapshots
                .FirstOrDefault(s => !string.IsNullOrEmpty(s.ErrorMessage))?.ErrorMessage;
        }

        /// <summary>
        /// 配方审批状态 → 中文文案（展示用）。
        /// </summary>
        private static string ApprovalStatusToText(Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus status)
        {
            switch (status)
            {
                case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Draft: return "草稿";
                case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.PendingApproval: return "待审批";
                case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Approved: return "已审批";
                case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Frozen: return "已冻结";
                case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Archived: return "已归档";
                case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Deprecated: return "已废弃";
                default: return status.ToString();
            }
        }

        private static string BuildSnapshotMessage(WorkOrderSnapshot snap)
        {
            if (snap == null) return string.Empty;
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(snap.RecipeName) && snap.RecipeName != "—")
                parts.Add($"配方: {snap.RecipeName}");
            if (snap.CycleTimeMs > 0)
                parts.Add($"耗时: {snap.CycleTimeMs:F1}ms");
            if (!string.IsNullOrEmpty(snap.ErrorMessage))
                parts.Add($"异常: {snap.ErrorMessage}");
            return parts.Count > 0 ? string.Join(" | ", parts) : snap.StatusText ?? string.Empty;
        }

        /// <summary>
        /// 🌟 事件源守卫（2026-09-09 #2 根治）：每个客户端事件都带 sender=发起事件的工位 proxy，
        /// 只有当该 sender 就是【当前选中且已连接】的 ActiveClient 时才允许落屏/计数。
        /// 原因：多工位并发时，切换工位(DetachClientEvents)与旧事件 BeginInvoke 落盘之间存在
        /// 竞态窗口——已切走的工位(如 005)最后一次抛出的帧/链完成事件可能仍挂在 UI 队列里，
        /// 稍后执行时 SelectedStationCode 已是 004，造成「选中 004 却冒出 005 内容」。
        /// 按 sender 引用相等判据在事件入口一次性拦截，从根上杜绝任何非当前工位的残留事件。
        /// </summary>
        private bool IsSenderActiveClient(object sender)
        {
            return sender != null && ReferenceEquals(sender, _activeClient);
        }

        private void Client_OnStateChanged(object sender, StationState e)
        {
            var source = sender; // 🌟 事件源守卫(落 UI 前校验，丢弃已切走工位的在途状态变更)
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (!IsSenderActiveClient(source)) return;
                State = e.ToString();
                AddLog("INFO", $"状态变更: {e}");
            }));
        }

        // 2. 帧渲染广播接入：将图像帧推送到 ImageDisplayVm 渲染展示
        private void Client_OnFrameRendered(object sender, ImageRenderEventArgs e)
        {
            if (e?.RenderData == null) return;
            // 🌟 事件源守卫：把 sender 捕获进 lambda，在 UI 线程真正执行落屏时再校验是否仍是当前工位——
            // 因为切换(Detach)只摘除"之后的"订阅，切换前已排队的在途帧仍会执行，必须就地丢弃。
            var source = sender;
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (!IsSenderActiveClient(source)) return; // 已切走/已断开的旧工位在途帧 → 丢弃
                try
                {
                    var renderImg = _renderService.WrapImage(e.RenderData);
                    if (renderImg == null) return;

                    // ⚠ 每次都新建上下文交给 AddOrUpdateImageContext 统一处理旧图释放：
                    // 旧实现"找到既有上下文就地换 Image 再 AddOrUpdate"会命中
                    // AddOrUpdate 的 existing==newContext 分支Dispose 当场销毁刚换上的新图
                    //（第二周期起底图变黑屏只剩叠加轮廓）。统一新建 + 按 NativeHandle 的
                    // 共享判定释放，兼容 MatchImage 借用语义（与相机输出同一 HImage）。
                    var renderContext = new WpfImageRenderContext
                    {
                        NodeId = e.NodeId,
                        NodeName = string.IsNullOrEmpty(e.NodeName) ? $"[{e.NodeId}]" : e.NodeName,
                        Image = renderImg,
                        Thumbnail = _renderService.CreateThumbnail(renderImg)
                    };

                    ImageDisplayVm.AddOrUpdateImageContext(renderContext);
                }
                catch (Exception ex)
                {
                    AddLog("ERROR", $"图像渲染通道处理失败: {ex.Message}");
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        // 🌟 执行链完成事件：
        // - 未挂业务过程（纯视觉链工位）：链完成 = 工单完成，照旧计数（并持久化到 JSON）；
        // - 挂了业务过程：链完成只是周期中段（视觉段约 400ms，后面还有走位/吸取/放料），
        //   绝不能提前报 OK/NG——只记日志，最终判定由轮询在「周期结束沿」从 Worker Metrics 取；
        //   同时自动开启相机 Live：吸嘴走位/吸取过程实时可见，符合行业标准显示策略。
        private void Client_OnExecutionCompleted(object sender, ChainCompletedEventArgs e)
        {
            // 🌟 事件源守卫：捕获 sender 到 lambda，UI 线程执行时若已切走则丢弃，
            // 防 005 的在途链完成事件把结果/计数计入 004 看板、巨幕与统计。
            var source = sender;
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (!IsSenderActiveClient(source)) return;
                var worker = GetStationWorker();
                if (worker != null && worker.Process != null)
                {
                    AddLog("DEBUG",
                        $"视觉链完成（业务周期进行中，最终判定以周期结束为准），链耗时: {e.ExecutionTimeMs:F1}ms");

                    // 视觉段结束 → 自动切 Live：此刻匹配结果+轮廓已在场景叠加层定格，
                    // 相机随吸嘴移动的实时画面接管显示窗口（下次触发前自动让出相机）。
                    StartLiveGrab(auto: true);
                    return;
                }

                var isOk = e.Result == ChainExecutionResult.Success;
                TotalCount++;
                if (isOk) OkCount++; else NgCount++;

                LastResult = isOk ? "OK" : "NG";
                CycleTimeMs = e.ExecutionTimeMs; // 拿到链完成耗时

                // 纯视觉链工位：结果直接持久化（业务过程工位由轮询按 Metrics 水位累计）
                _statsService.RecordChainCompleted(SelectedStationCode, isOk, (long)e.ExecutionTimeMs);

                Results.Insert(0, new StationResultEntry
                {
                    BatchId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Result = LastResult,
                    Message = isOk ? $"执行成功 (耗时: {e.ExecutionTimeMs:F1}ms)" : $"执行异常 (耗时: {e.ExecutionTimeMs:F1}ms)"
                });

                AddLog(isOk ? "INFO" : "ERROR", $"执行链结束，判定: {LastResult}，耗时: {e.ExecutionTimeMs:F1}ms");

                // 刷新触发源节拍统计（执行完成后统计会有新数据）
                RefreshTriggerStats();
            }));
        }

        private void Client_OnNodeExecuting(object sender, NodeEventArgs e)
        {
            var source = sender; // 🌟 事件源守卫：落 UI 时校验是否仍是当前工位
            // 🌟 相机让渡精确化：本事件由 FlowExecutor 在「节点执行器运行之前」、
            // 在执行链线程上同步抛出——恰好是让渡相机的黄金时机。
            // 命中 AcquireImage 节点才停 Live（同步完成停流，早于采图节点改触发模式），
            // 周期里此前的抬 Z / 移拍照位期间相机空闲，Live 持续直播工台送料过程。
            if (e?.Node != null && e.Node.Type == NodeType.AcquireImage && _liveGrabbing)
            {
                if (TryYieldLiveCameraSync())
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        if (!IsSenderActiveClient(source)) return;
                        IsLiveViewOn = false;
                        RestoreLastStaticImage();
                        AddLog("INFO", "视觉节点开始采图，实时画面已让渡相机（采图/模板匹配结果接管显示）。");
                    }));
                }
            }

            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (!IsSenderActiveClient(source)) return;
                AddLog("DEBUG", $"开始执行节点: {e.Node?.DisplayName ?? e.Node?.NodeId}");
            }));
        }

        private void Client_OnNodeExecuted(object sender, NodeEventArgs e)
        {
            // 🌟 事件源守卫：仅当前选中工位的节点输出刷新「最近视觉输出」/日志
            if (!IsSenderActiveClient(sender)) return;
            // 捕获视觉链输出端口（CalibrationApply.OutputX/Y = 世界坐标 wx/wy；ShapeMatch.MatchAngle =
            // 实测角度）→ 任务引导卡「最近视觉输出」回显。原由业务扩展面板(示教)持有，2026-09-06
            // 职责收敛后属公共运行结果，上提至本页。
            try
            {
                var ports = e.Node?.OutputPorts;
                if (ports != null)
                {
                    var px = ports.FirstOrDefault(p => p.PortName == "OutputX")?.DataValue;
                    var py = ports.FirstOrDefault(p => p.PortName == "OutputY")?.DataValue;
                    if (px is double wx && py is double wy)
                    {
                        _lastVisionWx = wx;
                        _lastVisionWy = wy;
                    }
                    var pa = ports.FirstOrDefault(p => p.PortName == "MatchAngle")?.DataValue;
                    if (pa is double ang)
                    {
                        _lastVisionAngle = ang;
                    }
                    if (_lastVisionWx.HasValue || _lastVisionWy.HasValue || _lastVisionAngle.HasValue)
                    {
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(RefreshLatestVisionText));
                    }
                }
            }
            catch { /* 捕获失败不影响正常流程 */ }

            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                AddLog("DEBUG", $"节点执行完成: {e.Node?.DisplayName ?? e.Node?.NodeId}");
            }));
        }

        private void RefreshLatestVisionText()
        {
            LatestVisionText = $"wx={_lastVisionWx?.ToString("F3") ?? "--"}  wy={_lastVisionWy?.ToString("F3") ?? "--"}  Angle={_lastVisionAngle?.ToString("F2") ?? "--"}°";
        }

        /// <summary>任务引导卡「① 档」入口：跳转 工位工程工作台 ④执行方案（引擎参数/示教表单）。
        /// StationManage 非缓存单例、暂不消费定位参数，落页后在左侧树点选本工位即可。</summary>
        private void OnGoEngineConfig()
        {
            try
            {
                NavigationService.Current?.NavigateTo(PageType.StationManage);
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"跳转工位工程工作台失败: {ex.Message}");
            }
        }

        private void Client_OnExecutionError(object sender, NodeExecutionErrorEventArgs e)
        {
            // 🌟 事件源守卫：捕获 sender 到 lambda，落 UI 时若已切走则丢弃（防旧工位在途异常计入当前结果表）
            var source = sender;
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (!IsSenderActiveClient(source)) return;
                Results.Insert(0, new StationResultEntry
                {
                    BatchId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Result = "NG",
                    Message = $"节点 {e.Node?.DisplayName ?? e.Node?.NodeId} 异常: {e.Exception?.Message}"
                });
                AddLog("ERROR", $"节点异常: {e.Exception?.Message}");
            }));
        }

        private void Client_OnLogReceived(object sender, string e)
        {
            // 🌟 事件源守卫：捕获 sender 到 lambda，落 UI 时若已切走则丢弃（防旧工位日志进本页日志区）
            var source = sender;
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (!IsSenderActiveClient(source)) return;
                AddLog("INFO", e);
            }));
        }

        #region 业务周期轮询：统计持久化 + 进行中状态 + Live 联动

        /// <summary>取当前选中工位的 Core Worker（未创建返回 null）。</summary>
        private Grayson.Vision.Core.StationWorker GetStationWorker()
        {
            try
            {
                return (App.StationHostRuntime as Grayson.Vision.Core.Station.StationHostRuntime)
                    ?.GetStationWorker(SelectedStationCode);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 500ms 轮询：业务周期「进行中/结束」判定 + 完成计数消费 + Worker Metrics 水位累计并持久化。
        /// 为什么轮询：业务周期（含 50s 机械动作）结束时没有事件可订阅，
        /// StationWorker.Metrics 是内存态权威数据源（Interlocked 计数、无通知机制）。
        /// 
        /// 2026-09-09 修复(#1 头部 RESULT 不同步)：
        ///   结束判定不再依赖 IsProcessBusy 边沿——快节奏/背靠背自动节拍(定时任务)下两次周期之间
        ///   busy 释放的窗口可能小于轮询间隔，UI 采样不到 false 样本 → 永不触发「结束沿」→
        ///   巨幕 RESULT 卡在『进行中』。改为消费 StationMetrics.FinalizedWorkOrders 单调计数：
        ///   计数比上次多 ⇒ 有完整周期刚定案，读 LastWorkOrderResult 刷新 RESULT/最近结果/日志。
        /// </summary>
        private void OnPollTimerTick()
        {
            try
            {
                var worker = GetStationWorker();
                if (worker == null) return;

                var metrics = worker.Metrics;
                bool busy = worker.IsProcessBusy;

                // ---- 周期开始沿：头部显示「进行中」，实时跳动 CycleTime ----
                if (busy && !_lastProcessBusy)
                {
                    IsCycleRunning = true;
                    if (worker.Process != null) LastResult = "进行中";
                    _cycleWatch.Restart();
                    // 相机让渡已下沉到 AcquireImage 节点执行前（Client_OnNodeExecuting，
                    // 对手动/PLC/外部触发源一律生效）——周期开始沿不再停 Live：
                    // Phase0/1 抬 Z、移拍照位期间相机空闲，继续直播送料过程。
                }

                // ---- 周期结束判定（业务过程工位）：完成计数比上次多即消费最新结果 ----
                // 纯视觉链工位(Process==null)由事件链 Client_OnExecutionCompleted 驱动，
                // 此处若再消费会与其重复计数 → 仅业务过程工位走计数消费。
                if (worker.Process != null)
                {
                    TryConsumeCompletedCycle(metrics);
                }

                // 轮询空闲且无新周期时，把瞬时 busy 复位记录（供下一次开始沿识别）
                _lastProcessBusy = busy;

                // ---- Live 帧心跳自愈 ----
                // 正常让渡（AcquireImage 节点执行前）会同步清 _liveGrabbing；
                // 若事件链缺失（如 FlowEdit 直跑/相机被外部占用停流），Live 状态位
                // 会卡在"开启"而实际已无帧——超过 3s 无帧输入即终结 UI 状态并恢复静态图。
                if (_liveGrabbing && _lastLivePushUtc != DateTime.MinValue &&
                    (DateTime.UtcNow - _lastLivePushUtc).TotalSeconds > 3)
                {
                    if (TryYieldLiveCameraSync())
                    {
                        IsLiveViewOn = false;
                        RestoreLastStaticImage();
                        AddLog("INFO", "实时画面已无帧输入（相机被采图节点或外部占用），自动结束直播。");
                    }
                }

                // 周期进行中：CycleTime 实时跳动
                if (busy && _cycleWatch.IsRunning)
                {
                    CycleTimeMs = _cycleWatch.Elapsed.TotalMilliseconds;
                }

                // ---- 统计水位累计 + 持久化（业务过程工位的主计数路径） ----
                AccumulateStats(worker);
            }
            catch
            {
                // 轮询异常不影响任何业务
            }
        }

        /// <summary>
        /// 消费新定案的完整周期：依赖 StationMetrics.FinalizedWorkOrders 单调计数，
        /// 比上次消费值多即说明有周期刚定案，把最新 LastWorkOrderResult 刷到
        /// RESULT 大卡 + 最近结果表 + 日志 + 触发节拍统计。切换工位后首轮与当前
        /// 水位对齐（不回溯展示历史计数）。
        /// </summary>
        private void TryConsumeCompletedCycle(Grayson.Vision.Contracts.Infrastructure.Metrics.StationMetrics metrics)
        {
            string code = SelectedStationCode;

            // 工位已切换/计数回滚（重启 Worker 归零）→ 以当前读数为水位基线，不回溯
            if (_lastAppliedCompletedStation != code || metrics.FinalizedWorkOrders < _lastAppliedCompletedCount)
            {
                _lastAppliedCompletedCount = metrics.FinalizedWorkOrders;
                _lastAppliedCompletedStation = code;
                return;
            }

            if (metrics.FinalizedWorkOrders == _lastAppliedCompletedCount) return; // 无新周期
            if (metrics.LastWorkOrderResult == 0) return;                            // 尚无判定

            _lastAppliedCompletedCount = metrics.FinalizedWorkOrders;
            IsCycleRunning = false;
            _cycleWatch.Stop();

            bool isOk = metrics.LastWorkOrderResult > 0;
            LastResult = isOk ? "OK" : "NG";
            if (metrics.LastCycleTimeMs > 0) CycleTimeMs = metrics.LastCycleTimeMs;

            Results.Insert(0, new StationResultEntry
            {
                BatchId = Guid.NewGuid().ToString("N").Substring(0, 8),
                Result = LastResult,
                Message = $"业务周期完成 (耗时: {metrics.LastCycleTimeMs:F0}ms)"
            });
            AddLog(isOk ? "INFO" : "ERROR",
                $"业务周期结束，判定: {LastResult}，耗时: {metrics.LastCycleTimeMs:F0}ms");
            RefreshTriggerStats();
        }

        /// <summary>
        /// 工位切换时清空上一工位的会话态展示内容（#2 内容区按工位隔离）。
        /// 仅清「会话瞬态」（日志/最近结果/RESULT/周期字段/Live 与完成计数水位），
        /// 不动跨会话持久化统计(StationStats 由 AccumulateStats 按工位回填)与图像现场(由调用方已清)。
        /// </summary>
        private void ClearStationSessionContent()
        {
            // Live：切换工位前 DetachClientEvents 已 TryYieldLiveCameraSync 停流
            IsLiveViewOn = false;
            IsCycleRunning = false;
            _lastProcessBusy = false;
            _cycleWatch.Reset();
            LastResult = "--";
            CycleTimeMs = 0;

            Logs.Clear();
            Results.Clear();

            // 复位视觉/任务引导瞬态
            LatestVisionText = "--";
            _lastVisionWx = _lastVisionWy = _lastVisionAngle = null;

            // 完成计数水位置 -1 → 下一轮 TryConsumeCompletedCycle 与目标工位当前水位对齐（不回溯）
            _lastAppliedCompletedCount = -1;
            _lastAppliedCompletedStation = null;
        }

        /// <summary>
        /// 水位法累计统计：把 Worker Metrics 超出水位线的增量累进 JSON 文件。
        /// 文件不存在（首次/被清除）→ 以当前读数为基线重建，不回溯历史；
        /// 读数小于水位线（程序重启 Worker 归零）→ 水位线下移，零累计。
        /// </summary>
        private void AccumulateStats(Grayson.Vision.Core.StationWorker worker)
        {
            var code = SelectedStationCode;
            if (worker == null || string.IsNullOrEmpty(code)) return;

            var metrics = worker.Metrics;
            var data = _statsService.Load(code);

            if (data == null)
            {
                // 首次（或刚被系统设置清除）：以当前 Worker 读数为水位基线，历史不回溯
                data = new StationStatsData
                {
                    StationCode = code,
                    WatermarkTotal = metrics.TotalWorkOrders,
                    WatermarkOk = metrics.OkCount,
                    WatermarkNg = metrics.NgCount
                };
                _statsService.Save(data);
            }
            else
            {
                // Worker 计数回落（重启/重建）→ 水位线下移到当前读数，不产生累计
                if (metrics.TotalWorkOrders < data.WatermarkTotal)
                {
                    data.WatermarkTotal = metrics.TotalWorkOrders;
                    data.WatermarkOk = metrics.OkCount;
                    data.WatermarkNg = metrics.NgCount;
                    _statsService.Save(data);
                }
                else if (metrics.TotalWorkOrders > data.WatermarkTotal)
                {
                    long dTotal = metrics.TotalWorkOrders - data.WatermarkTotal;
                    long dOk = metrics.OkCount - data.WatermarkOk;
                    long dNg = metrics.NgCount - data.WatermarkNg;
                    if (dOk < 0) dOk = 0;
                    if (dNg < 0) dNg = 0;

                    data.TotalCount += dTotal;
                    data.OkCount += dOk;
                    data.NgCount += dNg;
                    data.WatermarkTotal = metrics.TotalWorkOrders;
                    data.WatermarkOk = metrics.OkCount;
                    data.WatermarkNg = metrics.NgCount;
                    _statsService.Save(data);
                }
            }

            // 头部 KPI 以持久化文件为准（离开界面再回来 / 重启程序都从文件恢复）
            TotalCount = (int)Math.Min(data.TotalCount, int.MaxValue);
            OkCount = (int)Math.Min(data.OkCount, int.MaxValue);
            NgCount = (int)Math.Min(data.NgCount, int.MaxValue);

            if (!IsCycleRunning && data.LastWorkOrderResult != 0 && data.LastCycleTimeMs > 0)
            {
                CycleTimeMs = data.LastCycleTimeMs;
            }
        }

        #endregion

        #region Live 相机实时画面

        /// <summary>手动开关 Live 视图（按钮入口）。</summary>
        private void ToggleLiveView()
        {
            if (IsLiveViewOn) StopLiveGrab(auto: false);
            else StartLiveGrab(auto: false);
        }

        /// <summary>
        /// 解析 Live 用相机：优先取执行链 AcquireImage 节点配置的相机别名
        /// （与业务采图同一台相机——Live 看到的就是检测视野）；读不到则兜底
        /// 取工位注册的第一台相机。
        /// </summary>
        private ICamera ResolveLiveCamera(Grayson.Vision.Core.StationWorker worker)
        {
            if (worker?.Context == null) return null;

            try
            {
                var chain = worker.ActiveExecutionChain;
                if (chain?.Nodes != null)
                {
                    foreach (var node in chain.Nodes)
                    {
                        if (node == null || node.Type != NodeType.AcquireImage) continue;
                        var pm = node.ParameterModel;
                        if (pm == null) continue;
                        var alias = pm.GetType().GetProperty("CameraAlias")?.GetValue(pm) as string;
                        if (string.IsNullOrEmpty(alias)) continue;
                        var cam = worker.Context.ResolveDevice(alias) as ICamera;
                        if (cam != null) return cam;
                    }
                }
            }
            catch { /* 反射读参数失败 → 走兜底扫描 */ }

            try
            {
                var states = worker.Context.DeviceManager.GetDeviceStates();
                foreach (var kv in states)
                {
                    if (worker.Context.ResolveDevice(kv.Key) is ICamera cam) return cam;
                }
            }
            catch { }

            return null;
        }

        /// <summary>开启相机连续采集并订阅帧事件（auto=true 表示业务联动自动开启）。</summary>
        private void StartLiveGrab(bool auto)
        {
            if (_liveGrabbing) return;

            var worker = GetStationWorker();
            var camera = ResolveLiveCamera(worker);
            if (camera == null)
            {
                if (!auto) AddLog("WARN", "未找到可用相机（工位未注册相机或未连接），无法开启实时画面。");
                return;
            }

            try
            {
                // 相机未连接时先打开（与 AcquireImageExecutor 同样的自愈策略）
                if (camera.State != Grayson.Vision.Contracts.Devices.Enums.DeviceState.Connected)
                {
                    var open = camera.Connect();
                    if (open?.Success != true)
                    {
                        if (!auto) AddLog("WARN", $"相机打开失败: {open?.Message}");
                        return;
                    }
                }

                camera.FrameReceived += LiveCamera_OnFrameReceived;
                camera.SetTriggerMode(0); // 连续采集模式
                var start = camera.StartContinuousGrab();
                if (start?.Success != true)
                {
                    camera.FrameReceived -= LiveCamera_OnFrameReceived;
                    if (!auto) AddLog("WARN", $"开启连续采集失败: {start?.Message}");
                    return;
                }

                lock (_liveSync)
                {
                    if (_liveGrabbing) return; // 并发让渡已先到，放弃本次开启
                    _liveCamera = camera;
                    _liveGrabbing = true;
                    _lastLivePushUtc = DateTime.UtcNow; // 心跳宽限：等待首帧
                }
                IsLiveViewOn = true;
                AddLog("INFO", auto
                    ? "视觉链完成，自动开启相机实时画面（吸取/搬运/放料过程实时可见，下次采图节点执行前自动让渡）。"
                    : "相机实时画面已开启。");
            }
            catch (Exception ex)
            {
                if (!auto) AddLog("ERROR", $"开启实时画面失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 相机侧同步让渡（线程安全：UI 线程/执行链线程均可调用）。
        /// 只做「退订帧事件 + 停连续采集 + 状态复位」，绝不碰 UI——因为让渡可能
        /// 发生在执行链线程上（AcquireImage 节点执行前），且必须在节点执行器
        /// 改触发模式之前完成，所以不能走 Dispatcher 异步。
        /// 返回 true 表示本次确实停掉了 Live。
        /// </summary>
        private bool TryYieldLiveCameraSync()
        {
            lock (_liveSync)
            {
                if (!_liveGrabbing) return false;
                var camera = _liveCamera;
                _liveCamera = null;
                _liveGrabbing = false;
                try
                {
                    if (camera != null)
                    {
                        camera.FrameReceived -= LiveCamera_OnFrameReceived;
                        camera.StopContinuousGrab();
                    }
                }
                catch
                {
                    // 停流失败不阻塞业务链——AcquireImageExecutor 内部还有兜底停流
                }
                return true;
            }
        }

        /// <summary>停流后的 UI 收尾（必须在 UI 线程调用）：按钮状态复位 + 回到最近静态图。</summary>
        private void RestoreLastStaticImage()
        {
            // 回到最近一帧静态图像（模板匹配结果 + 叠加轮廓）
            var lastStatic = ImageDisplayVm.ImageHistoryList
                .LastOrDefault(c => c != null && c.NodeId != LiveNodeId);
            if (lastStatic != null) ImageDisplayVm.SelectImageItem(lastStatic);
        }

        /// <summary>停止 Live 采集并回到最近一帧静态图像（如模板匹配结果）。</summary>
        private void StopLiveGrab(bool auto)
        {
            var wasGrabbing = TryYieldLiveCameraSync();
            IsLiveViewOn = false;

            if (wasGrabbing)
            {
                RestoreLastStaticImage();
                AddLog("INFO",
                    auto ? "实时画面已让出相机（业务周期开始，保证采图节点独占配置）。" : "相机实时画面已关闭。");
            }
        }

        /// <summary>
        /// Live 帧回调（相机线程）：节流 ~15fps 后转 HImage 推给显示层。
        /// 注意：不再按 IsProcessBusy 提前让渡——业务周期内只有 AcquireImage 节点
        /// 真正需要相机，让渡时机已下沉到该节点执行前（Client_OnNodeExecuting）。
        /// </summary>
        private void LiveCamera_OnFrameReceived(object sender, FrameEventArgs e)
        {
            if (e?.Buffer == null || !_liveGrabbing) return;

            var now = DateTime.UtcNow;
            if ((now - _lastLivePushUtc).TotalMilliseconds < 66) return; // ~15fps 节流
            _lastLivePushUtc = now;

            var frame = e; // 捕获到闭包
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (!_liveGrabbing) return;
                try
                {
                    var conv = ImageBasicTool.FrameToHImage(frame);
                    if (!conv.Success || conv.Data == null) return;

                    var img = _renderService.WrapImage(conv.Data);
                    if (img == null) return;

                    // 与 Client_OnFrameRendered 同一套上下文管理：统一走 AddOrUpdateImageContext，
                    // 旧 Live 帧由替换路径按引用计数释放（防内存泄漏）
                    var ctx = new WpfImageRenderContext
                    {
                        NodeId = LiveNodeId,
                        NodeName = "📷 实时画面",
                        Image = img,
                        Thumbnail = _renderService.CreateThumbnail(img)
                    };
                    ImageDisplayVm.AddOrUpdateImageContext(ctx);
                }
                catch
                {
                    // 单帧渲染失败丢弃，不影响后续帧
                }
            }), DispatcherPriority.Background);
        }

        #endregion

        private void AddLog(string level, string message)
        {
            Logs.Insert(0, new StationLogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message
            });

            if (Logs.Count > 500)
            {
                for (int i = Logs.Count - 1; i >= 400; i--)
                {
                    Logs.RemoveAt(i);
                }
            }
        }
    }
}