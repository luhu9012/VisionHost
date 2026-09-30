using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.ReadBarcode
{
    /// <summary>
    /// 条码/二维码码制。★ 枚举整数值与 HalconWrapper 的 BarcodeKind 一一对应（勿调序）。
    /// 一维码（Code128/Code39/EAN13/UPC）走 find_bar_code；
    /// 二维码（QRCode/DataMatrix/Aztec/PDF417）走 find_data_code_2d。
    /// </summary>
    public enum BarcodeType
    {
        /// <summary>自动：一维码用 'auto'（多码制通用，较慢）；二维码默认按 QR Code 模型</summary>
        Auto,
        Code128,
        Code39,
        EAN13,
        QRCode,
        DataMatrix,
        UPC,
        Aztec,
        PDF417
    }

    /// <summary>条码极性（二维码专用；一维码默认暗条亮底）。</summary>
    public enum BarcodePolarity
    {
        /// <summary>任意（HALCON 'any'）：正反都试，最省心但略慢</summary>
        Any,
        /// <summary>黑码白底（HALCON 'dark_on_light'）：最常见</summary>
        DarkOnLight,
        /// <summary>白码黑底（HALCON 'light_on_dark'）：课程案例里 qr1.png 即此</summary>
        LightOnDark
    }

    public class ReadBarcodeParam : ParamBase
    {
        /// <summary>调整码制/超时等参数时预览窗口实时显示条码区域与识别结果。</summary>
        public override bool SupportsPreview => true;

        private BarcodeType _codeType = BarcodeType.Auto;
        public BarcodeType CodeType
        {
            get => _codeType;
            set => Set(ref _codeType, value);
        }

        private BarcodePolarity _polarity = BarcodePolarity.Any;
        public BarcodePolarity Polarity
        {
            get => _polarity;
            set => Set(ref _polarity, value);
        }

        private int _maxCount = 1;
        public int MaxCount
        {
            get => _maxCount;
            set => Set(ref _maxCount, value);
        }

        private int _timeoutMs = 1000;
        public int TimeoutMs
        {
            get => _timeoutMs;
            set => Set(ref _timeoutMs, value);
        }

        /// <summary>
        /// 条/码元最小像素宽（一维码 element_size_min）。0=用 HALCON 默认值（偏保守）。
        /// ★ 小码/远距离码必须调小（如 1.5~2），否则 find_bar_code 直接找不到。
        /// </summary>
        private double _elementSizeMin = 0;
        public double ElementSizeMin
        {
            get => _elementSizeMin;
            set => Set(ref _elementSizeMin, value);
        }
    }
}
