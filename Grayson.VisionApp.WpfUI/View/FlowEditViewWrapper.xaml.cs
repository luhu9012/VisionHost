using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Recipe.Services;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vison.FlowEdit.ViewModels;
using Newtonsoft.Json;

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
            AttachHostHandlers();
        }

        /// <summary>
        /// 🌟 获取子控件 FlowEditorControl 的 ViewModel (FlowVm)
        /// </summary>
        private FlowVm ViewModel => FlowEditorControl.DataContext as FlowVm;

        /// <summary>
        /// 🌟 向编辑器注入宿主能力：
        /// - HostRecipeSaveHandler：配方落盘到配方库（Recipes 目录）
        /// - HostRecipeLoadHandler：按工位绑定的 BoundRecipeId/BoundRecipeName 反读配方，
        ///   支撑"编辑器内切换工位 → 自动加载该工位绑定的配方及其编排节点；未绑定则清空画布"
        /// </summary>
        private void AttachHostHandlers()
        {
            if (ViewModel == null) return;

            ViewModel.HostRecipeSaveHandler = SaveRecipeToStorage;
            ViewModel.HostRecipeLoadHandler = LoadRecipeForStation;
        }

        /// <summary>
        /// 按工位配置的绑定信息加载配方实体（BoundRecipeId 优先；BoundRecipeName 存的是显示名，需按名/编号兜底匹配）。
        /// 返回 null 表示该工位未绑定配方或绑定已失联（配方被删除/改名）→ 编辑器按"未绑定"清空画布。
        /// </summary>
        private RecipeModel LoadRecipeForStation(StationConfigModel station)
        {
            if (station == null) return null;

            try
            {
                if (!string.IsNullOrWhiteSpace(station.BoundRecipeId))
                {
                    var byId = _recipeStorage.LoadRecipe(station.BoundRecipeId);
                    if (byId != null) return byId;
                }

                if (!string.IsNullOrWhiteSpace(station.BoundRecipeName))
                {
                    var all = _recipeStorage.GetAllRecipes() ?? new List<RecipeModel>();
                    var byCode = all.FirstOrDefault(r =>
                        string.Equals(r.RecipeCode, station.BoundRecipeName, StringComparison.OrdinalIgnoreCase));
                    if (byCode != null) return byCode;

                    var byName = all.Where(r =>
                        string.Equals(r.RecipeName, station.BoundRecipeName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (byName.Count > 1)
                    {
                        // 配方名可重复（OnCreateRecipe 固定命名"新建视觉配方"），按名兜底存在歧义 → 记告警便于定位
                        LogBus.Warn("FlowEdit",
                            $"工位 [{station.StationCode ?? station.StationId}] 的 BoundRecipeName='{station.BoundRecipeName}' " +
                            $"命中 {byName.Count} 份同名配方，已取首条 [{byName[0].RecipeCode}]；" +
                            $"建议在【配方管理】重新下发绑定以写入唯一 BoundRecipeId。");
                    }
                    if (byName.Count > 0) return byName[0];
                }
            }
            catch
            {
                // 读取异常按"未绑定"处理，由编辑器清空画布并记录告警
            }

            return null;
        }

        /// <summary>
        /// 🌟 跨界面跳转进入时触发参数接收与编辑器加载
        /// </summary>
        /// <summary>
        /// 🌟 跨界面跳转进入时触发参数接收与编辑器加载
        /// </summary>
        public void OnNavigatedTo(object parameter)
        {
            //Console.WriteLine( JsonConvert.SerializeObject(parameter));
            AttachHostHandlers();
            if (ViewModel == null) return;

            if (parameter is RecipeModel recipe)
            {
                // 🌟 核心：直接使用引用，不进行 JSON 深拷贝，彻底解决线段搞乱和连线断裂问题
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
        /// 🌟 离开页面时同步最新流程变更并保存
        /// </summary>
        public void OnNavigatedFrom()
        {
            //if (ViewModel != null)
            //{
            //    _currentRecipe = ViewModel.ExportCurrentRecipe();
            //    if (_currentRecipe != null)
            //    {
            //        EnsureRecipeDefaults(_currentRecipe);
            //        _currentRecipe.LastModifiedTime = DateTime.Now;
            //        // 退出页面时立即落盘
            //        _recipeStorage.SaveRecipe(_currentRecipe);
            //    }
            //}
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

            // 🌟 核心修复（2026-09-10）：保存前重新从流程拓扑提取逻辑设备清单，
            //   否则用户在相机采集节点改了「相机逻辑名字」（CameraAlias）后，
            //   recipe.LogicalDevices[].LogicalDeviceId 仍是旧名，导致：
            //   - 工位运行时按旧 ID 注册逻辑设备，而 AcquireImageExecutor 用新名 GetHardware → 找不到相机；
            //   - 配方管理/工位逻辑映射 Tab 显示的 ID 与流程实际不符，无法正确映射。
            SyncLogicalDevicesFromFlow(recipe);

            bool success = _recipeStorage.SaveRecipe(recipe);
            if (!success)
            {
                MessageBox.Show("配方保存失败，请检查存储目录权限或文件占用。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            _currentRecipe = recipe;
            return true;
        }

        /// <summary>
        /// 从 MainProcess 递归重新提取逻辑设备清单，并尽力保留旧的物理绑定（MappedDeviceId）。
        /// 改名场景：LogicalDeviceId 已变（旧 ID 匹配不上），退而按 LogicalDeviceType（同类设备）兜底继承物理映射，
        /// 避免"改个相机名字就要重新映射硬件"的割裂体验。
        /// </summary>
        private static void SyncLogicalDevicesFromFlow(RecipeModel recipe)
        {
            if (recipe?.MainProcess == null) return;

            var extracted = RecipeDeviceExtractor.ExtractLogicalDevices(recipe.MainProcess);
            if (extracted == null || extracted.Count == 0)
            {
                // 提取为空（流程可能尚未初始化完成）→ 保留既有清单兜底，避免误清空
                return;
            }

            var oldMappings = recipe.LogicalDevices?
                .Where(d => !string.IsNullOrEmpty(d.LogicalDeviceId))
                .ToList()
                ?? new List<RecipeDeviceMappingModel>();

            foreach (var device in extracted)
            {
                // 1. 优先按 LogicalDeviceId 精确匹配（未改名场景，保留原映射）
                var exact = oldMappings.FirstOrDefault(d =>
                    string.Equals(d.LogicalDeviceId, device.LogicalDeviceId, StringComparison.OrdinalIgnoreCase));
                if (exact != null && !string.IsNullOrEmpty(exact.MappedDeviceId))
                {
                    device.MappedDeviceId = exact.MappedDeviceId;
                    continue;
                }

                // 2. 改名兜底：按 LogicalDeviceType 找同类设备且已绑定的旧映射，继承其物理设备
                var sameType = oldMappings.FirstOrDefault(d =>
                    !string.IsNullOrEmpty(d.MappedDeviceId)
                    && string.Equals(d.LogicalDeviceType, device.LogicalDeviceType, StringComparison.OrdinalIgnoreCase));
                if (sameType != null)
                {
                    device.MappedDeviceId = sameType.MappedDeviceId;
                }
            }

            recipe.LogicalDevices = extracted;
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
