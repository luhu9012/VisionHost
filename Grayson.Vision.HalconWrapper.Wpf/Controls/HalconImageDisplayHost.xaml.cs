
using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Logging;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using HalconDotNet;
using System;
using System.Windows;
using System.Windows.Controls;

namespace Grayson.Vision.HalconWrapper.Wpf.Controls
{
    public partial class HalconImageDisplayHost : UserControl, IImageDisplayHost
    {
        private HWindow _hWindow;
        private readonly IImageRenderService _renderService;
        private ImageDisplayVm _boundVm; // 保存绑定的 ViewModel 引用，便于解绑

        public bool IsReady => _hWindow != null;
        public event EventHandler<CursorPixelEventArgs> CursorPixelMoved;

        // 注册 RenderContext 依赖属性，支持 WPF 双向/单向绑定
        public static readonly DependencyProperty RenderContextProperty =
            DependencyProperty.Register(
                nameof(RenderContext),
                typeof(ImageRenderContext),
                typeof(HalconImageDisplayHost),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRenderContextChanged));

        public ImageRenderContext RenderContext
        {
            get => (ImageRenderContext)GetValue(RenderContextProperty);
            set => SetValue(RenderContextProperty, value);
        }

        public HalconImageDisplayHost()
        {
            InitializeComponent();
            _renderService = new HalconImageRenderService();

            // 🌟 核心增加：监听 DataContext 变化，订阅 ViewModel 的 OnRequestRender 渲染委托
            this.DataContextChanged += HalconImageDisplayHost_DataContextChanged;
        }

        private void HalconImageDisplayHost_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // 1. 解绑旧 ViewModel 的订阅，防止内存泄漏
            if (_boundVm != null)
            {
                _boundVm.OnRequestRender -= HandleRequestRender;
                LogBus.Debug("HalconHost", "已解绑旧 ImageDisplayVm.OnRequestRender 事件");
                _boundVm = null;
            }

            // 2. 绑定新 ViewModel 的 OnRequestRender 事件
            if (e.NewValue is ImageDisplayVm newVm)
            {
                _boundVm = newVm;
                _boundVm.OnRequestRender += HandleRequestRender;
                LogBus.Info("HalconHost", "成功订阅 ImageDisplayVm.OnRequestRender 事件！");

                // 如果此时 ViewModel 已有激活图像，补刷一次
                if (newVm.ActiveImageContext != null)
                {
                    HandleRequestRender(newVm.ActiveImageContext);
                }
            }
        }

        /// <summary>
        /// 响应 ImageDisplayVm 主动发起的 OnRequestRender 渲染通知
        /// </summary>
        private void HandleRequestRender(ImageRenderContext context)
        {
            if (context == null) return;
            LogBus.Info("HalconHost", $"[OnRequestRender 响应] 收到渲染请求，节点: [{context.NodeName}]，图像尺寸: [{context.Image?.Width}x{context.Image?.Height}]");
            Display(context);
        }

        // 当绑定的 Context 发生变化时（如切换不同节点），自动触发渲染
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

        private int _lastImageWidth = 0;
        private int _lastImageHeight = 0;

        public void Display(ImageRenderContext context)
        {
            if (_hWindow == null || context?.Image == null)
            {
                LogBus.Warn("HalconHost", $"[Display] 跳过渲染 - HWindow 已准备: {_hWindow != null}, Context/Image 是否为空: {context?.Image == null}");
                return;
            }

            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // 1. 执行 Halcon 内存图像绘制与 Overlays 叠加
                    _renderService.RenderToWindow(_hWindow, context.Image, context.Overlays);
                    LogBus.Debug("HalconHost", $"[Display] 图像已成功 DispObj 到 HWindow (尺寸: {context.Image.Width}x{context.Image.Height})");

                    if (context.Image != null)
                    {
                        // 🌟 性能优化：仅在图像分辨率尺寸发生变化时，才重置视口
                        if (_lastImageWidth != context.Image.Width || _lastImageHeight != context.Image.Height)
                        {
                            _renderService.FitImageToWindow(_hWindow, context.Image);
                            SmartWindow?.SetFullImagePart();

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
            }, System.Windows.Threading.DispatcherPriority.Render); // 渲染级别 Dispatcher 响应
        }

        public void FitImage()
        {
            var activeContext = RenderContext ?? _boundVm?.ActiveImageContext;
            if (_hWindow != null && activeContext?.Image != null)
            {
                _renderService.FitImageToWindow(_hWindow, activeContext.Image);
                SmartWindow?.SetFullImagePart();
                LogBus.Debug("HalconHost", "手动触发了图像自适应窗口 (FitImage)。");
            }
        }

        private void SmartWindow_HInitWindow(object sender, EventArgs e)
        {
            _hWindow = SmartWindow.HalconWindow;
            _hWindow.SetDraw("margin");
            _hWindow.SetLineWidth(2);

            LogBus.Info("HalconHost", "SmartWindow 句柄 HInitWindow 初始化完成！");

            // 优先使用 RenderContext，无则使用 ViewModel 中的 ActiveImageContext
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

        private void SmartWindow_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (SmartWindow == null || _hWindow == null) return;
            var pos = e.GetPosition(SmartWindow);

            // 触发事件通知绑定的 ViewModel 更新像素信息
            CursorPixelMoved?.Invoke(this, new CursorPixelEventArgs { X = (int)pos.X, Y = (int)pos.Y });

            // 如果 DataContext 是 ImageDisplayVm，可以直接通知
            if (DataContext is ImageDisplayVm vm)
            {
                vm.UpdateCursorPixelInfo((int)pos.X, (int)pos.Y);
            }
        }
    }
}