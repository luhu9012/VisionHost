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
            context.Log($"[图像读取] 开始读取路径: {targetPath}...");

            await Task.Yield();

            if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
            {
                // 🌟 修复：直接将真实文件的路径作为输出，让 WrapImage 能成功 new HImage(filePath)
                context.SetOutputValue(node, PORT_OUT_IMAGE, targetPath);
                context.Log($"[图像读取] 读取成功: {Path.GetFileName(targetPath)}");
            }
            else
            {
                // 如果是文件夹模式，取文件夹内的第一张图片
                if (param.IsBatchFolder && Directory.Exists(targetPath))
                {
                    var files = Directory.GetFiles(targetPath, "*.*", SearchOption.TopDirectoryOnly);
                    if (files.Length > 0)
                    {
                        context.SetOutputValue(node, PORT_OUT_IMAGE, files[0]);
                        context.Log($"[图像读取] 批处理文件夹加载成功: {Path.GetFileName(files[0])}");
                        return;
                    }
                }

                context.SetOutputValue(node, PORT_OUT_IMAGE, null);
                context.Log($"⚠️ [图像读取] 路径无效或文件不存在: '{targetPath}'");
            }
        }
    }
}