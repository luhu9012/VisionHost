using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.ReadOCR
{
    /// <summary>
    /// OCR 前景极性。HALCON 自带分类器（Industrial_* 等）按【白底黑字】训练，
    /// 亮字符暗底必须先反相，否则整篇识别错（课程 6OCR num.hdev 实证）。
    /// </summary>
    public enum OcrPolarity
    {
        /// <summary>暗字符亮底（默认）</summary>
        Dark,
        /// <summary>亮字符暗底（自动 invert_image）</summary>
        Light
    }

    public class ReadOCRParam : ParamBase
    {
        /// <summary>调整字体/笔画等参数时预览窗口实时显示字符区域与识别结果。</summary>
        public override bool SupportsPreview => true;

        /// <summary>
        /// 分类器 .omc：可填文件名（如 Industrial_0-9A-Z_NoRej.omc，会在
        /// Config\Ocr → Assets\Ocr → HALCON 安装 ocr\ 三级回退查找）或绝对路径。
        /// ★ 带 NoRej 的分类器不会拒识（遇没训过的字会给错答案且高置信度）；
        ///   带 Rej 的会拒识（更适合"读不出就该报 NG"的追溯场景）。
        /// </summary>
        private string _fontFileName = "Industrial_0-9A-Z_NoRej.omc";
        public string FontFileName
        {
            get => _fontFileName;
            set => Set(ref _fontFileName, value);
        }

        /// <summary>
        /// 膨胀半径（像素）：字体笔画中间有孔洞时膨胀填满，提高分类器命中率。
        /// 课程案例用 2；&lt;=0 表示不膨胀。
        /// </summary>
        private double _minStrokeWidth = 2.0;
        public double MinStrokeWidth
        {
            get => _minStrokeWidth;
            set => Set(ref _minStrokeWidth, value);
        }

        /// <summary>结果字符过滤：null/空/"*"=不过滤；"0-9" 这类区间白名单；其它按正则保留匹配。</summary>
        private string _expressionFilter = "*";
        public string ExpressionFilter
        {
            get => _expressionFilter;
            set => Set(ref _expressionFilter, value);
        }

        /// <summary>二值化上限（默认 71，与课程 6OCR num.hdev 一致：白底黑字取暗字符）。</summary>
        private double _thresholdMax = 71;
        public double ThresholdMax
        {
            get => _thresholdMax;
            set => Set(ref _thresholdMax, value);
        }

        /// <summary>字符面积下限（滤噪点）。</summary>
        private double _areaMin = 30;
        public double AreaMin
        {
            get => _areaMin;
            set => Set(ref _areaMin, value);
        }

        /// <summary>字符面积上限（滤大块背景）。</summary>
        private double _areaMax = 70000;
        public double AreaMax
        {
            get => _areaMax;
            set => Set(ref _areaMax, value);
        }

        private OcrPolarity _polarity = OcrPolarity.Dark;
        public OcrPolarity Polarity
        {
            get => _polarity;
            set => Set(ref _polarity, value);
        }

        /// <summary>排序方式：false=只按列（单行文本，默认）；true=先行后列（多行文本）。</summary>
        private bool _sortByRow = false;
        public bool SortByRow
        {
            get => _sortByRow;
            set => Set(ref _sortByRow, value);
        }

        /// <summary>
        /// 置信度门限（0~1）。低于它判失败。
        /// ★ 课程核心教训：do_ocr_multi_class_mlp 对未训练字符不拒绝，照样给高置信度，
        ///   所以"识别成功"≠"结果对"。追溯场景建议设 0.5~0.7（NoRej 分类器）或换 Rej 分类器。
        /// </summary>
        private double _minConfidence = 0.0;
        public double MinConfidence
        {
            get => _minConfidence;
            set => Set(ref _minConfidence, value);
        }
    }
}
