using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service; // 引入 DevicePoolManager 命名空间
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class DriverPluginInfo : ViewModelBase
    {
        public string DeviceId { get; set; }
        public string Name { get; set; }
        public string Brand { get; set; }
        public DeviceCategory Category { get; set; }
        public string CategoryName => Category.ToString();
        public string AssemblyName { get; set; }
        public string ClassTypeName { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public bool IsLoaded { get; set; } = true;

        public string CategoryIcon
        {
            get
            {
                switch (Category)
                {
                    case DeviceCategory.Camera:
                        return "📷";
                    case DeviceCategory.PLC:
                        return "📟";
                    case DeviceCategory.MotionCard:
                        return "⚙️";
                    case DeviceCategory.LightController:
                        return "💡";
                    default:
                        return "🧩";
                }
            }
        }
    }

    public class PluginManageViewModel : ViewModelBase
    {
        private List<DriverPluginInfo> _allPluginsCache = new List<DriverPluginInfo>();

        public ObservableCollection<DriverPluginInfo> LoadedPlugins { get; } = new ObservableCollection<DriverPluginInfo>();

        private DriverPluginInfo _selectedPlugin;
        public DriverPluginInfo SelectedPlugin
        {
            get => _selectedPlugin;
            set => Set(ref _selectedPlugin, value);
        }

        private string _searchText;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value))
                {
                    FilterPlugins();
                }
            }
        }

        #region 统计属性
        public int TotalCount => _allPluginsCache.Count;
        public int CameraCount => _allPluginsCache.Count(p => p.Category == DeviceCategory.Camera);
        public int PlcCount => _allPluginsCache.Count(p => p.Category == DeviceCategory.PLC);
        public int MotionCount => _allPluginsCache.Count(p => p.Category == DeviceCategory.MotionCard);
        #endregion

        public ICommand ScanPluginsCommand { get; }

        public PluginManageViewModel()
        {
            ScanPluginsCommand = new RelayCommand(_ => LoadRealPluginsFromService());

            LoadRealPluginsFromService();
        }

        /// <summary>
        /// 从真实 DevicePoolManager 中加载插件列表
        /// </summary>
        private void LoadRealPluginsFromService()
        {
            _allPluginsCache.Clear();

            try
            {
                // 1. 调用现有的自动扫描加载插件函数
                DevicePoolManager.Instance.AutoLoadAllPlugins();

                // 2. 获取当前已加载的插件集合
                var plugins = DevicePoolManager.Instance.GetAllPlugins();

                foreach (var plugin in plugins)
                {
                    Type type = plugin.GetType();
                    Assembly asm = type.Assembly;

                    // 提取版本号（优先使用插件接口定义的版本，若无则从 Assembly 获取）
                    string version = plugin.Version;
                    if (string.IsNullOrEmpty(version))
                    {
                        version = asm.GetName().Version?.ToString(3) ?? "1.0.0";
                    }

                    _allPluginsCache.Add(new DriverPluginInfo
                    {
                        // key 形式为 BrandName_Category
                        DeviceId = $"{plugin.BrandName}.{plugin.Category}",
                        Name = $"{plugin.BrandName} {plugin.Category} 驱动插件",
                        Brand = plugin.BrandName,
                        Category = plugin.Category,
                        AssemblyName = System.IO.Path.GetFileName(asm.Location), // DLL 文件名
                        ClassTypeName = type.FullName,                          // 插件实现类的完整类型名
                        Version = version,
                        Description = $"通过 {asm.GetName().Name} 动态装载的硬件驱动插件。",
                        IsLoaded = true
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载插件数据失败: {ex.Message}");
            }

            FilterPlugins();
            SelectedPlugin = LoadedPlugins.FirstOrDefault();

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(CameraCount));
            OnPropertyChanged(nameof(PlcCount));
            OnPropertyChanged(nameof(MotionCount));
        }

        private void FilterPlugins()
        {
            LoadedPlugins.Clear();
            var query = _allPluginsCache.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var keyword = SearchText.Trim().ToLower();
                query = query.Where(p => (p.Name != null && p.Name.ToLower().Contains(keyword)) ||
                                         (p.DeviceId != null && p.DeviceId.ToLower().Contains(keyword)) ||
                                         (p.Brand != null && p.Brand.ToLower().Contains(keyword)) ||
                                         (p.CategoryName != null && p.CategoryName.ToLower().Contains(keyword)) ||
                                         (p.AssemblyName != null && p.AssemblyName.ToLower().Contains(keyword)));
            }

            foreach (var plugin in query)
            {
                LoadedPlugins.Add(plugin);
            }
        }
    }
}