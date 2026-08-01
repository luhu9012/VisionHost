//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 图像渲染服务契约，由具体图像库（如 Halcon）实现。
//===================================================================================

using System.Collections.Generic;



namespace Grayson.Vision.Contracts.Imaging
{
    /// <summary>
    /// 图像渲染服务契约
    /// </summary>
    public interface IImageRenderService
    {
        /// <summary>
        /// 将原生图像对象包装为渲染图像句柄
        /// </summary>
        /// <param name="nativeImage">原生图像对象（如 HImage）</param>
        /// <returns>渲染图像句柄</returns>
        IRenderImage WrapImage(object nativeImage);

        /// <summary>
        /// 将渲染图像转换为 WPF 位图
        /// </summary>
        //BitmapSource ConvertToBitmapSource(IRenderImage image);// BitmapSource 是 WPF 的图像类型，而这里是类库项目不能依赖任何UI

        /// <summary>
        /// 查询指定像素灰度/颜色信息
        /// </summary>
        /// <param name="image">渲染图像</param>
        /// <param name="x">列坐标</param>
        /// <param name="y">行坐标</param>
        /// <returns>像素信息字符串，如 gray=128</returns>
        string GetPixelInfo(IRenderImage image, int x, int y);

        /// <summary>
        /// 将原生区域/XLD对象包装为叠加图元
        /// </summary>
        /// <param name="kind">图元类型</param>
        /// <param name="nativeHandle">原生句柄（如 HObject）</param>
        /// <param name="color">颜色</param>
        /// <returns>叠加图元</returns>
        ImageOverlay WrapOverlay(OverlayKind kind, object nativeHandle, string color = "red");

        /// <summary>
        /// 渲染一组图像和叠加图元到指定原生窗口句柄
        /// </summary>
        /// <param name="windowHandle">原生窗口句柄（如 Halcon HWindow）</param>
        /// <param name="image">主图</param>
        /// <param name="overlays">叠加图元集合</param>
        void RenderToWindow(object windowHandle, IRenderImage image, IEnumerable<ImageOverlay> overlays);

        /// <summary>
        /// 让指定窗口自适应完整图像
        /// </summary>
        /// <param name="windowHandle">原生窗口句柄</param>
        /// <param name="image">图像</param>
        void FitImageToWindow(object windowHandle, IRenderImage image);
    }
}
