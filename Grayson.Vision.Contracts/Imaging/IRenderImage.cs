//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 与具体图像库（Halcon/OpenCV 等）无关的渲染图像句柄。
//===================================================================================

namespace Grayson.Vision.Contracts.Imaging
{
    /// <summary>
    /// 渲染图像句柄，内部持有原生图像对象（如 HImage）的弱封装。
    /// </summary>
    public interface IRenderImage
    {
        /// <summary>
        /// 原生图像对象（如 Halcon HImage），供渲染实现层读取。
        /// </summary>
        object NativeHandle { get; }

        /// <summary>
        /// 图像宽度（像素）
        /// </summary>
        int Width { get; }

        /// <summary>
        /// 图像高度（像素）
        /// </summary>
        int Height { get; }
    }
}
