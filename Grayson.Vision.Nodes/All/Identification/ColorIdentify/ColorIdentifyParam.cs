using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.ColorIdentify
{
    public class ColorIdentifyParam : ParamBase
    {
        /// <summary>调整 HSV 色彩范围时预览窗口实时显示颜色提取区域与面积占比。</summary>
        public override bool SupportsPreview => true;

        private double _hueMin = 0;
        public double HueMin { get => _hueMin; set => Set(ref _hueMin, value); }

        private double _hueMax = 20;
        public double HueMax { get => _hueMax; set => Set(ref _hueMax, value); }

        private double _satMin = 100;
        public double SatMin { get => _satMin; set => Set(ref _satMin, value); }

        private double _satMax = 255;
        public double SatMax { get => _satMax; set => Set(ref _satMax, value); }

        private double _valMin = 100;
        public double ValMin { get => _valMin; set => Set(ref _valMin, value); }

        private double _valMax = 255;
        public double ValMax { get => _valMax; set => Set(ref _valMax, value); }
    }
}