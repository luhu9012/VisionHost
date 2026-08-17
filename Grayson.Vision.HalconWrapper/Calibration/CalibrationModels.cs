using System;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    /// <summary>
    /// 标定类型枚举
    /// </summary>
    public enum CalibrationType
    {
        NinePointHandEye,      // 1. 标准九点手眼标定 (仅 X/Y 平移矩阵)
        HandEyeWithRotation,   // 2. 12点/15点标定 (9点平移 + N点 R轴旋转中心拟合)
        Checkerboard2D,        // 3. 2D 棋盘格/畸变矫正标定
        PixelScale             // 4. 简单像素比例标定
    }

    /// <summary>
    /// 标定数据存储模型
    /// </summary>
    public class CalibrationProfileModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "默认标定方案";
        public CalibrationType Type { get; set; } = CalibrationType.NinePointHandEye;
        public string BoundDeviceOrStation { get; set; } = "Cam1 / Station1";

        /// <summary>重投影均方根误差 (RMS) mm</summary>
        public double RmsError { get; set; }

        /// <summary>标定矩阵存储路径 (.tup)</summary>
        public string MatrixFilePath { get; set; }

        /// <summary>最后标定时间</summary>
        public DateTime LastCalibratedTime { get; set; }

        /// <summary>是否有效标定</summary>
        public bool IsCalibrated => !string.IsNullOrEmpty(MatrixFilePath) && System.IO.File.Exists(MatrixFilePath);
    }
}