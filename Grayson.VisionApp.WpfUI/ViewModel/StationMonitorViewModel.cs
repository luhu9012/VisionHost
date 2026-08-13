// Grayson.Vision.WpfUI.ViewModel/StationMonitorViewModel.cs
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Core.Client;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

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
        private IWorkerClient _activeClient;
        private readonly HalconImageRenderService _renderService;

        private int _totalCount;
        public int TotalCount { get => _totalCount; set { if (Set(ref _totalCount, value)) OnPropertyChanged(nameof(YieldText)); } }

        private int _okCount;
        public int OkCount { get => _okCount; set { if (Set(ref _okCount, value)) OnPropertyChanged(nameof(YieldText)); } }

        private int _ngCount;
        public int NgCount { get => _ngCount; set { if (Set(ref _ngCount, value)) OnPropertyChanged(nameof(YieldText)); } }

        private double _cycleTimeMs;
        /// <summary>
        /// 最近一次检测链总耗时 (ms)
        /// </summary>
        public double CycleTimeMs { get => _cycleTimeMs; set => Set(ref _cycleTimeMs, value); }

        private string _lastResult = "--";
        /// <summary>
        /// 最近一次判定结果 (OK / NG)
        /// </summary>
        public string LastResult { get => _lastResult; set => Set(ref _lastResult, value); }

        public string YieldText
        {
            get
            {
                if (TotalCount <= 0) return "--";
                double yield = (double)OkCount / TotalCount * 100;
                return $"{yield:F1}%";
            }
        }

        public StationMonitorViewModel(StationRuntimeManager runtimeManager = null, StationConfigService configService = null)
        {
            _runtimeManager = runtimeManager ?? new StationRuntimeManager();
            _configService = configService ?? new StationConfigService();

            // 1. 初始化图像展示 ViewModel
            _renderService = new HalconImageRenderService();
            ImageDisplayVm = new ImageDisplayVm(_renderService);

            AvailableStations = new ObservableCollection<string>();
            Logs = new ObservableCollection<StationLogEntry>();
            Results = new ObservableCollection<StationResultEntry>();

            StartCommand = new RelayCommand(async _ => await StartAsync(), _ => ActiveClient != null && State != "Running");
            StopCommand = new RelayCommand(async _ => await StopAsync(), _ => ActiveClient != null);
            TriggerOnceCommand = new RelayCommand(async _ => await TriggerOnceAsync(), _ => ActiveClient != null);
            ResetCommand = new RelayCommand(async _ => await ResetAsync(), _ => ActiveClient != null && State == "Faulted");
            ClearLogsCommand = new RelayCommand(_ => Logs.Clear());

            LoadAvailableStations();
        }

        /// <summary>
        /// 图像显示 VM，用于直接绑定 View 层的 Halcon 空间
        /// </summary>
        public ImageDisplayVm ImageDisplayVm { get; }

        public ObservableCollection<string> AvailableStations { get; set; }

        private string _selectedStationCode;
        public string SelectedStationCode
        {
            get => _selectedStationCode;
            set
            {
                if (Set(ref _selectedStationCode, value))
                {
                    _ = ConnectToStationAsync(value);
                }
            }
        }

        public IWorkerClient ActiveClient
        {
            get => _activeClient;
            private set
            {
                if (Set(ref _activeClient, value))
                {
                    CommandManager.InvalidateRequerySuggested();
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
                    OnPropertyChanged(nameof(StateBrushKey));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

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

        private string _currentRecipeName;
        public string CurrentRecipeName
        {
            get => _currentRecipeName;
            set => Set(ref _currentRecipeName, value);
        }

        public ObservableCollection<StationLogEntry> Logs { get; set; }
        public ObservableCollection<StationResultEntry> Results { get; set; }

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand TriggerOnceCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand ClearLogsCommand { get; }

        #region INavigationAware 参数响应
        public void OnNavigatedTo(object parameter)
        {
            if (parameter is string stationCode && !string.IsNullOrEmpty(stationCode))
            {
                _ = InitializeWithStationAsync(stationCode);
            }
        }

        public void OnNavigatedFrom() { }
        #endregion

        private void LoadAvailableStations()
        {
            AvailableStations.Clear();
            var lines = _configService.LoadAllLines();
            foreach (var station in lines.SelectMany(l => l.Stations))
            {
                AvailableStations.Add(station.StationCode);
            }

            if (AvailableStations.Any() && string.IsNullOrEmpty(SelectedStationCode))
            {
                SelectedStationCode = AvailableStations.First();
            }
        }

        public async Task InitializeWithStationAsync(string stationCode)
        {
            if (!AvailableStations.Contains(stationCode))
            {
                AvailableStations.Add(stationCode);
            }
            SelectedStationCode = stationCode;
            await Task.CompletedTask;
        }

        private async Task ConnectToStationAsync(string stationCode)
        {
            if (string.IsNullOrEmpty(stationCode)) return;

            DetachClientEvents();

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

            ActiveClient = client;
            AttachClientEvents(client);

            State = client.CurrentState.ToString();

            var stationConfig = _configService.LoadAllLines()
                .SelectMany(l => l.Stations)
                .FirstOrDefault(s => s.StationCode == stationCode);
            CurrentRecipeName = stationConfig?.BoundRecipe?.RecipeName;

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
            if (_activeClient == null) return;

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
            try { await ActiveClient.StartAsync(); }
            catch (Exception ex) { AddLog("ERROR", $"启动失败: {ex.Message}"); }
        }

        private async Task StopAsync()
        {
            if (ActiveClient == null) return;
            try { await ActiveClient.StopAsync(); }
            catch (Exception ex) { AddLog("ERROR", $"停止失败: {ex.Message}"); }
        }

        private async Task TriggerOnceAsync()
        {
            if (ActiveClient == null) return;
            try { await ActiveClient.TriggerOnceAsync(); }
            catch (Exception ex) { AddLog("ERROR", $"单次触发失败: {ex.Message}"); }
        }

        private async Task ResetAsync()
        {
            if (ActiveClient == null) return;
            try
            {
                await ActiveClient.StopAsync();
                var config = _configService.LoadAllLines()
                    .SelectMany(l => l.Stations)
                    .FirstOrDefault(s => s.StationCode == SelectedStationCode);
                if (config?.BoundRecipe?.MainProcess != null)
                {
                    await ActiveClient.LoadRecipeAsync(config.BoundRecipe.MainProcess);
                }
                State = ActiveClient.CurrentState.ToString();
                AddLog("INFO", "工位已复位");
            }
            catch (Exception ex) { AddLog("ERROR", $"复位失败: {ex.Message}"); }
        }

        private void Client_OnStateChanged(object sender, StationState e)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                State = e.ToString();
                AddLog("INFO", $"状态变更: {e}");
            }));
        }

        // 2. 帧渲染广播接入：将图像帧推送到 ImageDisplayVm 渲染展示
        private void Client_OnFrameRendered(object sender, ImageRenderEventArgs e)
        {
            if (e?.RenderData == null) return;

            // 🌟 全量实时图像渲染更新（UI 低优先级分发，保证流体帧率）
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var renderImg = _renderService.WrapImage(e.RenderData);
                    if (renderImg == null) return;

                    WpfImageRenderContext renderContext;
                    renderContext = ImageDisplayVm.ImageHistoryList.FirstOrDefault(x => x.NodeId == e.NodeId);

                    if (renderContext != null)
                    {
                        renderContext.Image = renderImg;
                        renderContext.Thumbnail = _renderService.CreateThumbnail(renderImg);
                    }
                    else
                    {
                        renderContext = new WpfImageRenderContext
                        {
                            NodeId = e.NodeId,
                            NodeName = string.IsNullOrEmpty(e.NodeName) ? $"[{e.NodeId}]" : e.NodeName,
                            Image = renderImg,
                            Thumbnail = _renderService.CreateThumbnail(renderImg)
                        };
                    }

                    ImageDisplayVm.AddOrUpdateImageContext(renderContext);
                }
                catch (Exception ex)
                {
                    AddLog("ERROR", $"图像渲染通道处理失败: {ex.Message}");
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        // 🌟 在 Client_OnExecutionCompleted 中计算 Cycle Time 并更新良率
        private void Client_OnExecutionCompleted(object sender, ChainCompletedEventArgs e)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                var isOk = e.Result == ChainExecutionResult.Success;
                TotalCount++;
                if (isOk) OkCount++; else NgCount++;

                LastResult = isOk ? "OK" : "NG";
                CycleTimeMs = e.ExecutionTimeMs; // 拿到链完成耗时

                Results.Insert(0, new StationResultEntry
                {
                    BatchId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Result = LastResult,
                    Message = isOk ? $"执行成功 (耗时: {e.ExecutionTimeMs:F1}ms)" : $"执行异常 (耗时: {e.ExecutionTimeMs:F1}ms)"
                });

                AddLog(isOk ? "INFO" : "ERROR", $"执行链结束，判定: {LastResult}，耗时: {e.ExecutionTimeMs:F1}ms");
            }));
        }

        private void Client_OnNodeExecuting(object sender, NodeEventArgs e)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                AddLog("DEBUG", $"开始执行节点: {e.Node?.DisplayName ?? e.Node?.NodeId}");
            }));
        }

        private void Client_OnNodeExecuted(object sender, NodeEventArgs e)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                AddLog("DEBUG", $"节点执行完成: {e.Node?.DisplayName ?? e.Node?.NodeId}");
            }));
        }

        private void Client_OnExecutionError(object sender, NodeExecutionErrorEventArgs e)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
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
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                AddLog("INFO", e);
            }));
        }

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