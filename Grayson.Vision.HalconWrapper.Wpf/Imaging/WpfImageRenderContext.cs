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
            // 1. 释放 HalconRenderImage（含底层 HImage 句柄）
            if (Image is IDisposable disposableImg)
            {
                disposableImg.Dispose();
                Image = null;
            }

            DisposeShell();
        }

        /// <summary>
        /// 只释放上下文壳资源（缩略图/叠加层句柄），**不释放**图像句柄。
        ///
        /// 借用语义：ShapeMatch/NccMatch 等节点的 MatchImage 输出端口值 = 上游相机
        /// 输出的同一 HImage 实例（WrapImage 对 HImage 保持同一实例）。此时同一张
        /// 图像会被多个 NodeId 的上下文（相机节点 + 匹配节点）各自包一层
        /// HalconRenderImage——包装对象不同，底层 HImage 相同。任何一个上下文完整
        /// Dispose 都会把还在被其他上下文（以及显示控件场景底图）使用的 HImage 销毁，
        /// 表现为"底图瞬间消失只剩黑屏+匹配轮廓"。因此替换上下文时必须先按
        /// NativeHandle 判断图像是否仍被引用，仍被引用时只调用本方法清壳。
        /// </summary>
        public void DisposeShell()
        {
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
