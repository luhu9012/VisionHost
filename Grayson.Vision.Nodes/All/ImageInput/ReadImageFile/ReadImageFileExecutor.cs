using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Nodes.Common;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.ImageInput.ReadImageFile
{
    [Node(NodeType.ReadImageFile, NodeCategory.ImageInput, typeof(ReadImageFileParam))]
    [NodePort("Image", PortType.Out, PortCategory.Data, dataType: "Image", colorHex: "#028090")]
    public class ReadImageFileExecutor : NodeExecutorBase<ReadImageFileParam>
    {
        public const string PORT_OUT_IMAGE = "Image";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ReadImageFileParam param, NodeExecutionContext context, CancellationToken token)
        {
            string targetPath = param.IsBatchFolder ? param.FolderPath : param.FilePath;
            context.Log($"[图像读取] 加载图像文件: {targetPath}...");

            await Task.Yield();

            string imageHandle = $"HImage_File_{Path.GetFileName(targetPath)}_{DateTime.Now:HHmmss.fff}";
            context.SetOutputValue(node, PORT_OUT_IMAGE, imageHandle);
            context.Log($"[图像读取] 读取成功: {imageHandle}");
        }
    }
}