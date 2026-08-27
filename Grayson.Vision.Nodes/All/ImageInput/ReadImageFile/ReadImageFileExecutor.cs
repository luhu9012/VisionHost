using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.Nodes.Common;
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
            await Task.Yield();

            string targetFilePath = null;

            if (param.IsBatchFolder)
            {
                if (param.FileItems.Count == 0 && Directory.Exists(param.FolderPath))
                {
                    param.LoadFolderFiles();
                }

                if (param.FileItems.Count > 0)
                {
                    if (param.CurrentImageIndex < 0 || param.CurrentImageIndex >= param.FileItems.Count)
                    {
                        param.CurrentImageIndex = 0;
                    }

                    targetFilePath = param.FileItems[param.CurrentImageIndex];
                    param.SelectedFilePath = targetFilePath;

                    context.Log($"[图像读取] 批处理模式读取 [{param.CurrentImageIndex + 1}/{param.FileItems.Count}]: {Path.GetFileName(targetFilePath)}");

                    // 结合 LoopFolder 递增指针
                    if (param.LoopFolder)
                    {
                        param.CurrentImageIndex = (param.CurrentImageIndex + 1) % param.FileItems.Count;
                    }
                    else if (param.CurrentImageIndex < param.FileItems.Count - 1)
                    {
                        param.CurrentImageIndex++;
                    }
                }
                else
                {
                    context.Log($"⚠️ [图像读取] 批处理文件夹内无有效图片: {param.FolderPath}");
                }
            }
            else
            {
                targetFilePath = param.FilePath;
                if (!string.IsNullOrWhiteSpace(targetFilePath) && File.Exists(targetFilePath))
                {
                    context.Log($"[图像读取] 单图模式读取: {Path.GetFileName(targetFilePath)}");
                }
                else
                {
                    context.Log($"⚠️ [图像读取] 单图文件路径无效: {targetFilePath}");
                    targetFilePath = null;
                }
            }

            context.SetOutputValue(node, PORT_OUT_IMAGE, null);

            if (string.IsNullOrWhiteSpace(targetFilePath) || !File.Exists(targetFilePath))
            {
                Preview?.BeginScene();
                Preview?.AddText("⚠️ 未选择有效图片文件", 12, 12, "red");
                return;
            }

            // 加载图片文件为 object（运行时 HImage），输出到端口供下游消费
            // 用 LoadImage（返回 Result<object>）而非 ReadImageFile（返回 Result<HObject>），
            // 避免 HObject 类型穿透到 Nodes 层导致 CS0012
            var loadRes = ImageBasicTool.LoadImage(targetFilePath);
            if (!loadRes.Success || loadRes.Data == null)
            {
                context.Log($"⚠️ [图像读取] 图片加载失败: {loadRes.Message}");
                Preview?.BeginScene();
                Preview?.AddText("⚠️ 图片加载失败: " + loadRes.Message, 12, 12, "red");
                return;
            }

            context.SetOutputValue(node, PORT_OUT_IMAGE, loadRes.Data);

            // 实时预览：显示加载的图片 + 文件名标注
            Preview?.BeginScene();
            Preview?.AddBorrowed(loadRes.Data);
            Preview?.AddText($"📁 {Path.GetFileName(targetFilePath)}", 12, 12, "yellow");
        }
    }
}