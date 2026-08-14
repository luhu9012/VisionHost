using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    public static class ImagePreprocessTool
    {
        #region 1. 图像滤波
        public static Result<object> ApplyFilter(object nativeImage, int filterMethod, int kernelSize)
        {
            var hImg = nativeImage as HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Result<object>.Fail("输入的图像句柄无效或未初始化");

            Result<HObject> res;
            switch (filterMethod)
            {
                case 0: // Gauss
                    res = ImageFilterTool.GaussFilter(hImg, kernelSize);
                    break;
                case 1: // Median
                    res = ImageFilterTool.MedianFilter(hImg, kernelSize);
                    break;
                case 2: // Erode
                    res = ImageFilterTool.Erode(hImg, kernelSize);
                    break;
                case 3: // Dilate
                    res = ImageFilterTool.Dilate(hImg, kernelSize);
                    break;
                case 4: // Open
                    res = ImageFilterTool.OpenMorph(hImg, kernelSize);
                    break;
                case 5: // Close
                    res = ImageFilterTool.CloseMorph(hImg, kernelSize);
                    break;
                default:
                    res = ImageFilterTool.GaussFilter(hImg, kernelSize);
                    break;
            }

            if (!res.Success) return Result<object>.Fail(res.Message);
            return Result<object>.Ok(res.Data);
        }
        #endregion

        #region 2. 阈值分割
        public static Result<object> ApplyThreshold(object nativeImage, int method, int minGray, int maxGray, int maskSize, int offset)
        {
            var hImg = nativeImage as HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Result<object>.Fail("输入的图像句柄无效或未初始化");

            Result<HObject> res;
            switch (method)
            {
                case 0: // Fixed
                    res = ImageThresholdTool.FixedThreshold(hImg, minGray, maxGray);
                    break;
                case 1: // Dynamic
                    res = ImageThresholdTool.AutoThreshold(hImg, maskSize, offset);
                    break;
                case 2: // Otsu
                    res = OtsuThresholdInternal(hImg);
                    break;
                default:
                    res = ImageThresholdTool.FixedThreshold(hImg, minGray, maxGray);
                    break;
            }

            if (!res.Success) return Result<object>.Fail(res.Message);
            return Result<object>.Ok(res.Data);
        }

        private static Result<HObject> OtsuThresholdInternal(HObject hImg)
        {
            try
            {
                HObject regionOut;
                HOperatorSet.BinaryThreshold(hImg, out regionOut, "max_separability", "dark", out _);
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                return Result<HObject>.Fail("Otsu阈值分割异常: " + ex.Message);
            }
        }
        #endregion

        #region 3. ROI 运算
        public static Result<object> ApplyRoiCrop(object nativeImage, double r1, double c1, double r2, double c2)
        {
            var hImg = nativeImage as HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Result<object>.Fail("输入图像无效");

            var res = ImageBasicTool.CropImage(hImg, r1, c1, r2, c2);
            if (!res.Success) return Result<object>.Fail(res.Message);
            return Result<object>.Ok(res.Data);
        }

        public static Result<object> ApplyCombineRegions(object region1, object region2, int opType)
        {
            var r1 = region1 as HObject;
            var r2 = region2 as HObject;
            if (r1 == null || !r1.IsInitialized()   || r2 == null || !r2.IsInitialized())
                return Result<object>.Fail("输入的 Region 句柄无效");

            try
            {
                HObject resRegion;
                switch (opType)
                {
                    case 1: // Intersection
                        HOperatorSet.Intersection(r1, r2, out resRegion);
                        break;
                    case 2: // Union
                        HOperatorSet.Union2(r1, r2, out resRegion);
                        break;
                    case 3: // Difference
                        HOperatorSet.Difference(r1, r2, out resRegion);
                        break;
                    default:
                        return Result<object>.Fail("未知的 ROI 集合运算类型");
                }
                return Result<object>.Ok(resRegion);
            }
            catch (Exception ex)
            {
                return Result<object>.Fail("ROI 集合运算异常: " + ex.Message);
            }
        }
        #endregion

        #region 4. 图像仿射变换
        public static Result<object> ApplyAffineRotate(object nativeImage, double centerRow, double centerCol, double angleDegree)
        {
            var hImg = nativeImage as HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Result<object>.Fail("输入图像无效");

            try
            {
                HTuple homMat2D;
                HOperatorSet.HomMat2dIdentity(out homMat2D);
                double angleRad = angleDegree * Math.PI / 180.0;
                HOperatorSet.HomMat2dRotate(homMat2D, angleRad, centerRow, centerCol, out homMat2D);

                HObject dstImg;
                HOperatorSet.AffineTransImage(hImg, out dstImg, homMat2D, "constant", "false");
                return Result<object>.Ok(dstImg);
            }
            catch (Exception ex)
            {
                return Result<object>.Fail("图像旋转异常: " + ex.Message);
            }
        }
        #endregion
    }
}