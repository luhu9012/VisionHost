using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.ApplyFixture
{
    public enum FollowType
    {
        Point,
        Region
    }

    public class ApplyFixtureParam : ParamBase
    {
        private FollowType _targetType = FollowType.Point;
        public FollowType TargetType
        {
            get => _targetType;
            set => Set(ref _targetType, value);
        }

        private double _baseRow = 0.0;
        public double BaseRow
        {
            get => _baseRow;
            set => Set(ref _baseRow, value);
        }

        private double _baseCol = 0.0;
        public double BaseCol
        {
            get => _baseCol;
            set => Set(ref _baseCol, value);
        }

        // ── 角度跟随（2026-09-25 测量链补全）──
        // 背景：本节点原先只跟随"点/Region"，被测要素若带方向（如 FitLine 的边缘走向），
        // 工件一转就抓不到边。补上角度的"基准值 + Δθ"跟随：
        //   模板坐标系下的方向角填 BaseAngle，运行时把 CreateFixture 的 DeltaAngle 接到
        //   DeltaAngle 输入端口，输出端口 OutputAngle = BaseAngle − Δθ —— 与位置跟随同源同矩阵。
        //   ★ 是"减"不是"加"：HALCON 的朝向角与 vector_angle_to_rigid 的旋转手性相反，
        //     见 ApplyFixtureExecutor.ExecuteCoreAsync 内的实测证据（写成"+"误差 = 2Δθ）。
        private double _baseAngle = 0.0;
        /// <summary>基准朝向（°）：基准（模板）坐标系下被测要素的方向角；配合 DeltaAngle 输入得到跟随后角度</summary>
        public double BaseAngle
        {
            get => _baseAngle;
            set => Set(ref _baseAngle, value);
        }
    }
}