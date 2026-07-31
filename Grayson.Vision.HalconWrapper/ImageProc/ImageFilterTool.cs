using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;
using System;
using System.Threading;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// 图像滤波平滑 + 形态学开闭运算，适配划痕、斑点、脏点预处理
    /// 封装常用算子：高斯、均值、中值、开运算、闭运算、膨胀腐蚀
    /// </summary>
    public static class ImageFilterTool
    {
        /// <summary>高斯平滑滤波，消除相机噪点</summary>
        /// <param name="maskSize">卷积核尺寸，推荐3/5/7</param>
        public static Result<HObject> GaussFilter(HObject srcImg, int maskSize = 5)
        {
            if (srcImg == null || !srcImg.IsInitialized())
                return Result<HObject>.Fail("输入图像为空");
            try
            {
                HObject outImg;
                HOperatorSet.GaussFilter(srcImg, out outImg, maskSize);
                return Result<HObject>.Ok(outImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("高斯滤波执行失败", ex, nameof(ImageFilterTool));
                return Result<HObject>.Fail("高斯滤波异常", -1, ex);
            }
        }

        /// <summary>中值滤波，去除椒盐噪点、白点黑点脏污</summary>
        public static Result<HObject> MedianFilter(HObject srcImg, int mask = 3)
        {
            try
            {
                HObject res;
                HOperatorSet.MedianImage(srcImg, out res, mask, mask, "circle");
                return Result<HObject>.Ok(res);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("中值滤波失败", ex, nameof(ImageFilterTool));
                return Result<HObject>.Fail("中值滤波异常", -1, ex);
            }
        }

        /// <summary>腐蚀运算：收缩白色区域，去除细小白点杂讯</summary>
        public static Result<HObject> Erode(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Erode);
        }

        /// <summary>膨胀运算：扩大白色区域，填补细小缝隙</summary>
        public static Result<HObject> Dilate(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Dilate);
        }

        /// <summary>开运算：先腐蚀后膨胀，去除小白点，整体轮廓不变</summary>
        public static Result<HObject> OpenMorph(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Open);
        }

        /// <summary>闭运算：先膨胀后腐蚀，填补小黑洞、缝隙</summary>
        public static Result<HObject> CloseMorph(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Close);
        }

        #region 内部形态学统一入口
        private enum MorphType
        {
            Erode, Dilate, Open, Close
        }

        private static Result<HObject> MorphologyBase(HObject srcImg, int kernel, MorphType type)
        {
            if (srcImg == null || !srcImg.IsInitialized())
                return Result<HObject>.Fail("图像无效");
            try
            {
                HObject kernelRegion, dstImg;
                HOperatorSet.GenCircle(out kernelRegion, kernel, kernel, kernel);
                switch (type)
                {
                    case MorphType.Erode:
                        HOperatorSet.Erosion1(srcImg, kernelRegion, out dstImg, 1);
                        break;
                    case MorphType.Dilate:
                        HOperatorSet.Dilation1(srcImg, kernelRegion, out dstImg, 1);
                        break;
                    case MorphType.Open:
                        HOperatorSet.Opening(srcImg, kernelRegion, out dstImg);
                        break;
                    case MorphType.Close:
                        HOperatorSet.Closing(srcImg, kernelRegion, out dstImg);
                        break;
                    default:
                        return Result<HObject>.Fail("不支持的形态学类型");
                }
                kernelRegion.Dispose();
                return Result<HObject>.Ok(dstImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"形态学运算{type}失败", ex, nameof(ImageFilterTool));
                return Result<HObject>.Fail("形态学处理异常", -1, ex);
            }
        }
        #endregion
    }
}