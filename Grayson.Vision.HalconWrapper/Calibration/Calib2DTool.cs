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
        /// 计算九点手眼标定矩阵并评估 RMS 重投影误差
        /// </summary>
        public static Result<(HTuple HomMat2D, double RmsError)> CalcNinePointHomMatWithRms(
            double[] px, double[] py, double[] wx, double[] wy)
        {
            if (px == null || py == null || wx == null || wy == null || px.Length < 3)
            {
                return Result<(HTuple, double)>.Fail("标定点数不足，至少需要3个点（推荐9点）");
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
    }
}