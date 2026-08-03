using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Models;
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
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "Image", colorHex: "#028090")]
    [NodePort("OutputImage", PortType.Out, PortCategory.Data, dataType: "Image", colorHex: "#028090")]
    public class ImageChannelExecutor : NodeExecutorBase<ImageChannelParam>
    {
        /// <summary>输入图像端口名称常量</summary>
        public const string PORT_IN_IMAGE = "InputImage";
        /// <summary>输出图像端口名称常量</summary>
        public const string PORT_OUT_IMAGE = "OutputImage";

        /// <summary>
        /// 节点核心异步执行逻辑
        /// </summary>
        /// <param name="node">当前流程节点实例</param>
        /// <param name="param">节点配置参数实体（色彩空间、操作模式、通道索引等）</param>
        /// <param name="context">节点运行上下文（提供端口读写、日志、运行状态能力）</param>
        /// <param name="token">异步取消令牌，用于中断节点执行</param>
        /// <returns></returns>
        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ImageChannelParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            // 1. 获取输入图像
            var inputImg = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImg == null)
            {
                context.Log($"⚠️ [通道拆分] 未获取到输入图像。");
                context.SetOutputValue(node, PORT_OUT_IMAGE, null);
                throw new NotImplementedException("[通道拆分] 未获取到输入图像");
          
            }

            // 2. 执行通道拆分提取
            try
            {
                string channelName = GetChannelName(param.ColorSpace, param.SelectedChannelIndex);
                context.Log($"[通道拆分] 目标空间: {param.ColorSpace}, 提取通道: {channelName}");

                // TODO: 结合 OpenCVSharp / Halcon 实现通道提取逻辑
                // 示例: var monoImage = ImageEngine.ExtractChannel(inputImg, param.ColorSpace, param.SelectedChannelIndex);

                context.SetOutputValue(node, PORT_OUT_IMAGE, inputImg); // 占位输出提取后的单通道图像
            }
            catch (Exception ex)
            {
                context.Log($"❌ [通道拆分] 运行异常: {ex.Message}");
                throw;
            }
        }
        /// <summary>
        /// 根据色彩空间+通道索引，返回可读中文通道名称（C#7.3 兼容switch表达式改写）
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