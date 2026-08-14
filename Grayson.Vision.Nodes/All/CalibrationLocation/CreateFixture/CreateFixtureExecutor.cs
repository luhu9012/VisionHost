using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.CreateFixture
{
    [Node(NodeType.CreateFixture, NodeCategory.CalibrationLocation, typeof(CreateFixtureParam))]
    [NodePort("CurrentRow", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("CurrentCol", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("CurrentAngle", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("HomMat2D", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#3498DB")]
    [NodePort("DeltaX", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#F39C12")]
    [NodePort("DeltaY", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#F39C12")]
    [NodePort("DeltaAngle", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#F39C12")]
    public class CreateFixtureExecutor : NodeExecutorBase<CreateFixtureParam>
    {
        public const string PORT_IN_ROW = "CurrentRow";
        public const string PORT_IN_COL = "CurrentCol";
        public const string PORT_IN_ANGLE = "CurrentAngle";
        public const string PORT_OUT_HOMMAT = "HomMat2D";
        public const string PORT_OUT_DELTAX = "DeltaX";
        public const string PORT_OUT_DELTAY = "DeltaY";
        public const string PORT_OUT_DELTAANGLE = "DeltaAngle";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, CreateFixtureParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            double curRow = context.GetInputValue<double>(node, PORT_IN_ROW);
            double curCol = context.GetInputValue<double>(node, PORT_IN_COL);
            double curAngle = context.GetInputValue<double>(node, PORT_IN_ANGLE);

            var fixRes = FixtureTool.CreateFixture(param.BaselineRow, param.BaselineCol, param.BaselineAngle, curRow, curCol, curAngle);

            if (fixRes.Success && fixRes.Data != null)
            {
                context.SetOutputValue(node, PORT_OUT_HOMMAT, fixRes.Data.HomMat2DHandle);
                context.SetOutputValue(node, PORT_OUT_DELTAX, fixRes.Data.DeltaCol);
                context.SetOutputValue(node, PORT_OUT_DELTAY, fixRes.Data.DeltaRow);
                context.SetOutputValue(node, PORT_OUT_DELTAANGLE, fixRes.Data.DeltaAngle);

                context.Log("[" + node.DisplayName + "] 位置补正矩阵建立成功 -> ΔCol(X): " + fixRes.Data.DeltaCol.ToString("F2") + ", ΔRow(Y): " + fixRes.Data.DeltaRow.ToString("F2") + ", Δθ: " + fixRes.Data.DeltaAngle.ToString("F2") + "°");
            }
            else
            {
                context.Log("[" + node.DisplayName + "] 建立位置补正矩阵失败: " + fixRes.Message);
            }
        }
    }
}