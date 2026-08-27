using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Identification;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.ReadBarcode
{
    [Node(NodeType.ReadBarcode, NodeCategory.Identification, typeof(ReadBarcodeParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("SearchRegion", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    [NodePort("BarcodeText", PortType.Out, PortCategory.Data, dataType: "string", colorHex: "#3498DB")]
    [NodePort("ResultRegion", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    public class ReadBarcodeExecutor : NodeExecutorBase<ReadBarcodeParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_IN_REGION = "SearchRegion";
        public const string PORT_OUT_TEXT = "BarcodeText";
        public const string PORT_OUT_REGION = "ResultRegion";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ReadBarcodeParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            object inputImg = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImg == null)
            {
                context.Log($"[{node.DisplayName}] 错误: 输入图像为空");
                Preview?.BeginScene();
                Preview?.AddText("⚠️ 输入图像为空，请先运行上游节点", 12, 12, "red");
                return;
            }

            object searchRegion = context.GetInputValue<object>(node, PORT_IN_REGION);

            // 预览：输入图为底图 + 搜索区域（蓝色，若有）
            Preview?.BeginScene();
            Preview?.AddBorrowed(inputImg);
            if (searchRegion != null)
                Preview?.Add(NodePreviewHelper.CopyForDisplay(searchRegion), "blue", 1);

            var readRes = BarcodeTool.ReadBarcode(inputImg, searchRegion, (int)param.CodeType, param.MaxCount, param.TimeoutMs);
            if (readRes.Success)
            {
                context.SetOutputValue(node, PORT_OUT_TEXT, readRes.TextResult);
                context.SetOutputValue(node, PORT_OUT_REGION, readRes.BarcodeRegion);
                context.Log($"[{node.DisplayName}] 识别成功: {readRes.TextResult}");

                // 条码区域（品红色）+ 识别结果文本标注
                if (readRes.BarcodeRegion != null)
                    Preview?.Add(NodePreviewHelper.CopyForDisplay(readRes.BarcodeRegion), "magenta", 2);
                Preview?.AddText($"✅ [{param.CodeType}] {readRes.TextResult}", 12, 12, "green");
            }
            else
            {
                context.SetOutputValue(node, PORT_OUT_TEXT, string.Empty);
                context.Log($"[{node.DisplayName}] 条码识别失败或未找到符合要求的条码: {readRes.Message}");
                Preview?.AddText($"❌ 识别失败 [{param.CodeType}]: {readRes.Message}", 12, 12, "red");
            }
        }
    }
}