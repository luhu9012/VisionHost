using System;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// 灰度阈值分割工具
    /// 固定阈值、反阈值、动态自适应阈值，用于提取亮缺陷、暗缺陷、物料轮廓
    /// </summary>
    public static class ImageThresholdTool
    {
        /// <summary>
        /// 固定灰度阈值分割：低于minGray、高于maxGray保留
        /// 输出：满足灰度区间的Region区域
        /// </summary>
        public static Result<HObject> FixedThreshold(HObject grayImage, int minGray, int maxGray)
        {
            if (grayImage == null || !grayImage.IsInitialized())
                return Result<HObject>.Fail("灰度图无效");
            try
            {
                HObject regionOut;
                HOperatorSet.Threshold(grayImage, out regionOut, minGray, maxGray);
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageThresholdTool), "固定阈值分割失败", ex);
                return Result<HObject>.Fail("阈值分割异常", -1, ex);
            }
        }

        /// <summary>
        /// 自适应动态阈值（明暗不均匀工况必备）
        /// 用局部均值做参考，亮于均值offset则检出
        /// </summary>
        /// <param name="maskSize">局部窗口尺寸</param>
        /// <param name="offset">亮度偏移</param>
        public static Result<HObject> AutoThreshold(HObject grayImage, int maskSize = 15, int offset = 5)
        {
            try
            {
                HObject regionOut;
                HOperatorSet.DynThreshold(grayImage, grayImage, out regionOut, offset, "light");
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageThresholdTool), "动态阈值分割失败", ex);
                return Result<HObject>.Fail("动态阈值异常", -1, ex);
            }
        }
        /// <summary>
        /// Otsu 自动阈值分割
        /// </summary>
        public static Result<HObject> OtsuThreshold(HObject grayImage)
        {
            if (grayImage == null || !grayImage.IsInitialized())
                return Result<HObject>.Fail("灰度图无效");
            try
            {
                HObject regionOut;
                // 使用 Halcon 的 binary_threshold 算子实现 Otsu
                HOperatorSet.BinaryThreshold(grayImage, out regionOut, "max_separability", "dark", out _);
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageThresholdTool), "Otsu阈值分割失败", ex);
                return Result<HObject>.Fail("Otsu分割异常", -1, ex);
            }
        }

        /// <summary>
        /// 区域筛选：按面积过滤小噪点，保留指定面积区间轮廓
        /// </summary>
        public static Result<HObject> SelectRegionByArea(HObject inputRegion, double areaMin, double areaMax)
        {
            try
            {
                HObject regionFiltered;
                HOperatorSet.SelectShape(inputRegion, out regionFiltered, "area", "and", areaMin, areaMax);
                return Result<HObject>.Ok(regionFiltered);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageThresholdTool), "区域面积筛选失败", ex);
                return Result<HObject>.Fail("区域筛选异常", -1, ex);
            }
        }
    }
}