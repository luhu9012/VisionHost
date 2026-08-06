//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: ShellViewModel.cs
// 创 建: 2026-07-18
// 修 改: 2026-07-28
// 说 明: 主框架窗口 ViewModel - 负责分栏菜单与核心功能区导航（支持菜单收起/展开）
//===================================================================================

using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Forms.Design;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 导航菜单项 ViewModel（支持二级折叠与分节）
    /// </summary>
    public class MenuItemViewModel : ViewModelBase
    {
        public string Icon { get; set; }
        public string Title { get; set; }
        public PageType PageType { get; set; }
        public UserRole RequiredRole { get; set; } = UserRole.Operator;

        public bool IsSectionHeader { get; set; }
        public ObservableCollection<MenuItemViewModel> Children { get; set; }
        public bool HasChildren => Children != null && Children.Count > 0;

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (Set(ref _isExpanded, value))
                    OnPropertyChanged(nameof(ChevronText));
            }
        }

        // 使用尺寸更饱满的箭头符号
        public string ChevronText => HasChildren ? (IsExpanded ? "▼" : "▶") : string.Empty;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }
    }

    /// <summary>
    /// 主框架 ShellViewModel
    /// </summary>
    public class ShellViewModel : ViewModelBase
    {
        private readonly INavigationService _navigationService;
        private readonly GlobalData _globalData;

        public ShellViewModel(INavigationService navigationService)
        {
            _navigationService = navigationService;
            _globalData = GlobalData.Instance;

            InitializeMenuItems();

            // 🌟 订阅导航切换事件，保证菜单选中状态始终与 NavigationService 当前页面同步
            _navigationService.PageChanged += OnPageChanged;

            NavigateCommand = new RelayCommand(OnNavigate);
            LogoutCommand = new RelayCommand(_ => OnLogout());
            MinimizeCommand = new RelayCommand(_ => OnMinimize());
            MaximizeCommand = new RelayCommand(_ => OnMaximize());
            CloseCommand = new RelayCommand(_ => OnClose());

            NavigateToAlarmCommand = new RelayCommand(_ => OnNavigateToAlarm());
            DismissAlarmBannerCommand = new RelayCommand(_ => _globalData.DismissCriticalAlarm());
            ToggleMenuCollapseCommand = new RelayCommand(_ => ToggleMenuCollapse());
        }
        /// <summary>
        /// 🌟 当 NavigationService 发生页面切换（无论是代码跳转还是 GoBack）时自动触发
        /// </summary>
        private void OnPageChanged(object sender, PageType pageType)
        {
            // 递归查找匹配 PageType 的菜单项
            var matchedItem = FindMenuItemByPageType(MenuItems, pageType);
            if (matchedItem != null)
            {
                ClearMenuSelection(MenuItems);
                matchedItem.IsSelected = true;
                SelectedMenuItem = matchedItem;

                // 如果该菜单属于某个折叠组（父节点），顺便将父节点展开
                ExpandParentMenuIfNeeded(MenuItems, matchedItem);
            }
        }
        /// <summary>
        /// 递归查找目标 PageType 对应的 MenuItemViewModel
        /// </summary>
        private MenuItemViewModel FindMenuItemByPageType(ObservableCollection<MenuItemViewModel> items, PageType pageType)
        {
            if (items == null) return null;

            foreach (var item in items)
            {
                if (!item.IsSectionHeader && !item.HasChildren && item.PageType == pageType)
                {
                    return item;
                }

                if (item.HasChildren)
                {
                    var childMatch = FindMenuItemByPageType(item.Children, pageType);
                    if (childMatch != null) return childMatch;
                }
            }
            return null;
        }
        /// <summary>
        /// 若选中的是子菜单，自动展开父级菜单
        /// </summary>
        private bool ExpandParentMenuIfNeeded(ObservableCollection<MenuItemViewModel> items, MenuItemViewModel targetItem)
        {
            if (items == null) return false;

            foreach (var item in items)
            {
                if (item.HasChildren)
                {
                    if (item.Children.Contains(targetItem) || ExpandParentMenuIfNeeded(item.Children, targetItem))
                    {
                        item.IsExpanded = true;
                        return true;
                    }
                }
            }
            return false;
        }

        #region 属性

        private ObservableCollection<MenuItemViewModel> _menuItems;
        public ObservableCollection<MenuItemViewModel> MenuItems
        {
            get => _menuItems;
            set => Set(ref _menuItems, value);
        }

        private MenuItemViewModel _selectedMenuItem;
        public MenuItemViewModel SelectedMenuItem
        {
            get => _selectedMenuItem;
            set => Set(ref _selectedMenuItem, value);
        }

        private bool _isMenuCollapsed = true;
        /// <summary>
        /// 菜单是否处于收起（窄屏仅图标）状态
        /// </summary>
        public bool IsMenuCollapsed
        {
            get => _isMenuCollapsed;
            set
            {
                if (Set(ref _isMenuCollapsed, value))
                {
                    OnPropertyChanged(nameof(MenuWidth));
                    OnPropertyChanged(nameof(ToggleMenuTooltip));
                }
            }
        }

        /// <summary>
        /// 动态菜单宽度：展开230，收起60
        /// </summary>
        public GridLength MenuWidth => IsMenuCollapsed ? new GridLength(60) : new GridLength(230);

        public string ToggleMenuTooltip => IsMenuCollapsed ? "展开菜单" : "收起菜单";

        public string CurrentUserName => _globalData.CurrentUserName;
        public DeviceStatus SystemStatus => _globalData.SystemStatus;
        public int AlarmCount => _globalData.AlarmCount;
        public bool HasAlarm => _globalData.HasAlarm;
        public bool HasCriticalAlarm => _globalData.HasCriticalAlarm;
        public string CriticalAlarmMessage => _globalData.CriticalAlarmMessage;

        #endregion

        #region 命令

        public ICommand NavigateCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand MinimizeCommand { get; }
        public ICommand MaximizeCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand NavigateToAlarmCommand { get; }
        public ICommand DismissAlarmBannerCommand { get; }
        public ICommand ToggleMenuCollapseCommand { get; }

        #endregion

        #region 方法

        private void ToggleMenuCollapse()
        {
            IsMenuCollapsed = !IsMenuCollapsed;
        }

        private void InitializeMenuItems()
        {
            MenuItems = new ObservableCollection<MenuItemViewModel>
    {
        // ------------------ 1. 运行操作区 ------------------
        new MenuItemViewModel { IsSectionHeader = true, Title = "运行操作区" },
        new MenuItemViewModel
        {
            Icon = "🖥️", Title = "生产看板", IsExpanded = true, RequiredRole = UserRole.Operator,
            Children = new ObservableCollection<MenuItemViewModel>
            {
                new MenuItemViewModel { Icon = "📊", Title = "产线拓扑总览", PageType = PageType.LineOverview, RequiredRole = UserRole.Operator },
                new MenuItemViewModel { Icon = "🔍", Title = "单工位监控", PageType = PageType.StationMonitor, RequiredRole = UserRole.Operator }
            }
        },
        new MenuItemViewModel { Icon = "⚠️", Title = "报警与诊断", PageType = PageType.Alarm, RequiredRole = UserRole.Operator },

        // ------------------ 2. 核心工程配置 ------------------
        new MenuItemViewModel { IsSectionHeader = true, Title = "核心工程配置" },
        new MenuItemViewModel { Icon = "🏭", Title = "产线工位与映射", PageType = PageType.StationManage, RequiredRole = UserRole.Engineer },
        new MenuItemViewModel { Icon = "📦", Title = "配方管理", PageType = PageType.RecipeManage, RequiredRole = UserRole.Engineer },
        new MenuItemViewModel { Icon = "🌿", Title = "视觉流程编辑器", PageType = PageType.FlowEdit, RequiredRole = UserRole.Engineer },

        // ------------------ 3. 硬件设备池 (插件驱动) ------------------
        //硬件设备池 (Hardware Device Pool)
        //│
        //├── 1. 物理设备实例 (DevicePoolView / PageType.DevicePool)
        //│   ├── 功能: 硬件管理卡片/列表、动态添加/删除设备实例、参数配置、连接/断开状态控制。
        //│   └── 对应后端: DevicePoolManager.ActiveDevices (已实例化的 IDevice 集合)
        //│
        //├── 2. 轴/IO/相机调试台 (HardwareConsoleView / PageType.HardwareConsole)
        //│   ├── 功能: 单硬件在线调测工具（实时图像抓拍/流显示、轴点动控制/回零、IO点位监视与强制切换）。
        //│   └── 对应后端: 从 DevicePoolManager 获取 IDevice 实例并转换为 ICamera / IMotion / IIoService 进行调试。
        //│
        //└── 3. 驱动插件管理 (PluginManageView / PageType.PluginManage)
        //    ├── 功能: 驱动 DLL 扫描装载情况、已安装驱动列表、版本信息、元数据（DevicePluginAttribute）查看。
        //    └── 对应后端: DevicePoolManager.AvailableDrivers 与 CameraPluginManager
        new MenuItemViewModel { IsSectionHeader = true, Title = "硬件设备池" },
        new MenuItemViewModel
        {
            Icon = "🔌", Title = "设备与驱动", IsExpanded = false, RequiredRole = UserRole.Engineer,
            Children = new ObservableCollection<MenuItemViewModel>
            {
                new MenuItemViewModel { Icon = "⚙️", Title = "物理设备实例", PageType = PageType.DevicePool, RequiredRole = UserRole.Engineer },
                new MenuItemViewModel { Icon = "🛠️", Title = "轴/IO/相机调试台", PageType = PageType.HardwareConsole, RequiredRole = UserRole.Engineer },
                new MenuItemViewModel { Icon = "🧩", Title = "驱动插件管理", PageType = PageType.PluginManage, RequiredRole = UserRole.Engineer }
            }
        },

        // ------------------ 4. 数据与运维 ------------------
        new MenuItemViewModel { IsSectionHeader = true, Title = "数据与运维" },
        new MenuItemViewModel { Icon = "📈", Title = "本地追溯与防错", PageType = PageType.DataTrace, RequiredRole = UserRole.Operator },
        new MenuItemViewModel { Icon = "🌐", Title = "MES 对接状态", PageType = PageType.MesBridge, RequiredRole = UserRole.Engineer },
        new MenuItemViewModel { Icon = "👥", Title = "用户与权限", PageType = PageType.UserManage, RequiredRole = UserRole.Administrator },
        new MenuItemViewModel { Icon = "⚙️", Title = "系统与存储设置", PageType = PageType.SystemSetting, RequiredRole = UserRole.Administrator }
    };

            // 默认导航至 "产线拓扑总览"
            var defaultItem = MenuItems.FirstOrDefault(m => !m.IsSectionHeader && !m.HasChildren);

            if (defaultItem != null)

            {

                OnNavigate(defaultItem);

            }

            else if (MenuItems.Count > 1 && MenuItems[1].HasChildren)

            {

                OnNavigate(MenuItems[1].Children.First());

            }
        }
        private void OnNavigate(object parameter)
        {
            if (parameter is MenuItemViewModel menuItem)
            {
                if (menuItem.IsSectionHeader) return;

                if (menuItem.HasChildren)
                {
                    // 收起状态下点击有子菜单的项目，自动展开侧边栏
                    if (IsMenuCollapsed)
                    {
                        IsMenuCollapsed = false;
                    }
                    menuItem.IsExpanded = !menuItem.IsExpanded;
                    return;
                }

                if (_globalData.CurrentUserRole < menuItem.RequiredRole)
                {
                    return;
                }

                // 🌟 发起页面导航（PageChanged 事件会自动更新菜单高亮状态）
                _navigationService.NavigateTo(menuItem.PageType);
            }
        }

        private void ClearMenuSelection(ObservableCollection<MenuItemViewModel> items)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                item.IsSelected = false;
                if (item.HasChildren)
                {
                    ClearMenuSelection(item.Children);
                }
            }
        }

        private void OnNavigateToAlarm()
        {
            _globalData.DismissCriticalAlarm();
            var alarmItem = MenuItems.FirstOrDefault(i => i.PageType == PageType.Alarm);
            if (alarmItem != null)
            {
                OnNavigate(alarmItem);
            }
            else
            {
                _navigationService.NavigateTo(PageType.Alarm);
            }
        }

        private void OnLogout()
        {
            if (MessageBox.Show("确定要退出当前账号并返回登录界面吗?", "退出登录确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _globalData.RaiseUserLoggedOut();
            }
        }

        private void OnMinimize()
        {
            if (Application.Current?.MainWindow != null)
                Application.Current.MainWindow.WindowState = WindowState.Minimized;
        }

        private void OnMaximize()
        {
            if (Application.Current?.MainWindow != null)
            {
                var window = Application.Current.MainWindow;
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
        }

        private void OnClose()
        {
            if (MessageBox.Show("确定要退出工业视觉上位机系统吗?", "退出系统确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                Application.Current?.Shutdown();
            }
        }

        #endregion
    }
}