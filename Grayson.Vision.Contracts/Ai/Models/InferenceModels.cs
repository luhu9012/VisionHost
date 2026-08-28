using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Ai
{
    /// <summary>
    /// 模型加载配置：节点参数 → 此对象 → IInferenceProvider.Load()。
    /// 集中描述"一个模型文件 + 它的推理要求"，插件按需取用。
    /// </summary>
    public class InferenceModelOptions
    {
        /// <summary>ONNX 模型文件绝对路径（如 models/yolov8_defect.onnx）。
        /// 【约定】相对路径以程序运行目录（AppDomain.BaseDirectory）为基准解析。</summary>
        public string ModelPath { get; set; }

        /// <summary>任务类型（分类/检测/分割），决定后处理方式</summary>
        public InferenceTaskType TaskType { get; set; }

        /// <summary>类别标签表：ClassId → 名称。如 { "ok", "scratch", "dent" }。
        /// 【约定】可选；与模型导出时的类别顺序一致。为空时显示 "class_&lt;id&gt;"。</summary>
        public List<string> Labels { get; set; } = new List<string>();

        /// <summary>模型输入边长（正方形输入）。0 = 自动从 ONNX 元数据读取。</summary>
        public int InputSize { get; set; }

        /// <summary>归一化均值（RGB 顺序，值域 0~255 意义下）。为 null 时按任务类型取默认值：
        /// 分类=ImageNet(123.675,116.28,103.53)；YOLO 检测/分割=(0,0,0) 即仅缩放。</summary>
        public float[] Mean { get; set; }

        /// <summary>归一化方差（RGB 顺序，值域 0~255 意义下）。为 null 时取默认值：
        /// 分类=(58.395,57.12,57.375)；YOLO=(255,255,255) 即缩放到 0~1。</summary>
        public float[] Std { get; set; }

        /// <summary>置信度阈值：
        /// 分类/检测/分割 = 常规含义（低于丢弃）；
        /// 异常检测 = 异常分数 NG 判定线（分数 ≥ 该值判 NG），
        /// 建议用 OK 样本分数的"均值 + 3σ"标定后填入（见模型训练文档）。</summary>
        public float ConfidenceThreshold { get; set; } = 0.5f;

        /// <summary>异常检测专用：热力图二值化阈值（0~1，对"归一化后的热力图"取阈值，
        /// 0.5 ≈ 高亮异常强度排名前一半的像素作为缺陷 Region）。其他任务忽略。</summary>
        public float AnomalyRegionThreshold { get; set; } = 0.5f;

        /// <summary>NMS IoU 阈值：检测框两两重叠超过该值时只保留分高者</summary>
        public float IouThreshold { get; set; } = 0.45f;

        /// <summary>是否优先使用 GPU。当前 CPU 版 ONNX Runtime 插件忽略此参数。
        /// 【TODO】接入 DirectML/TensorRT 版 Microsoft.ML.OnnxRuntime 后生效</summary>
        public bool UseGpu { get; set; }

        /// <summary>检测输出张量布局提示：
        /// "auto"=按形状自动判断（[1,N,6+nc]=v5 横排 / [1,4+nc,N]=v8 竖排）；
        /// 也可显式填 "yolov5" 或 "yolov8"，用于 auto 判断歧义时手工指定。</summary>
        public string LayoutHint { get; set; } = "auto";
    }

    /// <summary>
    /// 一次推理的输入图像。纯 float 数组 + 尺寸描述，与任何图像库（HALCON/OpenCV/Bitmap）解耦，
    /// 由 HalconWrapper 层负责把 HObject 拆成这个结构（见 DeepLearningTool.cs 的图像桥接代码）。
    /// </summary>
    public class InferenceInput
    {
        /// <summary>图像宽（像素）</summary>
        public int Width { get; set; }

        /// <summary>图像高（像素）</summary>
        public int Height { get; set; }

        /// <summary>像素格式（灰度 / RGB）</summary>
        public InferencePixelFormat Format { get; set; }

        /// <summary>
        /// 像素数据，HWC 排布（先按行、再按列、最后按通道），float 值域 0~255。
        /// 【性能提示】此数组是"拷贝出的托管副本"，与 HALCON 原生内存无关，
        /// 插件可以放心跨线程持有（HALCON 的原生指针在它的 GC 期间可能被移动，绝不能直接持有）。
        /// </summary>
        public float[] Pixels { get; set; }

        /// <summary>构造便捷方法</summary>
        public static InferenceInput Create(int width, int height, InferencePixelFormat format, float[] pixels)
        {
            return new InferenceInput { Width = width, Height = height, Format = format, Pixels = pixels };
        }
    }

    /// <summary>单个检测框（坐标已换算回【原图】像素坐标系，插件负责从模型输入尺寸还原）</summary>
    public class DetectionBox
    {
        /// <summary>类别索引（对应 Labels）</summary>
        public int ClassId { get; set; }

        /// <summary>类别名称</summary>
        public string ClassName { get; set; }

        /// <summary>置信度 0~1</summary>
        public float Score { get; set; }

        /// <summary>左上角 X（原图像素）</summary>
        public float X1 { get; set; }

        /// <summary>左上角 Y（原图像素）</summary>
        public float Y1 { get; set; }

        /// <summary>右下角 X（原图像素）</summary>
        public float X2 { get; set; }

        /// <summary>右下角 Y（原图像素）</summary>
        public float Y2 { get; set; }

        public override string ToString()
        {
            return string.Format("{0} ({1:P1}) [{2:F0},{3:F0},{4:F0},{5:F0}]",
                ClassName ?? ("class_" + ClassId), Score, X1, Y1, X2, Y2);
        }
    }

    /// <summary>分类结果中的单个类别得分</summary>
    public class ClassificationLabel
    {
        public int ClassId { get; set; }
        public string ClassName { get; set; }
        public float Score { get; set; }
    }

    /// <summary>
    /// 分割结果：与模型输出同尺寸的概率图（后续可换算成 HALCON Region 显示/量测）。
    /// 【注意】ScoreMap 尺寸 = MaskWidth × MaskHeight，是【模型输出】分辨率
    /// （通常等于输入尺寸如 640×640，不等于原图），由上层负责按比例缩放映射回原图。
    /// </summary>
    public class SegmentationMask
    {
        public int MaskWidth { get; set; }
        public int MaskHeight { get; set; }

        /// <summary>每个像素属于"缺陷"的概率 0~1（单类分割；多类分割 TODO 扩展）</summary>
        public float[] ScoreMap { get; set; }

        /// <summary>渲染 Region 时使用的二值化阈值（默认 0.5）</summary>
        public float Threshold { get; set; } = 0.5f;
    }

    /// <summary>
    /// 一次推理的统一输出。无论什么任务类型，字段不适用处为 null/空集合，
    /// 调用方（节点/工具类）按 TaskType 取对应字段即可。
    /// </summary>
    public class InferenceOutput
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;

        /// <summary>本次推理耗时（毫秒），节点日志会输出，方便性能调优</summary>
        public double ElapsedMs { get; set; }

        public InferenceTaskType TaskType { get; set; }

        /// <summary>分类任务：按得分降序的全部类别（调用方自取 Top-1 / Top-K）</summary>
        public List<ClassificationLabel> Classification { get; set; }

        /// <summary>检测任务：已过置信度阈值 + NMS 去重后的检测框</summary>
        public List<DetectionBox> Detections { get; set; }

        /// <summary>分割任务：像素级概率图（异常检测任务也复用此字段放归一化热力图）</summary>
        public SegmentationMask Segmentation { get; set; }

        /// <summary>异常检测任务：整图异常分数（模型原始值，越大越异常；
        /// 无监督模型的分数没有固定值域，必须用 OK 样本标定阈值，不能拍脑袋填 0.5）</summary>
        public double AnomalyScore { get; set; }

        /// <summary>异常检测任务：分数是否超过阈值判为 NG</summary>
        public bool IsAnomaly { get; set; }

        /// <summary>失败时构造便捷方法</summary>
        public static InferenceOutput Fail(string message)
        {
            return new InferenceOutput { Success = false, Message = message };
        }
    }
}
