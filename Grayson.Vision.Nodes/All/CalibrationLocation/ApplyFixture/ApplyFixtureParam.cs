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
    }
}