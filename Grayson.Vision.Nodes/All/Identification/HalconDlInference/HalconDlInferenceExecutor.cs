using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.HalconWrapper.Identification;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.HalconDlInference
{
    /// <summary>
    /// HALCON 原生 DL 推理节点（HTH《Demo_深度学习》翻译落地，stage9-2）。
    ///
    /// 【数据流】InputImage(HObject) → HalconDlNativeTool.RunInference
    ///   → 结构化结果(DetectionResults) + 缺陷Region(DefectRegion)
    ///
    /// 【输出端口】与 DlInference 节点完全同名同义：
    ///   - DetectionResults：摘要文本（分类 "ok: 99.1%" / 分割 "检出 破裂 3处…" / "未检出"），
    ///     供独立视觉引擎/下游逻辑节点做判据；
    ///   - DefectRegion：分割=缺陷区域并集（放大回原图尺寸）/ 分类=null。
    ///
    /// 【通道约定】C# + halcondotnet 直调 read_dl_model / apply_dl_model 等算子——
    ///   绝不加载/执行 .hdev 文件（HDevEngine 铁律），翻译即算子级翻译。
    /// </summary>
    [Node(NodeType.HalconDlInference, NodeCategory.Identification, typeof(HalconDlInferenceParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("DetectionResults", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#3498DB")]
    [NodePort("DefectRegion", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    public class HalconDlInferenceExecutor : NodeExecutorBase<HalconDlInferenceParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_RESULTS = "DetectionResults";
        public const string PORT_OUT_REGION = "DefectRegion";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, HalconDlInferenceParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            // ---------- 1) 取输入图像 ----------
            object inputImg = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImg == null)
            {
                // 🌟 失败可见化：图像源为空也要让"结果摘要"携带原因，否则独立引擎/CSV 只会记一个无信息空摘要
                string failText = "推理失败: 输入图像为空（请先运行上游读取节点/检查图像源目录）";
                context.SetOutputValue(node, PORT_OUT_RESULTS, failText);
                context.SetOutputValue(node, PORT_OUT_REGION, null);
                LogBus.Error("HalconDlInference", $"[{node.DisplayName}] {failText}");
                context.Log($"[{node.DisplayName}] 错误: 输入图像为空");
                Preview?.BeginScene();
                Preview?.AddText(failText, 12, 12, "red");
                return;
            }

            // ---------- 2) 组装推理请求（节点参数 → Halcon 原生工具） ----------
            var request = new HalconNativeDlRequest
            {
                HdlModelPath = param.HdlModelPath,
                PreprocessParamPath = param.PreprocessParamPath,
                TaskKind = (HalconDlTaskKind)(int)param.TaskType, // 枚举值一一对应
                ResizeEnabled = param.ResizeEnabled,
                ResizeWidth = param.ResizeWidth,
                ResizeHeight = param.ResizeHeight,
                ResizeInterpolation = param.ResizeInterpolation,
                DefectClassIds = ParseClassIds(param.DefectClassIdsText),
                MinDefectArea = param.MinDefectArea
            };

            // ---------- 3) 预览：先铺底图，推理结果出来再叠加 ----------
            Preview?.BeginScene();
            Preview?.AddBorrowed(inputImg);

            var res = HalconDlNativeTool.RunInference(inputImg, request);
            if (!res.Success)
            {
                // 🌟 失败可见化（2026-09-09 修复）：
                //   此前失败分支只写空端口+预览红字——引擎层判定"完成"→ 独立引擎/CSV 只能记
                //   "空摘要 + NG"，且不产生任何可诊断日志（7787/2106 等错误被吞）。
                //   现在把失败原因写入 DetectionResults（CSV 摘要列/判据可见）并走 Error 级日志，
                //   链不中断、工位不 Faulted，循环运行可持续观察每一周期错误。
                string failText = "推理失败: " + res.Message;
                context.SetOutputValue(node, PORT_OUT_RESULTS, failText);
                context.SetOutputValue(node, PORT_OUT_REGION, null);
                LogBus.Error("HalconDlInference", $"[{node.DisplayName}] {failText}");
                context.Log($"[{node.DisplayName}] HALCON DL 推理异常: {res.Message}");
                Preview?.AddText(failText, 12, 12, "red");
                return;
            }

            // ---------- 4) 输出结构化结果（端口语义与 DlInference 一致） ----------
            string summary = res.Summary ?? string.Empty;
            context.SetOutputValue(node, PORT_OUT_RESULTS, summary);
            context.SetOutputValue(node, PORT_OUT_REGION, res.HasRegion ? res.ResultRegion : null);
            context.Log($"[{node.DisplayName}] HALCON DL 推理完成({res.ElapsedMs:F0}ms): {summary}，检出数: {res.Count}");
            if (!string.IsNullOrWhiteSpace(res.CorrectionNote))
            {
                // 模型类型与节点参数不一致已被工具层自动校正：Warn 级留痕，便于发现过期配方
                LogBus.Warn("HalconDlInference", $"[{node.DisplayName}] {res.CorrectionNote} 摘要: {summary}");
            }

            // ---------- 5) 预览叠加：缺陷Region(红) + 结果文字 ----------
            if (res.HasRegion && res.ResultRegion != null)
            {
                Preview?.Add(NodePreviewHelper.CopyForDisplay(res.ResultRegion), "red", 2);
            }

            if (res.TaskKind == HalconDlTaskKind.Classification)
            {
                // 分类任务（镁片 ok/ng）：Count 恒为 1，不能照检测语义走"检出 N 处"，否则好件也红字。
                // 直接显示 Top-1 摘要（"ok: 72.8 %"），颜色按模型类名：ng 类红字、ok/其它绿字。
                // 词表与 StandaloneVisionProcess.TryClassifyLabelPrefix 兜底同源维护（分类 Top-1 非 ng 即视为 OK 类）。
                bool looksNg = LooksNgClassName(res.TopClassName);
                Preview?.AddText(string.IsNullOrWhiteSpace(res.Summary) ? "分类无输出" : res.Summary,
                    12, 12, looksNg ? "red" : "green");
            }
            else
            {
                Preview?.AddText(res.Count > 0
                    ? $"检出 {res.Count} 处: {summary} ({res.ElapsedMs:F0}ms)"
                    : $"未检出 ({res.ElapsedMs:F0}ms)",
                    12, 12, res.Count > 0 ? "red" : "green");
            }
        }

        /// <summary>分类 Top-1 类名是否命中 NG 语义词表（镁片模型类名 "ng"；与判据兜底同源维护）。</summary>
        private static bool LooksNgClassName(string topClassName)
        {
            if (string.IsNullOrWhiteSpace(topClassName)) return false;
            var t = topClassName.Trim();
            string[] ngWords = { "ng", "bad", "fail", "failed", "defect", "defective", "reject", "rejected", "ng", "异常", "不良", "缺陷" };
            string[] okWords = { "ok", "good", "pass", "passed", "合格", "良", "良品", "正常" };
            foreach (var w in ngWords)
                if (t.Equals(w, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var w in okWords)
                if (t.Equals(w, StringComparison.OrdinalIgnoreCase)) return false; // 显式 ok 类→绿
            return false; // 未知类名→乐观按 OK 显示
        }

        /// <summary>逗号分隔类别 ID 文本 → int 列表（"1,2" → [1,2]；空→[1,2] 兜底同示例）</summary>
        private static List<int> ParseClassIds(string text)
        {
            var list = (text ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => { int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v); return v; })
                .Where(v => v >= 0)
                .Distinct()
                .ToList();
            return list.Count > 0 ? list : new List<int> { 1, 2 };
        }
    }
}
