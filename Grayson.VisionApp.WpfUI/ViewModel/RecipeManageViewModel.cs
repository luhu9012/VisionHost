using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Recipe.Services;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Core.Client;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;


namespace Grayson.Vision.WpfUI.ViewModel
{
    public class RecipeManageViewModel : ViewModelBase
    {
        private readonly string _recipesFolderPath;
        private readonly StationRuntimeManager _runtimeManager;

        public RecipeManageViewModel(StationRuntimeManager runtimeManager = null)
        {
            _runtimeManager = runtimeManager;
            _recipesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes");

            if (!Directory.Exists(_recipesFolderPath))
                Directory.CreateDirectory(_recipesFolderPath);

            // 初始化工位选择列表 (可对接 RuntimeManager 或配置服务)
            AvailableStations = new ObservableCollection<string> { "ST_01", "ST_02", "ST_03" };
            SelectedTargetStationId = AvailableStations.FirstOrDefault();

            LoadRecipesFromDisk();

            // 命令绑定
            SearchCommand = new RelayCommand(_ => OnSearch());
            CreateRecipeCommand = new RelayCommand(_ => OnCreateRecipe());
            ApplyRecipeCommand = new RelayCommand(_ => OnApplyRecipe(), _ => SelectedRecipe != null && !string.IsNullOrEmpty(SelectedTargetStationId));
            DeleteRecipeCommand = new RelayCommand(_ => OnDeleteRecipe(), _ => SelectedRecipe != null && !SelectedRecipe.IsActive);
            SaveDetailCommand = new RelayCommand(_ => OnSaveDetail(), _ => SelectedRecipe != null);
            OpenFlowEditCommand = new RelayCommand(_ => OnOpenFlowEdit(), _ => SelectedRecipe != null);
            RefreshDevicesCommand = new RelayCommand(_ => RefreshLogicalDevicesFromFlow(), _ => SelectedRecipe?.MainProcess != null);
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
                }
            }
        }

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

        public ICommand SearchCommand { get; }
        public ICommand CreateRecipeCommand { get; }
        public ICommand ApplyRecipeCommand { get; }
        public ICommand DeleteRecipeCommand { get; }
        public ICommand SaveDetailCommand { get; }
        public ICommand OpenFlowEditCommand { get; }
        public ICommand RefreshDevicesCommand { get; }

        #endregion

        #region 交互逻辑

        /// <summary>
        /// 🌟 1. 递归提取与拓扑同步
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
                SelectedRecipe.LogicalDevices = extractedDevices;
            }

            LogicalDevicesList = SelectedRecipe.LogicalDevices != null
                ? new ObservableCollection<RecipeDeviceMappingModel>(SelectedRecipe.LogicalDevices)
                : new ObservableCollection<RecipeDeviceMappingModel>();
        }

        /// <summary>
        /// 🌟 2. 打开编辑器并传参
        /// </summary>
        private void OnOpenFlowEdit()
        {
            if (SelectedRecipe == null) return;

            SaveRecipeToDisk(SelectedRecipe);

            // 跨界面跳转并携带 SelectedRecipe 参数对象
            NavigationService.Current?.NavigateTo(PageType.FlowEdit, SelectedRecipe);
        }

        /// <summary>
        /// 🌟 3. 指定目标工位下发配方
        /// </summary>
        private async void OnApplyRecipe()
        {
            if (SelectedRecipe == null || string.IsNullOrEmpty(SelectedTargetStationId)) return;

            // 保持内存与存储一致
            SaveRecipeToDisk(SelectedRecipe);

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

            MessageBox.Show($"未找到运行中的目标工位 [{SelectedTargetStationId}] 实例！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnSaveDetail()
        {
            if (SelectedRecipe == null) return;

            SelectedRecipe.LastModifiedTime = DateTime.Now;
            SaveRecipeToDisk(SelectedRecipe);

            MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 保存成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadRecipesFromDisk()
        {
            var list = new List<RecipeModel>();
            if (Directory.Exists(_recipesFolderPath))
            {
                var files = Directory.GetFiles(_recipesFolderPath, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var recipe = JsonConvert.DeserializeObject<RecipeModel>(json);
                        if (recipe != null) list.Add(recipe);
                    }
                    catch { }
                }
            }

            AllRecipes = new ObservableCollection<RecipeModel>(list);
            OnSearch();
        }

        private void SaveRecipeToDisk(RecipeModel recipe)
        {
            string filePath = Path.Combine(_recipesFolderPath, $"{recipe.RecipeCode ?? recipe.RecipeId}.json");
            string json = JsonConvert.SerializeObject(recipe, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        private void OnCreateRecipe()
        {
            var newRecipe = new RecipeModel
            {
                RecipeCode = "RCP-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                RecipeName = "新建视觉配方",
                ProductCategory = "通用分类",
                Author = "Admin",
                IsActive = false,
                LogicalDevices = new List<RecipeDeviceMappingModel>()
            };

            SaveRecipeToDisk(newRecipe);
            AllRecipes.Insert(0, newRecipe);
            OnSearch();
            SelectedRecipe = newRecipe;
        }

        private void OnDeleteRecipe()
        {
            if (SelectedRecipe == null || SelectedRecipe.IsActive) return;

            string filePath = Path.Combine(_recipesFolderPath, $"{SelectedRecipe.RecipeCode ?? SelectedRecipe.RecipeId}.json");
            if (File.Exists(filePath)) File.Delete(filePath);

            AllRecipes.Remove(SelectedRecipe);
            OnSearch();
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
    }
}