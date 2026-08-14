using System;

namespace Grayson.Vision.HalconWrapper.Identification
{
    public class BarcodeReadResult
    {
        public bool Success { get; set; }
        public string TextResult { get; set; } = string.Empty;
        public object BarcodeRegion { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public static class BarcodeTool
    {
        public static BarcodeReadResult ReadBarcode(object image, object region, int codeType, int maxCount, int timeoutMs)
        {
            // TODO: 调用 Halcon HBarcode / HDataCode2D API 识别
            // 此处为框架底座实现与空值安全防御
            if (image == null)
            {
                return new BarcodeReadResult { Success = false, Message = "输入图像为空" };
            }

            return new BarcodeReadResult
            {
                Success = true,
                TextResult = "SAMPLE_BARCODE_12345",
                BarcodeRegion = region,
                Message = "OK"
            };
        }
    }
}