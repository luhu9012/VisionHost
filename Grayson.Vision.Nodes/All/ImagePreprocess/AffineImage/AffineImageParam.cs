using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.AffineImage
{
    public class AffineImageParam : ParamBase
    {
        /// <summary>调整旋转角度/中心时预览窗口实时显示仿射效果与旋转中心十字。</summary>
        public override bool SupportsPreview => true;

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