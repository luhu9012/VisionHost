//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationManageViewModel.cs
//===================================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

using RecipeModel = Grayson.Vision.Contracts.Recipe.Models.RecipeModel;
using RecipeDeviceMappingModel = Grayson.Vision.Contracts.Recipe.Models.RecipeDeviceMappingModel;
using ContractsStationConfig = Grayson.Vision.Contracts.Station.Models.StationConfigModel;
using ContractsLineConfig = Grayson.Vision.Contracts.Station.Models.LineConfigModel;
using HardwareDeviceModel = Grayson.Vision.WpfUI.Model.HardwareDeviceModel;

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Recipe.Services;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Core.Client;
using Grayson.Vision.Core.Station;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.Repository.Services;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using Newtonsoft.Json;

namespace Grayson.Vision.WpfUI.ViewModel
{
    #region 数据模型 (UI 专用本地模型)

    public class StationModel : ViewModelBase
    {
        public Action OnBoundRecipeChangedAction { get; set; }

        private string _stationId;
        public string StationId
        {
            get => _stationId;
            set => Set(ref _stationId, value);
        }

        private string _stationCode;
        public string StationCode
        {
            get => _stationCode;
            set => Set(ref _stationCode, value);
        }

        private string _stationName;
        public string StationName
        {
            get => _stationName;
            set => Set(ref _stationName, value);
        }

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set => Set(ref _isEnabled, value);
        }

        private int _timeoutMs = 3000;
        public int TimeoutMs
        {
            get => _timeoutMs;
            set => Set(ref _timeoutMs, value);
        }

        private RecipeModel _boundRecipe;
        public RecipeModel BoundRecipe
        {
            get => _boundRecipe;
            set
            {
                if (Set(ref _boundRecipe, value))
                {
                    OnBoundRecipeChangedAction?.Invoke();
                }
            }
        }

        public ObservableCollection<HardwareDeviceModel> HardwareDevices { get; set; }
        public ObservableCollection<RecipeDeviceMappingModel> RecipeDeviceMappings { get; set; }

        public StationModel()
        {
            HardwareDevices = new ObservableCollection<HardwareDeviceModel>();
            RecipeDeviceMappings = new ObservableCollection<RecipeDeviceMappingModel>();
        }
    }

    public class LineModel : ViewModelBase
    {
        public string LineId { get; set; }
        public string LineName { get; set; }
        public ObservableCollection<StationModel> Stations { get; set; }

        public LineModel()
        {
            Stations = new ObservableCollection<StationModel>();
        }
    }

    #endregion

    // 2. 新增快捷跳转命令
   
    public class StationManageViewModel : ViewModelBase
    {
        private readonly StationRuntimeManager _runtimeManager;
        private readonly StationConfigService _configService;
        private readonly IRecipeStorageService _recipeStorage;
        private readonly INavigationService _navigationService;
        public RelayCommand OpenCalibrationCenterCommand { get; }

        public StationManageViewModel(
            StationRuntimeManager runtimeManager = null,
            StationConfigService configService = null,
            IRecipeStorageService recipeStorage = null)
        {
            _runtimeManager = runtimeManager ?? new StationRuntimeManager(App.StationHostRuntime);
            _configService = configService ?? new StationConfigService();
            _recipeStorage = recipeStorage ?? Grayson.Vision.Repository.Services.RecipeStorageFactory.CreateRecipeStorageService();

            GlobalHardwarePool = new ObservableCollection<HardwareDeviceModel>();
            AvailableRecipes = new ObservableCollection<RecipeModel>();
            ProductionLines = new ObservableCollection<LineModel>();

            AddLineCommand = new RelayCommand(_ => OnAddLine());
            AddStationCommand = new RelayCommand(_ => OnAddStation(), _ => SelectedLine != null);
            DeleteNodeCommand = new RelayCommand(_ => OnDeleteNode(), _ => SelectedLine != null || SelectedStation != null);
            AddHardwareCommand = new RelayCommand(_ => OnAddHardware(), _ => SelectedStation != null);
            RemoveHardwareCommand = new RelayCommand(_ => OnRemoveHardware(), _ => SelectedStation != null && SelectedHardware != null);
            SaveStationConfigCommand = new RelayCommand(async _ => await OnSaveStationConfigAsync(), _ => SelectedStation != null);

            InitializeFromServices();

            // initialize per-tab view models
            HardwareAllocationVm = new HardwareAllocationViewModel(this);
            RecipeMappingVm = new RecipeMappingViewModel(this);
            // 初始化快捷跳转命令
            OpenCalibrationCenterCommand = new RelayCommand(
                _ => OnOpenCalibrationCenter(),
                _ => SelectedStation != null
            );

        }
        /// <summary>
        /// 跳转至标定中心页面
        /// </summary>
        private void OnOpenCalibrationCenter()
        {
            if (SelectedStation == null) return;

            // 组装上下文数据传递给标定中心
            var navContext = new StationNavigationContext
            {
                StationCode = SelectedStation.StationCode,
                StationName = SelectedStation.StationName,
                // 将 LogicalDeviceMappings 改为 StationModel 中实际定义的 RecipeDeviceMappings
                DeviceId = SelectedStation.RecipeDeviceMappings?.FirstOrDefault()?.MappedDeviceId
                           ?? SelectedStation.RecipeDeviceMappings?.FirstOrDefault()?.LogicalDeviceId
            };
            NavigationService.Current?.NavigateTo(PageType.CalibrationManage, navContext);
        }

        public HardwareAllocationViewModel HardwareAllocationVm { get; }
        public RecipeMappingViewModel RecipeMappingVm { get; }
 

        #region 属性

        private ObservableCollection<LineModel> _productionLines;
        public ObservableCollection<LineModel> ProductionLines
        {
            get => _productionLines;
            set => Set(ref _productionLines, value);
        }

        private LineModel _selectedLine;
        public LineModel SelectedLine
        {
            get => _selectedLine;
            set
            {
                if (Set(ref _selectedLine, value))
                {
                    RefreshCommandStates();
                }
            }
        }

        private StationModel _selectedStation;
        public StationModel SelectedStation
        {
            get => _selectedStation;
            set
            {
                if (Set(ref _selectedStation, value))
                {
                    OnStationSelectedChanged();
                    RefreshCommandStates();
                }
            }
        }

        private HardwareDeviceModel _selectedHardware;
        public HardwareDeviceModel SelectedHardware
        {
            get => _selectedHardware;
            set
            {
                if (Set(ref _selectedHardware, value))
                {
                    RefreshCommandStates();
                }
            }
        }

        public ObservableCollection<HardwareDeviceModel> GlobalHardwarePool { get; set; }
        public ObservableCollection<RecipeModel> AvailableRecipes { get; set; }

        #endregion

        #region 命令定义

        public RelayCommand AddLineCommand { get; }
        public RelayCommand AddStationCommand { get; }
        public RelayCommand DeleteNodeCommand { get; }
        public RelayCommand AddHardwareCommand { get; }
        public RelayCommand RemoveHardwareCommand { get; }
        public RelayCommand SaveStationConfigCommand { get; }

        #endregion

        #region 私有辅助方法

        /// <summary>
        /// 主动刷新 UI 命令状态
        /// </summary>
        private void RefreshCommandStates()
        {
            AddStationCommand.RaiseCanExecuteChanged();
            DeleteNodeCommand.RaiseCanExecuteChanged();
            AddHardwareCommand.RaiseCanExecuteChanged();
            RemoveHardwareCommand.RaiseCanExecuteChanged();
            SaveStationConfigCommand.RaiseCanExecuteChanged();
            OpenCalibrationCenterCommand.RaiseCanExecuteChanged();
        }

        private void OnStationSelectedChanged()
        {
            if (SelectedStation == null) return;

            // 订阅工位的配方切换回调
            SelectedStation.OnBoundRecipeChangedAction = SyncRecipeMappings;

            if (SelectedStation.BoundRecipe != null && SelectedStation.RecipeDeviceMappings.Count == 0)
            {
                SyncRecipeMappings();
            }
        }

        private void SyncRecipeMappings()
        {
            if (SelectedStation?.BoundRecipe == null) return;

            SelectedStation.RecipeDeviceMappings.Clear();
            if (SelectedStation.BoundRecipe.LogicalDevices != null)
            {
                foreach (var logical in SelectedStation.BoundRecipe.LogicalDevices)
                {
                    var preferredHardware = SelectedStation.HardwareDevices
                        .FirstOrDefault(h => h.DeviceType?.Equals(logical.LogicalDeviceType, StringComparison.OrdinalIgnoreCase) == true)
                        ?? SelectedStation.HardwareDevices.FirstOrDefault()
                        ?? GlobalHardwarePool.FirstOrDefault();

                    SelectedStation.RecipeDeviceMappings.Add(new RecipeDeviceMappingModel
                    {
                        LogicalDeviceId = logical.LogicalDeviceId,
                        LogicalDeviceName = logical.LogicalDeviceName,
                        LogicalDeviceType = logical.LogicalDeviceType,
                        RequiredSpec = logical.RequiredSpec,
                        MappedDeviceId = preferredHardware?.DeviceId
                    });
                }
            }
        }

        #endregion

        #region 纯数据库保存（不涉及 Runtime 操作）

        /// <summary>
        /// 仅保存工位配置到数据库，不涉及 Runtime 操作和硬件同步。
        /// 用于添加/删除硬件后立即保存，避免触发 Runtime 同步导致数据丢失。
        /// </summary>
        private async Task SaveStationToDatabaseAsync()
        {
            if (SelectedStation == null) return;

            try
            {
                // 在仅保存数据库时同样回写映射并持久化配方，避免配方视图状态滞后
                if (!SyncMappingsBackToBoundRecipe(SelectedStation, _recipeStorage))
                {
                    MessageBox.Show("保存配方设备映射信息失败！", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var stationConfig = ToStationConfigModel(SelectedStation);
                if (!_configService.SaveStation(stationConfig))
                {
                    MessageBox.Show("工位配置保存到数据库失败！", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 仅显示保存成功提示，不进行 Runtime 操作
                // (Runtime 操作由用户显式点击"保存工位并同步 Runtime"按钮时触发)
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存工位配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 把工位侧最新的 RecipeDeviceMappings 同步回写到 BoundRecipe.LogicalDevices，
        /// 并调用 recipe 存储持久化，确保配方管理视图中的 IsBoundToPhysical 状态与工位侧保持一致。
        /// </summary>
        private static bool SyncMappingsBackToBoundRecipe(
            StationModel station,
            IRecipeStorageService recipeStorage)
        {
            var recipe = station?.BoundRecipe;
            if (recipe == null) return true;

            if (recipe.LogicalDevices == null)
            {
                recipe.LogicalDevices = new List<RecipeDeviceMappingModel>();
            }

            var stationMappings = station.RecipeDeviceMappings
                ?.Where(m => !string.IsNullOrEmpty(m.LogicalDeviceId))
                .ToDictionary(m => m.LogicalDeviceId, m => m.MappedDeviceId)
                ?? new Dictionary<string, string>();

            foreach (var logical in recipe.LogicalDevices.Where(l => !string.IsNullOrEmpty(l.LogicalDeviceId)))
            {
                if (stationMappings.TryGetValue(logical.LogicalDeviceId, out var mappedId))
                {
                    logical.MappedDeviceId = mappedId;
                }
            }

            return recipeStorage?.SaveRecipe(recipe) ?? true;
        }

        #endregion

        #region 对接 Core 的保存与运行逻辑

        private async Task OnSaveStationConfigAsync()
        {
            if (SelectedStation == null) return;

            try
            {
                // 1. 将 station 侧更新的设备映射回写到绑定的配方模型并持久化，保证 RecipeManageView 显示一致
                if (!SyncMappingsBackToBoundRecipe(SelectedStation, _recipeStorage))
                {
                    MessageBox.Show("保存配方设备映射信息失败！", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 2. 先持久化配置到数据库（包括 LeaseDeviceIds 和 DeviceMappings）
                var stationConfig = ToStationConfigModel(SelectedStation);
                //Console.WriteLine(JsonConvert.SerializeObject(stationConfig));
                if (!_configService.SaveStation(stationConfig))
                {
                    MessageBox.Show("工位配置保存到数据库失败！", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 3. 同步 Core 运行时：按 BoundRecipeId 加载完整配方，避免 StationConfigPo 过深嵌套
                var boundRecipe = !string.IsNullOrEmpty(stationConfig.BoundRecipeId)
                    ? _recipeStorage.LoadRecipe(stationConfig.BoundRecipeId)
                    : null;

                var hostRuntime = App.StationHostRuntime as IStationHostRuntime
                                  ?? StationHostRuntime.GlobalInstance;

                if (hostRuntime != null && hostRuntime.GetStationClient(SelectedStation.StationCode) != null)
                {
                    hostRuntime.RemoveStation(SelectedStation.StationCode);
                }

                IWorkerClient client = null;
                if (hostRuntime != null)
                {
                    client = await hostRuntime.CreateStationWithRecipeAsync(
                        SelectedStation.StationCode,
                        boundRecipe,
                        stationConfig.DeviceMappings,
                        WorkMode.Production);
                }

                if (client == null)
                {
                    // 3. 如果 Core 站点创建失败，尝试兼容旧路径
                    client = await _runtimeManager.CreateAndConnectStationAsync(
                        SelectedStation.StationCode,
                        WorkerConnectMode.Embedded);

                    if (boundRecipe?.MainProcess != null && client != null)
                    {
                        await client.LoadRecipeAsync(boundRecipe.MainProcess);
                    }
                }

                // 注意：不调用 SyncHardwareFromRuntimeLeases，因为：
                // 1. UI 中的 HardwareDevices 已经通过 LeaseDeviceIds 持久化到数据库
                // 2. Runtime 中的租赁关系是实时的，不应该用来覆盖用户在 UI 中的配置
                // 3. 如果从 Runtime 租赁同步，反而会丢失硬件列表（当 Runtime 中没有租赁时）

                MessageBox.Show($"工位 [{SelectedStation.StationName}] ({SelectedStation.StationCode}) 配置已保存并初始化 Core 站点成功！",
                                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存工位或初始化 Core 站点失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private ContractsStationConfig ToStationConfigModel(StationModel station)
        {
            var config = new ContractsStationConfig
            {
                StationId = string.IsNullOrEmpty(station.StationId) ? _configService.GenerateStationId() : station.StationId,
                StationCode = station.StationCode,
                StationName = station.StationName,
                LineId = SelectedLine?.LineId,
                LineName = SelectedLine?.LineName,
                IsEnabled = station.IsEnabled,
                TimeoutMs = station.TimeoutMs,
                BoundRecipeId = station.BoundRecipe?.RecipeId,
                BoundRecipeName = station.BoundRecipe?.RecipeName
            };

            // 保存工位领用的硬件设备 ID 列表
            config.LeaseDeviceIds = new List<string>();
            if (station.HardwareDevices != null)
            {
                foreach (var device in station.HardwareDevices)
                {
                    config.LeaseDeviceIds.Add(device.DeviceId);
                }
            }

            // 保存配方中逻辑设备映射关系
            config.DeviceMappings = new List<RecipeDeviceMappingModel>();
            if (station.RecipeDeviceMappings != null)
            {
                foreach (var mapping in station.RecipeDeviceMappings)
                {
                    config.DeviceMappings.Add(mapping);
                }
            }

            return config;
        }

        #endregion

        #region 节点与硬件增删逻辑

        private void OnAddLine()
        {
            int count = ProductionLines.Count + 1;
            var newLine = new LineModel
            {
                LineId = _configService.GenerateLineId(),
                LineName = $"新产线_0{count}"
            };
            ProductionLines.Add(newLine);
            SelectedLine = newLine;
        }

        private void OnAddStation()
        {
            if (SelectedLine == null)
            {
                MessageBox.Show("请先在左侧选择需要添加工位的产线！", "操作提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int count = SelectedLine.Stations.Count + 1;
            var newStation = new StationModel
            {
                StationId = _configService.GenerateStationId(),
                StationCode = $"ST_0{count}",
                StationName = $"新工位_0{count}",
                IsEnabled = true,
                TimeoutMs = 3000
            };
            SelectedLine.Stations.Add(newStation);
            SelectedStation = newStation;
        }

        private void OnDeleteNode()
        {
            if (SelectedStation != null && SelectedLine != null)
            {
                if (MessageBox.Show($"确定要删除工位 [{SelectedStation.StationName}] 吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    // 1. 同步释放 Core 运行时中该工位领用的设备
                    var hostRuntime = App.StationHostRuntime as IStationHostRuntime
                                      ?? StationHostRuntime.GlobalInstance;
                    hostRuntime?.RemoveStation(SelectedStation.StationCode);

                    // 2. 删除数据库配置
                    _configService.DeleteStation(SelectedStation.StationId);
                    SelectedLine.Stations.Remove(SelectedStation);
                    SelectedStation = SelectedLine.Stations.FirstOrDefault();
                }
            }
                else if (SelectedLine != null)
            {
                if (MessageBox.Show($"确定要删除产线 [{SelectedLine.LineName}] 及其下属所有工位吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    // 1. 同步释放 Core 运行时中产线下所有工位领用的设备
                    var hostRuntime = App.StationHostRuntime as IStationHostRuntime
                                      ?? StationHostRuntime.GlobalInstance;
                    foreach (var station in SelectedLine.Stations.ToList())
                    {
                        hostRuntime?.RemoveStation(station.StationCode);
                        _configService.DeleteStation(station.StationId);
                    }

                    // 2. 删除产线本身
                    _configService.DeleteLine(SelectedLine.LineId);
                    ProductionLines.Remove(SelectedLine);
                    SelectedLine = ProductionLines.FirstOrDefault();
                    SelectedStation = null;
                }
            }
        }

  

        private async void OnAddHardware()
        {
            if (SelectedStation == null) return;

            // 1. 过滤出全局硬件池中尚未被当前工位领用的硬件
            var unassignedDevices = GlobalHardwarePool
                .Where(g => !SelectedStation.HardwareDevices.Any(h => h.DeviceId == g.DeviceId))
                .ToList();

            if (!unassignedDevices.Any())
            {
                MessageBox.Show("【全局硬件池】中的所有设备已全部领用，或当前无可用硬件设备！",
                                "领用提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 2. 实例化弹窗 View & ViewModel
            var selectVm = new HardwareSelectViewModel(unassignedDevices);
            var dialog = new View.HardwareSelectWindow(selectVm)
            {
                Owner = Application.Current.MainWindow
            };

            // 3. 打开模态弹窗并接收选中的硬件列表
            if (dialog.ShowDialog() == true)
            {
                var selectedDevices = selectVm.GetSelectedDevices();
                if (selectedDevices.Count == 0)
                {
                    MessageBox.Show("未勾选任何硬件设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                foreach (var device in selectedDevices)
                {
                    SelectedStation.HardwareDevices.Add(device);
                }

                MessageBox.Show($"已成功将 {selectedDevices.Count} 台硬件设备领用并添加到工位 [{SelectedStation.StationName}]！",
                                "领用成功", MessageBoxButton.OK, MessageBoxImage.Information);

                // 4. 仅保存到数据库，不涉及 Runtime 操作（避免触发 SyncHardwareFromRuntimeLeases 清空数据）
                await SaveStationToDatabaseAsync();
            }
        }
        private async void OnRemoveHardware()
        {
            if (SelectedStation != null && SelectedHardware != null)
            {
                string devName = SelectedHardware.DeviceName;
                SelectedStation.HardwareDevices.Remove(SelectedHardware);
                SelectedHardware = null;

                MessageBox.Show($"硬件设备 [{devName}] 已从本工位领用列表中移除！", "移除成功", MessageBoxButton.OK, MessageBoxImage.Information);

                // 仅保存到数据库，不涉及 Runtime 操作（避免触发 SyncHardwareFromRuntimeLeases 清空数据）
                await SaveStationToDatabaseAsync();
            }
        }

        public void OnSelectedNodeChanged(object selectedItem)
        {
            if (selectedItem is LineModel line)
            {
                SelectedLine = line;
                SelectedStation = null;
            }
            else if (selectedItem is StationModel station)
            {
                SelectedStation = station;
                SelectedLine = ProductionLines.FirstOrDefault(l => l.Stations.Contains(station));
            }
        }

        #endregion

        #region 真实数据初始化

        private void InitializeFromServices()
        {
            GlobalHardwarePool.Clear();
            foreach (var device in _configService.LoadAvailableHardwareDevices())
            {
                GlobalHardwarePool.Add(device);
            }

            AvailableRecipes.Clear();
            foreach (var recipe in _recipeStorage.GetAllRecipes())
            {
                AvailableRecipes.Add(recipe);
            }

            ProductionLines.Clear();
            foreach (var lineConfig in _configService.LoadAllLines())
            {
                ProductionLines.Add(MapFromLineConfig(lineConfig));
            }
        }

        /// <summary>
        /// 根据 Core 设备池实际租赁记录，把 UI 本地工位的 HardwareDevices 同步为租赁设备。
        /// </summary>
        private void SyncHardwareFromRuntimeLeases(IStationHostRuntime hostRuntime, StationModel station)
        {
            if (hostRuntime == null || station == null) return;

            var leasedKeys = hostRuntime.DevicePool?.GetLeasedDeviceKeys(station.StationCode) ?? Enumerable.Empty<string>();
            var leasedDevices = leasedKeys
                .Select(key => GlobalHardwarePool.FirstOrDefault(h => h.DeviceId == key))
                .Where(h => h != null)
                .ToList();

            station.HardwareDevices.Clear();
            foreach (var device in leasedDevices)
            {
                station.HardwareDevices.Add(device);
            }
        }

        private LineModel MapFromLineConfig(ContractsLineConfig lineConfig)
        {
            var line = new LineModel
            {
                LineId = lineConfig.LineId,
                LineName = lineConfig.LineName
            };

            foreach (var stationConfig in lineConfig.Stations ?? new List<ContractsStationConfig>())
            {
                var station = new StationModel
                {
                    StationId = stationConfig.StationId,
                    StationCode = stationConfig.StationCode,
                    StationName = stationConfig.StationName,
                    IsEnabled = stationConfig.IsEnabled,
                    TimeoutMs = stationConfig.TimeoutMs,
                    BoundRecipe = !string.IsNullOrEmpty(stationConfig.BoundRecipeId)
                        ? AvailableRecipes.FirstOrDefault(r => r.RecipeId == stationConfig.BoundRecipeId)
                        : null
                };

                // 从持久化数据恢复工位领用的硬件设备列表
                if (stationConfig.LeaseDeviceIds != null && stationConfig.LeaseDeviceIds.Count > 0)
                {
                    foreach (var deviceId in stationConfig.LeaseDeviceIds)
                    {
                        var hardware = GlobalHardwarePool.FirstOrDefault(h => h.DeviceId == deviceId);
                        if (hardware != null && !station.HardwareDevices.Any(h => h.DeviceId == deviceId))
                        {
                            station.HardwareDevices.Add(hardware);
                        }
                    }
                }

                // 从持久化数据恢复配方中的逻辑设备映射关系
                if (stationConfig.DeviceMappings != null)
                {
                    foreach (var mapping in stationConfig.DeviceMappings)
                    {
                        station.RecipeDeviceMappings.Add(mapping);
                    }
                }

                line.Stations.Add(station);
            }

            return line;
        }


        #endregion
    }
}