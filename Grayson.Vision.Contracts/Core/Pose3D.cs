namespace Grayson.Vision.Contracts.Core
{
    /// <summary>
    /// 全局统一位姿结构体
    /// 单位：XYZ mm；Rx/Ry/Rz 角度制°，对接Halcon Pose、机器人、运动轴坐标
    /// 所有定位、机械手移动全部使用该结构，避免多套坐标格式
    /// </summary>
    public struct Pose3D
    {
        /// <summary>X轴平移</summary>
        public double X;
        /// <summary>Y轴平移</summary>
        public double Y;
        /// <summary>Z轴平移</summary>
        public double Z;
        /// <summary>绕X旋转</summary>
        public double Rx;
        /// <summary>绕Y旋转</summary>
        public double Ry;
        /// <summary>绕Z旋转</summary>
        public double Rz;

        public Pose3D(double x, double y, double z, double rx, double ry, double rz)
        {
            X = x;
            Y = y;
            Z = z;
            Rx = rx;
            Ry = ry;
            Rz = rz;
        }
    }
}