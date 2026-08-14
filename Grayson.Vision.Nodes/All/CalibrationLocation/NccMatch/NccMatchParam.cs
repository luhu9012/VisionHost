using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.NccMatch
{
    public class NccMatchParam : ParamBase
    {
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