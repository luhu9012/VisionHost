using System;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Core;
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
        /// 读取本地图片文件返回HObject
        /// </summary>
        public static Result<HObject> ReadImageFile(string filePath)
        {
            try
            {
                HObject img;
                HOperatorSet.ReadImage(out img, filePath);
                return Result<HObject>.Ok(img);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageBasicTool), $"读取图片失败:{filePath}", ex);
                return Result<HObject>.Fail("图片读取异常", -1, ex);
            }
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