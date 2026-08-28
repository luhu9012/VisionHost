using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.Nodes.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.ImageInput.ImageChannel
{
    /// <summary>
    /// 图像通道拆分/合成执行器
    /// 支持RGB、HSV色彩空间，实现单通道提取、多通道合并图像功能
    /// </summary>
    [Node(NodeType.ImageChannel, NodeCategory.ImageInput, typeof(ImageChannelParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#028090")]
    [NodePort("OutputImage", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#028090")]
    public class ImageChannelExecutor : NodeExecutorBase<ImageChannelParam>
    {
        /// <summary>输入图像端口名称常量</summary>
        public const string PORT_IN_IMAGE = "InputImage";
        /// <summary>输出图像端口名称常量</summary>
        public const string PORT_OUT_IMAGE = "OutputImage";

        /// <summary>
        /// 节点核心异步执行逻辑
        /// </summary>
        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ImageChannelParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            // 1. 获取输入图像
            var inputImg = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImg == null)
            {
                context.Log("⚠️ [通道拆分] 未获取到输入图像。");
                context.SetOutputValue(node, PORT_OUT_IMAGE, null);
                return;
            }

            // 2. 预览场景初始化（调试期注入；生产运行 Preview 为 null 全部跳过）
            var disp = context.Preview;
            disp?.BeginScene();
            disp?.AddBorrowed(inputImg);

            // 3. 执行通道提取
            string channelName = GetChannelName(param.ColorSpace, param.SelectedChannelIndex);
            context.Log("[通道拆分] 目标空间: " + param.ColorSpace + ", 提取通道: " + channelName);

            var res = ImageBasicTool.ExtractChannel(
                inputImg,
                (int)param.ColorSpace,
                param.SelectedChannelIndex
            );

            if (res.Success)
            {
                context.SetOutputValue(node, PORT_OUT_IMAGE, res.Data);

                // 诊断日志：输出图像信息（尺寸/通道数）——匹配类算子要求单通道，此处应显示"单通道灰度"
                var outInfo = ImageBasicTool.GetImageInfo(res.Data);
                context.Log("[通道拆分] 提取成功 → " + channelName + " 单通道灰度图" +
                    (outInfo.Success ? "（" + outInfo.Data + "）" : "（信息读取失败: " + outInfo.Message + "）"));

                // 提取结果画显示副本
                disp?.Add(NodePreviewHelper.CopyForDisplay(res.Data), "green", 1);
                disp?.AddText(
                    param.ColorSpace + " → " + channelName,
                    12, 12, "yellow");
            }
            else
            {
                context.Log("❌ [通道拆分] 失败: " + res.Message);
                context.SetOutputValue(node, PORT_OUT_IMAGE, null);
                disp?.AddText("通道提取失败: " + res.Message, 12, 12, "red");
            }
        }

        /// <summary>
        /// 根据色彩空间+通道索引，返回可读中文通道名称
        /// </summary>
        /// <param name="colorSpace">色彩空间枚举 RGB / HSV</param>
        /// <param name="index">选中通道下标 0/1/2</param>
        /// <returns>通道中文标识</returns>
        private string GetChannelName(ColorSpaceType colorSpace, int index)
        {
            if (colorSpace == ColorSpaceType.RGB)
            {
                switch (index)
                {
                    case 0:
                        return "R (红)";
                    case 1:
                        return "G (绿)";
                    case 2:
                        return "B (蓝)";
                    default:
                        return "R";
                }
            }
            else
            {
                switch (index)
                {
                    case 0:
                        return "H (色调)";
                    case 1:
                        return "S (饱和度)";
                    case 2:
                        return "V (明度)";
                    default:
                        return "H";
                }
            }
        }
    }
}
