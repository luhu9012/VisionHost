using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Identification;
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
                return;
            }

            object searchRegion = context.GetInputValue<object>(node, PORT_IN_REGION);

            var readRes = BarcodeTool.ReadBarcode(inputImg, searchRegion, (int)param.CodeType, param.MaxCount, param.TimeoutMs);
            if (readRes.Success)
            {
                context.SetOutputValue(node, PORT_OUT_TEXT, readRes.TextResult);
                context.SetOutputValue(node, PORT_OUT_REGION, readRes.BarcodeRegion);
                context.Log($"[{node.DisplayName}] 识别成功: {readRes.TextResult}");
            }
            else
            {
                context.SetOutputValue(node, PORT_OUT_TEXT, string.Empty);
                context.Log($"[{node.DisplayName}] 条码识别失败或未找到符合要求的条码: {readRes.Message}");
            }
        }
    }
}