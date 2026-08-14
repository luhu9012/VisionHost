using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.ROIOperation
{
    [Node(NodeType.ROIOperation, NodeCategory.ImagePreprocess, typeof(ROIOperationParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("InputRegion1", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    [NodePort("InputRegion2", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    [NodePort("OutputImage", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("OutputRegion", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    public class ROIOperationExecutor : NodeExecutorBase<ROIOperationParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_IN_REGION1 = "InputRegion1";
        public const string PORT_IN_REGION2 = "InputRegion2";
        public const string PORT_OUT_IMAGE = "OutputImage";
        public const string PORT_OUT_REGION = "OutputRegion";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ROIOperationParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            switch (param.OpType)
            {
                case RoiOpType.Crop:
                    {
                        object inputImg = context.GetInputValue<object>(node, PORT_IN_IMAGE);
                        if (inputImg == null)
                        {
                            context.Log($"[{node.DisplayName}] 错误: 输入图像为空");
                            return;
                        }
                        var cropRes = ImagePreprocessTool.ApplyRoiCrop(inputImg, param.Row1, param.Col1, param.Row2, param.Col2);
                        if (cropRes.Success)
                        {
                            context.SetOutputValue(node, PORT_OUT_IMAGE, cropRes.Data);
                        }
                        else
                        {
                            context.Log($"[{node.DisplayName}] 裁剪执行失败: {cropRes.Message}");
                        }
                    }
                    break;

                case RoiOpType.GenerateMask:
                    {
                        object reg1 = context.GetInputValue<object>(node, PORT_IN_REGION1);
                        object inputImg = context.GetInputValue<object>(node, PORT_IN_IMAGE);
                        if (reg1 == null)
                        {
                            context.Log($"[{node.DisplayName}] 错误: 区域1 (InputRegion1) 为空，无法生成 Mask");
                            return;
                        }
                        // 如果有图像包装方法，传入区域生成掩膜图像
                        var maskRes = ImagePreprocessTool.ApplyCombineRegions(reg1, null, 1);
                        if (maskRes.Success)
                        {
                            context.SetOutputValue(node, PORT_OUT_IMAGE, maskRes.Data);
                        }
                    }
                    break;

                case RoiOpType.Intersection:
                case RoiOpType.Union:
                case RoiOpType.Difference:
                    {
                        object reg1 = context.GetInputValue<object>(node, PORT_IN_REGION1);
                        object reg2 = context.GetInputValue<object>(node, PORT_IN_REGION2);

                        if (reg1 == null || reg2 == null)
                        {
                            context.Log($"[{node.DisplayName}] 错误: 集合运算要求输入区域 InputRegion1 和 InputRegion2 均不为空");
                            return;
                        }

                        int opCode = 1; // 1: Intersection
                        if (param.OpType == RoiOpType.Union) opCode = 2;
                        else if (param.OpType == RoiOpType.Difference) opCode = 3;

                        var setRes = ImagePreprocessTool.ApplyCombineRegions(reg1, reg2, opCode);
                        if (setRes.Success)
                        {
                            context.SetOutputValue(node, PORT_OUT_REGION, setRes.Data);
                        }
                        else
                        {
                            context.Log($"[{node.DisplayName}] 区域集合运算失败: {setRes.Message}");
                        }
                    }
                    break;
            }

            context.Log($"[{node.DisplayName}] ROI 运算处理完毕 ({param.OpType})");
        }
    }
}