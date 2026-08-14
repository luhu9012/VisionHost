using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.ImageFilter
{
    public enum FilterMethod
    {
        Gauss,      // 高斯平滑
        Median,     // 中值去噪
        Erode,      // 形态学腐蚀
        Dilate,     // 形态学膨胀
        Open,       // 开运算
        Close       // 闭运算
    }

    public class ImageFilterParam : ParamBase
    {
        private FilterMethod _method = FilterMethod.Gauss;
        public FilterMethod Method
        {
            get => _method;
            set => Set(ref _method, value);
        }

        private int _kernelSize = 5;
        public int KernelSize
        {
            get => _kernelSize;
            set => Set(ref _kernelSize, value);
        }

        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(KernelSize) && KernelSize < 1)
                    return "滤波/形态学核大小必须大于等于 1";
                return null;
            }
        }
    }
}