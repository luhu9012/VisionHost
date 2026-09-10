using System;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    /// <summary>
    /// 2D九点手眼标定（相机平面对标工作台）
    /// 采集9个特征点像素坐标+物理坐标，生成HomMat2D矩阵
    /// </summary>
    public static class Calib2DTool
    {
        /// <summary>
        /// 九点标定计算HomMat2D单应矩阵（基础版）
        /// </summary>
        public static Result<HTuple> CalcNinePointHomMat(HTuple pixelXList, HTuple pixelYList, HTuple worldXList, HTuple worldYList)
        {
            try
            {
                if (pixelXList == null || pixelYList == null || worldXList == null || worldYList == null
                    || pixelXList.Length != pixelYList.Length || pixelXList.Length != worldXList.Length
                    || pixelXList.Length != worldYList.Length)
                {
                    return Result<HTuple>.Fail("标定输入数组缺失或长度不一致");
                }
                if (pixelXList.Length < 4)
                {
                    return Result<HTuple>.Fail("标定点数不足，vector_to_hom_mat2d 至少需要 4 对点（推荐 9 点）");
                }

                // 像素坐标必须有二维跨度：全同点/共线点无法唯一确定 2D 映射
                int nPt = pixelXList.Length;
                double[] pxs = new double[nPt];
                double[] pys = new double[nPt];
                for (int i = 0; i < nPt; i++)
                {
                    pxs[i] = pixelXList[i].D;
                    pys[i] = pixelYList[i].D;
                }
                string degenerate = CheckDegeneratePoints(pxs, pys);
                if (degenerate != null)
                {
                    return Result<HTuple>.Fail(degenerate);
                }

                HTuple homMat;
                HOperatorSet.VectorToHomMat2d(pixelXList, pixelYList, worldXList, worldYList, out homMat);
                return Result<HTuple>.Ok(homMat);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(Calib2DTool), "九点标定矩阵计算失败", ex);
                return Result<HTuple>.Fail("标定计算异常", -1, ex);
            }
        }

        /// <summary>
        /// 退化点集校验：像素点几乎重合 / 近似共线（缺一个方向跨度）时，2D 映射无法唯一确定，
        /// HALCON vector_to_hom_mat2d 会抛异常或给出病态矩阵。返回 null 表示通过，否则返回提示文案。
        /// </summary>
        internal static string CheckDegeneratePoints(double[] px, double[] py)
        {
            if (px == null || py == null || px.Length == 0 || px.Length != py.Length)
            {
                return "标定像素坐标为空或长度不一致";
            }
            if (px.Length < 4)
            {
                return null; // 点数下限由调用方各自把关
            }

            double minX = px[0], maxX = px[0];
            double minY = py[0], maxY = py[0];
            for (int i = 1; i < px.Length; i++)
            {
                if (px[i] < minX) minX = px[i];
                if (px[i] > maxX) maxX = px[i];
                if (py[i] < minY) minY = py[i];
                if (py[i] > maxY) maxY = py[i];
            }
            double spreadX = maxX - minX;
            double spreadY = maxY - minY;

            if (spreadX < 0.5 && spreadY < 0.5)
            {
                return "所有像素点几乎重合——Mark 可能未随轴移动或识别始终命中同一位置，请检查走位与特征提取";
            }
            if (spreadX < 0.5 || spreadY < 0.5)
            {
                return "像素点近似共线（X/Y 仅一个方向有跨度），无法唯一确定 2D 映射，请按二维网格分布采样";
            }
            return null;
        }

        /// <summary>
        /// 计算九点手眼标定矩阵并评估 RMS 重投影误差
        /// </summary>
        public static Result<(HTuple HomMat2D, double RmsError)> CalcNinePointHomMatWithRms(
            double[] px, double[] py, double[] wx, double[] wy)
        {
            // ⚠ 2026-09-04 契约修正：HALCON vector_to_hom_mat2d 求 2D 投影/仿射映射至少需要 4 对点，
            //   3 点会在算子层抛异常（此前契约写"≥3 即可"是错的）。严谨下限取 6，但底层放行 4，
            //   由业务层（向导要求 ≥6）把守精度。
            if (px == null || py == null || wx == null || wy == null)
            {
                return Result<(HTuple, double)>.Fail("标定点数据为空，无法拟合");
            }
            if (px.Length != py.Length || px.Length != wx.Length || px.Length != wy.Length)
            {
                return Result<(HTuple, double)>.Fail("像素/世界坐标数组长度不一致，无法拟合");
            }
            if (px.Length < 4)
            {
                return Result<(HTuple, double)>.Fail("标定点数不足，vector_to_hom_mat2d 至少需要 4 对点（推荐 9 点）");
            }
            string degenerate = CheckDegeneratePoints(px, py);
            if (degenerate != null)
            {
                return Result<(HTuple, double)>.Fail(degenerate);
            }

            try
            {
                HTuple hPx = new HTuple(px);
                HTuple hPy = new HTuple(py);
                HTuple hWx = new HTuple(wx);
                HTuple hWy = new HTuple(wy);

                HOperatorSet.VectorToHomMat2d(hPx, hPy, hWx, hWy, out HTuple homMat2D);

                // 计算重投影误差 (RMS)
                HOperatorSet.AffineTransPoint2d(homMat2D, hPx, hPy, out HTuple calcWx, out HTuple calcWy);
                double sumSquareErr = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    double errX = calcWx[i].D - wx[i];
                    double errY = calcWy[i].D - wy[i];
                    sumSquareErr += (errX * errX + errY * errY);
                }
                double rms = Math.Sqrt(sumSquareErr / px.Length);

                return Result<(HTuple, double)>.Ok((homMat2D, rms));
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(Calib2DTool), "九点标定矩阵计算失败", ex);
                return Result<(HTuple, double)>.Fail("标定计算异常: " + ex.Message, -1, ex);
            }
        }

        /// <summary>保存标定矩阵到本地文件</summary>
        public static Result SaveHomMatToFile(HTuple homMat, string filePath)
        {
            try
            {
                HOperatorSet.WriteTuple(homMat, filePath);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(Calib2DTool), "标定矩阵保存失败", ex);
                return Result.Fail("矩阵保存异常", -1, ex);
            }
        }

        /// <summary>从文件读取标定矩阵</summary>
        public static Result<HTuple> LoadHomMatFromFile(string filePath)
        {
            try
            {
                HOperatorSet.ReadTuple(filePath, out HTuple mat);
                return Result<HTuple>.Ok(mat);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(Calib2DTool), "标定矩阵读取失败", ex);
                return Result<HTuple>.Fail("矩阵读取异常", -1, ex);
            }
        }

        /// <summary>
        /// 最小二乘圆拟合（Kåsa 代数法），用于旋转中心标定。
        /// 输入多个角度下特征点的像素坐标（应近似分布在一个圆弧上——
        /// 偏心吸嘴绕 U 轴旋转时 Mark/吸嘴尖扫出的轨迹），拟合出圆心 (cx,cy) 与半径 r。
        /// 圆心即旋转轴在图像平面的投影。返回 RMS 残差供诊断（像素单位）。
        /// 公式：对每点 (xi,yi)：2xc·xi + 2yc·yi + (r²−xc²−yc²) = xi²+yi²，
        ///       令 A=[2xi, 2yi, 1]，b=xi²+yi²，最小二乘解 3 元一次方程组。
        /// </summary>
        public static Result<(double Cx, double Cy, double Radius, double Rms)> FitCircleKasa(
            double[] px, double[] py)
        {
            if (px == null || py == null || px.Length < 3 || px.Length != py.Length)
            {
                return Result<(double, double, double, double)>.Fail("圆拟合至少需要 3 个采样点");
            }
            try
            {
                int n = px.Length;
                // 法方程系数（对称 3x3）：变量为 [xc, yc, k]，k = r²−xc²−yc²
                double s00 = 0, s01 = 0, s02 = 0, s11 = 0, s12 = 0, s22 = 0;
                double b0 = 0, b1 = 0, b2 = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = px[i], y = py[i];
                    double a0 = 2 * x, a1 = 2 * y, a2 = 1;
                    double rhs = x * x + y * y;
                    s00 += a0 * a0; s01 += a0 * a1; s02 += a0 * a2;
                    s11 += a1 * a1; s12 += a1 * a2; s22 += a2 * a2;
                    b0 += a0 * rhs; b1 += a1 * rhs; b2 += a2 * rhs;
                }
                // 高斯消元解 3x3：s·[xc,yc,k] = b
                double[,] m = { { s00, s01, s02, b0 }, { s01, s11, s12, b1 }, { s02, s12, s22, b2 } };
                for (int col = 0; col < 3; col++)
                {
                    // 选主元
                    int piv = col;
                    for (int r = col + 1; r < 3; r++)
                    {
                        if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
                    }
                    if (Math.Abs(m[piv, col]) < 1e-12) return Result<(double, double, double, double)>.Fail("圆拟合矩阵奇异：采样点可能近似共线或重合");
                    if (piv != col)
                    {
                        for (int c = 0; c < 4; c++) { double t = m[col, c]; m[col, c] = m[piv, c]; m[piv, c] = t; }
                    }
                    for (int r = 0; r < 3; r++)
                    {
                        if (r == col) continue;
                        double f = m[r, col] / m[col, col];
                        for (int c = col; c < 4; c++) m[r, c] -= f * m[col, c];
                    }
                }
                double xc = m[0, 3] / m[0, 0];
                double yc = m[1, 3] / m[1, 1];
                double k = m[2, 3] / m[2, 2];
                double r2 = k + xc * xc + yc * yc;
                if (r2 < 0) return Result<(double, double, double, double)>.Fail("圆拟合半径平方为负（数据不呈圆弧分布），请检查旋转采样是否覆盖了足够角度");
                double radius = Math.Sqrt(r2);

                // RMS 残差（像素）
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    double d = Math.Sqrt((px[i] - xc) * (px[i] - xc) + (py[i] - yc) * (py[i] - yc)) - radius;
                    sum += d * d;
                }
                double rms = Math.Sqrt(sum / n);
                return Result<(double, double, double, double)>.Ok((xc, yc, radius, rms));
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(Calib2DTool), "圆拟合失败", ex);
                return Result<(double, double, double, double)>.Fail("圆拟合异常: " + ex.Message, -1, ex);
            }
        }
    }
}