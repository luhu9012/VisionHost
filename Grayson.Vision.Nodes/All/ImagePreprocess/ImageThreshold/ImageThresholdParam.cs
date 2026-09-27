using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.ImageThreshold
{
    public enum ThresholdMethod
    {
        Fixed,      // 固定灰度阈值
        Dynamic,    // 自适应动态阈值
        Otsu        // Otsu 自动阈值
    }

    /// <summary>动态阈值极性：检出比局部均值亮（Light，亮缺陷）还是暗（Dark，暗划痕/缺料）。</summary>
    public enum DynPolarity
    {
        Light,      // 亮缺陷（默认，历史行为）
        Dark        // 暗缺陷（划痕/脏污/缺料最常见）
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

        // 2026-09-26：原先算子层写死 "light"，暗划痕/暗缺陷永远提不出来——补成可配参数
        private DynPolarity _polarity = DynPolarity.Light;
        public DynPolarity Polarity
        {
            get => _polarity;
            set => Set(ref _polarity, value);
        }
    }
}