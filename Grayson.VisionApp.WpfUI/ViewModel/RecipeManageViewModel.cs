using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Infrastructure.Permission;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Recipe.Services;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Core.Client;
using Grayson.Vision.Core.Station;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using Newtonsoft.Json;


namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 配方管理页 VM。
    /// 2026-09-05 中等重构：改缓存单例 + INavigationAware（跨页传参定位、返回刷新）；
    /// 审批按钮按状态/权限显隐（不再常显三按钮）；新增选中配方的绑定工位摘要；
    /// 移除死代码（ReloadCurrentRecipe / 未接线 SearchCommand —— SearchText 已实时过滤）。
    /// </summary>
    public class RecipeManageViewModel : ViewModelBase, INavigationAware
    {
        private readonly StationRuntimeManager _runtimeManager;
        private readonly IStationHostRuntime _hostRuntime;
        private readonly IRecipeStorageService _recipeStorage;
        private readonly IRecipeApprovalService _approvalService;
        private readonly StationConfigService _stationConfigService;

        public RecipeManageViewModel(
            StationRuntimeManager runtimeManager = null,
            IStationHostRuntime hostRuntime = null,
            IRecipeStorageService recipeStorage = null,
            StationConfigService stationConfigService = null,
            IRecipeApprovalService approvalService = null)
        {
            _runtimeManager = runtimeManager;
            _hostRuntime = hostRuntime ?? App.StationHostRuntime;
            _recipeStorage = recipeStorage ?? Grayson.Vision.Repository.Services.RecipeStorageFactory.CreateRecipeStorageService();
            _approvalService = approvalService ?? Grayson.Vision.Repository.Services.RecipeApprovalFactory.CreateRecipeApprovalService();
            _stationConfigService = stationConfigService ?? new StationConfigService();

            // 目标工位列表从 Core 已创建的站点动态获取
            AvailableStations = new ObservableCollection<string>();
            RefreshAvailableStations();
            SelectedTargetStationId = AvailableStations.FirstOrDefault();

            // 构造保底加载；缓存单例下每次导航经 OnNavigatedTo 再全量刷新
            LoadAllRecipes();

            // 命令绑定（SearchCommand 已移除：SearchText 实时过滤，无需额外命令）
            CreateRecipeCommand = new RelayCommand(_ => OnCreateRecipe());
            ApplyRecipeCommand = new RelayCommand(_ => OnApplyRecipe(), _ => SelectedRecipe != null && !string.IsNullOrEmpty(SelectedTargetStationId) && SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Approved);
            DeleteRecipeCommand = new RelayCommand(_ => OnDeleteRecipe(), _ => SelectedRecipe != null);
            SaveDetailCommand = new RelayCommand(_ => OnSaveDetail(), _ => SelectedRecipe != null);
            OpenFlowEditCommand = new RelayCommand(_ => OnOpenFlowEdit(), _ => SelectedRecipe != null && SelectedRecipe.IsEditable);
            RefreshDevicesCommand = new RelayCommand(_ => RefreshLogicalDevicesFromFlow(), _ => SelectedRecipe?.MainProcess != null && SelectedRecipe.IsEditable);
            SubmitApprovalCommand = new RelayCommand(_ => OnSubmitApproval(), _ => CanSubmitApproval());
            ApproveRecipeCommand = new RelayCommand(_ => OnApproveRecipe(), _ => CanApproveRecipe());
            RejectRecipeCommand = new RelayCommand(_ => OnRejectRecipe(), _ => CanRejectRecipe());

            // 增加：当 GlobalData 的用户角色发生变化时刷新审批命令可用性
            GlobalData.Instance.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(GlobalData.CurrentUserRole))
                {
                    OnPropertyChanged(nameof(CanEditProcessParameters));
                    OnPropertyChanged(nameof(CanEditRecipe));
                    OnPropertyChanged(nameof(ShowSubmitApproval));
                    OnPropertyChanged(nameof(ShowApproveApproval));
                    OnPropertyChanged(nameof(ShowRejectApproval));
                    RaiseCommandsCanExecuteChanged();
                }
            };
        }
        /// <summary>
        /// 统一触发依赖设备状态的 RelayCommand 状态更新
        /// </summary>
        private void RaiseCommandsCanExecuteChanged()
        {
            ApplyRecipeCommand?.RaiseCanExecuteChanged();
            DeleteRecipeCommand?.RaiseCanExecuteChanged();
            SaveDetailCommand?.RaiseCanExecuteChanged();
            OpenFlowEditCommand?.RaiseCanExecuteChanged();
            RefreshDevicesCommand?.RaiseCanExecuteChanged();

            SubmitApprovalCommand?.RaiseCanExecuteChanged();
            ApproveRecipeCommand?.RaiseCanExecuteChanged();
            RejectRecipeCommand?.RaiseCanExecuteChanged();

            // 审批动作后状态已变 → 同步按钮显隐与顶部状态提示
            OnPropertyChanged(nameof(ApprovalStateHint));
            OnPropertyChanged(nameof(ShowSubmitApproval));
            OnPropertyChanged(nameof(ShowApproveApproval));
            OnPropertyChanged(nameof(ShowRejectApproval));
        }

        #region 属性绑定

        private ObservableCollection<RecipeModel> _allRecipes;
        public ObservableCollection<RecipeModel> AllRecipes
        {
            get => _allRecipes;
            set => Set(ref _allRecipes, value);
        }

        private ObservableCollection<RecipeModel> _filteredRecipes;
        public ObservableCollection<RecipeModel> FilteredRecipes
        {
            get => _filteredRecipes;
            set => Set(ref _filteredRecipes, value);
        }

        private RecipeModel _selectedRecipe;
        public RecipeModel SelectedRecipe
        {
            get => _selectedRecipe;
            set
            {
                if (Set(ref _selectedRecipe, value))
                {
                    // 🌟 自动从 FlowEdit 递归提取最新的逻辑设备依赖
                    RefreshLogicalDevicesFromFlow();
                    OnPropertyChanged(nameof(CanEditProcessParameters));
                    OnPropertyChanged(nameof(CanEditRecipe));
                    OnPropertyChanged(nameof(ApprovalStateHint));
                    OnPropertyChanged(nameof(ReadOnlyHint));
                    OnPropertyChanged(nameof(ShowReadOnlyHint));
                    OnPropertyChanged(nameof(ShowSubmitApproval));
                    OnPropertyChanged(nameof(ShowApproveApproval));
                    OnPropertyChanged(nameof(ShowRejectApproval));
                    OnPropertyChanged(nameof(BoundStationsSummary));
                    OnPropertyChanged(nameof(MainProcessName));
                    RaiseCommandsCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// 🌟 2026-09-10 新增：「编排流程（主流程）」名称 —— 与配方名称相互独立，均可编辑。
        /// 它同时就是视觉流程编辑器面包屑 / 顶部「主流程:」显示的那个名字。
        /// 历史配方 JSON 可能没有 MainProcess 节点，故 getter 做空安全，setter 在首次输入时按需补建容器。
        /// </summary>
        public string MainProcessName
        {
            get => SelectedRecipe?.MainProcess?.ProcessName ?? string.Empty;
            set
            {
                if (SelectedRecipe == null) return;

                if (SelectedRecipe.MainProcess == null)
                {
                    if (string.IsNullOrWhiteSpace(value)) return;
                    SelectedRecipe.MainProcess = new FlowProcessModel { ProcessName = value };
                }
                else if (!string.Equals(SelectedRecipe.MainProcess.ProcessName, value, StringComparison.Ordinal))
                {
                    SelectedRecipe.MainProcess.ProcessName = value;
                }

                OnPropertyChanged();
            }
        }

        // ==================== 2026-09-05 中等重构：审批按钮按状态/权限显隐 ====================
        /// <summary>显示"提交审批"按钮（仅可提交状态 Draft/Rejected 且角色够）</summary>
        public bool ShowSubmitApproval => SelectedRecipe != null && CanSubmitApproval();
        /// <summary>显示"审批通过"按钮（仅 PendingApproval 且 Admin）</summary>
        public bool ShowApproveApproval => SelectedRecipe != null && CanApproveRecipe();
        /// <summary>显示"驳回"按钮（PendingApproval/Approved 且 Admin）</summary>
        public bool ShowRejectApproval => SelectedRecipe != null && CanRejectRecipe();

        /// <summary>
        /// 选中配方的绑定工位摘要（来自 StationConfig.BoundRecipeId 反向查询；空=未绑定任何工位）。
        /// 供元数据卡展示，解释"该配方当前被哪些工位运行"。
        /// </summary>
        public string BoundStationsSummary
        {
            get
            {
                if (SelectedRecipe == null) return string.Empty;
                try
                {
                    var bound = GetBoundStationCodes(SelectedRecipe);
                    return bound.Count == 0
                        ? "未绑定任何工位"
                        : $"已绑定 {bound.Count} 个工位：{string.Join("、", bound)}";
                }
                catch
                {
                    return "绑定信息不可用";
                }
            }
        }

        /// <summary>
        /// 反向查询绑定了指定配方的工位代码列表（按 StationConfig.BoundRecipeId/Name 匹配）。
        /// 删除守卫与 BoundStationsSummary 共用，杜绝两处独立查询漂移。
        /// </summary>
        private List<string> GetBoundStationCodes(RecipeModel recipe)
        {
            if (recipe == null) return new List<string>();
            return (_stationConfigService.LoadAllLines() ?? new List<LineConfigModel>())
                .SelectMany(l => l.Stations ?? new List<StationConfigModel>())
                .Where(s => !string.IsNullOrWhiteSpace(s.BoundRecipeId) &&
                            (s.BoundRecipeId == recipe.RecipeId ||
                             (string.IsNullOrEmpty(recipe.RecipeId) && s.BoundRecipeName == recipe.RecipeName)))
                .Select(s => s.StationCode)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .ToList();
        }

        /// <summary>当前用户是否允许编辑工艺参数（Engineer 以上且配方可编辑）</summary>
        public bool CanEditProcessParameters =>
            GlobalData.Instance.CurrentUserRole >= UserRole.Engineer &&
            SelectedRecipe != null &&
            SelectedRecipe.IsEditable;

        private ObservableCollection<RecipeDeviceMappingModel> _logicalDevicesList;
        public ObservableCollection<RecipeDeviceMappingModel> LogicalDevicesList
        {
            get => _logicalDevicesList;
            set => Set(ref _logicalDevicesList, value);
        }

        private string _searchText;
        public string SearchText
        {
            get => _searchText;
            set { if (Set(ref _searchText, value)) OnSearch(); }
        }

        // 🌟 目标下发工位列表与选中项
        public ObservableCollection<string> AvailableStations { get; set; }

        private string _selectedTargetStationId;
        public string SelectedTargetStationId
        {
            get => _selectedTargetStationId;
            set => Set(ref _selectedTargetStationId, value);
        }

        #endregion

        #region 命令定义

        public RelayCommand CreateRecipeCommand { get; }
        public RelayCommand ApplyRecipeCommand { get; }
        public RelayCommand DeleteRecipeCommand { get; }
        public RelayCommand SaveDetailCommand { get; }
        public RelayCommand OpenFlowEditCommand { get; }
        public RelayCommand RefreshDevicesCommand { get; }
        public RelayCommand SubmitApprovalCommand { get; }
        public RelayCommand ApproveRecipeCommand { get; }
        public RelayCommand RejectRecipeCommand { get; }

        #endregion

        #region 交互逻辑

        /// <summary>
        /// 递归提取与拓扑同步，并刷新每个逻辑设备的物理绑定状态。
        /// </summary>
        private void RefreshLogicalDevicesFromFlow()
        {
            if (SelectedRecipe == null)
            {
                LogicalDevicesList = new ObservableCollection<RecipeDeviceMappingModel>();
                return;
            }

            if (SelectedRecipe.MainProcess != null)
            {
                // 1. 反射拓扑提取
                var extractedDevices = RecipeDeviceExtractor.ExtractLogicalDevices(SelectedRecipe.MainProcess);

                // 2. 合并保留已有的 MappedDeviceId
                var existingMappings = SelectedRecipe.LogicalDevices?
                    .Where(d => !string.IsNullOrEmpty(d.LogicalDeviceId))
                    .ToDictionary(d => d.LogicalDeviceId, d => d.MappedDeviceId, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>();

                // 🌟 核心修复 1：只有提取到有效设备时才更新 SelectedRecipe.LogicalDevices
                if (extractedDevices != null && extractedDevices.Any())
                {
                    foreach (var device in extractedDevices)
                    {
                        if (existingMappings.TryGetValue(device.LogicalDeviceId, out var mappedId))
                        {
                            device.MappedDeviceId = mappedId;
                        }
                    }
                    SelectedRecipe.LogicalDevices = extractedDevices;
                }
            }

            // 🌟 核心修复 2：如果提取为空（例如第一次加载/节点未完全初始化），使用 SelectedRecipe.LogicalDevices 兜底，绝不直接赋空
            var devices = SelectedRecipe.LogicalDevices ?? new List<RecipeDeviceMappingModel>();

            foreach (var device in devices)
            {
                device.RaisePropertyChanged(nameof(device.IsBoundToPhysical));
            }

            LogicalDevicesList = new ObservableCollection<RecipeDeviceMappingModel>(devices);
        }

        /// <summary>
        /// 打开编辑器
        /// </summary>
        private void OnOpenFlowEdit()
        {
            if (SelectedRecipe == null) return;

            // 🌟 跳转前先刷新并落盘，保证数据一致
            RefreshLogicalDevicesFromFlow();
            _recipeStorage.SaveRecipe(SelectedRecipe);

            // 跨界面跳转并携带 SelectedRecipe 引用
            NavigationService.Current?.NavigateTo(PageType.FlowEdit, SelectedRecipe);
        }

        /// <summary>
        /// 刷新目标工位列表：从工位配置中加载“全工位”。
        /// </summary>
        private void RefreshAvailableStations()
        {
            var previousSelected = SelectedTargetStationId;
            AvailableStations.Clear();

            // 原逻辑（仅运行时已创建工位）：
            // var stationIds = _hostRuntime?.GetStationIds() ?? Enumerable.Empty<string>();
            // foreach (var id in stationIds)
            // {
            //     AvailableStations.Add(id);
            // }

            var stationCodes = (_stationConfigService.LoadAllLines() ?? new List<Grayson.Vision.Contracts.Station.Models.LineConfigModel>())
                .SelectMany(l => l.Stations ?? new List<Grayson.Vision.Contracts.Station.Models.StationConfigModel>())
                .Select(s => !string.IsNullOrWhiteSpace(s.StationCode) ? s.StationCode : s.StationId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            foreach (var code in stationCodes)
            {
                AvailableStations.Add(code);
            }

            SelectedTargetStationId = AvailableStations.Contains(previousSelected)
                ? previousSelected
                : AvailableStations.FirstOrDefault();
        }

        /// <summary>
        /// 🌟 3. 指定目标工位下发配方（统一走 Core 标准入口，绑定并持久化，重启后仍生效）
        /// </summary>
        private async void OnApplyRecipe()
        {
            if (SelectedRecipe == null || string.IsNullOrEmpty(SelectedTargetStationId)) return;

            try
            {
                // 保持内存与存储一致
                _recipeStorage.SaveRecipe(SelectedRecipe);

                // 每次下发前刷新一次目标列表，确保拿到最新站点
                RefreshAvailableStations();

                // 1. 查找目标工位配置（配置存在才允许下发，避免产生悬挂绑定）
                var config = (_stationConfigService.LoadAllLines() ?? new List<LineConfigModel>())
                    .SelectMany(l => l.Stations ?? new List<StationConfigModel>())
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.StationCode) && s.StationCode == SelectedTargetStationId);

                if (config == null)
                {
                    MessageBox.Show($"未找到工位 [{SelectedTargetStationId}] 的配置，请先在【工位管理】中创建并保存该工位。",
                                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 2. 更新并持久化工位绑定的配方（与工位管理保存路径一致，重启后绑定不丢失）
                config.BoundRecipeId = SelectedRecipe.RecipeId;
                config.BoundRecipeName = SelectedRecipe.RecipeName;
                _stationConfigService.SaveStation(config);

                // 3. 统一走 Core 标准入口：创建/更新工位 + 绑定设备映射 + 加载配方
                //    补传工位已配的业务过程与模板代码（此前漏传会把工位已挂的独立视觉引擎/模板判据拆掉，
                //    导致 StandaloneVision 判据读不到模板 VerdictRule → 分类好件误判 NG）
                var hostRuntime = _hostRuntime ?? App.StationHostRuntime;
                if (hostRuntime == null)
                {
                    MessageBox.Show("Core 运行时不可用，无法下发配方。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                await hostRuntime.CreateStationWithRecipeAsync(
                    SelectedTargetStationId,
                    SelectedRecipe,
                    SelectedRecipe.LogicalDevices,
                    WorkMode.Production,
                    null,
                    config.ProcessKey,
                    config.ProcessConfigJson,
                    config.TaskTemplateCode);

                MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 已下发并绑定至工位 [{SelectedTargetStationId}]，重启后仍生效！",
                                "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下发配方失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSaveDetail()
        {
            if (SelectedRecipe == null) return;

            // 非草案/被驳回状态下，禁止修改已锁定或已审批的核心内容；
            // 但允许 Engineer+ 在可编辑状态下保存元数据与工艺参数。
            if (!SelectedRecipe.IsEditable && GlobalData.Instance.CurrentUserRole < UserRole.Administrator)
            {
                MessageBox.Show("当前配方状态不允许编辑，请联系管理员或将配方驳回后再修改。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedRecipe.LastModifiedTime = DateTime.Now;
            _recipeStorage.SaveRecipe(SelectedRecipe);

            MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 保存成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #region INavigationAware（2026-09-05 缓存单例：每次进入全量刷新 + 支持传参定位）

        /// <summary>
        /// 导航进入：全量重载配方与目标工位（缓存单例下页面不重建，靠此刷新数据）。
        /// parameter 支持：null → 保持当前选中；RecipeModel → 定位选中该配方（来自工位工作台"配方管理"跳转）。
        /// </summary>
        public void OnNavigatedTo(object parameter)
        {
            RefreshAvailableStations();
            LoadAllRecipes();

            if (parameter is RecipeModel target)
            {
                SelectRecipeByIdentity(target);
            }
            else if (parameter is string identity && !string.IsNullOrWhiteSpace(identity))
            {
                var matched = AllRecipes.FirstOrDefault(r =>
                    r.RecipeId == identity || r.RecipeCode == identity || r.RecipeName == identity);
                if (matched != null) SelectedRecipe = matched;
            }
        }

        /// <summary>按 RecipeId/RecipeCode/名称命中选中；命中失败不打扰（保持现状）</summary>
        private void SelectRecipeByIdentity(RecipeModel target)
        {
            if (target == null) return;
            var matched = AllRecipes.FirstOrDefault(r =>
                (!string.IsNullOrEmpty(target.RecipeId) && r.RecipeId == target.RecipeId)
                || (!string.IsNullOrEmpty(target.RecipeCode) && r.RecipeCode == target.RecipeCode)
                || (!string.IsNullOrEmpty(target.RecipeName) && r.RecipeName == target.RecipeName));
            if (matched != null) SelectedRecipe = matched;
        }

        /// <summary>导航离开：无需清理（数据实时落盘）</summary>
        public void OnNavigatedFrom()
        {
        }

        #endregion

        private void LoadAllRecipes()
        {
            var recipes = _recipeStorage.GetAllRecipes();
            AllRecipes = new ObservableCollection<RecipeModel>(recipes);
            OnSearch();
        }

        private void OnCreateRecipe()
        {
            var newRecipe = new RecipeModel
            {
                RecipeId = Guid.NewGuid().ToString("N"), // 🌟 显式生成 RecipeId，确保标识唯一
                RecipeCode = "RCP-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                RecipeName = "新建视觉配方",
                ProductCategory = "通用分类",
                Author = GlobalData.Instance.CurrentUserName ?? "Admin",
                // 注：IsActive 属旧 LiteDB 体系字段（JSON 主链路恒 false，无需显式赋值）
                LogicalDevices = new List<RecipeDeviceMappingModel>()
            };

            // 🌟 立即持久化到磁盘文件
            bool success = _recipeStorage.SaveRecipe(newRecipe);
            if (success)
            {
                AllRecipes.Insert(0, newRecipe);
                OnSearch();
                SelectedRecipe = newRecipe;
            }
            else
            {
                MessageBox.Show("新建配方持久化失败，请检查文件系统权限。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnDeleteRecipe()
        {
            if (SelectedRecipe == null) return;

            // 🌟 删除守卫：先检查是否有工位绑定该配方（RecipeModel.IsActive 属旧 LiteDB 体系，
            //    JSON 主链路恒 false 无拦截意义，真正的守卫是绑定检查，杜绝悬挂引用）
            var boundStations = GetBoundStationCodes(SelectedRecipe);

            if (boundStations.Any())
            {
                MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 已被工位 [{string.Join(", ", boundStations)}] 绑定，请先在【工位管理】中解绑后再删除。",
                                "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 🌟 增加删除确认弹窗
            var result = MessageBox.Show(
                $"确定要永久删除配方 [{SelectedRecipe.RecipeName}] ({SelectedRecipe.RecipeCode}) 吗？此操作不可撤销。",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            // 🌟 优先通过 RecipeId 删除，若为空则降级用 RecipeCode 匹配删除
            string targetId = !string.IsNullOrEmpty(SelectedRecipe.RecipeId)
                ? SelectedRecipe.RecipeId
                : SelectedRecipe.RecipeCode;

            bool deleted = _recipeStorage.DeleteRecipe(targetId);

            if (deleted)
            {
                var itemToRemove = SelectedRecipe;
                AllRecipes.Remove(itemToRemove);
                OnSearch();
                // 🌟 自动切换或清空当前选中项
                SelectedRecipe = FilteredRecipes.FirstOrDefault();
            }
            else
            {
                MessageBox.Show($"删除磁盘配方文件失败，请检查文件是否存在或被占用。", "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSearch()
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                FilteredRecipes = new ObservableCollection<RecipeModel>(AllRecipes);
             
            }
            else
            {
                var kw = SearchText.Trim().ToLower();
                FilteredRecipes = new ObservableCollection<RecipeModel>(
                    AllRecipes.Where(r => (r.RecipeCode?.ToLower().Contains(kw) == true) ||
                                          (r.RecipeName?.ToLower().Contains(kw) == true))
                );
            }

            SelectedRecipe = FilteredRecipes.FirstOrDefault();

        }

        #endregion

        #region 审批流程（状态机下沉到 Core/Repository 的 IRecipeApprovalService）

        private bool CanSubmitApproval()
        {
            return _approvalService.CanSubmit(SelectedRecipe, GlobalData.Instance.CurrentUserRole) == null;
        }

        private bool CanApproveRecipe()
        {
            return _approvalService.CanApprove(SelectedRecipe, GlobalData.Instance.CurrentUserRole) == null;
        }

        private bool CanRejectRecipe()
        {
            return _approvalService.CanReject(SelectedRecipe, GlobalData.Instance.CurrentUserRole) == null;
        }

        /// <summary>
        /// 当前用户是否可以编辑配方核心内容（草案或被驳回，且未被锁定时）。
        /// </summary>
        public bool CanEditRecipe =>
            GlobalData.Instance.CurrentUserRole >= UserRole.Engineer &&
            SelectedRecipe != null &&
            SelectedRecipe.IsEditable;

        /// <summary>
        /// 当前配方审批状态文本提示（用于 UI 状态栏/标题）。
        /// </summary>
        /// <summary>
        /// 顶部状态栏提示，包含锁定/生效状态。
        /// </summary>
        public string ApprovalStateHint
        {
            get
            {
                if (SelectedRecipe == null) return "未选择配方";
                if (SelectedRecipe.IsLocked) return "🔒 配方已锁定，仅可查看";
                if (!SelectedRecipe.HasMinimumMetadata) return "配方基础信息不完整";
                if (SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Draft)
                    return "草稿状态：可编辑并提交审批";
                if (SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.PendingApproval)
                    return "待审批：等待管理员审批";
                if (SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Approved)
                    return SelectedRecipe.IsEffective ? "✅ 已批准并已生效，可下发；如需修改可由管理员驳回后编辑" : "已批准，等待生效时间；如需修改可由管理员驳回后编辑";
                if (SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Rejected)
                    return "已驳回：请根据意见修改后重新提交";
                return SelectedRecipe.ApprovalStatus.ToString();
            }
        }

        /// <summary>
        /// 当配方处于生效/锁定状态时的只读提示文本（用于元数据卡片）。
        /// </summary>
        public string ReadOnlyHint =>
            SelectedRecipe != null && !SelectedRecipe.IsEditable
                ? "⚠️ 当前配方已生效或已锁定，禁止编辑核心内容与参数。如需修改，请先驳回或复制为新版本。"
                : string.Empty;

        /// <summary>
        /// 是否显示只读提示（供 BooleanToVisibilityConverter 使用）。
        /// </summary>
        public bool ShowReadOnlyHint => SelectedRecipe != null && !SelectedRecipe.IsEditable;

        private void OnSubmitApproval()
        {
            if (SelectedRecipe == null) return;

            var denyReason = _approvalService.CanSubmit(SelectedRecipe, GlobalData.Instance.CurrentUserRole);
            if (denyReason != null)
            {
                MessageBox.Show(denyReason, "无法提交", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool success = _approvalService.SubmitForApproval(
                SelectedRecipe,
                GlobalData.Instance.CurrentUserName ?? "未知用户",
                SelectedRecipe.ApprovalInfo?.ApprovalComment);

            RaiseCommandsCanExecuteChanged();
            if (success)
                MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 已提交审批。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("提交审批失败，请检查存储目录权限或文件占用。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnApproveRecipe()
        {
            if (SelectedRecipe == null) return;

            var denyReason = _approvalService.CanApprove(SelectedRecipe, GlobalData.Instance.CurrentUserRole);
            if (denyReason != null)
            {
                MessageBox.Show(denyReason, "无法审批", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool success = _approvalService.Approve(
                SelectedRecipe,
                GlobalData.Instance.CurrentUserName ?? "未知用户",
                SelectedRecipe.ApprovalInfo?.ApprovalComment);

            RaiseCommandsCanExecuteChanged();
            if (success)
                MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 审批已通过，已可下发工位。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("审批通过失败，请检查存储目录权限或文件占用。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnRejectRecipe()
        {
            if (SelectedRecipe == null) return;

            var denyReason = _approvalService.CanReject(SelectedRecipe, GlobalData.Instance.CurrentUserRole);
            if (denyReason != null)
            {
                MessageBox.Show(denyReason, "无法驳回", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool success = _approvalService.Reject(
                SelectedRecipe,
                GlobalData.Instance.CurrentUserName ?? "未知用户",
                SelectedRecipe.ApprovalInfo?.ApprovalComment);

            RaiseCommandsCanExecuteChanged();
            if (success)
                MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 已驳回并回退到可编辑状态，请修改后重新提交。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show("驳回失败，请检查存储目录权限或文件占用。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        #endregion
    }
}