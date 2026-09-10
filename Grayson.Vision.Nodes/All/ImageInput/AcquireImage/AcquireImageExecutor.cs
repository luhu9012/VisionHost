using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.Nodes.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.ImageInput.AcquireImage
{
    [Node(NodeType.AcquireImage, NodeCategory.ImageInput, typeof(AcquireImageParam))]
    [NodePort("Input", PortType.In, PortCategory.Data, dataType: "Bool", colorHex: "#028090")]
    [NodePort("Image", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    public class AcquireImageExecutor : NodeExecutorBase<AcquireImageParam>
    {
        public const string PORT_OUT_IMAGE = "Image";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, AcquireImageParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            context.Log($"[相机采集] 开始采集，逻辑相机: {param.CameraAlias}, 模式: {param.TriggerMode}");

            var camera = context.GetHardware<ICamera>(param.CameraAlias);
            if (camera == null)
                throw new InvalidOperationException($"未找到逻辑相机 [{param.CameraAlias}] 对应硬件实例。");

            EnsureCameraReady(camera, param.CameraAlias, context);

            // 相机独占让渡兜底：工位监视的 Live 连续采集正常会在本节点开始执行前
            // （OnNodeExecuting 事件链）同步停流；若该事件链缺失（如 FlowEdit 直跑、
            // 外部占用），此处再停一次连续采集，保证下方触发模式配置不与连续采集冲突。
            // 未在采集时调用停止无副作用（失败忽略）。
            try { camera.StopContinuousGrab(); } catch { /* 兜底停流失败交由后续显式调用报错 */ }

            EnsureSuccess(camera.SetExposureTime(param.ExposureTime), "设置曝光失败");
            EnsureSuccess(camera.SetGain(param.Gain), "设置增益失败");

            var mode = (param.TriggerMode ?? string.Empty).Trim().ToLowerInvariant();
            var shouldStopAfterSingle = false;

            try
            {
                FrameEventArgs frame;
                switch (mode)
                {
                    case "continuous":
                        EnsureSuccess(camera.SetTriggerMode(0), "设置连续采集模式失败");
                        EnsureSuccess(camera.StartContinuousGrab(), "启动连续采集失败");
                        frame = await WaitForSingleFrameAsync(camera, param.TimeoutMs, token).ConfigureAwait(false);
                        break;

                    case "hardware":
                        EnsureSuccess(camera.SetTriggerMode(2), "设置硬触发模式失败");
                        EnsureSuccess(camera.StartGrabbing(), "启动采集流失败");
                        shouldStopAfterSingle = true;
                        frame = await WaitForSingleFrameAsync(camera, param.TimeoutMs, token).ConfigureAwait(false);
                        break;

                    default:
                        EnsureSuccess(camera.SetTriggerMode(1), "设置软触发模式失败");
                        EnsureSuccess(camera.StartGrabbing(), "启动采集流失败");
                        shouldStopAfterSingle = true;

                        // 先挂帧等待再发软触发（防丢帧）。若软触发失败：
                        // 必须取消 waitTask 使其 finally 退订 FrameReceived，避免 handler 泄漏
                        //（残留 handler 会在下一帧到达时继续 TrySetResult，虽无害但随失败次数累积）。
                        using (var triggerCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            var waitTask = WaitForSingleFrameAsync(camera, param.TimeoutMs, triggerCts.Token);
                            try
                            {
                                EnsureSuccess(camera.SoftTrigger(), "发送软触发失败");
                            }
                            catch
                            {
                                triggerCts.Cancel();
                                try { await waitTask.ConfigureAwait(false); }
                                catch { /* 忽略取消失败 */ }
                                throw;
                            }
                            frame = await waitTask.ConfigureAwait(false);
                        }
                        break;
                }

                context.CycleContext.TransientItems[$"CameraFrame:{param.CameraAlias}"] = frame;

                // 将相机原始帧转换为 Halcon HImage，供下游视觉节点（ShapeMatch/Threshold 等）消费
                var convRes = ImageBasicTool.FrameToHImage(frame);
                if (!convRes.Success)
                {
                    context.Log("❌ [相机采集] 图像转换失败: " + convRes.Message);
                    context.SetOutputValue(node, PORT_OUT_IMAGE, null);
                    return;
                }

                context.SetOutputValue(node, PORT_OUT_IMAGE, convRes.Data);
                context.Log($"[相机采集] 采集成功，FrameNum={frame?.FrameNum}, Size={frame?.Width}x{frame?.Height}, Format={frame?.PixelFormat}");
            }
            finally
            {
                if (shouldStopAfterSingle)
                {
                    var stopResult = camera.StopGrabbing();
                    if (stopResult?.Success != true)
                    {
                        context.Log($"⚠️ [相机采集] 停止采集流失败: {stopResult?.Message}");
                    }
                }
            }
        }

        private static void EnsureSuccess(Result result, string action)
        {
            if (result?.Success != true)
            {
                throw new InvalidOperationException($"{action}: {result?.Message ?? "未知错误"}");
            }
        }

        private static void EnsureCameraReady(ICamera camera, string cameraAlias, NodeExecutionContext context)
        {
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }

            if (camera.State == DeviceState.Connected)
            {
                return;
            }

            var statusResult = camera.CheckStatus();
            if (statusResult?.Success == true && camera.State == DeviceState.Connected)
            {
                return;
            }

            context.Log($"[相机采集] 逻辑相机 [{cameraAlias}] 当前状态 [{camera.State}]，尝试自动打开...");
            EnsureSuccess(camera.Connect(), "打开相机失败");
            context.Log($"[相机采集] 逻辑相机 [{cameraAlias}] 打开成功。");
        }

        private static async Task<FrameEventArgs> WaitForSingleFrameAsync(ICamera camera, int timeoutMs, CancellationToken token)
        {
            var safeTimeout = timeoutMs < 100 ? 100 : timeoutMs;
            var tcs = new TaskCompletionSource<FrameEventArgs>();
            EventHandler<FrameEventArgs> handler = null;
            var timeoutCts = new CancellationTokenSource(safeTimeout);

            handler = (s, e) => tcs.TrySetResult(e);
            camera.FrameReceived += handler;

            try
            {
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token))
                using (linkedCts.Token.Register(() => tcs.TrySetCanceled()))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);

                throw new TimeoutException($"等待相机帧超时 ({safeTimeout}ms)");
            }
            finally
            {
                camera.FrameReceived -= handler;
                timeoutCts.Dispose();
            }
        }
    }
}
