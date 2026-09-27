using System;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// Blob 连通域分析工具（2026-09-26，梯队 B）
    /// 连通域拆分 → 形状特征筛选 → 计数，用于计数、有无、异物、划痕判定。
    /// 输入一律是 Region（上游阈值分割的产物），不是图像。
    /// ★ 供 Nodes 层调用的 API 一律 object 进出（Nodes 不引用 halcondotnet，暴露 HObject 会 CS0012，
    ///   参照 ImagePreprocessTool 的 Result&lt;object&gt; 约定）；HObject 细节藏在私有核心里。
    /// </summary>
    public static class BlobTool
    {
        /// <summary>
        /// 连通域拆分：把像素相邻的区域拆成一个个独立连通域
        /// 输出：N 个连通域组成的 Region 对象（后续按"对象"逐个筛选/统计）
        /// </summary>
        public static Result<object> Connection(object nativeRegion)
        {
            var region = nativeRegion as HObject;
            if (region == null || !region.IsInitialized())
                return Result<object>.Fail("输入区域无效");
            try
            {
                HObject connected;
                HOperatorSet.Connection(region, out connected);
                return Result<object>.Ok(connected);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(BlobTool), "连通域拆分失败", ex);
                return Result<object>.Fail("连通域拆分异常", -1, ex);
            }
        }

        /// <summary>
        /// 按形状特征筛选连通域：最常用 area（面积），也可 circularity / rectangularity 等
        /// op 固定 "and"（区间内保留）；min>max 时返回 Fail，不给静默清空
        /// </summary>
        public static Result<object> SelectByFeature(object nativeRegions, string feature, double min, double max)
        {
            var regions = nativeRegions as HObject;
            if (regions == null || !regions.IsInitialized())
                return Result<object>.Fail("输入连通域无效");
            if (string.IsNullOrWhiteSpace(feature))
                return Result<object>.Fail("筛选特征名为空");
            if (min > max)
                return Result<object>.Fail($"筛选区间非法（min {min} > max {max}），请检查参数");
            try
            {
                HObject filtered;
                HOperatorSet.SelectShape(regions, out filtered, feature, "and", min, max);
                return Result<object>.Ok(filtered);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(BlobTool), $"按 {feature} 筛选连通域失败", ex);
                return Result<object>.Fail("连通域筛选异常", -1, ex);
            }
        }

        /// <summary>
        /// 统计连通域个数（count_obj）
        /// </summary>
        public static Result<int> CountObjects(object nativeRegions)
        {
            var regions = nativeRegions as HObject;
            if (regions == null || !regions.IsInitialized())
                return Result<int>.Fail("输入连通域无效");
            try
            {
                HOperatorSet.CountObj(regions, out HTuple count);
                return Result<int>.Ok(count.I);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(BlobTool), "连通域计数失败", ex);
                return Result<int>.Fail("连通域计数异常", -1, ex);
            }
        }

        /// <summary>
        /// 逐连通域取面积与质心（area_center），供 wrapper 内部/结果面板使用。
        /// ⚠ 返回值带 HTuple —— 只限 HalconWrapper 内部调用，不要从 Nodes 调（CS0012）。
        /// </summary>
        public static Result<Tuple> AreaCenter(object nativeRegions)
        {
            var regions = nativeRegions as HObject;
            if (regions == null || !regions.IsInitialized())
                return Result<Tuple>.Fail("输入连通域无效");
            try
            {
                HOperatorSet.AreaCenter(regions, out HTuple area, out HTuple row, out HTuple col);
                return Result<Tuple>.Ok(new Tuple(area, row, col));
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(BlobTool), "质心/面积提取失败", ex);
                return Result<Tuple>.Fail("质心提取异常", -1, ex);
            }
        }

        /// <summary>面积/质心三元组（包一层避免暴露 HTuple 元组语法给 Nodes）</summary>
        public sealed class Tuple
        {
            public HTuple Areas { get; }
            public HTuple Rows { get; }
            public HTuple Cols { get; }
            internal Tuple(HTuple areas, HTuple rows, HTuple cols) { Areas = areas; Rows = rows; Cols = cols; }
        }
    }
}
