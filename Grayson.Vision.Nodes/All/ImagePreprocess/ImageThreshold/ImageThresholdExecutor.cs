using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.ImageThreshold
{
    [Node(NodeType.ImageThreshold, NodeCategory.ImagePreprocess, typeof(ImageThresholdParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("OutputRegion", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    public class ImageThresholdExecutor : NodeExecutorBase<ImageThresholdParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_REGION = "OutputRegion";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ImageThresholdParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            object inputImage = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImage == null)
            {
                context.Log("[" + node.DisplayName + "] 错误：未获取到有效输入图像句柄！");
                return;
            }

            var res = ImagePreprocessTool.ApplyThreshold(
                inputImage,
                (int)param.Method,
                param.MinGray,
                param.MaxGray,
                param.DynamicMaskSize,
                param.DynamicOffset
            );

            if (res.Success)
            {
                context.SetOutputValue(node, PORT_OUT_REGION, res.Data);
                context.Log("[" + node.DisplayName + "] 阈值分割成功 (模式: " + param.Method + ")");
            }
            else
            {
                context.Log("[" + node.DisplayName + "] 阈值分割失败: " + res.Message);
            }
        }
    }
}