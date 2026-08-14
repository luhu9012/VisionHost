using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.AffineImage
{
    public class AffineImageParam : ParamBase
    {
        private double _centerRow = 500;
        public double CenterRow
        {
            get => _centerRow;
            set => Set(ref _centerRow, value);
        }

        private double _centerCol = 500;
        public double CenterCol
        {
            get => _centerCol;
            set => Set(ref _centerCol, value);
        }

        private double _angleDegree = 0.0;
        public double AngleDegree
        {
            get => _angleDegree;
            set => Set(ref _angleDegree, value);
        }
    }
}