using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Factories;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.ViewModels;
using Grayson.Vison.FlowEdit.Helpers;
using Grayson.Vison.FlowEdit.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

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
                    // 当选择的节点变化时，手动刷新删除命令的 CanExecute 状态
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
                    AddLog(value ? "已开启【数据端口】显示" : "已隐藏【数据端口】，仅保留控制流端口");
                }
            }
        }

        // 2. 解耦引用的内部服务与引擎
        public ExecutionContext Context { get; }
        private readonly RecipeManager _recipeManager;
        private FlowExecutor _executor;

        // 3. UI 交互事件 (如 View 层画布自动平移/跟焦)
        public event Action<FlowNodeBase> OnNodeExecuting;

        // 4. 命令属性 (需要手动触发 RaiseCanExecuteChanged 的声明为具体强类型)
        public RelayCommand DeleteNodeCmd { get; }
        public RelayCommand<ConnectionModel> DeleteConnectionCmd { get; }
        public ICommand SaveRecipeCmd { get; }
        public ICommand ImportRecipeCmd { get; }
        public ICommand ExportRecipeCmd { get; }
        public ICommand NavigateToProcessCmd { get; }
        public ICommand RunContinuousCmd { get; }
        public ICommand StepRunCmd { get; }
        public ICommand PauseRunCmd { get; }
        public ICommand StopRunCmd { get; }
        public ICommand AutoLayoutCmd { get; }
        public ICommand SaveCurrentPipelineAsRecipeCommand { get; }
        public ICommand ClearCanvasCommand { get; }

        public ICommand ToggleShowDataPortsCommand { get; }
        public ICommand OpenNodePropertyCommand { get; }

        public ImageDisplayVm ImageDisplayVm { get; set; }

        public FlowVm()
        {
            // 初始化事件与服务上下文
            Context = new ExecutionContext();
            Context.OnLogProduced += (s, log) => AddLog(log);
            Context.OnNodeExecuting += (s, node) => OnNodeExecuting?.Invoke(node);
            Context.OnExecutionError += OnGlobalExecutionError;

            // 初始化图像显示 VM
            ImageDisplayVm = new ImageDisplayVm(Context, new Grayson.Vision.HalconWrapper.Wpf.Imaging.HalconImageRenderService());

            // 自动加载当前运行目录及 Debug 目录下的 Nodes DLL 插件
            string pluginDir = AppDomain.CurrentDomain.BaseDirectory;
            new NodePluginLoader().LoadPlugins(pluginDir, AddLog);

            _recipeManager = new RecipeManager(new WpfDialogService());
            _recipeManager.LoadCompositeRecipeTemplates(ToolBox, AddLog);

            // 初始化工具箱与主配方
            InitFullToolBox();

            Breadcrumbs.Add(RootProcess);
            CurrentProcess = RootProcess;

            // 绑定执行引擎
            _executor = new FlowExecutor(CurrentProcess, Context);

            // Command 路由绑定
            RunContinuousCmd = new RelayCommand(async () => await _executor.RunContinuousAsync());
            StepRunCmd = new RelayCommand(async () => await _executor.StepAsync());
            StopRunCmd = new RelayCommand(() => _executor.Stop());
            PauseRunCmd = new RelayCommand(() => AddLog("暂停流程"));

            DeleteNodeCmd = new RelayCommand(DeleteSelectedNode, () => SelectedNode != null);
            DeleteConnectionCmd = new RelayCommand<ConnectionModel>(DeleteConnection);
            SaveRecipeCmd = new RelayCommand(() => AddLog("配方参数已成功保存！"));
            ImportRecipeCmd = new RelayCommand(ImportRecipe);
            ExportRecipeCmd = new RelayCommand(() => _recipeManager.ExportRecipe(RootProcess, AddLog));
            NavigateToProcessCmd = new RelayCommand<FlowProcessModel>(NavigateToProcess);
            AutoLayoutCmd = new RelayCommand(AutoLayout);
            SaveCurrentPipelineAsRecipeCommand = new RelayCommand(OnSaveCurrentPipelineAsRecipe);
            ClearCanvasCommand = new RelayCommand(() => ClearCanvas(false));
            ToggleShowDataPortsCommand = new RelayCommand(() => ShowDataPorts = !ShowDataPorts);
            OpenNodePropertyCommand = new RelayCommand<FlowNodeBase>(OnNodeDoubleClicked);
        }

        #region 点击节点属性弹框
        public void OnNodeDoubleClicked(FlowNodeBase node)
        {
            if (node == null) return;

            // 1. 如果是复合节点，保持原样下挖
            if (node is CompositeFlowNode compositeNode)
            {
                DrillDownCompositeNode(compositeNode);
            }
            // 2. 如果是普通节点，弹出参数模型与 XAML 配置窗口
            else
            {
                var win = new Grayson.Vison.FlowEdit.Views.NodePropertyWindow
                {
                    DataContext = node, // 将节点的 Model / ParameterModel 绑定至弹窗
                    Owner = Application.Current?.MainWindow
                };
                win.ShowDialog();
            }
        }
        #endregion

        #region 事件总线与切换流程关联
        private void OnGlobalExecutionError(object sender, NodeExecutionErrorEventArgs e)
        {
            var tryCatchNode = CurrentProcess.Nodes.FirstOrDefault(n => n.Type == NodeType.TryCatch && n.Enable);
            if (tryCatchNode != null)
            {
                e.Handled = true;
                AddLog($"捕获到节点 [{e.Node.DisplayName}] 的异常: {e.Exception.Message}");
            }
            else
            {
                MessageBox.Show($"节点 [{e.Node.DisplayName}] 抛出未处理异常:\n{e.Exception.Message}",
                                "流程错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCurrentProcessChanged()
        {
            _executor?.Stop();
            _executor = new FlowExecutor(CurrentProcess, Context);
        }

        public void ResetStepProgress() => _executor?.ResetIndex();
        public void AddLog(string log)
        {
            var logMsg = $"[{DateTime.Now:HH:mm:ss}] {log}";
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.InvokeAsync(() => ExecutionLogs.Insert(0, logMsg));
            }
            else
            {
                ExecutionLogs.Insert(0, logMsg);
            }
        }
        #endregion

        #region 配方、模板与下钻交互
        private void OnSaveCurrentPipelineAsRecipe()
        {
            string defaultName = CurrentProcess?.ProcessName ?? "新复合配方";
            string recipeName = PromptDialog.Show("保存为复合模板", "请输入要导出的配方名称：", defaultName);
            if (string.IsNullOrWhiteSpace(recipeName)) return;

            _recipeManager.SavePipelineAsRecipe(CurrentProcess, recipeName.Trim(), ToolBox, AddLog);
        }

        public void DrillDownCompositeNode(CompositeFlowNode compositeNode)
        {
            if (compositeNode?.SubProcess != null)
            {
                CurrentProcess = compositeNode.SubProcess;
                if (!Breadcrumbs.Contains(CurrentProcess)) Breadcrumbs.Add(CurrentProcess);

                SelectedNode = null;
                ResetStepProgress();
                AddLog($"已经下钻进入子流程: [{CurrentProcess.ProcessName}]");
            }
        }

        private void ImportRecipe()
        {
            var imported = _recipeManager.ImportRecipe(AddLog);
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
            if (meta.Type == NodeType.CompositeFlow && meta.NodeId.StartsWith("RECIPE_"))
            {
                string jsonFilePath = meta.Description;
                if (System.IO.File.Exists(jsonFilePath))
                {
                    string json = System.IO.File.ReadAllText(jsonFilePath);
                    var settings = new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Auto };
                    var subProcessModel = Newtonsoft.Json.JsonConvert.DeserializeObject<FlowProcessModel>(json, settings);

                    var compositeNode = new CompositeFlowNode
                    {
                        DisplayName = meta.DisplayName.Replace("复合: ", ""),
                        PosX = pos.X,
                        PosY = pos.Y,
                        RecipeFilePath = jsonFilePath,
                        SubProcess = subProcessModel ?? new FlowProcessModel { ProcessName = meta.DisplayName }
                    };

                    CurrentProcess.Nodes.Add(compositeNode);
                    SelectedNode = compositeNode;
                    AddLog($"成功实例化复合节点: {compositeNode.DisplayName}");
                    return;
                }
            }

            // 2. 普通节点统统交给底层 NodeFactory 构建！
            var node = NodeFactory.CreateFromMeta(meta, pos);
            CurrentProcess.Nodes.Add(node);
            SelectedNode = node;
            AddLog($"新增节点: {node.DisplayName}");
        }
        #endregion

        #region 画布操作与连线控制
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
            ResetStepProgress();
            AddLog($"画布 [{CurrentProcess.ProcessName}] 已清空。");
        }

        public void AddConnection(FlowNodeBase source, NodePort sourcePort, FlowNodeBase target, NodePort targetPort)
        {
            if (source == null || target == null || source == target || sourcePort == null || targetPort == null) return;
            if (sourcePort.PortType == targetPort.PortType) return;

            var connection = new ConnectionModel(source, sourcePort, target, targetPort);
            CurrentProcess.Connections.Add(connection);
            AddLog($"建立连线: {source.DisplayName} [{sourcePort.PortName}] -> {target.DisplayName} [{targetPort.PortName}]");
        }

        private void DeleteSelectedNode()
        {
            if (SelectedNode == null) return;
            for (int i = CurrentProcess.Connections.Count - 1; i >= 0; i--)
            {
                if (CurrentProcess.Connections[i].SourceNode == SelectedNode || CurrentProcess.Connections[i].TargetNode == SelectedNode)
                    CurrentProcess.Connections.RemoveAt(i);
            }
            CurrentProcess.Nodes.Remove(SelectedNode);
            AddLog($"删除节点: {SelectedNode.DisplayName}");
            SelectedNode = null;
        }

        public void DeleteConnection(ConnectionModel conn)
        {
            if (conn != null && CurrentProcess.Connections.Contains(conn))
            {
                CurrentProcess.Connections.Remove(conn);
                AddLog("删除了连线");
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
        #endregion

        #region DAG 智能拓扑重排
        private void AutoLayout()
        {
            if (CurrentProcess == null || CurrentProcess.Nodes.Count == 0) return;

            var nodes = CurrentProcess.Nodes;
            var connections = CurrentProcess.Connections;

            var inDegree = nodes.ToDictionary(n => n, n => 0);
            var layers = nodes.ToDictionary(n => n, n => 0);

            foreach (var conn in connections)
            {
                if (conn.TargetNode != null && inDegree.ContainsKey(conn.TargetNode))
                    inDegree[conn.TargetNode]++;
            }

            var queue = new Queue<FlowNodeBase>(nodes.Where(n => inDegree[n] == 0));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int currentLayer = layers[current];

                foreach (var conn in connections.Where(c => c.SourceNode == current && c.TargetNode != null))
                {
                    var target = conn.TargetNode;
                    layers[target] = Math.Max(layers[target], currentLayer + 1);
                    inDegree[target]--;
                    if (inDegree[target] == 0) queue.Enqueue(target);
                }
            }

            var layerGroups = nodes.GroupBy(n => layers[n]).OrderBy(g => g.Key).ToList();

            double startY = 80, gapY = 160; // 层级控制 Y 轴
            double startX = 200, gapX = 200; // 同一层节点控制 X 轴

            foreach (var group in layerGroups)
            {
                int layerIndex = group.Key;
                var nodeList = group.ToList();
                double totalWidth = (nodeList.Count - 1) * gapX;
                double layerStartX = startX - (totalWidth / 2.0);

                for (int i = 0; i < nodeList.Count; i++)
                {
                    var node = nodeList[i];
                    // Y 随着 DAG 层级递增（上到下）
                    node.PosY = startY + layerIndex * gapY;
                    // X 轴同一层平铺
                    node.PosX = Math.Max(50, layerStartX + i * gapX);
                }
            }

            foreach (var conn in connections) conn.UpdatePoints();
            AddLog("已完成自上而下的智能拓扑重排。");
        }
        #endregion

        #region 工具箱初始化与元数据绑定

        /// <summary>
        /// 动态构建工具箱 (优先根据扫描到的 DLL 自动推导 ToolBox) 初始化完整流程工具箱，加载插件节点并按分类分组
        /// </summary>
        private void InitFullToolBox()
        {
            ToolBox.Clear();

            // 1. 直接从 Contracts 的 NodeFactory 拿所有注册好的元数据！
            var metas = NodeFactory.GenerateToolboxMetas();
            foreach (var meta in metas)
            {
                meta.CategoryName = GetCategoryDisplayName(meta.Category);
                ToolBox.Add(meta);
            }

            // 2. UI 视图分组更新
            ToolBoxGrouped = CollectionViewSource.GetDefaultView(ToolBox);
            ToolBoxGrouped.GroupDescriptions.Clear();
            ToolBoxGrouped.GroupDescriptions.Add(new PropertyGroupDescription("CategoryName"));
        }

        /// <summary>
        /// 从 NodeMetaRegistry 统一提取分类显示名称
        /// </summary>
        private string GetCategoryDisplayName(NodeCategory category)
        {
            var info = NodeMetaRegistry.Get(category);
            if (info != null)
            {
                return $"{info.Emoji} {info.ShortName}";
            }

            return "其他节点";
        }

        #endregion
    }

    #region 简易 Prompt 弹窗辅助类
    public static class PromptDialog
    {
        public static string Show(string title, string prompt, string defaultValue = "")
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow,
                ResizeMode = ResizeMode.NoResize
            };

            var stack = new StackPanel { Margin = new Thickness(15) };
            stack.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });

            var txtInput = new TextBox { Text = defaultValue, Height = 25, VerticalContentAlignment = VerticalAlignment.Center };
            txtInput.SelectAll();
            stack.Children.Add(txtInput);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            var btnOk = new Button { Content = "确定", Width = 70, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = "取消", Width = 70, Height = 26, IsCancel = true };

            btnOk.Click += (s, e) => { win.DialogResult = true; };
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            stack.Children.Add(btnPanel);

            win.Content = stack;
            return win.ShowDialog() == true ? txtInput.Text : null;
        }
    }
    #endregion
}