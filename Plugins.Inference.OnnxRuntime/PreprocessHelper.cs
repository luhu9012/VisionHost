using System;
using Grayson.Vision.Contracts.Ai;

namespace Grayson.Vision.Plugins.Inference.OnnxRuntime
{
    /// <summary>
    /// 预处理辅助：契约层的 InferenceInput（HWC、float 0~255）
    /// → 模型输入张量（缩放 + 通道适配 + 归一化 + NCHW/NHWC 排布）。
    ///
    /// 【为什么放在插件里而不是契约层】预处理参数（均值/方差/输入尺寸）是
    /// "模型的要求"而不是"图像的属性"，属于推理引擎实现细节，契约层只传原图。
    /// </summary>
    internal static class PreprocessHelper
    {
        /// <summary>
        /// 把输入图像变成模型要求的张量数据。
        /// 步骤：通道适配（灰度↔RGB）→ 双线性缩放到 netW×netH → 减均值除方差 → 按排布打包。
        /// </summary>
        public static float[] ToModelTensor(
            InferenceInput input, int netW, int netH, int netChannels,
            InferenceTaskType taskType, float[] mean, float[] std, bool isNhwc)
        {
            int srcW = input.Width;
            int srcH = input.Height;
            float[] src = input.Pixels;
            int srcChannels = input.Format == InferencePixelFormat.Gray ? 1 : 3;

            // ---------- 1) 通道适配：先统一成"模型通道数"的 HWC 源数据 ----------
            // 灰度图 + 3 通道模型：灰度复制 3 份
            // 彩色图 + 1 通道模型：按 BT.601 亮度加权转灰度
            float[] adapted;
            if (srcChannels == netChannels)
            {
                adapted = src;
            }
            else if (srcChannels == 1 && netChannels == 3)
            {
                adapted = new float[srcW * srcH * 3];
                int n = srcW * srcH;
                for (int i = 0; i < n; i++)
                {
                    adapted[i * 3] = src[i];
                    adapted[i * 3 + 1] = src[i];
                    adapted[i * 3 + 2] = src[i];
                }
            }
            else // rgb -> gray
            {
                adapted = new float[srcW * srcH];
                int n = srcW * srcH;
                for (int i = 0; i < n; i++)
                {
                    adapted[i] = src[i * 3] * 0.299f + src[i * 3 + 1] * 0.587f + src[i * 3 + 2] * 0.114f;
                }
            }

            // ---------- 2) 归一化参数兜底（模型没显式配 mean/std 时按惯例取） ----------
            float[] effMean = mean;
            float[] effStd = std;
            if (effMean == null || effStd == null)
            {
                // 分类模型惯例：ImageNet 统计值
                // 异常检测（anomalib）默认 transform 同样是 ImageNet 归一化
                // YOLO 系（检测/分割）惯例：只做 /255 缩放到 0~1（mean=0, std=255）
                bool isYoloStyle = taskType == InferenceTaskType.ObjectDetection
                                || taskType == InferenceTaskType.Segmentation;
                effMean = effMean ?? (isYoloStyle ? new[] { 0f, 0f, 0f } : new[] { 123.675f, 116.28f, 103.53f });
                effStd = effStd ?? (isYoloStyle ? new[] { 255f, 255f, 255f } : new[] { 58.395f, 57.12f, 57.375f });
            }

            // ---------- 3) 逐通道双线性缩放 + 归一化 ----------
            // 输出 planar（CHW）布局：先缩放到临时 HWC，再打包时转 NCHW/NHWC
            var resized = new float[netChannels * netW * netH];
            float xRatio = (float)srcW / netW;
            float yRatio = (float)srcH / netH;

            for (int c = 0; c < netChannels; c++)
            {
                for (int y = 0; y < netH; y++)
                {
                    // 源图采样坐标（中心对齐，与 OpenCV resize 行为一致）
                    float sy = (y + 0.5f) * yRatio - 0.5f;
                    int y0 = (int)Math.Floor(sy);
                    float fy = sy - y0;
                    int y1 = y0 + 1;
                    if (y0 < 0) { y0 = 0; fy = 0; }
                    if (y1 >= srcH) { y1 = srcH - 1; }

                    for (int x = 0; x < netW; x++)
                    {
                        float sx = (x + 0.5f) * xRatio - 0.5f;
                        int x0 = (int)Math.Floor(sx);
                        float fx = sx - x0;
                        int x1 = x0 + 1;
                        if (x0 < 0) { x0 = 0; fx = 0; }
                        if (x1 >= srcW) { x1 = srcW - 1; }

                        // 双线性插值（4 个邻点加权）
                        float p00 = adapted[(y0 * srcW + x0) * netChannels + c];
                        float p01 = adapted[(y0 * srcW + x1) * netChannels + c];
                        float p10 = adapted[(y1 * srcW + x0) * netChannels + c];
                        float p11 = adapted[(y1 * srcW + x1) * netChannels + c];

                        float v = p00 * (1 - fx) * (1 - fy) + p01 * fx * (1 - fy)
                                + p10 * (1 - fx) * fy + p11 * fx * fy;

                        // 归一化：(v - mean) / std
                        // 【注意】单通道模型只使用 mean/std 的第 0 个分量
                        resized[c * netW * netH + y * netW + x] = (v - effMean[Math.Min(c, effMean.Length - 1)])
                                                                  / effStd[Math.Min(c, effStd.Length - 1)];
                    }
                }
            }

            // ---------- 4) 排布打包 ----------
            // resized 目前是 CHW；NCHW 直接加一个 batch 维即复用；
            // NHWC 需要重排（罕见路径，TFLite 转换模型才用）
            if (!isNhwc)
                return resized;

            var nhwc = new float[netChannels * netW * netH];
            for (int c = 0; c < netChannels; c++)
                for (int y = 0; y < netH; y++)
                    for (int x = 0; x < netW; x++)
                        nhwc[(y * netW + x) * netChannels + c] = resized[c * netW * netH + y * netW + x];
            return nhwc;
        }
    }
}
