using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Ai
{
    /// <summary>
    /// AI 推理任务类型。
    /// 【说明】为什么不用 Nodes 项目里的 DlTaskType 枚举？
    /// 因为 Contracts 是零依赖的最顶层契约层，插件（如 OnnxRuntime 插件）只引用 Contracts，
    /// 不能反向引用 Nodes，所以推理任务类型必须定义在契约层，Nodes 层的枚举只负责 UI 展示。
    /// </summary>
    public enum InferenceTaskType
    {
        /// <summary>图像分类：整图判定 OK/NG 或缺陷类别（如 resnet / yolov8-cls）</summary>
        Classification = 0,

        /// <summary>目标检测：输出缺陷框 + 类别 + 置信度（如 yolov5/v8-det）</summary>
        ObjectDetection = 1,

        /// <summary>语义分割：输出每个像素属于缺陷的概率（如 yolov8-seg / unet）</summary>
        Segmentation = 2,

        /// <summary>异常检测（无监督）：只用 OK 样本训练（如 anomalib 的 PatchCore / paDiM），
        /// 推理输出"异常分数 + 热力图"，工业现场缺陷样本最难收集，这个方向落地价值最大。
        /// 输出约定：双输出模型（热力图 [1,1,H,W] + 分数标量）或单输出热力图（分数取图最大值）均可。</summary>
        AnomalyDetection = 3
    }

    /// <summary>
    /// 输入图像的像素排布格式。
    /// 【注意】HALCON 的 HObject 图像取出像素后统一转成 HWC（高×宽×通道）的 float 数组，
    /// 值域保持 0~255（原始灰度/RGB 值），归一化（减均值除方差、缩放到 0~1 或 -1~1）
    /// 由推理插件按模型要求自己完成——契约层只传"原图数据"，不带模型相关的预处理。
    /// </summary>
    public enum InferencePixelFormat
    {
        /// <summary>单通道灰度图，Data 长度 = Width × Height</summary>
        Gray = 0,

        /// <summary>RGB 三通道彩色图（HWC 排布），Data 长度 = Width × Height × 3</summary>
        Rgb = 1
    }
}
