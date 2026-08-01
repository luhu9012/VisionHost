// Grayson.Vision.HalconWrapper.Wpf/Controls/HalconImageDisplayHost.xaml.cs
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
        }

        // 当绑定的 Context 发生变化时（例如用户切换图片或节点执行完），自动触发渲染
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

        public void Display(ImageRenderContext context)
        {
            if (_hWindow == null)
            {
                LogBus.Warn("HalconHost", "⚠️ Display 被调用，但 _hWindow 尚未初始化 (SmartWindow 还没完成 HInitWindow)！请求已暂存，等待窗口加载。");
                return;
            }
            if (context == null)
            {
                LogBus.Warn("HalconHost", "⚠️ Display 被调用，但传入的 RenderContext 为 null！");
                return;
            }

            // 切换到 WPF UI 线程执行 Halcon 渲染
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    LogBus.Debug("HalconHost", $"▶ 开始执行 SmartWindow 主图渲染指令 | 节点: [{context.NodeName}]...");

                    // 1. 调用渲染服务画图与叠加图元
                    _renderService.RenderToWindow(_hWindow, context.Image, context.Overlays);

                    // 2. 图像视口全图自适应（关键步骤：防止视口缩放全黑）
                    if (context.Image != null)
                    {
                        _renderService.FitImageToWindow(_hWindow, context.Image);

                        // 🌟 核心补充：触发 SmartWindow 的全图视图刷新（针对 HSmartWindowControlWPF 特有机制）
                        SmartWindow?.SetFullImagePart();
                        LogBus.Info("HalconHost", $"✔ [主图渲染成功] 图像尺寸: [{context.Image.Width} x {context.Image.Height}]，已适应 SmartWindow 视口。");
                    }
                    else
                    {
                        LogBus.Warn("HalconHost", "⚠️ context.Image 为 null，仅清屏未绘制主图。");
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Error("HalconHost", $"❌ 渲染主图到 SmartWindow 失败: {ex.Message}", ex);
                }
            });
        }

        public void FitImage()
        {
            if (_hWindow != null && RenderContext?.Image != null)
            {
                _renderService.FitImageToWindow(_hWindow, RenderContext.Image);
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

            // 窗口加载完成时，如果已经有图片，立即补发绘制
            if (RenderContext != null)
            {
                LogBus.Info("HalconHost", "窗口准备完毕，开始补发渲染之前已设置的 RenderContext...");
                Display(RenderContext);
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