using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Templates.Models;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Measure2D
{
    /// <summary>单条亚像素边缘点（MeasurePos 输出）</summary>
    public class CaliperEdgePoint
    {
        public double Row { get; set; }
        public double Col { get; set; }
        /// <summary>边缘幅度（对比度）</summary>
        public double Amplitude { get; set; }
        /// <summary>到测量对象中心的带符号距离（沿扫描方向）</summary>
        public double Distance { get; set; }
    }

    /// <summary>直线拟合结果（FitLineContourXld）</summary>
    public class CaliperLineFit
    {
        public double Row1 { get; set; }
        public double Col1 { get; set; }
        public double Row2 { get; set; }
        public double Col2 { get; set; }
        /// <summary>线段角度（弧度，HALCON phi 约定：atan2(ΔRow,ΔCol)）</summary>
        public double Phi => Math.Atan2(Row2 - Row1, Col2 - Col1);
        public double RmsError { get; set; }
        public int UsedPoints { get; set; }
    }

    /// <summary>圆/弧拟合结果（FitCircleContourXld）</summary>
    public class CaliperCircleFit
    {
        public double CenterRow { get; set; }
        public double CenterCol { get; set; }
        public double Radius { get; set; }
        public double RmsError { get; set; }
        public int UsedPoints { get; set; }
    }

    /// <summary>
    /// 基于 HALCON measure 工具的亚像素边缘测量（P0，替代 EdgeMeasureTool 被注释的空壳）。
    /// HALCON 语义（官方文档 2026-09-09 核实）：
    ///   · gen_measure_rectangle2(Row,Col,Phi,Length1,Length2,W,H,Interp)：长轴=剖面扫描方向，
    ///     提取【垂直于长轴】的直边；灰度剖面沿长轴采样、沿短轴(2·Length2)方向平均。
    ///   · gen_measure_arc(CenterRow,CenterCol,Radius,AngleStart,AngleExtent,AnnulusRadius,W,H,Interp)：环形同语义。
    /// 因此本类约定：调用方给出【横跨边缘的测量带】时，
    ///   measureRectLen1 = 跨边探测半长（扫描范围），measureRectLen2 = 沿边平均半宽。
    /// 单探针按 Select 语义收敛为一个点：All→幅度最大；First→距中心最近；Last→距中心最远。
    /// </summary>
    public static class CaliperMeasureTool
    {
        public static string TransitionStr(CaliperTransition t)
        {
            switch (t)
            {
                case CaliperTransition.Positive: return "positive";
                case CaliperTransition.Negative: return "negative";
                default: return "all";
            }
        }

        /// <summary>
        /// 矩形测量带单次取样：在 (row,col) 处以 scanPhi 为主轴方向（横跨边缘）取边缘点集。
        /// measureRectLen1=跨边扫描半长；measureRectLen2=沿边平均半宽。
        /// 返回按 Select 收敛后的点（All 语义=取幅度最大的一条，避免带内多边缘污染拟合）。
        /// </summary>
        public static Result<List<CaliperEdgePoint>> MeasureRectProbe(HObject gray,
            double row, double col, double scanPhiRad,
            double measureRectLen1, double measureRectLen2,
            double sigma, double threshold, CaliperTransition transition, CaliperEdgeSelect select,
            int imgW, int imgH)
        {
            if (gray == null || !gray.IsInitialized())
                return Result<List<CaliperEdgePoint>>.Fail("灰度图无效");
            try
            {
                HTuple mh;
                // HalconDotNet 约定：handle 输出在参数最后（同 CreateShapeModel(..., out modelId)）
                HOperatorSet.GenMeasureRectangle2(row, col, scanPhiRad,
                    measureRectLen1, measureRectLen2, imgW, imgH, "bilinear", out mh);
                try
                {
                    HTuple rows, cols, amps, dists;
                    HOperatorSet.MeasurePos(gray, mh, sigma, threshold,
                        TransitionStr(transition), "all", out rows, out cols, out amps, out dists);
                    int n = rows.Length;
                    if (n == 0)
                        return Result<List<CaliperEdgePoint>>.Ok(new List<CaliperEdgePoint>());

                    var pts = new List<CaliperEdgePoint>(n);
                    for (int i = 0; i < n; i++)
                    {
                        pts.Add(new CaliperEdgePoint
                        {
                            Row = rows[i].D,
                            Col = cols[i].D,
                            Amplitude = amps[i].D,
                            Distance = dists[i].D
                        });
                    }
                    // 按 Select 收敛
                    switch (select)
                    {
                        case CaliperEdgeSelect.First:
                            return Result<List<CaliperEdgePoint>>.Ok(new List<CaliperEdgePoint>
                            {
                                pts.OrderBy(p => Math.Abs(p.Distance)).First()
                            });
                        case CaliperEdgeSelect.Last:
                            return Result<List<CaliperEdgePoint>>.Ok(new List<CaliperEdgePoint>
                            {
                                pts.OrderByDescending(p => Math.Abs(p.Distance)).First()
                            });
                        default:
                            // All：取幅度最大（最显著）的一条边缘作为本探针代表点
                            return Result<List<CaliperEdgePoint>>.Ok(new List<CaliperEdgePoint>
                            {
                                pts.OrderByDescending(p => p.Amplitude).First()
                            });
                    }
                }
                finally
                {
                    HOperatorSet.CloseMeasure(mh);
                }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperMeasureTool), "矩形测量带取样失败", ex);
                return Result<List<CaliperEdgePoint>>.Fail("卡尺取样异常", -1, ex);
            }
        }

        /// <summary>
        /// 沿目标边缘的多探针直线卡尺测量：在边缘段中点 (midRow,midCol)、方向 edgePhiRad（HALCON phi 约定）
        /// 上按 NumPoints 个探针取样（探针横跨边缘），返回全部边缘点（用于 FitLine）。
        /// halfSpanAlongEdge=探针沿边散布半跨距；scanHalf=跨边探测半长；probeAvgHalf=沿边平均半宽。
        /// </summary>
        public static Result<List<CaliperEdgePoint>> MeasureLine(HObject gray,
            double midRow, double midCol, double edgePhiRad,
            double halfSpanAlongEdge, double scanHalf, double probeAvgHalf,
            int numPoints, double sigma, double threshold,
            CaliperTransition transition, CaliperEdgeSelect select,
            int imgW, int imgH)
        {
            if (numPoints < 1) numPoints = 1;
            var all = new List<CaliperEdgePoint>();
            // 沿边单位方向（phi 约定：dRow=sinφ, dCol=cosφ）
            double duRow = Math.Sin(edgePhiRad);
            double duCol = Math.Cos(edgePhiRad);
            // 扫描方向 = 边缘法向（+90°），即测量带长轴方向
            double scanPhi = edgePhiRad + Math.PI / 2.0;
            for (int i = 0; i < numPoints; i++)
            {
                double t = numPoints == 1 ? 0.0 : -1.0 + 2.0 * i / (numPoints - 1);
                double r = midRow + t * halfSpanAlongEdge * duRow;
                double c = midCol + t * halfSpanAlongEdge * duCol;
                var res = MeasureRectProbe(gray, r, c, scanPhi, scanHalf, probeAvgHalf,
                    sigma, threshold, transition, select, imgW, imgH);
                if (res.Success)
                {
                    foreach (var p in res.Data) all.Add(p);
                }
                else
                {
                    LogBus.Warn(nameof(CaliperMeasureTool), $"探针 #{i} 取样失败（忽略）: {res.Message}");
                }
            }
            return Result<List<CaliperEdgePoint>>.Ok(all);
        }

        /// <summary>边缘点集 → 直线拟合（tukey 抗离群）</summary>
        public static Result<CaliperLineFit> FitLine(IEnumerable<CaliperEdgePoint> pts)
        {
            var list = (pts ?? Enumerable.Empty<CaliperEdgePoint>()).Where(p => p != null).ToList();
            if (list.Count < 2)
                return Result<CaliperLineFit>.Fail($"有效边缘点不足（{list.Count}/2）");
            try
            {
                double[] rows = list.Select(p => p.Row).ToArray();
                double[] cols = list.Select(p => p.Col).ToArray();
                HObject contour;
                HOperatorSet.GenContourPolygonXld(out contour, rows, cols);
                try
                {
                    HTuple r1, c1, r2, c2, nr, nc, dist;
                    HOperatorSet.FitLineContourXld(contour, "tukey", -1, 0, 5, 2,
                        out r1, out c1, out r2, out c2, out nr, out nc, out dist);
                    double rms = 0;
                    for (int i = 0; i < dist.Length; i++) rms += dist[i].D * dist[i].D;
                    rms = Math.Sqrt(rms / Math.Max(1, dist.Length));
                    return Result<CaliperLineFit>.Ok(new CaliperLineFit
                    {
                        Row1 = r1.D, Col1 = c1.D, Row2 = r2.D, Col2 = c2.D,
                        RmsError = rms, UsedPoints = list.Count
                    });
                }
                finally { contour.Dispose(); }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperMeasureTool), "直线拟合失败", ex);
                return Result<CaliperLineFit>.Fail("直线拟合异常", -1, ex);
            }
        }

        /// <summary>
        /// 环形/圆弧卡尺测量：center 处半径 radius、角段 [arcStartRad, arcStartRad+arcExtentRad]，
        /// annulusHalf=环半宽（跨半径方向）；返回全部边缘点（用于 FitCircle）。
        /// </summary>
        public static Result<List<CaliperEdgePoint>> MeasureArc(HObject gray,
            double centerRow, double centerCol, double radius,
            double arcStartRad, double arcExtentRad, double annulusHalf,
            double sigma, double threshold, CaliperTransition transition, CaliperEdgeSelect select,
            int imgW, int imgH)
        {
            if (gray == null || !gray.IsInitialized())
                return Result<List<CaliperEdgePoint>>.Fail("灰度图无效");
            try
            {
                HTuple mh;
                HOperatorSet.GenMeasureArc(centerRow, centerCol, radius,
                    arcStartRad, arcExtentRad, annulusHalf, imgW, imgH, "bilinear", out mh);
                try
                {
                    HTuple rows, cols, amps, dists;
                    HOperatorSet.MeasurePos(gray, mh, sigma, threshold,
                        TransitionStr(transition), "all", out rows, out cols, out amps, out dists);
                    int n = rows.Length;
                    var all = new List<CaliperEdgePoint>(n);
                    for (int i = 0; i < n; i++)
                    {
                        all.Add(new CaliperEdgePoint
                        {
                            Row = rows[i].D, Col = cols[i].D,
                            Amplitude = amps[i].D, Distance = dists[i].D
                        });
                    }
                    if (n <= 1) return Result<List<CaliperEdgePoint>>.Ok(all);
                    // 环形带内同半径可能有内外两条边缘：按 Select 语义收敛为一条链
                    switch (select)
                    {
                        case CaliperEdgeSelect.First:
                            return Result<List<CaliperEdgePoint>>.Ok(all
                                .GroupBy(p => Math.Sign(p.Distance))
                                .Select(g => g.OrderBy(p => Math.Abs(p.Distance)).First()).ToList());
                        case CaliperEdgeSelect.Last:
                            return Result<List<CaliperEdgePoint>>.Ok(all
                                .GroupBy(p => Math.Sign(p.Distance))
                                .Select(g => g.OrderByDescending(p => Math.Abs(p.Distance)).First()).ToList());
                        default:
                            // All：两链都保留（拟合圆时 tukey 可抗内外环混入，但更稳的是只取幅度大的一链）
                            var chainA = all.Where(p => p.Distance >= 0).OrderByDescending(p => p.Amplitude).ToList();
                            var chainB = all.Where(p => p.Distance < 0).OrderByDescending(p => p.Amplitude).ToList();
                            var keep = chainA.Count >= chainB.Count ? chainA : chainB;
                            return Result<List<CaliperEdgePoint>>.Ok(keep);
                    }
                }
                finally { HOperatorSet.CloseMeasure(mh); }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperMeasureTool), "环形卡尺取样失败", ex);
                return Result<List<CaliperEdgePoint>>.Fail("环形卡尺取样异常", -1, ex);
            }
        }

        /// <summary>边缘点集 → 圆/弧拟合（algebraic 最小二乘）</summary>
        public static Result<CaliperCircleFit> FitCircle(IEnumerable<CaliperEdgePoint> pts)
        {
            var list = (pts ?? Enumerable.Empty<CaliperEdgePoint>()).Where(p => p != null).ToList();
            if (list.Count < 3)
                return Result<CaliperCircleFit>.Fail($"有效边缘点不足（{list.Count}/3）");
            try
            {
                double[] rows = list.Select(p => p.Row).ToArray();
                double[] cols = list.Select(p => p.Col).ToArray();
                HObject contour;
                HOperatorSet.GenContourPolygonXld(out contour, rows, cols);
                try
                {
                    HTuple crow, ccol, rad, sphi, ephi, order;
                    HOperatorSet.FitCircleContourXld(contour, "algebraic", -1, 0, 0, 3, 2,
                        out crow, out ccol, out rad, out sphi, out ephi, out order);
                    return Result<CaliperCircleFit>.Ok(new CaliperCircleFit
                    {
                        CenterRow = crow.D, CenterCol = ccol.D, Radius = rad.D,
                        UsedPoints = list.Count
                    });
                }
                finally { contour.Dispose(); }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperMeasureTool), "圆拟合失败", ex);
                return Result<CaliperCircleFit>.Fail("圆拟合异常", -1, ex);
            }
        }
    }
}
