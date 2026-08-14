using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// ROI 裁剪、 Mask 掩膜生成与 ROI 集合运算 (交/并/差)
    /// </summary>
    public static class RoiOperationTool
    {
        /// <summary>
        /// 将 Region 区域生成为二值 Mask 掩膜图像 (255 表示 Region 内部, 0 表示背景)
        /// </summary>
        public static Result<HObject> RegionToMask(HObject region, int width, int height)
        {
            if (region == null || !region.IsInitialized())
                return Result<HObject>.Fail("输入 Region 无效");
            try
            {
                HObject maskImg;
                HOperatorSet.RegionToBin(region, out maskImg, 255, 0, width, height);
                return Result<HObject>.Ok(maskImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("生成 Mask 掩膜失败", ex, nameof(RoiOperationTool));
                return Result<HObject>.Fail("掩膜生成异常", -1, ex);
            }
        }

        /// <summary>
        /// ROI 集合运算 (1: 交集 Intersection, 2: 并集 Union, 3: 差集 Difference)
        /// </summary>
        public static Result<HObject> CombineRegions(HObject region1, HObject region2, int operationType)
        {
            if (region1 == null || !region1.IsInitialized() || region2 == null || !region2.IsInitialized())
                return Result<HObject>.Fail("集合运算输入 Region 无效");
            try
            {
                HObject resRegion;
                switch (operationType)
                {
                    case 1: // 交集
                        HOperatorSet.Intersection(region1, region2, out resRegion);
                        break;
                    case 2: // 并集
                        HOperatorSet.Union2(region1, region2, out resRegion);
                        break;
                    case 3: // 差集
                        HOperatorSet.Difference(region1, region2, out resRegion);
                        break;
                    default:
                        return Result<HObject>.Fail("未知的 ROI 集合运算类型");
                }
                return Result<HObject>.Ok(resRegion);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ROI 集合运算失败", ex, nameof(RoiOperationTool));
                return Result<HObject>.Fail("ROI 集合运算异常", -1, ex);
            }
        }
    }
}