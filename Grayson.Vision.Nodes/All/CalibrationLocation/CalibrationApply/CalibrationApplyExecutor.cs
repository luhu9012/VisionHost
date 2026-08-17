using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.CalibrationApply
{
    [Node(NodeType.CalibrationApply, NodeCategory.CalibrationLocation, typeof(CalibrationApplyParam))]
    [NodePort("InputX", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("InputY", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("OutputX", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#2ECC71")]
    [NodePort("OutputY", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#2ECC71")]
    public class CalibrationApplyExecutor : NodeExecutorBase<CalibrationApplyParam>
    {
        public const string PORT_IN_X = "InputX";
        public const string PORT_IN_Y = "InputY";
        public const string PORT_OUT_X = "OutputX";
        public const string PORT_OUT_Y = "OutputY";

        private readonly ICalibrationService _calibService;

        public CalibrationApplyExecutor()
        {
            _calibService = new CalibrationService();
        }

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, CalibrationApplyParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            double inX = context.GetInputValue<double>(node, PORT_IN_X);
            double inY = context.GetInputValue<double>(node, PORT_IN_Y);

            if (string.IsNullOrEmpty(param.HomMatFilePath))
            {
                context.Log($"[{node.DisplayName}] 错误：尚未指定有效的标定矩阵文件路径！");
                return;
            }

            if (param.IsPixelToWorld)
            {
                // 像素坐标 -> 机械物理坐标 (mm)
                var mapRes = _calibService.MapPixelToWorld(param.HomMatFilePath, inX, inY);
                if (mapRes.Success)
                {
                    context.SetOutputValue(node, PORT_OUT_X, mapRes.Data.WorldX);
                    context.SetOutputValue(node, PORT_OUT_Y, mapRes.Data.WorldY);
                    context.Log($"[{node.DisplayName}] 坐标转换成功：Pixel({inX:F2}, {inY:F2}) ➔ World({mapRes.Data.WorldX:F3}, {mapRes.Data.WorldY:F3}) mm");
                }
                else
                {
                    context.Log($"[{node.DisplayName}] 坐标转换失败：{mapRes.Message}");
                }
            }
            else
            {
                // 机械物理坐标 -> 像素坐标
                var mapRes = _calibService.MapWorldToPixel(param.HomMatFilePath, inX, inY);
                if (mapRes.Success)
                {
                    context.SetOutputValue(node, PORT_OUT_X, mapRes.Data.PixelX);
                    context.SetOutputValue(node, PORT_OUT_Y, mapRes.Data.PixelY);
                    context.Log($"[{node.DisplayName}] 坐标逆转换成功：World({inX:F3}, {inY:F3}) ➔ Pixel({mapRes.Data.PixelX:F2}, {mapRes.Data.PixelY:F2}) px");
                }
                else
                {
                    context.Log($"[{node.DisplayName}] 坐标逆转换失败：{mapRes.Message}");
                }
            }
        }
    }
}