using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Flow.Factories;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Recipe.DTOs;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vison.FlowEdit.Helpers;
using Grayson.Vison.FlowEdit.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace Grayson.Vison.FlowEdit.ViewModels
{
    /// <summary>
    /// 流程编辑器 ViewModel：负责流程树管理、Worker生命周期交互与UI命令响应
    /// </summary>
    public class FlowVm : ViewModelBase
    {
        #region 1. 业务数据模型与状态属性
        // 🌟 1. 引入当前配方实体对象
        private RecipeModel _currentRecipe = new RecipeModel
        {
            RecipeName = "新建配方",
            MainProcess = new FlowProcessModel { ProcessName = "主流程" }
        };

        public RecipeModel CurrentRecipe
        {
            get => _currentRecipe;
            set => Set(ref _currentRecipe, value);
        }

        // 🌟 2. RootProcess 绑定到 CurrentRecipe.MainProcess
        public FlowProcessModel RootProcess
        {
            get => CurrentRecipe?.MainProcess;
            set
            {
                if (CurrentRecipe != null)
                {
                    CurrentRecipe.MainProcess = value;
                    OnPropertyChanged(nameof(RootProcess));
                    OnPropertyChanged(nameof(CurrentRecipeInfo));
                }
            }
        }



        private FlowProcessModel _currentProcess;
        /// <summary>
        /// 当前画布正在编辑的流程层级
        /// </summary>
        public FlowProcessModel CurrentProcess
        {
            get => _currentProcess;
            set
            {
                if (Set(ref _currentProcess, value))
                {
                    OnCurrentProcessChanged();
                    OnPropertyChanged(nameof(CurrentRecipeInfo));
                }
            }
        }

        private FlowNodeBase _selectedNode;
        /// <summary>
        /// 当前选中的节点
        /// </summary>
        public FlowNodeBase SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (Set(ref _selectedNode, value))
                {
                    DeleteNodeCmd?.RaiseCanExecuteChanged();
                }
            }
        }

        private bool _useRemoteWorkerProcess = false;
        /// <summary>
        /// 是否采用 Remote Worker 独立进程运行 (IPC模式)
        /// </summary>
        public bool UseRemoteWorkerProcess
        {
            get => _useRemoteWorkerProcess;
            set
            {
                if (Set(ref _useRemoteWorkerProcess, value))
                {
                    InitWorkerClient();
                }
            }
        }

        private bool _showDataPorts = true;
        /// <summary>
        /// 是否显示数据端口
        /// </summary>
        public bool ShowDataPorts
        {
            get => _showDataPorts;
            set
            {
                if (Set(ref _showDataPorts, value))
                {
                    LogBus.Info("UI", value ? "已开启【数据端口】显示" : "已隐藏【数据端口】");
                }
            }
        }


        private bool _isDirty = false;
        /// <summary>
        /// 画布或流程拓扑是否发生变更（未同步到 Worker / 未保存）
        /// </summary>
        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                if (Set(ref _isDirty, value))
                {
                    OnPropertyChanged(nameof(CurrentRecipeInfo));
                }
            }
        }

        /// <summary>
        /// 顶部配方关键信息摘要（供 Header 绑定显示）
        /// </summary>
        //public string CurrentRecipeInfo => $"配方: {RootProcess?.ProcessName ?? "未定义"}{(IsDirty ? " *" : "")} | 工位: {CurrentStationDisplayName} | 当前层级: {CurrentProcess?.ProcessName} | 节点数: {CurrentProcess?.Nodes?.Count ?? 0}";
        public string CurrentRecipeInfo => $"配方: {RootProcess?.ProcessName ?? "未定义"}{(IsDirty ? " *" : "")} | 当前层级: {CurrentProcess?.ProcessName} | 节点数: {CurrentProcess?.Nodes?.Count ?? 0}";

        public ObservableCollection<SharedDataItem> WatchData { get; set; } = new ObservableCollection<SharedDataItem>();
        public ObservableCollection<string> ExecutionLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<PreflightIssueItem> PreflightIssues { get; } = new ObservableCollection<PreflightIssueItem>();
        public ObservableCollection<FlowProcessModel> Breadcrumbs { get; set; } = new ObservableCollection<FlowProcessModel>();
        public ObservableCollection<UnitMeta> ToolBox { get; set; } = new ObservableCollection<UnitMeta>();
        public ObservableCollection<StationOptionItem> AvailableStations { get; } = new ObservableCollection<StationOptionItem>();
        public ICollectionView ToolBoxGrouped { get; set; }
        public ImageDisplayVm ImageDisplayVm { get; set; }
        public ExecutionChain CurrentExecutionChain { get; private set; }

        private string _selectedStationId;
        private bool _isApplyingStationContext;
        private readonly Dictionary<string, StationConfigModel> _stationConfigLookup =
            new Dictionary<string, StationConfigModel>(StringComparer.OrdinalIgnoreCase);

        public string SelectedStationId
        {
            get => _selectedStationId;
            set
            {
                if (Set(ref _selectedStationId, value))
                {
                    OnPropertyChanged(nameof(CurrentStationDisplayName));
                    OnPropertyChanged(nameof(CurrentRecipeInfo));

                    if (!_isApplyingStationContext)
                    {
                        SwitchWorkerStationAsync();
                    }
                }
            }
        }

        public string CurrentStationDisplayName
        {
            get
            {
                var current = AvailableStations.FirstOrDefault(s => string.Equals(s.StationId, SelectedStationId, StringComparison.OrdinalIgnoreCase));
                if (current != null) return current.DisplayName;
                return string.IsNullOrWhiteSpace(SelectedStationId) ? "未选择工位" : SelectedStationId;
            }
        }

        #endregion

        #region 2. 服务代理与事件生命周期

        private const string DefaultEditorStationId = "FlowEditStation";

        private IWorkerClient _workerClient;
        private readonly HalconImageRenderService _renderService;
        private readonly RecipeManager _recipeManager;

        public event Action<FlowNodeBase> OnNodeExecuting;
        public event Action<FlowNodeBase> OnNodeExecuted;
        public event Action<FlowNodeBase, Exception> OnExecutionError;

        /// <summary>
        /// 宿主可注入统一配方保存委托（如 WpfUI 保存到 RecipeStorage）；
        /// 未注入时回退到编辑器内置“导出文件”保存模式。
        /// </summary>
        public Func<RecipeModel, bool> HostRecipeSaveHandler { get; set; }

        #endregion

        #region 3. UI 命令定义

        public RelayCommand DeleteNodeCmd { get; }
        public RelayCommand<ConnectionModel> DeleteConnectionCmd { get; }
        public ICommand SaveRecipeCmd { get; }
        public ICommand ImportRecipeCmd { get; }
        public ICommand ExportRecipeCmd { get; }
        public ICommand NavigateToProcessCmd { get; }
        public ICommand RunContinuousCmd { get; }
        public ICommand StepRunCmd { get; }
        public ICommand StepRunNodeCmd { get; }
        public ICommand PauseRunCmd { get; }
        public ICommand StopRunCmd { get; }
        public ICommand ResetCmd { get; }
        public ICommand AutoLayoutCmd { get; }
        public ICommand SaveCurrentPipelineAsRecipeCommand { get; }
        public ICommand ClearCanvasCommand { get; }
        public ICommand ToggleShowDataPortsCommand { get; }
        public ICommand OpenNodePropertyCommand { get; }
        public ICommand RunPreflightCmd { get; }
        public ICommand LocatePreflightIssueCmd { get; }

        #endregion

        #region 4. 构造函数与初始化

        public FlowVm()
        {
            // 1. 初始化日志与图像渲染
            InitLogBusSubscription();
            _renderService = new HalconImageRenderService();
            ImageDisplayVm = new ImageDisplayVm(_renderService);

            // 2. 插件加载已经在 App 启动时完成，这里不再重复执行
            // 原代码：new NodePluginLoader().LoadPlugins(pluginDir);
            // 新逻辑：插件在 App.xaml.cs 的 Application_Startup 中统一加载

            // 3. 初始化工具箱与流程层级
            InitFullToolBox();
            Breadcrumbs.Add(RootProcess);
            CurrentProcess = RootProcess;


            _recipeManager = new RecipeManager(new WpfDialogService());
            _recipeManager.LoadCompositeRecipeTemplates(ToolBox);

            // 4. 命令绑定（Worker 客户端在 LoadRecipe 加载真实配方后由 InitWorkerClient 初始化，避免与空配方产生竞争）
            RunContinuousCmd = new RelayCommand(async () => await StartWorkerAsync());
            StepRunCmd = new RelayCommand(async () => await TriggerWorkerOnceAsync());
            StepRunNodeCmd = new RelayCommand(async () => await StepRunNodeAsync());
            PauseRunCmd = new RelayCommand(async () => await StopWorkerAsync());
            StopRunCmd = new RelayCommand(async () => await StopWorkerAsync());
            ResetCmd = new RelayCommand(async () => await ResetWorkerAsync());

            DeleteNodeCmd = new RelayCommand(DeleteSelectedNode, () => SelectedNode != null);
            DeleteConnectionCmd = new RelayCommand<ConnectionModel>(DeleteConnection);

            SaveRecipeCmd = new RelayCommand(OnSaveRecipe);
            ImportRecipeCmd = new RelayCommand(ImportRecipe);
            ExportRecipeCmd = new RelayCommand(OnSaveRecipe);
            NavigateToProcessCmd = new RelayCommand<FlowProcessModel>(NavigateToProcess);
            AutoLayoutCmd = new RelayCommand(AutoLayout);
            SaveCurrentPipelineAsRecipeCommand = new RelayCommand(OnSaveCurrentPipelineAsRecipe);
            ClearCanvasCommand = new RelayCommand(() => ClearCanvas(false));
            ToggleShowDataPortsCommand = new RelayCommand(() => ShowDataPorts = !ShowDataPorts);
            OpenNodePropertyCommand = new RelayCommand<FlowNodeBase>(OnNodeDoubleClicked);
            RunPreflightCmd = new RelayCommand(() => EnsureValidExecutionChain(false));
            LocatePreflightIssueCmd = new RelayCommand<PreflightIssueItem>(LocatePreflightIssue);

            LogBus.Info("System", "FlowVm 初始化完成。");
        }

        #endregion

        #region 5. 校验与配方保存/导入

        private bool EnsureValidExecutionChain(bool showDialog = true)
        {
            if (CurrentProcess?.Nodes == null) return false;

            PreflightIssues.Clear();
            foreach (var n in CurrentProcess.Nodes)
            {
                n.HasError = false;
                n.ValidationMessage = null;
            }

            var missingInputIssues = CollectMissingInputIssues();
            foreach (var issue in missingInputIssues)
            {
                issue.Node.HasError = true;
                issue.Node.ValidationMessage = issue.Message;
                PreflightIssues.Add(new PreflightIssueItem
                {
                    NodeId = issue.Node.NodeId,
                    NodeName = issue.Node.DisplayName,
                    Message = issue.Message,
                    Severity = "Error"
                });
            }

            if (missingInputIssues.Count > 0)
            {
                var firstMsg = missingInputIssues[0].Message;
                LogBus.Error("FlowVm", $"流程预检失败: {firstMsg}");
                if (showDialog)
                {
                    MessageBox.Show($"流程存在错误无法运行：\n{firstMsg}", "预检错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return false;
            }

            var buildResult = ExecutionChain.BuildAndValidate(CurrentProcess);
            if (!buildResult.IsSuccess)
            {
                LogBus.Error("FlowVm", $"流程校验失败: {buildResult.ErrorMessage}");

                foreach (var invalidNode in buildResult.InvalidNodes)
                {
                    invalidNode.HasError = true;
                    invalidNode.ValidationMessage = buildResult.ErrorMessage;
                    PreflightIssues.Add(new PreflightIssueItem
                    {
                        NodeId = invalidNode.NodeId,
                        NodeName = invalidNode.DisplayName,
                        Message = buildResult.ErrorMessage,
                        Severity = "Error"
                    });
                }

                if (buildResult.TypeMismatchedConnections != null)
                {
                    foreach (var mismatch in buildResult.TypeMismatchedConnections)
                    {
                        if (mismatch?.TargetNode == null) continue;
                        mismatch.TargetNode.HasError = true;
                        mismatch.TargetNode.ValidationMessage = buildResult.ErrorMessage;
                        PreflightIssues.Add(new PreflightIssueItem
                        {
                            NodeId = mismatch.TargetNode.NodeId,
                            NodeName = mismatch.TargetNode.DisplayName,
                            Message = buildResult.ErrorMessage,
                            Severity = "Error"
                        });
                    }
                }

                if (showDialog)
                {
                    MessageBox.Show($"流程存在错误无法运行：\n{buildResult.ErrorMessage}", "拓扑校验错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return false;
            }

            CurrentExecutionChain = buildResult.Chain;
            LogBus.Info("FlowVm", "流程预检通过。");
            return true;
        }

        private List<PreflightPortIssue> CollectMissingInputIssues()
        {
            var issues = new List<PreflightPortIssue>();
            var connections = CurrentProcess?.Connections?.ToList() ?? new List<ConnectionModel>();

            foreach (var node in CurrentProcess.Nodes)
            {
                if (node?.InputPorts == null) continue;

                foreach (var inputPort in node.InputPorts.Where(p => p != null && p.PortType == PortType.In))
                {
                    bool hasIncoming = connections.Any(c => c.TargetNode == node && c.TargetPortId == inputPort.PortId);
                    if (!hasIncoming)
                    {
                        issues.Add(new PreflightPortIssue
                        {
                            Node = node,
                            PortName = inputPort.PortName,
                            Message = $"节点 [{node.DisplayName}] 输入端口 [{inputPort.PortName}] 未连接上游输出。"
                        });
                    }
                }
            }

            return issues;
        }

        private sealed class PreflightPortIssue
        {
            public FlowNodeBase Node { get; set; }
            public string PortName { get; set; }
            public string Message { get; set; }
        }

        private void LocatePreflightIssue(PreflightIssueItem issue)
        {
            if (issue == null || CurrentProcess?.Nodes == null) return;

            var node = CurrentProcess.Nodes.FirstOrDefault(n => string.Equals(n.NodeId, issue.NodeId, StringComparison.OrdinalIgnoreCase));
            if (node == null) return;

            SelectedNode = node;
            OnNodeExecuting?.Invoke(node);
        }

        public bool TryFocusNodeByDisplayName(string nodeName)
        {
            if (string.IsNullOrWhiteSpace(nodeName) || CurrentProcess?.Nodes == null) return false;

            var node = CurrentProcess.Nodes.FirstOrDefault(n => string.Equals(n.DisplayName, nodeName, StringComparison.OrdinalIgnoreCase));
            if (node == null) return false;

            SelectedNode = node;
            OnNodeExecuting?.Invoke(node);
            return true;
        }


        /// <summary>
        /// 【保存/导出配方】统一入口（基于 DTO 模式）
        /// </summary>
        private void OnSaveRecipe()
        {
            if (RootProcess == null)
            {
                LogBus.Warn("Recipe", "当前没有可保存/导出的配方！");
                return;
            }

            // 1. 保存前校验拓扑完整性
            if (!EnsureValidExecutionChain())
            {
                LogBus.Warn("Recipe", "流程拓扑校验未通过，保存已被终止。");
                return;
            }

            try
            {
                if (CurrentRecipe != null)
                {
                    CurrentRecipe.MainProcess = RootProcess;
                    CurrentRecipe.LastModifiedTime = DateTime.Now;
                }

                string recipeName = CurrentRecipe?.RecipeName ?? RootProcess.ProcessName;
                bool isSuccess;

                // 2. 优先走宿主注入的统一配方存储；未注入则回退为导出文件
                if (HostRecipeSaveHandler != null)
                {
                    isSuccess = HostRecipeSaveHandler(CurrentRecipe);
                    if (!isSuccess)
                    {
                        LogBus.Warn("Recipe", $"宿主保存配方 [{recipeName}] 失败。");
                    }
                }
                else
                {
                    isSuccess = _recipeManager.ExportRecipe(CurrentRecipe);
                }

                if (isSuccess)
                {
                    IsDirty = false;
                    LogBus.Info("Recipe", $"配方 [{recipeName}] 保存成功。");
                }
            }
            catch (Exception ex)
            {
                LogBus.Error("Recipe", $"保存/导出配方失败: {ex.Message}", ex);
                MessageBox.Show($"配方保存失败：{ex.Message}", "系统错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        /// <summary>
        /// 【打开/导入配方】（基于 DTO 加载并还原完整 RecipeModel VM）
        /// </summary>
        private void ImportRecipe()
        {
            var importedRecipe = _recipeManager.ImportRecipe();
            if (importedRecipe != null)
            {
                CurrentRecipe = importedRecipe;

                // 🌟 1. 导入完成后，递归重新绑定并计算主流程及所有嵌套子流程的连线坐标
                BindAndRefreshConnections(CurrentRecipe.MainProcess);
                if (CurrentRecipe.SubProcesses != null)
                {
                    foreach (var subProc in CurrentRecipe.SubProcesses.Values)
                    {
                        BindAndRefreshConnections(subProc);
                    }
                }

                // 2. 重置导航面包屑与当前画板
                Breadcrumbs.Clear();
                Breadcrumbs.Add(RootProcess);
                CurrentProcess = RootProcess;

                IsDirty = false;
                LogBus.Info("Recipe", $"配方 [{CurrentRecipe.RecipeName}] DTO 导入还原成功！");
            }
        }

        /// <summary>
        /// 🌟 递归绑定并刷新指定流程（及其子流程）的所有连线与位置监听事件
        /// </summary>
        private void BindAndRefreshConnections(FlowProcessModel process)
        {
            if (process == null) return;

            // 1. 刷新当前流程中的连线坐标并重新挂载位置监听
            RefreshProcessConnections(process);

            // 2. 递归刷新包含在当前流程中的复合节点（CompositeFlowNode）内部子流程
            if (process.Nodes != null)
            {
                foreach (var node in process.Nodes)
                {
                    if (node is CompositeFlowNode compositeNode && compositeNode.SubProcess != null)
                    {
                        BindAndRefreshConnections(compositeNode.SubProcess);
                    }
                }
            }
        }

        /// <summary>
        /// 重新计算指定流程内所有连线的端点坐标。
        /// </summary>
        public void RefreshProcessConnections(FlowProcessModel process)
        {
            if (process?.Connections == null) return;

            foreach (var conn in process.Connections)
            {
                conn.BindAndUpdate();
            }
        }

        private void OnSaveCurrentPipelineAsRecipe()
        {
            if (CurrentProcess == null) return;

            // 🌟 1. 保存前校验拓扑完整性（如果不正确则弹窗并终止放行）[cite: 1]
            if (!EnsureValidExecutionChain())
            {
                LogBus.Warn("Recipe", "当前流程拓扑或参数校验未通过，终止保存为复合模板！");
                return;
            }

            // 🌟 2. 弹窗提示输入子流程名称[cite: 1]
            string defaultName = CurrentProcess.ProcessName ?? "新复合流程";
            string recipeName = PromptDialog.Show("保存为 CompositeFlow 模板", "请输入子流程/模板名称：", defaultName);
            if (string.IsNullOrWhiteSpace(recipeName)) return;

            recipeName = recipeName.Trim();
            CurrentProcess.ProcessName = recipeName;

            try
            {
                // 🌟 3. 调用 RecipeManager 存盘至 CompositeFlow 目录并刷工具箱（只在此处进行存盘与刷工具箱）[cite: 6]
                _recipeManager.SavePipelineAsRecipe(CurrentProcess, recipeName, ToolBox);

                // 不再挂载到 CurrentRecipe.SubProcesses[cite: 7]

                MessageBox.Show($"流程 [{recipeName}] 校验通过，已成功保存至 CompositeFlow 目录并更新工具箱！",
                                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);

                IsDirty = true; // 标记画布变动[cite: 1]
            }
            catch (Exception ex)
            {
                LogBus.Error("Recipe", $"保存子流程模板失败: {ex.Message}", ex);
                MessageBox.Show($"保存失败：{ex.Message}", "系统错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region 6. Worker 客户端通信与运行控制

        private IStationHostRuntime _stationHostRuntime;

        private async void InitWorkerClient()
        {
            await RebindWorkerClientAsync(GetEffectiveStationId());
        }

        private async void SwitchWorkerStationAsync()
        {
            var stationId = GetEffectiveStationId();
            if (_workerClient != null && string.Equals(_workerClient.StationId, stationId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await RebindWorkerClientAsync(stationId);
        }

        private string GetEffectiveStationId()
        {
            if (!string.IsNullOrWhiteSpace(SelectedStationId))
            {
                return SelectedStationId;
            }

            var firstAvailable = AvailableStations.FirstOrDefault()?.StationId;
            return string.IsNullOrWhiteSpace(firstAvailable) ? DefaultEditorStationId : firstAvailable;
        }

        private async Task RebindWorkerClientAsync(string stationId)
        {
            try
            {
                DetachWorkerClient();

                _stationHostRuntime = App.StationHostRuntime;
                if (_stationHostRuntime == null)
                {
                    throw new InvalidOperationException(
                        "未找到可用的 StationHostRuntime。独立运行请在 App.OnStartup 初始化；嵌入 WpfUI 时请由宿主 App 注入。");
                }

                var stationConfig = ResolveStationConfig(stationId);
                var runtimeStationKey = ResolveRuntimeStationKey(stationId, stationConfig);
                var deviceMappings = ResolveEffectiveDeviceMappings(stationConfig);

                await _stationHostRuntime.InitializeAsync();
                _workerClient = await _stationHostRuntime.CreateStationWithRecipeAsync(runtimeStationKey, CurrentRecipe, deviceMappings);

                _workerClient.OnFrameRendered += WorkerClient_OnFrameRendered;
                _workerClient.OnNodeExecuting += Worker_OnNodeExecuting;
                _workerClient.OnNodeExecuted += Worker_OnNodeExecuted;
                _workerClient.OnExecutionError += Worker_OnExecutionError;
                _workerClient.OnExecutionCompleted += Worker_OnExecutionCompleted;

                if (CurrentProcess != null)
                {
                    await _workerClient.LoadRecipeAsync(CurrentProcess);
                }

                LogBus.Info("FlowVm", $"执行目标工位已切换为: {runtimeStationKey}");
            }
            catch (Exception ex)
            {
                LogBus.Error("FlowVm", $"初始化工位 Worker 失败: {ex.Message}", ex);
            }
        }

        private void DetachWorkerClient()
        {
            if (_workerClient == null) return;

            _workerClient.OnFrameRendered -= WorkerClient_OnFrameRendered;
            _workerClient.OnNodeExecuting -= Worker_OnNodeExecuting;
            _workerClient.OnNodeExecuted -= Worker_OnNodeExecuted;
            _workerClient.OnExecutionError -= Worker_OnExecutionError;
            _workerClient.OnExecutionCompleted -= Worker_OnExecutionCompleted;
            _workerClient.Dispose();
            _workerClient = null;
        }

        private void Worker_OnNodeExecuting(object sender, NodeEventArgs e)
        {
            if (e?.Node == null) return;

            // 🌟 使用 Dispatcher 强制切回 UI 主线程更新
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                e.Node.IsRunning = true;
                e.Node.HasError = false;

                // 🌟 1. 单步/连续运行到该节点时，自动切换 SelectedNode，让画布视觉焦点随之移动
                SelectedNode = e.Node;

                OnNodeExecuting?.Invoke(e.Node);
            });
        }

        private void Worker_OnNodeExecuted(object sender, NodeEventArgs e)
        {
            if (e?.Node == null) return;

            // 🌟 使用 Dispatcher 强制切回 UI 主线程更新
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                e.Node.IsRunning = false;
                e.Node.HasError = false;

                OnNodeExecuted?.Invoke(e.Node);
            });
        }

        private void Worker_OnExecutionError(object sender, NodeExecutionErrorEventArgs e)
        {
            if (e?.Node == null) return;

            // 🌟 使用 Dispatcher 强制切回 UI 主线程更新
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                e.Node.IsRunning = false;
                e.Node.HasError = true;

                OnExecutionError?.Invoke(e.Node, e.Exception);
            });
        }
        // 后台StatinWorker线程、进程发布的更新渲染事件
        private void WorkerClient_OnFrameRendered(object sender, ImageRenderEventArgs e)
        {
            if (e?.RenderData == null) return;

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
                        var node = CurrentProcess?.Nodes?.FirstOrDefault(n => n.NodeId == e.NodeId);
                        renderContext = new WpfImageRenderContext
                        {
                            NodeId = e.NodeId,
                            NodeName = node?.DisplayName ?? $"[{e.NodeName}]",
                            Image = renderImg,
                            Thumbnail = _renderService.CreateThumbnail(renderImg)
                        };
                    }
                    ImageDisplayVm.AddOrUpdateImageContext(renderContext);
                }
                catch (Exception ex)
                {
                    LogBus.Error("FlowVm", $"渲染图像更新异常: {ex.Message}", ex);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        // 🌟 流程结束后的回调函数（处理 UI 复位及副作用清理）
        private void Worker_OnExecutionCompleted(object sender, ChainCompletedEventArgs e)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                LogBus.Info("FlowVm", $"接收到流程结束通知: {e.Result}");

                // 2. 根据不同的结束结果做不同的业务副作用处理
                switch (e.Result)
                {
                    case ChainExecutionResult.Success:
                        // 例如：触发 OK 信号输出、自动化复位、日志收尾等
                        break;
                    case ChainExecutionResult.Failed:
                        // 例如：触发 NG 报警清理
                        break;
                    case ChainExecutionResult.Canceled:
                        // 例如：手动停止后的清空处理
                        break;
                }
            });
        }
        /// <summary>
        /// 准备进入新一轮执行：重置节点 UI 状态、清空临时观察数据
        /// </summary>
        private async Task PrepareForNewExecutionAsync()
        {
            // 1. 如果 Worker 处于 Faulted 或 Finished 状态，先重置 Worker/Engine 指针
            if (_workerClient != null &&
               (_workerClient.CurrentState == StationState.Faulted || _workerClient.CurrentState == StationState.Idle))
            {
                await ResetWorkerAsync();
            }

           
        }


        private async Task EnsureWorkerSyncedAsync()
        {
            // 如果流程被修改过，或者 Worker 当前未加载过配方，强行校验并重载
            if (IsDirty || CurrentExecutionChain == null)
            {
                if (!EnsureValidExecutionChain())
                {
                    throw new InvalidOperationException("拓扑校验不通过，无法更新执行器。");
                }

                if (_workerClient != null)
                {
                    await _workerClient.LoadRecipeAsync(CurrentProcess);
                    LogBus.Info("FlowVm", "流程已更新，重新构建 Worker 成功。");
                }

                IsDirty = false; // 同步完成后重置 Dirty 状态
            }
        }
        private async Task StartWorkerAsync()
        {
            if (_workerClient == null) return;

            try
            {
                //await PrepareForNewExecutionAsync();
                await EnsureWorkerSyncedAsync(); // 🌟 运行前自动检查并重新构建
                await _workerClient.StartAsync();
                await _workerClient.TriggerOnceAsync();
            }
            catch (Exception ex)
            {
                LogBus.Error("FlowVm", $"启动失败: {ex.Message}");
            }
        }
        // 🌟 单步运行（Step Run）命令：在当前流程上触发一次执行
        private async Task TriggerWorkerOnceAsync()
        {
            if (_workerClient == null) return;

            try
            {
                //await PrepareForNewExecutionAsync();
                await EnsureWorkerSyncedAsync(); // 🌟 触发前自动检查并重新构建
                await _workerClient.TriggerOnceAsync();
            }
            catch (Exception ex)
            {
                LogBus.Error("FlowVm", $"单步运行失败: {ex.Message}");
            }
        }

        // 🌟 运行当前选中节点（Step Run Node）命令
        private async Task StepRunNodeAsync()
        {
            if (_workerClient == null) return;
            await _workerClient.LoadRecipeAsync(CurrentProcess);

            // StationHostRuntime 已统一创建 EmbeddedWorkerClientProxy，直接调用接口即可
            await _workerClient.StepNodeAsync(SelectedNode);
        }

        private async Task StopWorkerAsync()
        {
            if (_workerClient != null) await _workerClient.StopAsync();
        }

        private async void OnCurrentProcessChanged()
        {
            if (_workerClient != null && CurrentProcess != null)
            {
                await _workerClient.LoadRecipeAsync(CurrentProcess);
                if (_workerClient.CurrentState == StationState.Faulted)
                {
                    LogBus.Warn("FlowVm", "切换的流程存在校验异常，已切入 Faulted 状态。");
                }
            }
        }
        private async Task ResetWorkerAsync()
        {
            try
            {
                // 0) 清空图像显示历史
                ImageDisplayVm.Clear();
                // 1) 停止当前可能在运行的 Worker
                if (_workerClient != null)
                {
                    await _workerClient.StopAsync();

                    // 重新加载配方，重置引擎内部的 Step/Node 指针
                    if (CurrentProcess != null)
                    {
                        await _workerClient.LoadRecipeAsync(CurrentProcess);
                    }
                }

                // 2) 清空 UI 节点上的状态标志 (IsRunning, HasError)
                if (CurrentProcess?.Nodes != null)
                {
                    foreach (var node in CurrentProcess.Nodes)
                    {
                        node.IsRunning = false;
                        node.HasError = false;
                    }

                    // 焦点自动切回第一个节点（如果有）
                   SelectedNode = null;
                }

                // 3) 可选：清空运行日志或监控数据
                ExecutionLogs.Clear();
                WatchData.Clear();

                LogBus.Info("FlowVm", "流程已成功复位至就绪状态。");
            }
            catch (Exception ex)
            {
                LogBus.Error("FlowVm", $"复位失败: {ex.Message}");
            }
        }
        #endregion

        #region 7. 画布节点与连线操作

        public void AddNodeFromTemplate(UnitMeta meta, Point2D position) => AddNodeFromMeta(meta, position);

        public void AddNodeFromMeta(UnitMeta meta, Point2D pos)
        {
            if (meta == null || CurrentProcess == null) return;

            FlowProcessModel subProcess = null;

            // 🌟 1. 在 ViewModel/UI 层处理 CompositeFlow 模板 JSON 的读取与反序列化（契约层不操作文件与 JSON）
            if (meta.Type == NodeType.CompositeFlow && !string.IsNullOrWhiteSpace(meta.Description))
            {
                string filePath = meta.Description; // 模板文件路径保存在 Description 中
                if (System.IO.File.Exists(filePath))
                {
                    try
                    {
                        string json = System.IO.File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                        var dto = Newtonsoft.Json.JsonConvert.DeserializeObject<ProcessDto>(json);
                        if (dto != null)
                        {
                            // 还原为 UI ViewModel 流程实体
                            subProcess = RecipeConverter.ToProcessModel(dto);

                            // 递归绑定并刷新子流程内的连线坐标与事件
                            BindAndRefreshConnections(subProcess);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogBus.Error("FlowVm", $"拖拽加载复合节点模板 JSON 失败 [{filePath}]: {ex.Message}", ex);
                    }
                }
            }

            // 🌟 2. 将解析好的 subProcess 传入 NodeFactory
            FlowNodeBase node = NodeFactory.CreateFromMeta(meta, pos, subProcess);

            if (node != null)
            {
                CurrentProcess.Nodes.Add(node);
                SelectedNode = node;
                OnPropertyChanged(nameof(CurrentRecipeInfo));
                IsDirty = true; // 🌟 标记变更
                LogBus.Info("Flow", $"新增节点: {node.DisplayName}");
            }
        }

        // ----------------------------------------------------------------------------------
        // 1. 优化【手绘/手动添加连线】：使用 RecipeConverter 的统一绑定逻辑
        // ----------------------------------------------------------------------------------
        public void AddConnection(FlowNodeBase source, NodePort sourcePort, FlowNodeBase target, NodePort targetPort)
        {
            if (source == null || target == null || source == target || sourcePort == null || targetPort == null) return;
            if (sourcePort.PortType == targetPort.PortType) return;

            // 🌟 使用 RecipeConverter 的工厂绑定方法，自动挂载 OnPositionChanged 监听
            var connection = RecipeConverter.CreateAndBindConnection(source, sourcePort, target, targetPort);

            CurrentProcess.Connections.Add(connection);
            IsDirty = true; // 标记变更
            LogBus.Info("Flow", $"连接成功: {source.DisplayName} -> {target.DisplayName}");
        }

        public void DeleteSelectedNode()
        {
            if (SelectedNode == null) return;
            for (int i = CurrentProcess.Connections.Count - 1; i >= 0; i--)
            {
                if (CurrentProcess.Connections[i].SourceNode == SelectedNode || CurrentProcess.Connections[i].TargetNode == SelectedNode)
                    CurrentProcess.Connections.RemoveAt(i);
            }
            CurrentProcess.Nodes.Remove(SelectedNode);
            SelectedNode = null;
            OnPropertyChanged(nameof(CurrentRecipeInfo));
            IsDirty = true; // 🌟 标记变更
            LogBus.Info("Flow", "删除选中节点");
        }

        public void DeleteConnection(ConnectionModel conn)
        {
            if (conn != null && CurrentProcess.Connections.Contains(conn))
            {
                CurrentProcess.Connections.Remove(conn);
                IsDirty = true; // 🌟 标记变更
                LogBus.Info("Flow", "连线已清除");
            }
        }

        public void ClearCanvas(bool showConfirm = true)
        {
            if (CurrentProcess == null || (CurrentProcess.Nodes.Count == 0 && CurrentProcess.Connections.Count == 0)) return;

            if (showConfirm && MessageBox.Show("确认清空画布上的所有节点和连线？", "提示", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            CurrentProcess.Connections.Clear();
            CurrentProcess.Nodes.Clear();
            SelectedNode = null;
            OnPropertyChanged(nameof(CurrentRecipeInfo));
            LogBus.Info("Flow", $"画布 [{CurrentProcess.ProcessName}] 已清空。");
        }

        public void OnNodeDoubleClicked(FlowNodeBase node)
        {
            if (node == null) return;

            if (node is CompositeFlowNode compositeNode)
            {
                DrillDownCompositeNode(compositeNode);
            }
            else
            {
                var win = new Grayson.Vison.FlowEdit.Views.NodePropertyWindow
                {
                    DataContext = node,
                    Owner = Application.Current?.MainWindow,
                    ParentFlowVm = this
                };
                win.ShowDialog();

            }

        }

        public void DrillDownCompositeNode(CompositeFlowNode compositeNode)
        {
            if (compositeNode?.SubProcess != null)
            {
                CurrentProcess = compositeNode.SubProcess;
                if (!Breadcrumbs.Contains(CurrentProcess)) Breadcrumbs.Add(CurrentProcess);
                SelectedNode = null;
                LogBus.Info("Flow", $"进入子流程: [{CurrentProcess.ProcessName}]");
            }
        }

        private void NavigateToProcess(FlowProcessModel targetProcess)
        {
            if (targetProcess == null) return;
            int index = Breadcrumbs.IndexOf(targetProcess);
            if (index >= 0)
            {
                while (Breadcrumbs.Count > index + 1) Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
                CurrentProcess = targetProcess;
                SelectedNode = null;
            }
        }

        public void AutoLayout()
        {
            if (CurrentProcess == null || CurrentProcess.Nodes == null || CurrentProcess.Nodes.Count == 0) return;

            var nodes = CurrentProcess.Nodes.ToList();
            var connections = CurrentProcess.Connections.ToList();

            // 1. 计算每个节点的入度
            var inDegree = nodes.ToDictionary(n => n, n => 0);
            foreach (var conn in connections)
            {
                if (conn.TargetNode != null && inDegree.ContainsKey(conn.TargetNode))
                {
                    inDegree[conn.TargetNode]++;
                }
            }

            // 2. 按入度进行拓扑分层 (Layering)
            var layers = new List<List<FlowNodeBase>>();
            var visited = new HashSet<FlowNodeBase>();

            var currentLayer = nodes.Where(n => inDegree[n] == 0).ToList();
            if (currentLayer.Count == 0 && nodes.Count > 0)
            {
                currentLayer.Add(nodes.First());
            }

            while (currentLayer.Count > 0)
            {
                layers.Add(currentLayer);
                foreach (var node in currentLayer) visited.Add(node);

                var nextLayer = new List<FlowNodeBase>();
                foreach (var node in currentLayer)
                {
                    var downstream = connections
                        .Where(c => c.SourceNode == node && c.TargetNode != null && !visited.Contains(c.TargetNode) && !nextLayer.Contains(c.TargetNode))
                        .Select(c => c.TargetNode);

                    nextLayer.AddRange(downstream);
                }

                currentLayer = nextLayer;
            }

            var unvisited = nodes.Where(n => !visited.Contains(n)).ToList();
            if (unvisited.Count > 0)
            {
                layers.Add(unvisited);
            }

            // 🌟 3. 修正：改为竖直方向拓扑重排 (Y轴代表层级深度，X轴代表同一层的水平并列)
            double startX = 100;
            double startY = 50;
            double layerSpacingY = 180; // 纵向层级间距 (上下节点距离)
            double nodeSpacingX = 220;  // 横向节点间距 (左右并列节点距离)

            for (int layerIdx = 0; layerIdx < layers.Count; layerIdx++)
            {
                var layer = layers[layerIdx];
                // 纵向 Y 随拓扑层级增加（从上往下）
                double currentY = startY + layerIdx * layerSpacingY;

                for (int nodeIdx = 0; nodeIdx < layer.Count; nodeIdx++)
                {
                    // 横向 X 随同层节点增加（从左往右）
                    double currentX = startX + nodeIdx * nodeSpacingX;

                    // 更新节点在 Canvas 上的坐标
                    layer[nodeIdx].PosX = currentX;
                    layer[nodeIdx].PosY = currentY;
                }
            }

            LogBus.Info("Flow", $"竖直方向拓扑自动排版完成，已对 {nodes.Count} 个节点重新布局。");
        }
        private void InitFullToolBox()
        {
            ToolBox.Clear();
            var metas = NodeFactory.GenerateToolboxMetas();

            // ========= 业务流程分组权重定义：工业视觉标准业务流顺序 =========
            // key: Category枚举，value:排序权重，数字越小越靠前
            var categoryOrderDict = new Dictionary<NodeCategory, int>()
            {
                {NodeCategory.ImageInput, 10},        // 图像输入（相机采集、读图）
                {NodeCategory.ImagePreprocess, 20},   // 图像预处理
                {NodeCategory.CalibrationLocation, 30}, // 标定、模板匹配、位置补正
                {NodeCategory.Identification, 40},    // 识别检测(OCR、条码、AI推理)
                {NodeCategory.FlowControl, 50},       // 流程控制(if、循环、延时)
                {NodeCategory.DeviceIO, 60},          // PLC、光源等外设IO通信
                {NodeCategory.DataStorage, 70}        // 存图、存数据、MES上报
            };

            // 排序：先按分组权重；同分组内部可以再按Type或者DisplayName做次级排序
            var sortedMetas = metas
                .OrderBy(m => categoryOrderDict.ContainsKey(m.Category) ? categoryOrderDict[m.Category] : 999)
                .ThenBy(m => m.Type)  // 同一分组内部节点按Type顺序排布，你也可以改成 ThenBy(m=>m.DisplayName)
                .ToList();

            //Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(sortedMetas, Newtonsoft.Json.Formatting.Indented));

            foreach (var meta in sortedMetas)
            {
                meta.CategoryName = GetCategoryDisplayName(meta.Category);
                ToolBox.Add(meta);
            }

            ToolBoxGrouped = CollectionViewSource.GetDefaultView(ToolBox);
            ToolBoxGrouped.GroupDescriptions.Clear();
            ToolBoxGrouped.GroupDescriptions.Add(new PropertyGroupDescription("CategoryName"));
        }

        private string GetCategoryDisplayName(NodeCategory category)
        {
            var info = NodeMetaRegistry.Get(category);
            return info != null ? $"{info.Emoji} {info.ShortName}" : "其他节点";
        }

        #endregion

        #region 8. 系统日志订阅

        private void InitLogBusSubscription()
        {
            const int MAX_UI_LOG_COUNT = 300;
            LogBus.OnLogProduced += entry =>
            {
                string formattedMsg = $"[{entry.Timestamp:HH:mm:ss}] [{entry.Category}] {entry.Message}";
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    ExecutionLogs.Insert(0, formattedMsg);
                    while (ExecutionLogs.Count > MAX_UI_LOG_COUNT)
                    {
                        ExecutionLogs.RemoveAt(ExecutionLogs.Count - 1);
                    }
                });
            };
        }

        #endregion
        #region 9🌟 跨界面/外部对接接口 (供 View 界面调用)

        public void ConfigureStationContext(IEnumerable<StationConfigModel> stations, string preferredStationId = null)
        {
            var normalizedStations = (stations ?? Enumerable.Empty<StationConfigModel>())
                .Where(station => station != null && !string.IsNullOrWhiteSpace(station.StationId))
                .GroupBy(station => station.StationId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            _stationConfigLookup.Clear();
            foreach (var station in normalizedStations)
            {
                _stationConfigLookup[station.StationId] = station;
            }

            var stationItems = normalizedStations
                .Select(station => new StationOptionItem
                {
                    StationId = station.StationId,
                    DisplayName = BuildStationDisplayName(station),
                    IsEnabled = station.IsEnabled
                })
                .OrderBy(item => item.DisplayName)
                .ToList();

            _isApplyingStationContext = true;
            try
            {
                AvailableStations.Clear();
                foreach (var item in stationItems)
                {
                    AvailableStations.Add(item);
                }

                var targetStationId = !string.IsNullOrWhiteSpace(preferredStationId)
                    ? preferredStationId
                    : AvailableStations.FirstOrDefault()?.StationId;

                SelectedStationId = targetStationId;
            }
            finally
            {
                _isApplyingStationContext = false;
            }

            OnPropertyChanged(nameof(CurrentStationDisplayName));
            OnPropertyChanged(nameof(CurrentRecipeInfo));
        }

        private StationConfigModel ResolveStationConfig(string stationId)
        {
            if (string.IsNullOrWhiteSpace(stationId)) return null;
            _stationConfigLookup.TryGetValue(stationId, out var stationConfig);
            return stationConfig;
        }

        private static string ResolveRuntimeStationKey(string stationId, StationConfigModel stationConfig)
        {
            if (!string.IsNullOrWhiteSpace(stationConfig?.StationCode))
            {
                return stationConfig.StationCode;
            }

            return stationId;
        }

        private IEnumerable<RecipeDeviceMappingModel> ResolveEffectiveDeviceMappings(StationConfigModel stationConfig)
        {
            var stationMappings = stationConfig?.DeviceMappings?
                .Where(mapping => mapping != null
                    && !string.IsNullOrWhiteSpace(mapping.MappedDeviceId)
                    && (!string.IsNullOrWhiteSpace(mapping.LogicalDeviceId) || !string.IsNullOrWhiteSpace(mapping.LogicalDeviceName)))
                .ToList();

            if (stationMappings?.Count > 0)
            {
                return stationMappings;
            }

            return CurrentRecipe?.LogicalDevices;
        }

        private static string BuildStationDisplayName(StationConfigModel station)
        {
            var core = !string.IsNullOrWhiteSpace(station.StationCode)
                ? station.StationCode
                : station.StationId;

            if (!string.IsNullOrWhiteSpace(station.StationName))
            {
                core += $" - {station.StationName}";
            }

            if (!station.IsEnabled)
            {
                core += " (未启用)";
            }

            return core;
        }

        /// <summary>
        /// 外部加载配方实体统一入口
        /// </summary>
        /// <param name="recipe">外部传入的 RecipeModel 对象</param>
        public void LoadRecipe(RecipeModel recipe)
        {
            if (recipe == null) return;

            // 1. 更新当前配方引用
            CurrentRecipe = recipe;

            // 2. 防空保护：确保主流程实体存在
            if (CurrentRecipe.MainProcess == null)
            {
                CurrentRecipe.MainProcess = new FlowProcessModel
                {
                    ProcessName = string.IsNullOrWhiteSpace(recipe.RecipeName) ? "主流程" : recipe.RecipeName
                };
            }

            // 3. 递归重构主流程及所有子流程的连线坐标与位置事件监听
            BindAndRefreshConnections(CurrentRecipe.MainProcess);
            if (CurrentRecipe.SubProcesses != null)
            {
                foreach (var subProc in CurrentRecipe.SubProcesses.Values)
                {
                    BindAndRefreshConnections(subProc);
                }
            }

            // 4. 重置导航面包屑与当前编辑画布
            Breadcrumbs.Clear();
            Breadcrumbs.Add(RootProcess);
            CurrentProcess = RootProcess;

            // 5. 重新载入 Worker 客户端
            InitWorkerClient();

            // 6. 重置脏标记
            IsDirty = false;

            LogBus.Info("Recipe", $"[FlowVm] 成功加载外部配方: [{CurrentRecipe.RecipeName}]");
        }

        /// <summary>
        /// 外部导出/同步当前编辑后的配方实体
        /// </summary>
        /// <returns>最新同步后的 RecipeModel</returns>
        public RecipeModel ExportCurrentRecipe()
        {
            // 在退出或切换前同步当前编辑的流程数据
            if (CurrentRecipe != null && RootProcess != null)
            {
                CurrentRecipe.MainProcess = RootProcess;
                CurrentRecipe.LastModifiedTime = DateTime.Now;
            }
            return CurrentRecipe;
        }

        public sealed class StationOptionItem
        {
            public string StationId { get; set; }
            public string DisplayName { get; set; }
            public bool IsEnabled { get; set; }
        }

        public sealed class PreflightIssueItem
        {
            public string NodeId { get; set; }
            public string NodeName { get; set; }
            public string Message { get; set; }
            public string Severity { get; set; }
        }

        #endregion
    }
}
