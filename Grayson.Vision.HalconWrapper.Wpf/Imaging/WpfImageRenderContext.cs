//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 包含 WPF 缩略图的渲染上下文扩展，位于 Wpf 层，不污染 Contracts。
//===================================================================================

using System.Windows.Media.Imaging;
using Grayson.Vision.Contracts.Imaging;

namespace Grayson.Vision.HalconWrapper.Wpf.Imaging
{
    /// <summary>
    /// WPF 专用渲染上下文，增加了 BitmapSource 缩略图。
    /// </summary>
    public class WpfImageRenderContext : ImageRenderContext
    {
        /// <summary>
        /// WPF 缩略图，用于底部缩略图列表显示。
        /// </summary>
        public BitmapSource Thumbnail { get; set; }
    }
}
