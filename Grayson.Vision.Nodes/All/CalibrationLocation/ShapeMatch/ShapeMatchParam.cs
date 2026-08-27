using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.ShapeMatch
{
    public class ShapeMatchParam : ParamBase
    {
        /// <summary>调整最小分数等参数时预览窗口实时显示匹配位置十字与分数。</summary>
        public override bool SupportsPreview => true;

        private int _modelId = -1;
        public int ModelId
        {
            get => _modelId;
            set => Set(ref _modelId, value);
        }

        private double _minScore = 0.7;
        public double MinScore
        {
            get => _minScore;
            set => Set(ref _minScore, value);
        }

        private double _angleStart = -20.0;
        public double AngleStart
        {
            get => _angleStart;
            set => Set(ref _angleStart, value);
        }

        private double _angleEnd = 20.0;
        public double AngleEnd
        {
            get => _angleEnd;
            set => Set(ref _angleEnd, value);
        }
    }
}