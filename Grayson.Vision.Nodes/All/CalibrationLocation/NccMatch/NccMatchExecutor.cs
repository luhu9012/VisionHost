using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Match2D;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.NccMatch
{
    [Node(NodeType.NccMatch, NodeCategory.CalibrationLocation, typeof(NccMatchParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("MatchRow", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("MatchCol", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("MatchAngle", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("MatchScore", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#2ECC71")]
    public class NccMatchExecutor : NodeExecutorBase<NccMatchParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_ROW = "MatchRow";
        public const string PORT_OUT_COL = "MatchCol";
        public const string PORT_OUT_ANGLE = "MatchAngle";
        public const string PORT_OUT_SCORE = "MatchScore";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, NccMatchParam param, NodeExecutionContext context, CancellationToken token)
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

            if (param.ModelId < 0)
            {
                context.Log("[" + node.DisplayName + "] 错误：NCC 模板 ID 无效！");
                Preview?.BeginScene();
                Preview?.AddBorrowed(inputImage);
                Preview?.AddText("⚠️ NCC 模板 ID 无效，请先训练模板", 12, 12, "red");
                return;
            }

            // 预览：输入图为底图
            Preview?.BeginScene();
            Preview?.AddBorrowed(inputImage);

            var matchRes = MatchTool.ApplyFindNccModel(inputImage, param.ModelId, param.MinScore);

            if (matchRes.Success && matchRes.Data != null && matchRes.Data.Length > 0)
            {
                var best = matchRes.Data[0];
                context.SetOutputValue(node, PORT_OUT_ROW, best.PixelRow);
                context.SetOutputValue(node, PORT_OUT_COL, best.PixelCol);
                context.SetOutputValue(node, PORT_OUT_ANGLE, best.RotateDegree);
                context.SetOutputValue(node, PORT_OUT_SCORE, best.Score);

                context.Log("[" + node.DisplayName + "] NCC 灰度匹配成功 -> Score: " + best.Score.ToString("F2"));

                // 匹配位置画十字 + 分数标注
                Preview?.AddCross(best.PixelRow, best.PixelCol, 40, "green");
                Preview?.AddText($"✅ Score: {best.Score:F2}  ({best.PixelRow:F1}, {best.PixelCol:F1})", 12, 12, "green");
            }
            else
            {
                context.Log("[" + node.DisplayName + "] NCC 灰度匹配失败: " + matchRes.Message);
                Preview?.AddText($"❌ 匹配失败 (MinScore={param.MinScore:F2}): {matchRes.Message}", 12, 12, "red");
            }
        }
    }
}