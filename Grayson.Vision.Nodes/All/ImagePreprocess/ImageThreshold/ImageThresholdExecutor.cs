using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.ImagePreprocess.ImageThreshold
{
    [Node(NodeType.ImageThreshold, NodeCategory.ImagePreprocess, typeof(ImageThresholdParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("OutputRegion", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    // 处理效果图输出：端口名含 "Image" → 帧事件自动推送，缩略图列表/主视图/工位监视上屏，
    // 值 = 输入图同实例（借用），场景叠加层（分割区域副本+参数文本）随帧重放不被清除。
    [NodePort("OutputImage", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    public class ImageThresholdExecutor : NodeExecutorBase<ImageThresholdParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_REGION = "OutputRegion";
        public const string PORT_OUT_IMAGE = "OutputImage";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ImageThresholdParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            object inputImage = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImage == null)
            {
                context.Log("[" + node.DisplayName + "] 错误：未获取到有效输入图像句柄！");
                return;
            }

            // 🌟 实时预览（编辑器属性面板调试期注入；生产运行 Preview 为 null 全部跳过）：
            //    开新场景 → 借用底图（生命周期归端口缓存）→ 分割结果提交显示副本（所有权归显示层）
            var disp = context.Preview;
            disp?.BeginScene();
            disp?.AddBorrowed(inputImage);

            // 处理效果图输出（同实例借用）：帧推送上缩略图/主视图，场景叠加层共存
            context.SetOutputValue(node, PORT_OUT_IMAGE, inputImage);

            var res = ImagePreprocessTool.ApplyThreshold(
                inputImage,
                (int)param.Method,
                param.MinGray,
                param.MaxGray,
                param.DynamicMaskSize,
                param.DynamicOffset
            );

            if (res.Success)
            {
                context.SetOutputValue(node, PORT_OUT_REGION, res.Data);
                context.Log("[" + node.DisplayName + "] 阈值分割成功 (模式: " + param.Method + ")");

                // 分割区域画显示副本：原对象作为端口输出继续被下游消费，副本所有权归显示层
                disp?.Add(NodePreviewHelper.CopyForDisplay(res.Data), "green", 2);
                disp?.AddText(
                    $"[{param.Method}] Min={param.MinGray} Max={param.MaxGray}" +
                    (param.Method == ThresholdMethod.Dynamic ? $" Mask={param.DynamicMaskSize} Offset={param.DynamicOffset}" : ""),
                    12, 12, "yellow");
            }
            else
            {
                context.Log("[" + node.DisplayName + "] 阈值分割失败: " + res.Message);
                disp?.AddText("阈值分割失败: " + res.Message, 12, 12, "red");
            }
        }
    }
}