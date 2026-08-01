//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 基于 Halcon 的图像渲染服务实现。
//===================================================================================

using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using Grayson.Vision.Contracts.Imaging;
using HalconDotNet;
using System.IO;

namespace Grayson.Vision.HalconWrapper.Wpf.Imaging
{
    /// <summary>
    /// Halcon 图像渲染服务
    /// </summary>
    public class HalconImageRenderService : IImageRenderService
    {
        /// <summary>
        /// 将原生图像对象包装为渲染图像句柄
        /// </summary>
        public IRenderImage WrapImage(object nativeImage)
        {
            return new HalconRenderImage(nativeImage as HImage);
        }

        /// <summary>
        /// 将 HImage 转换为 WPF BitmapSource
        /// </summary>
        public BitmapSource ConvertToBitmapSource(IRenderImage image)
        {
            var halconImage = (image as HalconRenderImage)?.HImage;
            if (halconImage == null || !halconImage.IsInitialized())
                return null;

            HOperatorSet.DumpWindowImage(out HObject dumpObj, new HWindow { });
            // 简化：将 HImage 通过 Halcon 的 HOperatorSet 转换并使用 WriteIcon / 内存流加载
            // 实际实现可能需根据项目已有 Halcon 转换工具补充
            return null;
        }

        /// <summary>
        /// 生成 WPF 缩略图
        /// </summary>
        public BitmapSource CreateThumbnail(IRenderImage image)
        {
            var halconImage = (image as HalconRenderImage)?.HImage;
            if (halconImage == null || !halconImage.IsInitialized())
                return null;

            string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bmp");
            try
            {
                halconImage.WriteImage("bmp", 0, tempFile);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(tempFile);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        /// <summary>
        /// 查询指定像素信息
        /// </summary>
        public string GetPixelInfo(IRenderImage image, int x, int y)
        {
            var halconImage = (image as HalconRenderImage)?.HImage;
            if (halconImage == null || !halconImage.IsInitialized())
                return string.Empty;

            try
            {
                halconImage.GetImageSize(out int w, out int h);
                if (x < 0 || x >= w || y < 0 || y >= h)
                    return string.Empty;

                HTuple gray = halconImage.GetGrayval(y, x);
                return $"Gray:{gray}";
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 包装原生区域/XLD 为叠加图元
        /// </summary>
        public ImageOverlay WrapOverlay(OverlayKind kind, object nativeHandle, string color = "red")
        {
            return new ImageOverlay
            {
                Kind = kind,
                Color = color,
                NativeHandle = nativeHandle
            };
        }

        /// <summary>
        /// 渲染图像与叠加图元到 Halcon HWindow
        /// </summary>
        public void RenderToWindow(object windowHandle, IRenderImage image, IEnumerable<ImageOverlay> overlays)
        {
            if (!(windowHandle is HWindow hWindow)) return;

            var halconImage = (image as HalconRenderImage)?.HImage;
            hWindow.ClearWindow();

            if (halconImage != null && halconImage.IsInitialized())
            {
                hWindow.DispObj(halconImage);
            }

            if (overlays == null) return;
            foreach (var overlay in overlays)
            {
                if (overlay == null) continue;

                hWindow.SetColor(overlay.Color ?? "green");
                switch (overlay.Kind)
                {
                    case OverlayKind.Text:
                        hWindow.SetTposition((int)overlay.Row, (int)overlay.Column);
                        hWindow.WriteString(overlay.Text);
                        break;
                    case OverlayKind.Region:
                    case OverlayKind.Xld:
                        if (overlay.NativeHandle is HObject hObj && hObj.IsInitialized())
                            hWindow.DispObj(hObj);
                        break;
                }
            }
        }

        /// <summary>
        /// 让窗口自适应图像
        /// </summary>
        public void FitImageToWindow(object windowHandle, IRenderImage image)
        {
            if (windowHandle is HWindow hWindow && (image as HalconRenderImage)?.HImage is HImage hImage && hImage.IsInitialized())
            {
                HOperatorSet.SetPart(hWindow, 0, 0, image.Height - 1, image.Width - 1);
            }
        }
    }
}
