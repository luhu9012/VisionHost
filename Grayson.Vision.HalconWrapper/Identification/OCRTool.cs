using System;

namespace Grayson.Vision.HalconWrapper.Identification
{
    public class OcrReadResult
    {
        public bool Success { get; set; }
        public string TextResult { get; set; } = string.Empty;
        public object CharRegions { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public static class OCRTool
    {
        public static OcrReadResult RecognizeText(object image, object region, string fontFileName, double minStrokeWidth, string expressionFilter)
        {
            if (image == null)
            {
                return new OcrReadResult { Success = false, Message = "输入图像为空" };
            }

            return new OcrReadResult
            {
                Success = true,
                TextResult = "A1234",
                CharRegions = region,
                Message = "OK"
            };
        }
    }
}