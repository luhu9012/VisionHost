using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.ImageThreshold
{
    public enum ThresholdMethod
    {
        Fixed,      // 固定灰度阈值
        Dynamic,    // 自适应动态阈值
        Otsu        // Otsu 自动阈值
    }

    public class ImageThresholdParam : ParamBase
    {
        /// <summary>
        /// 阈值分割是实时预览的样板节点：拖动 MinGray/MaxGray 滑块时，
        /// 属性面板右侧预览窗口实时刷新分割区域（绿色）与参数标注。
        /// </summary>
        public override bool SupportsPreview => true;

        private ThresholdMethod _method = ThresholdMethod.Fixed;
        public ThresholdMethod Method
        {
            get => _method;
            set => Set(ref _method, value);
        }

        private int _minGray = 128;
        public int MinGray
        {
            get => _minGray;
            set => Set(ref _minGray, value);
        }

        private int _maxGray = 255;
        public int MaxGray
        {
            get => _maxGray;
            set => Set(ref _maxGray, value);
        }

        private int _dynamicMaskSize = 15;
        public int DynamicMaskSize
        {
            get => _dynamicMaskSize;
            set => Set(ref _dynamicMaskSize, value);
        }

        private int _dynamicOffset = 5;
        public int DynamicOffset
        {
            get => _dynamicOffset;
            set => Set(ref _dynamicOffset, value);
        }
    }
}