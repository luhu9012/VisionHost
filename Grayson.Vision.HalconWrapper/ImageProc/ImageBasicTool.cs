using System;
using System.Runtime.InteropServices;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// 图像基础操作：文件读写、ROI裁剪、灰度/彩色互转、尺寸缩放
    /// 所有对外返回Result封装，托管HObject资源
    /// </summary>
    public static class ImageBasicTool
    {
        /// <summary>
        /// 读取本地图片文件返回 HObject（运行时类型为 HImage）。
        /// 使用 new HImage(path) 而非 HOperatorSet.ReadImage(out HObject)，
        /// 确保返回对象同时满足下游 `as HObject`（节点管线）与 `is HImage`（渲染服务）。
        /// </summary>
        public static Result<HObject> ReadImageFile(string filePath)
        {
            try
            {
                var img = new HImage(filePath);
                return Result<HObject>.Ok(img);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageBasicTool), $"读取图片失败:{filePath}", ex);
                return Result<HObject>.Fail("图片读取异常", -1, ex);
            }
        }

        /// <summary>
        /// 读取本地图片文件返回弱类型 object（运行时 HImage）。
        /// 供 Nodes 层（零 halcondotnet 引用）调用——返回 Result&lt;object&gt;
        /// 避免 HObject 类型穿透到 Nodes 导致 CS0012。
        /// </summary>
        public static Result<object> LoadImage(string filePath)
        {
            var res = ReadImageFile(filePath);
            if (res.Success)
                return Result<object>.Ok(res.Data);
            return Result<object>.Fail(res.Message, res.ErrorCode, res.Exception);
        }

        /// <summary>
        /// HObject保存到本地文件（png/jpg/tif）
        /// </summary>
        public static Result SaveImageToFile(HObject image, string filePath, string format = "png")
        {
            if (image == null || !image.IsInitialized())
                return Result.Fail("无效图像");
            try
            {
                HOperatorSet.WriteImage(image, format, 0, filePath);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageBasicTool), $"保存图片失败:{filePath}", ex);
                return Result.Fail("图像保存失败", -1, ex);
            }
        }

        /// <summary>
        /// 彩色图转灰度图
        /// </summary>
        public static Result<HObject> RgbToGray(HObject colorImage)
        {
            if (colorImage == null || !colorImage.IsInitialized())
                return Result<HObject>.Fail("输入图像为空");
            try
            {
                HObject grayImg;
                HOperatorSet.Rgb1ToGray(colorImage, out grayImg);
                return Result<HObject>.Ok(grayImg);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageBasicTool), "彩色转灰度失败", ex);
                return Result<HObject>.Fail("灰度转换异常", -1, ex);
            }
        }

        /// <summary>
        /// 将相机采集的 FrameEventArgs（原始字节缓冲）转换为 Halcon HImage。
        /// 支持 Mono8（单通道）和 BGR24/RGB24（三通道交织）格式。
        /// GenImage1 / GenImageInterleaved 会拷贝像素数据，返回的 HImage 独立于原始 Buffer。
        /// </summary>
        /// <param name="frame">相机帧数据（Buffer + Width + Height + PixelFormat）</param>
        /// <returns>Result&lt;object&gt;（运行时 HImage），供 Nodes 层弱类型传递</returns>
        public static Result<object> FrameToHImage(FrameEventArgs frame)
        {
            if (frame == null || frame.Buffer == null || frame.Buffer.Length == 0)
                return Result<object>.Fail("相机帧数据为空");
            if (frame.Width <= 0 || frame.Height <= 0)
                return Result<object>.Fail("图像尺寸无效");

            string fmt = (frame.PixelFormat ?? "Mono8").Trim().ToUpperInvariant();
            GCHandle handle = GCHandle.Alloc(frame.Buffer, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                HImage img;

                if (fmt.Contains("MONO") || fmt == "GRAY8" || fmt == "GRAY")
                {
                    // 单通道灰度：GenImage1 拷贝像素数据
                    img = new HImage();
                    img.GenImage1("byte", frame.Width, frame.Height, ptr);
                }
                else
                {
                    // 三通道交织（BGR24 / RGB24）
                    string colorFormat = fmt.Contains("BGR") ? "bgr" : "rgb";
                    img = new HImage();
                    img.GenImageInterleaved(
                        ptr, colorFormat,
                        frame.Width, frame.Height,
                        -1, "byte",
                        frame.Width, frame.Height,
                        -1, 0, 0, -1);
                }

                return Result<object>.Ok(img);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageBasicTool), "FrameToHImage 转换失败: " + fmt, ex);
                return Result<object>.Fail("图像转换异常: " + ex.Message, -1, ex);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// 通道提取：从多通道彩色图像中抽取指定色彩空间的单通道灰度图。
        /// RGB 空间直接 Decompose3 取通道；HSV 空间先 Decompose3 再 RgbToHsv 转换后取通道。
        /// </summary>
        /// <param name="nativeImage">输入图像（运行时 HObject / HImage）</param>
        /// <param name="colorSpace">色彩空间：0=RGB, 1=HSV</param>
        /// <param name="channelIndex">通道索引：0/1/2（RGB→R/G/B，HSV→H/S/V）</param>
        /// <returns>单通道灰度图像（运行时 HObject）</returns>
        public static Result<object> ExtractChannel(object nativeImage, int colorSpace, int channelIndex)
        {
            var hImg = nativeImage as HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Result<object>.Fail("输入的图像句柄无效或未初始化");

            HObject ch1 = null, ch2 = null, ch3 = null;
            HObject h = null, s = null, v = null;
            HObject result = null;
            try
            {
                // 分解为三通道（对 RGB 图得到 R/G/B）
                HOperatorSet.Decompose3(hImg, out ch1, out ch2, out ch3);

                if (colorSpace == 1) // HSV
                {
                    HOperatorSet.TransFromRgb(ch1, ch2, ch3, out h, out s, out v, "hsv");
                    // RGB 中间通道不再需要，释放
                    ch1?.Dispose(); ch2?.Dispose(); ch3?.Dispose();
                    ch1 = h; ch2 = s; ch3 = v;
                    h = s = v = null;
                }

                // 选取目标通道（其余释放）
                switch (channelIndex)
                {
                    case 0: result = ch1; ch2?.Dispose(); ch3?.Dispose(); break;
                    case 1: result = ch2; ch1?.Dispose(); ch3?.Dispose(); break;
                    case 2: result = ch3; ch1?.Dispose(); ch2?.Dispose(); break;
                    default: result = ch1; ch2?.Dispose(); ch3?.Dispose(); break;
                }
                ch1 = ch2 = ch3 = null;

                return Result<object>.Ok(result);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageBasicTool), "通道提取失败", ex);
                // 异常路径释放所有临时句柄
                result?.Dispose();
                ch1?.Dispose(); ch2?.Dispose(); ch3?.Dispose();
                h?.Dispose(); s?.Dispose(); v?.Dispose();
                return Result<object>.Fail("通道提取异常", -1, ex);
            }
        }

        /// <summary>
        /// ROI矩形裁剪图像
        /// </summary>
        /// <param name="row1">起始行Y</param>
        /// <param name="col1">起始列X</param>
        /// <param name="row2">结束行Y</param>
        /// <param name="col2">结束列X</param>
        public static Result<HObject> CropImage(HObject srcImage, double row1, double col1, double row2, double col2)
        {
            if (srcImage == null || !srcImage.IsInitialized())
                return Result<HObject>.Fail("原图无效");
            try
            {
                HObject roiRect, cropImg;
                using (var guard = new HalconMemoryGuard())
                {
                    HOperatorSet.GenRectangle1(out roiRect, row1, col1, row2, col2);
                    guard.Register(roiRect);
                    HOperatorSet.ReduceDomain(srcImage, roiRect, out cropImg);
                }
                return Result<HObject>.Ok(cropImg);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageBasicTool), "图像ROI裁剪失败", ex);
                return Result<HObject>.Fail("裁剪异常", -1, ex);
            }
        }
    }
}