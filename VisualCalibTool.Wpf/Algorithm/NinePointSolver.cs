using System;
using System.Collections.Generic;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// 九点标定求解器：像素 → 世界（法兰命令位域）的 6 参数仿射 H。
    ///
    /// ★ 真实做法（不是"拍一整块标定板"）：机械手走 9 个位置，每次都拍<b>同一个固定不动的 Mark</b>，
    ///   每次得到 1 个像素点 u 与 1 个机械手反馈位 P，凑 9 对 → 最小二乘拟合 H，使得 H(u) = P。
    /// </summary>
    public static class NinePointSolver
    {
        /// <summary>最少需要的可用点数（仿射每行 3 个未知量）。</summary>
        public const int MinUsablePoints = 3;

        /// <summary>低于该点数时直接拒绝（无法判断形状失真，属于"算出来也不敢用"）。</summary>
        public const int RecommendedPoints = 3;

        /// <summary>
        /// 解算 H。
        /// ★ 真值必须取 <see cref="CalibSample.FeedbackXy"/>（控制器反馈位），不是下发值。
        /// </summary>
        public static NinePointResult Solve(IList<CalibSample> samples, CalibTopology topology)
        {
            var result = new NinePointResult();

            if (samples == null)
            {
                result.Error = CalibError.Create(CalibFailureKind.SolveFailed, "采样集为空");
                return result;
            }

            var usable = CollectUsable(samples);
            if (usable.Count < MinUsablePoints)
            {
                result.Error = CalibError.Create(
                    CalibFailureKind.SolveFailed,
                    string.Format("可用采样点不足：{0} 个（至少需要 {1} 个）", usable.Count, MinUsablePoints));
                return result;
            }

            // 两行独立解算：Px = h11·ux + h12·uy + h13 ；Py = h21·ux + h22·uy + h23
            var rows = new List<double[]>(usable.Count);
            var rhsX = new List<double>(usable.Count);
            var rhsY = new List<double>(usable.Count);
            for (int i = 0; i < usable.Count; i++)
            {
                var s = usable[i];
                rows.Add(new double[] { s.Pixel.X, s.Pixel.Y, 1.0 });
                rhsX.Add(s.FeedbackXy.X);
                rhsY.Add(s.FeedbackXy.Y);
            }

            double[] sx = GeometryUtil.SolveLeastSquares(rows, rhsX, 3);
            double[] sy = GeometryUtil.SolveLeastSquares(rows, rhsY, 3);
            if (sx == null || sy == null)
            {
                result.Error = CalibError.Create(
                    CalibFailureKind.SolveFailed,
                    "正规方程奇异 —— 采样点在像素空间共线或重复，轨迹规划有问题（检查步长是否为 0）");
                return result;
            }

            var h = new HomMat2D(sx[0], sx[1], sx[2], sy[0], sy[1], sy[2]);
            if (!h.IsFinite)
            {
                result.Error = CalibError.Create(CalibFailureKind.SolveFailed, "解算结果含 NaN/Inf");
                return result;
            }

            result.H = h;

            // 残差（世界坐标，mm）
            var residuals = new double[usable.Count];
            for (int i = 0; i < usable.Count; i++)
            {
                Vec2 mapped = h.Transform(usable[i].Pixel);
                residuals[i] = mapped.DistanceTo(usable[i].FeedbackXy);
            }

            result.ResidualsMm = residuals;
            result.RmsMm = GeometryUtil.Rms(residuals);
            result.NearestCornerRadiusMm = ComputeNearestCornerRadius(topology);
            result.Success = true;
            return result;
        }

        /// <summary>取出可用于解算的采样点（按原始序号升序）。</summary>
        public static List<CalibSample> CollectUsable(IList<CalibSample> samples)
        {
            var list = new List<CalibSample>();
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] != null && samples[i].IsUsable)
                {
                    list.Add(samples[i]);
                }
            }

            list.Sort(CompareByIndex);
            return list;
        }

        private static int CompareByIndex(CalibSample a, CalibSample b)
        {
            return a.Index.CompareTo(b.Index);
        }

        /// <summary>
        /// 最近角点半径（mm）= 网格中心到机器人原点的距离 − 步长×√2。
        /// ★ 这是<b>保守下界</b>：九点里离原点最近的角点半径，规划步长时必须先算这个
        ///   （内圈可达性的第一杀手）。公式只此一份，见 <see cref="NinePointGrid.NearestCornerRadius"/>。
        /// </summary>
        public static double ComputeNearestCornerRadius(CalibTopology topology)
        {
            if (topology == null)
            {
                return 0.0;
            }

            return NinePointGrid.NearestCornerRadius(topology.BasePosXY, topology.StepX, topology.StepY);
        }
    }
}
