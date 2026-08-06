//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationManageViewModel.cs
// 说 明: 产线工位管理 ViewModel (修复版本)
//===================================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

// 🌟 解决 CS0104 命名空间冲突：明确指定使用的 Model 类型
using RecipeModel = Grayson.Vision.Contracts.Recipe.Models.RecipeModel;
using RecipeDeviceMappingModel = Grayson.Vision.Contracts.Recipe.Models.RecipeDeviceMappingModel;

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Core.Client;
using Grayson.Vision.WpfUI.Common;

namespace Grayson.Vision.WpfUI.ViewModel
{
    #region 数据模型 (UI 专用本地模型)

    public class HardwareDeviceModel : ViewModelBase
    {
        public string DeviceId { get; set; }
        public string DeviceCode { get; set; }
        public string DeviceName { get; set; }
        public string DeviceType { get; set; }
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
            set => Set(ref _boundRecipe, value);
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

        public StationManageViewModel(StationRuntimeManager runtimeManager = null)
        {
            _runtimeManager = runtimeManager ?? new StationRuntimeManager();

            // 初始化集合
            GlobalHardwarePool = new ObservableCollection<HardwareDeviceModel>();
            AvailableRecipes = new ObservableCollection<RecipeModel>();
            ProductionLines = new ObservableCollection<LineModel>();

            // 命令实例化 (解决 AsyncRelayCommand 未找到问题，统一使用 RelayCommand 包装 async 委托)
            AddLineCommand = new RelayCommand(_ => OnAddLine());
            AddStationCommand = new RelayCommand(_ => OnAddStation(), _ => SelectedLine != null);
            DeleteNodeCommand = new RelayCommand(_ => OnDeleteNode(), _ => SelectedLine != null || SelectedStation != null);

            AddHardwareCommand = new RelayCommand(_ => OnAddHardware(), _ => SelectedStation != null);
            RemoveHardwareCommand = new RelayCommand(_ => OnRemoveHardware(), _ => SelectedStation != null && SelectedHardware != null);
            SaveStationConfigCommand = new RelayCommand(async _ => await OnSaveStationConfigAsync(), _ => SelectedStation != null);

            InitMockData();
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
            set => Set(ref _selectedLine, value);
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
                }
            }
        }

        private HardwareDeviceModel _selectedHardware;
        public HardwareDeviceModel SelectedHardware
        {
            get => _selectedHardware;
            set => Set(ref _selectedHardware, value);
        }

        public ObservableCollection<HardwareDeviceModel> GlobalHardwarePool { get; set; }
        public ObservableCollection<RecipeModel> AvailableRecipes { get; set; }

        #endregion

        #region 命令定义

        public ICommand AddLineCommand { get; }
        public ICommand AddStationCommand { get; }
        public ICommand DeleteNodeCommand { get; }
        public ICommand AddHardwareCommand { get; }
        public ICommand RemoveHardwareCommand { get; }
        public ICommand SaveStationConfigCommand { get; }

        #endregion

        #region 对接 Core.Client 的保存与运行逻辑

        /// <summary>
        /// 🌟 对接 StationRuntimeManager: 创建并连接工位 Client
        /// </summary>
        private async Task OnSaveStationConfigAsync()
        {
            if (SelectedStation == null) return;

            try
            {
                // 1. 调用对齐后的真实 API：CreateAndConnectStationAsync
                if (_runtimeManager != null)
                {
                    // 嵌入式模式 (Embedded) 或 跨进程 IPC 模式 (RemoteIpc)
                    var client = await _runtimeManager.CreateAndConnectStationAsync(
                        SelectedStation.StationCode,
                        WorkerConnectMode.Embedded
                    );

                    // 2. 如果绑定了配方且含有主流程，下发到工位运行期 Client
                    if (SelectedStation.BoundRecipe?.MainProcess != null && client != null)
                    {
                        await client.LoadRecipeAsync(SelectedStation.BoundRecipe.MainProcess);
                    }
                }

                MessageBox.Show($"工位 [{SelectedStation.StationName}] ({SelectedStation.StationCode}) 配置已保存并初始化 Client 成功！",
                                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存工位或连接 Core.Client 失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region 节点与硬件增删逻辑

        private void OnStationSelectedChanged()
        {
            if (SelectedStation == null) return;

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
                    SelectedStation.RecipeDeviceMappings.Add(new RecipeDeviceMappingModel
                    {
                        LogicalDeviceId = logical.LogicalDeviceId,
                        LogicalDeviceName = logical.LogicalDeviceName,
                        LogicalDeviceType = logical.LogicalDeviceType,
                        RequiredSpec = logical.RequiredSpec,
                        MappedDeviceId = SelectedStation.HardwareDevices.FirstOrDefault()?.DeviceId
                    });
                }
            }
        }

        private void OnAddLine()
        {
            int count = ProductionLines.Count + 1;
            var newLine = new LineModel
            {
                LineId = $"LINE_0{count}",
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
                    SelectedLine.Stations.Remove(SelectedStation);
                    SelectedStation = SelectedLine.Stations.FirstOrDefault();
                }
            }
            else if (SelectedLine != null)
            {
                if (MessageBox.Show($"确定要删除产线 [{SelectedLine.LineName}] 及其下属所有工位吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    ProductionLines.Remove(SelectedLine);
                    SelectedLine = ProductionLines.FirstOrDefault();
                    SelectedStation = null;
                }
            }
        }

        private void OnAddHardware()
        {
            if (SelectedStation == null) return;

            var unassignedDevices = GlobalHardwarePool.Where(g => !SelectedStation.HardwareDevices.Any(h => h.DeviceId == g.DeviceId)).ToList();

            if (!unassignedDevices.Any())
            {
                MessageBox.Show("【全局硬件池】中的可供领用设备已全部加入本工位，或硬件池为空！", "领用提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var itemToAdd = unassignedDevices.First();
            SelectedStation.HardwareDevices.Add(itemToAdd);

            MessageBox.Show($"已成功将硬件设备 [{itemToAdd.DeviceName}] 领用加入到工位 [{SelectedStation.StationName}]！", "领用成功", MessageBoxButton.OK, MessageBoxImage.Information);
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

        #region Mock 数据初始化

        private void InitMockData()
        {
            GlobalHardwarePool.Clear();
            AvailableRecipes.Clear();
            ProductionLines.Clear();

            var dev1 = new HardwareDeviceModel { DeviceId = "DEV_01", DeviceCode = "CAM_TOP_01", DeviceName = "顶视扫码相机", DeviceType = "HikVision Camera", ConnectionString = "192.168.1.101", IsConnected = true, Remark = "海康 500万像素" };
            var dev2 = new HardwareDeviceModel { DeviceId = "DEV_02", DeviceCode = "CAM_POS_01", DeviceName = "定位贴合相机", DeviceType = "Cognex Camera", ConnectionString = "192.168.1.102", IsConnected = true, Remark = "康耐视 智能相机" };

            GlobalHardwarePool.Add(dev1);
            GlobalHardwarePool.Add(dev2);

            var recipe1 = new RecipeModel { RecipeCode = "RCP_01", RecipeName = "Phone_Cover_A10", Version = "V1.0.2" };
            recipe1.LogicalDevices.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_CAM_01", LogicalDeviceName = "扫码识别相机", LogicalDeviceType = "2D Camera", RequiredSpec = "分辨率 >= 1080P" });

            AvailableRecipes.Add(recipe1);

            var station1 = new StationModel
            {
                StationCode = "ST_01",
                StationName = "上料扫码工位",
                IsEnabled = true,
                TimeoutMs = 3000,
                BoundRecipe = recipe1
            };
            station1.HardwareDevices.Add(dev1);

            var line1 = new LineModel { LineId = "LINE_01", LineName = "A线 - 模组组装产线" };
            line1.Stations.Add(station1);

            ProductionLines.Add(line1);

            SelectedLine = line1;
            SelectedStation = station1;
        }

        #endregion
    }
}