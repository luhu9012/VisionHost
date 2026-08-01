using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Nodes.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.ImageInput.ImageChannel
{
    [Node(NodeType.ImageChannel, NodeCategory.ImageInput, typeof(ImageChannelParam))]
    [NodePort("ImageIn", PortType.In, PortCategory.Data, dataType: "Image", colorHex: "#05668D")]
    [NodePort("Channel1", PortType.Out, PortCategory.Data, dataType: "Image", colorHex: "#E74C3C")]
    [NodePort("Channel2", PortType.Out, PortCategory.Data, dataType: "Image", colorHex: "#2ECC71")]
    [NodePort("Channel3", PortType.Out, PortCategory.Data, dataType: "Image", colorHex: "#3498DB")]
    public class ImageChannelExecutor : NodeExecutorBase<ImageChannelParam>
    {
        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ImageChannelParam param, NodeExecutionContext context, CancellationToken token)
        {
            var srcImage = context.GetInputValue<object>(node, "ImageIn");
            context.Log($"[通道拆分] 开始对图像进行 {param.ColorSpace} 色彩空间 {param.OperationType} 操作...");

            await Task.Yield();

            context.SetOutputValue(node, "Channel1", $"{srcImage}_Ch1");
            context.SetOutputValue(node, "Channel2", $"{srcImage}_Ch2");
            context.SetOutputValue(node, "Channel3", $"{srcImage}_Ch3");

            context.Log($"[通道拆分] 通道拆分输出完成");
        }
    }
}