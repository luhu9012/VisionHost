using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Ai;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Grayson.Vision.Plugins.Inference.OnnxRuntime
{
    /// <summary>
    /// 后处理辅助：把模型输出张量解析成契约层的 InferenceOutput。
    ///
    /// 【检测模型布局说明】（节点参数 LayoutHint 可强制指定，"auto" 自动判断）：
    ///   - YOLOv5 导出：输出 [1, N, 5+nc]，每行 = cx,cy,w,h,obj_conf,cls1..clsN
    ///   - YOLOv8/v11 导出：输出 [1, 4+nc, N]，每列 = cx,cy,w,h,cls1..clsN（无 obj 维）
    /// auto 判断规则：dim1 &lt; dim2 → v8 竖排（小维在前）；否则 v5 横排。
    ///
    /// 【坐标约定】模型输出的 cx,cy,w,h 是"网络输入尺寸"坐标系下的像素值，
    /// 这里统一换算回【原图】坐标（X/Y 独立缩放，配合"直接拉伸"的预处理方式），
    /// 上层节点拿到即可用，无需再关心缩放细节。
    /// </summary>
    internal static class PostprocessHelper
    {
        /// <summary>
        /// 统一解码入口（多输出版）。
        /// 【为什么改成数组】异常检测模型（anomalib 的 PatchCore 等）常为双输出：
        /// 热力图 [1,1,H,W] + 异常分数标量，单输出假设不成立；
        /// 其他任务仍然只取第一个输出，行为不变。
        /// </summary>
        /// <param name="outputs">模型全部 float 输出张量</param>
        /// <param name="options">模型选项（阈值/标签/布局提示）</param>
        /// <param name="srcW">原图宽（坐标换算目标）</param>
        /// <param name="srcH">原图高</param>
        /// <param name="netW">网络输入宽（Load 时从模型元数据解析）</param>
        /// <param name="netH">网络输入高</param>
        public static InferenceOutput Decode(
            Tensor<float>[] outputs, InferenceModelOptions options,
            int srcW, int srcH, int netW, int netH)
        {
            if (outputs == null || outputs.Length == 0)
                return InferenceOutput.Fail("模型没有 float 输出张量");

            switch (options.TaskType)
            {
                case InferenceTaskType.Classification:
                    return DecodeClassification(outputs[0], options);
                case InferenceTaskType.ObjectDetection:
                    return DecodeDetection(outputs[0], options, srcW, srcH, netW, netH);
                case InferenceTaskType.Segmentation:
                    return DecodeSegmentation(outputs[0], options);
                case InferenceTaskType.AnomalyDetection:
                    return DecodeAnomaly(outputs, options);
                default:
                    return InferenceOutput.Fail("暂不支持的任务类型: " + options.TaskType);
            }
        }

        // ------------------------------------------------------------------
        // 异常检测（无监督，只需 OK 样本训练）：
        //   - 双输出（anomalib PatchCore 导出惯例）：
        //       热力图 [1,1,H,W]（值越大越异常）+ 分数标量 [1]（整图异常度）
        //   - 单输出 [1,1,H,W]：只有热力图 → 分数取图中最大值
        //   - 单输出标量：只有分数 → 无热力图（只判 NG/OK，无区域输出）
        // 输出语义：
        //   AnomalyScore = 原始分数（无固定值域，需 OK 样本标定阈值）
        //   Segmentation = 热力图 min-max 归一化到 0~1 后的概率图（复用分割字段）
        //   IsAnomaly    = 分数 ≥ ConfidenceThreshold（异常任务的 NG 判定线）
        // ------------------------------------------------------------------
        private static InferenceOutput DecodeAnomaly(
            Tensor<float>[] outputs, InferenceModelOptions options)
        {
            // 按元素数分拣：最大的张量当热力图，恰好 1 个元素的是分数标量
            Tensor<float> mapTensor = null;
            Tensor<float> scoreTensor = null;

            foreach (var t in outputs)
            {
                if (t.Length == 1)
                {
                    scoreTensor = t;
                }
                else if (mapTensor == null || t.Length > mapTensor.Length)
                {
                    mapTensor = t; // 双输出时热力图元素数远大于分数；都非 1 元素时取最大者当图
                }
            }

            // 只有分数标量（无图）：直接判 NG/OK
            if (mapTensor == null)
            {
                double score = scoreTensor != null ? scoreTensor.ToArray()[0] : 0;
                bool isNg = score >= options.ConfidenceThreshold;
                return new InferenceOutput
                {
                    Success = true,
                    TaskType = InferenceTaskType.AnomalyDetection,
                    AnomalyScore = score,
                    IsAnomaly = isNg,
                    Message = isNg
                        ? string.Format("异常分数 {0:F3} ≥ 阈值 {1:F3} → NG", score, options.ConfidenceThreshold)
                        : string.Format("异常分数 {0:F3} < 阈值 {1:F3} → OK", score, options.ConfidenceThreshold)
                };
            }

            // 有热力图：解析尺寸（兼容 [1,1,H,W] / [1,H,W] / [H,W]）
            var dims = mapTensor.Dimensions;
            int maskW = 0, maskH = 0;
            if (dims.Length == 4) { maskH = dims[2]; maskW = dims[3]; }
            else if (dims.Length == 3) { maskH = dims[1]; maskW = dims[2]; }
            else if (dims.Length == 2) { maskH = dims[0]; maskW = dims[1]; }
            else
            {
                return InferenceOutput.Fail(
                    "异常检测热力图输出应为 [1,1,H,W]，实际: [" + string.Join(",", dims.ToArray()) + "]");
            }

            float[] raw = mapTensor.ToArray();
            int n = maskW * maskH;
            if (raw.Length < n) n = raw.Length; // 防御：导出模型带 batch 维时元素更多，取前 n 个

            // 分数：优先用模型专门的分数输出；没有则取热力图最大值（最异常像素的强度）
            float max = float.MinValue;
            for (int i = 0; i < raw.Length; i++) if (raw[i] > max) max = raw[i];
            double score2 = scoreTensor != null ? scoreTensor.ToArray()[0] : max;

            // 热力图 min-max 归一化到 0~1（复用 SegmentationMask 的"概率图"语义）
            float min = float.MaxValue;
            for (int i = 0; i < raw.Length; i++) if (raw[i] < min) min = raw[i];
            float range = max - min;
            var scoreMap = new float[n];
            for (int i = 0; i < n; i++)
            {
                scoreMap[i] = range > 1e-9f
                    ? Math.Max(0f, Math.Min(1f, (raw[i] - min) / range))
                    : 0f; // 全图同值（纯色图）时无异常梯度
            }

            bool isNg2 = score2 >= options.ConfidenceThreshold;
            return new InferenceOutput
            {
                Success = true,
                TaskType = InferenceTaskType.AnomalyDetection,
                AnomalyScore = score2,
                IsAnomaly = isNg2,
                Segmentation = new SegmentationMask
                {
                    MaskWidth = maskW,
                    MaskHeight = maskH,
                    ScoreMap = scoreMap,
                    // Region 二值化阈值用异常专用参数（对归一化热力图取阈值）
                    Threshold = Math.Max(0.01f, Math.Min(0.99f, options.AnomalyRegionThreshold))
                },
                Message = isNg2
                    ? string.Format("异常分数 {0:F3} ≥ 阈值 {1:F3} → NG", score2, options.ConfidenceThreshold)
                    : string.Format("异常分数 {0:F3} < 阈值 {1:F3} → OK", score2, options.ConfidenceThreshold)
            };
        }

        // ------------------------------------------------------------------
        // 分类：[1, nc]（或 [1, nc, 1, 1]）→ softmax → 全类别得分（降序）
        // ------------------------------------------------------------------
        private static InferenceOutput DecodeClassification(Tensor<float> tensor, InferenceModelOptions options)
        {
            // 一次性取连续内存快照（ORT 的 DenseTensor 行主序连续存储）
            float[] raw = tensor.ToArray();
            int nc = raw.Length;

            // softmax：即使模型导出时已带 softmax，再做一次不改变排序，
            // 且保证"概率"语义统一，上层可用固定阈值过滤
            float max = float.MinValue;
            for (int i = 0; i < nc; i++) if (raw[i] > max) max = raw[i];

            float sum = 0;
            var exp = new float[nc];
            for (int i = 0; i < nc; i++) { exp[i] = (float)Math.Exp(raw[i] - max); sum += exp[i]; }

            var labels = new List<ClassificationLabel>();
            for (int i = 0; i < nc; i++)
            {
                labels.Add(new ClassificationLabel
                {
                    ClassId = i,
                    ClassName = LabelOf(options, i),
                    Score = sum > 0 ? exp[i] / sum : 0
                });
            }
            labels.Sort((a, b) => b.Score.CompareTo(a.Score)); // Top-1 在最前

            return new InferenceOutput
            {
                Success = true,
                TaskType = InferenceTaskType.Classification,
                Classification = labels,
                Message = "OK"
            };
        }

        // ------------------------------------------------------------------
        // 检测：YOLOv5 / v8 输出 → 过滤置信度 → NMS → 原图坐标框
        // ------------------------------------------------------------------
        private static InferenceOutput DecodeDetection(
            Tensor<float> tensor, InferenceModelOptions options,
            int srcW, int srcH, int netW, int netH)
        {
            var dims = tensor.Dimensions;
            // 注意：Tensor.Dimensions 是 ReadOnlySpan<int>（结构体），
            // 没有 .Count，长度用 .Length；打印要先 .ToArray() 拷出来
            if (dims.Length != 3)
                return InferenceOutput.Fail(
                    "检测模型输出应为 3 维 [1,N,C] 或 [1,C,N]，实际: [" +
                    string.Join(",", dims.ToArray()) + "]（自定义检测头请扩展 DecodeDetection）");

            int dim1 = dims[1];
            int dim2 = dims[2];

            // 布局判断：显式提示优先；auto 时按"小维在前 = v8 竖排"
            string hint = (options.LayoutHint ?? "auto").ToLowerInvariant();
            bool transposed;
            if (hint == "yolov8" || hint == "v8") transposed = true;
            else if (hint == "yolov5" || hint == "v5") transposed = false;
            else transposed = dim1 < dim2;

            float[] raw = tensor.ToArray(); // 连续快照，行主序

            // v5 行结构 [cx,cy,w,h,obj,cls...]；v8 列结构每 anchor 取 4+nc
            int rowLen = transposed ? dim1 : dim2;   // v8: 4+nc；v5: 5+nc
            bool hasObjConf = !transposed;           // 约定：非转置布局按 v5（带 obj 维）解析
            int numClasses = transposed ? rowLen - 4 : rowLen - 5;
            if (numClasses <= 0)
                return InferenceOutput.Fail("类别数解析为 0，请检查 LayoutHint 设置（v5/v8）");

            // 线性下标辅助（raw 是 [1, dim1, dim2] 行主序展平）
            // transposed(v8): 数据按 [anchor 维在最后] 排 → raw[i * dim2 + j]，i=通道行、j=anchor
            // 非 transposed(v5): raw[j * dimLen + i]，j=anchor 行、i=列
            var boxes = new List<DetectionBox>();
            float conf = options.ConfidenceThreshold;
            float scaleX = (float)srcW / netW;  // 预处理是直接拉伸 → X/Y 独立缩放
            float scaleY = (float)srcH / netH;  // （将来改 letterbox 需在此加 padding 还原）

            for (int j = 0; j < dim2; j++)
            {
                // v8 竖排时 j 是 anchor；v5 横排时 j 是"每行宽度"维度，anchor 应遍历 dim1
                int anchors = transposed ? dim2 : dim1;
                if (j >= anchors) break;

                float bestScore = 0;
                int bestCls = -1;
                for (int c = 0; c < numClasses; c++)
                {
                    float score;
                    if (transposed)
                        score = raw[(4 + c) * dim2 + j];         // v8: cls 行 c、anchor 列 j
                    else
                        score = raw[j * rowLen + 5 + c] * raw[j * rowLen + 4]; // v5: obj×cls

                    if (score > bestScore) { bestScore = score; bestCls = c; }
                }

                if (bestScore < conf) continue;

                float cx, cy, w, h;
                if (transposed)
                {
                    cx = raw[0 * dim2 + j]; cy = raw[1 * dim2 + j];
                    w = raw[2 * dim2 + j]; h = raw[3 * dim2 + j];
                }
                else
                {
                    cx = raw[j * rowLen + 0]; cy = raw[j * rowLen + 1];
                    w = raw[j * rowLen + 2]; h = raw[j * rowLen + 3];
                }

                boxes.Add(new DetectionBox
                {
                    ClassId = bestCls,
                    ClassName = LabelOf(options, bestCls),
                    Score = bestScore,
                    X1 = (cx - w / 2f) * scaleX,
                    Y1 = (cy - h / 2f) * scaleY,
                    X2 = (cx + w / 2f) * scaleX,
                    Y2 = (cy + h / 2f) * scaleY
                });
            }

            // NMS 按类别去重（不同类别框允许互相重叠）
            var kept = NonMaxSuppression(boxes, options.IouThreshold);

            return new InferenceOutput
            {
                Success = true,
                TaskType = InferenceTaskType.ObjectDetection,
                Detections = kept,
                Message = kept.Count > 0 ? "OK" : "未检出目标"
            };
        }

        /// <summary>贪婪 NMS：按分数降序保留，同类别 IoU 超阈值的丢弃</summary>
        private static List<DetectionBox> NonMaxSuppression(List<DetectionBox> boxes, float iouThreshold)
        {
            var result = new List<DetectionBox>();
            var sorted = boxes.OrderByDescending(b => b.Score).ToList();

            while (sorted.Count > 0)
            {
                var best = sorted[0];
                result.Add(best);
                sorted.RemoveAt(0);

                sorted.RemoveAll(b => b.ClassId == best.ClassId && ComputeIou(best, b) > iouThreshold);
            }
            return result;
        }

        private static float ComputeIou(DetectionBox a, DetectionBox b)
        {
            float x1 = Math.Max(a.X1, b.X1);
            float y1 = Math.Max(a.Y1, b.Y1);
            float x2 = Math.Min(a.X2, b.X2);
            float y2 = Math.Min(a.Y2, b.Y2);

            float inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            if (inter <= 0) return 0;

            float areaA = Math.Max(0, a.X2 - a.X1) * Math.Max(0, a.Y2 - a.Y1);
            float areaB = Math.Max(0, b.X2 - b.X1) * Math.Max(0, b.Y2 - b.Y1);
            return inter / (areaA + areaB - inter);
        }

        // ------------------------------------------------------------------
        // 分割：[1,1,H,W] / [1,nc,H,W] 概率图（网络输入分辨率，上层负责映射回原图）
        // ------------------------------------------------------------------
        private static InferenceOutput DecodeSegmentation(Tensor<float> tensor, InferenceModelOptions options)
        {
            var dims = tensor.Dimensions;

            int maskW, maskH, channels;
            if (dims.Length == 4)
            {
                channels = dims[1]; maskH = dims[2]; maskW = dims[3];      // [1,C,H,W]
            }
            else if (dims.Length == 3)
            {
                channels = dims[0]; maskH = dims[1]; maskW = dims[2];      // [C,H,W]
            }
            else
            {
                return InferenceOutput.Fail(
                    "分割模型输出应为 [1,C,H,W]，实际: [" + string.Join(",", dims.ToArray()) + "]");
            }

            float[] raw = tensor.ToArray();
            int n = maskW * maskH;
            var scoreMap = new float[n];

            if (channels <= 1)
            {
                // 单通道：值即缺陷概率；若值域越界说明导出的是 logits，补 sigmoid
                bool needSigmoid = false;
                for (int i = 0; i < n; i++)
                {
                    if (raw[i] < -0.001f || raw[i] > 1.001f) { needSigmoid = true; break; }
                }
                for (int i = 0; i < n; i++)
                {
                    float v = raw[i];
                    scoreMap[i] = needSigmoid ? 1f / (1f + (float)Math.Exp(-v))
                                              : Math.Max(0, Math.Min(1, v));
                }
            }
            else
            {
                // 多通道：逐像素取各类最大值当作缺陷概率
                // 【简化】单类缺陷够用；需要"每类一个 mask"时 TODO 扩展
                for (int i = 0; i < n; i++)
                {
                    float best = float.MinValue;
                    for (int c = 0; c < channels; c++)
                        best = Math.Max(best, raw[c * n + i]);
                    scoreMap[i] = Math.Max(0, Math.Min(1, best));
                }
            }

            return new InferenceOutput
            {
                Success = true,
                TaskType = InferenceTaskType.Segmentation,
                Segmentation = new SegmentationMask
                {
                    MaskWidth = maskW,
                    MaskHeight = maskH,
                    ScoreMap = scoreMap,
                    // 分割的"阈值"复用置信度参数（0~1 概率二值化界线）
                    Threshold = Math.Max(0.01f, Math.Min(0.99f, options.ConfidenceThreshold))
                },
                Message = "OK"
            };
        }

        private static string LabelOf(InferenceModelOptions options, int classId)
        {
            if (options.Labels != null && classId >= 0 && classId < options.Labels.Count)
                return options.Labels[classId];
            return "class_" + classId;
        }
    }
}
