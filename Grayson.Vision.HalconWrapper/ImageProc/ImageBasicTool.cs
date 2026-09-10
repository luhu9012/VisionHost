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
                    // 三通道交织（BGR24 / RGB24）：每像素 3 字节
                    long expected = (long)frame.Width * frame.Height * 3;
                    if (frame.Buffer.Length < expected)
                        return Result<object>.Fail($"帧缓冲长度不足：{frame.Buffer.Length} < {expected}（{frame.Width}x{frame.Height} BGR24/RGB24）");

                    string colorFormat = fmt.Contains("BGR") ? "bgr" : "rgb";
                    img = new HImage();
                    // 注意：halcondotnet 此重载为 12 参数扩展版，末四位含义为
                    // startRow / startColumn / bitsPerChannel / bitShift，
                    // 其中 startRow/startColumn 必须 >= 0，bitsPerChannel=-1 表示全部位使用，bitShift=0。
                    img.GenImageInterleaved(
                        ptr, colorFormat,
                        frame.Width, frame.Height,
                        -1, "byte",
                        frame.Width, frame.Height,
                        0, 0,
                        -1, 0);
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
        /// 获取图像基本信息（宽×高×通道数），供节点日志/诊断输出。
        /// 仅返回字符串，HObject 类型不穿透到 Nodes 层（与 LoadImage 同模式）。
        /// 典型用途：匹配节点 0 分排查时，一眼看出输入是否多通道彩色（匹配算子要求单通道）。
        /// </summary>
        public static Result<string> GetImageInfo(object nativeImage)
        {
            var hImg = nativeImage as HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Result<string>.Fail("图像句柄无效");
            try
            {
                HOperatorSet.CountChannels(hImg, out HTuple channels);
                HOperatorSet.GetImageSize(hImg, out HTuple width, out HTuple height);
                string channelDesc = channels.I > 1 ? $"{channels.I} 通道彩色" : "单通道灰度";
                return Result<string>.Ok($"{width.I} x {height.I} px, {channelDesc}");
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageBasicTool), "获取图像信息失败", ex);
                return Result<string>.Fail("图像信息获取异常", -1, ex);
            }
        }

        /// <summary>
        /// 取图像宽高（按 HALCON 惯例：width=列数 Column，height=行数 Row）。
        /// 与 GetImageInfo 的区别：本方法返回数值，供上层计算视野中心、判断目标是否贴近视野边缘；
        /// HObject 类型不外泄（与 GetImageInfo 同模式）。
        /// </summary>
        public static bool TryGetImageSize(object nativeImage, out int width, out int height)
        {
            width = 0;
            height = 0;
            var hImg = nativeImage as HObject;
            if (hImg == null || !hImg.IsInitialized())
                return false;
            try
            {
                HOperatorSet.GetImageSize(hImg, out HTuple w, out HTuple h);
                width = w.I;
                height = h.I;
                return width > 0 && height > 0;
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageBasicTool), "获取图像尺寸失败", ex);
                return false;
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