using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.NccMatch
{
    public class NccMatchParam : ParamBase
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
    }
}