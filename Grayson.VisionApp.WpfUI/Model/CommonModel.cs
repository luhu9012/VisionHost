using Grayson.Vision.Contracts.Infrastructure.Mvvm;
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
    /// 配方逻辑设备模型 (配方对硬件的能力需求)
    /// </summary>
    public class RecipeLogicalDeviceModel : ViewModelBase
    {
        public string LogicalDeviceId { get; set; }
        public string LogicalDeviceName { get; set; }
        public string LogicalDeviceType { get; set; }
        public string RequiredSpec { get; set; }
    }

    /// <summary>
    /// 工位配方逻辑设备 -> 物理设备映射项
    /// </summary>
    public class StationDeviceMappingModel : ViewModelBase
    {
        public string LogicalDeviceId { get; set; }
        public string LogicalDeviceName { get; set; }
        public string LogicalDeviceType { get; set; }
        public string RequiredSpec { get; set; }

        private string _mappedDeviceId;
        /// <summary>
        /// 绑定的工位物理硬件ID
        /// </summary>
        public string MappedDeviceId
        {
            get => _mappedDeviceId;
            set => Set(ref _mappedDeviceId, value);
        }
    }

    /// <summary>
    /// 配方模型 (支持关联多个工位)
    /// </summary>
    public class RecipeModel : ViewModelBase
    {
        private string _recipeCode;
        public string RecipeCode { get => _recipeCode; set => Set(ref _recipeCode, value); }

        private string _recipeName;
        public string RecipeName { get => _recipeName; set => Set(ref _recipeName, value); }

        private string _productCategory;
        public string ProductCategory { get => _productCategory; set => Set(ref _productCategory, value); }

        private string _version;
        public string Version { get => _version; set => Set(ref _version, value); }

        private string _flowName;
        public string FlowName { get => _flowName; set => Set(ref _flowName, value); }

        private bool _isActive;
        public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

        private string _description;
        public string Description { get => _description; set => Set(ref _description, value); }

        private DateTime _updatedTime = DateTime.Now;
        public DateTime UpdatedTime { get => _updatedTime; set => Set(ref _updatedTime, value); }

        public double ExposureTime { get; set; } = 1000;
        public double Gain { get; set; } = 1.0;
        public double ToleranceMm { get; set; } = 0.05;

        /// <summary>
        /// 配方定义的逻辑设备要求列表
        /// </summary>
        public ObservableCollection<RecipeLogicalDeviceModel> LogicalDevices { get; set; } = new ObservableCollection<RecipeLogicalDeviceModel>();

        /// <summary>
        /// 关联的工位 Code/ID 列表 (多对多)
        /// </summary>
        public ObservableCollection<string> ApplicableStationCodes { get; set; } = new ObservableCollection<string>();
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
        /// </summary>
        public ObservableCollection<StationDeviceMappingModel> RecipeDeviceMappings { get; set; } = new ObservableCollection<StationDeviceMappingModel>();
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
