using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.HalconWrapper;
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
                Preview?.BeginScene();
                Preview?.AddText("⚠️ 输入图像为空，请先运行上游节点", 12, 12, "red");
                return;
            }

            // 预览：以输入图为底图，滤波后叠加输出图对比
            Preview?.BeginScene();
            Preview?.AddBorrowed(inputImage);

            var res = ImagePreprocessTool.ApplyFilter(inputImage, (int)param.Method, param.KernelSize);
            if (res.Success)
            {
                context.SetOutputValue(node, PORT_OUT_IMAGE, res.Data);
                context.Log("[" + node.DisplayName + "] 图像滤波成功 (方式: " + param.Method + ", 核大小: " + param.KernelSize + ")");

                // 滤波输出图以显示副本提交（原对象留端口管线），半透明叠加对比
                Preview?.Add(NodePreviewHelper.CopyForDisplay(res.Data), "yellow", 1);
                Preview?.AddText($"滤波: {param.Method}  核大小: {param.KernelSize}", 12, 12, "yellow");
            }
            else
            {
                context.Log("[" + node.DisplayName + "] 滤波失败: " + res.Message);
                Preview?.AddText("⚠️ 滤波失败: " + res.Message, 12, 12, "red");
            }
        }
    }
}