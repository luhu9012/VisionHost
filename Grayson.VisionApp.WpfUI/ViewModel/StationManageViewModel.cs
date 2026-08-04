//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationManageViewModel.cs
// 说 明: 产线工位管理 ViewModel (C# 7.3 兼容版，支持按钮完整演示逻辑)
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    #region 数据模型 (Models - C# 7.3 语法)

    /// <summary>
    /// 物理硬件设备模型
    /// </summary>
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

    /// <summary>
    /// 配方定义模型
    /// </summary>
    //public class RecipeModel : ViewModelBase
    //{
    //    public string RecipeCode { get; set; }
    //    public string RecipeName { get; set; }
    //    public string Version { get; set; }

    //    public ObservableCollection<RecipeDeviceMappingModel> LogicalDevices { get; set; }

    //    public RecipeModel()
    //    {
    //        LogicalDevices = new ObservableCollection<RecipeDeviceMappingModel>();
    //    }
    //}

    /// <summary>
    /// 配方逻辑设备 -> 物理硬件映射模型
    /// </summary>
    public class RecipeDeviceMappingModel : ViewModelBase
    {
        public string LogicalDeviceId { get; set; }
        public string LogicalDeviceName { get; set; }
        public string LogicalDeviceType { get; set; }
        public string RequiredSpec { get; set; }

        private string _mappedDeviceId;
        public string MappedDeviceId
        {
            get => _mappedDeviceId;
            set => Set(ref _mappedDeviceId, value);
        }
    }

    /// <summary>
    /// 工位模型
    /// </summary>
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

    /// <summary>
    /// 产线模型
    /// </summary>
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
        public StationManageViewModel()
        {
            // 初始化集合
            GlobalHardwarePool = new ObservableCollection<HardwareDeviceModel>();
            AvailableRecipes = new ObservableCollection<RecipeModel>();
            ProductionLines = new ObservableCollection<LineModel>();
           
            // 命令实例化
            AddLineCommand = new RelayCommand(_ => OnAddLine());
            AddStationCommand = new RelayCommand(_ => OnAddStation(), _ => SelectedLine != null);
            DeleteNodeCommand = new RelayCommand(_ => OnDeleteNode(), _ => SelectedLine != null || SelectedStation != null);

            AddHardwareCommand = new RelayCommand(_ => OnAddHardware(), _ => SelectedStation != null);
            RemoveHardwareCommand = new RelayCommand(_ => OnRemoveHardware(), _ => SelectedStation != null && SelectedHardware != null);
            SaveStationConfigCommand = new RelayCommand(_ => OnSaveStationConfig(), _ => SelectedStation != null);

            // 统一归拢初始化 Mock 数据
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

        #region Mock 数据集中归拢函数

        /// <summary>
        /// 集中归拢所有演示用的 Mock 数据
        /// </summary>
        private void InitMockData()
        {
            GlobalHardwarePool.Clear();
            AvailableRecipes.Clear();
            ProductionLines.Clear();

            // 1. 全局硬件池 Mock 数据
            var dev1 = new HardwareDeviceModel { DeviceId = "DEV_01", DeviceCode = "CAM_TOP_01", DeviceName = "顶视扫码相机", DeviceType = "HikVision Camera", ConnectionString = "192.168.1.101", IsConnected = true, Remark = "海康 500万像素" };
            var dev2 = new HardwareDeviceModel { DeviceId = "DEV_02", DeviceCode = "CAM_POS_01", DeviceName = "定位贴合相机", DeviceType = "Cognex Camera", ConnectionString = "192.168.1.102", IsConnected = true, Remark = "康耐视 智能相机" };
            var dev3 = new HardwareDeviceModel { DeviceId = "DEV_03", DeviceCode = "PLC_MAIN_01", DeviceName = "主控线PLC", DeviceType = "Siemens S7-1200", ConnectionString = "192.168.1.200", IsConnected = true, Remark = "主逻辑与气缸控制" };
            var dev4 = new HardwareDeviceModel { DeviceId = "DEV_04", DeviceCode = "MC_CARD_01", DeviceName = "三轴运动控制卡", DeviceType = "Gts Motion Card", ConnectionString = "Slot: 0", IsConnected = false, Remark = "贴合模组三轴控制" };

            GlobalHardwarePool.Add(dev1);
            GlobalHardwarePool.Add(dev2);
            GlobalHardwarePool.Add(dev3);
            GlobalHardwarePool.Add(dev4);

            // 2. 预设产品配方 Mock 数据
            var recipe1 = new RecipeModel { RecipeCode = "RCP_01", RecipeName = "Phone_Cover_A10", Version = "V1.0.2" };
            recipe1.LogicalDevices.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_CAM_01", LogicalDeviceName = "扫码识别相机", LogicalDeviceType = "2D Camera", RequiredSpec = "分辨率 >= 1080P" });
            recipe1.LogicalDevices.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_PLC_01", LogicalDeviceName = "工位顶升PLC", LogicalDeviceType = "PLC IO", RequiredSpec = "支持 ModbusTCP" });

            var recipe2 = new RecipeModel { RecipeCode = "RCP_02", RecipeName = "Phone_Cover_B20_HighPrecision", Version = "V2.1.0" };
            recipe2.LogicalDevices.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_CAM_POS", LogicalDeviceName = "高精度引导相机", LogicalDeviceType = "2D Camera", RequiredSpec = "500万像素黑白" });
            recipe2.LogicalDevices.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_AXIS_XYZ", LogicalDeviceName = "对位模组轴卡", LogicalDeviceType = "Motion Card", RequiredSpec = "支持 3轴插补" });

            AvailableRecipes.Add(recipe1);
            AvailableRecipes.Add(recipe2);

            // 3. 产线与工位 Mock 数据
            var station1 = new StationModel
            {
                StationCode = "ST_01",
                StationName = "上料扫码工位",
                IsEnabled = true,
                TimeoutMs = 3000,
                BoundRecipe = recipe1
            };
            station1.HardwareDevices.Add(dev1);
            station1.HardwareDevices.Add(dev3);
            station1.RecipeDeviceMappings.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_CAM_01", LogicalDeviceName = "扫码识别相机", LogicalDeviceType = "2D Camera", RequiredSpec = "分辨率 >= 1080P", MappedDeviceId = "DEV_01" });
            station1.RecipeDeviceMappings.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_PLC_01", LogicalDeviceName = "工位顶升PLC", LogicalDeviceType = "PLC IO", RequiredSpec = "支持 ModbusTCP", MappedDeviceId = "DEV_03" });

            var station2 = new StationModel
            {
                StationCode = "ST_02",
                StationName = "视觉定位贴合工位",
                IsEnabled = true,
                TimeoutMs = 5000,
                BoundRecipe = recipe2
            };
            station2.HardwareDevices.Add(dev2);
            station2.HardwareDevices.Add(dev4);

            var line1 = new LineModel { LineId = "LINE_01", LineName = "A线 - 模组组装产线" };
            line1.Stations.Add(station1);
            line1.Stations.Add(station2);

            var line2 = new LineModel { LineId = "LINE_02", LineName = "B线 - 外观AOI检测线" };

            ProductionLines.Add(line1);
            ProductionLines.Add(line2);

            // 默认选中第一个
            SelectedLine = line1;
            SelectedStation = station1;
        }

        #endregion

        #region TreeView 选择事件

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

        #region 按钮功能逻辑

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

            // 查找未领用的全局硬件
            var unassignedDevices = GlobalHardwarePool.Where(g => !SelectedStation.HardwareDevices.Any(h => h.DeviceId == g.DeviceId)).ToList();

            if (!unassignedDevices.Any())
            {
                MessageBox.Show("【全局硬件池】中的可供领用设备已全部加入本工位，或硬件池为空！", "领用提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 演示效果：循环将硬件池未领用的硬件加进来
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

        private void OnSaveStationConfig()
        {
            if (SelectedStation == null) return;

            MessageBox.Show($"工位 [{SelectedStation.StationName}] ({SelectedStation.StationCode}) 的基础配置、领用硬件与配方映射已成功保存！", "保存提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion
    }
}