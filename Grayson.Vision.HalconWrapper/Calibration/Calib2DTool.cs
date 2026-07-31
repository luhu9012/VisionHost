using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    /// <summary>
    /// 2D九点手眼标定（相机平面对标工作台）
    /// 采集9个特征点像素坐标+物理坐标，生成HomMat2D矩阵
    /// 后续所有定位结果依靠矩阵完成像素↔毫米换算
    /// </summary>
    public static class Calib2DTool
    {
        /// <summary>
        /// 九点标定计算HomMat2D单应矩阵
        /// </summary>
        /// <param name="pixelXList">9个点像素X数组</param>
        /// <param name="pixelYList">9个点像素Y数组</param>
        /// <param name="worldXList">9个点实际物理X mm</param>
        /// <param name="worldYList">9个点实际物理Y mm</param>
        /// <returns>HomMat2D标定矩阵</returns>
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
                GlobalLogger.Error("九点标定矩阵计算失败", ex, nameof(Calib2DTool));
                return Result<HTuple>.Fail("标定计算异常", -1, ex);
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
                GlobalLogger.Error("标定矩阵保存失败", ex, nameof(Calib2DTool));
                return Result.Fail("矩阵保存异常", -1, ex);
            }
        }

        /// <summary>从文件读取标定矩阵</summary>
        public static Result<HTuple> LoadHomMatFromFile(string filePath)
        {
            try
            {
                HTuple mat;
                HOperatorSet.ReadTuple(filePath,out mat);
                return Result<HTuple>.Ok(mat);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("标定矩阵读取失败", ex, nameof(Calib2DTool));
                return Result<HTuple>.Fail("矩阵读取异常", -1, ex);
            }
        }
    }
}