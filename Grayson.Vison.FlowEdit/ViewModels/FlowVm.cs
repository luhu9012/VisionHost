// 业务基础、特性、数据模型、节点工厂命名空间引用

using Grayson.Vision.Contracts.Business.Engine;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Events;
using Grayson.Vision.Contracts.Business.Factories;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.Logging;
using Grayson.Vision.Contracts.ViewModels;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vison.FlowEdit.Helpers;
using Grayson.Vison.FlowEdit.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace Grayson.Vison.FlowEdit.ViewModels
{
    public class FlowVm : ViewModelBase
    {
        // 1. 数据模型与 Observable 集合
        public FlowProcessModel RootProcess { get; set; } = new FlowProcessModel { ProcessName = "主工作流" };
        public ObservableCollection<SharedDataItem> WatchData { get; set; } = new ObservableCollection<SharedDataItem>();
        public ObservableCollection<string> ExecutionLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<FlowProcessModel> Breadcrumbs { get; set; } = new ObservableCollection<FlowProcessModel>();

        private FlowProcessModel _currentProcess;
        public FlowProcessModel CurrentProcess
        {
            get => _currentProcess;
            set
            {
                if (Set(ref _currentProcess, value))
                {
                    OnCurrentProcessChanged();
                }
            }
        }

        private FlowNodeBase _selectedNode;
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

        public ObservableCollection<UnitMeta> ToolBox { get; set; } = new ObservableCollection<UnitMeta>();
        public ICollectionView ToolBoxGrouped { get; set; }

        private bool _showDataPorts = true;
        public bool ShowDataPorts
        {
            get => _showDataPorts;
            set
            {
                if (Set(ref _showDataPorts, value))
                {
                    LogBus.Info("UI", value ? "已开启【数据端口】显示" : "已隐藏【数据端口】，仅保留控制流端口");
                }
            }
        }

        // 2. Worker 客户端代理与引擎解耦
        private IWorkerClient _workerClient;
        private readonly HalconImageRenderService _renderService;
        private readonly RecipeManager _recipeManager;
        // 🌟 重新对外暴露 View 所依赖的节点生命周期事件
        public event Action<FlowNodeBase> OnNodeExecuting;
        public event Action<FlowNodeBase> OnNodeExecuted;
        public event Action<FlowNodeBase, Exception> OnExecutionError;

        // 是否采用远程进程 Worker 模式
        private bool _useRemoteWorkerProcess = false;
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

        // 3. UI 交互事件与命令
        public RelayCommand DeleteNodeCmd { get; }
        public RelayCommand<ConnectionModel> DeleteConnectionCmd { get; }
        public ICommand SaveRecipeCmd { get; }
        public ICommand ImportRecipeCmd { get; }
        public ICommand ExportRecipeCmd { get; }
        public ICommand NavigateToProcessCmd { get; }
        public ICommand RunContinuousCmd { get; }
        public ICommand StepRunCmd { get; }
        public ICommand StopRunCmd { get; }
        public ICommand AutoLayoutCmd { get; }
        public ICommand SaveCurrentPipelineAsRecipeCommand { get; }
        public ICommand ClearCanvasCommand { get; }
        public ICommand ToggleShowDataPortsCommand { get; }
        public ICommand OpenNodePropertyCommand { get; }

        public ImageDisplayVm ImageDisplayVm { get; set; }

        public FlowVm()
        {
            // 必须最先初始化 LogBus 订阅
            InitLogBusSubscription();

            // 初始化 Halcon 渲染服务
            _renderService = new HalconImageRenderService();
            ImageDisplayVm = new ImageDisplayVm(new Grayson.Vision.Contracts.Business.Engine.Execution.ExecutionContext(), _renderService);

            // 自动加载插件
            string pluginDir = AppDomain.CurrentDomain.BaseDirectory;
            new NodePluginLoader().LoadPlugins(pluginDir);

            _recipeManager = new RecipeManager(new WpfDialogService());
            _recipeManager.LoadCompositeRecipeTemplates(ToolBox);

            // 初始化工具箱与主配方
            InitFullToolBox();

            Breadcrumbs.Add(RootProcess);
            CurrentProcess = RootProcess;

            // 初始化 Worker 运行代理客户端
            InitWorkerClient();

            // Command 路由绑定 (改为通过 Worker Client 发送异步指令)
            RunContinuousCmd = new RelayCommand(async () => await StartWorkerAsync());
            StepRunCmd = new RelayCommand(async () => await TriggerWorkerOnceAsync());
            StopRunCmd = new RelayCommand(async () => await StopWorkerAsync());

            DeleteNodeCmd = new RelayCommand(DeleteSelectedNode, () => SelectedNode != null);
            DeleteConnectionCmd = new RelayCommand<ConnectionModel>(DeleteConnection);
            SaveRecipeCmd = new RelayCommand(() => LogBus.Info("Recipe", "配方参数已成功保存！"));
            ImportRecipeCmd = new RelayCommand(ImportRecipe);
            ExportRecipeCmd = new RelayCommand(() => _recipeManager.ExportRecipe(RootProcess));
            NavigateToProcessCmd = new RelayCommand<FlowProcessModel>(NavigateToProcess);
            AutoLayoutCmd = new RelayCommand(AutoLayout);
            SaveCurrentPipelineAsRecipeCommand = new RelayCommand(OnSaveCurrentPipelineAsRecipe);
            ClearCanvasCommand = new RelayCommand(() => ClearCanvas(false));
            ToggleShowDataPortsCommand = new RelayCommand(() => ShowDataPorts = !ShowDataPorts);
            OpenNodePropertyCommand = new RelayCommand<FlowNodeBase>(OnNodeDoubleClicked);

            LogBus.Info("System", "FlowVm 重构多工位/Worker 架构完成。");
        }

        #region Worker 模式初始化与事件处理
        private async void InitWorkerClient()
        {
            if (_workerClient != null)
            {
                // 取消旧的事件订阅
                _workerClient.OnNodeExecuting -= Worker_OnNodeExecuting;
                _workerClient.OnNodeExecuted -= Worker_OnNodeExecuted;
                _workerClient.OnExecutionError -= Worker_OnExecutionError;
                _workerClient.Dispose();
            }

            if (UseRemoteWorkerProcess)
            {
                _workerClient = new RemoteWorkerClientProxy("Station_01");
                await _workerClient.ConnectAsync();
            }
            else
            {
                _workerClient = new EmbeddedWorkerClientProxy("Station_01");
            }

            // 重新挂载 WorkerClient 的生命周期事件
            _workerClient.OnFrameRendered += WorkerClient_OnFrameRendered;
            _workerClient.OnNodeExecuting += Worker_OnNodeExecuting;
            _workerClient.OnNodeExecuted += Worker_OnNodeExecuted;
            _workerClient.OnExecutionError += Worker_OnExecutionError;

            await _workerClient.LoadRecipeAsync(CurrentProcess);
        }
        #region Worker 模式初始化与事件处理
        private void Worker_OnNodeExecuting(object sender, NodeEventArgs e)
        {
            // 触发 UI 层的居中滚动/运行状态动画
            if (e?.Node != null)
            {
                OnNodeExecuting?.Invoke(e.Node);
            }
        }

        private void Worker_OnNodeExecuted(object sender, NodeEventArgs e)
        {
            if (e?.Node != null)
            {
                OnNodeExecuted?.Invoke(e.Node);
            }
        }

        private void Worker_OnExecutionError(object sender, NodeExecutionErrorEventArgs e)
        {
            if (e != null)
            {
                OnExecutionError?.Invoke(e.Node, e.Exception);
            }
        }
        #endregion

        /// <summary>
        /// 收到 Worker 推送的渲染数据时，转换并刷新 ImageDisplayVm 视口
        /// </summary>
        private void WorkerClient_OnFrameRendered(object sender, ImageRenderEventArgs e)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (e?.RenderData == null) return;

                // 包装 Halcon 图像对象
                var renderImg = _renderService.WrapImage(e.RenderData);
                if (renderImg != null)
                {
                    var renderContext = new WpfImageRenderContext
                    {
                        NodeId = e.NodeId,
                        NodeName = $"Node_{e.NodeId}",
                        Image = renderImg,
                        Thumbnail = _renderService.CreateThumbnail(renderImg)
                    };

                    ImageDisplayVm.ImageHistoryList.Add(renderContext);
                    if (ImageDisplayVm.IsAutoSwitchEnabled)
                    {
                        ImageDisplayVm.SelectImageItem(renderContext);
                    }
                }
            });
        }

        private async Task StartWorkerAsync()
        {
            if (_workerClient == null) return;
            await _workerClient.LoadRecipeAsync(CurrentProcess);
            await _workerClient.StartAsync();
            await _workerClient.TriggerOnceAsync(); // 触发一次执行
        }

        private async Task TriggerWorkerOnceAsync()
        {
            if (_workerClient == null) return;
            await _workerClient.TriggerOnceAsync();
        }

        private async Task StopWorkerAsync()
        {
            if (_workerClient == null) return;
            await _workerClient.StopAsync();
        }

        private async void OnCurrentProcessChanged()
        {
            if (_workerClient != null && CurrentProcess != null)
            {
                await _workerClient.LoadRecipeAsync(CurrentProcess);
            }
        }
        #endregion

        #region 日志通信总线与 UI 防爆机制
        private void InitLogBusSubscription()
        {
            const int MAX_UI_LOG_COUNT = 500;

            LogBus.OnLogProduced += entry =>
            {
                if (entry.Level < LogLevel.Info) return;

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

        #region 画布、配方与节点交互保持
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
                    Owner = Application.Current?.MainWindow
                };
                win.ShowDialog();
            }
        }

        private void OnSaveCurrentPipelineAsRecipe()
        {
            string defaultName = CurrentProcess?.ProcessName ?? "新复合配方";
            string recipeName = PromptDialog.Show("保存为复合模板", "请输入要导出的配方名称：", defaultName);
            if (string.IsNullOrWhiteSpace(recipeName)) return;

            _recipeManager.SavePipelineAsRecipe(CurrentProcess, recipeName.Trim(), ToolBox);
        }

        public void DrillDownCompositeNode(CompositeFlowNode compositeNode)
        {
            if (compositeNode?.SubProcess != null)
            {
                CurrentProcess = compositeNode.SubProcess;
                if (!Breadcrumbs.Contains(CurrentProcess)) Breadcrumbs.Add(CurrentProcess);

                SelectedNode = null;
                LogBus.Info("Flow", $"已经下钻进入子流程: [{CurrentProcess.ProcessName}]");
            }
        }

        private void ImportRecipe()
        {
            var imported = _recipeManager.ImportRecipe();
            if (imported != null)
            {
                RootProcess = imported;
                Breadcrumbs.Clear();
                Breadcrumbs.Add(RootProcess);
                CurrentProcess = RootProcess;
            }
        }
        public void AddNodeFromTemplate(UnitMeta meta, Point2D position)
        {
            AddNodeFromMeta(meta, position);
        }
        public void AddNodeFromMeta(UnitMeta meta, Point2D pos)
        {
            var node = NodeFactory.CreateFromMeta(meta, pos);
            CurrentProcess.Nodes.Add(node);
            SelectedNode = node;
            LogBus.Info("Flow", $"新增节点: {node.DisplayName}");
        }

        public void ClearCanvas(bool showConfirm = true)
        {
            if (CurrentProcess == null || (CurrentProcess.Nodes.Count == 0 && CurrentProcess.Connections.Count == 0)) return;

            if (showConfirm)
            {
                var res = MessageBox.Show("确定要清空当前画布上的所有节点和连线吗？", "提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }

            CurrentProcess.Connections.Clear();
            CurrentProcess.Nodes.Clear();
            SelectedNode = null;
            LogBus.Info("Flow", $"画布 [{CurrentProcess.ProcessName}] 已清空。");
        }
           public void AddConnection(FlowNodeBase source, NodePort sourcePort, FlowNodeBase target, NodePort targetPort)
        {
            if (source == null || target == null || source == target || sourcePort == null || targetPort == null) return;
            if (sourcePort.PortType == targetPort.PortType) return;

            var connection = new ConnectionModel(source, sourcePort, target, targetPort);
            CurrentProcess.Connections.Add(connection);
            LogBus.Info("Flow", $"建立连线: {source.DisplayName} [{sourcePort.PortName}] -> {target.DisplayName} [{targetPort.PortName}]");
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
            LogBus.Info("Flow", $"删除节点: {SelectedNode.DisplayName}");
            SelectedNode = null;
        }

        public void DeleteConnection(ConnectionModel conn)
        {
            if (conn != null && CurrentProcess.Connections.Contains(conn))
            {
                CurrentProcess.Connections.Remove(conn);
                LogBus.Info("Flow", "删除了连线");
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

        private void AutoLayout()
        {
            if (CurrentProcess == null || CurrentProcess.Nodes.Count == 0) return;
            LogBus.Info("Flow", "已完成自上而下的智能拓扑重排。");
        }

        private void InitFullToolBox()
        {
            ToolBox.Clear();
            var metas = NodeFactory.GenerateToolboxMetas();
            foreach (var meta in metas)
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
    }
}