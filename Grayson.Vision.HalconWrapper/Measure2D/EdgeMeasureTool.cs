using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Measure2D
{
    /// <summary>
    /// 基于measure工具的亚像素级边缘测量
    /// 测长度、宽度、孔间距、两条边夹角，精度优于普通轮廓计算
    /// </summary>
    public static class EdgeMeasureTool
    {
        /// <summary>
        /// 直线矩形测量卡尺，查找两侧边缘，返回两点距离
        /// </summary>
        /// <param name="grayImg">灰度原图</param>
        /// <param name="lineRow1/Col1">卡尺起点</param>
        /// <param name="lineRow2/Col2">卡尺终点</param>
        /// <param name="width">卡尺横向宽度</param>
        /// <param name="edgeSelect">first/last/all 取第一条/最后一条边缘</param>
        public static Result<double> MeasureLineDistance(HObject grayImg, double lineRow1, double lineCol1, double lineRow2, double lineCol2, double width, string edgeSelect = "all")
        {
            if (grayImg == null || !grayImg.IsInitialized())
                return Result<double>.Fail("灰度图无效");
            try
            {
                //HTuple measureHandle;
                //// 创建测量句柄
                //HOperatorSet.GenMeasureRectangle2(lineRow1, lineCol1, lineRow2, lineCol2, width, out measureHandle);
                //HTuple edgeRows, edgeCols, amplitudes, distances;
                //// 执行边缘查找
                //HOperatorSet.MeasurePos(grayImg, measureHandle, 10, "all", edgeSelect, out edgeRows, out edgeCols, out amplitudes, out distances);
                //// 释放句柄
                //HOperatorSet.CloseMeasure(measureHandle);

                //if (distances.Length >= 2)
                //{
                //    return Result<double>.Ok(distances[1].D - distances[0].D);
                //}
                return Result<double>.Fail("未找到足够边缘点");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("直线距离测量失败", ex, nameof(EdgeMeasureTool));
                return Result<double>.Fail("测量异常", -1, ex);
            }
        }

        /// <summary>
        /// 圆形卡尺测量圆孔内径，返回直径
        /// </summary>
        public static Result<double> MeasureCircleDiameter(HObject grayImg, double centerRow, double centerCol, double radiusMin, double radiusMax)
        {
            try
            {
                HTuple rows, cols, radii;
                //HOperatorSet.FindCircle(grayImg, centerRow, centerCol, radiusMin, radiusMax, out rows, out cols, out radii);
                //if (radii.Length > 0)
                //{
                //    return Result<double>.Ok(radii[0].D * 2);
                //}
                return Result<double>.Fail("未检出圆孔");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("圆孔测量失败", ex, nameof(EdgeMeasureTool));
                return Result<double>.Fail("圆孔测量异常", -1, ex);
            }
        }
    }
}