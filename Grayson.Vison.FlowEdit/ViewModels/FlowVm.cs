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
                    RefreshSelectedNodeOutputs();
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
        /// <summary>
        /// 🌟 2026-09-10 修复：此处此前显示的是 <see cref="FlowProcessModel.ProcessName"/>（主流程名），
        ///    并非配方名。演示配方工厂（DlDemoTaskFactory:439 / MeasurementDemoTaskFactory:290）刻意让
        ///    ProcessName ≠ RecipeName，配方页手工改名也不会同步 ProcessName，
        ///    于是编辑器顶部「配方」名与配方管理页列表名系统性不一致。
        ///    现统一以 RecipeName 为准，主流程名另列，便于对照。
        /// </summary>
        public string CurrentRecipeInfo =>
            $"配方: {CurrentRecipe?.RecipeName ?? RootProcess?.ProcessName ?? "未定义"}{(IsDirty ? " *" : "")}" +
            $"  | 主流程: {RootProcess?.ProcessName ?? "未定义"}" +
            $"  | 节点数: {CurrentProcess?.Nodes?.Count ?? 0}";

        public ObservableCollection<SharedDataItem> WatchData { get; set; } = new ObservableCollection<SharedDataItem>();
        /// <summary>WatchData 的 Key 索引（Key = "节点名.端口名"），增量更新避免列表重建闪烁</summary>
        private readonly Dictionary<string, SharedDataItem> _watchDataIndex = new Dictionary<string, SharedDataItem>();

        /// <summary>节点执行历史（最新在前，最多保留 300 条）</summary>
        public ObservableCollection<NodeExecRecord> ExecutionHistory { get; } = new ObservableCollection<NodeExecRecord>();

        /// <summary>当前选中节点的输出端口值明细（画布点选节点 → 右侧面板实时显示）</summary>
        public ObservableCollection<SharedDataItem> SelectedNodeOutputs { get; } = new ObservableCollection<SharedDataItem>();
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
                    RefreshBoundProcessKey();
                    OnPropertyChanged(nameof(CurrentStationDisplayName));
                    OnPropertyChanged(nameof(CurrentRecipeInfo));

                    if (!_isApplyingStationContext)
                    {
                        // 🌟 2026-09-10：切换工位不再只重绑 Worker，而是先按工位绑定关系整体切换上下文
                        //    （加载该工位绑定的配方及其编排节点；该工位未绑定配方则清空画布）
                        SwitchStationContextAsync();
                    }
                }
            }
        }

        private string _boundProcessKey;
        /// <summary>
        /// 当前编辑目标工位在生产模式下绑定的业务过程键（来自工位配置，非当前 Worker）。
        /// 编辑器始终以纯视觉链模式运行（过程已卸载），此属性仅用于顶部 banner 提示。
        /// </summary>
        public string BoundProcessKey
        {
            get => _boundProcessKey;
            private set
            {
                if (Set(ref _boundProcessKey, value))
                {
                    OnPropertyChanged(nameof(ProcessBannerText));
                }
            }
        }

        /// <summary>业务过程绑定提示文本（未绑定业务过程时为 null，banner 折叠）</summary>
        public string ProcessBannerText => string.IsNullOrWhiteSpace(BoundProcessKey)
            ? null
            : $"⚠ 生产模式绑定业务过程 [{BoundProcessKey}] — 本编辑器已卸载该过程，仅调试视觉链（运动/IO 时序由外部业务代码控制）";

        /// <summary>从工位配置刷新 Banner（工位配置声明了 ProcessKey 才显示）</summary>
        private void RefreshBoundProcessKey()
        {
            var config = ResolveStationConfig(SelectedStationId);
            BoundProcessKey = string.IsNullOrWhiteSpace(config?.ProcessKey) ? null : config.ProcessKey;
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

        #region 实时预览（节点属性面板内嵌视图窗口）

        /// <summary>预览防抖定时器（参数变化后 300ms 触发一次单步重跑）</summary>
        private readonly System.Windows.Threading.DispatcherTimer _previewDebounce;

        /// <summary>预览目标节点（当前属性弹窗编辑的节点，可能与 SelectedNode 不同）</summary>
        private FlowNodeBase _previewTargetNode;

        /// <summary>预览参数模型的事件订阅句柄（解绑用）</summary>
        private INotifyPropertyChanged _previewParamModel;

        /// <summary>预览执行中标记（重入保护）</summary>
        private bool _isPreviewRunning;

        /// <summary>执行忙时记录一次待跑（拖动滑块高频触发的合并策略）</summary>
        private bool _previewQueued;

        private bool _isLivePreview = true;

        /// <summary>参数变化是否自动刷新预览（属性面板复选框绑定）</summary>
        public bool IsLivePreview
        {
            get => _isLivePreview;
            set => Set(ref _isLivePreview, value);
        }

        /// <summary>每次预览执行完成后回调（属性窗口用来隐藏"运行后显示预览"占位提示）</summary>
        public event Action OnPreviewExecuted;

        #endregion

        public event Action<FlowNodeBase> OnNodeExecuting;
        public event Action<FlowNodeBase> OnNodeExecuted;
        public event Action<FlowNodeBase, Exception> OnExecutionError;

        /// <summary>
        /// 宿主可注入统一配方保存委托（如 WpfUI 保存到 RecipeStorage）；
        /// 未注入时回退到编辑器内置“导出文件”保存模式。
        /// </summary>
        public Func<RecipeModel, bool> HostRecipeSaveHandler { get; set; }

        /// <summary>
        /// 🌟 2026-09-10 新增：宿主注入的配方读取委托。
        /// 编辑器内切换工位时，用它按工位配置的 BoundRecipeId / BoundRecipeName 从配方库取回完整配方，
        /// 从而"切工位 → 自动加载该工位绑定的配方及其编排节点"。
        /// 未注入时退化为只重绑 Worker（保持旧行为，不换配方、不清画布）。
        /// </summary>
        public Func<StationConfigModel, RecipeModel> HostRecipeLoadHandler { get; set; }

        private bool _isPlaceholderRecipe;
        /// <summary>
        /// 当前画布承载的是"工位未绑定配方"时生成的临时占位配方（切换工位自动清空画布的产物）。
        /// 占位配方无对应的配方库实体，禁止回写配方库，避免产生垃圾配方文件。
        /// </summary>
        public bool IsPlaceholderRecipe
        {
            get => _isPlaceholderRecipe;
            private set => Set(ref _isPlaceholderRecipe, value);
        }

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
        /// <summary>🌟 2026-09-10 新增：重命名当前流程层级（面包屑上显示的名称）。</summary>
        public ICommand RenameProcessCmd { get; }
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

            // 1.1 初始化预览防抖定时器（属性面板参数变化 → 300ms 后单步重跑该节点）
            _previewDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _previewDebounce.Tick += async (s, e) =>
            {
                _previewDebounce.Stop();
                await GuardRunPreviewAsync();
            };

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
            PauseRunCmd = new RelayCommand(async () => await PauseWorkerAsync());
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
            RenameProcessCmd = new RelayCommand(RenameCurrentProcess);
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
                        // 🌟 1. 如果端口声明了 IsRequired = false（非必连），直接跳过预检报警
                        if (!inputPort.IsRequired)
                        {
                            continue;
                        }

                        // 🌟 2. 如果未扩展 IsRequired 属性，可根据端口类型（PortCategory）做通用策略处理：
                        // 数据端口（Data Port）通常允许直接读取节点属性面板中的默认值，不强制要求连线；
                        // 只有控制流端口（Exec Port）且不是首节点时才强制要求连线。
                        if (inputPort.Category == PortCategory.Data)
                        {
                            continue; // 数据端口默认允许无连线输入（从节点 Param 默认值获取）
                        }

                        // 🌟 3. 记录真正的缺失连线异常
                        issues.Add(new PreflightPortIssue
                        {
                            Node = node,
                            PortName = inputPort.PortName,
                            Message = $"节点 [{node.DisplayName}] 的必填输入端口 [{inputPort.PortName}] 未连接上游节点。"
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

            // 🌟 2026-09-10：工位未绑定配方时的空白占位画布不允许回写配方库，
            //    否则会在 Recipes 目录产生 RCP-UNBOUND-* 垃圾配方文件。
            if (IsPlaceholderRecipe)
            {
                MessageBox.Show("当前工位未绑定配方，画布内容无法保存。\n请先在【配方管理】中创建配方并下发绑定到该工位。",
                                "未绑定配方", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                // 🌟 2026-09-10：导入的是真实配方，清除"工位未绑定占位流程"标记，否则会被保存守卫误拦
                IsPlaceholderRecipe = false;

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

        /// <summary>
        /// 🌟 2026-09-10 新增：重命名「当前流程层级」的名称（即面包屑上显示的那个名字）。
        /// 说明：主流程名 = 配方页「主流程名称」字段显示的值，两处改的是同一个 ProcessName；
        /// 子流程名则只影响该子流程。改名本身不落盘，需随后点保存（与画布编辑一致）。
        /// </summary>
        private void RenameCurrentProcess()
        {
            if (CurrentProcess == null) return;

            string oldName = CurrentProcess.ProcessName ?? string.Empty;
            string input = PromptDialog.Show("重命名流程", "请输入流程名称（面包屑上显示的名称）：", oldName);
            if (string.IsNullOrWhiteSpace(input)) return;

            input = input.Trim();
            if (string.Equals(input, oldName, StringComparison.Ordinal)) return;

            CurrentProcess.ProcessName = input;   // 属性已带通知 → 面包屑即时刷新
            IsDirty = true;
            OnPropertyChanged(nameof(CurrentRecipeInfo)); // 顶部「主流程: xxx」依赖它

            LogBus.Info("Flow", $"流程 [{oldName}] 已重命名为 [{input}]。");
        }

        #endregion

        #region 6. Worker 客户端通信与运行控制

        private IStationHostRuntime _stationHostRuntime;

        private async void InitWorkerClient()
        {
            await RebindWorkerClientAsync(GetEffectiveStationId());
        }

        /// <summary>
        /// 🌟 2026-09-10 新增：工位切换总入口（取代原先只重绑 Worker 的 SwitchWorkerStationAsync）。
        /// 约定语义：切换工位 → 自动加载该工位绑定的配方及其编排节点；该工位未绑定配方 → 清空对应内容区域。
        /// 切换前若画布有未保存改动 → 先自动落盘当前配方（用户约定：自动保存后切换）。
        /// </summary>
        private async void SwitchStationContextAsync()
        {
            // 防御：AvailableStations 重建（Clear）时 ComboBox 会回写 null 到选中项，
            // 那是绑定产物而非用户切换工位，不能据此清空/切换画布。
            if (string.IsNullOrWhiteSpace(SelectedStationId))
            {
                return;
            }

            var stationId = GetEffectiveStationId();
            var stationConfig = ResolveStationConfig(stationId);

            // 0. 工位上下文未注册（独立编辑器壳 / 未注入工位清单）→ 退化为旧行为：仅重绑 Worker
            if (stationConfig == null)
            {
                await RebindWorkerClientAsync(stationId);
                return;
            }

            var boundRecipe = ResolveBoundRecipeForStation(stationConfig);

            // 1. 该工位未绑定任何配方（或绑定已失联）→ 清空画布内容区域
            if (boundRecipe == null)
            {
                var hadContent = (CurrentRecipe?.MainProcess?.Nodes?.Count ?? 0) > 0
                                 || (CurrentRecipe?.MainProcess?.Connections?.Count ?? 0) > 0;
                if (hadContent)
                {
                    AutoSaveBeforeStationSwitch();
                }

                LoadRecipe(CreateUnboundPlaceholderRecipe(stationConfig), isPlaceholder: true);
                LogBus.Warn("FlowVm", $"工位 [{stationId}] 未绑定配方，画布已清空（当前为占位空白流程，不落盘）。");
                return;
            }

            // 2. 该工位绑定了配方，且与当前编辑的不是同一份 → 先落盘当前改动，再整体切换到目标配方
            if (!IsSameRecipe(boundRecipe, CurrentRecipe))
            {
                AutoSaveBeforeStationSwitch();
                LoadRecipe(boundRecipe);
                LogBus.Info("FlowVm", $"已切换至工位 [{stationId}] 绑定的配方: [{boundRecipe.RecipeName}]");
                return;
            }

            // 3. 同一份配方：画布内容保留，只重绑 Worker 到新工位
            if (_workerClient != null && string.Equals(_workerClient.StationId, stationId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await RebindWorkerClientAsync(stationId);
        }

        /// <summary>
        /// 解析指定工位绑定的配方实体。
        /// 匹配顺序：BoundRecipeId 优先，BoundRecipeName 兜底（配方页下发时 BoundRecipeName 存的是显示名）。
        /// 未注入宿主读取委托、或绑定失联（配方已被删除/改名）时返回 null → 由调用方按"未绑定"处理。
        /// </summary>
        private RecipeModel ResolveBoundRecipeForStation(StationConfigModel stationConfig)
        {
            if (stationConfig == null) return null;
            if (string.IsNullOrWhiteSpace(stationConfig.BoundRecipeId)
                && string.IsNullOrWhiteSpace(stationConfig.BoundRecipeName))
            {
                return null;
            }

            if (HostRecipeLoadHandler == null)
            {
                LogBus.Warn("FlowVm", "宿主未注入配方读取委托，无法按工位绑定加载配方。");
                return null;
            }

            try
            {
                var recipe = HostRecipeLoadHandler(stationConfig);
                if (recipe == null)
                {
                    LogBus.Warn("FlowVm",
                        $"工位 [{stationConfig.StationCode ?? stationConfig.StationId}] 绑定的配方无法加载" +
                        $"（BoundRecipeId={stationConfig.BoundRecipeId ?? "-"}，" +
                        $"BoundRecipeName={stationConfig.BoundRecipeName ?? "-"}），按未绑定处理。");
                }

                return recipe;
            }
            catch (Exception ex)
            {
                LogBus.Error("FlowVm", $"加载工位绑定配方失败: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>判定两个配方是否同一份（按 RecipeId 优先、RecipeCode 兜底；引用相同直接命中）</summary>
        private static bool IsSameRecipe(RecipeModel left, RecipeModel right)
        {
            if (left == null || right == null) return false;
            if (ReferenceEquals(left, right)) return true;

            if (!string.IsNullOrWhiteSpace(left.RecipeId) && !string.IsNullOrWhiteSpace(right.RecipeId))
            {
                return string.Equals(left.RecipeId, right.RecipeId, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(left.RecipeCode) && !string.IsNullOrWhiteSpace(right.RecipeCode))
            {
                return string.Equals(left.RecipeCode, right.RecipeCode, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// 切换工位前的自动落盘（用户约定：不弹窗、不丢弃，直接保存当前编排）。
        /// 占位配方（工位未绑定）没有配方库实体，直接丢弃脏标记而不落盘。
        /// </summary>
        private void AutoSaveBeforeStationSwitch()
        {
            if (CurrentRecipe == null || !IsDirty) return;

            if (IsPlaceholderRecipe)
            {
                IsDirty = false;
                return;
            }

            if (HostRecipeSaveHandler == null)
            {
                LogBus.Warn("FlowVm", "宿主未注入配方保存委托，切换工位前的自动保存已跳过。");
                return;
            }

            try
            {
                CurrentRecipe.MainProcess = RootProcess;
                CurrentRecipe.LastModifiedTime = DateTime.Now;

                if (HostRecipeSaveHandler(CurrentRecipe))
                {
                    IsDirty = false;
                    LogBus.Info("Recipe", $"切换工位前已自动保存配方 [{CurrentRecipe.RecipeName}]。");
                }
                else
                {
                    LogBus.Warn("Recipe", $"切换工位前自动保存配方 [{CurrentRecipe.RecipeName}] 失败。");
                }
            }
            catch (Exception ex)
            {
                LogBus.Error("Recipe", $"切换工位前自动保存异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 构造"工位未绑定配方"时用于清空画布的空白占位配方。
        /// 带明确命名标识，且 IsPlaceholderRecipe=true 时不允许回写配方库。
        /// </summary>
        private static RecipeModel CreateUnboundPlaceholderRecipe(StationConfigModel stationConfig)
        {
            var label = stationConfig?.StationCode ?? stationConfig?.StationId ?? "未知工位";

            return new RecipeModel
            {
                RecipeId = Guid.NewGuid().ToString("N"),
                RecipeCode = "RCP-UNBOUND-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                RecipeName = $"未绑定配方（{label}）",
                ProductCategory = "未绑定工位",
                Version = "1.0.0",
                Author = Environment.UserName,
                Description = "视觉流程编辑器在切换工位时自动生成的空白占位流程（该工位未绑定任何配方），不会写入配方库。",
                MainProcess = new FlowProcessModel { ProcessName = "主流程" }
            };
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

                // 🌟 编辑器固定为纯视觉链调试模式：无论工位配置是否声明了业务过程，
                //    均显式卸载——避免「运行/单步」误触发完整业务周期（含运动/IO 时序）。
                //    生产模式下的完整业务周期仍由工位 UI 侧创建的 Worker 承载。
                _workerClient.DetachProcess();

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

                // 🌟 数据监控：刷新全节点端口值汇总 + 记录执行历史
                RefreshWatchData();
                RecordNodeExecution(e.Node, success: true);

                // 🌟 节点执行完成 → 主视图跟随该节点输出图（编辑器「单步/连续」的核心体验）。
                // 说明：帧推送事件 OnFrameRendered 走 Dispatcher Background 优先级且与
                // OnNodeExecuted 分属两条回调，若仅依赖它，会出现「缩略图已加、主视图没切」
                // 的时序缺口；这里在节点完成回调（Normal 优先级、每节点必触发）内直接按
                // 输出端口取图推入显示层。AddOrUpdateImageContext 按 NodeId 幂等：若帧事件
                // 稍后到达，同 NodeId 同图原地替换，无闪烁。节点无图像输出（纯数值/点集）时
                // 跳过——其叠加图形已由 Executor 经 Preview 场景直接上屏，不抢占主视图。
                FollowExecutedNodeImage(e.Node);

                OnNodeExecuted?.Invoke(e.Node);
            });
        }

        /// <summary>
        /// 把「刚执行完的节点」的图像输出推给显示层并自动选中（主视图跟随）。
        /// 与 WorkerClient_OnFrameRendered 的判定口径一致（DataType=="Image" 或端口名含 Image），
        /// 重复调用同 NodeId 幂等（替换路径按 NativeHandle 共享判定释放，不泄漏）。
        /// </summary>
        private void FollowExecutedNodeImage(FlowNodeBase node)
        {
            if (node?.OutputPorts == null || ImageDisplayVm == null) return;
            try
            {
                var imagePort = node.OutputPorts.FirstOrDefault(p =>
                    (p.DataType != null && p.DataType == "Image") ||
                    (p.PortName != null && p.PortName.IndexOf("Image", StringComparison.OrdinalIgnoreCase) >= 0));
                if (imagePort?.DataValue == null) return;

                var renderImg = _renderService.WrapImage(imagePort.DataValue);
                if (renderImg == null) return;

                var context = new WpfImageRenderContext
                {
                    NodeId = node.NodeId,
                    NodeName = node.DisplayName,
                    Image = renderImg,
                    Thumbnail = _renderService.CreateThumbnail(renderImg)
                };
                ImageDisplayVm.AddOrUpdateImageContext(context);
            }
            catch (Exception ex)
            {
                LogBus.Error("FlowVm", $"节点输出图跟随显示失败 [{node.DisplayName}]: {ex.Message}");
            }
        }

        private void Worker_OnExecutionError(object sender, NodeExecutionErrorEventArgs e)
        {
            if (e?.Node == null) return;

            // 🌟 使用 Dispatcher 强制切回 UI 主线程更新
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                e.Node.IsRunning = false;
                e.Node.HasError = true;

                // 🌟 数据监控：记录失败历史（保留出错摘要）
                RecordNodeExecution(e.Node, success: false, e.Exception?.Message);

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

                    // ⚠ 每次都新建上下文交给 AddOrUpdateImageContext 统一释放旧图：
                    // 旧实现"就地换 Image 再 AddOrUpdate"会命中 existing==newContext 分支
                    // Dispose 当场销毁刚换上的新图（第二帧起黑屏）。新建 + 共享判定释放，
                    // 兼容 MatchImage 借用语义（与相机输出同一 HImage 实例）。
                    var node = CurrentProcess?.Nodes?.FirstOrDefault(n => n.NodeId == e.NodeId);
                    var renderContext = new WpfImageRenderContext
                    {
                        NodeId = e.NodeId,
                        NodeName = node?.DisplayName ?? $"[{e.NodeName}]",
                        Image = renderImg,
                        Thumbnail = _renderService.CreateThumbnail(renderImg)
                    };

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
        #region 数据监控（WatchData / 执行历史 / 节点输出）

        /// <summary>
        /// 刷新全节点输出端口值汇总（WatchData）。增量更新：
        /// 端口值变化的项原地刷新，新增项添加，节点删除/端口清空的项移除。
        /// 仅在 UI 线程调用（由 Worker_OnNodeExecuted 的 Dispatcher 回调进入）。
        /// </summary>
        private void RefreshWatchData()
        {
            if (CurrentProcess?.Nodes == null) return;

            var liveKeys = new HashSet<string>();
            foreach (var node in CurrentProcess.Nodes)
            {
                if (node?.OutputPorts == null) continue;
                foreach (var port in node.OutputPorts)
                {
                    if (port?.DataValue == null) continue;
                    string key = node.DisplayName + "." + port.PortName;
                    liveKeys.Add(key);

                    var fmt = FormatPortValue(port.DataValue);
                    if (_watchDataIndex.TryGetValue(key, out var item))
                    {
                        if (!Equals(item.Value, fmt.Text))
                        {
                            item.RawType = fmt.Type;
                            item.Value = fmt.Text;
                        }
                    }
                    else
                    {
                        var newItem = new SharedDataItem { Key = key, RawType = fmt.Type, Value = fmt.Text };
                        _watchDataIndex[key] = newItem;
                        WatchData.Add(newItem);
                    }
                }
            }

            // 移除已不在画布/已无输出值的监控项
            var stale = WatchData.Where(w => !liveKeys.Contains(w.Key)).ToList();
            foreach (var s in stale)
            {
                _watchDataIndex.Remove(s.Key);
                WatchData.Remove(s);
            }
        }

        /// <summary>刷新当前选中节点的输出端口值明细（点选节点时调用）</summary>
        private void RefreshSelectedNodeOutputs()
        {
            SelectedNodeOutputs.Clear();
            if (SelectedNode?.OutputPorts == null) return;
            foreach (var port in SelectedNode.OutputPorts)
            {
                if (port.DataValue == null) continue;
                var fmt = FormatPortValue(port.DataValue);
                SelectedNodeOutputs.Add(new SharedDataItem
                {
                    Key = port.PortName,
                    RawType = fmt.Type,
                    Value = fmt.Text
                });
            }
        }

        /// <summary>追加一条节点执行历史（最新在前，上限 300 条）</summary>
        private void RecordNodeExecution(FlowNodeBase node, bool success, string errorMsg = null)
        {
            string summary;
            if (success)
            {
                var outputs = node.OutputPorts?.Where(p => p.DataValue != null)
                    .Select(p => $"{p.PortName}={FormatPortValue(p.DataValue).Text}").ToList();
                summary = outputs != null && outputs.Count > 0
                    ? string.Join(" | ", outputs)
                    : "执行成功（无输出值）";
            }
            else
            {
                summary = "执行失败" + (string.IsNullOrEmpty(errorMsg) ? "" : "： " + errorMsg);
            }
            if (summary.Length > 400) summary = summary.Substring(0, 400) + " …";

            ExecutionHistory.Insert(0, new NodeExecRecord
            {
                Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                NodeName = node.DisplayName,
                Success = success,
                Summary = summary
            });
            while (ExecutionHistory.Count > 300)
            {
                ExecutionHistory.RemoveAt(ExecutionHistory.Count - 1);
            }
        }

        /// <summary>端口值 → (显示文本, 原始类型名)。图像对象给出友好摘要，避免 ToString 无意义输出。</summary>
        private (string Text, string Type) FormatPortValue(object val)
        {
            if (val == null) return ("null", "null");
            string typeName = val.GetType().Name;

            // 图像类对象（HObject/HImage/IRenderImage 等）：不给原始 ToString，尝试取尺寸
            if (typeName.IndexOf("HObject", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("HImage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("RenderImage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("Image", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    var mi = val.GetType().GetMethod("GetImageSize");
                    if (mi != null)
                    {
                        var pars = new object[] { 0, 0 };
                        mi.Invoke(val, pars);
                        return ($"(图像 {pars[1]}x{pars[0]})", typeName);
                    }
                }
                catch { }
                return ("(图像对象)", typeName);
            }

            switch (val)
            {
                case string s: return (s, "string");
                case double d: return (d.ToString("G4"), "double");
                case float f: return (f.ToString("G4"), "float");
                case int i: return (i.ToString(), "int");
                case long l: return (l.ToString(), "long");
                case bool b: return (b ? "true" : "false", "bool");
                default: return (val.ToString(), typeName);
            }
        }

        #endregion

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
        // 🌟 【运行】(F5)：从链头完整执行整条视觉链一次（节点间上下文关联）
        private async Task StartWorkerAsync()
        {
            if (_workerClient == null) return;

            try
            {
                //await PrepareForNewExecutionAsync();
                await EnsureWorkerSyncedAsync(); // 🌟 运行前自动检查并重新构建
                EnsurePreviewTarget();           // ★ 同「单步」：运行前重新武装预览目标（共享 Worker 单槽）
                await _workerClient.StartAsync();
                await _workerClient.RunContinuousAsync();
            }
            catch (Exception ex)
            {
                LogBus.Error("FlowVm", $"启动失败: {ex.Message}");
            }
        }
        // 🌟 【单步】(F10)：视觉链步进——从链头开始、一次只执行 1 个节点，
        //    与前后节点输入输出上下文关联（内部即调度器 TriggerOnceAsync 单步）；
        //    走 StepChainAsync 保证绝不进入业务过程分流（即使 StartAsync 自愈重挂了业务过程，
        //    编辑器单步也永远不会触发运动/IO 时序）。
        private async Task TriggerWorkerOnceAsync()
        {
            if (_workerClient == null) return;

            try
            {
                //await PrepareForNewExecutionAsync();
                await EnsureWorkerSyncedAsync(); // 🌟 触发前自动检查并重新构建
                // ★ 执行前重新武装预览目标（与 StepRunNodeAsync 同口径）。
                //   共享 Worker 只有一个预览槽（StationContext.PreviewContext），
                //   工位监视页进入/离开都会改它；整链「单步」不重新武装的话，
                //   节点叠加层就会画到别的窗口（或无处可画）。必须在
                //   EnsureWorkerSyncedAsync 之后——重新装载配方会重建执行链。
                EnsurePreviewTarget();
                await _workerClient.StepChainAsync();
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

            // 单步前重新武装预览目标（依赖当前已注入的上下文，可能被共享 Worker 的其他宿主覆盖）
            EnsurePreviewTarget();

            // StationHostRuntime 已统一创建 EmbeddedWorkerClientProxy，直接调用接口即可
            await _workerClient.StepNodeAsync(SelectedNode);
        }

        #region 实时预览执行链（属性面板专用）

        /// <summary>主编辑器视图的预览适配器（常驻默认目标），由 FlowEditView 注册</summary>
        private IFlowPreviewContext _defaultPreview;

        /// <summary>默认预览目标的注册者（视图实例）。用于卸载时"只解除自己注册的那个"</summary>
        private object _defaultPreviewOwner;

        /// <summary>属性面板的预览适配器（临时目标）</summary>
        private IFlowPreviewContext _panelPreview;

        /// <summary>
        /// 注册默认预览目标（主编辑器视图窗口）。
        /// 只要主视图存在，它**始终**是预览目标之一（与属性面板扇出，不再二选一）。
        /// </summary>
        /// <param name="owner">注册者（视图实例），仅用于卸载时的归属校验，可为 null</param>
        public void SetDefaultPreview(IFlowPreviewContext previewContext, object owner = null)
        {
            _defaultPreview = previewContext;
            _defaultPreviewOwner = previewContext == null ? null : owner;
            ApplyPreviewTarget();
        }

        /// <summary>
        /// 解除默认预览目标（视图卸载时调用）。
        /// ★ 只解除 owner 自己注册的那一个：编辑器页面每次导航都是**新**的视图实例，
        ///   而"新视图已注册 → 旧视图延迟卸载"的乱序会让旧视图把新视图刚注册的目标抹掉，
        ///   结果又是"预览无处可画"却看不出是谁清的。
        /// </summary>
        public void ClearDefaultPreview(object owner)
        {
            if (_defaultPreview == null) return;

            if (owner != null && _defaultPreviewOwner != null && !ReferenceEquals(_defaultPreviewOwner, owner))
            {
                LogBus.Info("FlowVm", "忽略非注册者的默认预览目标解除请求（防旧视图卸载抹掉新视图的注册）。");
                return;
            }

            _defaultPreview = null;
            _defaultPreviewOwner = null;
            ApplyPreviewTarget();
        }

        /// <summary>
        /// ★★ 预览目标注入的**唯一入口**：把「属性面板预览 + 编辑器主视图预览」一起注入引擎
        /// （MultiTargetPreviewContext 扇出），而不是原来的"二选一"。
        ///
        /// 为什么必须扇出（2026-09-15 定案）：节点执行期间的"效果"分两部分——
        ///   ① 图像：经端口值 → ImageDisplayVm → 主视图（这条通路本来就一直在工作）；
        ///   ② 场景叠加层：经 NodeExecutionContext.Preview → **单个**显示宿主。
        /// 原实现面板打开时只注入面板 ⇒ 形状匹配的十字/分数文本/贴合轮廓全画在面板里，
        /// 主视图只剩"和上游同一实例的底图"（MatchImage 借用 InputImage），看着就像"没刷新"。
        ///
        /// 每次执行前重新武装的原因不变：工位监视页与 FlowEdit 共享同一个 Worker（单槽），
        /// 别的宿主 SetPreviewContext 会直接覆盖本编辑器的注入。
        /// </summary>
        private void ApplyPreviewTarget()
        {
            // Combine 在单目标时直接返回该目标本体（不包壳），保持与改动前一致的对象身份。
            var target = MultiTargetPreviewContext.Combine(_panelPreview, _defaultPreview);

            // 只在"目标组合发生变化"时记一行（实时预览会按参数变化高频重跑，逐次记会淹掉日志）
            var desc = target == null
                ? "无（节点绘制将被跳过）"
                : string.Join(" + ", new[]
                    {
                        _panelPreview != null ? "属性面板" : null,
                        _defaultPreview != null ? "编辑器主视图" : null
                    }.Where(x => x != null));

            if (_workerClient == null)
            {
                // 不静默：视图构造期就注册默认目标（此时 Worker 还没连）是正常时序，
                // 但"备好了却没注入"与"根本没有目标"在日志里必须能区分开，
                // 否则永远查不出"注册了却从没生效"（本轮踩过）。
                if (desc != _lastPreviewTargetDesc)
                {
                    _lastPreviewTargetDesc = desc;
                    LogBus.Info("FlowVm", $"[VM {GetHashCode():X}] 预览目标已就绪待注入: {desc}（Worker 未连接，执行前会重新武装）");
                }
                return;
            }

            _workerClient.SetPreviewContext(target);

            if (desc != _lastPreviewTargetDesc)
            {
                _lastPreviewTargetDesc = desc;
                LogBus.Info("FlowVm", $"[VM {GetHashCode():X}] 节点预览目标已注入: {desc}");
            }
        }

        /// <summary>上一次注入的预览目标组合描述（仅用于日志去重）</summary>
        private string _lastPreviewTargetDesc;

        /// <summary>
        /// 执行前确认预览目标已注入（面板 + 主视图扇出）。
        /// 保留方法名以免调用点语义扩散；行为统一收敛到 ApplyPreviewTarget。
        /// </summary>
        private void EnsurePreviewTarget()
        {
            ApplyPreviewTarget();
        }

        /// <summary>
        /// 挂接实时预览：把属性面板视图窗口的适配器注入引擎（经 WorkerClient → StationWorker
        /// → 调试单步 NodeExecutionContext.Preview），并订阅参数变化（防抖重跑）+ 立即执行首帧。
        /// 预览执行 = 一次真实的调试单步执行：输入取自端口值缓存（上游最近输出），
        /// 输出写回输出端口，主视图/端口调试区同步刷新，不存在第二套数据通路。
        /// </summary>
        public void AttachPreview(IFlowPreviewContext previewContext, FlowNodeBase node)
        {
            if (previewContext == null || node == null) return;

            DetachPreview();

            _previewTargetNode = node;
            _panelPreview = previewContext;
            ApplyPreviewTarget(); // 面板 + 主视图扇出（原来面板独占，主视图拿不到任何叠加层）

            if (node.ParameterModel is INotifyPropertyChanged pcm)
            {
                _previewParamModel = pcm;
                pcm.PropertyChanged += OnPreviewParamChanged;
            }

            // 首帧：打开面板立即跑一次（相当于自动点一次「▶ 运行该节点」）
            _ = GuardRunPreviewAsync();
        }

        /// <summary>
        /// 卸载实时预览：解绑参数事件、清除引擎注入（属性窗口 OnClosed 时调用）。
        /// </summary>
        public void DetachPreview()
        {
            if (_previewParamModel != null)
            {
                _previewParamModel.PropertyChanged -= OnPreviewParamChanged;
                _previewParamModel = null;
            }
            _previewDebounce?.Stop();
            _previewTargetNode = null;
            _panelPreview = null;
            // 属性面板关闭后回切到「主视图」预览目标，而非置空——否则工具栏「单步」无处可画
            ApplyPreviewTarget();
        }

        /// <summary>参数属性变化 → 防抖 300ms（拖动滑块高频触发时只跑最后一次）</summary>
        private void OnPreviewParamChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!IsLivePreview || _previewTargetNode == null) return;
            _previewDebounce.Stop();
            _previewDebounce.Start();
        }

        /// <summary>
        /// 预览执行（重入保护 + 合并待跑）：与 StepRunNodeAsync 同链路，
        /// 但目标是当前弹窗编辑的节点而非 SelectedNode。
        /// </summary>
        private async Task GuardRunPreviewAsync()
        {
            if (_previewTargetNode == null || _workerClient == null) return;

            if (_isPreviewRunning)
            {
                _previewQueued = true;
                return;
            }

            _isPreviewRunning = true;
            try
            {
                // 与工具栏「单步」同口径：跑之前重新武装预览目标（面板 + 主视图扇出）。
                // 共享 Worker 被别的宿主 SetPreviewContext 覆盖后，这里若不重新武装，
                // 面板里的实时预览会"画到别处去"（现象：改参数后预览窗口不再更新）。
                ApplyPreviewTarget();
                await _workerClient.StepNodeAsync(_previewTargetNode);
                OnPreviewExecuted?.Invoke();
            }
            catch (Exception ex)
            {
                LogBus.Error("FlowVm", $"实时预览执行失败 [{_previewTargetNode?.DisplayName}]: {ex.Message}");
            }
            finally
            {
                _isPreviewRunning = false;
                if (_previewQueued)
                {
                    _previewQueued = false;
                    _ = GuardRunPreviewAsync();
                }
            }
        }

        #endregion

        /// <summary>
        /// 暂停执行：编辑器为纯视觉链模式（过程已卸载），暂停走调度器挂起——
        /// 连续运行循环停止、新触发被忽略；单步节点预览仍可手动触发。
        /// 恢复：点「运行」按钮（StartAsync 会解除暂停并重启循环）。
        /// </summary>
        private async Task PauseWorkerAsync()
        {
            if (_workerClient == null) return;

            try
            {
                await _workerClient.PauseAsync();
                LogBus.Info("FlowVm", "已暂停执行（连续循环停止，点「运行」恢复）。");
            }
            catch (Exception ex)
            {
                LogBus.Error("FlowVm", $"暂停失败: {ex.Message}");
            }
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
                _watchDataIndex.Clear();
                ExecutionHistory.Clear();
                SelectedNodeOutputs.Clear();

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

            // 🌟【Bug修复】重复连线守卫：同一对端口之间禁止重复连线。
            //    此前画布连线未去重，重复保存/重连会在配方中堆积多条完全相同的连线。
            bool isDuplicate = CurrentProcess.Connections.Any(c =>
                c.SourceNode == source && c.TargetNode == target &&
                c.SourcePortId == sourcePort.PortId && c.TargetPortId == targetPort.PortId);

            if (isDuplicate)
            {
                LogBus.Info("Flow", $"连线已存在，忽略重复连接: {source.DisplayName}.{sourcePort.PortName} -> {target.DisplayName}.{targetPort.PortName}");
                return;
            }

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

                // 🌟 2026-09-10：不再在"配方未绑定任何工位"时静默选中列表第一个工位。
                //    静默兜底会造出「配方 A + 工位 X」这种自相矛盾的组合（X 可能绑定的是另一份配方 B），
                //    也让用户误以为正在为 X 编辑流程。此处保持未选中，UI 显示"未选择工位"；
                //    Worker 侧仍由 GetEffectiveStationId() 兜底到首个可用工位，不影响调试执行。
                var targetStationId = !string.IsNullOrWhiteSpace(preferredStationId)
                    ? preferredStationId
                    : null;

                SelectedStationId = targetStationId;

                if (string.IsNullOrWhiteSpace(targetStationId) && AvailableStations.Count > 0)
                {
                    LogBus.Info("FlowVm", "当前配方未绑定任何工位，工位选择保持为空（Worker 仍按首个可用工位调试运行）。");
                }
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
        /// <param name="isPlaceholder">
        /// 是否为"工位未绑定配方"生成的空白占位流程（切换工位清空画布的产物）。
        /// true 时禁止回写配方库，避免污染配方库。
        /// </param>
        public void LoadRecipe(RecipeModel recipe, bool isPlaceholder = false)
        {
            if (recipe == null) return;

            // 1. 更新当前配方引用
            CurrentRecipe = recipe;
            IsPlaceholderRecipe = isPlaceholder;

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

        /// <summary>
        /// 节点执行历史记录（ExecutionHistory 数据项，最新在前）。
        /// 由 Worker_OnNodeExecuted / Worker_OnExecutionError 经 RecordNodeExecution 追加。
        /// </summary>
        public sealed class NodeExecRecord
        {
            /// <summary>执行时刻（HH:mm:ss.fff）</summary>
            public string Time { get; set; }

            /// <summary>节点显示名</summary>
            public string NodeName { get; set; }

            /// <summary>是否执行成功</summary>
            public bool Success { get; set; }

            /// <summary>结果摘要（成功 = 输出端口值列表；失败 = 异常消息，截断至 400 字符）</summary>
            public string Summary { get; set; }

            /// <summary>UI 友好状态文本（成功 ✓ / 失败 ✗），供列表直接绑定显示</summary>
            public string StatusText => Success ? "✓ 成功" : "✗ 失败";
        }

        #endregion
    }
}
