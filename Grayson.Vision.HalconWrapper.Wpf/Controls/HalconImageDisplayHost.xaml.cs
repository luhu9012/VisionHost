using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using HalconDotNet;
using System;
using System.Windows;
using System.Windows.Controls;

namespace Grayson.Vision.HalconWrapper.Wpf.Controls
{
    /// <summary>
    /// Halcon图像渲染宿主控件
    /// 封装Halcon HWindow窗口，实现图像渲染、自适应缩放、鼠标坐标采集、VM渲染事件订阅
    /// 实现IImageDisplayHost统一图像显示宿主接口，供上层流程画布绑定使用
    /// </summary>
    public partial class HalconImageDisplayHost : UserControl, IImageDisplayHost
    {
        /// <summary>Halcon底层窗口句柄，用于绘制图像、绘制叠加轮廓</summary>
        private HWindow _hWindow;

        /// <summary>图像渲染服务，封装Halcon图像绘制、自适应填充逻辑</summary>
        private readonly IImageRenderService _renderService;

        /// <summary>缓存当前绑定的图像ViewModel，用于事件解绑防止内存泄漏</summary>
        private ImageDisplayVm _boundVm;

        /// <summary>窗口是否初始化完成（拥有有效Halcon句柄）</summary>
        public bool IsReady => _hWindow != null;

        /// <summary>鼠标在图像上移动时触发事件，传递鼠标像素坐标</summary>
        public event EventHandler<CursorPixelEventArgs> CursorPixelMoved;

        #region 依赖属性：RenderContext 绑定图像渲染上下文
        /// <summary>
        /// 可绑定依赖属性：图像渲染上下文（包含图像、绘制叠加层、节点信息）
        /// 支持XAML双向绑定，切换图像时自动触发渲染
        /// </summary>
        public static readonly DependencyProperty RenderContextProperty =
            DependencyProperty.Register(
                nameof(RenderContext),
                typeof(ImageRenderContext),
                typeof(HalconImageDisplayHost),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnRenderContextChanged // 上下文变更回调
                ));

        /// <summary>当前绑定的图像渲染上下文</summary>
        public ImageRenderContext RenderContext
        {
            get => (ImageRenderContext)GetValue(RenderContextProperty);
            set => SetValue(RenderContextProperty, value);
        }
        #endregion

        public HalconImageDisplayHost()
        {
            InitializeComponent();
            // 实例化Halcon图像渲染工具服务
            _renderService = new HalconImageRenderService();

            // 监听控件DataContext切换，自动订阅/解绑VM渲染事件，避免内存泄漏
            this.DataContextChanged += HalconImageDisplayHost_DataContextChanged;
        }

        /// <summary>
        /// DataContext变更回调：切换绑定的ImageDisplayVm时处理事件订阅
        /// 核心：先解绑旧VM渲染事件，再绑定新VM的渲染推送事件
        /// </summary>
        private void HalconImageDisplayHost_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // 1. 解绑上一个ViewModel的渲染事件，防止多次订阅、内存泄漏
            if (_boundVm != null)
            {
                _boundVm.OnRequestRender -= HandleRequestRender;
                LogBus.Debug("HalconHost", "已解绑旧 ImageDisplayVm.OnRequestRender 事件");
                _boundVm = null;
            }

            // 2. 新ViewModel绑定，订阅渲染推送事件
            if (e.NewValue is ImageDisplayVm newVm)
            {
                _boundVm = newVm;
                _boundVm.OnRequestRender += HandleRequestRender;
                //🌟 订阅自适应事件
                _boundVm.OnRequestFitImage += FitImage;
                LogBus.Info("HalconHost", "成功订阅 ImageDisplayVm.OnRequestRender 事件！");

                // 如果VM当前已有激活图像，控件窗口已就绪则立刻渲染
                if (newVm.ActiveImageContext != null)
                {
                    HandleRequestRender(newVm.ActiveImageContext);
                }
            }
        }

        /// <summary>
        /// 响应ViewModel主动发起的渲染请求
        /// VM切换图像、运行节点推送图像时会触发该回调
        /// </summary>
        /// <param name="context">待渲染图像上下文，null代表清空窗口</param>
        private void HandleRequestRender(ImageRenderContext context)
        {
            LogBus.Info("HalconHost", context == null
                ? "[OnRequestRender] 收到清空指令"
                : $"[OnRequestRender] 收到渲染请求，节点: [{context.NodeName}]");
            // 统一调用渲染方法
            Display(context);
        }

        /// <summary>
        /// RenderContext依赖属性变更静态回调
        /// XAML绑定的图像上下文切换时自动执行渲染
        /// </summary>
        private static void OnRenderContextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HalconImageDisplayHost host)
            {
                if (e.NewValue is ImageRenderContext context)
                {
                    LogBus.Info("HalconHost", $"[OnRenderContextChanged] 监听到 RenderContext 改变，节点名称: [{context.NodeName ?? "Unknown"}]，准备触发 Display(...)");
                    host.Display(context);
                }
                else
                {
                    LogBus.Warn("HalconHost", "[OnRenderContextChanged] 接收到 null 或非 ImageRenderContext 对象，跳过渲染。");
                }
            }
        }

        /// <summary>上一次渲染图像宽度，用于判断是否需要重新自适应窗口</summary>
        private int _lastImageWidth = 0;
        /// <summary>上一次渲染图像高度，用于判断是否需要重新自适应窗口</summary>
        private int _lastImageHeight = 0;

        /// <summary>
        /// 核心图像渲染方法：绘制图像+叠加轮廓，空上下文则清空窗口
        /// 全部调度至WPF渲染优先级线程执行，防止跨线程报错
        /// </summary>
        /// <param name="context">图像渲染上下文</param>
        public void Display(ImageRenderContext context)
        {
            // Halcon窗口未初始化，直接放弃渲染
            if (_hWindow == null)
            {
                LogBus.Warn("HalconHost", $"[Display] 跳过渲染 - HWindow 已准备: {_hWindow != null}");
                return;
            }

            // 切换至渲染线程执行绘制逻辑
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // 上下文为空 / 无图像：清空窗口，重置尺寸缓存
                    if (context?.Image == null)
                    {
                        _hWindow.ClearWindow();
                        _lastImageWidth = 0;
                        _lastImageHeight = 0;
                        LogBus.Debug("HalconHost", "[Display] 窗口与状态已成功清空。");
                        return;
                    }

                    // 1. 渲染原图 + 叠加绘图（ROI、检测框、文字等Overlay）
                    _renderService.RenderToWindow(_hWindow, context.Image, context.Overlays);
                    LogBus.Debug("HalconHost", $"[Display] 图像已成功 DispObj 到 HWindow (尺寸: {context.Image.Width}x{context.Image.Height})");

                    // 2. 仅当图像分辨率变化时，执行窗口自适应填充（性能优化，避免重复缩放）
                    if (context.Image != null)
                    {
                        if (_lastImageWidth != context.Image.Width || _lastImageHeight != context.Image.Height)
                        {
                            _renderService.FitImageToWindow(_hWindow, context.Image);
                            SmartWindow?.SetFullImagePart();

                            // 更新尺寸缓存
                            _lastImageWidth = context.Image.Width;
                            _lastImageHeight = context.Image.Height;
                            LogBus.Info("HalconHost", $"[Display] 视口区域根据新图像尺寸 [{context.Image.Width}x{context.Image.Height}] 完成自适应调整。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Error("HalconHost", $"渲染主图失败: {ex.Message}", ex);
                }
            }, System.Windows.Threading.DispatcherPriority.Render); // 指定渲染优先级，UI刷新更顺滑
        }

        /// <summary>
        /// 手动触发图像自适应窗口铺满
        /// 供工具栏"适配图像"按钮调用
        /// </summary>
        public void FitImage()
        {
            // 优先取绑定的RenderContext，无则取ViewModel激活图像
            var activeContext = RenderContext ?? _boundVm?.ActiveImageContext;
            if (_hWindow != null && activeContext?.Image != null)
            {
                _renderService.FitImageToWindow(_hWindow, activeContext.Image);
                SmartWindow?.SetFullImagePart();
                LogBus.Debug("HalconHost", "手动触发了图像自适应窗口 (FitImage)。");
            }
        }

        /// <summary>
        /// Halcon底层窗口初始化完成回调
        /// 获取HWindow句柄，设置全局绘图样式，补发待渲染图像
        /// </summary>
        private void SmartWindow_HInitWindow(object sender, EventArgs e)
        {
            // 捕获Halcon原生窗口句柄
            _hWindow = SmartWindow.HalconWindow;
            // 绘图模式：图像边缘留白
            _hWindow.SetDraw("margin");
            // 默认线条宽度2像素
            _hWindow.SetLineWidth(2);

            LogBus.Info("HalconHost", "SmartWindow 句柄 HInitWindow 初始化完成！");

            // 窗口就绪后，补发当前待渲染图像
            var currentContext = RenderContext ?? _boundVm?.ActiveImageContext;
            if (currentContext != null)
            {
                LogBus.Info("HalconHost", "窗口准备完毕，开始补发渲染当前 Context...");
                Display(currentContext);
            }
            else
            {
                LogBus.Debug("HalconHost", "窗口准备完毕，当前无待渲染的 RenderContext。");
            }
        }

        /// <summary>
        /// 窗口鼠标移动事件
        /// 捕获鼠标坐标，向上抛出事件，同步更新ViewModel像素信息
        /// </summary>
        private void SmartWindow_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (SmartWindow == null || _hWindow == null) return;

            // 1. 判断是否按下了 Ctrl 键
            bool isCtrlPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;

            if (!isCtrlPressed) return;

            try
            {
                // 2. 获取当前图像上下文与尺寸
                var activeContext = RenderContext ?? _boundVm?.ActiveImageContext;
                if (activeContext?.Image == null) return;

                int imgWidth = activeContext.Image.Width;
                int imgHeight = activeContext.Image.Height;

                // 3. 🌟 关键点：优先尝试 Halcon 原生获取，若失败/不灵敏则用 Part 矩阵反算
                double row = 0, col = 0;
                bool getPosSuccess = false;

                try
                {
                    // 尝试直接获取（注意：Halcon 内部 Handle 在无 mouse-button 时可能抛异常）
                    _hWindow.GetMpositionSubPix(out row, out col, out _);
                    getPosSuccess = true;
                }
                catch
                {
                    // 如果 Halcon 抛异常，降级到 WPF 坐标 + HWindow Part 逆映射算法
                    var mousePos = e.GetPosition(SmartWindow);
                    if (SmartWindow.ActualWidth > 0 && SmartWindow.ActualHeight > 0)
                    {
                        // 获取当前窗口显示的图像 Part 区域 (row1, col1, row2, col2)
                        _hWindow.GetPart(out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);

                        double partWidth = c2.D - c1.D;
                        double partHeight = r2.D - r1.D;

                        // 算坐标比例 (WPF控件坐标 -> 图像Part真实坐标)
                        col = c1.D + (mousePos.X / SmartWindow.ActualWidth) * partWidth;
                        row = r1.D + (mousePos.Y / SmartWindow.ActualHeight) * partHeight;
                        getPosSuccess = true;
                    }
                }

                if (getPosSuccess)
                {
                    // 取整获取像素阵列的列(X)和行(Y)
                    int imgX = (int)Math.Floor(col);
                    int imgY = (int)Math.Floor(row);

                    // 4. 越界检查：基于图片 0,0 到 Width,Height 判定
                    if (imgX >= 0 && imgX < imgWidth && imgY >= 0 && imgY < imgHeight)
                    {
                        // 更新 ViewModel 中的 SelectedImageInfo 文本
                        if (DataContext is ImageDisplayVm vm)
                        {
                            vm.UpdateCursorPixelInfo(imgX, imgY);
                        }

                        CursorPixelMoved?.Invoke(this, new CursorPixelEventArgs { X = imgX, Y = imgY });
                    }
                }
            }
            catch (Exception ex)
            {
                LogBus.Debug("HalconHost", $"MouseMove 坐标转换微损: {ex.Message}");
            }
        }
        /// <summary>
        /// 监听外层 Grid 的 MouseMove，彻底绕过 HSmartWindowControlWPF 的 Win32 消息截断问题
        /// </summary>
        private void Grid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (SmartWindow == null || _hWindow == null) return; 

    // 1. 判断是否按下了 Ctrl 键
    bool isCtrlPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;

            if (!isCtrlPressed) return;

            try
            {
                // 2. 获取当前图像上下文
                var activeContext = RenderContext ?? _boundVm?.ActiveImageContext;
        if (activeContext?.Image == null) return; 

        int imgWidth = activeContext.Image.Width;
                int imgHeight = activeContext.Image.Height;

                // 3. 获取鼠标在 SmartWindow 控件上的实时 WPF 像素坐标
                var mousePos = e.GetPosition(SmartWindow);

                if (SmartWindow.ActualWidth > 0 && SmartWindow.ActualHeight > 0)
                {
                    // 4. 获取当前 Halcon 窗口显示的图像 Part 区域 (row1, col1, row2, col2)
                    _hWindow.GetPart(out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);

                    double partWidth = c2.D - c1.D;
                    double partHeight = r2.D - r1.D;

                    // 5. 将 WPF 相对坐标精准映射为图片像素坐标 (Col = X, Row = Y)
                    double col = c1.D + (mousePos.X / SmartWindow.ActualWidth) * partWidth;
                    double row = r1.D + (mousePos.Y / SmartWindow.ActualHeight) * partHeight;

                    int imgX = (int)Math.Floor(col);
                    int imgY = (int)Math.Floor(row);

                    // 6. 越界检查：只有在图像真实的 [0,0] ~ [Width, Height] 范围内才更新
                    if (imgX >= 0 && imgX < imgWidth && imgY >= 0 && imgY < imgHeight)
                    {
                        if (DataContext is ImageDisplayVm vm)
                        {
                            vm.UpdateCursorPixelInfo(imgX, imgY); 
                }

                        CursorPixelMoved?.Invoke(this, new CursorPixelEventArgs { X = imgX, Y = imgY }); 
            }
                }
            }
            catch (Exception ex)
            {
                // 忽略视口未准备好时的异常
            }
        }
    }
}