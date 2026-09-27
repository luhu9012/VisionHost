using System;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Identification
{
    /// <summary>颜色提取结果：ResultRegion=命中的颜色区域，AreaRatio=占搜索域面积的百分比，HitArea=命中的像素数。</summary>
    public class ColorExtractResult
    {
        public bool Success { get; set; }
        public object ResultRegion { get; set; }
        public double AreaRatio { get; set; }
        /// <summary>命中像素数（面积占比的分子，便于判据侧直接用绝对面积）</summary>
        public double HitArea { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 颜色识别工具（HSV 色彩空间）。
    ///
    /// ⚠ 2026-09-26：由 mock 改为真实实现。原实现直接返回
    ///   Success=true + AreaRatio=85.5（恒定假值，连 TODO 都没有）——链路会"跑通并给出结果"，
    ///   但门禁一处都不会红，属于最危险的静默 mock。
    ///
    /// 真实链路（与课程 3色彩空间-颜色识别.hdev 同思路，本仓算子全部已有）：
    ///   decompose3(R,G,B) → trans_from_rgb(...,'hsv') 得 H/S/V
    ///   → 饱和度 + 明度双门限取交集造【有效彩色像素掩膜】
    ///   → reduce_domain 把色相图限制到掩膜上 → threshold 按色相区间取色 → area_center 算占比。
    ///
    /// 为什么先造掩膜再取色相：低饱和（灰/白/黑）像素的 Hue 是纯噪声，直接对整幅 Hue 图
    /// threshold 会把背景噪声一起选进来。课程脚本同样是先 threshold(S) 再 reduce_domain(ImageH, ...)。
    /// </summary>
    public static class ColorTool
    {
        /// <summary>
        /// 按 HSV 区间提取颜色区域。hMin &gt; hMax 时表示色相跨 0 环绕（红色：Hue 350~360 ∪ 0~20）。
        /// </summary>
        public static ColorExtractResult ExtractHsvRegion(object image, object region,
            double hMin, double hMax, double sMin, double sMax, double vMin, double vMax)
        {
            var hImg = image as HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Fail("输入图像为空或未初始化");

            if (sMin > sMax || vMin > vMax)
                return Fail($"饱和度/明度区间上下限颠倒（S:{sMin}~{sMax} V:{vMin}~{vMax}）");

            HObject work = null;      // ROI 裁剪后的工作图；未给 ROI 时即 hImg，不可释放
            HObject r = null, g = null, b = null;
            HObject hue = null, sat = null, val = null;
            HObject satRegion = null, valRegion = null, mask = null, hueReduced = null;
            HObject lowBand = null, highBand = null;
            bool ownWork = false;
            try
            {
                var roi = region as HObject;
                if (roi != null && roi.IsInitialized())
                {
                    HOperatorSet.ReduceDomain(hImg, roi, out work);
                    ownWork = true;
                }
                else
                {
                    work = hImg;
                }

                // 颜色提取必须 3 通道彩色图：灰度图上"色相"无定义，响亮失败而不是给个假结果
                HOperatorSet.CountChannels(work, out HTuple channels);
                if (channels.I != 3)
                    return Fail($"颜色提取需要 3 通道彩色图像，当前为 {channels.I} 通道（灰度图请改用阈值/Blob 类节点）");

                HOperatorSet.Decompose3(work, out r, out g, out b);
                HOperatorSet.TransFromRgb(r, g, b, out hue, out sat, out val, "hsv");

                // 1) 饱和度 + 明度双门限 → 有效彩色像素掩膜
                HOperatorSet.Threshold(sat, out satRegion, sMin, sMax);
                HOperatorSet.Threshold(val, out valRegion, vMin, vMax);
                HOperatorSet.Intersection(satRegion, valRegion, out mask);

                // 2) 色相图限制到掩膜域上，再按色相区间取色
                HOperatorSet.ReduceDomain(hue, mask, out hueReduced);

                HObject hueRegion;
                if (hMin <= hMax)
                {
                    HOperatorSet.Threshold(hueReduced, out hueRegion, hMin, hMax);
                }
                else
                {
                    // 色相跨 0 环绕：红色 = [0, hMax] ∪ [hMin, 255]（HALCON Hue 为 0~255 字节值）
                    HOperatorSet.Threshold(hueReduced, out lowBand, 0, hMax);
                    HOperatorSet.Threshold(hueReduced, out highBand, hMin, 255);
                    HOperatorSet.Union2(lowBand, highBand, out hueRegion);
                }

                // 3) 面积占比 = 命中像素 / 搜索域像素（给了 ROI 就以 ROI 面积为分母）
                HOperatorSet.AreaCenter(hueRegion, out HTuple hitArea, out HTuple hitRow, out HTuple hitCol);
                double hit = 0;
                for (int i = 0; i < hitArea.TupleLength(); i++) hit += hitArea[i].D;

                double totalPx;
                if (ownWork)
                {
                    HOperatorSet.AreaCenter(roi, out HTuple roiArea, out HTuple roiRow, out HTuple roiCol);
                    totalPx = 0;
                    for (int i = 0; i < roiArea.TupleLength(); i++) totalPx += roiArea[i].D;
                }
                else
                {
                    HOperatorSet.GetImageSize(work, out HTuple imgW, out HTuple imgH);
                    totalPx = imgW.D * imgH.D;
                }
                double ratio = totalPx > 0 ? hit / totalPx * 100.0 : 0.0;

                return new ColorExtractResult
                {
                    Success = true,
                    ResultRegion = hueRegion,
                    AreaRatio = ratio,
                    HitArea = hit,
                    Message = hit > 0
                        ? $"命中 {hit:0} px（占比 {ratio:F2}%）"
                        : "该色相区间内无有效像素（检查饱和度/明度门限与色相范围）"
                };
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(ColorTool), "HSV 颜色提取失败", ex);
                return Fail("颜色提取异常: " + ex.Message);
            }
            finally
            {
                // 临时图/区域逐级释放（返回的 hueRegion 是独立对象，HALCON 图标对象引用计数，不受影响）
                r?.Dispose(); g?.Dispose(); b?.Dispose();
                hue?.Dispose(); sat?.Dispose(); val?.Dispose();
                satRegion?.Dispose(); valRegion?.Dispose(); mask?.Dispose(); hueReduced?.Dispose();
                lowBand?.Dispose(); highBand?.Dispose();
                if (ownWork) work?.Dispose();
            }
        }

        private static ColorExtractResult Fail(string msg)
            => new ColorExtractResult { Success = false, Message = msg };
    }
}
