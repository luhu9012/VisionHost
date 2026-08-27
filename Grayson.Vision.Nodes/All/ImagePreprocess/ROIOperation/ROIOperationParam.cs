using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.ROIOperation
{
    public enum RoiOpType
    {
        Crop,           // ROI 图像裁剪
        GenerateMask,   // 生成 Mask 掩膜
        Intersection,   // ROI 集合交集
        Union,          // ROI 集合并集
        Difference      // ROI 集合差集
    }

    public class ROIOperationParam : ParamBase
    {
        /// <summary>ROI 运算参数调整时预览窗口实时显示裁剪框/区域集合运算结果。</summary>
        public override bool SupportsPreview => true;

        private RoiOpType _opType = RoiOpType.Crop;
        public RoiOpType OpType
        {
            get => _opType;
            set => Set(ref _opType, value);
        }

        // 裁剪坐标参数
        private double _row1 = 100;
        public double Row1 { get => _row1; set => Set(ref _row1, value); }

        private double _col1 = 100;
        public double Col1 { get => _col1; set => Set(ref _col1, value); }

        private double _row2 = 500;
        public double Row2 { get => _row2; set => Set(ref _row2, value); }

        private double _col2 = 500;
        public double Col2 { get => _col2; set => Set(ref _col2, value); }
    }
}