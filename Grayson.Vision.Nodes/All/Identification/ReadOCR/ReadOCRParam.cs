using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.ReadOCR
{
    public class ReadOCRParam : ParamBase
    {
        /// <summary>调整字体/笔画等参数时预览窗口实时显示字符区域与识别结果。</summary>
        public override bool SupportsPreview => true;

        private string _fontFileName = "Industrial_0-9A-Z.omc";
        public string FontFileName
        {
            get => _fontFileName;
            set => Set(ref _fontFileName, value);
        }

        private double _minStrokeWidth = 2.0;
        public double MinStrokeWidth
        {
            get => _minStrokeWidth;
            set => Set(ref _minStrokeWidth, value);
        }

        private string _expressionFilter = "*";
        public string ExpressionFilter
        {
            get => _expressionFilter;
            set => Set(ref _expressionFilter, value);
        }
    }
}