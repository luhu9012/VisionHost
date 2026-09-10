//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 测量节点门面（P2 §5.2）—— 把 CaliperMeasureTool(HObject 级)包装成"object 图像"
//        入口，供 Nodes 层（零 halcondotnet 引用）直接消费：解包图像、必要时彩色转灰度、
//        取尺寸后一次完成"测量 + 拟合"，输出纯托管类型（点集/线拟合/圆拟合）。
//        FitLine/FitCircle/CaliperMeasure 三个测量节点共用本门面，不重复解包逻辑。
//===================================================================================
using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Templates.Models;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Measure2D
{
    /// <summary>测量节点一次执行的完整结果（未用到的拟合字段为 null）</summary>
    public sealed class CaliperNodeMeasureResult
    {
        /// <summary>边缘点总数（跨多探针/整环合计）</summary>
        public int EdgeCount { get; set; }

        /// <summary>全部有效边缘点（供节点可视化画十字/回放）</summary>
        public List<CaliperEdgePoint> Points { get; set; } = new List<CaliperEdgePoint>();

        /// <summary>直线拟合结果（MeasureLine 成功且点数达标时非空）</summary>
        public CaliperLineFit LineFit { get; set; }

        /// <summary>圆/弧拟合结果（MeasureCircle 成功且点数达标时非空）</summary>
        public CaliperCircleFit CircleFit { get; set; }

        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
    }

    /// <summary>
    /// 测量节点门面：object 图像入口 → HObject 解包 → 委托 CaliperMeasureTool 执行。
    /// 语义与模板资产 TemplateCaliper 完全一致（Line=沿边探针散布/横跨扫描；Circle=环形带），
    /// 只是几何种子来自节点参数/上游数据端口而非模板资产——Nodes 与模板引擎同一套测量实现。
    /// </summary>
    public static class CaliperNodeMeasure
    {
        /// <summary>把节点管线传入的图像 object（运行时 HImage/HObject）解包为灰度 HObject；彩色自动转灰度</summary>
        public static Result<HObject> AsGray(object image)
        {
            if (image == null)
                return Result<HObject>.Fail("输入图像为空");
            var h = image as HObject;
            if (h == null || !h.IsInitialized())
                return Result<HObject>.Fail("输入图像不是有效 HALCON 图像句柄");
            try
            {
                HTuple ch;
                HOperatorSet.CountChannels(h, out ch);
                if (ch.I != 1)
                {
                    HObject gray;
                    HOperatorSet.Rgb1ToGray(h, out gray);
                    return Result<HObject>.Ok(gray);
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CaliperNodeMeasure), $"通道判定/灰度转换失败(按灰度继续): {ex.Message}");
            }
            return Result<HObject>.Ok(h);
        }

        private static bool TryGetSize(HObject gray, out int width, out int height)
        {
            width = height = 0;
            try
            {
                HTuple w, h;
                HOperatorSet.GetImageSize(gray, out w, out h);
                width = w.I;
                height = h.I;
                return width > 0 && height > 0;
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CaliperNodeMeasure), $"取图像尺寸失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 直线卡尺测量 + 直线拟合（FitLine 节点用）：沿目标边缘散布 NumPoints 个探针，
        /// 每探针横跨边缘取样 → FitLineContourXld("tukey")。
        /// </summary>
        public static Result<CaliperNodeMeasureResult> MeasureLine(object image,
            double midRow, double midCol, double edgePhiRad,
            double halfSpanAlongEdge, double scanHalf, double probeAvgHalf,
            int numPoints, double sigma, double threshold,
            CaliperTransition transition, CaliperEdgeSelect select, int minEdgePoints)
        {
            var grayRes = AsGray(image);
            if (!grayRes.Success) return Result<CaliperNodeMeasureResult>.Fail(grayRes.Message);
            var gray = grayRes.Data;
            if (!TryGetSize(gray, out int w, out int h))
                return Result<CaliperNodeMeasureResult>.Fail("无法获取图像尺寸");
            try
            {
                var ptsRes = CaliperMeasureTool.MeasureLine(gray, midRow, midCol, edgePhiRad,
                    halfSpanAlongEdge, scanHalf, probeAvgHalf,
                    numPoints, sigma, threshold, transition, select, w, h);
                if (!ptsRes.Success)
                    return Result<CaliperNodeMeasureResult>.Fail(ptsRes.Message, ptsRes.ErrorCode, ptsRes.Exception);
                var pts = ptsRes.Data ?? new List<CaliperEdgePoint>();
                var result = new CaliperNodeMeasureResult
                {
                    EdgeCount = pts.Count,
                    Points = pts,
                    ImageWidth = w,
                    ImageHeight = h
                };
                if (pts.Count >= System.Math.Max(2, minEdgePoints))
                {
                    var fit = CaliperMeasureTool.FitLine(pts);
                    if (fit.Success) result.LineFit = fit.Data;
                }
                return Result<CaliperNodeMeasureResult>.Ok(result);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperNodeMeasure), "线卡尺测量节点异常", ex);
                return Result<CaliperNodeMeasureResult>.Fail("线卡尺测量异常", -1, ex);
            }
        }

        /// <summary>
        /// 单矩形测量带找边缘点（CaliperMeasure 节点用）：一条带横跨边缘取样，
        /// 输出带内全部/按 Select 收敛的边缘点，不做几何拟合。
        /// </summary>
        public static Result<CaliperNodeMeasureResult> MeasureRect(object image,
            double row, double col, double scanPhiRad,
            double scanHalf, double avgHalf,
            double sigma, double threshold,
            CaliperTransition transition, CaliperEdgeSelect select)
        {
            var grayRes = AsGray(image);
            if (!grayRes.Success) return Result<CaliperNodeMeasureResult>.Fail(grayRes.Message);
            var gray = grayRes.Data;
            if (!TryGetSize(gray, out int w, out int h))
                return Result<CaliperNodeMeasureResult>.Fail("无法获取图像尺寸");
            try
            {
                var ptsRes = CaliperMeasureTool.MeasureRectProbe(gray, row, col, scanPhiRad,
                    scanHalf, avgHalf, sigma, threshold, transition, select, w, h);
                if (!ptsRes.Success)
                    return Result<CaliperNodeMeasureResult>.Fail(ptsRes.Message, ptsRes.ErrorCode, ptsRes.Exception);
                var pts = ptsRes.Data ?? new List<CaliperEdgePoint>();
                return Result<CaliperNodeMeasureResult>.Ok(new CaliperNodeMeasureResult
                {
                    EdgeCount = pts.Count,
                    Points = pts,
                    ImageWidth = w,
                    ImageHeight = h
                });
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperNodeMeasure), "单卡尺测量节点异常", ex);
                return Result<CaliperNodeMeasureResult>.Fail("单卡尺测量异常", -1, ex);
            }
        }

        /// <summary>
        /// 环形卡尺测量 + 圆拟合（FitCircle 节点用）：整环/弧段带取样 → FitCircleContourXld("algebraic")。
        /// </summary>
        public static Result<CaliperNodeMeasureResult> MeasureCircle(object image,
            double centerRow, double centerCol, double radius,
            double arcStartRad, double arcExtentRad, double annulusHalf,
            double sigma, double threshold,
            CaliperTransition transition, CaliperEdgeSelect select, int minEdgePoints)
        {
            var grayRes = AsGray(image);
            if (!grayRes.Success) return Result<CaliperNodeMeasureResult>.Fail(grayRes.Message);
            var gray = grayRes.Data;
            if (!TryGetSize(gray, out int w, out int h))
                return Result<CaliperNodeMeasureResult>.Fail("无法获取图像尺寸");
            try
            {
                var ptsRes = CaliperMeasureTool.MeasureArc(gray, centerRow, centerCol, radius,
                    arcStartRad, arcExtentRad, annulusHalf, sigma, threshold, transition, select, w, h);
                if (!ptsRes.Success)
                    return Result<CaliperNodeMeasureResult>.Fail(ptsRes.Message, ptsRes.ErrorCode, ptsRes.Exception);
                var pts = ptsRes.Data ?? new List<CaliperEdgePoint>();
                var result = new CaliperNodeMeasureResult
                {
                    EdgeCount = pts.Count,
                    Points = pts,
                    ImageWidth = w,
                    ImageHeight = h
                };
                if (pts.Count >= System.Math.Max(3, minEdgePoints))
                {
                    var fit = CaliperMeasureTool.FitCircle(pts);
                    if (fit.Success)
                    {
                        // 底层 FitCircleContourXld 未回填 RMS：门面按"点到拟合圆径向距离"补算
                        var cf = fit.Data;
                        if (cf != null)
                        {
                            double sum = 0;
                            foreach (var p in pts)
                            {
                                double d = System.Math.Sqrt((p.Row - cf.CenterRow) * (p.Row - cf.CenterRow) +
                                                           (p.Col - cf.CenterCol) * (p.Col - cf.CenterCol));
                                double e = d - cf.Radius;
                                sum += e * e;
                            }
                            cf.RmsError = System.Math.Sqrt(sum / System.Math.Max(1, pts.Count));
                        }
                        result.CircleFit = fit.Data;
                    }
                }
                return Result<CaliperNodeMeasureResult>.Ok(result);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperNodeMeasure), "圆卡尺测量节点异常", ex);
                return Result<CaliperNodeMeasureResult>.Fail("圆卡尺测量异常", -1, ex);
            }
        }
    }
}
