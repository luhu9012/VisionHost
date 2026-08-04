//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: App.xaml.cs
// 创 建: 2026-07-18
// 说 明: WPF应用程序入口核心逻辑，对应前端项目main.js / main.ts
// 核心职责：程序启动初始化、全局异常捕获、登录窗口与主页面窗口切换、页面导航注册、全局生命周期管控
// 前端类比：整个文件等同于Vue项目main.ts，负责创建应用实例、全局挂载、路由注册、全局错误捕获
//===================================================================================

using System;
using System.Windows;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.WpfUI.View;
using Grayson.Vision.WpfUI.ViewModel;
using Grayson.VisionApp.WpfUI.View;
using Grayson.Vision.Contracts.Infrastructure.Logging;

namespace Grayson.Vision.WpfUI
{
    /// <summary>
    /// App.xaml 后台交互类，WPF程序根应用实例
    /// 生命周期：程序双击启动 → 触发Startup事件 → 执行本文件Startup方法，是整个软件最先执行的代码
    /// 全局作用域：所有窗口、ViewModel、服务的顶层容器，可挂载全局异常、全局事件、全局服务实例
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 全局认证服务实例，全程序共用一套登录校验逻辑
        /// 前端类比：main.ts中全局挂载的api请求实例
        /// </summary>
        private IAuthenticationService _authService;

        /// <summary>
        /// 全局文件日志服务实例，负责将 LogBus 日志落盘到本地磁盘
        /// </summary>
        private FileLogSink _fileLogSink;

        /// <summary>
        /// 应用程序启动入口事件，程序打开时第一个执行的方法
        /// 类比前端main.ts入口函数，统一完成全局初始化工作
        /// </summary>
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // 1. 初始化文件日志服务
            _fileLogSink = new FileLogSink();

#if DEBUG
            // 【VS 开发环境】：
            // 不需要开启 FileLogSink（或者只输出到默认 Debug/Logs 目录）
            // 所有 LogBus.Info/Error 都会直接显示在 VS 的 "输出(Output)" 窗口中！

            // 如果开发时也想顺便写本地日志，取消下面这句注释即可：
            // _fileLogSink.Enable(); 
#else
            // 【生产打包环境】：
            // 1. 可以从 App.config / appsettings.json 读取生产环境配置的磁盘路径
            string customPath = ConfigurationManager.AppSettings["LogPath"]; 
            
            if (!string.IsNullOrEmpty(customPath))
            {
                _fileLogSink.SetDirectory(customPath); // 例如 "D:\FactoryData\Logs"
            }

            // 2. 生产环境开启落盘
            _fileLogSink.Enable();
#endif

          
            // 1. 初始化账号认证服务（当前使用Mock模拟登录服务，可替换为数据库/网络登录实现）
            _authService = new MockAuthenticationService();

            // 2. 注册两套全局异常捕获，兜底防止程序无提示闪退
            // AppDomain：捕获后台非UI线程、Task、子线程抛出的未处理异常（如视觉采集、运动控制后台线程报错）
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            // Dispatcher：捕获UI主线程控件绑定、页面操作产生的异常（等同于前端全局errorHandler捕获界面错误）
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // 🚀 在程序启动时，自动扫描并加载海康、西门子等所有硬件插件
            DevicePoolManager.Instance.AutoLoadAllPlugins();
            // 3. 程序启动默认弹出登录窗口，登录校验通过后再加载主业务界面
            ShowLoginWindow();
            LogBus.Info("System", "应用程序启动完成！");
        }

        /// <summary>
        /// 创建并弹出登录窗口，承载LoginView登录页面控件
        /// 设计思路：将页面View与窗口Window分离，Window仅做窗体外壳，View负责页面布局
        /// </summary>
        private void ShowLoginWindow()
        {
            // 实例化登录页面UI控件（对应Vue单文件组件Login.vue）
            var loginView = new LoginView();
            // 定义登录窗体外壳变量，用于登录成功后关闭窗口
            Window loginWindow = null;

            // 实例化登录ViewModel，注入全局认证服务 + 登录完成回调函数
            // 回调逻辑：登录成功回调触发主窗口加载，同时关闭当前登录窗体
            var loginViewModel = new LoginViewModel(_authService, (success) =>
            {
                if (success)
                {
                    // 先初始化并显示主业务窗口
                    ShowMainWindow();
                    // 关闭登录窗体
                    loginWindow?.Close();
                }
            });

            // 关键绑定：给登录页面指定数据上下文，页面所有{Binding}绑定全部读取loginViewModel属性
            // 类比Vue：app.component('Login', { setup: loginViewModel })
            loginView.DataContext = loginViewModel;

            // 实例化Window窗体外壳，将LoginView页面作为窗体内部内容
            loginWindow = new Window
            {
                Content = loginView,                // 窗体内部填充登录页面
                Title = "系统登录",                  // 窗口标题
                Width = 800,
                Height = 600,          // 固定宽高
                WindowStyle = WindowStyle.None,      // 隐藏原生标题栏，自定义登录页面头部
                ResizeMode = ResizeMode.NoResize,    // 禁止拖拽缩放窗口
                WindowStartupLocation = WindowStartupLocation.CenterScreen, // 窗口居中弹出
                AllowsTransparency = false           // 关闭透明，提升渲染性能
            };

            // ShowDialog：模态弹窗，阻塞代码往下执行，必须关闭登录窗口才会继续执行后续逻辑
            loginWindow.ShowDialog();
        }

        /// <summary>
        /// 登录成功后加载软件主窗口（外壳Shell）
        /// Shell = 主布局容器，左侧菜单+中间内容区域，类比Vue后台管理系统layout布局组件
        /// </summary>
        private void ShowMainWindow()
        {
            // 实例化主布局外壳页面
            var shellView = new ShellView();
            // 初始化页面导航服务，传入主布局的内容占位控件（用于切换中间业务页面）
            // 前端类比：Vue Router路由实例，负责页面跳转、组件切换
            var navigationService = new NavigationService(shellView.ContentPresenter);
            // 登记全局导航服务引用，供各页面 ViewModel 发起跨页跳转
            NavigationService.Current = navigationService;

            // 将所有业务页面注册到导航服务，后续可通过页面标识跳转
            RegisterPages(navigationService);

            // 实例化主布局ViewModel，注入导航服务，控制左侧菜单点击跳转逻辑
            var shellViewModel = new ShellViewModel(navigationService);
            shellView.DataContext = shellViewModel;

            // 全局登出事件监听：任意页面触发登出时，关闭主窗口，重新打开登录界面
            // 类比前端mitt全局事件总线监听logout事件
            GlobalData.Instance.UserLoggedOut += (s, e) =>
            {
                shellView.Close();
                ShowLoginWindow();
            };

            // 将当前主窗体赋值给应用全局MainWindow对象
            MainWindow = shellView;
            // 非模态窗口，打开后不阻塞程序其他逻辑
            shellView.Show();

            // 程序登录成功默认跳转到【生产监控】页面
            navigationService.NavigateTo(PageType.Alarm);
        }

        /// <summary>
        /// 统一注册系统全部业务页面到导航服务
        /// 作用：导航服务只保存页面创建工厂方法，不会一次性实例化所有页面，节省内存（懒加载）
        /// 前端类比：Vue Router中routes路由表配置，每个路由对应一个页面组件
        /// </summary>
        /// <param name="navigationService">页面导航管理器</param>
        private void RegisterPages(NavigationService navigationService)
        {

            // 报警记录页面
            navigationService.RegisterPage(PageType.Alarm, () => new AlarmView());
            // 流程编辑页面
            navigationService.RegisterPage(PageType.FlowEdit, () => new FlowEditView());
            // 工位管理页面
            navigationService.RegisterPage(PageType.StationManage, () => new StationManageView());
            // 配方管理页面
            navigationService.RegisterPage(PageType.RecipeManage, () => new RecipeManageView());
            // 插件管理页面
            navigationService.RegisterPage(PageType.PluginManage, () => new PluginManageView());
            //设备池页面 
            navigationService.RegisterPage(PageType.DevicePool, () => new DevicePoolView());
            // 硬件控制台页面  
            navigationService.RegisterPage(PageType.HardwareConsole, () => new HardwareConsoleView());
            // 用户权限管理页面
            navigationService.RegisterPage(PageType.UserManage, () => new UserManageView());

        }

        /// <summary>
        /// 全局非UI线程异常捕获（后台线程、采集循环、异步任务崩溃兜底）
        /// 触发时机：后台线程抛出未捕获异常，程序即将崩溃前执行
        /// 缺陷：此处弹窗仅提示错误，无法执行硬件/视觉资源释放；完善方案需在弹窗前调用全局资源回收方法
        /// </summary>
        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            MessageBox.Show($"应用程序发生严重错误:\n{exception?.Message}\n\n详细信息:\n{exception?.StackTrace}",
                "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// UI主线程异常捕获（界面按钮、输入框、绑定逻辑报错）
        /// e.Handled = true：标记异常已处理，阻止WPF直接关闭程序，保证软件不闪退
        /// 前端类比：Vue app.config.errorHandler 全局界面错误捕获
        /// </summary>
        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show($"界面操作发生错误:\n{e.Exception.Message}\n\n详细信息:\n{e.Exception.StackTrace}",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            // 告知框架异常已人工处理，不再向上抛出导致程序崩溃
            e.Handled = true;
        }
    }
}