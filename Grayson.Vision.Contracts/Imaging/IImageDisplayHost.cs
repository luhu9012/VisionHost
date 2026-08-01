//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 图像显示宿主契约，由具体实现（如 Halcon WPF 控件）提供。
//===================================================================================

using System;

namespace Grayson.Vision.Contracts.Imaging
{
    /// <summary>
    /// 图像显示宿主
    /// </summary>
    public interface IImageDisplayHost
    {
        /// <summary>
        /// 宿主是否已就绪
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// 显示指定渲染上下文
        /// </summary>
        /// <param name="context">渲染上下文</param>
        void Display(ImageRenderContext context);

        /// <summary>
        /// 让当前显示内容自适应窗口
        /// </summary>
        void FitImage();

        /// <summary>
        /// 鼠标在图像上移动时触发，参数为图像坐标 (x, y)
        /// </summary>
        event EventHandler<CursorPixelEventArgs> CursorPixelMoved;
    }

    /// <summary>
    /// 鼠标像素移动事件参数
    /// </summary>
    public class CursorPixelEventArgs : EventArgs
    {
        /// <summary>
        /// 图像列坐标 X
        /// </summary>
        public int X { get; set; }

        /// <summary>
        /// 图像行坐标 Y
        /// </summary>
        public int Y { get; set; }
    }
}
