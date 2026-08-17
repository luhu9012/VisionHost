using System;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// 图像与 ROI 的仿射变换 (旋转/平移/矫正)
    /// </summary>
    public static class AffineImageTool
    {
        /// <summary>
        /// 基于中心点坐标和旋转角度旋转图像
        /// </summary>
        /// <param name="srcImg">输入图像</param>
        /// <param name="centerRow">旋转中心行 Y</param>
        /// <param name="centerCol">旋转中心列 X</param>
        /// <param name="angleDegree">旋转角度 (角度制)</param>
        public static Result<HObject> RotateImage(HObject srcImg, double centerRow, double centerCol, double angleDegree)
        {
            if (srcImg == null || !srcImg.IsInitialized())
                return Result<HObject>.Fail("输入图像无效");
            try
            {
                HTuple homMat2D;
                HOperatorSet.HomMat2dIdentity(out homMat2D);
                // 角度转换为弧度
                double angleRad = angleDegree * Math.PI / 180.0;
                HOperatorSet.HomMat2dRotate(homMat2D, angleRad, centerRow, centerCol, out homMat2D);

                HObject dstImg;
                HOperatorSet.AffineTransImage(srcImg, out dstImg, homMat2D, "constant", "false");
                return Result<HObject>.Ok(dstImg);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(AffineImageTool), "图像旋转失败", ex);
                return Result<HObject>.Fail("图像旋转异常", -1, ex);
            }
        }
    }
}