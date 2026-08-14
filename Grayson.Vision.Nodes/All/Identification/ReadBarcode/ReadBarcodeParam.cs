using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.ReadBarcode
{
    public enum BarcodeType
    {
        Code128,
        Code39,
        EAN13,
        QRCode,
        DataMatrix,
        Auto
    }

    public class ReadBarcodeParam : ParamBase
    {
        private BarcodeType _codeType = BarcodeType.Auto;
        public BarcodeType CodeType
        {
            get => _codeType;
            set => Set(ref _codeType, value);
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
    }
}