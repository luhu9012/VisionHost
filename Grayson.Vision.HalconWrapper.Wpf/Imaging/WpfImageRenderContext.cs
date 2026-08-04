//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 包含 WPF 缩略图的渲染上下文扩展，位于 Wpf 层，不污染 Contracts。
//===================================================================================

using Grayson.Vision.Contracts.Imaging;
using System;
using System.Windows.Media.Imaging;

namespace Grayson.Vision.HalconWrapper.Wpf.Imaging
{
    /// <summary>
    /// WPF 专用渲染上下文，增加了 BitmapSource 缩略图。
    /// </summary>
    public class WpfImageRenderContext : ImageRenderContext, IDisposable
    {
        /// <summary>
        /// WPF 缩略图，用于底部缩略图列表显示。
        /// </summary>
        public BitmapSource Thumbnail { get; set; }
        /// <summary>
        /// 🌟 扩展属性：用于存储附加信息（如文件真实路径、在文件夹中的索引等）
        /// </summary>
        public object Tag { get; set; }

        // ... 其他继承得属性s

        public void Dispose()
        {
            // 1. 释放 HalconRenderImage
            if (Image is IDisposable disposableImg)
            {
                disposableImg.Dispose();
                Image = null; 
        }

            // 2. 释放 Overlays 中可能持有的 HObject 句柄
            if (Overlays != null)
            {
                foreach (var overlay in Overlays)
                {
                    if (overlay?.NativeHandle is IDisposable hObj)
                    {
                        hObj.Dispose();
                    }
                }
                Overlays = null;
            }

            Thumbnail = null;
        }
    }
}
