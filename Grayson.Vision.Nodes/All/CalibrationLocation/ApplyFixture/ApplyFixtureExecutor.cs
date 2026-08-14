using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.ApplyFixture
{
    [Node(NodeType.ApplyFixture, NodeCategory.CalibrationLocation, typeof(ApplyFixtureParam))]
    [NodePort("HomMat2D", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#3498DB")]
    [NodePort("InputRegion", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    [NodePort("OutputRow", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("OutputCol", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("OutputRegion", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    public class ApplyFixtureExecutor : NodeExecutorBase<ApplyFixtureParam>
    {
        public const string PORT_IN_HOMMAT = "HomMat2D";
        public const string PORT_IN_REGION = "InputRegion";
        public const string PORT_OUT_ROW = "OutputRow";
        public const string PORT_OUT_COL = "OutputCol";
        public const string PORT_OUT_REGION = "OutputRegion";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ApplyFixtureParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            object homMat = context.GetInputValue<object>(node, PORT_IN_HOMMAT);
            if (homMat == null)
            {
                context.Log("[" + node.DisplayName + "] 错误：未输入有效的变换矩阵 HomMat2D！");
                return;
            }

            if (param.TargetType == FollowType.Point)
            {
                var ptRes = FixtureTool.ApplyFixtureToPoint(param.BaseRow, param.BaseCol, homMat);
                if (ptRes.Success)
                {
                    context.SetOutputValue(node, PORT_OUT_ROW, ptRes.Data.newRow);
                    context.SetOutputValue(node, PORT_OUT_COL, ptRes.Data.newCol);
                    context.Log("[" + node.DisplayName + "] 点坐标跟随成功 -> Row: " + ptRes.Data.newRow.ToString("F2") + ", Col: " + ptRes.Data.newCol.ToString("F2"));
                }
                else
                {
                    context.Log("[" + node.DisplayName + "] 点坐标跟随失败: " + ptRes.Message);
                }
            }
            else
            {
                object inRegion = context.GetInputValue<object>(node, PORT_IN_REGION);
                if (inRegion == null)
                {
                    context.Log("[" + node.DisplayName + "] 错误：未输入待跟随的 Region 区域！");
                    return;
                }

                var regRes = FixtureTool.ApplyFixtureToRegion(inRegion, homMat);
                if (regRes.Success)
                {
                    context.SetOutputValue(node, PORT_OUT_REGION, regRes.Data);
                    context.Log("[" + node.DisplayName + "] Region 区域位置跟随成功");
                }
                else
                {
                    context.Log("[" + node.DisplayName + "] Region 区域位置跟随失败: " + regRes.Message);
                }
            }
        }
    }
}