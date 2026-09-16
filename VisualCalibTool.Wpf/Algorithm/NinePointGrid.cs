using System;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// 九点网格规划。★ 偏移公式必须与主项目 <c>CalibrationWizardViewModel.TryGetNinePointTarget</c> 一致，
    /// 否则嵌入后"规划的点"和主项目的点不是同一批：
    ///
    ///     row = (i-1) / 3      ∈ {0,1,2}
    ///     col = (i-1) % 3      ∈ {0,1,2}
    ///     offset = ( (col-1)*StepX , (row-1)*StepY )
    ///     P_i    = Base + offset
    ///
    /// 于是 #1 = 左下(−X,−Y)、#5 = 中心、#9 = 右上(+X,+Y)。
    /// </summary>
    public static class NinePointGrid
    {
        public const int PointCount = 9;

        /// <summary>螺旋顺序：从中心出发（#5 先走，方便先确认中心和视野）。</summary>
        public static readonly int[] SpiralOrder = new int[] { 5, 6, 3, 2, 1, 4, 7, 8, 9 };

        /// <summary>逐行顺序 1..9。</summary>
        public static readonly int[] RowMajorOrder = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

        /// <summary>
        /// 生成九个点的<b>偏移量</b>（相对基准位），按编号 1..9 排列。
        /// </summary>
        /// <param name="mirroredX">是否翻转 X（某些安装方向下主项目会翻）。</param>
        public static Vec2[] BuildOffsets(double stepX, double stepY, bool mirroredX = false)
        {
            var result = new Vec2[PointCount];
            for (int i = 1; i <= PointCount; i++)
            {
                int row = (i - 1) / 3;
                int col = (i - 1) % 3;

                double dx = (col - 1) * stepX;
                double dy = (row - 1) * stepY;
                if (mirroredX)
                {
                    dx = -dx;
                }

                // ★ 注意：点 #1 在 offset 上是 (dx=-stepX, dy=-stepY)，
                //   但世界 Y 的"上下"取决于工作台安装方向。这里只给数学偏移，
                //   方向语义交给拓扑/SafetyGuard 判断，避免在算法层埋一个方向假设。
                result[i - 1] = new Vec2(dx, dy);
            }

            return result;
        }

        /// <summary>生成九个实际目标位（基准位 + 偏移，Z/U 保持不变）。</summary>
        public static MotionPose[] BuildTargets(Vec2 baseXy, double z, double u, double stepX, double stepY, bool mirroredX = false)
        {
            Vec2[] offsets = BuildOffsets(stepX, stepY, mirroredX);
            var targets = new MotionPose[PointCount];
            for (int i = 0; i < PointCount; i++)
            {
                targets[i] = new MotionPose(baseXy.X + offsets[i].X, baseXy.Y + offsets[i].Y, z, u);
            }

            return targets;
        }

        /// <summary>
        /// 网格中心到原点的半径（相对机器人原点）—— 可达性分析用。
        /// </summary>
        public static double CenterRadiusFromOrigin(Vec2 baseXy)
        {
            return baseXy.Length;
        }

        /// <summary>
        /// ★ 最近角点半径（保守下界）= 中心半径 − 步长×√2。
        ///   九点里离原点最近的角点大约在这个半径上，规划步长时必须先算（内圈第一杀手）。
        /// </summary>
        public static double NearestCornerRadius(Vec2 baseXy, double stepX, double stepY)
        {
            return baseXy.Length - Math.Sqrt(stepX * stepX + stepY * stepY);
        }
    }
}
