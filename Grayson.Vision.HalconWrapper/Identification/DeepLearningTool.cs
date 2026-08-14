using System;

namespace Grayson.Vision.HalconWrapper.Identification
{
    public class DlInferenceResult
    {
        public bool Success { get; set; }
        public object ResultsList { get; set; }
        public object SegmentedRegion { get; set; }
        public int Count { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public static class DeepLearningTool
    {
        public static DlInferenceResult RunInference(object image, string modelPath, int taskType, double confidenceThreshold, bool useGpu)
        {
            if (image == null)
            {
                return new DlInferenceResult { Success = false, Message = "输入图像为空" };
            }

            return new DlInferenceResult
            {
                Success = true,
                Count = 1,
                ResultsList = "Defect_Class_A (0.92)",
                Message = "OK"
            };
        }
    }
}