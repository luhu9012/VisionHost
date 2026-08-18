using Grayson.Vision.Contracts.Core;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Calibration.Models;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    public class CalibrationResult
    {
        public string SavedFilePath { get; set; }
        public double RmsError { get; set; }
    }

    public class DetectionResult
    {
        public double[] Px { get; set; }
        public double[] Py { get; set; }
    }

    public interface ICalibrationService
    {
        /// <summary>
        /// 获取系统中所有标定方案配置
        /// </summary>
        Result<List<CalibrationProfile>> GetAllProfiles();
        /// <summary>
        /// 九点标定计算HomMat2D单应矩阵
        /// </summary>
        Result<CalibrationResult> CalcNinePointHomMat(double[] pixelXList, double[] pixelYList, double[] worldXList, double[] worldYList);

        /// <summary>
        /// 保存标定矩阵文件
        /// </summary>
        Result SaveHomMatFile(string sourceFilePath, string destFilePath);

        /// <summary>
        /// 标定板角点/圆点检测
        /// </summary>
        Result<DetectionResult> DetectCalibrationPoints(string imageFilePath);

        /// <summary>
        /// 建立位置补正
        /// </summary>
        Result<FixtureData> CreateFixture(double refRow, double refCol, double refAngle, double curRow, double curCol, double curAngle);

        /// <summary>
        /// 像素坐标转换物理坐标 (Pixel -> World)
        /// </summary>
        Result<(double WorldX, double WorldY)> MapPixelToWorld(string matrixFilePath, double px, double py);

        /// <summary>
        /// 物理坐标逆转换像素坐标 (World -> Pixel)
        /// </summary>
        Result<(double PixelX, double PixelY)> MapWorldToPixel(string matrixFilePath, double wx, double wy);
        /// <summary>
        /// 高级手眼坐标转换：考虑像素、物理旋转中心与角度补正
        /// </summary>
        Result<(double FinalWorldX, double FinalWorldY)> MapPixelToWorldWithOffset(
           string matrixFilePath,
           double px, double py,
           double rotateAngleDeg,
           double centerWx, double centerWy,
           EyeMode eyeMode,
           (double RobotX, double RobotY) currentRobotPos);

    }
}