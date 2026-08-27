using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Identification;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.ReadOCR
{
    [Node(NodeType.ReadOCR, NodeCategory.Identification, typeof(ReadOCRParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("SearchRegion", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    [NodePort("OcrText", PortType.Out, PortCategory.Data, dataType: "string", colorHex: "#3498DB")]
    [NodePort("CharRegions", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    public class ReadOCRExecutor : NodeExecutorBase<ReadOCRParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_IN_REGION = "SearchRegion";
        public const string PORT_OUT_TEXT = "OcrText";
        public const string PORT_OUT_CHAR_REGIONS = "CharRegions";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ReadOCRParam param, NodeExecutionContext context, CancellationToken token)
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

            var ocrRes = OCRTool.RecognizeText(inputImg, searchRegion, param.FontFileName, param.MinStrokeWidth, param.ExpressionFilter);
            if (ocrRes.Success)
            {
                context.SetOutputValue(node, PORT_OUT_TEXT, ocrRes.TextResult);
                context.SetOutputValue(node, PORT_OUT_CHAR_REGIONS, ocrRes.CharRegions);
                context.Log($"[{node.DisplayName}] OCR 识别结果: {ocrRes.TextResult}");

                // 字符区域（绿色）+ 识别结果文本标注
                if (ocrRes.CharRegions != null)
                    Preview?.Add(NodePreviewHelper.CopyForDisplay(ocrRes.CharRegions), "green", 2);
                Preview?.AddText($"✅ OCR: {ocrRes.TextResult}", 12, 12, "green");
            }
            else
            {
                context.SetOutputValue(node, PORT_OUT_TEXT, string.Empty);
                context.Log($"[{node.DisplayName}] OCR 识别失败: {ocrRes.Message}");
                Preview?.AddText($"❌ OCR 识别失败: {ocrRes.Message}", 12, 12, "red");
            }
        }
    }
}