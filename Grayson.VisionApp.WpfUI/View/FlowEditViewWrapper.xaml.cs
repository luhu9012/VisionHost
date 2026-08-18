using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Recipe.Services;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vison.FlowEdit.ViewModels;

namespace Grayson.Vision.WpfUI.View
{
    public partial class FlowEditViewWrapper : UserControl, INavigationAware
    {
        private readonly IRecipeStorageService _recipeStorage;
        private readonly IStationRepository _stationRepository;
        private RecipeModel _currentRecipe;

        public FlowEditViewWrapper()
        {
            InitializeComponent();
            _recipeStorage = Grayson.Vision.Repository.Services.RecipeStorageFactory.CreateRecipeStorageService();
            _stationRepository = StorageFactory.CreateStationRepository();
            AttachHostSaveHandler();
        }

        /// <summary>
        /// 🌟 获取子控件 FlowEditorControl 的 ViewModel (FlowVm)
        /// </summary>
        private FlowVm ViewModel => FlowEditorControl.DataContext as FlowVm;

        private void AttachHostSaveHandler()
        {
            if (ViewModel != null)
            {
                ViewModel.HostRecipeSaveHandler = SaveRecipeToStorage;
            }
        }

        /// <summary>
        /// 🌟 跨界面跳转进入时触发参数接收与编辑器加载
        /// </summary>
        public void OnNavigatedTo(object parameter)
        {
            AttachHostSaveHandler();
            if (ViewModel == null) return;

            if (parameter is RecipeModel recipe)
            {
                _currentRecipe = recipe;
            }
            else
            {
                _currentRecipe = ViewModel.ExportCurrentRecipe() ?? new RecipeModel();
                EnsureRecipeDefaults(_currentRecipe);
            }

            var stationContext = ResolveStationContext(_currentRecipe);
            ViewModel.ConfigureStationContext(stationContext.AvailableStations, stationContext.PreferredStationId);
            ViewModel.LoadRecipe(_currentRecipe);
        }

        /// <summary>
        /// 🌟 离开页面时同步最新流程变更
        /// </summary>
        public void OnNavigatedFrom()
        {
            if (ViewModel != null)
            {
                _currentRecipe = ViewModel.ExportCurrentRecipe();
            }
        }

        private FlowEditStationContext ResolveStationContext(RecipeModel recipe)
        {
            var allStations = (_stationRepository?.GetAllLines() ?? new List<LineConfigModel>())
                .Where(line => line?.Stations != null)
                .SelectMany(line => line.Stations)
                .Where(station => station != null && !string.IsNullOrWhiteSpace(station.StationId))
                .GroupBy(station => station.StationId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var enabledStations = allStations
                .Where(station => station.IsEnabled)
                .OrderBy(station => station.StationCode)
                .ThenBy(station => station.StationName)
                .ToList();

            var boundStation = FindBoundStation(allStations, recipe);
            if (boundStation != null && enabledStations.All(s => !string.Equals(s.StationId, boundStation.StationId, StringComparison.OrdinalIgnoreCase)))
            {
                enabledStations.Insert(0, boundStation);
            }

            return new FlowEditStationContext
            {
                AvailableStations = enabledStations,
                PreferredStationId = boundStation?.StationId
            };
        }

        private static StationConfigModel FindBoundStation(IEnumerable<StationConfigModel> stations, RecipeModel recipe)
        {
            if (stations == null || recipe == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(recipe.RecipeId))
            {
                var byId = stations.FirstOrDefault(station =>
                    string.Equals(station.BoundRecipeId, recipe.RecipeId, StringComparison.OrdinalIgnoreCase));
                if (byId != null)
                {
                    return byId;
                }
            }

            if (!string.IsNullOrWhiteSpace(recipe.RecipeName))
            {
                return stations.FirstOrDefault(station =>
                    string.Equals(station.BoundRecipeName, recipe.RecipeName, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private bool SaveRecipeToStorage(RecipeModel recipe)
        {
            if (recipe == null) return false;

            EnsureRecipeDefaults(recipe);
            recipe.LastModifiedTime = DateTime.Now;

            bool success = _recipeStorage.SaveRecipe(recipe);
            if (!success)
            {
                MessageBox.Show("配方保存失败，请检查存储目录权限或文件占用。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            _currentRecipe = recipe;
            return true;
        }

        private void EnsureRecipeDefaults(RecipeModel recipe)
        {
            if (recipe == null) return;

            if (string.IsNullOrWhiteSpace(recipe.RecipeId))
                recipe.RecipeId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(recipe.RecipeCode))
                recipe.RecipeCode = "RCP-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (string.IsNullOrWhiteSpace(recipe.RecipeName))
                recipe.RecipeName = "新建视觉配方";
            if (string.IsNullOrWhiteSpace(recipe.ProductCategory))
                recipe.ProductCategory = "通用分类";
            if (string.IsNullOrWhiteSpace(recipe.Version))
                recipe.Version = "1.0.0";
            if (string.IsNullOrWhiteSpace(recipe.Author))
                recipe.Author = GlobalData.Instance.CurrentUserName ?? "Admin";
            if (recipe.MainProcess == null)
                recipe.MainProcess = new Grayson.Vision.Contracts.Flow.Nodes.FlowProcessModel { ProcessName = recipe.RecipeName };
        }

        private sealed class FlowEditStationContext
        {
            public List<StationConfigModel> AvailableStations { get; set; } = new List<StationConfigModel>();
            public string PreferredStationId { get; set; }
        }
    }
}
