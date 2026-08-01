//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: Halcon 图像显示宿主，封装 HSmartWindowControlWPF。
//===================================================================================

using System;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Wpf.Controls
{
    /// <summary>
    /// Halcon 图像显示宿主
    /// </summary>
    public partial class HalconImageDisplayHost : UserControl, IImageDisplayHost
    {
        private HWindow _hWindow;
        private readonly IImageRenderService _renderService;

        /// <summary>
        /// 宿主是否已就绪
        /// </summary>
        public bool IsReady => _hWindow != null;

        /// <summary>
        /// 鼠标像素移动事件
        /// </summary>
        public event EventHandler<CursorPixelEventArgs> CursorPixelMoved;

        /// <summary>
        /// 构造
        /// </summary>
        public HalconImageDisplayHost()
        {
            InitializeComponent();
            _renderService = new HalconImageRenderService();
        }

        /// <summary>
        /// 显示指定渲染上下文
        /// </summary>
        public void Display(ImageRenderContext context)
        {
            if (_hWindow == null || context == null) return;

            Dispatcher.InvokeAsync(() =>
            {
                _renderService.RenderToWindow(_hWindow, context.Image, context.Overlays);
                if (context.Image != null)
                    _renderService.FitImageToWindow(_hWindow, context.Image);
            });
        }

        /// <summary>
        /// 让当前显示内容自适应窗口
        /// </summary>
        public void FitImage()
        {
            // 保留给外部调用，实际显示时由 Display 内部自适应
        }

        private void SmartWindow_HInitWindow(object sender, EventArgs e)
        {
            _hWindow = SmartWindow.HalconWindow;
            _hWindow.SetDraw("margin");
            _hWindow.SetLineWidth(2);
        }

        private void SmartWindow_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (SmartWindow == null || _hWindow == null) return;
            var pos = e.GetPosition(SmartWindow);
            // 粗略映射为图像像素坐标，实际应用需要按窗口缩放比例换算
            CursorPixelMoved?.Invoke(this, new CursorPixelEventArgs { X = (int)pos.X, Y = (int)pos.Y });
        }
    }
}
