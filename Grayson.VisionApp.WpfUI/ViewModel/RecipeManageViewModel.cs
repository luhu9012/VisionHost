using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Infrastructure.Permission;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Recipe.Services;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Core.Client;
using Grayson.Vision.Core.Station;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;


namespace Grayson.Vision.WpfUI.ViewModel
{
    public class RecipeManageViewModel : ViewModelBase
    {
        private readonly StationRuntimeManager _runtimeManager;
        private readonly IStationHostRuntime _hostRuntime;
        private readonly IRecipeStorageService _recipeStorage;

        public RecipeManageViewModel(
            StationRuntimeManager runtimeManager = null,
            IStationHostRuntime hostRuntime = null,
            IRecipeStorageService recipeStorage = null)
        {
            _runtimeManager = runtimeManager;
            _hostRuntime = hostRuntime ?? App.StationHostRuntime;
            _recipeStorage = recipeStorage ?? Grayson.Vision.Repository.Services.RecipeStorageFactory.CreateRecipeStorageService();

            // 目标工位列表从 Core 已创建的站点动态获取
            AvailableStations = new ObservableCollection<string>();
            RefreshAvailableStations();
            SelectedTargetStationId = AvailableStations.FirstOrDefault();

            LoadAllRecipes();

            // 命令绑定
            SearchCommand = new RelayCommand(_ => OnSearch());
            CreateRecipeCommand = new RelayCommand(_ => OnCreateRecipe());
            ApplyRecipeCommand = new RelayCommand(_ => OnApplyRecipe(), _ => SelectedRecipe != null && !string.IsNullOrEmpty(SelectedTargetStationId) && SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Approved);
            DeleteRecipeCommand = new RelayCommand(_ => OnDeleteRecipe(), _ => SelectedRecipe != null && !SelectedRecipe.IsActive);
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
                    OnPropertyChanged(nameof(CanEditRuntimeConfig));
                    OnPropertyChanged(nameof(CanEditProcessParameters));
                    OnPropertyChanged(nameof(CanEditRecipe));
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
                    OnPropertyChanged(nameof(CanEditRuntimeConfig));
                    OnPropertyChanged(nameof(CanEditProcessParameters));
                    OnPropertyChanged(nameof(CanEditRecipe));
                    OnPropertyChanged(nameof(ApprovalStateHint));
                    OnPropertyChanged(nameof(ReadOnlyHint));
                    OnPropertyChanged(nameof(ShowReadOnlyHint));
                    RaiseCommandsCanExecuteChanged();
                }
            }
        }

        /// <summary>当前用户是否允许编辑运行配置（Engineer 以上且配方可编辑）</summary>
        public bool CanEditRuntimeConfig =>
            GlobalData.Instance.CurrentUserRole >= UserRole.Engineer &&
            SelectedRecipe != null &&
            SelectedRecipe.IsEditable;

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

        public RelayCommand SearchCommand { get; }
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
                // 反射拓扑提取
                var extractedDevices = RecipeDeviceExtractor.ExtractLogicalDevices(SelectedRecipe.MainProcess);

                // 合并保留已有的 MappedDeviceId，避免从 Flow 重提取后丢失工位映射关系
                var existingMappings = SelectedRecipe.LogicalDevices?
                    .Where(d => !string.IsNullOrEmpty(d.LogicalDeviceId))
                    .ToDictionary(d => d.LogicalDeviceId, d => d.MappedDeviceId)
                    ?? new Dictionary<string, string>();

                foreach (var device in extractedDevices)
                {
                    if (existingMappings.TryGetValue(device.LogicalDeviceId, out var mappedId))
                    {
                        device.MappedDeviceId = mappedId;
                    }
                }

                SelectedRecipe.LogicalDevices = extractedDevices;
            }

            // 触发每个映射项的绑定状态属性变更通知（MappedDeviceId 在流程编辑器中可能已变更）
            var devices = SelectedRecipe.LogicalDevices ?? new List<RecipeDeviceMappingModel>();
            foreach (var device in devices)
            {
                device.RaisePropertyChanged(nameof(device.IsBoundToPhysical));
            }

            LogicalDevicesList = devices.Any()
                ? new ObservableCollection<RecipeDeviceMappingModel>(devices)
                : new ObservableCollection<RecipeDeviceMappingModel>();
        }

        /// <summary>
        /// 🌟 2. 打开编辑器并传参
        /// </summary>
        private void OnOpenFlowEdit()
        {
            if (SelectedRecipe == null) return;

            _recipeStorage.SaveRecipe(SelectedRecipe);

            // 跨界面跳转并携带 SelectedRecipe 参数对象
            NavigationService.Current?.NavigateTo(PageType.FlowEdit, SelectedRecipe);
        }

        /// <summary>
        /// 刷新目标工位列表：从 Core StationHostRuntime 获取当前已创建的站点。
        /// </summary>
        private void RefreshAvailableStations()
        {
            var previousSelected = SelectedTargetStationId;
            AvailableStations.Clear();

            var stationIds = _hostRuntime?.GetStationIds() ?? Enumerable.Empty<string>();
            foreach (var id in stationIds)
            {
                AvailableStations.Add(id);
            }

            SelectedTargetStationId = AvailableStations.Contains(previousSelected)
                ? previousSelected
                : AvailableStations.FirstOrDefault();
        }

        /// <summary>
        /// 🌟 3. 指定目标工位下发配方
        /// </summary>
        private async void OnApplyRecipe()
        {
            if (SelectedRecipe == null || string.IsNullOrEmpty(SelectedTargetStationId)) return;

            // 保持内存与存储一致
            _recipeStorage.SaveRecipe(SelectedRecipe);

            // 每次下发前刷新一次目标列表，确保拿到最新站点
            RefreshAvailableStations();

            if (_runtimeManager != null)
            {
                IWorkerClient client = _runtimeManager.GetClient(SelectedTargetStationId);
                if (client != null && SelectedRecipe.MainProcess != null)
                {
                    await client.LoadRecipeAsync(SelectedRecipe.MainProcess);
                    MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 已成功下发至工位 [{SelectedTargetStationId}]！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            MessageBox.Show($"未找到运行中的目标工位 [{SelectedTargetStationId}]，请先在【工位管理】中保存并启动工位。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnSaveDetail()
        {
            if (SelectedRecipe == null) return;

            // 非草案/被驳回状态下，禁止修改已锁定或已审批的核心内容；
            // 但允许 Engineer+ 在可编辑状态下保存元数据、运行配置、工艺参数。
            if (!SelectedRecipe.IsEditable && GlobalData.Instance.CurrentUserRole < UserRole.Administrator)
            {
                MessageBox.Show("当前配方状态不允许编辑，请联系管理员或将配方驳回后再修改。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedRecipe.LastModifiedTime = DateTime.Now;
            _recipeStorage.SaveRecipe(SelectedRecipe);

            MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 保存成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

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
                IsActive = false,
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
            if (SelectedRecipe == null || SelectedRecipe.IsActive) return;

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

        #region 审批流程（UI 占位，实际工作流可后续接权限服务）

        private bool CanSubmitApproval()
        {
            return SelectedRecipe != null &&
                   GlobalData.Instance.CurrentUserRole >= UserRole.Engineer &&
                   SelectedRecipe.HasMinimumMetadata &&
                   (SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Draft ||
                    SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Rejected);
        }

        private bool CanApproveRecipe()
        {
            return SelectedRecipe != null &&
                   GlobalData.Instance.CurrentUserRole == UserRole.Administrator &&
                   SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.PendingApproval;
        }

        private bool CanRejectRecipe()
        {
            return SelectedRecipe != null &&
                   GlobalData.Instance.CurrentUserRole == UserRole.Administrator &&
                   (SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.PendingApproval ||
                    SelectedRecipe.ApprovalStatus == Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Approved);
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

            if (!SelectedRecipe.HasMinimumMetadata)
            {
                MessageBox.Show("请完善配方名称、编号、产品类别和版本号后再提交审批。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedRecipe.ApprovalStatus = Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.PendingApproval;
            EnsureApprovalInfo();
            SelectedRecipe.ApprovalInfo.SubmittedBy = GlobalData.Instance.CurrentUserName ?? "未知用户";
            SelectedRecipe.ApprovalInfo.SubmittedAt = DateTime.Now;
            SelectedRecipe.ApprovalInfo.LastActionAt = DateTime.Now;
            AddApprovalHistory(Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.PendingApproval);
            _recipeStorage.SaveRecipe(SelectedRecipe);

            RaiseCommandsCanExecuteChanged();
            MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 已提交审批。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnApproveRecipe()
        {
            if (SelectedRecipe == null) return;

            SelectedRecipe.ApprovalStatus = Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Approved;
            EnsureApprovalInfo();
            SelectedRecipe.ApprovalInfo.ApprovedBy = GlobalData.Instance.CurrentUserName ?? "未知用户";
            SelectedRecipe.ApprovalInfo.ApprovedAt = DateTime.Now;
            SelectedRecipe.ApprovalInfo.LastActionAt = DateTime.Now;
            SelectedRecipe.ApprovalInfo.RejectionReason = null;
            SelectedRecipe.EffectiveFrom = DateTime.Now;
            SelectedRecipe.IsLocked = false;
            AddApprovalHistory(Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Approved);
            _recipeStorage.SaveRecipe(SelectedRecipe);

            RaiseCommandsCanExecuteChanged();
            MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 审批已通过，已可下发工位。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnRejectRecipe()
        {
            if (SelectedRecipe == null) return;

            var reason = SelectedRecipe.ApprovalInfo?.ApprovalComment;
            SelectedRecipe.ApprovalStatus = Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Rejected;
            EnsureApprovalInfo();
            SelectedRecipe.ApprovalInfo.ApprovedBy = null;
            SelectedRecipe.ApprovalInfo.ApprovedAt = null;
            SelectedRecipe.ApprovalInfo.LastActionAt = DateTime.Now;
            SelectedRecipe.ApprovalInfo.RejectionReason = reason;
            SelectedRecipe.EffectiveFrom = null;
            SelectedRecipe.IsLocked = false;
            AddApprovalHistory(Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Rejected, reason);
            _recipeStorage.SaveRecipe(SelectedRecipe);

            RaiseCommandsCanExecuteChanged();
            MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 已驳回并回退到可编辑状态，请修改后重新提交。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void EnsureApprovalInfo()
        {
            if (SelectedRecipe == null) return;
            if (SelectedRecipe.ApprovalInfo == null)
                SelectedRecipe.ApprovalInfo = new RecipeApprovalInfo();
            if (SelectedRecipe.ApprovalInfo.History == null)
                SelectedRecipe.ApprovalInfo.History = new List<RecipeApprovalHistoryEntry>();
        }

        private void AddApprovalHistory(Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus action, string comment = null)
        {
            EnsureApprovalInfo();
            var entry = new RecipeApprovalHistoryEntry
            {
                ActionAt = DateTime.Now,
                ActionBy = GlobalData.Instance.CurrentUserName ?? "未知用户",
                Action = action,
                Comment = comment ?? SelectedRecipe.ApprovalInfo.ApprovalComment,
                ElectronicSignature = SelectedRecipe.ApprovalInfo.ElectronicSignature
            };
            SelectedRecipe.ApprovalInfo.History.Add(entry);
        }

        #endregion
    }
}