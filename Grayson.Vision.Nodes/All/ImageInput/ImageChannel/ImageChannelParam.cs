using Grayson.Vision.Contracts.ViewModels;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImageInput.ImageChannel
{
    public enum ColorSpaceType
    {
        RGB = 0,
        HSV = 1
    }

    public class ImageChannelParam : ParamBase
    {
        private ColorSpaceType _colorSpace = ColorSpaceType.RGB;
        /// <summary>
        /// 色彩空间：RGB / HSV
        /// </summary>
        public ColorSpaceType ColorSpace
        {
            get => _colorSpace;
            set => Set(ref _colorSpace, value);
        }

        private int _selectedChannelIndex = 0;
        /// <summary>
        /// 拆分提取的通道索引 (0: R/H, 1: G/S, 2: B/V)
        /// </summary>
        public int SelectedChannelIndex
        {
            get => _selectedChannelIndex;
            set => Set(ref _selectedChannelIndex, value);
        }

        public override string this[string columnName]
        {
            get
            {
                if (SelectedChannelIndex < 0 || SelectedChannelIndex > 2)
                    return "通道索引超出合法范围 (0-2)";
                return null;
            }
        }
    }
}