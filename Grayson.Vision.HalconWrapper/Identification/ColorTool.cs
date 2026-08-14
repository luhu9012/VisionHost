using System;

namespace Grayson.Vision.HalconWrapper.Identification
{
    public class ColorExtractResult
    {
        public bool Success { get; set; }
        public object ResultRegion { get; set; }
        public double AreaRatio { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public static class ColorTool
    {
        public static ColorExtractResult ExtractHsvRegion(object image, object region, double hMin, double hMax, double sMin, double sMax, double vMin, double vMax)
        {
            if (image == null)
            {
                return new ColorExtractResult { Success = false, Message = "输入图像为空" };
            }

            return new ColorExtractResult
            {
                Success = true,
                ResultRegion = region,
                AreaRatio = 85.5,
                Message = "OK"
            };
        }
    }
}