using System;
using System.Collections.Generic;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// 基础几何与线性代数工具。★ 本类零 WPF、零 HALCON、零 Contracts —— 可以离线单测。
    /// </summary>
    public static class GeometryUtil
    {
        /// <summary>
        /// 最小二乘：解 A x = b，其中 A 为 m×n（m ≥ n）。返回 null 表示奇异 / 无解。
        /// 用法兰中给出的行向量列表，内部构造正规方程 (AᵀA)x = Aᵀb 并用带部分主元的高斯消元求解。
        /// </summary>
        public static double[] SolveLeastSquares(IList<double[]> rows, IList<double> rhs, int n)
        {
            if (rows == null || rhs == null || rows.Count != rhs.Count || rows.Count < n)
            {
                return null;
            }

            int m = rows.Count;

            // 正规方程
            double[,] ata = new double[n, n];
            double[] atb = new double[n];

            for (int k = 0; k < m; k++)
            {
                double[] row = rows[k];
                if (row == null || row.Length < n)
                {
                    return null;
                }

                double y = rhs[k];
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        ata[i, j] += row[i] * row[j];
                    }

                    atb[i] += row[i] * y;
                }
            }

            return SolveDense(ata, atb, n);
        }

        /// <summary>
        /// 解稠密线性方程组（部分主元高斯消元）。返回 null 表示奇异。
        /// </summary>
        public static double[] SolveDense(double[,] a, double[] b, int n)
        {
            double[,] m = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    m[i, j] = a[i, j];
                }

                m[i, n] = b[i];
            }

            for (int col = 0; col < n; col++)
            {
                // 部分主元
                int pivot = col;
                double best = Math.Abs(m[col, col]);
                for (int r = col + 1; r < n; r++)
                {
                    double v = Math.Abs(m[r, col]);
                    if (v > best)
                    {
                        best = v;
                        pivot = r;
                    }
                }

                if (best < 1e-12)
                {
                    return null;
                }

                if (pivot != col)
                {
                    for (int j = col; j <= n; j++)
                    {
                        double t = m[col, j];
                        m[col, j] = m[pivot, j];
                        m[pivot, j] = t;
                    }
                }

                double d = m[col, col];
                for (int j = col; j <= n; j++)
                {
                    m[col, j] /= d;
                }

                for (int r = 0; r < n; r++)
                {
                    if (r == col)
                    {
                        continue;
                    }

                    double f = m[r, col];
                    if (f == 0.0)
                    {
                        continue;
                    }

                    for (int j = col; j <= n; j++)
                    {
                        m[r, j] -= f * m[col, j];
                    }
                }
            }

            double[] x = new double[n];
            for (int i = 0; i < n; i++)
            {
                x[i] = m[i, n];
            }

            return x;
        }

        /// <summary>
        /// Kåsa 代数圆拟合（最小二乘）。★ 只取圆心，半径仅供诊断 ——
        /// 旋转中心标定里半径会被延伸杆与吸嘴偏心污染，写进产物就是错的几何量。
        /// </summary>
        public static bool FitCircleKasa(IList<Vec2> pts, out Vec2 center, out double radius)
        {
            center = Vec2.Zero;
            radius = 0.0;

            if (pts == null || pts.Count < 3)
            {
                return false;
            }

            // 以质心为原点做数值预处理，避免大坐标把正规方程搞成病态（本工位坐标量级 1e2~1e3）
            Vec2 mean = Centroid(pts);

            int m = pts.Count;
            var rows = new List<double[]>(m);
            var rhs = new List<double>(m);
            for (int i = 0; i < m; i++)
            {
                double x = pts[i].X - mean.X;
                double y = pts[i].Y - mean.Y;
                rows.Add(new double[] { x, y, 1.0 });
                rhs.Add(x * x + y * y);
            }

            double[] sol = SolveLeastSquares(rows, rhs, 3);
            if (sol == null)
            {
                return false;
            }

            // x²+y² = a·x + b·y + c  ⇒  圆心 (a/2, b/2)，半径² = c + (a²+b²)/4
            double cx = sol[0] / 2.0;
            double cy = sol[1] / 2.0;
            double r2 = sol[2] + (cx * cx + cy * cy);
            if (r2 < 0.0)
            {
                return false;
            }

            center = new Vec2(cx + mean.X, cy + mean.Y);
            radius = Math.Sqrt(r2);
            return true;
        }

        public static Vec2 Centroid(IList<Vec2> pts)
        {
            if (pts == null || pts.Count == 0)
            {
                return Vec2.Zero;
            }

            double sx = 0.0;
            double sy = 0.0;
            for (int i = 0; i < pts.Count; i++)
            {
                sx += pts[i].X;
                sy += pts[i].Y;
            }

            return new Vec2(sx / pts.Count, sy / pts.Count);
        }

        /// <summary>均方根。</summary>
        public static double Rms(IList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0.0;
            }

            double s = 0.0;
            for (int i = 0; i < values.Count; i++)
            {
                s += values[i] * values[i];
            }

            return Math.Sqrt(s / values.Count);
        }

        public static double Max(IList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0.0;
            }

            double v = values[0];
            for (int i = 1; i < values.Count; i++)
            {
                if (values[i] > v)
                {
                    v = values[i];
                }
            }

            return v;
        }

        public static double Mean(IList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0.0;
            }

            double s = 0.0;
            for (int i = 0; i < values.Count; i++)
            {
                s += values[i];
            }

            return s / values.Count;
        }

        /// <summary>
        /// 线性回归斜率 + 相关系数（用于判断"残差是否随半径增大" —— 畸变特征判读）。
        /// </summary>
        public static bool LinearTrend(IList<double> xs, IList<double> ys, out double slope, out double r)
        {
            slope = 0.0;
            r = 0.0;

            if (xs == null || ys == null || xs.Count != ys.Count || xs.Count < 3)
            {
                return false;
            }

            int n = xs.Count;
            double mx = Mean(xs);
            double my = Mean(ys);
            double sxy = 0.0;
            double sxx = 0.0;
            double syy = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dx = xs[i] - mx;
                double dy = ys[i] - my;
                sxy += dx * dy;
                sxx += dx * dx;
                syy += dy * dy;
            }

            if (sxx < 1e-15 || syy < 1e-15)
            {
                return false;
            }

            slope = sxy / sxx;
            r = sxy / Math.Sqrt(sxx * syy);
            return true;
        }
    }
}
