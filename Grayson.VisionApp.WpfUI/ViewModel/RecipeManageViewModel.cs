//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: RecipeManageViewModel.cs
// 创 建: 2026-07-29
// 说 明: 配方管理界面 ViewModel (包含Mock数据收拢与交互逻辑)
//===================================================================================

using Grayson.Vision.Contracts.ViewModels;
using Grayson.Vision.WpfUI.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 配方数据模型
    /// </summary>
    public class RecipeModel : ViewModelBase
    {
        private string _recipeCode;
        public string RecipeCode
        {
            get => _recipeCode;
            set => Set(ref _recipeCode, value);
        }

        private string _recipeName;
        public string RecipeName
        {
            get => _recipeName;
            set => Set(ref _recipeName, value);
        }

        private string _productCategory;
        public string ProductCategory
        {
            get => _productCategory;
            set => Set(ref _productCategory, value);
        }

        private string _flowName;
        public string FlowName
        {
            get => _flowName;
            set => Set(ref _flowName, value);
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set => Set(ref _isActive, value);
        }

        private string _description;
        public string Description
        {
            get => _description;
            set => Set(ref _description, value);
        }

        private DateTime _updatedTime;
        public DateTime UpdatedTime
        {
            get => _updatedTime;
            set => Set(ref _updatedTime, value);
        }

        private string _updatedBy;
        public string UpdatedBy
        {
            get => _updatedBy;
            set => Set(ref _updatedBy, value);
        }

        private double _exposureTime;
        public double ExposureTime
        {
            get => _exposureTime;
            set => Set(ref _exposureTime, value);
        }

        private double _gain;
        public double Gain
        {
            get => _gain;
            set => Set(ref _gain, value);
        }

        private double _toleranceMm;
        public double ToleranceMm
        {
            get => _toleranceMm;
            set => Set(ref _toleranceMm, value);
        }
        private string _version;
        public string Version
        {
            get => _version;
            set => Set(ref _version, value);
        }
        public ObservableCollection<RecipeDeviceMappingModel> LogicalDevices { get; set; }
        public RecipeModel()
        {
            LogicalDevices = new ObservableCollection<RecipeDeviceMappingModel>();
        }
    }

    /// <summary>
    /// 配方管理 ViewModel
    /// </summary>
    public class RecipeManageViewModel : ViewModelBase
    {
        public RecipeManageViewModel()
        {
            // 加载 Mock 数据
            LoadMockData();

            SearchCommand = new RelayCommand(_ => OnSearch());
            CreateRecipeCommand = new RelayCommand(_ => OnCreateRecipe());
            ApplyRecipeCommand = new RelayCommand(_ => OnApplyRecipe(), _ => SelectedRecipe != null);
            DeleteRecipeCommand = new RelayCommand(_ => OnDeleteRecipe(), _ => SelectedRecipe != null && !SelectedRecipe.IsActive);
            SaveDetailCommand = new RelayCommand(_ => OnSaveDetail(), _ => SelectedRecipe != null);
        }

        #region Mock 数据产生（收拢至单一函数）

        /// <summary>
        /// 统一生成Mock数据集合，方便后期切换真正API/DB
        /// </summary>
        private static List<RecipeModel> GetMockRecipes()
        {
            return new List<RecipeModel>
            {
                new RecipeModel
                {
                    RecipeCode = "RCP-3C-001",
                    RecipeName = "手机中框外观缺陷检测",
                    ProductCategory = "3C电子",
                    FlowName = "Main_Frame_Inspection_v2",
                    IsActive = true,
                    Description = "针对铝合金中框划痕、崩角的高精度检测配方",
                    UpdatedTime = DateTime.Now.AddDays(-1),
                    UpdatedBy = "张工",
                    ExposureTime = 1200.0,
                    Gain = 2.5,
                    ToleranceMm = 0.05
                },
                new RecipeModel
                {
                    RecipeCode = "RCP-BAT-002",
                    RecipeName = "锂电池极耳焊接质量检测",
                    ProductCategory = "新能源锂电",
                    FlowName = "Battery_Tab_Weld_Flow",
                    IsActive = false,
                    Description = "极耳焊点虚焊、炸飞、爆点红外与视觉融合分析",
                    UpdatedTime = DateTime.Now.AddDays(-3),
                    UpdatedBy = "李工",
                    ExposureTime = 800.0,
                    Gain = 1.0,
                    ToleranceMm = 0.10
                },
                new RecipeModel
                {
                    RecipeCode = "RCP-SEMI-003",
                    RecipeName = "晶圆Bumping金球尺寸测量",
                    ProductCategory = "半导体",
                    FlowName = "Wafer_Bump_Measure_Precise",
                    IsActive = false,
                    Description = "亚微米级金球高度及圆度测量配方",
                    UpdatedTime = DateTime.Now.AddDays(-5),
                    UpdatedBy = "王工",
                    ExposureTime = 2500.0,
                    Gain = 4.0,
                    ToleranceMm = 0.01
                },
                new RecipeModel
                {
                    RecipeCode = "RCP-AUTO-004",
                    RecipeName = "汽车刹车盘螺孔位置度",
                    ProductCategory = "汽车零部件",
                    FlowName = "Auto_Brake_Disc_Locating",
                    IsActive = false,
                    Description = "大视野高景深多螺孔中心距测量",
                    UpdatedTime = DateTime.Now.AddDays(-7),
                    UpdatedBy = "张工",
                    ExposureTime = 1500.0,
                    Gain = 1.5,
                    ToleranceMm = 0.20
                }
            };
        }

        private void LoadMockData()
        {
            var data = GetMockRecipes();
            AllRecipes = new ObservableCollection<RecipeModel>(data);
            FilteredRecipes = new ObservableCollection<RecipeModel>(data);
            SelectedRecipe = FilteredRecipes.FirstOrDefault();
        }

        #endregion

        #region 属性

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
            set => Set(ref _selectedRecipe, value);
        }

        private string _searchText;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value))
                {
                    OnSearch();
                }
            }
        }

        #endregion

        #region 命令与逻辑

        public ICommand SearchCommand { get; }
        public ICommand CreateRecipeCommand { get; }
        public ICommand ApplyRecipeCommand { get; }
        public ICommand DeleteRecipeCommand { get; }
        public ICommand SaveDetailCommand { get; }

        private void OnSearch()
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                FilteredRecipes = new ObservableCollection<RecipeModel>(AllRecipes);
            }
            else
            {
                var keyword = SearchText.Trim().ToLower();
                var results = AllRecipes.Where(r =>
                    (r.RecipeCode != null && r.RecipeCode.ToLower().Contains(keyword)) ||
                    (r.RecipeName != null && r.RecipeName.ToLower().Contains(keyword)) ||
                    (r.ProductCategory != null && r.ProductCategory.ToLower().Contains(keyword))
                );
                FilteredRecipes = new ObservableCollection<RecipeModel>(results);
            }

            if (SelectedRecipe == null || !FilteredRecipes.Contains(SelectedRecipe))
            {
                SelectedRecipe = FilteredRecipes.FirstOrDefault();
            }
        }

        private void OnCreateRecipe()
        {
            var newRecipe = new RecipeModel
            {
                RecipeCode = "RCP-NEW-" + DateTime.Now.ToString("fff"),
                RecipeName = "新建配方方案",
                ProductCategory = "通用分类",
                FlowName = "Default_Vision_Flow",
                IsActive = false,
                Description = "自定义新建检测配方",
                UpdatedTime = DateTime.Now,
                UpdatedBy = "CurrentOperator",
                ExposureTime = 1000,
                Gain = 1.0,
                ToleranceMm = 0.05
            };

            AllRecipes.Insert(0, newRecipe);
            OnSearch();
            SelectedRecipe = newRecipe;
        }

        private void OnApplyRecipe()
        {
            if (SelectedRecipe == null) return;

            foreach (var recipe in AllRecipes)
            {
                recipe.IsActive = (recipe == SelectedRecipe);
            }

            MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 已成功下发并应用至当前生产线！", "配方应用成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnDeleteRecipe()
        {
            if (SelectedRecipe == null) return;

            if (SelectedRecipe.IsActive)
            {
                MessageBox.Show("不能删除当前正在生产生效中的配方！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show($"确定要删除配方 [{SelectedRecipe.RecipeName}] 吗？", "警告", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm == MessageBoxResult.Yes)
            {
                AllRecipes.Remove(SelectedRecipe);
                OnSearch();
            }
        }

        private void OnSaveDetail()
        {
            if (SelectedRecipe == null) return;

            SelectedRecipe.UpdatedTime = DateTime.Now;
            MessageBox.Show($"配方 [{SelectedRecipe.RecipeName}] 参数配置保存成功！", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion
    }
}