using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.DataStorage.SaveData
{
    [Node(NodeType.SaveData, NodeCategory.DataStorage, typeof(SaveDataParam))]
    [NodePort("DataRow", PortType.In, PortCategory.Data, dataType: "String", colorHex: "#E67E22")]
    [NodePort("IsSuccess", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#3498DB")]
    public class SaveDataExecutor : NodeExecutorBase<SaveDataParam>
    {
        public const string PORT_IN_DATA_ROW = "DataRow";
        public const string PORT_OUT_IS_SUCCESS = "IsSuccess";

        private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, SaveDataParam param, NodeExecutionContext context, CancellationToken token)
        {
            string dataRow = context.GetInputValue<string>(node, PORT_IN_DATA_ROW, string.Empty);

            if (string.IsNullOrWhiteSpace(dataRow))
            {
                context.Log($"⚠️ [{node.DisplayName}] 待追加的数据行 (DataRow) 为空，跳过写盘。");
                context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, false);
                return;
            }

            await _fileLock.WaitAsync(token);
            try
            {
                string filePath = param.FilePath.Trim();
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                bool fileExists = File.Exists(filePath);

                using (var writer = new StreamWriter(filePath, append: true, encoding: Encoding.UTF8))
                {
                    if (!fileExists && !string.IsNullOrWhiteSpace(param.CsvHeader))
                    {
                        await writer.WriteLineAsync(param.CsvHeader);
                    }

                    await writer.WriteLineAsync(dataRow);
                }

                context.Log($"📊 [{node.DisplayName}] 数据成功追加写入: {filePath}");
                context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, true);
            }
            catch (Exception ex)
            {
                context.Log($"❌ [{node.DisplayName}] 数据写盘失败: {ex.Message}");
                context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, false);
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }
}