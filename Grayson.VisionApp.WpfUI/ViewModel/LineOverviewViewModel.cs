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

        private BitmapSource _latestThumbnail;
        /// <summary>
        /// 拓扑缩略图模式下展示的最新渲染帧
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

        public string StateBrushKey
        {
            get
            {
                switch (State)
                {
                    case "Running": return "SuccessBrush";
                    case "Faulted": return "DangerBrush";
                    case "Idle": return "InfoBrush";
                    case "Paused": return "WarningBrush";
                    default: return "TextDisabledBrush";
                }
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

    public class LineOverviewViewModel : ViewModelBase
    {
        private readonly StationRuntimeManager _runtimeManager;
        private readonly StationConfigService _configService;
        private readonly HalconImageRenderService _renderService;
        private readonly IStationHostRuntime _hostRuntime;
        // 🌟 订阅按 client 实例管理（而非 StationCode）：工位保存时 RemoveStation + 重建 client 后，
        // 新实例会被重新订阅，修复旧代码"按 Code 去重导致新实例永远收不到事件"的 Bug。
        private readonly Dictionary<string, IWorkerClient> _subscribedClients = new Dictionary<string, IWorkerClient>();
        private DateTime _lastThumbnailUpdate = DateTime.MinValue;
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

        private string _selectedLine;
        public string SelectedLine
        {
            get => _selectedLine;
            set { if (Set(ref _selectedLine, value)) _ = RefreshAsync(); }
        }

        public ICommand RefreshCommand { get; }
        public ICommand MonitorStationCommand { get; }
        public ICommand ToggleViewModeCommand { get; }

        private void LoadLines()
        {
            Lines.Clear();
            var allLines = _configService.LoadAllLines();
            foreach (var line in allLines)
            {
                if (!string.IsNullOrEmpty(line.LineName)) Lines.Add(line.LineName);
            }
            if (Lines.Any()) SelectedLine = Lines.First();
            // ... 加载卡片数据 ...
            UpdateGridColumns();
            CalculateLayout();
        }

        private async Task RefreshAsync()
        {
            var allLines = _configService.LoadAllLines();
            var targetStations = string.IsNullOrEmpty(SelectedLine)
                ? allLines.SelectMany(l => l.Stations)
                : allLines.Where(l => l.LineName == SelectedLine).SelectMany(l => l.Stations);

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

                // 优先从 Core StationHostRuntime 获取客户端，确保与全局运行时一致
                var client = _hostRuntime?.GetStationClient(stationConfig.StationCode)
                             ?? _runtimeManager.GetClient(stationConfig.StationCode);
                if (client != null)
                {
                    card.IsConnected = client.IsConnected;
                    card.State = client.CurrentState.ToString();

                    // 🌟 每个工位只挂载一次事件；若实例已重建（RemoveStation+重建），退订旧实例并重新订阅
                    SubscribeClient(stationConfig.StationCode, client);
                }
                else
                {
                    card.IsConnected = false;
                    card.State = "NotConnected";
                    UnsubscribeClient(stationConfig.StationCode);
                }
            }

            // 清理已不在目标列表中的订阅
            CleanRemovedSubscriptions(targetStations.Select(s => s.StationCode));

            await Task.CompletedTask;
        }

        /// <summary>
        /// 🌟 按 client 实例订阅事件；若实例已变化则先退订旧实例再订阅新实例。
        /// </summary>
        private void SubscribeClient(string stationCode, IWorkerClient client)
        {
            if (string.IsNullOrEmpty(stationCode) || client == null) return;

            if (_subscribedClients.TryGetValue(stationCode, out var existing))
            {
                if (ReferenceEquals(existing, client)) return; // 已订阅同一实例，无需重复
                UnsubscribeClient(stationCode);                // 实例已重建，退订旧实例
            }

            client.OnStateChanged += Client_OnStateChanged;
            client.OnFrameRendered += Client_OnFrameRendered;
            _subscribedClients[stationCode] = client;
        }

        private void UnsubscribeClient(string stationCode)
        {
            if (string.IsNullOrEmpty(stationCode)) return;
            if (!_subscribedClients.TryGetValue(stationCode, out var existing)) return;

            existing.OnStateChanged -= Client_OnStateChanged;
            existing.OnFrameRendered -= Client_OnFrameRendered;
            _subscribedClients.Remove(stationCode);
        }

        private void CleanRemovedSubscriptions(IEnumerable<string> activeStationCodes)
        {
            var activeSet = new HashSet<string>(activeStationCodes ?? Enumerable.Empty<string>());
            var removed = _subscribedClients.Keys.Where(c => !activeSet.Contains(c)).ToList();
            foreach (var stationCode in removed)
            {
                UnsubscribeClient(stationCode);
            }
        }

        private void Client_OnStateChanged(object sender, StationState e)
        {
            if (sender is IWorkerClient client)
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    var card = StationCards.FirstOrDefault(c => c.StationCode == client.StationId);
                    if (card != null) card.State = e.ToString();
                });
            }
        }

        // 🌟 事件驱动的降频渲染更新（每工位最快 500ms 刷新一次缩略图，避免 UI 卡顿）
        private void Client_OnFrameRendered(object sender, ImageRenderEventArgs e)
        {
            if (ViewMode != CardViewMode.Thumbnail || e?.RenderData == null) return;

            // 节流处理
            if ((DateTime.Now - _lastThumbnailUpdate).TotalMilliseconds < 500) return;
            _lastThumbnailUpdate = DateTime.Now;

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var card = StationCards.FirstOrDefault(c => c.StationCode == e.StationId);
                if (card != null)
                {
                    var renderImg = _renderService.WrapImage(e.RenderData);
                    if (renderImg != null)
                    {
                        card.LatestThumbnail = _renderService.CreateThumbnail(renderImg);
                    }
                }
            }, DispatcherPriority.Background);
        }

        private void OnMonitorStation(object parameter)
        {
            if (SelectedCard == null) return;
            NavigationService.Current?.NavigateTo(PageType.StationMonitor, SelectedCard.StationCode);
        }
    }
}