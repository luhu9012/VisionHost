using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.DataStorage.MesReport
{
    [Node(NodeType.MesReport, NodeCategory.DataStorage, typeof(MesReportParam))]
    [NodePort("Barcode", PortType.In, PortCategory.Data, dataType: "String", colorHex: "#E67E22")]
    [NodePort("IsOk", PortType.In, PortCategory.Data, dataType: "Boolean", colorHex: "#3498DB")]
    [NodePort("Payload", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#7C3AED")]
    [NodePort("IsSuccess", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#3498DB")]
    [NodePort("Response", PortType.Out, PortCategory.Data, dataType: "String", colorHex: "#2ECC71")]
    public class MesReportExecutor : NodeExecutorBase<MesReportParam>
    {
        public const string PORT_IN_BARCODE = "Barcode";
        public const string PORT_IN_IS_OK = "IsOk";
        public const string PORT_IN_PAYLOAD = "Payload";

        public const string PORT_OUT_IS_SUCCESS = "IsSuccess";
        public const string PORT_OUT_RESPONSE = "Response";

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, MesReportParam param, NodeExecutionContext context, CancellationToken token)
        {
            string barcode = context.GetInputValue<string>(node, PORT_IN_BARCODE, string.Empty);
            bool isOk = context.GetInputValue<bool>(node, PORT_IN_IS_OK, true);
            object payload = context.GetInputValue<object>(node, PORT_IN_PAYLOAD, null);

            var body = new
            {
                Station = param.StationName,
                Timestamp = DateTime.Now.ToString("o"),
                Barcode = barcode,
                Result = isOk ? "PASS" : "FAIL",
                Details = payload
            };

            // 使用 Newtonsoft.Json 进行序列化
            string jsonStr = JsonConvert.SerializeObject(body);
            var content = new StringContent(jsonStr, Encoding.UTF8, "application/json");

            context.Log($"🌐 [{node.DisplayName}] 正在上传 MES 报文: {jsonStr}");

            try
            {
                HttpResponseMessage response = await _httpClient.PostAsync(param.ApiUrl, content, token);
                string responseText = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    context.Log($"✅ [{node.DisplayName}] MES 上报成功, 响应: {responseText}");
                    context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, true);
                    context.SetOutputValue(node, PORT_OUT_RESPONSE, responseText);
                }
                else
                {
                    context.Log($"⚠️ [{node.DisplayName}] MES 返回 HTTP 错误码: {(int)response.StatusCode}");
                    context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, false);
                    context.SetOutputValue(node, PORT_OUT_RESPONSE, $"HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                context.Log($"❌ [{node.DisplayName}] MES 通信发生异常: {ex.Message}");
                context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, false);
                context.SetOutputValue(node, PORT_OUT_RESPONSE, ex.Message);
            }
        }
    }
}