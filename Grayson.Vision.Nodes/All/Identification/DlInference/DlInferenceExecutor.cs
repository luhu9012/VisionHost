using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Ai;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.HalconWrapper.Identification;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.DlInference
{
    /// <summary>
    /// AI 深度学习推理节点（Step-1 落地版）。
    ///
    /// 【数据流】InputImage(HObject) → DeepLearningTool.RunInference
    ///   → 结构化结果(DetectionResults) + 缺陷Region(DefectRegion)
    ///
    /// 【输出端口】
    ///   - DetectionResults：摘要文本（分类 Top-1 / 检测框列表 "类别 置信度" /
    ///     异常检测 "异常分数 x → NG/OK"），供下游逻辑节点做字符串包含判断；
    ///   - DefectRegion：HALCON Region（检测=所有框并集 / 分割=缺陷区域放大到原图 /
    ///     异常检测=NG 时的异常热区，OK 时为 null），可直接接区域量测、面积筛选、显示类节点。
    ///
    /// 【线程模型】本 ExecuteCoreAsync 由流程引擎在后台算子线程调用，
    /// 推理与 HALCON 对象创建都不占 UI 线程；每步绘制经 Preview 上下文
    /// 自动 Invoke 到 UI 线程上屏（对齐标定采样的既有模型）。
    /// </summary>
    [Node(NodeType.DlInference, NodeCategory.Identification, typeof(DlInferenceParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("DetectionResults", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#3498DB")]
    [NodePort("DefectRegion", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    public class DlInferenceExecutor : NodeExecutorBase<DlInferenceParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_RESULTS = "DetectionResults";
        public const string PORT_OUT_REGION = "DefectRegion";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, DlInferenceParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            // ---------- 1) 取输入图像 ----------
            object inputImg = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImg == null)
            {
                context.Log($"[{node.DisplayName}] 错误: 输入图像为空");
                Preview?.BeginScene();
                Preview?.AddText("输入图像为空，请先运行上游节点", 12, 12, "red");
                return;
            }

            // ---------- 2) 组装推理请求 ----------
            var request = new DlInferenceRequest
            {
                ModelPath = param.ModelPath,
                TaskType = (InferenceTaskType)(int)param.TaskType, // 枚举值与契约层一一对应
                ConfidenceThreshold = param.ConfidenceThreshold,
                IouThreshold = param.IouThreshold,
                AnomalyRegionThreshold = param.AnomalyRegionThreshold,
                UseGpu = param.UseGpu,
                LayoutHint = param.LayoutHint,
                Labels = ParseLabels(param.LabelsText)
            };

            // ---------- 3) 预览：先铺底图，推理结果出来再叠加 ----------
            Preview?.BeginScene();
            Preview?.AddBorrowed(inputImg);

            var res = DeepLearningTool.RunInference(inputImg, request);
            if (!res.Success)
            {
                context.SetOutputValue(node, PORT_OUT_RESULTS, string.Empty);
                context.SetOutputValue(node, PORT_OUT_REGION, null);
                context.Log($"[{node.DisplayName}] AI 推理异常: {res.Message}");
                Preview?.AddText($"推理失败: {res.Message}", 12, 12, "red");
                return;
            }

            // ---------- 4) 输出结构化结果 ----------
            string summary = BuildSummary(res);
            context.SetOutputValue(node, PORT_OUT_RESULTS, summary);
            context.SetOutputValue(node, PORT_OUT_REGION, res.ResultRegion);
            context.Log($"[{node.DisplayName}] AI 推理完成({res.ElapsedMs:F0}ms): {summary}，检出数: {res.Count}");

            // ---------- 5) 预览叠加：缺陷Region(红) + 检测框标签 ----------
            if (res.HasRegion && res.ResultRegion != null)
            {
                // CopyForDisplay 生成显示副本，避免显示层持有业务对象所有权
                Preview?.Add(NodePreviewHelper.CopyForDisplay(res.ResultRegion), "red", 2);
            }

            // 检测任务：在每个框位置标注 "类别 置信度"
            if (res.Detections != null)
            {
                foreach (var box in res.Detections)
                {
                    Preview?.AddText($"{box.ClassName} {box.Score:P0}",
                        (box.X1 + box.X2) / 2f, Math.Max(2f, box.Y1 - 12f), "yellow");
                }
            }

            Preview?.AddText(res.Count > 0
                ? $"检出 {res.Count} 处: {summary} ({res.ElapsedMs:F0}ms)"
                : $"未检出 ({res.ElapsedMs:F0}ms)",
                12, 12, res.Count > 0 ? "red" : "green");
        }

        /// <summary>逗号分隔标签文本 → 列表（"ok,scratch,dent" → [ok, scratch, dent]）</summary>
        private static List<string> ParseLabels(string labelsText)
        {
            if (string.IsNullOrWhiteSpace(labelsText))
                return new List<string>();

            return labelsText
                .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        /// <summary>生成结果摘要文本（下游逻辑节点用字符串包含判断，如 Contains("scratch")）</summary>
        private static string BuildSummary(DlInferenceResult res)
        {
            switch (res.TaskType)
            {
                case InferenceTaskType.Classification:
                {
                    if (res.Classification == null || res.Classification.Count == 0) return "无分类输出";
                    var top = res.Classification[0];
                    return $"{top.ClassName}: {top.Score:P1}";
                }

                case InferenceTaskType.ObjectDetection:
                {
                    if (res.Detections == null || res.Detections.Count == 0) return "未检出";
                    var sb = new StringBuilder();
                    foreach (var b in res.Detections)
                    {
                        if (sb.Length > 0) sb.Append("; ");
                        sb.Append($"{b.ClassName} {b.Score:P0}");
                    }
                    return sb.ToString();
                }

                case InferenceTaskType.Segmentation:
                    return res.Message; // "检出缺陷区域" / "分割未检出缺陷区域"

                case InferenceTaskType.AnomalyDetection:
                    // Message 形如 "异常分数 0.832 ≥ 阈值 0.500 → NG"
                    // 下游判断节点可用 Contains("NG") / Contains("OK") 路由
                    return res.Message;

                default:
                    return res.Message;
            }
        }
    }
}
