using System;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Measurement2D.BlobAnalysis
{
    /// <summary>
    /// Blob 分析节点（2026-09-26，梯队 B）：连通域拆分 → 面积筛选 → 计数。
    /// 输入 Region（上游阈值分割产物，如 ImageThreshold / 动态阈值暗极性），
    /// 输出筛选后连通域 + Count（double，失败时 NaN —— 判据纪律：算不出 ≠ 0，引擎见 NaN 直接 NG 不静默放行）。
    /// CountMin/CountMax 不在本节点判定，由引擎反射读取后按"个数区间"判 OK/NG。
    /// </summary>
    [Node(NodeType.BlobAnalysis, NodeCategory.Measurement2D, typeof(BlobAnalysisParam))]
    [NodePort("InputRegion", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    // 预览底图（可选）：接上游 OutputImage 后，预览/效果图带原图；不接则只画连通域
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("OutputRegions", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#F59E0B")]
    // ⚠ Count 必须 double：引擎 GetOutValue<double> 按 `is double` 匹配，装箱 int 会读成 0
    [NodePort("Count", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#F59E0B")]
    // 处理效果图输出：端口名含 "Image" → 帧事件自动推送上屏（借用输入图，叠加层随帧共存）
    [NodePort("BlobImage", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    public class BlobAnalysisExecutor : NodeExecutorBase<BlobAnalysisParam>
    {
        public const string PORT_IN_REGION = "InputRegion";
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_REGIONS = "OutputRegions";
        public const string PORT_OUT_COUNT = "Count";
        public const string PORT_OUT_IMAGE = "BlobImage";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, BlobAnalysisParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            object inputRegion = context.GetInputValue<object>(node, PORT_IN_REGION);
            object inputImage = context.GetInputValue<object>(node, PORT_IN_IMAGE);

            // 🌟 实时预览：底图（可选借用输入图）→ 连通域叠加 → 参数/计数标注
            var disp = context.Preview;
            disp?.BeginScene();
            if (inputImage != null) disp?.AddBorrowed(inputImage);

            if (inputRegion == null)
            {
                context.SetOutputValue(node, PORT_OUT_COUNT, double.NaN);
                context.Log("[" + node.DisplayName + "] 错误：未获取到输入 Region！请先接上游阈值分割节点（ImageThreshold 的 OutputRegion）。");
                disp?.AddText("⚠️ 未接输入 Region：请先接阈值分割节点", 12, 12, "red");
                return;
            }

            // 处理效果图输出（可选，同实例借用）
            if (inputImage != null)
                context.SetOutputValue(node, PORT_OUT_IMAGE, inputImage);

            // 连通域拆分
            var connRes = BlobTool.Connection(inputRegion);
            if (!connRes.Success)
            {
                context.SetOutputValue(node, PORT_OUT_COUNT, double.NaN);
                context.Log("[" + node.DisplayName + "] ❌ 连通域拆分失败: " + connRes.Message);
                disp?.AddText("⚠️ 连通域拆分失败: " + connRes.Message, 12, 12, "red");
                return;
            }

            // 面积筛选（小噪点丢弃）
            var selRes = BlobTool.SelectByFeature(connRes.Data, "area", param.AreaMin, param.AreaMax);
            NodePreviewHelper.DisposeQuietly(connRes.Data);   // 中间产物：拆分结果已被筛选消费，无论成败都释放
            connRes.Data = null;
            if (!selRes.Success)
            {
                context.SetOutputValue(node, PORT_OUT_COUNT, double.NaN);
                context.Log("[" + node.DisplayName + "] ❌ 面积筛选失败: " + selRes.Message);
                disp?.AddText("⚠️ 面积筛选失败: " + selRes.Message, 12, 12, "red");
                return;
            }

            // 计数
            var cntRes = BlobTool.CountObjects(selRes.Data);
            if (!cntRes.Success)
            {
                NodePreviewHelper.DisposeQuietly(selRes.Data);   // 不再往下传，防句柄泄漏
                context.SetOutputValue(node, PORT_OUT_COUNT, double.NaN);
                context.Log("[" + node.DisplayName + "] ❌ 连通域计数失败: " + cntRes.Message);
                disp?.AddText("⚠️ 连通域计数失败: " + cntRes.Message, 12, 12, "red");
                return;
            }

            int count = cntRes.Data;

            // 输出：连通域（所有权移交端口缓存）+ Count（⚠ 必须 double，见端口注释）
            context.SetOutputValue(node, PORT_OUT_REGIONS, selRes.Data);
            context.SetOutputValue(node, PORT_OUT_COUNT, (double)count);

            // 预览：筛选后连通域画显示副本（原对象归端口输出，副本归显示层）
            disp?.Add(NodePreviewHelper.CopyForDisplay(selRes.Data), "green", 2);
            disp?.AddText(
                $"Blob 数量={count}  面积[{param.AreaMin:F0}~{param.AreaMax:F0}]px²  判据[{param.CountMin}~{param.CountMax}]个",
                12, 12, "yellow");

            context.Log("[" + node.DisplayName + "] ✅ Blob 分析完成：检出 " + count +
                " 个连通域（面积 " + param.AreaMin + "~" + param.AreaMax + "px²）");
        }
    }
}
