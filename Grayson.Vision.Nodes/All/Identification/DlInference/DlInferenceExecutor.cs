using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Identification;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Identification.DlInference
{
    [Node(NodeType.DlInference, NodeCategory.Identification, typeof(DlInferenceParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("DetectionResults", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#3498DB")]
    [NodePort("DefectRegion", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    public class DlInferenceExecutor : NodeExecutorBase<DlInferenceParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_RESULTS = "DetectionResults";
        public const string PORT_OUT_REGION = "DefectRegion";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, DlInferenceParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            object inputImg = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImg == null)
            {
                context.Log($"[{node.DisplayName}] 错误: 输入图像为空");
                return;
            }

            var aiRes = DeepLearningTool.RunInference(inputImg, param.ModelPath, (int)param.TaskType, param.ConfidenceThreshold, param.UseGpu);
            if (aiRes.Success)
            {
                context.SetOutputValue(node, PORT_OUT_RESULTS, aiRes.ResultsList);
                context.SetOutputValue(node, PORT_OUT_REGION, aiRes.SegmentedRegion);
                context.Log($"[{node.DisplayName}] AI 推理完成，检测目标/缺陷数: {aiRes.Count}");
            }
            else
            {
                context.Log($"[{node.DisplayName}] AI 推理异常: {aiRes.Message}");
            }
        }
    }
}