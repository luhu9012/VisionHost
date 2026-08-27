// Grayson.Vision.WpfUI.ViewModel/StationMonitorViewModel.cs
using Grayson.Vision.Contracts.Station.WorkOrderTracking;
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
        private readonly Grayson.Vision.Contracts.Recipe.Services.IRecipeStorageService _recipeStorage;
        private IWorkerClient _activeClient;
        private readonly HalconImageRenderService _renderService;

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

            AvailableStations = new ObservableCollection<string>();
            Logs = new ObservableCollection<StationLogEntry>();
            Results = new ObservableCollection<StationResultEntry>();

            StartCommand = new RelayCommand(async _ => await StartAsync(), _ => ActiveClient != null && (State == "Idle" || State == "Stopped"));
            StopCommand = new RelayCommand(async _ => await StopAsync(), _ => ActiveClient != null && (State == "Running" || State == "Paused"));
            PauseCommand = new RelayCommand(async _ => await PauseAsync(), _ => ActiveClient != null && State == "Running");
            ResumeCommand = new RelayCommand(async _ => await ResumeAsync(), _ => ActiveClient != null && State == "Paused");
            TriggerOnceCommand = new RelayCommand(async _ => await TriggerOnceAsync(), _ => ActiveClient != null && (State == "Idle" || State == "Running" || State == "Paused"));
            ResetCommand = new RelayCommand(async _ => await SoftResetAsync(), _ => ActiveClient != null);
            WorkOrderResetCommand = new RelayCommand(async _ => await WorkOrderResetAsync(), _ => ActiveClient != null);
            HardwareResetCommand = new RelayCommand(async _ => await HardwareResetAsync(), _ => ActiveClient != null);
            EmergencyStopCommand = new RelayCommand(async _ => await EmergencyStopAsync(), _ => ActiveClient != null && (State == "Running" || State == "Paused" || State == "Idle"));
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
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(StateBrushKey));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>
        /// 状态中文文案（状态机中文映射），供 Banner/卡片等展示层绑定；逻辑判断仍使用 State 英文字符串。
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

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand ResumeCommand { get; }
        public ICommand TriggerOnceCommand { get; }
        /// <summary>软复位（ResetCommand 指向软复位）</summary>
        public ICommand ResetCommand { get; }
        /// <summary>工单级复位</summary>
        public ICommand WorkOrderResetCommand { get; }
        /// <summary>硬件全复位（断连恢复）</summary>
        public ICommand HardwareResetCommand { get; }
        /// <summary>急停</summary>
        public ICommand EmergencyStopCommand { get; }
        public ICommand ClearLogsCommand { get; }

        #region INavigationAware 参数响应
        public void OnNavigatedTo(object parameter)
        {
            try
            {
                if (parameter is string stationCode && !string.IsNullOrEmpty(stationCode))
                {
                    _ = InitializeWithStationAsync(stationCode);
                }
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"导航初始化失败: {ex.Message}");
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
            var recipe = !string.IsNullOrEmpty(stationConfig?.BoundRecipeId)
                ? _recipeStorage.LoadRecipe(stationConfig.BoundRecipeId)
                : null;
            CurrentRecipeName = recipe?.RecipeName ?? stationConfig?.BoundRecipeName;
            CurrentRecipeApprovalText = recipe == null
                ? "未绑定配方"
                : $"{ApprovalStatusToText(recipe.ApprovalStatus)} · {recipe.Version}";

            // 加载触发源摘要（从工位配置读取）
            RefreshTriggerInfo(stationConfig?.TriggerSource);

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
            try
            {
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

                // 刷新触发源节拍统计（执行完成后统计会有新数据）
                RefreshTriggerStats();
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