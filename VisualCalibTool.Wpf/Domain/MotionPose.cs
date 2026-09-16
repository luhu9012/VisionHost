using System.Globalization;

namespace VisualCalibTool.Domain
{
    /// <summary>
    /// 标定用位姿：X / Y / Z（mm） + 旋转 U（度）。
    /// 放在 Domain 而非 Abstractions：它是纯数据，且 Imaging 层（合成相机）也要用它，
    /// 不能让低层反过来依赖防腐层。
    /// </summary>
    public struct MotionPose
    {
        public double X;
        public double Y;
        public double Z;
        public double U;

        public MotionPose(double x, double y, double z, double u)
        {
            X = x;
            Y = y;
            Z = z;
            U = u;
        }

        public Vec2 Xy
        {
            get { return new Vec2(X, Y); }
        }

        public bool IsFinite
        {
            get
            {
                return !double.IsNaN(X) && !double.IsInfinity(X)
                    && !double.IsNaN(Y) && !double.IsInfinity(Y)
                    && !double.IsNaN(Z) && !double.IsInfinity(Z)
                    && !double.IsNaN(U) && !double.IsInfinity(U);
            }
        }

        public static MotionPose FromXy(Vec2 xy, double z, double u)
        {
            return new MotionPose(xy.X, xy.Y, z, u);
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "X={0:F3} Y={1:F3} Z={2:F3} U={3:F3}", X, Y, Z, U);
        }
    }
}
