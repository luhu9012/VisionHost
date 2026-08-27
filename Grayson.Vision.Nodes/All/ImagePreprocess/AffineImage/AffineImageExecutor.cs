using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.AffineImage
{
    [Node(NodeType.AffineImage, NodeCategory.ImagePreprocess, typeof(AffineImageParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("OutputImage", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    public class AffineImageExecutor : NodeExecutorBase<AffineImageParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_IMAGE = "OutputImage";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, AffineImageParam param, NodeExecutionContext context, CancellationToken token)
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

            // 预览：以输入图为底图，标注旋转中心和角度
            Preview?.BeginScene();
            Preview?.AddBorrowed(inputImage);
            Preview?.AddCross(param.CenterRow, param.CenterCol, 30, "red");

            var res = ImagePreprocessTool.ApplyAffineRotate(inputImage, param.CenterRow, param.CenterCol, param.AngleDegree);
            if (res.Success)
            {
                context.SetOutputValue(node, PORT_OUT_IMAGE, res.Data);
                context.Log("[" + node.DisplayName + "] 图像仿射矫正成功 (旋转角度: " + param.AngleDegree + "°)");

                // 仿射输出图以显示副本叠加对比
                Preview?.Add(NodePreviewHelper.CopyForDisplay(res.Data), "cyan", 1);
                Preview?.AddText($"仿射旋转: {param.AngleDegree}°  中心: ({param.CenterRow:F0}, {param.CenterCol:F0})", 12, 12, "yellow");
            }
            else
            {
                context.Log("[" + node.DisplayName + "] 图像仿射矫正失败: " + res.Message);
                Preview?.AddText("⚠️ 仿射矫正失败: " + res.Message, 12, 12, "red");
            }
        }
    }
}