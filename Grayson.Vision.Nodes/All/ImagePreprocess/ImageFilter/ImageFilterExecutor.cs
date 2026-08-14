using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.ImageFilter
{
    [Node(NodeType.ImageFilter, NodeCategory.ImagePreprocess, typeof(ImageFilterParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("OutputImage", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    public class ImageFilterExecutor : NodeExecutorBase<ImageFilterParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_IMAGE = "OutputImage";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ImageFilterParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            object inputImage = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImage == null)
            {
                context.Log("[" + node.DisplayName + "] 错误：未获取到有效输入图像句柄！");
                return;
            }

            var res = ImagePreprocessTool.ApplyFilter(inputImage, (int)param.Method, param.KernelSize);
            if (res.Success)
            {
                context.SetOutputValue(node, PORT_OUT_IMAGE, res.Data);
                context.Log("[" + node.DisplayName + "] 图像滤波成功 (方式: " + param.Method + ", 核大小: " + param.KernelSize + ")");
            }
            else
            {
                context.Log("[" + node.DisplayName + "] 滤波失败: " + res.Message);
            }
        }
    }
}