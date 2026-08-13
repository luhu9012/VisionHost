using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Core.Client;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using System;
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

        public string StateText => string.IsNullOrEmpty(State) ? "未连接" : State;

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



        public LineOverviewViewModel(StationRuntimeManager runtimeManager = null, StationConfigService configService = null)
        {
            _runtimeManager = runtimeManager ?? new StationRuntimeManager();
            _configService = configService ?? new StationConfigService();
            _renderService = new HalconImageRenderService();

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
            set => Set(ref _viewMode, value);
        }

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
                        CurrentRecipeName = stationConfig.BoundRecipe?.RecipeName
                    };
                    StationCards.Add(card);
                }

                // 获取并挂载事件驱动
                var client = _runtimeManager.GetClient(stationConfig.StationCode);
                if (client != null)
                {
                    card.IsConnected = client.IsConnected;
                    card.State = client.CurrentState.ToString();

                    // 重新挂载事件通知（防重挂）
                    client.OnStateChanged -= Client_OnStateChanged;
                    client.OnStateChanged += Client_OnStateChanged;

                    client.OnFrameRendered -= Client_OnFrameRendered;
                    client.OnFrameRendered += Client_OnFrameRendered;
                }
                else
                {
                    card.IsConnected = false;
                    card.State = "NotConnected";
                }
            }
            await Task.CompletedTask;
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