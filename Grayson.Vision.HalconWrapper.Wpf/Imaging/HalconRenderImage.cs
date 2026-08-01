//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: Halcon 渲染图像句柄实现。
//===================================================================================

using Grayson.Vision.Contracts.Imaging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Wpf.Imaging
{
    /// <summary>
    /// Halcon 渲染图像句柄
    /// </summary>
    public class HalconRenderImage : IRenderImage
    {
        /// <summary>
        /// 内部 Halcon 图像
        /// </summary>
        public HImage HImage { get; }

        /// <summary>
        /// 原生句柄
        /// </summary>
        public object NativeHandle => HImage;

        /// <summary>
        /// 图像宽度
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// 图像高度
        /// </summary>
        public int Height { get; }

        /// <summary>
        /// 构造
        /// </summary>
        public HalconRenderImage(HImage image)
        {
            HImage = image;
            if (image != null && image.IsInitialized())
            {
                image.GetImageSize(out int width, out int height);
                Width = width;
                Height = height;
            }
        }
    }
}
