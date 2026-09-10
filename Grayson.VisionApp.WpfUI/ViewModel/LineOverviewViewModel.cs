using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Core.Client;
using Grayson.Vision.Core.Station;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public enum CardViewMode
    {
        Compact,   // 标准统计卡片模式
        Thumbnail  // 视觉缩略图监控模式
    }

    public class StationStatusCardModel : ViewModelBase
    {
        public string StationId { get; set; }
        public string StationCode { get; set; }
        public string StationName { get; set; }
        public string LineId { get; set; }

        private bool _isConnected;
        public bool IsConnected { get => _isConnected; set => Set(ref _isConnected, value); }

        private string _state;
        public string State
        {
            get => _state;
            set
            {
                if (Set(ref _state, value))
                {
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(StateBrushKey));
                }
            }
        }

        private string _currentRecipeName;
        public string CurrentRecipeName { get => _currentRecipeName; set => Set(ref _currentRecipeName, value); }

        private int _totalCount;
        public int TotalCount { get => _totalCount; set { if (Set(ref _totalCount, value)) OnPropertyChanged(nameof(YieldText)); } }

        private int _okCount;
        public int OkCount { get => _okCount; set { if (Set(ref _okCount, value)) OnPropertyChanged(nameof(YieldText)); } }

        private int _ngCount;
        public int NgCount { get => _ngCount; set { if (Set(ref _ngCount, value)) OnPropertyChanged(nameof(YieldText)); } }

        // ── 最近一次结果徽标（低刷新：由 1s 轮询在 AccumulateStats 旁一并更新）──
        private int _lastResultCode; // 1=OK, -1=NG, 0=未知/无
        /// <summary>
        /// 最近一次定案周期结果码（1=OK / -1=NG / 0=未知）。由 1s 轮询读 worker.Metrics.LastWorkOrderResult 写入，
        /// 事件无关、频率受限，避免高频刷新整卡。
        /// </summary>
        public int LastResultCode
        {
            get => _lastResultCode;
            set
            {
                if (Set(ref _lastResultCode, value))
                {
                    OnPropertyChanged(nameof(HasLastResult));
                    OnPropertyChanged(nameof(LastResultText));
                    OnPropertyChanged(nameof(LastResultBrush));
                }
            }
        }

        /// <summary>是否已有最近一次结果（无则隐藏徽标）。</summary>
        public bool HasLastResult => _lastResultCode != 0;

        /// <summary>徽标文案：最近OK / 最近NG。</summary>
        public string LastResultText => _lastResultCode == 1 ? "最近 OK" : (_lastResultCode == -1 ? "最近 NG" : "");

        /// <summary>徽标填充色（半透明绿/红底，深色主题仍清晰可辨）。</summary>
        public System.Windows.Media.Brush LastResultBrush
        {
            get
            {
                if (_lastResultCode == 1)
                    return _chipCache[0];
                if (_lastResultCode == -1)
                    return _chipCache[1];
                return System.Windows.Media.Brushes.Transparent;
            }
        }

        // 预构建半透明色刷（绿 #3300C853 / 红 #33FF3D00），避免每次取值解析颜色
        private static readonly System.Windows.Media.Brush[] _chipCache = new[]
        {
            new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3300C853")),
            new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#33FF3D00"))
        };

        private BitmapSource _latestThumbnail;
        /// <summary>
        /// 拓扑缩略图模式下展示的最新渲染帧（每工位独立 1s 节流更新）。
        /// </summary>
        public BitmapSource LatestThumbnail
        {
            get => _latestThumbnail;
            set => Set(ref _latestThumbnail, value);
        }

        /// <summary>
        /// 状态中文文案：未连接或空状态显示「未连接」，其余按状态机中文映射。
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

        public string YieldText
        {
            get
            {
                if (TotalCount <= 0) return "--";
                double yield = (double)OkCount / TotalCount * 100;
                return $"{yield:F1}%";
            }
        }
    }

    public class LineOverviewViewModel : ViewModelBase, IDisposable, INavigationAware
    {
        /// <summary>
        /// 产线筛选下拉首位「全部产线」占位项（非配置文件中的真实产线）。
        /// 选中时等价于不筛选，展示所有产线下的工位。
        /// </summary>
        public const string AllLinesOption = "全部产线";

        private readonly StationRuntimeManager _runtimeManager;
        private readonly StationConfigService _configService;
        private readonly StationStatisticsService _statsService = new StationStatisticsService();
        private readonly HalconImageRenderService _renderService;
        private readonly IStationHostRuntime _hostRuntime;

        // 🌟 订阅按 StationWorker 实例管理（而非 StationCode / IWorkerClient 代理）：
        //   · 统计需要 worker.Metrics（IWorkerClient 不暴露 Metrics）；
        //   · EmbeddedWorkerClientProxy 的事件退订用「新 lambda -=」（退订无效、泄漏），
        //     直连 StationWorker 用具名方法可正确退订；
        //   · 工位保存时 RemoveStation + 重建 worker 后，新实例会被重新订阅（按实例去重）。
        private readonly Dictionary<string, Grayson.Vision.Core.StationWorker> _subscribedWorkers =
            new Dictionary<string, Grayson.Vision.Core.StationWorker>();

        /// <summary>每工位缩略图节流时间戳（低帧率监控：1s/工位，多工位同时出图互不挤占）。</summary>
        private readonly Dictionary<string, DateTime> _thumbnailThrottle = new Dictionary<string, DateTime>();

        /// <summary>
        /// 统计轮询（常驻 1s）：Worker Metrics 内存态无通知 → 水位法累计持久化 + 卡片状态/统计刷新。
        /// 与工位监视页的 500ms 轮询同源同法（水位线在文件里，双页面并发累计幂等）。
        /// </summary>
        private readonly DispatcherTimer _pollTimer;

        private bool _disposed;
        private int _gridColumns = 3;
        /// <summary>
        /// 动态计算的网格列数 (2, 3, 4)
        /// </summary>
        public int GridColumns
        {
            get => _gridColumns;
            set => Set(ref _gridColumns, value);
        }

        // 当加载/刷新工位列表时调用此方法计算列数
        private void UpdateGridColumns()
        {
            int count = StationCards?.Count ?? 0;

            if (count <= 2)
            {
                GridColumns = 2; // 1~2 个工位时，使用 2 列，卡片宽大气
            }
            else if (count <= 6)
            {
                GridColumns = 3; // 3~6 个工位时，使用 3 列布局
            }
            else
            {
                GridColumns = 4; // >6 个工位时，采用 4 列精细网格布局
            }
        }

        // LineOverviewViewModel.cs 中增加智能计算逻辑
        // 1. 手动实现 Clamp 兼容逻辑（适配 .NET Framework 4.X）
        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // 2. 补全 CalculateLayout 无参重载与有参重载
        public void CalculateLayout(double actualWidth)
        {
            int stationCount = StationCards?.Count ?? 0;
            if (stationCount == 0) return;

            if (actualWidth <= 0)
            {
                // 默认保底计算
                UpdateGridColumns();
                return;
            }

            // 单张卡片的理想宽度范围 320px ~ 420px
            int targetColumns = (int)(actualWidth / 360);

            // 限制最小 2 列，最大 5 列
            GridColumns = Clamp(targetColumns, 2, 5);
        }

        // 提供无参调用重载，避免 CS7036 报错
        public void CalculateLayout()
        {
            UpdateGridColumns();
        }

        public LineOverviewViewModel(StationRuntimeManager runtimeManager = null, StationConfigService configService = null, IStationHostRuntime hostRuntime = null)
        {
            _runtimeManager = runtimeManager ?? new StationRuntimeManager();
            _configService = configService ?? new StationConfigService();
            _renderService = new HalconImageRenderService();
            _hostRuntime = hostRuntime ?? App.StationHostRuntime;

            Lines = new ObservableCollection<string>();
            StationCards = new ObservableCollection<StationStatusCardModel>();

            RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
            MonitorStationCommand = new RelayCommand(OnMonitorStation, _ => SelectedCard != null);
            ToggleViewModeCommand = new RelayCommand(_ => ViewMode = ViewMode == CardViewMode.Compact ? CardViewMode.Thumbnail : CardViewMode.Compact);

            // 统计轮询：Worker Metrics 是内存态权威数据源（Interlocked 计数、无通知机制），
            // 1s 轮询做水位累计 + 卡片状态/统计刷新（页面注册为缓存单例，轮询跨导航存活）。
            _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _pollTimer.Tick += (s, e) => OnPollTimerTick();
            _pollTimer.Start();

            LoadLines();
        }

        public ObservableCollection<string> Lines { get; set; }
        public ObservableCollection<StationStatusCardModel> StationCards { get; set; }

        private CardViewMode _viewMode = CardViewMode.Compact;
        public CardViewMode ViewMode
        {
            get => _viewMode;
            set
            {
                if (Set(ref _viewMode, value))
                {
                    OnPropertyChanged(nameof(ViewModeToggleText));
                }
            }
        }

        /// <summary>
        /// 视图切换按钮文本：当前是统计卡片时提示切换到缩略图监控，反之提示切回统计卡片。
        /// </summary>
        public string ViewModeToggleText =>
            ViewMode == CardViewMode.Compact ? "🖼 缩略图监控" : "📊 统计卡片";

        private StationStatusCardModel _selectedCard;
        public StationStatusCardModel SelectedCard
        {
            get => _selectedCard;
            set { if (Set(ref _selectedCard, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        /// <summary>
        /// 当前产线筛选：默认「全部产线」（与空串同义，展示所有产线下工位）。
        /// </summary>
        private string _selectedLine = AllLinesOption;
        public string SelectedLine
        {
            get => _selectedLine;
            set { if (Set(ref _selectedLine, value)) _ = RefreshAsync(); }
        }

        public ICommand RefreshCommand { get; }
        public ICommand MonitorStationCommand { get; }
        public ICommand ToggleViewModeCommand { get; }

        /// <summary>
        /// 导航激活钩子（页面为缓存单例，跨导航复用同一实例）：
        /// 每次导航进入都按配置文件全量重建产线列表与工位卡片——
        /// 增删/修改工位信息后切回本页立即生效，无需重启程序。
        /// </summary>
        public void OnNavigatedTo(object parameter)
        {
            ReloadFromConfig();
        }

        /// <summary>
        /// 导航离开钩子：缓存单例设计下轮询与订阅常驻存活（跨导航统计/缩略图不中断），无需清理。
        /// </summary>
        public void OnNavigatedFrom()
        {
        }

        /// <summary>
        /// 以配置文件为准全量重建产线与工位卡片（增删改全部生效）：
        /// 清空旧卡片与订阅 → 重新加载产线 → 选定产线 → 显式刷新。
        /// 统计从持久化文件恢复（水位法），缩略图 1s 内按帧事件重新出图。
        /// </summary>
        private void ReloadFromConfig()
        {
            var previous = SelectedLine;

            // 1. 重新加载产线列表（配置为准；首位固定「全部产线」占位项）
            Lines.Clear();
            Lines.Add(AllLinesOption);
            var allLines = _configService.LoadAllLines();
            foreach (var line in allLines)
            {
                if (!string.IsNullOrEmpty(line.LineName)) Lines.Add(line.LineName);
            }

            // 2. 清理全部订阅与节流（卡片集合即将整体重建，避免悬挂订阅）
            foreach (var stationCode in _subscribedWorkers.Keys.ToList())
            {
                UnsubscribeWorker(stationCode);
            }

            // 3. 重建卡片集合：增删改工位全部按最新配置反映（统计由 RefreshAsync 从文件恢复）
            StationCards.Clear();

            // 4. 选定产线：保留原选择（含「全部产线」），已删除则回退「全部产线」；直接赋值避免 setter 触发重复刷新
            _selectedLine = previous != null && Lines.Contains(previous) ? previous : AllLinesOption;
            OnPropertyChanged(nameof(SelectedLine));

            // 5. 显式刷新（SelectedLine 值未变时 setter 不触发，必须主动调）
            _ = RefreshAsync();
            UpdateGridColumns();
        }


        private void LoadLines()
        {
            Lines.Clear();
            Lines.Add(AllLinesOption);
            var allLines = _configService.LoadAllLines();
            foreach (var line in allLines)
            {
                if (!string.IsNullOrEmpty(line.LineName)) Lines.Add(line.LineName);
            }

            // 默认「全部产线」（字段初值已是它：SelectedLine setter 不会触发，需主动拉取一次卡片数据）
            SelectedLine = AllLinesOption;
            _ = RefreshAsync();
            UpdateGridColumns();
            CalculateLayout();
        }

        private async Task RefreshAsync()
        {
            var allLines = _configService.LoadAllLines();
            // 「全部产线」或空选择 → 不筛选，展示所有产线下的工位
            var showAll = string.IsNullOrEmpty(SelectedLine) || SelectedLine == AllLinesOption;
            var targetStations = showAll
                ? allLines.SelectMany(l => l.Stations)
                : allLines.Where(l => l.LineName == SelectedLine).SelectMany(l => l.Stations);

            var runtime = _hostRuntime as StationHostRuntime;
            foreach (var stationConfig in targetStations)
            {
                var card = StationCards.FirstOrDefault(c => c.StationId == stationConfig.StationId);
                if (card == null)
                {
                    card = new StationStatusCardModel
                    {
                        StationId = stationConfig.StationId,
                        StationCode = stationConfig.StationCode,
                        StationName = stationConfig.StationName,
                        LineId = stationConfig.LineId,
                        CurrentRecipeName = stationConfig.BoundRecipeName
                    };
                    StationCards.Add(card);
                }

                // 优先直连 StationHostRuntime 的 Worker：状态/统计/帧事件一把抓；
                // 拿不到时回退 IWorkerClient 只取状态（旧运行时兼容）。
                var worker = runtime?.GetStationWorker(stationConfig.StationCode);
                if (worker != null)
                {
                    card.IsConnected = true;
                    card.State = worker.State.ToString();
                    SubscribeWorker(stationConfig.StationCode, worker);
                    AccumulateStats(stationConfig.StationCode, worker, card);
                }
                else
                {
                    var client = _hostRuntime?.GetStationClient(stationConfig.StationCode)
                                 ?? _runtimeManager.GetClient(stationConfig.StationCode);
                    card.IsConnected = client?.IsConnected ?? false;
                    card.State = client?.CurrentState.ToString() ?? "NotConnected";
                    UnsubscribeWorker(stationConfig.StationCode);
                    LoadStatsOnly(stationConfig.StationCode, card);
                }
            }

            // 清理已不在目标列表中的订阅
            CleanRemovedWorkerSubscriptions(targetStations.Select(s => s.StationCode));

            await Task.CompletedTask;
        }

        /// <summary>
        /// 🌟 按 worker 实例订阅事件；若实例已变化则先退订旧实例再订阅新实例。
        /// 直连 StationWorker（具名方法可正确退订，绕开 Proxy 的 lambda 退订陷阱）。
        /// </summary>
        private void SubscribeWorker(string stationCode, Grayson.Vision.Core.StationWorker worker)
        {
            if (string.IsNullOrEmpty(stationCode) || worker == null) return;

            if (_subscribedWorkers.TryGetValue(stationCode, out var existing))
            {
                if (ReferenceEquals(existing, worker)) return; // 已订阅同一实例，无需重复
                UnsubscribeWorker(stationCode);               // 实例已重建，退订旧实例
            }

            worker.OnStateChanged += Worker_OnStateChanged;
            worker.OnFrameRendered += Worker_OnFrameRendered;
            _subscribedWorkers[stationCode] = worker;
        }

        private void UnsubscribeWorker(string stationCode)
        {
            if (string.IsNullOrEmpty(stationCode)) return;
            if (!_subscribedWorkers.TryGetValue(stationCode, out var existing)) return;

            existing.OnStateChanged -= Worker_OnStateChanged;
            existing.OnFrameRendered -= Worker_OnFrameRendered;
            _subscribedWorkers.Remove(stationCode);
            _thumbnailThrottle.Remove(stationCode);
        }

        private void CleanRemovedWorkerSubscriptions(IEnumerable<string> activeStationCodes)
        {
            var activeSet = new HashSet<string>(activeStationCodes ?? Enumerable.Empty<string>());
            var removed = _subscribedWorkers.Keys.Where(c => !activeSet.Contains(c)).ToList();
            foreach (var stationCode in removed)
            {
                UnsubscribeWorker(stationCode);
            }
        }

        private void Worker_OnStateChanged(object sender, StationState e)
        {
            if (sender is Grayson.Vision.Core.StationWorker worker)
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    var card = StationCards.FirstOrDefault(c => c.StationCode == worker.StationId);
                    if (card != null) card.State = e.ToString();
                });
            }
        }

        /// <summary>
        /// 事件驱动的低帧率缩略图更新（每工位独立 1s 节流，多工位同时出图互不挤占）。
        /// 直连 Worker 帧事件：RenderData 为图像端口借用值，WrapImage 借用包一层，
        /// CreateThumbnail 在 Halcon 内存层缩放到 ~100px 宽后 Freeze 成 BitmapSource（跨线程安全）。
        /// </summary>
        private void Worker_OnFrameRendered(object sender, ImageRenderEventArgs e)
        {
            if (e?.RenderData == null) return;

            var worker = sender as Grayson.Vision.Core.StationWorker;
            string code = worker?.StationId ?? e.StationId;
            if (string.IsNullOrEmpty(code)) return;

            // 每工位独立节流：1s 内只刷新一次（低帧率监控）
            if (_thumbnailThrottle.TryGetValue(code, out var last) &&
                (DateTime.Now - last).TotalMilliseconds < 1000) return;
            _thumbnailThrottle[code] = DateTime.Now;

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var card = StationCards.FirstOrDefault(c => c.StationCode == code);
                    if (card != null)
                    {
                        var renderImg = _renderService.WrapImage(e.RenderData);
                        if (renderImg != null)
                        {
                            var thumb = _renderService.CreateThumbnail(renderImg);
                            if (thumb != null)
                            {
                                card.LatestThumbnail = thumb;
                            }
                        }
                    }
                }
                catch
                {
                    // 借用语义下图像可能已被上游释放（黑屏修复前的历史隐患）：缩略图失败静默跳过，
                    // 绝不让渲染异常逃逸到 UI 线程调度器（会闪退整程序）
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// 1s 统计轮询：卡片状态刷新 + Worker Metrics 水位累计并持久化（业务过程工位主计数路径）。
        /// 无 Worker 的工位（纯视觉链/未挂载）只读持久化文件显示（RecordChainCompleted 已写）。
        /// 水位法幂等：与工位监视页并发累计同一工位时结果一致（水位线在文件里，增量按同一基线计算）。
        /// </summary>
        private void OnPollTimerTick()
        {
            if (_disposed) return;
            try
            {
                var runtime = _hostRuntime as StationHostRuntime;
                foreach (var card in StationCards.ToList())
                {
                    if (string.IsNullOrEmpty(card.StationCode)) continue;

                    var worker = runtime?.GetStationWorker(card.StationCode);
                    if (worker != null)
                    {
                        card.IsConnected = true;
                        card.State = worker.State.ToString();
                        SubscribeWorker(card.StationCode, worker);
                        AccumulateStats(card.StationCode, worker, card);
                    }
                    else
                    {
                        card.IsConnected = false;
                        LoadStatsOnly(card.StationCode, card);
                    }
                }
            }
            catch
            {
                // 轮询异常不影响任何业务
            }
        }

        /// <summary>
        /// 水位法累计统计（与工位监视页同法）：把 Worker Metrics 超出水位线的增量累进 JSON 文件。
        /// 文件不存在（首次/被清除）→ 以当前读数为基线重建，不回溯历史；
        /// 读数小于水位线（程序重启 Worker 归零）→ 水位线下移，零累计。
        /// </summary>
        private void AccumulateStats(string stationCode, Grayson.Vision.Core.StationWorker worker, StationStatusCardModel card)
        {
            if (worker == null || string.IsNullOrEmpty(stationCode) || card == null) return;

            var metrics = worker.Metrics;
            // 最近一次结果徽标：随本 1s 轮询一并刷新（LastWorkOrderResult=1/OK、-1/NG、0/未知）
            card.LastResultCode = (int)metrics.LastWorkOrderResult;
            var data = _statsService.Load(stationCode);

            if (data == null)
            {
                // 首次（或刚被系统设置清除）：以当前 Worker 读数为水位基线，历史不回溯
                data = new StationStatsData
                {
                    StationCode = stationCode,
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
                    long dOk = Math.Max(0, metrics.OkCount - data.WatermarkOk);
                    long dNg = Math.Max(0, metrics.NgCount - data.WatermarkNg);

                    data.TotalCount += dTotal;
                    data.OkCount += dOk;
                    data.NgCount += dNg;
                    data.WatermarkTotal = metrics.TotalWorkOrders;
                    data.WatermarkOk = metrics.OkCount;
                    data.WatermarkNg = metrics.NgCount;
                    _statsService.Save(data);
                }
            }

            // 卡片以持久化文件为准（离开界面/重启程序都从文件恢复）
            card.TotalCount = (int)Math.Min(data.TotalCount, int.MaxValue);
            card.OkCount = (int)Math.Min(data.OkCount, int.MaxValue);
            card.NgCount = (int)Math.Min(data.NgCount, int.MaxValue);
        }

        /// <summary>无 Worker 工位（纯视觉链/未挂载）：只读持久化统计文件显示。</summary>
        private void LoadStatsOnly(string stationCode, StationStatusCardModel card)
        {
            if (string.IsNullOrEmpty(stationCode) || card == null) return;

            // 无实时 Worker：清空最近结果徽标，避免残留上次运行结果
            card.LastResultCode = 0;

            var data = _statsService.Load(stationCode);
            if (data == null) return;

            card.TotalCount = (int)Math.Min(data.TotalCount, int.MaxValue);
            card.OkCount = (int)Math.Min(data.OkCount, int.MaxValue);
            card.NgCount = (int)Math.Min(data.NgCount, int.MaxValue);
        }

        private void OnMonitorStation(object parameter)
        {
            if (SelectedCard == null) return;
            NavigationService.Current?.NavigateTo(PageType.StationMonitor, SelectedCard.StationCode);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pollTimer?.Stop();
            foreach (var stationCode in _subscribedWorkers.Keys.ToList())
            {
                UnsubscribeWorker(stationCode);
            }
            _subscribedWorkers.Clear();
        }
    }
}
