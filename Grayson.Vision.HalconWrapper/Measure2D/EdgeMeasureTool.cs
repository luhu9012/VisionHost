using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Templates.Models;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Measure2D
{
    /// <summary>
    /// 基于 HALCON measure 工具的亚像素级边缘测量（P0 重写，替代被注释的空壳）。
    /// 核心算子全部落到 CaliperMeasureTool（单探针/线/圆测量与拟合的唯一实现），
    /// 本类仅保留对外 API（MeasureLineDistance/MeasureCircleDiameter），语义按官方文档：
    ///   gen_measure_rectangle2 长轴=剖面扫描方向，提取垂直于长轴的直边。
    /// </summary>
    public static class EdgeMeasureTool
    {
        /// <summary>
        /// 沿 p1→p2 扫描轴测量"两条边缘之间的距离"（如宽度/间距）：
        /// 测量带中心=线段中点，长轴=p1→p2 方向，跨距半长=线段半长；
        /// width=垂直于扫描轴的剖面平均宽度（像素）。返回两条最外边缘沿扫描轴的间距。
        /// </summary>
        /// <param name="grayImg">灰度原图</param>
        /// <param name="lineRow1/Col1/lineRow2/Col2">扫描轴端点</param>
        /// <param name="width">测量带平均宽度（垂直于扫描轴，像素）</param>
        /// <param name="edgeSelect">first/last/all——本 API 恒取"距离中心最远的两条边"求间距</param>
        public static Result<double> MeasureLineDistance(HObject grayImg,
            double lineRow1, double lineCol1, double lineRow2, double lineCol2,
            double width, string edgeSelect = "all")
        {
            if (grayImg == null || !grayImg.IsInitialized())
                return Result<double>.Fail("灰度图无效");
            try
            {
                HOperatorSet.GetImageSize(grayImg, out HTuple iw, out HTuple ih);
                double dRow = lineRow2 - lineRow1;
                double dCol = lineCol2 - lineCol1;
                double len = Math.Sqrt(dRow * dRow + dCol * dCol);
                if (len < 1e-6)
                    return Result<double>.Fail("扫描轴长度为零");
                double scanPhi = Math.Atan2(dRow, dCol);
                double midRow = (lineRow1 + lineRow2) / 2.0;
                double midCol = (lineCol1 + lineCol2) / 2.0;

                // 取最外两条边：First/Last 语义映射为扫描方向两端（一次取样返回全部，再取 ±两端）
                HTuple mh;
                HOperatorSet.GenMeasureRectangle2(midRow, midCol, scanPhi,
                    len / 2.0 + 0.5, Math.Max(0.5, width / 2.0), iw.I, ih.I, "bilinear", out mh);
                try
                {
                    HTuple rows, cols, amps, dists;
                    HOperatorSet.MeasurePos(grayImg, mh, 1.0, 30, "all", "all",
                        out rows, out cols, out amps, out dists);
                    if (dists.Length < 2)
                        return Result<double>.Fail("未找到足够边缘点");
                    double dMin = double.MaxValue, dMax = double.MinValue;
                    for (int i = 0; i < dists.Length; i++)
                    {
                        double d = dists[i].D;
                        if (d < dMin) dMin = d;
                        if (d > dMax) dMax = d;
                    }
                    return Result<double>.Ok(dMax - dMin);
                }
                finally { HOperatorSet.CloseMeasure(mh); }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(EdgeMeasureTool), "直线距离测量失败", ex);
                return Result<double>.Fail("测量异常", -1, ex);
            }
        }

        /// <summary>
        /// 环形卡尺测量圆孔/圆盘直径：以 [radiusMin, radiusMax] 为探测环带，MeasurePos 取样
        /// 后 FitCircleContourXld 拟合（替代不存在的 FindCircle），返回直径。
        /// </summary>
        public static Result<double> MeasureCircleDiameter(HObject grayImg,
            double centerRow, double centerCol, double radiusMin, double radiusMax)
        {
            if (grayImg == null || !grayImg.IsInitialized())
                return Result<double>.Fail("灰度图无效");
            try
            {
                if (radiusMax <= radiusMin)
                    return Result<double>.Fail("半径范围无效（需 radiusMax>radiusMin）");
                HOperatorSet.GetImageSize(grayImg, out HTuple iw, out HTuple ih);
                double radius = (radiusMin + radiusMax) / 2.0;
                double annulus = (radiusMax - radiusMin) / 2.0;
                var ptsRes = CaliperMeasureTool.MeasureArc(grayImg, centerRow, centerCol,
                    radius, 0, 2.0 * Math.PI, Math.Max(0.5, annulus),
                    1.0, 30, CaliperTransition.All, CaliperEdgeSelect.All, iw.I, ih.I);
                if (!ptsRes.Success)
                    return Result<double>.Fail(ptsRes.Message);
                if (ptsRes.Data.Count < 3)
                    return Result<double>.Fail($"未检出圆孔（边缘点 {ptsRes.Data.Count}/3）");
                var fit = CaliperMeasureTool.FitCircle(ptsRes.Data);
                if (!fit.Success)
                    return Result<double>.Fail(fit.Message);
                return Result<double>.Ok(fit.Data.Radius * 2.0);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(EdgeMeasureTool), "圆孔测量失败", ex);
                return Result<double>.Fail("圆孔测量异常", -1, ex);
            }
        }
    }
}
