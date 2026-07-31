using Grayson.Vison.FlowEdit.Models;
using Microsoft.VisualBasic;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Grayson.Vison.FlowEdit.Execution;


namespace Grayson.Vison.FlowEdit.ViewModels
{
    public class FlowVm : ViewModelBase
    {
        public FlowProcessModel RootProcess { get; set; } = new FlowProcessModel { ProcessName = "主工作流" };
        public ObservableCollection<FlowProcessModel> Breadcrumbs { get; set; } = new ObservableCollection<FlowProcessModel>();

        private FlowProcessModel _currentProcess;
        public FlowProcessModel CurrentProcess { get => _currentProcess; set => Set(ref _currentProcess, value); }

        private FlowNodeBase _selectedNode;
        public FlowNodeBase SelectedNode { get => _selectedNode; set => Set(ref _selectedNode, value); }

        public ObservableCollection<SharedDataItem> WatchData { get; set; } = new ObservableCollection<SharedDataItem>();
        public ObservableCollection<UnitMeta> ToolBox { get; set; } = new ObservableCollection<UnitMeta>();
        public ICollectionView ToolBoxGrouped { get; set; }
        public ObservableCollection<string> ExecutionLogs { get; set; } = new ObservableCollection<string>();

        private bool _isFlowRunning;
        public bool IsFlowRunning { get => _isFlowRunning; set => Set(ref _isFlowRunning, value); }

        public ICommand DeleteNodeCmd { get; }
        public ICommand DeleteConnectionCmd { get; }
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


        // 存放 JSON 配方模板的目录路径
        private readonly string _recipesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes");

        public ExecutionContext Context { get; }
        private FlowExecutor _executor;
        // 新增事件：用于通知 View 层平移画布聚焦当前节点
        public event Action<FlowNodeBase> OnNodeExecuting;
        public FlowVm()
        {
            // 1. 初始化上下文与事件总线监听
            Context = new ExecutionContext();
            Context.OnLogProduced += (s, log) => AddLog(log);
            Context.OnNodeExecuting += (s, node) => OnNodeExecuting?.Invoke(node);
            Context.OnExecutionError += OnGlobalExecutionError;



            // 确保 Recipes 目录存在
            if (!Directory.Exists(_recipesFolderPath))
            {
                Directory.CreateDirectory(_recipesFolderPath);
            }

            InitFull23ToolBox();

            // 动态扫描 Recipes 文件夹并将 .json 配方载入工具箱
            LoadCompositeRecipeTemplates();

            Breadcrumbs.Add(RootProcess);
            CurrentProcess = RootProcess;

            LoadFullDemoProcess();

            // 2. 绑定执行器
            _executor = new FlowExecutor(CurrentProcess, Context);

            // 3. Command 重新映射
            RunContinuousCmd = new RelayCommand(async () => await _executor.RunContinuousAsync());
            StepRunCmd = new RelayCommand(async () => await _executor.StepAsync());
            StopRunCmd = new RelayCommand(() => _executor.Stop());
            PauseRunCmd = new RelayCommand(() => AddLog("⏸ 流程暂停"));


            DeleteNodeCmd = new RelayCommand(DeleteSelectedNode, () => SelectedNode != null);
            DeleteConnectionCmd = new RelayCommand<ConnectionModel>(DeleteConnection);
            SaveRecipeCmd = new RelayCommand(SaveRecipe);
            ImportRecipeCmd = new RelayCommand(ImportRecipe);
            ExportRecipeCmd = new RelayCommand(ExportRecipe);
            NavigateToProcessCmd = new RelayCommand<FlowProcessModel>(NavigateToProcess);
            AutoLayoutCmd = new RelayCommand(AutoLayout);
            SaveCurrentPipelineAsRecipeCommand = new RelayCommand(OnSaveCurrentPipelineAsRecipe);
            ClearCanvasCommand = new RelayCommand(() => ClearCanvas(false));
        }

        #region MyRegion
        /// <summary>
        /// 全局事件总线捕获异常
        /// </summary>
        private void OnGlobalExecutionError(object sender, NodeExecutionErrorEventArgs e)
        {
            // 判定当前流程中是否有异常捕获 (TryCatch) 节点
            var tryCatchNode = CurrentProcess.Nodes.FirstOrDefault(n => n.Type == NodeType.TryCatch && n.Enable);
            if (tryCatchNode != null)
            {
                e.Handled = true; // 标记已处理
                AddLog($"🚨 捕获到节点 [{e.Node.DisplayName}] 的异常: {e.Exception.Message}");
            }
            else
            {
                MessageBox.Show($"节点 [{e.Node.DisplayName}] 抛出未处理异常:\n{e.Exception.Message}",
                                "流程错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void ResetStepProgress()
        {
            _executor?.ResetIndex();
        }

        // 当下钻子流程或切换画布时更新 Executor
        private void OnCurrentProcessChanged()
        {
            _executor?.Stop();
            _executor = new FlowExecutor(CurrentProcess, Context);
        }
        #endregion

        #region 清除画布 (Clear Canvas)
        /// <summary>
        /// 清除当前画布上的所有节点和连线
        /// </summary>
        /// <param name="showConfirm">是否弹出二次确认框（默认弹出）</param>
        public void ClearCanvas(bool showConfirm = true)
        {
            if (CurrentProcess == null) return;

            // 1. 如果画布本身就是空的，无需操作
            if (CurrentProcess.Nodes.Count == 0 && CurrentProcess.Connections.Count == 0)
            {
                return;
            }

            // 2. 弹窗二次确认，防止误操作
            if (showConfirm)
            {
                var result = MessageBox.Show(
                    "确定要清空当前画布上的所有节点和连线吗？此操作无法撤销。",
                    "清空提示",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            try
            {
                // 3. 清空连接线与节点数据
                CurrentProcess.Connections.Clear();
                CurrentProcess.Nodes.Clear();

                // 4. 重置 ViewModel 的选中状态与执行状态
                SelectedNode = null;
                ResetStepProgress(); // 重置单步执行的高亮/进度

                AddLog($"🧹 画布 [{CurrentProcess.ProcessName}] 已清空。");
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ 清空画布时发生异常: {ex.Message}");
            }
        }

        #endregion
        #region 1. 动态读取 /Recipes/ 目录填充工具箱

        /// <summary>
        /// 扫描 /Recipes/ 文件夹，将所有 .json 自动渲染为可拖拽的复合积木
        /// </summary>
        public void LoadCompositeRecipeTemplates()
        {
            try
            {
                if (!Directory.Exists(_recipesFolderPath)) return;

                // 移除原有的复合模板，重新扫描
                var existingComposites = ToolBox.Where(x => x.Category == NodeCategory.CompositeEx && x.NodeId.StartsWith("RECIPE_")).ToList();
                foreach (var item in existingComposites)
                {
                    ToolBox.Remove(item);
                }

                var files = Directory.GetFiles(_recipesFolderPath, "*.json");
                foreach (var file in files)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);

                    ToolBox.Add(new UnitMeta
                    {
                        NodeId = $"RECIPE_{fileName}",
                        DisplayName = $"📦 {fileName}",
                        CategoryName = UnitMeta.GetEnumDescription(NodeCategory.CompositeEx),
                        Category = NodeCategory.CompositeEx,
                        Type = NodeType.CompositeFlow,
                        // 技巧：使用 NodeId 缓存真实文件路径
                        Description = file
                    });
                }

                AddLog($"📂 已加载 {files.Length} 个复合流程模板到工具箱。");
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ 扫描 Recipes 目录失败: {ex.Message}");
            }
        }

        #endregion
        #region 2. 响应从工具箱拖拽 / 双击复合积木到画布

        /// <summary>
        /// 重写模板生成节点逻辑，支持读取 .json 构建 CompositeFlowNode
        /// </summary>
        public  void AddNodeFromMeta(UnitMeta meta, Point pos)
        {
            if (meta.Type == NodeType.CompositeFlow && meta.NodeId.StartsWith("RECIPE_"))
            {
                string jsonFilePath = meta.Description; // 上一步存入的文件路径
                if (File.Exists(jsonFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(jsonFilePath);
                        var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
                        var subProcessModel = JsonConvert.DeserializeObject<FlowProcessModel>(json, settings);

                        var compositeNode = new CompositeFlowNode
                        {
                            DisplayName = meta.DisplayName.Replace("📦 ", ""),
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
                    catch (Exception ex)
                    {
                        MessageBox.Show($"读取复合节点配方失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
            }

            // 非复合 JSON 节点走原有的工厂逻辑
            AddNodeFromMeta2(meta, pos);
        }

        #endregion
        #region 3. 双击“下钻渲染”与“将当前 Pipeline 序列化为模板”
        public static class PromptDialog
        {
            /// <summary>
            /// 弹出一个简单的文本输入框
            /// </summary>
            public static string Show(string title, string prompt, string defaultValue = "")
            {
                var win = new Window
                {
                    Title = title,
                    Width = 360,
                    Height = 170,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Application.Current?.MainWindow,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false
                };

                var stack = new StackPanel { Margin = new Thickness(15) };
                stack.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });

                var txtInput = new TextBox { Text = defaultValue, Height = 25, VerticalContentAlignment = VerticalAlignment.Center };
                txtInput.SelectAll();
                stack.Children.Add(txtInput);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 15, 0, 0)
                };

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
        private void OnSaveCurrentPipelineAsRecipe()
        {
            string defaultName = CurrentProcess?.ProcessName ?? "新复合配方";

            // 调用简易弹窗
            string recipeName = PromptDialog.Show("保存为复合模板", "请输入要导出的配方名称：", defaultName);

            // 用户点击了取消或输入的为空
            if (string.IsNullOrWhiteSpace(recipeName)) return;

            // 保存并刷新工具箱
            SaveCurrentPipelineAsRecipe(recipeName.Trim());

            MessageBox.Show($"配方模板 [{recipeName}] 已成功保存到 AppRoot/Recipes/！",
                            "保存成功",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
        }

        #endregion

        /// <summary>
        /// 将当前画布的 Pipeline 一键 Serialize 导出为 .json 模板文件
        /// </summary>
        public void SaveCurrentPipelineAsRecipe(string recipeName)
        {
            if (string.IsNullOrWhiteSpace(recipeName)) return;

            string filePath = Path.Combine(_recipesFolderPath, $"{recipeName}.json");
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                Formatting = Formatting.Indented
            };

            string json = JsonConvert.SerializeObject(CurrentProcess, settings);
            File.WriteAllText(filePath, json);

            AddLog($"💾 当前流程已保存为模板: {filePath}");

            // 重新刷新左侧工具箱
            LoadCompositeRecipeTemplates();
        }
        /// <summary>
        /// 双击下钻：将 DataContext 的 CurrentProcess 临时切换为该子节点的 InnerPipeline
        /// </summary>
        public void DrillDownCompositeNode(CompositeFlowNode compositeNode)
        {
            if (compositeNode?.SubProcess != null)
            {
                // 1. 切换当前的 FlowProcessModel
                CurrentProcess = compositeNode.SubProcess;

                // 2. 更新面包屑 Breadcrumbs 导航
                if (!Breadcrumbs.Contains(CurrentProcess))
                {
                    Breadcrumbs.Add(CurrentProcess);
                }

                SelectedNode = null;
                ResetStepProgress();
                AddLog($"🔍 已经下钻进入子流程: [{CurrentProcess.ProcessName}]");
            }
        }



        /// <summary>
        /// 完整注入 6 大类、全部 23 个工业视觉节点工具箱
        /// </summary>
        private void InitFull23ToolBox()
        {
            // 0. 🛑 复合子流程与异常处理类 (Composite & Exception)
            ToolBox.Add(new UnitMeta { NodeId = "Comp_Try", DisplayName = "🛡️ 异常捕获 (Try Catch)", CategoryName = "🛑 复合子流程与异常处理类", Category = NodeCategory.CompositeEx, Type = NodeType.TryCatch });
            ToolBox.Add(new UnitMeta { NodeId = "Comp_End", DisplayName = "🛑 流程终止 (End/Terminate)", CategoryName = "🛑 复合子流程与异常处理类", Category = NodeCategory.CompositeEx, Type = NodeType.TerminateFlow });
            // 1. ⚙️ 设备与 IO 控制类 (Device & I/O)
            ToolBox.Add(new UnitMeta { NodeId = "Dev_Cam", DisplayName = "📷 相机采集 (Acquire Image)", CategoryName = "⚙️ 设备与 IO 控制类", Category=NodeCategory.DeviceIO, Type = NodeType.AcquireImage });
            ToolBox.Add(new UnitMeta { NodeId = "Dev_PLC", DisplayName = "🔌 PLC 读写 (PLC Read/Write)", CategoryName = "⚙️ 设备与 IO 控制类", Category = NodeCategory.DeviceIO, Type = NodeType.PlcReadWrite });
            ToolBox.Add(new UnitMeta { NodeId = "Dev_Axis", DisplayName = "🚚 运动轴移动 (Axis Move)", CategoryName = "⚙️ 设备与 IO 控制类", Category = NodeCategory.DeviceIO, Type = NodeType.AxisMove });
            ToolBox.Add(new UnitMeta { NodeId = "Dev_IO", DisplayName = "🚨 数字 IO 输出 (Digital Output)", CategoryName = "⚙️ 设备与 IO 控制类", Category = NodeCategory.DeviceIO, Type = NodeType.DigitalOutput });
            ToolBox.Add(new UnitMeta { NodeId = "Dev_Light", DisplayName = "💡 光照控制 (Light Controller)", CategoryName = "⚙️ 设备与 IO 控制类", Category = NodeCategory.DeviceIO, Type = NodeType.LightControl });

            // 2. 👁️ Halcon 算法与视觉处理类 (Vision Processing)
            ToolBox.Add(new UnitMeta { NodeId = "Vis_Match", DisplayName = "🎯 模板匹配 (Template Matching)", CategoryName = "👁️ Halcon 算法与视觉处理类", Category = NodeCategory.Vision, Type = NodeType.TemplateMatch });
            ToolBox.Add(new UnitMeta { NodeId = "Vis_Calib", DisplayName = "📐 九点/手眼标定 (Calib 2D)", CategoryName = "👁️ Halcon 算法与视觉处理类", Category = NodeCategory.Vision, Type = NodeType.Calib2D });
            ToolBox.Add(new UnitMeta { NodeId = "Vis_Measure", DisplayName = "📏 几何测量 (Measurement)",  CategoryName = "👁️ Halcon 算法与视觉处理类", Category = NodeCategory.Vision, Type = NodeType.Measurement });
            ToolBox.Add(new UnitMeta { NodeId = "Vis_Defect", DisplayName = "🔍 缺陷检测 (Defect Detection)", CategoryName = "👁️ Halcon 算法与视觉处理类", Category = NodeCategory.Vision, Type = NodeType.DefectDetect });
            ToolBox.Add(new UnitMeta { NodeId = "Vis_Barcode", DisplayName = "🏁 条码/二维码识别 (Read Barcode)", CategoryName = "👁️ Halcon 算法与视觉处理类", Category = NodeCategory.Vision, Type = NodeType.ReadBarcode });
            ToolBox.Add(new UnitMeta { NodeId = "Vis_DL", DisplayName = "🧠 深度学习推理 (DL Inference)", CategoryName = "👁️ Halcon 算法与视觉处理类", Category = NodeCategory.Vision, Type = NodeType.DlInference });

            // 3. 🧠 逻辑控制与数据流类 (Logic & Control)
            ToolBox.Add(new UnitMeta { NodeId = "Log_If", DisplayName = "🔀 条件分支 (If/Else Branch)",  CategoryName = "🧠 逻辑控制与数据流类", Category = NodeCategory.Logic, Type = NodeType.ConditionIf });
            ToolBox.Add(new UnitMeta { NodeId = "Log_Switch", DisplayName = "🔀 多路分支 (Switch Case)",  CategoryName = "🧠 逻辑控制与数据流类", Category = NodeCategory.Logic, Type = NodeType.SwitchCase });
            ToolBox.Add(new UnitMeta { NodeId = "Log_Loop", DisplayName = "🔁 循环控制 (For/While Loop)", CategoryName = "🧠 逻辑控制与数据流类", Category = NodeCategory.Logic, Type = NodeType.ForLoop });
            ToolBox.Add(new UnitMeta { NodeId = "Log_Delay", DisplayName = "⏱️ 延时等待 (Sleep/Wait)",  CategoryName = "🧠 逻辑控制与数据流类", Category = NodeCategory.Logic, Type = NodeType.Delay });
            ToolBox.Add(new UnitMeta { NodeId = "Log_WaitSig", DisplayName = "⏳ 状态信号等待 (Wait Signal)",  CategoryName = "🧠 逻辑控制与数据流类", Category = NodeCategory.Logic,  Type = NodeType.WaitSignal });

            // 4. 📊 数据处理与转换类 (Data Transformation)
            ToolBox.Add(new UnitMeta { NodeId = "Data_Offset", DisplayName = "🧭 坐标计算/偏移 (Offset Math)",  CategoryName = "📊 数据处理与转换类", Category = NodeCategory.DataProcess, Type = NodeType.OffsetMath });
            ToolBox.Add(new UnitMeta { NodeId = "Data_Script", DisplayName = "🧮 公式计算 (Script/Math)",  CategoryName = "📊 数据处理与转换类", Category = NodeCategory.DataProcess, Type = NodeType.ScriptMath });
            ToolBox.Add(new UnitMeta { NodeId = "Data_Map", DisplayName = "🔗 变量映射 (Var Mapper)",  CategoryName = "📊 数据处理与转换类", Category = NodeCategory.DataProcess, Type = NodeType.VarMapper });
            ToolBox.Add(new UnitMeta { NodeId = "Data_StrFormat", DisplayName = "📝 字符串格式化 (String Format)",  CategoryName = "📊 数据处理与转换类", Category = NodeCategory.DataProcess, Type = NodeType.StringFormat });

            // 5. 🏭 生产与数据对接类 (Factory Integration)
            ToolBox.Add(new UnitMeta { NodeId = "Sys_MES", DisplayName = "🌐 MES 上报 (MES Report)",  CategoryName = "🏭 生产与数据对接类", Category = NodeCategory.SystemMES, Type = NodeType.MesReport });
            ToolBox.Add(new UnitMeta { NodeId = "Sys_SaveData", DisplayName = "💾 数据存盘 (Save Data)",  CategoryName = "🏭 生产与数据对接类", Category = NodeCategory.SystemMES, Type = NodeType.SaveData });
            ToolBox.Add(new UnitMeta { NodeId = "Sys_SaveImg", DisplayName = "🖼️ 图像保存 (Save Image)",  CategoryName = "🏭 生产与数据对接类", Category = NodeCategory.SystemMES, Type = NodeType.SaveImage });

      

            ToolBoxGrouped = CollectionViewSource.GetDefaultView(ToolBox);
            ToolBoxGrouped.GroupDescriptions.Add(new PropertyGroupDescription("CategoryName"));
        }

        /// <summary>
        /// 构建全流程标准的 Demo 链条
        /// </summary>
        private void LoadFullDemoProcess()
        {
            CurrentProcess.Nodes.Clear();
            CurrentProcess.Connections.Clear();

            // ==========================================
            // 1. 创建节点实例
            // ==========================================
            var n1 = CreateNodeInstance(NodeType.WaitSignal, "等待 PLC 触发", NodeCategory.DeviceIO, new Point(50, 100));
            var n2 = CreateNodeInstance(NodeType.ReadBarcode, "产品条码识别", NodeCategory.DeviceIO, new Point(270, 100));
            var n3 = CreateNodeInstance(NodeType.AcquireImage, "顶部相机采图", NodeCategory.DeviceIO, new Point(490, 100));
            var n4 = CreateNodeInstance(NodeType.TemplateMatch, "Mark点形状匹配", NodeCategory.Vision, new Point(710, 100));
            var n5 = CreateNodeInstance(NodeType.ConditionIf, "定位是否成功?", NodeCategory.Vision, new Point(930, 100));

            // True 分支 -> 标定计算 -> 数据存盘 -> MES上报
            var n6a = CreateNodeInstance(NodeType.Calib2D, "手眼标定映射", NodeCategory.Vision, new Point(1170, 30));
            var n7a = CreateNodeInstance(NodeType.SaveData, "OK 数据存盘", NodeCategory.DataProcess, new Point(1390, 30));
            var n8a = CreateNodeInstance(NodeType.MesReport, "MES 上报过站", NodeCategory.DataProcess, new Point(1610, 30));

            // False 分支 -> 气缸剔除 -> 报警终止
            var n6b = CreateNodeInstance(NodeType.DigitalOutput, "吹气气缸剔除", NodeCategory.DataProcess, new Point(1170, 200));
            var n7b = CreateNodeInstance(NodeType.TerminateFlow, "流程终止报警", NodeCategory.Logic, new Point(1390, 200));

            CurrentProcess.Nodes.Add(n1); CurrentProcess.Nodes.Add(n2); CurrentProcess.Nodes.Add(n3);
            CurrentProcess.Nodes.Add(n4); CurrentProcess.Nodes.Add(n5); CurrentProcess.Nodes.Add(n6a);
            CurrentProcess.Nodes.Add(n7a); CurrentProcess.Nodes.Add(n8a); CurrentProcess.Nodes.Add(n6b);
            CurrentProcess.Nodes.Add(n7b);

            // ==========================================
            // 2. 建立【 Exec 控制流】连线 (触发顺序)
            // ==========================================
            // 主干 Exec 顺序控制
            ConnectByName(n1, "Exec", n2, "Exec"); // WaitSignal -> ReadBarcode
            ConnectByName(n2, "Exec", n3, "Exec"); // ReadBarcode -> AcquireImage
            ConnectByName(n3, "Exec", n4, "Exec"); // AcquireImage -> TemplateMatch
            ConnectByName(n4, "Exec", n5, "Exec"); // TemplateMatch -> ConditionIf

            // ConditionIf 条件分支 Exec 连线
            ConnectByName(n5, "True", n6a, "Exec");  // 成功分支 -> Calib2D
            ConnectByName(n6a, "Exec", n7a, "Exec"); // Calib2D -> SaveData
            ConnectByName(n7a, "Exec", n8a, "Exec"); // SaveData -> MesReport

            ConnectByName(n5, "False", n6b, "Exec"); // 失败分支 -> DigitalOutput
            ConnectByName(n6b, "Exec", n7b, "Exec"); // DigitalOutput -> TerminateFlow

            // ==========================================
            // 3. 建立【 Data 数据流】连线 (数据依赖传递)
            // ==========================================
            // 相机输出 Image -> 模板匹配输入 Image
            ConnectByName(n3, "Image", n4, "Image");

            // 模板匹配输出 PosX/PosY -> 标定映射输入 RawX/RawY
            ConnectByName(n4, "MatchX", n6a, "RawX");
            ConnectByName(n4, "MatchY", n6a, "RawY");

            // 模板匹配输出 IsSuccess -> 条件分支判断输入
            ConnectByName(n4, "IsSuccess", n5, "Condition");

            // 扫码输出 Barcode -> MES 上报与数据存盘输入
            ConnectByName(n2, "Barcode", n7a, "Data1");
            ConnectByName(n2, "Barcode", n8a, "Barcode");

            // 标定后物理坐标 -> MES 上报
            ConnectByName(n6a, "WorldX", n8a, "PosX");
            ConnectByName(n6a, "WorldY", n8a, "PosY");

            // ==========================================
            // 4. Mock Watch 监视数据与日志
            // ==========================================
            WatchData.Clear();
            WatchData.Add(new SharedDataItem { Key = "BarCode", Value = "SN_20260725_0088" });
            WatchData.Add(new SharedDataItem { Key = "MatchScore", Value = 0.92 });
            WatchData.Add(new SharedDataItem { Key = "Robot_X", Value = 152.34 });

            AddLog("全量控制流 (Exec) 与数据流 (Data) Demo 配方加载完毕。");
        }

        #region 控制是否显示数据端口
        private bool _showDataPorts = false; // 默认显示，或设为 false 默认隐藏
        public bool ShowDataPorts
        {
            get => _showDataPorts;
            set
            {
                if (_showDataPorts != value)
                {
                    _showDataPorts = value;
                    OnPropertyChanged(nameof(ShowDataPorts));
                    AddLog(value ? "已开启【数据端口】显示" : "已隐藏【数据端口】，仅保留控制流端口");
                }
            }
        }
        public ICommand ToggleShowDataPortsCommand => new RelayCommand(() => ShowDataPorts = !ShowDataPorts);
        #endregion

        /// <summary>
        /// 辅助方法：通过端口名称找到 NodePort 后，调用原生的 AddConnection
        /// </summary>
        private void ConnectByName(FlowNodeBase source, string sourcePortName, FlowNodeBase target, string targetPortName)
        {
            if (source == null || target == null) return;

            // 1. 精确匹配端口名称
            var sourcePort = source.OutputPorts.FirstOrDefault(p => p.PortName.Equals(sourcePortName, StringComparison.OrdinalIgnoreCase));
            var targetPort = target.InputPorts.FirstOrDefault(p => p.PortName.Equals(targetPortName, StringComparison.OrdinalIgnoreCase));

            // 2. 兜底逻辑：如果是 Exec 且没匹配到，取第一个 Exec 端口
            if (sourcePort == null && sourcePortName == "Exec")
                sourcePort = source.OutputPorts.FirstOrDefault(p => p.Category == PortCategory.Exec);
            if (targetPort == null && targetPortName == "Exec")
                targetPort = target.InputPorts.FirstOrDefault(p => p.Category == PortCategory.Exec);

            // 3. 执行连线
            if (sourcePort != null && targetPort != null)
            {
                AddConnection(source, sourcePort, target, targetPort);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ [连线匹配失败] 源节点:{source.DisplayName}(寻找:{sourcePortName}) -> 目标节点:{target.DisplayName}(寻找:{targetPortName})");
            }
        }

        #region 工厂构建节点与参数实例化
        public FlowNodeBase CreateNodeInstance(NodeType type, string name, NodeCategory nodeCategory, Point position, string description = "", object parameterModel = null)
        {
            FlowNode node = new FlowNode(type, name, nodeCategory, position, description, parameterModel);
            node.PosX = position.X;
            node.PosY = position.Y;

            // 分配 23 个专属参数模型 Model
            switch (type)
            {
                // --- 1. 设备与 IO 控制类 ---
                case NodeType.AcquireImage: node.ParameterModel = new AcquireImageParam(); node.Category = NodeCategory.DeviceIO; break;
                case NodeType.PlcReadWrite: node.ParameterModel = new PlcReadWriteParam(); node.Category = NodeCategory.DeviceIO; break;
                case NodeType.AxisMove: node.ParameterModel = new AxisMoveParam(); node.Category = NodeCategory.DeviceIO; break;
                case NodeType.DigitalOutput: node.ParameterModel = new DigitalIoParam(); node.Category = NodeCategory.DeviceIO; break; // 对应 DigitalIoParam
                case NodeType.LightControl: node.ParameterModel = new LightControlParam(); node.Category = NodeCategory.DeviceIO; break;

                // --- 2. Halcon 算法与视觉处理类 ---
                case NodeType.TemplateMatch: node.ParameterModel = new TemplateMatchParam(); node.Category = NodeCategory.Vision; break;
                case NodeType.Calib2D: node.ParameterModel = new Calib2DParam(); node.Category = NodeCategory.Vision; break;
                case NodeType.Measurement: node.ParameterModel = new GeometryMeasureParam(); node.Category = NodeCategory.Vision; break; // 对应 GeometryMeasureParam
                case NodeType.DefectDetect: node.ParameterModel = new AiDefectDetectParam(); node.Category = NodeCategory.Vision; break; // 对应 AiDefectDetectParam
                case NodeType.ReadBarcode: node.ParameterModel = new ReadBarcodeParam(); node.Category = NodeCategory.Vision; break;
                case NodeType.DlInference: node.ParameterModel = new AiClassifyParam(); node.Category = NodeCategory.Vision; break; // 对应 AiClassifyParam

                // --- 3. 逻辑控制与数据流类 ---
                case NodeType.ConditionIf: node.ParameterModel = new ConditionIfParam(); node.Category = NodeCategory.Logic; break;
                case NodeType.SwitchCase: node.ParameterModel = new SwitchCaseParam(); node.Category = NodeCategory.Logic; break;
                case NodeType.ForLoop: node.ParameterModel = new LoopForParam(); node.Category = NodeCategory.Logic; break; // 对应 LoopForParam
                case NodeType.Delay: node.ParameterModel = new DelayParam(); node.Category = NodeCategory.Logic; break;
                case NodeType.WaitSignal: node.ParameterModel = new WaitSignalParam(); node.Category = NodeCategory.Logic; break;

                // --- 4. 数据处理与转换类 ---
                case NodeType.OffsetMath: node.ParameterModel = new OffsetMathParam(); node.Category = NodeCategory.DataProcess; break;
                case NodeType.ScriptMath: node.ParameterModel = new ScriptMathParam(); node.Category = NodeCategory.DataProcess; break;
                case NodeType.VarMapper: node.ParameterModel = new VarMapperParam(); node.Category = NodeCategory.DataProcess; break;
                case NodeType.StringFormat: node.ParameterModel = new StringFormatParam(); node.Category = NodeCategory.DataProcess; break;

                // --- 5. 生产与数据对接类 ---
                case NodeType.MesReport: node.ParameterModel = new MesReportParam(); node.Category = NodeCategory.SystemMES; break;
                case NodeType.SaveData: node.ParameterModel = new SaveDataParam(); node.Category = NodeCategory.SystemMES; break;
                case NodeType.SaveImage: node.ParameterModel = new SaveImageParam(); node.Category = NodeCategory.SystemMES; break;

                // --- 6. 复合子流程与异常处理类 ---
                case NodeType.CompositeFlow: node.ParameterModel = new CompositeFlowParam(); node.Category = NodeCategory.CompositeEx; break;
                case NodeType.TryCatch: node.ParameterModel = new TryCatchParam(); node.Category = NodeCategory.CompositeEx; break;
                case NodeType.TerminateFlow: node.ParameterModel = new TerminateFlowParam(); node.Category = NodeCategory.CompositeEx; break;
            }

            // 1. 绝大多数节点都有 1 个默认输入端口
            node.InputPorts.Add(new NodePort { PortName = "In", PortType = PortType.In, Connector = ConnectorType.Input, RelativeY = 35 });

            // 2. 根据节点类型，按需配置输出端口
            switch (type)
            {
                // --- 逻辑条件 & 视觉判定节点：分配 OK / NG (True / False) 双输出端口 ---
                case NodeType.ConditionIf:
                case NodeType.TemplateMatch:
                case NodeType.DefectDetect:
                    node.OutputPorts.Add(new NodePort { PortName = "OK (True)", PortType = PortType.Out, Connector = ConnectorType.OutputTrue, RelativeY = 18, ColorHex = "#2ECC71" });
                    node.OutputPorts.Add(new NodePort { PortName = "NG (False)", PortType = PortType.Out, Connector = ConnectorType.OutputFalse, RelativeY = 52, ColorHex = "#E74C3C" });
                    break;

                // --- 多路分支 Switch 节点：支持多个分支端口 ---
                case NodeType.SwitchCase:
                    node.OutputPorts.Add(new NodePort { PortName = "Case 1", PortType = PortType.Out, Connector = ConnectorType.OutputDefault, RelativeY = 15, ColorHex = "#3498DB" });
                    node.OutputPorts.Add(new NodePort { PortName = "Case 2", PortType = PortType.Out, Connector = ConnectorType.OutputDefault, RelativeY = 35, ColorHex = "#3498DB" });
                    node.OutputPorts.Add(new NodePort { PortName = "Default", PortType = PortType.Out, Connector = ConnectorType.OutputDefault, RelativeY = 55, ColorHex = "#95A5A6" });
                    break;

                // --- 终止节点：无输出端口 ---
                case NodeType.TerminateFlow:
                    // 不添加任何 OutputPort
                    break;

                // --- 普通动作节点 (相机采图, PLC读写, 延时等)：单输出端口 ---
                default:
                    node.OutputPorts.Add(new NodePort { PortName = "Out", PortType = PortType.Out, Connector = ConnectorType.OutputDefault, RelativeY = 35, ColorHex = "#007ACC" });
                    break;
            }

            // 1. 默认控制输入端口 (Exec In)
            if (type != NodeType.WaitSignal) // 某些起始节点可根据需求决定是否需要 Exec In
            {
                node.InputPorts.Add(new NodePort
                {
                    PortName = "Exec",
                    PortType = PortType.In,
                    Category = PortCategory.Exec,
                    Connector = ConnectorType.Input,
                    RelativeY = 20
                });
            }

            // 2. 根据节点类型添加【控制端口】与【数据端口】
            switch (type)
            {
                case NodeType.AcquireImage:
                    // 控制输出
                    //node.OutputPorts.Add(new NodePort { PortName = "Exec", PortType = PortType.Out, Category = PortCategory.Exec, RelativeY = 20, ColorHex = "#007ACC" });

                    // 数据输出：图像数据
                    node.OutputPorts.Add(new NodePort { PortName = "Image", PortType = PortType.Out, Category = PortCategory.Data, DataType = "HImage", RelativeY = 45, ColorHex = "#E67E22" });
                    break;

                case NodeType.TemplateMatch:
                    // 数据输入：依赖上一节点的图像
                    node.InputPorts.Add(new NodePort { PortName = "Image", PortType = PortType.In, Category = PortCategory.Data, DataType = "HImage", RelativeY = 45, ColorHex = "#E67E22" });

                    // 控制输出 (OK / NG)
                    //node.OutputPorts.Add(new NodePort { PortName = "OK", PortType = PortType.Out, Category = PortCategory.Exec, Connector = ConnectorType.OutputTrue, RelativeY = 20, ColorHex = "#2ECC71" });
                    //node.OutputPorts.Add(new NodePort { PortName = "NG", PortType = PortType.Out, Category = PortCategory.Exec, Connector = ConnectorType.OutputFalse, RelativeY = 40, ColorHex = "#E74C3C" });

                    // 数据输出：匹配出的 Pose/Coordinates
                    node.OutputPorts.Add(new NodePort { PortName = "Pose", PortType = PortType.Out, Category = PortCategory.Data, DataType = "Point3D", RelativeY = 60, ColorHex = "#9B59B6" });
                    break;

                default:
                    // 普通节点默认配置
                    //node.OutputPorts.Add(new NodePort { PortName = "Exec", PortType = PortType.Out, Category = PortCategory.Exec, Connector = ConnectorType.OutputDefault, RelativeY = 20, ColorHex = "#007ACC" });
                    break;
            }

            return node;
        }

        public void AddNodeFromMeta2(UnitMeta meta, Point pos)
        {
            var node = CreateNodeInstance(meta.Type, meta.DisplayName,meta.Category, pos);
            CurrentProcess.Nodes.Add(node);
            SelectedNode = node;
            AddLog($"新增节点: {node.DisplayName}");
        }
        #endregion

        #region 运行与基本控制逻辑
        #region 2. 运行高亮与视图平移支持
        private int _currentStepIndex = 0; // 记录当前单步执行到第几个节点

       


        /// <summary>
        /// 抽取公共的单节点执行方法 (包含高亮、聚焦与耗时模拟)
        /// </summary>
        private async Task ExecuteSingleNodeAsync(FlowNodeBase node)
        {
            node.IsRunning = true;

            // 1. 触发 View 层自动聚焦跟焦
            OnNodeExecuting?.Invoke(node);

            AddLog($"[执行中] 节点: {node.DisplayName} (类型: {node.Type})");

            // 2. 模拟真实算法/图像处理耗时
            await Task.Delay(800);

            node.IsRunning = false;
            AddLog($"[完成] 节点: {node.DisplayName}");
        }



        #endregion

        private void StopFlow()
        {
            IsFlowRunning = false;
            foreach (var node in CurrentProcess.Nodes) node.IsRunning = false;
            AddLog("⏹ 流程被强制停止。");
        }

        public void AddLog(string log) => ExecutionLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {log}");

        private void SaveRecipe() { AddLog("💾 配方已保存。"); MessageBox.Show("配方参数已成功保存！", "提示"); }

        private void ExportRecipe()
        {
            SaveFileDialog sfd = new SaveFileDialog { Filter = "Recipe File (*.json)|*.json", FileName = "VisionStationRecipe.json" };
            if (sfd.ShowDialog() == true)
            {
                string json = JsonConvert.SerializeObject(RootProcess, Formatting.Indented, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
                File.WriteAllText(sfd.FileName, json);
                AddLog($"📤 配方成功导出至: {sfd.FileName}");
            }
        }

        private void ImportRecipe()
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "Recipe File (*.json)|*.json" };
            if (ofd.ShowDialog() == true)
            {
                string json = File.ReadAllText(ofd.FileName);
                var process = JsonConvert.DeserializeObject<FlowProcessModel>(json, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
                if (process != null)
                {
                    RootProcess = process;
                    Breadcrumbs.Clear();
                    Breadcrumbs.Add(RootProcess);
                    CurrentProcess = RootProcess;
                    AddLog($"📥 成功导入配方文件: {ofd.FileName}");
                }
            }
        }

        /// <summary>
        /// 支持具体端口 (NodePort) 的连线建立方法
        /// </summary>
        public void AddConnection(FlowNodeBase source, NodePort sourcePort, FlowNodeBase target, NodePort targetPort)
        {
            if (source == null || target == null || source == target || sourcePort == null || targetPort == null) return;

            // 1. 不能将输入接到输入，或输出接到输出
            if (sourcePort.PortType == targetPort.PortType) return;

            // 2. 类型检查：控制端口只能连接控制端口，数据端口只能连接数据端口
            if (sourcePort.Category != targetPort.Category)
            {
                AddLog($"⚠️ 连线失败：不能将 [{sourcePort.Category}] 端口与 [{targetPort.Category}] 端口相连。");
                return;
            }

            // 3. 数据类型匹配检查 (如 HImage 对应 HImage)
            if (sourcePort.Category == PortCategory.Data)
            {
                if (sourcePort.DataType != targetPort.DataType && targetPort.DataType != "object")
                {
                    AddLog($"⚠️ 连线失败：数据类型不匹配 ({sourcePort.DataType} -> {targetPort.DataType})。");
                    return;
                }
            }

            var connection = new ConnectionModel(source, sourcePort, target, targetPort);
            CurrentProcess.Connections.Add(connection);

            AddLog($"建立连线: {source.DisplayName} [{sourcePort.PortName}] -> {target.DisplayName} [{targetPort.PortName}]");
        }
        /// <summary>
        /// 兼容旧版调用的重载方法（若无具体 Port，默认取第一个/匹配类型端口）
        /// </summary>
        public void AddConnection(FlowNodeBase source, ConnectorType sourceType, FlowNodeBase target)
        {
            if (source == null || target == null || source == target) return;

            // 寻找匹配 ConnectorType 的端口
            NodePort sPort = null;
            foreach (var p in source.OutputPorts)
            {
                if (p.Connector == sourceType) { sPort = p; break; }
            }
            sPort = source.OutputPorts.Count > 0 ? source.OutputPorts[0] : null;

            NodePort tPort = target.InputPorts.Count > 0 ? target.InputPorts[0] : null;

            AddConnection(source, sPort, target, tPort);
        }

        public void DeleteConnection(ConnectionModel conn)
        {

            if (conn != null && CurrentProcess.Connections.Contains(conn))
            {
                CurrentProcess.Connections.Remove(conn);
                AddLog("删除了连线");
            }
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


        #region 1. 智能思维导图/DAG 拓扑重排算法
        private void AutoLayout()
        {
            if (CurrentProcess == null || CurrentProcess.Nodes.Count == 0) return;

            var nodes = CurrentProcess.Nodes;
            var connections = CurrentProcess.Connections;

            // 1. 计算每个节点的入度 (In-degree)
            var inDegree = new Dictionary<FlowNodeBase, int>();
            var layers = new Dictionary<FlowNodeBase, int>();
            foreach (var node in nodes)
            {
                inDegree[node] = 0;
                layers[node] = 0;
            }

            foreach (var conn in connections)
            {
                if (conn.TargetNode != null && inDegree.ContainsKey(conn.TargetNode))
                {
                    inDegree[conn.TargetNode]++;
                }
            }

            // 2. 拓扑分层 (Topological Layering)
            var queue = new Queue<FlowNodeBase>();
            foreach (var node in nodes)
            {
                if (inDegree[node] == 0) // 入口节点（如等待信号、相机采集）
                {
                    queue.Enqueue(node);
                    layers[node] = 0;
                }
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int currentLayer = layers[current];

                foreach (var conn in connections)
                {
                    if (conn.SourceNode == current && conn.TargetNode != null)
                    {
                        var target = conn.TargetNode;
                        // 目标节点的层级 = Max(现有层级, 当前节点层级 + 1)
                        layers[target] = Math.Max(layers[target], currentLayer + 1);

                        inDegree[target]--;
                        if (inDegree[target] == 0)
                        {
                            queue.Enqueue(target);
                        }
                    }
                }
            }

            // 3. 按层级分组 (Group By Layer)
            var layerGroups = nodes.GroupBy(n => layers[n]).OrderBy(g => g.Key).ToList();

            double startX = 80;    // 起始 X 坐标
            double gapX = 240;     // 横向层级间距
            double gapY = 110;     // 纵向节点间距
            double startY = 150;   // 基准 Y 坐标

            // 4. 树状/思维导图布局计算
            foreach (var group in layerGroups)
            {
                int layerIndex = group.Key;
                var nodeList = group.ToList();
                int count = nodeList.Count;

                // 计算该列整体高度并垂直居中
                double totalHeight = (count - 1) * gapY;
                double layerStartY = startY - (totalHeight / 2.0);

                for (int i = 0; i < count; i++)
                {
                    var node = nodeList[i];
                    node.PosX = startX + layerIndex * gapX;
                    node.PosY = Math.Max(50, layerStartY + i * gapY); // 确保不会小于 Canvas 上边界
                }
            }

            // 5. 刷新所有连线的起点与终点坐标
            foreach (var conn in connections)
            {
                conn.UpdatePoints();
            }

            AddLog("✨ 已完成智能树状拓扑重排。");
        }
        #endregion
        #endregion

        #region 响应工具箱双击或拖拽放置
        /// <summary>
        /// 响应工具箱双击或拖拽放置，生成带有完整中文注释与真实默认参数的节点
        /// </summary>
        public void AddNodeFromTemplate(UnitMeta meta, Point position)
        {
            if (CurrentProcess == null) return;

            FlowNodeBase newNode = CreateNodeInstance(meta.Type,meta.DisplayName,meta.Category,position);



            switch (meta.NodeId)
            {
                // 1. 采集
                case "ACQ_CAMERA":
                    newNode.Description = "驱动工业相机获取 2D 图像。";
                    newNode.ParameterModel = new AcquireImageParam();

                    break;
                case "ACQ_FILE":
                    newNode.Description = "从磁盘文件加载图像进行离线调试。";
                    newNode.ParameterModel = new LoadImageParam();
                      break;
                case "ACQ_3D":
                    newNode.Description = "从 3D 轮廓仪/相机获取 Depth 点云数据。";
                    newNode.ParameterModel = new AcquirePointCloudParam();
                    break;

                // 2. 预处理
                case "PROC_FILTER":
                    newNode.Description = "高斯滤波、中值滤波或灰度增强。";
                    newNode.ParameterModel = new ImagePreprocessParam();
                    break;
                case "PROC_ROI":
                    newNode.Description = "设定感兴趣区域，并可绑定跟跟随矩阵。";
                    newNode.ParameterModel = new RoiCropParam();
                    break;

                // 3. 定位测量
                case "MATCH_SHAPE":
                    newNode.Description = "基于边缘特征查找目标，返回 X,Y,Angle。";
                    newNode.ParameterModel = new TemplateMatchParam();

                    break;
                case "MEAS_CALIPER":
                    newNode.Description = "沿指定方向提取边缘点并拟合直线/圆。";
                    newNode.ParameterModel = new CaliperParam();
                    break;
                case "MEAS_BLOB":
                    newNode.Description = "基于二值化分析连通域面积、质心。";
                    newNode.ParameterModel = new BlobAnalysisParam();
                    break;
                case "MEAS_GEO":
                    newNode.Description = "计算点线距离、线线夹角及公差判定。";
                    newNode.ParameterModel = new GeometryMeasureParam();
                    break;

                // 4. 识别标定
                case "CODE_READ":
                    newNode.Description = "读取 QR Code, DataMatrix, Code128 等字符串。";
                    newNode.ParameterModel = new ReadBarcodeParam();

                    break;
                case "OCR_READ":
                    newNode.Description = "识别工业字符、喷码或刻字。";
                    newNode.ParameterModel = new OcrReadParam();
                    break;

                // 5. AI
                case "AI_DEFECT":
                    newNode.Description = "利用深度学习推理目标缺陷与位置";
                    newNode.ParameterModel = new AiDefectDetectParam();
                    break;
                case "AI_CLASS":
                    newNode.Description = "判断图像类别及合格分数";
                    newNode.ParameterModel = new AiClassifyParam();
                    break;

                // 6. 控制逻辑
                case "COND_IF":
                    newNode.Description = "比对 SharedData 变量值决定流程分支。";
                    newNode.ParameterModel = new ConditionIfParam();
                    break;
                case "LOOP_FOR":
                    newNode.Description = "循环迭代执行子流，支持索引计数。";
                    newNode.ParameterModel = new LoopForParam();
                    break;
                case "CTRL_BREAK":
                    newNode.Description = "立即中断并退出当前最外层 Loop。";
                    newNode.ParameterModel = new BreakParam();
                    break;
                case "CTRL_DELAY":
                    newNode.Description = "阻塞线程指定毫秒数。";
                    newNode.ParameterModel = new DelayParam();
                    break;

                // 7. 通讯
                case "COMM_PLC":
                    newNode.Description = "与西门子/三菱/Modbus PLC 交换寄存器数据。";
                    newNode.ParameterModel = new PlcReadWriteParam();
                    break;
                case "COMM_SOCKET":
                    newNode.Description = "通过 TCP/IP 协议收发自定义格式字符串。";
                    newNode.ParameterModel = new SocketCommParam();

                    break;
                case "COMM_IO":
                    newNode.Description = "控制 PCIe/USB IO 卡输出高低电平或触发脉冲。";
                    newNode.ParameterModel = new DigitalIoParam();
                    break;

                // 8. MES 与存盘
                case "DATA_MES":
                    newNode.Description = "通过 HTTP Restful API 将检测结果推送到 MES。";
                    newNode.ParameterModel = new MesReportParam();
                    break;
                case "DATA_SAVE":
                    newNode.Description = "将检测结果写 CSV 数据表并异步保存图像。";
                    newNode.ParameterModel = new SaveDataParam();
                    break;

                default:
                    newNode.Description = $"用于处理 {meta.DisplayName} 场景。";
                    newNode.ParameterModel = new AcquireImageParam();
                    break;
            }

            if (newNode != null)
            {
                //newNode.InputPorts.Add(new NodePort { PortId = Guid.NewGuid().ToString(), PortName = "In", PortType = PortType.In });
                //newNode.OutputPorts.Add(new NodePort { PortId = Guid.NewGuid().ToString(), PortName = "Out", PortType = PortType.Out });

                CurrentProcess.Nodes.Add(newNode);
                SelectedNode = newNode;
                AddLog($"追加节点: {newNode.DisplayName}");
            }
        }
        #endregion
    }
}