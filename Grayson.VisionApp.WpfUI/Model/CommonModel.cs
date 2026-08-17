using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Recipe.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.WpfUI.Model
{
    /// <summary>
    /// 物理硬件设备模型 (设备池中的真实硬件)
    /// </summary>
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
                    OnPropertyChanged(nameof(StatusText));
            }
        }

        public string StatusText => IsConnected ? "在线" : "离线";
        public string Remark { get; set; }
    }

    /// <summary>
    /// 配方模型 (支持关联多个工位)
    /// 使用 Contracts.Recipe.Models.RecipeModel 并添加 UI 同步字段
    /// </summary>
    public class RecipeModel : ViewModelBase
    {
        private Grayson.Vision.Contracts.Recipe.Models.RecipeModel _contractsModel =
            new Grayson.Vision.Contracts.Recipe.Models.RecipeModel();

        /// <summary>获取底层契约模型（用于存储/API 通信）</summary>
        public Grayson.Vision.Contracts.Recipe.Models.RecipeModel Contract => _contractsModel;

        /// <summary>配方代码 - 绑定到 UI</summary>
        public string RecipeCode
        {
            get => _contractsModel.RecipeCode;
            set
            {
                if (_contractsModel.RecipeCode != value)
                {
                    _contractsModel.RecipeCode = value;
                    OnPropertyChanged(nameof(RecipeCode));
                }
            }
        }

        /// <summary>配方名称</summary>
        public string RecipeName
        {
            get => _contractsModel.RecipeName;
            set
            {
                if (_contractsModel.RecipeName != value)
                {
                    _contractsModel.RecipeName = value;
                    OnPropertyChanged(nameof(RecipeName));
                }
            }
        }

        /// <summary>产品类别</summary>
        public string ProductCategory
        {
            get => _contractsModel.ProductCategory;
            set
            {
                if (_contractsModel.ProductCategory != value)
                {
                    _contractsModel.ProductCategory = value;
                    OnPropertyChanged(nameof(ProductCategory));
                }
            }
        }

        /// <summary>版本</summary>
        public string Version
        {
            get => _contractsModel.Version;
            set
            {
                if (_contractsModel.Version != value)
                {
                    _contractsModel.Version = value;
                    OnPropertyChanged(nameof(Version));
                }
            }
        }

        /// <summary>流程名称</summary>
        public string FlowName { get; set; }

        /// <summary>是否激活</summary>
        public bool IsActive
        {
            get => _contractsModel.IsActive;
            set
            {
                if (_contractsModel.IsActive != value)
                {
                    _contractsModel.IsActive = value;
                    OnPropertyChanged(nameof(IsActive));
                }
            }
        }

        /// <summary>描述</summary>
        public string Description
        {
            get => _contractsModel.Description;
            set
            {
                if (_contractsModel.Description != value)
                {
                    _contractsModel.Description = value;
                    OnPropertyChanged(nameof(Description));
                }
            }
        }

        /// <summary>更新时间</summary>
        private DateTime _updatedTime = DateTime.Now;
        public DateTime UpdatedTime
        {
            get => _updatedTime;
            set => Set(ref _updatedTime, value);
        }

        /// <summary>相机曝光时间</summary>
        public double ExposureTime { get; set; } = 1000;

        /// <summary>相机增益</summary>
        public double Gain { get; set; } = 1.0;

        /// <summary>公差（单位毫米）</summary>
        public double ToleranceMm { get; set; } = 0.05;

        /// <summary>
        /// 配方定义的逻辑设备要求列表
        /// 改用 Contracts 中的 RecipeDeviceMappingModel
        /// </summary>
        public ObservableCollection<RecipeDeviceMappingModel> LogicalDevices { get; set; } = 
            new ObservableCollection<RecipeDeviceMappingModel>();

        /// <summary>
        /// 关联的工位 Code/ID 列表 (多对多)
        /// </summary>
        public ObservableCollection<string> ApplicableStationCodes { get; set; } = 
            new ObservableCollection<string>();
    }

    /// <summary>
    /// 工位模型
    /// </summary>
    public class StationModel : ViewModelBase
    {
        private string _stationCode;
        public string StationCode { get => _stationCode; set => Set(ref _stationCode, value); }

        private string _stationName;
        public string StationName { get => _stationName; set => Set(ref _stationName, value); }

        private bool _isEnabled = true;
        public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

        private int _timeoutMs = 3000;
        public int TimeoutMs { get => _timeoutMs; set => Set(ref _timeoutMs, value); }

        private RecipeModel _boundRecipe;
        /// <summary>
        /// 工位当前生效/选择的配方
        /// </summary>
        public RecipeModel BoundRecipe
        {
            get => _boundRecipe;
            set => Set(ref _boundRecipe, value);
        }

        /// <summary>
        /// 本工位从设备池领用的真实硬件设备
        /// </summary>
        public ObservableCollection<HardwareDeviceModel> HardwareDevices { get; set; } = new ObservableCollection<HardwareDeviceModel>();

        /// <summary>
        /// 本工位针对当前配方的逻辑设备 -> 物理设备映射关系
        /// 改用 Contracts 中的 RecipeDeviceMappingModel
        /// </summary>
        public ObservableCollection<RecipeDeviceMappingModel> RecipeDeviceMappings { get; set; } = 
            new ObservableCollection<RecipeDeviceMappingModel>();
    }

    /// <summary>
    /// 产线模型
    /// </summary>
    public class LineModel : ViewModelBase
    {
        public string LineId { get; set; }
        public string LineName { get; set; }
        public ObservableCollection<StationModel> Stations { get; set; } = new ObservableCollection<StationModel>();
    }
}
