using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.CreateFixture
{
    public class CreateFixtureParam : ParamBase
    {
        private double _baselineRow = 0.0;
        public double BaselineRow
        {
            get => _baselineRow;
            set => Set(ref _baselineRow, value);
        }

        private double _baselineCol = 0.0;
        public double BaselineCol
        {
            get => _baselineCol;
            set => Set(ref _baselineCol, value);
        }

        private double _baselineAngle = 0.0;
        public double BaselineAngle
        {
            get => _baselineAngle;
            set => Set(ref _baselineAngle, value);
        }
    }
}