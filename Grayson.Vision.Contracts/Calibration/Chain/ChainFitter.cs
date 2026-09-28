//===================================================================================
// 文件名: ChainFitter.cs
// 说 明: 范式2 链图的最小二乘仿射拟合（像素 → 目标系），MathNet 真奇异值形状判据。
//
// 数学真源（迁移设计 §3 / 单元纪律 HomMat2D.cs）：
//   拟合模型：q = A·p + t（2×3 仿射，6 参数），q=目标系坐标，p=(像素col, 像素row)。
//   ★ 元素序与传参约定（与 HomMat2D.Map / 生产端 PickAnchor(matchCol,matchRow) 一致）：
//     Matrix = [a11,a12,tx,a21,a22,ty]，wx = a11·col + a12·row + tx，wy = a21·col + a22·row + ty。
//
// ★ 形状判据（2026-09 血泪定案，此处是它的落地）：
//   落点第一判据 = 矩阵形状，RMS 抓不到形状失真（拟合半径被各向异性拉伸时 RMS 仍可≈0）。
//   σ 必须是【拟合矩阵线性部分 2×2】的真奇异值（MathNet SVD），严禁"列范数比"近似——用错=把验收门开大。
//   硬拦：|σ1/σ2 − 1| > 0.03 ⇒ ShapeDeviation 超门（调用方拒绝保存/拒绝消费）。
//
// 单元纪律：
//   · 纯函数：不做 IO、不弹 UI、不改输入；失败用 Ok=false + Error 表达，不抛异常。
//   · EIH 不在此归一化——"世界点按拍照位姿规范到法兰系"是采集编排（向导）的职责，
//     本类只对"已规范好的点对"负责。
//===================================================================================
using System;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace Grayson.Vision.Contracts.Calibration.Chain
{
    /// <summary>仿射拟合结果。Ok=false 时 Error 说明原因；Ok=true 时 Matrix/Rms/σ 有效。</summary>
    public sealed class ChainFitResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        /// <summary>6 元组 [a11,a12,tx,a21,a22,ty]；px=像素col、py=像素row</summary>
        public double[] Matrix { get; set; }
        /// <summary>重投影均方根误差（目标系单位，mm）</summary>
        public double RmsMm { get; set; }
        /// <summary>拟合矩阵线性部分（2×2）最大真奇异值（映射主尺度）</summary>
        public double Sigma1 { get; set; }
        /// <summary>拟合矩阵线性部分次大真奇异值</summary>
        public double Sigma2 { get; set; }
        /// <summary>形状偏差 |σ1/σ2 − 1|；&gt; ShapeGateRatio ⇒ 形状失真硬拦</summary>
        public double ShapeDeviation { get; set; }
        public int PointCount { get; set; }
    }

    public static partial class ChainFitter
    {
        /// <summary>形状失真硬拦门（血泪定案：拟合半径可被各向异性拉伸骗过 RMS）</summary>
        public const double ShapeGateRatio = 0.03;

        /// <summary>
        /// 最小二乘仿射拟合（SVD 求解）。px/py=像素 col/row，tx/ty=目标系坐标。
        /// 要求 ≥4 点且像素点有二维跨度（共线/重合不可解，血泪：Mark 未随轴移动时全同点）。
        /// </summary>
        public static ChainFitResult FitAffine(double[] px, double[] py, double[] tx, double[] ty)
        {
            var r = new ChainFitResult();
            if (px == null || py == null || tx == null || ty == null)
            {
                r.Error = "点对数组为空"; return r;
            }
            int n = px.Length;
            if (py.Length != n || tx.Length != n || ty.Length != n)
            {
                r.Error = "点对数组长度不一致"; return r;
            }
            if (n < 4)
            {
                r.Error = "点对不足（≥4 可解，推荐 9 点；当前 " + n + "）"; return r;
            }

            // 退化点集校验（与 HalconWrapper.CheckDegeneratePoints 同判据：二维跨度缺一不可）
            double minX = px[0], maxX = px[0], minY = py[0], maxY = py[0];
            for (int i = 1; i < n; i++)
            {
                if (px[i] < minX) minX = px[i];
                if (px[i] > maxX) maxX = px[i];
                if (py[i] < minY) minY = py[i];
                if (py[i] > maxY) maxY = py[i];
            }
            if (maxX - minX < 0.5 && maxY - minY < 0.5)
            {
                r.Error = "所有像素点几乎重合——Mark 可能未随轴移动或识别始终命中同一位置";
                return r;
            }
            if (maxX - minX < 0.5 || maxY - minY < 0.5)
            {
                r.Error = "像素点近似共线（仅一个方向有跨度），无法唯一确定 2D 映射";
                return r;
            }

            try
            {
                // 设计矩阵 A（2n×6）：奇数行 [col,row,1,0,0,0]，偶数行 [0,0,0,col,row,1]
                double[][] rows = new double[2 * n][];
                double[] b = new double[2 * n];
                for (int i = 0; i < n; i++)
                {
                    rows[2 * i]     = new double[] { px[i], py[i], 1.0, 0.0, 0.0, 0.0 };
                    rows[2 * i + 1] = new double[] { 0.0, 0.0, 0.0, px[i], py[i], 1.0 };
                    b[2 * i]     = tx[i];
                    b[2 * i + 1] = ty[i];
                }
                var A = DenseMatrix.OfRowArrays(rows);
                var bv = DenseVector.OfArray(b);

                var svd = A.Svd(true);
                var sol = svd.Solve(bv);               // 最小二乘解（6×1）
                r.Matrix = new[] { sol[0], sol[1], sol[2], sol[3], sol[4], sol[5] };

                // ★ 形状 σ 取【拟合矩阵线性部分 2×2】的真奇异值（= 映射的各向异性/形状失真）。
                //   不是设计矩阵的奇异值——那反映的是点集跨度（网格长宽比会误触发门）。
                //   依据：范式1 血泪「落点第一判据=矩阵形状」指的是 H 本身的形状。
                var lin = DenseMatrix.OfArray(new[,]
                {
                    { r.Matrix[0], r.Matrix[1] },
                    { r.Matrix[3], r.Matrix[4] },
                });
                var linSvd = lin.Svd(true);
                double s1 = linSvd.S[0], s2 = linSvd.S[1];
                r.Sigma1 = s1;
                r.Sigma2 = s2;
                r.PointCount = n;
                r.ShapeDeviation = s2 <= 1e-12 ? double.PositiveInfinity
                                               : Math.Abs(s1 / s2 - 1.0);

                // 重投影 RMS
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    double ex = r.Matrix[0] * px[i] + r.Matrix[1] * py[i] + r.Matrix[2] - tx[i];
                    double ey = r.Matrix[3] * px[i] + r.Matrix[4] * py[i] + r.Matrix[5] - ty[i];
                    sum += ex * ex + ey * ey;
                }
                r.RmsMm = Math.Sqrt(sum / n);

                if (double.IsNaN(r.RmsMm) || double.IsInfinity(r.RmsMm))
                {
                    r.Ok = false;
                    r.Error = "拟合结果含非有限值（输入数据非法？）";
                    r.Matrix = null;
                    return r;
                }
                r.Ok = true;
                r.Error = null;
                return r;
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Error = "拟合异常: " + ex.Message;
                return r;
            }
        }
    }

    /// <summary>pivoting（针尖对点法）求解结果。Ok=false 时 Error 说明原因。</summary>
    public sealed class ChainPivotResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        /// <summary>解出的工具偏移 e（T_TCP→Flange 平移，法兰系）</summary>
        public double Ex { get; set; }
        public double Ey { get; set; }
        /// <summary>针尖物理点 P_ref（Base 系，诊断用）</summary>
        public double RefX { get; set; }
        public double RefY { get; set; }
        /// <summary>逐点残差 RMS（mm）＝ 对针重复精度——pivoting 的验收门</summary>
        public double RmsMm { get; set; }
        /// <summary>逐点残差（mm，与输入同序）；异常点排查用</summary>
        public double[] PerPointResidual { get; set; }
        /// <summary>U 角跨度（度）——e 与 P_ref 可分性判据</summary>
        public double USpanDeg { get; set; }
        public int PointCount { get; set; }
        /// <summary>可解但建议改善的提示（如角度跨度不足 90°）</summary>
        public string Warning { get; set; }
    }

    public static partial class ChainFitter
    {
        /// <summary>对针重复精度验收门（mm）。超门 ⇒ 针尖没扎稳/机器人重复精度不足，禁止采用。</summary>
        public const double PivotRmsGateMm = 0.5;

        /// <summary>
        /// 针尖对点法（pivoting）求工具偏移 e 与参考点 P_ref。
        /// 模型：tᵢ + R(Uᵢ)·e = P_ref（针尖物理点在 Base 系不动，法兰绕针尖转）。
        /// 线性化：[R(Uᵢ) | −I₂]·[e ; P_ref] = −tᵢ（2n×4，SVD 最小二乘）。
        /// 门禁：n≥3；U 跨度 &lt;30° 硬拦（e 与 P_ref 不可分）；&lt;90° 警示（条件数放大）；
        ///       残差 RMS &gt; PivotRmsGateMm ⇒ Ok=true 但调用方必须把 RmsMm 当验收门呈现给用户。
        /// </summary>
        public static ChainPivotResult FitPivoting(double[] uDeg, double[] fx, double[] fy)
        {
            var r = new ChainPivotResult();
            if (uDeg == null || fx == null || fy == null || uDeg.Length != fx.Length || fx.Length != fy.Length)
            {
                r.Error = "输入数组为空或长度不一致"; return r;
            }
            int n = uDeg.Length;
            if (n < 3)
            {
                r.Error = "对针点不足（≥3 可解，推荐 4~8 个角度；当前 " + n + "）"; return r;
            }

            double uMin = uDeg[0], uMax = uDeg[0];
            for (int i = 1; i < n; i++)
            {
                if (uDeg[i] < uMin) uMin = uDeg[i];
                if (uDeg[i] > uMax) uMax = uDeg[i];
            }
            r.USpanDeg = uMax - uMin;
            if (r.USpanDeg < 30.0)
            {
                r.Error = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "U 角跨度仅 {0:F1}°（<30°）——角度未散开时 e 与 P_ref 不可分，请把法兰转过更大范围再扎点", r.USpanDeg);
                return r;
            }
            if (r.USpanDeg < 90.0)
                r.Warning = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "U 角跨度 {0:F1}° < 90°：可解但 e/P_ref 相关性偏高，建议覆盖 ≥90°（行业惯例 0/90/180/270 四点）", r.USpanDeg);

            try
            {
                // 设计矩阵 A（2n×4）：行 x = [cosU, −sinU, −1, 0]，行 y = [sinU, cosU, 0, −1]；b = −t
                double[][] rows = new double[2 * n][];
                double[] b = new double[2 * n];
                for (int i = 0; i < n; i++)
                {
                    double rad = uDeg[i] * Math.PI / 180.0;
                    double c = Math.Cos(rad), s = Math.Sin(rad);
                    rows[2 * i]     = new double[] { c, -s, -1.0, 0.0 };
                    rows[2 * i + 1] = new double[] { s,  c,  0.0, -1.0 };
                    b[2 * i]     = -fx[i];
                    b[2 * i + 1] = -fy[i];
                }
                var A = DenseMatrix.OfRowArrays(rows);
                var bv = DenseVector.OfArray(b);
                var sol = A.Svd(true).Solve(bv);       // [ex, ey, PrefX, PrefY]

                r.Ex = sol[0]; r.Ey = sol[1];
                r.RefX = sol[2]; r.RefY = sol[3];
                r.PointCount = n;

                double sum = 0;
                r.PerPointResidual = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double rad = uDeg[i] * Math.PI / 180.0;
                    double c = Math.Cos(rad), s = Math.Sin(rad);
                    // 预测针尖位置：tᵢ + R(Uᵢ)·e，与 P_ref 的偏差即残差
                    double px = fx[i] + c * r.Ex - s * r.Ey;
                    double py = fy[i] + s * r.Ex + c * r.Ey;
                    double dx = px - r.RefX, dy = py - r.RefY;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    r.PerPointResidual[i] = d;
                    sum += d * d;
                }
                r.RmsMm = Math.Sqrt(sum / n);

                if (double.IsNaN(r.RmsMm) || double.IsInfinity(r.RmsMm))
                {
                    r.Ok = false;
                    r.Error = "求解结果含非有限值（输入数据非法？）";
                    return r;
                }
                if (r.RmsMm > PivotRmsGateMm)
                    r.Warning = (r.Warning == null ? "" : r.Warning + "；")
                        + string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "残差 RMS {0:F3}mm 超对针重复精度门 {1:F1}mm——检查针尖是否松动/每次是否扎入同一物理点", r.RmsMm, PivotRmsGateMm);
                r.Ok = true;
                return r;
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Error = "pivoting 求解异常: " + ex.Message;
                return r;
            }
        }
    }
}
