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
using ContractsDeviceMapping = Grayson.Vision.Contracts.Station.Models.DeviceMappingModel;
using ContractsLineConfig = Grayson.Vision.Contracts.Station.Models.LineConfigModel;

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Recipe.Services;
using Grayson.Vision.Core.Client;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    #region 数据模型 (UI 专用本地模型)

    public class HardwareDeviceModel : ViewModelBase
    {
        public string DeviceId { get; set; }
        public string DeviceCode { get; set; }
        public string DeviceName { get; set; }
        public string DeviceType { get; set; }
        public string BrandName { get; set; }
        public string ConnectionString { get; set; }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (Set(ref _isConnected, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string StatusText => IsConnected ? "在线" : "离线";
        public string Remark { get; set; }
    }

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

    public class StationManageViewModel : ViewModelBase
    {
        private readonly StationRuntimeManager _runtimeManager;
        private readonly StationConfigService _configService;
        private readonly IRecipeRepository _recipeRepo;

        public StationManageViewModel(
            StationRuntimeManager runtimeManager = null,
            StationConfigService configService = null,
            IRecipeRepository recipeRepo = null)
        {
            _runtimeManager = runtimeManager ?? new StationRuntimeManager();
            _configService = configService ?? new StationConfigService();
            _recipeRepo = recipeRepo ?? StorageFactory.CreateRecipeRepository();

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
        }

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

        #region 对接 Core.Client 的保存与运行逻辑

        private async Task OnSaveStationConfigAsync()
        {
            if (SelectedStation == null) return;

            try
            {
                var stationConfig = ToStationConfigModel(SelectedStation);
                _configService.SaveStation(stationConfig);

                var client = await _runtimeManager.CreateAndConnectStationAsync(
                    SelectedStation.StationCode,
                    WorkerConnectMode.Embedded
                );

                if (SelectedStation.BoundRecipe?.MainProcess != null && client != null)
                {
                    await client.LoadRecipeAsync(SelectedStation.BoundRecipe.MainProcess);
                }

                MessageBox.Show($"工位 [{SelectedStation.StationName}] ({SelectedStation.StationCode}) 配置已保存并初始化 Client 成功！",
                                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存工位或连接 Core.Client 失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                BoundRecipe = station.BoundRecipe
            };

            foreach (var hardware in station.HardwareDevices)
            {
                config.DeviceMappings.Add(new ContractsDeviceMapping
                {
                    LogicalDeviceId = hardware.DeviceId,
                    LogicalDeviceName = hardware.DeviceName,
                    LogicalDeviceType = hardware.DeviceType,
                    MappedDeviceKey = hardware.DeviceId,
                    MappedDeviceName = hardware.DeviceName
                });
            }

            foreach (var mapping in station.RecipeDeviceMappings)
            {
                var existing = config.DeviceMappings.FirstOrDefault(d => d.LogicalDeviceId == mapping.LogicalDeviceId);
                if (existing != null)
                {
                    existing.MappedDeviceKey = mapping.MappedDeviceId;
                    var mappedHardware = GlobalHardwarePool.FirstOrDefault(h => h.DeviceId == mapping.MappedDeviceId);
                    existing.MappedDeviceName = mappedHardware?.DeviceName;
                }
                else
                {
                    var mappedHardware = GlobalHardwarePool.FirstOrDefault(h => h.DeviceId == mapping.MappedDeviceId);
                    config.DeviceMappings.Add(new ContractsDeviceMapping
                    {
                        LogicalDeviceId = mapping.LogicalDeviceId,
                        LogicalDeviceName = mapping.LogicalDeviceName,
                        LogicalDeviceType = mapping.LogicalDeviceType,
                        RequiredSpec = mapping.RequiredSpec,
                        MappedDeviceKey = mapping.MappedDeviceId,
                        MappedDeviceName = mappedHardware?.DeviceName
                    });
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
                    _configService.DeleteStation(SelectedStation.StationId);
                    SelectedLine.Stations.Remove(SelectedStation);
                    SelectedStation = SelectedLine.Stations.FirstOrDefault();
                }
            }
            else if (SelectedLine != null)
            {
                if (MessageBox.Show($"确定要删除产线 [{SelectedLine.LineName}] 及其下属所有工位吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _configService.DeleteLine(SelectedLine.LineId);
                    ProductionLines.Remove(SelectedLine);
                    SelectedLine = ProductionLines.FirstOrDefault();
                    SelectedStation = null;
                }
            }
        }

  

        private void OnAddHardware()
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
            }
        }
        private void OnRemoveHardware()
        {
            if (SelectedStation != null && SelectedHardware != null)
            {
                string devName = SelectedHardware.DeviceName;
                SelectedStation.HardwareDevices.Remove(SelectedHardware);
                SelectedHardware = null;

                MessageBox.Show($"硬件设备 [{devName}] 已从本工位领用列表中移除！", "移除成功", MessageBoxButton.OK, MessageBoxImage.Information);
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
            foreach (var recipe in _recipeRepo.GetAll().Select(r => r.Model))
            {
                AvailableRecipes.Add(recipe);
            }

            ProductionLines.Clear();
            foreach (var lineConfig in _configService.LoadAllLines())
            {
                ProductionLines.Add(MapFromLineConfig(lineConfig));
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
                    BoundRecipe = stationConfig.BoundRecipe
                };

                foreach (var mapping in stationConfig.DeviceMappings ?? new List<ContractsDeviceMapping>())
                {
                    var hardware = GlobalHardwarePool.FirstOrDefault(h => h.DeviceId == mapping.MappedDeviceKey);
                    if (hardware != null && !station.HardwareDevices.Any(h => h.DeviceId == hardware.DeviceId))
                    {
                        station.HardwareDevices.Add(hardware);
                    }
                }

                line.Stations.Add(station);
            }

            return line;
        }

        #endregion
    }
}