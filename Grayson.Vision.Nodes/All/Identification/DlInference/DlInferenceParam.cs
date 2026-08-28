using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.DlInference
{
    /// <summary>
    /// 任务类型（UI 展示用枚举，值与 Contracts.Ai.InferenceTaskType 保持一致，
    /// Executor 里直接按数值映射，避免 Nodes 层枚举与契约层耦合）。
    /// </summary>
    public enum DlTaskType
    {
        ObjectDetection = 1,  // YOLO 目标检测（1）
        Classification = 0,  // 图像分类（0）
        Segmentation = 2,  // 缺陷分割（2）
        AnomalyDetection = 3  // 异常检测（3）：无监督，只需 OK 样本训练（PatchCore/paDiM）
    }

    /// <summary>
    /// DL 推理节点参数。
    /// 【使用方法】
    ///   1. ModelPath 填 ONNX 模型路径（相对路径以程序运行目录为基准，如 models/defect.onnx）；
    ///   2. TaskType 按模型选：v5/v8 检测选 ObjectDetection，resnet 分类选 Classification，
    ///      unet 分割选 Segmentation，PatchCore 等无监督模型选 AnomalyDetection；
    ///   3. Labels 用英文逗号填类别名（顺序必须与训练时一致），如 "ok,scratch,dent"
    ///      （异常检测不需要填）；
    ///   4. LayoutHint 一般留 auto，检测框错乱时按模型填 yolov5 / yolov8；
    ///   5. 异常检测的"置信度阈值"含义不同：它是异常分数的 NG 判定线，
    ///      必须用 OK 样本分数标定（均值+3σ），不能沿用 0.5 默认值！
    ///      详见《AI模型训练与使用.md》第 4 章。
    /// </summary>
    public class DlInferenceParam : ParamBase
    {
        private DlTaskType _taskType = DlTaskType.ObjectDetection;
        /// <summary>AI 任务类型</summary>
        public DlTaskType TaskType
        {
            get => _taskType;
            set => Set(ref _taskType, value);
        }

        private string _modelPath = "models/yolov8_defect.onnx";
        /// <summary>ONNX 模型路径（相对路径以运行目录为基准）</summary>
        public string ModelPath
        {
            get => _modelPath;
            set => Set(ref _modelPath, value);
        }

        private string _labelsText = "";
        /// <summary>
        /// 类别标签表：逗号分隔，顺序与训练导出一致（如 "ok,scratch,dent"）。
        /// 留空则显示 class_0/class_1 ...
        /// </summary>
        public string LabelsText
        {
            get => _labelsText;
            set => Set(ref _labelsText, value);
        }

        private double _confidenceThreshold = 0.5;
        /// <summary>置信度阈值（低于该值的检出被丢弃；分割任务复用为像素二值化阈值；
        /// 异常检测任务 = 异常分数 NG 判定线，必须用 OK 样本标定，勿用默认 0.5）</summary>
        public double ConfidenceThreshold
        {
            get => _confidenceThreshold;
            set => Set(ref _confidenceThreshold, value);
        }

        private double _anomalyRegionThreshold = 0.5;
        /// <summary>异常检测专用：热力图二值化阈值 0~1（对归一化热力图取阈值生成缺陷 Region，
        /// 0.5≈高亮异常强度排名前一半的像素；仅 NG 时才输出 Region）</summary>
        public double AnomalyRegionThreshold
        {
            get => _anomalyRegionThreshold;
            set => Set(ref _anomalyRegionThreshold, value);
        }

        private double _iouThreshold = 0.45;
        /// <summary>NMS IoU 阈值（仅检测任务：同类别框重叠超过该值丢分低的）</summary>
        public double IouThreshold
        {
            get => _iouThreshold;
            set => Set(ref _iouThreshold, value);
        }

        private string _layoutHint = "auto";
        /// <summary>检测输出布局提示：auto / yolov5 / yolov8（检测框异常时手工指定）</summary>
        public string LayoutHint
        {
            get => _layoutHint;
            set => Set(ref _layoutHint, value);
        }

        private bool _useGpu = false;
        /// <summary>是否优先 GPU（当前 CPU 版 ONNX Runtime 插件忽略此参数，
        /// 接入 DirectML/TensorRT 包后生效）</summary>
        public bool UseGpu
        {
            get => _useGpu;
            set => Set(ref _useGpu, value);
        }

        /// <summary>DL 节点带图像预览（属性面板左参数/右预览两列布局）</summary>
        public override bool SupportsPreview => true;
    }
}
