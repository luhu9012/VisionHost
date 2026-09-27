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
        /// 彩色图自适应转灰度（2026-09-26）：threshold/dyn_threshold 都只吃单通道 byte 图，
        /// 直接喂 RGB 会抛 HALCON 异常。3 通道走 Rgb1ToGray，>3 通道取第 1 通道。
        /// ownGray=true 时返回值是新对象，调用方负责 Dispose；false 时原样返回、不可释放。
        /// </summary>
        public static HObject ToGrayIfNeeded(HObject image, out bool ownGray)
        {
            ownGray = false;
            if (image == null || !image.IsInitialized()) return image;
            HOperatorSet.CountChannels(image, out HTuple ch);
            int c = ch.I;
            if (c <= 1) return image;
            if (c == 3)
            {
                HOperatorSet.Rgb1ToGray(image, out HObject gray);
                ownGray = true;
                return gray;
            }
            HOperatorSet.AccessChannel(image, out HObject first, 1);
            ownGray = true;
            return first;
        }

        /// <summary>
        /// 固定灰度阈值分割：低于minGray、高于maxGray保留
        /// 输出：满足灰度区间的Region区域
        /// </summary>
        public static Result<HObject> FixedThreshold(HObject grayImage, int minGray, int maxGray)
        {
            if (grayImage == null || !grayImage.IsInitialized())
                return Result<HObject>.Fail("灰度图无效");
            HObject gray = null;
            bool own = false;
            try
            {
                gray = ToGrayIfNeeded(grayImage, out own);
                HObject regionOut;
                HOperatorSet.Threshold(gray, out regionOut, minGray, maxGray);
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageThresholdTool), "固定阈值分割失败", ex);
                return Result<HObject>.Fail("阈值分割异常", -1, ex);
            }
            finally { if (own) gray?.Dispose(); }
        }

        /// <summary>
        /// 自适应动态阈值（明暗不均匀工况必备）
        /// 用局部均值做参考，亮于均值offset则检出
        /// </summary>
        /// <param name="maskSize">局部窗口尺寸</param>
        /// <param name="offset">亮度偏移</param>
        /// <param name="polarity">极性："light"=检出比局部均值亮的缺陷（亮缺陷）；
        /// "dark"=检出比局部均值暗的缺陷（暗划痕/缺料）。2026-09-26 起可配，原先写死 light。</param>
        public static Result<HObject> AutoThreshold(HObject grayImage, int maskSize = 15, int offset = 5, string polarity = "light")
        {
            if (grayImage == null || !grayImage.IsInitialized())
                return Result<HObject>.Fail("灰度图无效");
            HObject gray = null;
            bool own = false;
            try
            {
                // ⚠ 2026-09-26 修复（静默失效）：dyn_threshold(OrigImage, ThresholdImage, ...) 的语义是
                //   「|原图 − 参考图| ≥ Offset 的像素」。原实现把同一张 hImg 同时当原图与参考图传入，
                //   差恒为 0 ⇒ Offset > 0 时永远返回【空区域】，且 Success=true 一路绿到底，门禁不会红。
                //   参考图必须【另造】——按官方/课程做法先用 mean_image 造局部均值图当门限图。
                //   同时把长期"声明了却从未使用"的 maskSize 真正接上（均值窗口，HALCON 要求奇数、≥1）。
                int win = maskSize < 1 ? 1 : (maskSize % 2 == 0 ? maskSize + 1 : maskSize);
                gray = ToGrayIfNeeded(grayImage, out own);
                HObject refImage;
                HOperatorSet.MeanImage(gray, out refImage, win, win);

                // 极性防呆：只认 light/dark，其它一律回落 light（响亮日志优于静默错向）
                string pol = string.Equals(polarity, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
                if (pol != polarity)
                    LogBus.Info(nameof(ImageThresholdTool), $"动态阈值极性参数非法「{polarity}」，回落 light");

                HObject regionOut;
                HOperatorSet.DynThreshold(gray, refImage, out regionOut, offset, pol);
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageThresholdTool), "动态阈值分割失败", ex);
                return Result<HObject>.Fail("动态阈值异常", -1, ex);
            }
            finally { if (own) gray?.Dispose(); }
        }
        /// <summary>
        /// Otsu 自动阈值分割
        /// </summary>
        public static Result<HObject> OtsuThreshold(HObject grayImage)
        {
            if (grayImage == null || !grayImage.IsInitialized())
                return Result<HObject>.Fail("灰度图无效");
            HObject gray = null;
            bool own = false;
            try
            {
                gray = ToGrayIfNeeded(grayImage, out own);
                HObject regionOut;
                // 使用 Halcon 的 binary_threshold 算子实现 Otsu
                HOperatorSet.BinaryThreshold(gray, out regionOut, "max_separability", "dark", out _);
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ImageThresholdTool), "Otsu阈值分割失败", ex);
                return Result<HObject>.Fail("Otsu分割异常", -1, ex);
            }
            finally { if (own) gray?.Dispose(); }
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
