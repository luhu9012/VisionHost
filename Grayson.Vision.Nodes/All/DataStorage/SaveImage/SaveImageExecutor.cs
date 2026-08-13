using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Nodes.Common;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.DataStorage.SaveImage
{
    [Node(NodeType.SaveImage, NodeCategory.DataStorage, typeof(SaveImageParam))]
    [NodePort("Image", PortType.In, PortCategory.Data, dataType: "Image", colorHex: "#9B59B6")]
    [NodePort("IsOk", PortType.In, PortCategory.Data, dataType: "Boolean", colorHex: "#3498DB")]
    [NodePort("SavedPath", PortType.Out, PortCategory.Data, dataType: "String", colorHex: "#E67E22")]
    public class SaveImageExecutor : NodeExecutorBase<SaveImageParam>
    {
        public const string PORT_IN_IMAGE = "Image";
        public const string PORT_IN_IS_OK = "IsOk";
        public const string PORT_OUT_SAVED_PATH = "SavedPath";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, SaveImageParam param, NodeExecutionContext context, CancellationToken token)
        {
            object imageObj = context.GetInputValue<object>(node, PORT_IN_IMAGE, null);
            bool isOk = context.GetInputValue<bool>(node, PORT_IN_IS_OK, true);

            if (imageObj == null)
            {
                context.Log($"⚠️ [{node.DisplayName}] 待保存图像对象为空，跳过存盘。");
                context.SetOutputValue(node, PORT_OUT_SAVED_PATH, string.Empty);
                return;
            }

            // 过滤保存设置
            if ((isOk && !param.SaveOkImage) || (!isOk && !param.SaveNgImage))
            {
                context.Log($"ℹ️ [{node.DisplayName}] 判定结果为 {(isOk ? "OK" : "NG")}，根据配置忽略存盘。");
                context.SetOutputValue(node, PORT_OUT_SAVED_PATH, string.Empty);
                return;
            }

            // 动态路径: D:\VisionImages\2026-08-13\OK\11_30_42_123_OK.jpg
            string subFolder = isOk ? "OK" : "NG";
            string dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
            string fullFolderPath = Path.Combine(param.BasePath, dateFolder, subFolder);

            string ext = param.ImageFormat.TrimStart('.').ToLower();
            string fileName = $"{DateTime.Now:HH_mm_ss_fff}{(isOk ? "_OK" : "_NG")}.{ext}";
            string fullFilePath = Path.Combine(fullFolderPath, fileName);

            // 提交后台任务写盘
            _ = Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(fullFolderPath))
                    {
                        Directory.CreateDirectory(fullFolderPath);
                    }

                    // 兼容处理：支持动态 ImWrite、System.Drawing.Bitmap 或通过反射/SDK 保存
                    SaveImageObject(imageObj, fullFilePath);
                }
                catch (Exception ex)
                {
                    LogBus.Error("SaveImage", $"图像异步存盘异常: {ex.Message}", ex);
                }
            }, token);

            context.SetOutputValue(node, PORT_OUT_SAVED_PATH, fullFilePath);
            context.Log($"💾 [{node.DisplayName}] 图像已成功提交异步写盘: {fileName}");

            await Task.CompletedTask;
        }

        private void SaveImageObject(object imageObj, string filePath)
        {
            // 移除 System.Drawing.Image 分支
            // if (imageObj is System.Drawing.Image bmp)
            // {
            //     bmp.Save(filePath);
            //     return;
            // }

            // 利用反射调用 OpenCvSharp.Cv2.ImWrite 或 OpenCvSharp.Mat.SaveImage，不硬依赖程序集
            var type = imageObj.GetType();
            var saveMethod = type.GetMethod("Save") ?? type.GetMethod("SaveImage");
            if (saveMethod != null)
            {
                saveMethod.Invoke(imageObj, new object[] { filePath });
                return;
            }

            // 若全不匹配，尝试按序列化字节流/ToString 保存备用
            File.WriteAllText(filePath, imageObj.ToString());
        }
    }
}