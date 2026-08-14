using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.DlInference
{
    public enum DlTaskType
    {
        ObjectDetection,  // YOLO 目标检测
        Classification,   // 图像分类
        Segmentation      // 缺陷分割
    }

    public class DlInferenceParam : ParamBase
    {
        private DlTaskType _taskType = DlTaskType.ObjectDetection;
        public DlTaskType TaskType
        {
            get => _taskType;
            set => Set(ref _taskType, value);
        }

        private string _modelPath = "models/yolov8_defect.onnx";
        public string ModelPath
        {
            get => _modelPath;
            set => Set(ref _modelPath, value);
        }

        private double _confidenceThreshold = 0.5;
        public double ConfidenceThreshold
        {
            get => _confidenceThreshold;
            set => Set(ref _confidenceThreshold, value);
        }

        private bool _useGpu = true;
        public bool UseGpu
        {
            get => _useGpu;
            set => Set(ref _useGpu, value);
        }
    }
}