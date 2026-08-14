using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Match2D;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.ShapeMatch
{
    [Node(NodeType.ShapeMatch, NodeCategory.CalibrationLocation, typeof(ShapeMatchParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("MatchRow", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("MatchCol", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("MatchAngle", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("MatchScore", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#2ECC71")]
    public class ShapeMatchExecutor : NodeExecutorBase<ShapeMatchParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_ROW = "MatchRow";
        public const string PORT_OUT_COL = "MatchCol";
        public const string PORT_OUT_ANGLE = "MatchAngle";
        public const string PORT_OUT_SCORE = "MatchScore";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ShapeMatchParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            object inputImage = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImage == null)
            {
                context.Log("[" + node.DisplayName + "] 错误：未获取到有效输入图像句柄！");
                return;
            }

            if (param.ModelId < 0)
            {
                context.Log("[" + node.DisplayName + "] 错误：Shape 模板 ID 无效，请先训练模板！");
                return;
            }

            var matchRes = MatchTool.ApplyFindShapeModel(inputImage, param.ModelId, param.MinScore);

            if (matchRes.Success && matchRes.Data != null && matchRes.Data.Length > 0)
            {
                var best = matchRes.Data[0];
                context.SetOutputValue(node, PORT_OUT_ROW, best.PixelRow);
                context.SetOutputValue(node, PORT_OUT_COL, best.PixelCol);
                context.SetOutputValue(node, PORT_OUT_ANGLE, best.RotateDegree);
                context.SetOutputValue(node, PORT_OUT_SCORE, best.Score);

                context.Log("[" + node.DisplayName + "] 形状匹配成功 -> Row: " + best.PixelRow.ToString("F2") + ", Col: " + best.PixelCol.ToString("F2") + ", Angle: " + best.RotateDegree.ToString("F2") + "°, Score: " + best.Score.ToString("F2"));
            }
            else
            {
                context.Log("[" + node.DisplayName + "] 形状匹配失败: " + matchRes.Message);
            }
        }
    }
}