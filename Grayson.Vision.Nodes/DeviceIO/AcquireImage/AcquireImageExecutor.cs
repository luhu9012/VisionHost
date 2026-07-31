using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.DeviceIO.AcquireImage
{
    [Node(
        type: NodeType.AcquireImage,
        category: NodeCategory.DeviceIO,
        displayName: "相机采集",
        description: "触发硬件相机采图或从软触发源获取图像",
        parameterType: typeof(AcquireImageParam)
        ,icon: "📷"
    )]
    //[NodePort("ExecIn", PortType.In, PortCategory.Exec)]
    //[NodePort("ExecOut", PortType.Out, PortCategory.Exec)]
    [NodePort("Image", PortType.Out, PortCategory.Data, dataType: "Image", colorHex: "#9B59B6")]
    public class AcquireImageExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            // 1. 获取并校验参数模型
            var param = node.ParameterModel as AcquireImageParam;
            if (param == null)
            {
                throw new InvalidOperationException($"节点 [{node.DisplayName}] 未正确配置 AcquireImageParam 参数对象。");
            }

            if (string.IsNullOrWhiteSpace(param.CameraAlias))
            {
                throw new ArgumentException($"节点 [{node.DisplayName}] 的相机标识 (CameraAlias) 不能为空！");
            }

            context.Log($"📷 [相机采集] 开始处理... (相机: {param.CameraAlias}, 模式: {param.TriggerMode}, 曝光: {param.ExposureTime}μs)");

            // 2. 从上下文/硬件管理总线获取绑定的相机设备接口 (例如 ICameraDevice)
            // 💡 架构建议：context 提供 GetHardware<T> 或 Services 容器
            /* 
            var cameraService = context.GetService<ICameraService>();
            var camera = cameraService?.GetCamera(param.CameraAlias);
            if (camera == null || !camera.IsConnected)
            {
                throw new Exception($"未找到指定的相机对象或相机未连接: [{param.CameraAlias}]");
            }

            // 3. 动态应用参数 (曝光、增益、触发模式)
            await camera.SetExposureTimeAsync(param.ExposureTime);
            await camera.SetGainAsync(param.Gain);
            await camera.SetTriggerModeAsync(param.TriggerMode);

            // 4. 执行采图操作
            object rawImage = null;
            if (param.TriggerMode == "Software")
            {
                // 软触发采图
                rawImage = await camera.SoftwareTriggerAndGrabAsync(param.TimeoutMs, token);
            }
            else
            {
                // 硬触发 / 连续采集：等待外触发信号图像回调
                rawImage = await camera.WaitForNextFrameAsync(param.TimeoutMs, token);
            }
            */

            // ==================== 模拟硬件逻辑 (Demo 阶段) ====================
            await Task.Delay(100, token); // 模拟硬件传输延时

            // 模拟构造生产图像数据句柄/对象 (可为 Halcon HImage, OpenCV Mat, 或者是 Bitmap/Byte[])
            string mockImage = $"HImage_Handle_{param.CameraAlias}_{DateTime.Now:HHmmss.fff}";
            // =================================================================

            if (mockImage == null)
            {
                throw new TimeoutException($"相机 [{param.CameraAlias}] 在等待 {param.TimeoutMs}ms 后采图超时！");
            }

            // 5. 将采图结果输出到 "Image" Data 数据端口，供下游算法节点（如模板匹配、缺陷检测）读取
            context.SetOutputValue(node, "Image", mockImage);

            context.Log($"✔ [相机采集] 采图成功，输出图像句柄: {mockImage}");
        }
    }
}