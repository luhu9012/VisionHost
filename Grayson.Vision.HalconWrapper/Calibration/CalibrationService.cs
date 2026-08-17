using System;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    public class CalibrationService : ICalibrationService
    {
        public Result<CalibrationResult> CalcNinePointHomMat(double[] pixelXList, double[] pixelYList, double[] worldXList, double[] worldYList)
        {
            try
            {
                HTuple px = new HTuple(pixelXList);
                HTuple py = new HTuple(pixelYList);
                HTuple wx = new HTuple(worldXList);
                HTuple wy = new HTuple(worldYList);

                var res = Calib2DTool.CalcNinePointHomMat(px, py, wx, wy);
                if (!res.Success) return Result<CalibrationResult>.Fail(res.Message);

                // 计算 RMS 误差
                double rms = 0;
                try
                {
                    HOperatorSet.AffineTransPoint2d(res.Data, px, py, out HTuple calcWx, out HTuple calcWy);
                    double sumSquareErr = 0;
                    for (int i = 0; i < pixelXList.Length; i++)
                    {
                        double errX = calcWx[i].D - worldXList[i];
                        double errY = calcWy[i].D - worldYList[i];
                        sumSquareErr += (errX * errX + errY * errY);
                    }
                    rms = Math.Sqrt(sumSquareErr / pixelXList.Length);
                }
                catch { }

                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hommat_{Guid.NewGuid():N}.tup");
                var save = Calib2DTool.SaveHomMatToFile(res.Data, tmp);
                if (!save.Success) return Result<CalibrationResult>.Fail("矩阵保存失败: " + save.Message);

                return Result<CalibrationResult>.Ok(new CalibrationResult
                {
                    SavedFilePath = tmp,
                    RmsError = rms
                });
            }
            catch (Exception ex)
            {
                return Result<CalibrationResult>.Fail("标定计算异常: " + ex.Message, -1, ex);
            }
        }

        public Result SaveHomMatFile(string sourceFilePath, string destFilePath)
        {
            try
            {
                System.IO.File.Copy(sourceFilePath, destFilePath, true);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                return Result.Fail("保存文件失败: " + ex.Message, -1, ex);
            }
        }

        public Result<FixtureData> CreateFixture(double refRow, double refCol, double refAngle, double curRow, double curCol, double curAngle)
        {
            return FixtureTool.CreateFixture(refRow, refCol, refAngle, curRow, curCol, curAngle);
        }

        public Result<DetectionResult> DetectCalibrationPoints(string imageFilePath)
        {
            return Result<DetectionResult>.Fail("DetectCalibrationPoints 尚未实现，请在 HalconWrapper 中实现检测逻辑。");
        }

        public Result<(double WorldX, double WorldY)> MapPixelToWorld(string matrixFilePath, double px, double py)
        {
            var loadRes = Calib2DTool.LoadHomMatFromFile(matrixFilePath);
            if (!loadRes.Success) return Result<(double, double)>.Fail(loadRes.Message);

            try
            {
                HOperatorSet.AffineTransPoint2d(loadRes.Data, px, py, out HTuple wx, out HTuple wy);
                return Result<(double, double)>.Ok((wx.D, wy.D));
            }
            catch (Exception ex)
            {
                return Result<(double, double)>.Fail("坐标转换失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 物理坐标逆转换像素坐标 (World -> Pixel)
        /// </summary>
        public Result<(double PixelX, double PixelY)> MapWorldToPixel(string matrixFilePath, double wx, double wy)
        {
            var loadRes = Calib2DTool.LoadHomMatFromFile(matrixFilePath);
            if (!loadRes.Success) return Result<(double, double)>.Fail(loadRes.Message);

            try
            {
                // 计算矩阵逆矩阵 HomMat2dInvert
                HOperatorSet.HomMat2dInvert(loadRes.Data, out HTuple homMat2DInvert);

                // 使用逆矩阵执行坐标转换
                HOperatorSet.AffineTransPoint2d(homMat2DInvert, wx, wy, out HTuple px, out HTuple py);

                return Result<(double, double)>.Ok((px.D, py.D));
            }
            catch (Exception ex)
            {
                return Result<(double, double)>.Fail("坐标逆转换失败: " + ex.Message);
            }
        }
    }
}