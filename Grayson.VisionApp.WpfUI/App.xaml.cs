//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: App.xaml.cs
// 创 建: 2026-07-18
// 说 明: WPF应用程序入口核心逻辑，对应前端项目main.js / main.ts
// 核心职责：程序启动初始化、全局异常捕获、登录窗口与主页面窗口切换、页面导航注册、全局生命周期管控
// 前端类比：整个文件等同于Vue项目main.ts，负责创建应用实例、全局挂载、路由注册、全局错误捕获
//===================================================================================
using Grayson.Vision.Repository;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Core.Station;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.WpfUI.View;
using Grayson.Vision.WpfUI.ViewModel;
using Grayson.VisionApp.WpfUI.View;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

// FlowEdit 嵌入 WpfUI 时，其自身 App 不会启动，需共享运行时
using FlowEditApp = Grayson.Vison.FlowEdit.App;


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

        /// <summary>日志写盘失败告警节流时间（同一秒内多次失败只提示一次，防刷屏）</summary>
        private DateTime _lastLoggingFailedNotifiedAt = DateTime.MinValue;

        /// <summary>
        /// 全局 StationHost 运行时（Core 唯一入口），程序生命周期内共享。
        /// </summary>
        public static IStationHostRuntime StationHostRuntime { get; private set; }

        /// <summary>
        /// 全局登出事件处理器（避免重复订阅）
        /// </summary>
        private EventHandler _userLoggedOutHandler;

        /// <summary>
        /// 工位监视页缓存实例：该页注册为「缓存单例」——导航离开再进入时
        /// 复用同一 View+VM，统计/日志/图像历史/工位连接全部存活，
        /// 任务进行中切换页面回来信息不丢（图像由 Halcon 窗口重建事件补渲染）。
        /// </summary>
        private StationMonitorView _stationMonitorPage;

        /// <summary>
        /// 产线拓扑总览页缓存实例：该页注册为「缓存单例」——导航离开再进入时
        /// 复用同一 View+VM，统计数据轮询与缩略图事件订阅不重复、不泄漏，
        /// 统计卡片从持久化文件恢复 + 实时水位累计。
        /// </summary>
        private LineOverviewView _lineOverviewPage;

        /// <summary>
        /// 模板工作台页缓存实例（v3 信息架构：模板独立菜单页）：该页注册为「缓存单例」——
        /// 源图/ROI/掩膜笔画/验证叠加等编辑现场跨导航存活；OnNavigatedTo 接收 StationCode
        /// 时按 OwnerStation 定位并钉住归属（进入时经 TemplateManagerViewModel 处理；
        /// 2026-09-05 起 Workbench 壳已合并下沉，页面直挂 TemplateManagerView）。
        /// </summary>
        private TemplateManagerView _templateManagerPage;

        /// <summary>
        /// 标定中心页缓存实例（2026-09-05 起为「缓存单例」，镜像模板工作台）：进入即保留当前
        /// 工位定位态（钉住/回全库），方案编辑、新建自动归属现场跨导航存活。
        /// </summary>
        private CalibrationManagerView _calibrationPage;

        /// <summary>
        /// 配方管理页缓存实例（2026-09-05 中等重构改「缓存单例」）：列表选中态、审批操作上下文
        /// 跨导航存活；从工位工作台带 BoundRecipe 跳入时经 OnNavigatedTo 定位到该配方。
        /// </summary>
        private RecipeManageView _recipeManagePage;

        /// <summary>
        /// 应用程序启动入口事件，程序打开时第一个执行的方法
        /// 类比前端main.ts入口函数，统一完成全局初始化工作
        /// </summary>
        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            // 在 App.xaml.cs 的 Application_Startup 中，不要恢复 ShutdownMode 为 OnLastWindowClose，直接保持全局 OnExplicitShutdown：
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var splashWindow = new SplashView();
            var splashShownAt = DateTime.Now;
            splashWindow.Show();

            try
            {
                // 1. 初始化日志体系（LogConfig 装配 → LogRouter 路由 → 文件/JSON Sink 落盘）
                //    LogPath 等配置来自 exe.config 的 appSettings（与 SystemSettingView「日志与诊断」Tab 同源同键），
                //    解决了旧版 FileLogSink 不消费 LogPath 配置项的「孤儿配置」问题。
                LogConfig.Instance.LoadFromAppSettings();
                // 日志写失败告警（磁盘满等）：UI 一次性提示，防止产线「日志悄悄丢」无人知晓。
                // 注意：此订阅者只做界面提示，切勿在此写日志（防递归）。
                LogBus.LoggingFailed += OnLoggingFailed;

#if DEBUG
                // 【VS 开发环境】：
                // 日志默认输出到 VS「输出(Output)」窗口（由 LogRouter.Publish 的 #if DEBUG 输出）；
                // 2026-09-15 起本地落盘常开（工位 002 上机排障需要完整日志文件；LogPath 默认 运行目录\Logs）：
                _fileLogSink = new FileLogSink(LogConfig.Instance.LogPath);
                LogRouter.AddSink(_fileLogSink);
#else
                // 【生产打包环境】：装配文件 Sink（+ 可选结构化 JSON Sink）。
                // AddSink 内部自动 Configure + Enable（启动写盘线程），注册即生效。
                _fileLogSink = new FileLogSink(LogConfig.Instance.LogPath);
                LogRouter.AddSink(_fileLogSink);
                if (LogConfig.Instance.JsonEnabled)
                {
                    // 结构化 NDJSON 落盘（供清洗 / AI 分析消费，如 {进程名}_{日期}.jsonl）
                    LogRouter.AddSink(new JsonLogSink(LogConfig.Instance.LogPath));
                }
#endif
                LogRouter.ApplyConfig(LogConfig.Instance);

                // 2. 初始化存储层数据库路径 (存放在运行目录 Data/GraysonVision.db)
                string dbPath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Data", "GraysonVision.db");
                StorageFactory.Initialize(dbPath);

                // 1. 初始化账号认证服务（当前使用Mock模拟登录服务，可替换为数据库/网络登录实现）
                _authService = new LiteDbAuthenticationService();

                // 2. 注册两套全局异常捕获，兜底防止程序无提示闪退
                // AppDomain：捕获后台非UI线程、Task、子线程抛出的未处理异常（如视觉采集、运动控制后台线程报错）
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                // Dispatcher：捕获UI主线程控件绑定、页面操作产生的异常（等同于前端全局errorHandler捕获界面错误）
                DispatcherUnhandledException += OnDispatcherUnhandledException;

                // 🚀 在程序启动时，通过统一的 StationHostRuntime 初始化设备池与 Core 运行时
                var runtimeInstance = new Grayson.Vision.Core.Station.StationHostRuntime();
                StationHostRuntime = runtimeInstance;
                Grayson.Vision.Core.Station.StationHostRuntime.GlobalInstance = runtimeInstance;

                // 统一 StationRuntimeManager 也指向同一运行时，保证 UI 各 VM 读取到一致状态
                Grayson.Vision.Core.Client.StationRuntimeManager.GlobalInstance = new Grayson.Vision.Core.Client.StationRuntimeManager(runtimeInstance);

                await StationHostRuntime.InitializeAsync();

                // 🌟 初始化节点工厂：扫描并注册所有流程节点，确保配方加载时能正确还原 ParameterModel、ExecutorType 和端口
                // 必须在首次加载配方之前完成初始化，否则反序列化的节点会缺失业务参数和执行器信息
                Grayson.Vision.Contracts.Flow.Factories.NodeFactory.Initialize();

                // 🚀 加载节点插件：扫描并注册所有算子插件 DLL 中的节点类型到 NodeFactory
                // 这一步至关重要：必须在 RecipeStorageService.LoadRecipe 之前执行，否则首次加载配方时节点信息不完整
                // 原本此逻辑在 FlowVm 构造函数中执行，但那时配方可能已经加载完毕，导致首次跳转数据异常
                string pluginDir = System.AppDomain.CurrentDomain.BaseDirectory;
                new Grayson.Vison.FlowEdit.Services.NodePluginLoader().LoadPlugins(pluginDir,
                    msg => LogBus.Info("Plugin", msg));

                // FlowEdit 作为 UserControl 嵌入 WpfUI 时其 App.OnStartup 不会执行，
                // 因此把 WpfUI 的 StationHostRuntime 共享给 FlowEdit，使其 FlowVm 可正常初始化。
                try
                {
                    FlowEditApp.StationHostRuntime = StationHostRuntime;
                }
                catch
                {
                    // FlowEditApp 类型不可用时不影响主程序启动
                }

                // 🌟 引擎参数/示教面板注册表（StationMonitorExtensionRegistry，命名沿用）：
                // 2026-09-06 职责收敛——示教/参数面板不再挂「单工位监控」扩展位（监视页回归纯看板），
                // 改由「工位工程工作台 → ④ 执行方案」Tab 按工位 ProcessKey 就地挂载（StationManageViewModel）。
                // 新增业务引擎只需 Register 面板，④Tab 选中该引擎并保存同步后自动出现表单。
                StationMonitorExtensionRegistry.Register("MahjongPick",
                    () => new View.StationMonitorExtensions.MahjongPickTeachPanel());

                // 通用取放引擎 VisionPickPlace 示教面板（S2 双吸嘴 / S3 同心吸嘴共用）：
                // 角度策略 + TeachMode + 位点/偏心/真空 IO 参数表单。
                StationMonitorExtensionRegistry.Register("VisionPickPlace",
                    () => new View.StationMonitorExtensions.VisionPickPlaceTeachPanel());

                var elapsed = DateTime.Now - splashShownAt;
                var minimumDuration = TimeSpan.FromMilliseconds(1200);
                if (elapsed < minimumDuration)
                {
                    await Task.Delay(minimumDuration - elapsed);
                }

                // 3. 先关闭启动动画，再弹出登录窗口（ShowDialog 会阻塞）
                if (splashWindow.IsVisible)
                {
                    splashWindow.Close();
                }

              
                ShowLoginWindow();
                LogBus.Info("System", "应用程序启动完成！");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"系统启动失败: {ex.Message}", "启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
            finally
            {
                if (splashWindow.IsVisible)
                {
                    splashWindow.Close();
                }

              
            }
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
            if (_userLoggedOutHandler != null)
            {
                GlobalData.Instance.UserLoggedOut -= _userLoggedOutHandler;
            }

            _userLoggedOutHandler = (s, e) =>
            {
                if (shellView.IsVisible)
                {
                    shellView.Close();
                }

                ShowLoginWindow();
            };

            GlobalData.Instance.UserLoggedOut += _userLoggedOutHandler;

            shellView.Closed += (s, e) =>
            {
                if (_userLoggedOutHandler != null)
                {
                    GlobalData.Instance.UserLoggedOut -= _userLoggedOutHandler;
                    _userLoggedOutHandler = null;
                }
            };

            // 将当前主窗体赋值给应用全局MainWindow对象
            MainWindow = shellView;
            // 非模态窗口，打开后不阻塞程序其他逻辑
            shellView.Show();

            // 程序登录成功默认跳转到【产线拓扑总览】页面
            navigationService.NavigateTo(PageType.LineOverview);

            // 🌟 启动自动全量同步（2026-09-09）：把数据库已启用工位完整装配进 StationHostRuntime。
            // 背景：Core 不做 DB 自动还原，启动后监视页连接只建「裸工位」（不装配方/设备/过程/触发源），
            // 此前每次启动都要先去工位工程工作台点一次「💾 保存并同步」才能到监视页启动。
            // 此处登录进主界面即后台按数据库逐个装配所有已启用工位 → 监视页/总览页直接可启动。
            // 时序：Application_Startup 已完成 设备池初始化 + NodeFactory + 节点插件加载，
            //      DevicePool/Recipe 存储均就绪，可安全装配。幂等 + 逐工位容错 + 只记日志不弹窗。
            try
            {
                new Grayson.Vision.WpfUI.Service.StationRuntimeBootstrap()
                    .SyncAllEnabledStationsInBackground();
            }
            catch (Exception bootstrapEx)
            {
                LogBus.Error("System", $"触发启动自动同步失败: {bootstrapEx.Message}", bootstrapEx);
            }
        }

        /// <summary>
        /// 统一注册系统全部业务页面到导航服务
        /// 作用：导航服务只保存页面创建工厂方法，不会一次性实例化所有页面，节省内存（懒加载）
        /// 前端类比：Vue Router中routes路由表配置，每个路由对应一个页面组件
        /// </summary>
        /// <param name="navigationService">页面导航管理器</param>
        private void RegisterPages(NavigationService navigationService)
        {

            // 产线拓扑总览页面——缓存单例：统计轮询与缩略图订阅跨导航存活，
            // 避免每次新建导致事件重复订阅/泄漏、统计数据归零
            navigationService.RegisterPage(PageType.LineOverview, () =>
            {
                if (_lineOverviewPage == null)
                {
                    _lineOverviewPage = new LineOverviewView();
                }
                return _lineOverviewPage;
            });
            // 单工位监控页面——缓存单例：工位任务进行中离开页面再进入，
            // 统计/日志/图像历史/连接全部保留（其余页面保持原「每次全新」策略）
            navigationService.RegisterPage(PageType.StationMonitor, () =>
            {
                if (_stationMonitorPage == null)
                {
                    _stationMonitorPage = new StationMonitorView();
                }
                return _stationMonitorPage;
            });
            // 报警记录页面
            navigationService.RegisterPage(PageType.Alarm, () => new AlarmView());
            // 流程编辑页面
            navigationService.RegisterPage(PageType.FlowEdit, () => new FlowEditViewWrapper());
            // 工位管理页面
            navigationService.RegisterPage(PageType.StationManage, () => new StationManageView());
            // 模板工作台页面——缓存单例：源图/ROI/掩膜/特征编辑现场跨导航存活（v3 信息架构独立菜单页）
            navigationService.RegisterPage(PageType.TemplateManage, () =>
            {
                if (_templateManagerPage == null)
                {
                    _templateManagerPage = new TemplateManagerView();
                }
                return _templateManagerPage;
            });
            // 配方管理页面——缓存单例（2026-09-05 中等重构）：列表选中/审批上下文跨导航存活；
            // OnNavigatedTo 接收 BoundRecipe 参数时定位选中（来自工位工作台跳转）
            navigationService.RegisterPage(PageType.RecipeManage, () =>
            {
                if (_recipeManagePage == null)
                {
                    _recipeManagePage = new RecipeManageView();
                }
                return _recipeManagePage;
            });
            // 校准管理页面——缓存单例（2026-09-05）：工位定位态与方案编辑现场跨导航存活
            navigationService.RegisterPage(PageType.CalibrationManage, () =>
            {
                if (_calibrationPage == null)
                {
                    _calibrationPage = new CalibrationManagerView();
                }
                return _calibrationPage;
            });
            // 插件管理页面
            navigationService.RegisterPage(PageType.PluginManage, () => new PluginManageView());
            //设备池页面 
            navigationService.RegisterPage(PageType.DevicePool, () => new DevicePoolView());
            // 硬件控制台页面  
            navigationService.RegisterPage(PageType.HardwareConsole, () => new HardwareConsoleView());
            // 数据追溯页面
            navigationService.RegisterPage(PageType.DataTrace, () => new DataTraceView());
            // MES 对接状态页面
            navigationService.RegisterPage(PageType.MesBridge, () => new MesBridgeView());
            // 系统与存储设置页面
            navigationService.RegisterPage(PageType.SystemSetting, () => new SystemSettingView());
            // 工程工具:相机装调助手(垂直度检测与三点对焦调平闭环, 2026-09-06;每次全新不缓存,离开即清理)
            navigationService.RegisterPage(PageType.CameraTuneTool, () => new CameraTuneView());
            // 任务模板中心（T 层任务模板库：引导定位/深度学习推理/外观测量，2026-09-09 新增；数据即库即读，每次全新）
            navigationService.RegisterPage(PageType.TaskTemplateCenter, () => new TaskTemplateCenterView());
            // 模型仓库（深度学习/测量推理资产注册中心，任务模板按 ModelAssetCode 引用）
            navigationService.RegisterPage(PageType.ModelRegistry, () => new ModelRegistryView());
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
            // 🌟 崩溃前先落日志（此时是最后的记录机会——最需要日志的时刻不能只有弹窗）
            LogBus.Error("System", $"严重未处理异常: {exception?.Message}", exception);
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
            // 🌟 先落日志再弹窗（修复原「只弹窗不写日志」：界面异常也必须可追溯）
            LogBus.Error("System", $"界面操作异常: {e.Exception.Message}", e.Exception);
            MessageBox.Show($"界面操作发生错误:\n{e.Exception.Message}\n\n详细信息:\n{e.Exception.StackTrace}",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            // 告知框架异常已人工处理，不再向上抛出导致程序崩溃
            e.Handled = true;
        }

        /// <summary>
        /// 日志写盘失败告警（磁盘满 / 目录不可写等）：UI 一次性提示，防止产线「日志悄悄丢」无人知晓。
        /// 注意：此方法内禁止再写日志（防递归），只做界面提示。
        /// </summary>
        private void OnLoggingFailed(string sinkName, int failures, Exception ex)
        {
            try
            {
                // 节流：同一秒内多次失败只提示一次，避免写盘失败时弹窗刷屏
                if ((DateTime.Now - _lastLoggingFailedNotifiedAt).TotalSeconds < 1) return;
                _lastLoggingFailedNotifiedAt = DateTime.Now;

                // 后台线程触发 → 切到 UI 线程弹窗
                Dispatcher.BeginInvoke(new Action(() =>
                    MessageBox.Show($"日志写入失败（{sinkName}，累计 {failures} 次）：\n{ex?.Message}\n\n" +
                                    "请检查磁盘空间或日志目录权限，否则日志将无法记录！",
                        "日志告警", MessageBoxButton.OK, MessageBoxImage.Warning)));
            }
            catch
            {
                // 告警提示异常不影响主程序
            }
        }

        /// <summary>
        /// 程序退出时统一释放 StationHostRuntime 与全局设备池资源。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                StationHostRuntime?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] 退出释放资源异常: {ex.Message}");
            }
            // 🌟 冲刷并关闭所有日志 Sink（防程序退出时丢失缓冲中的日志）
            try
            {
                LogRouter.ShutdownAll();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] 日志关闭异常: {ex.Message}");
            }
            base.OnExit(e);
        }
    }
}