using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.ImageInput.AcquireImage
{
    [Node(NodeType.AcquireImage, NodeCategory.ImageInput, typeof(AcquireImageParam))]
    [NodePort("Image", PortType.Out, PortCategory.Data, dataType: "Image", colorHex: "#9B59B6")]
    public class AcquireImageExecutor : NodeExecutorBase<AcquireImageParam>
    {
        public const string PORT_OUT_IMAGE = "Image";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, AcquireImageParam param, NodeExecutionContext context, CancellationToken token)
        {
            context.Log($"?? [相机采集] 开始处理... (相机: {param.CameraAlias}, 模式: {param.TriggerMode})");

            await Task.Delay(100, token);

            string mockImage = $"HImage_Handle_{param.CameraAlias}_{DateTime.Now:HHmmss.fff}";

            // 使用重构后的 NodeExecutionContext 快捷写入方法
            context.SetOutputValue(node, PORT_OUT_IMAGE, mockImage);
            context.Log($"? [相机采集] 采图成功，输出句柄: {mockImage}");
        }
    }
}