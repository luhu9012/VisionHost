using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Identification;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.ColorIdentify
{
    [Node(NodeType.ColorIdentify, NodeCategory.Identification, typeof(ColorIdentifyParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("SearchRegion", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    [NodePort("ColorRegion", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    [NodePort("AreaRatio", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E67E22")]
    public class ColorIdentifyExecutor : NodeExecutorBase<ColorIdentifyParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_IN_REGION = "SearchRegion";
        public const string PORT_OUT_REGION = "ColorRegion";
        public const string PORT_OUT_RATIO = "AreaRatio";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ColorIdentifyParam param, NodeExecutionContext context, CancellationToken token)
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

            var res = ColorTool.ExtractHsvRegion(inputImg, searchRegion, param.HueMin, param.HueMax, param.SatMin, param.SatMax, param.ValMin, param.ValMax);
            if (res.Success)
            {
                context.SetOutputValue(node, PORT_OUT_REGION, res.ResultRegion);
                context.SetOutputValue(node, PORT_OUT_RATIO, res.AreaRatio);
                context.Log($"[{node.DisplayName}] 颜色提取成功, 面积占比: {res.AreaRatio:F2}%");

                // 颜色结果区域（绿色）+ 参数/占比标注
                Preview?.Add(NodePreviewHelper.CopyForDisplay(res.ResultRegion), "green", 2);
                Preview?.AddText($"✅ 面积占比: {res.AreaRatio:F2}%  H:{param.HueMin}-{param.HueMax} S:{param.SatMin}-{param.SatMax} V:{param.ValMin}-{param.ValMax}", 12, 12, "yellow");
            }
            else
            {
                context.Log($"[{node.DisplayName}] 颜色识别提取失败: {res.Message}");
                Preview?.AddText("⚠️ 颜色提取失败: " + res.Message, 12, 12, "red");
            }
        }
    }
}