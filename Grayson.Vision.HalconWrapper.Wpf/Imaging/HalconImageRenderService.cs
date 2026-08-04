//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 基于 Halcon 的图像渲染服务实现。
//===================================================================================

using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Logging;
using HalconDotNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace Grayson.Vision.HalconWrapper.Wpf.Imaging
{
    /// <summary>
    /// Halcon 图像渲染服务
    /// </summary>
    public class HalconImageRenderService : IImageRenderService
    {
        public IRenderImage WrapImage(object nativeImage)
        {
            if (nativeImage == null)
            {
                LogBus.Warn("Halcon", "WrapImage 接收到的数据为 null");
                return null;
            }
            LogBus.Debug("Halcon", $"正在包装图像，数据类型: {nativeImage.GetType().Name}");
            HImage hImage = null;

            try
            {
                // 1. 如果已经是 HImage
                if (nativeImage is HImage img)
                {
                    hImage = img;
                }
                // 2. 如果是文件路径 string
                else if (nativeImage is string filePath && File.Exists(filePath))
                {
                    LogBus.Debug("Halcon", $"通过路径加载 HImage: {filePath}");
                    hImage = new HImage(filePath);
                }
                // 3. 如果是 Bitmap
                else if (nativeImage is System.Drawing.Bitmap bitmap)
                {
                    LogBus.Debug("Halcon", "通过 Bitmap 锁内存转换 HImage...");
                    hImage = ConvertBitmapToHImage(bitmap);
                }

                if (hImage == null || !hImage.IsInitialized())
                {
                    LogBus.Error("Halcon", $"图像包装失败，未能成功构建 Halcon HImage 实例 (数据类型: {nativeImage.GetType().Name})");
                    return null;
                }

                hImage.GetImageSize(out int w, out int h);
                LogBus.Info("Halcon", $"HImage 包装成功: 尺寸 [{w} x {h}]");

                return new HalconRenderImage(hImage);
            }
            catch (Exception ex)
            {
                LogBus.Error("Halcon", $"WrapImage 抛出异常: {ex.Message}", ex);
                return null;
            }
        }

        private HImage ConvertBitmapToHImage(System.Drawing.Bitmap bitmap)
        {
            if (bitmap == null) return null;

            int width = bitmap.Width;
            int height = bitmap.Height;
            System.Drawing.Imaging.BitmapData bmpData = null;

            try
            {
                var rect = new System.Drawing.Rectangle(0, 0, width, height);
                bmpData = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, bitmap.PixelFormat);

                HImage hImage = new HImage();

                if (bitmap.PixelFormat == System.Drawing.Imaging.PixelFormat.Format8bppIndexed)
                {
                    hImage.GenImage1("byte", width, height, bmpData.Scan0);
                }
                else if (bitmap.PixelFormat == System.Drawing.Imaging.PixelFormat.Format24bppRgb)
                {
                    hImage.GenImageInterleaved(bmpData.Scan0, "bgr", width, height, -1, "byte", width, height, 0, 0, -1, 0);
                }
                else
                {
                    using (var rgbBitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(rgbBitmap))
                        {
                            g.DrawImage(bitmap, 0, 0, width, height);
                        }
                        var rgbRect = new System.Drawing.Rectangle(0, 0, width, height);
                        var rgbData = rgbBitmap.LockBits(rgbRect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                        try
                        {
                            hImage.GenImageInterleaved(rgbData.Scan0, "bgr", width, height, -1, "byte", width, height, 0, 0, -1, 0);
                        }
                        finally
                        {
                            rgbBitmap.UnlockBits(rgbData);
                        }
                    }
                }

                return hImage;
            }
            catch (Exception ex)
            {
                LogBus.Error("Halcon", $"Bitmap 转 HImage 异常: {ex.Message}", ex);
                return null;
            }
            finally
            {
                if (bmpData != null)
                {
                    bitmap.UnlockBits(bmpData);
                }
            }
        }

        public BitmapSource ConvertToBitmapSource(IRenderImage image)
        {
            var halconImage = (image as HalconRenderImage)?.HImage;
            if (halconImage == null || !halconImage.IsInitialized())
                return null;

            HOperatorSet.DumpWindowImage(out HObject dumpObj, new HWindow { });
            return null;
        }

        public BitmapSource CreateThumbnail(IRenderImage image)
        {
            var halconImage = (image as HalconRenderImage)?.HImage;
            if (halconImage == null || !halconImage.IsInitialized())
                return null;

            try
            {
                // 🌟 性能优化关键：直接在 Halcon 内存层缩放到小图（例如 100 像素宽），大幅降低转换与渲染开销
                halconImage.GetImageSize(out int w, out int h);
                if (w <= 0 || h <= 0) return null;

                double scale = 100.0 / Math.Max(w, h);
                using (HImage zoomImg = halconImage.ZoomImageFactor(scale, scale, "constant"))
                {
                    // 获取字节指针与尺寸
                    zoomImg.GetImageSize(out int zoomW, out int zoomH);
                    IntPtr pointer = zoomImg.GetImagePointer1(out string type, out int imgWidth, out int imgHeight);

                    // 构造 8位 灰度 BitmapSource (无磁盘 IO)
                    var pixelFormat = System.Windows.Media.PixelFormats.Gray8;
                    var bitmap = BitmapSource.Create(
                        zoomW, zoomH, 96, 96, pixelFormat,
                        BitmapPalettes.Gray256, pointer, zoomW * zoomH, zoomW);

                    bitmap.Freeze(); // 冻结对象，支持跨线程安全传递
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn("Halcon", $"纯内存创建缩略图失败: {ex.Message}");
                return null;
            }
        }
        public string GetPixelInfo(IRenderImage image, int x, int y)
        {
            var halconImage = (image as HalconRenderImage)?.HImage;
            if (halconImage == null || !halconImage.IsInitialized()) return string.Empty;

            try
            {
                halconImage.GetImageSize(out int w, out int h);
                if (x < 0 || x >= w || y < 0 || y >= h) return string.Empty;

                int channels = halconImage.CountChannels();
                if (channels == 1)
                {
                    HTuple gray = halconImage.GetGrayval(y, x);
                    return $"Gray: {gray.I}";
                }
                else if (channels == 3)
                {
                    // 彩色图像：分别获取 R, G, B
                    using (HImage r = halconImage.Decompose3(out HImage g, out HImage b))
                    {
                        int rVal = r.GetGrayval(y, x);
                        int gVal = g.GetGrayval(y, x);
                        int bVal = b.GetGrayval(y, x);
                        g.Dispose();
                        b.Dispose();
                        return $"R:{rVal}, G:{gVal}, B:{bVal}";
                    }
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

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
            if (!(windowHandle is HWindow hWindow))
            {
                LogBus.Warn("Halcon", "RenderToWindow 失败: windowHandle 不是有效的 HWindow 实例");
                return;
            }

            var halconImage = (image as HalconRenderImage)?.HImage;
            hWindow.ClearWindow();

            if (halconImage != null && halconImage.IsInitialized())
            {
                // 🌟 执行绘制
                hWindow.DispObj(halconImage);
                LogBus.Debug("Halcon", $"图像已成功 DispObj 到 HWindow (尺寸: {image.Width}x{image.Height})");
            }
            else
            {
                LogBus.Warn("Halcon", "DispObj 跳过: HImage 为 null 或未初始化");
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
        /// 让窗口视口适应图像全图尺寸
        /// </summary>
        public void FitImageToWindow(object windowHandle, IRenderImage image)
        {
            if (windowHandle is HWindow hWindow && (image as HalconRenderImage)?.HImage is HImage hImage && hImage.IsInitialized())
            {
                try
                {
                    // 🌟 关键修复：显式设定 Halcon Window 视口为全图边界
                    HOperatorSet.SetPart(hWindow, 0, 0, image.Height - 1, image.Width - 1);
                    LogBus.Debug("Halcon", $"[FitImageToWindow] 视口区域重置为: [0, 0, {image.Height - 1}, {image.Width - 1}]");
                }
                catch (Exception ex)
                {
                    LogBus.Warn("Halcon", $"SetPart 设置视口失败: {ex.Message}");
                }
            }
        }
    }
}