//===================================================================================
// 文件名: ReadImageFileExecutor.cs
// 说 明: 图像输入节点 —— 「单张文件 / 本地文件夹批处理」两条**离线图源**分支。
//
// ★★ 定位（2026-09-26 标注）：本节点是【离线 / 演示 / 回归复跑】用的图源，不是产线图源。
//    真机（工控现场）的正确做法不是在这里读文件，而是把链上的本节点**换成**
//    「图像采集 AcquireImage」节点（同层目录 ImageInput/AcquireImage）——
//    它已经是完整实现（逻辑相机别名 → ICamera → 等帧 → FrameToHImage），
//    连"独占让渡/软触发防丢帧/超时退订"都写好了，**不要在本节点重复实现**。
//
// ── 真机接入指引（TODO：按这 4 步走即可）──────────────────────────────────────
//   TODO-1 设备池里配好逻辑相机：给相机设备起别名（如 "TopCam"）并确认能取到、
//          状态为 Connected。取实例的口子是 NodeExecutionContext.GetHardware<ICamera>(别名)。
//   TODO-2 把本节点换成「图像采集」节点，属性面板填（AcquireImageParam 的默认值已给出）：
//            CameraAlias  = 上一步的别名（默认 "TopCam"，带 LogicalDeviceBinding 特性）
//            TriggerMode  = Software（默认；标定/测量取图**必须**软触发）
//            ExposureTime = 5000μs、Gain = 1.0、TimeoutMs = 3000（按现场光照与节拍调）
//   TODO-3 相机侧一次性配置软触发：ICamera.ConfigureSoftwareTrigger()。
//          ★ 纪律（ICamera.cs 注释里写死）：标定采样必须「走位 → 软触发 → 本点新帧」，
//            不能依赖连续自由流——连续流下"等新帧"等于等一个随机时刻，慢帧必超时，
//            且走位之后可能拿到**上一位置**的帧（帧与位姿不对应，误差会静默进标定）。
//   TODO-4 若确实需要在本节点内部直接取相机（不推荐），取消下方
//          「CAMERA-SKELETON」注释块，按骨架补齐别名/参数来源即可；两个私有辅助方法
//          直接照抄 AcquireImageExecutor 的同名实现，不要在流程层自己造第二套。
//
// ── 为什么不能拿「本地文件夹批处理」当产线图源 ─────────────────────────────────
//   · 无曝光/增益控制 ⇒ 换批工件或现场光一变就漏边，且问题会表现为"算法不稳"而非"取图不稳"；
//   · 无触发同步 ⇒ 与被测物"是否到位"无法对齐，"这张图对应哪一刻"不可判；
//   · 无帧号/时间戳 ⇒ 追溯链断裂（AcquireImage 会把 FrameNum/尺寸/格式写进日志）。
//   ⇒ 本节点的正当用途：算法调参、离线回归复跑、无相机时的演示与自测（当前 002/006 演示链即此）。
//===================================================================================
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.Nodes.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.ImageInput.ReadImageFile
{
    [Node(NodeType.ReadImageFile, NodeCategory.ImageInput, typeof(ReadImageFileParam))]
    [NodePort("Image", PortType.Out, PortCategory.Data, dataType: "Image", colorHex: "#028090")]
    public class ReadImageFileExecutor : NodeExecutorBase<ReadImageFileParam>
    {
        public const string PORT_OUT_IMAGE = "Image";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ReadImageFileParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            // ───────────────────────────────────────────────────────────────────────
            // 图源解析（★ 真机改造点**就在这里**）
            //   现状：只有两条离线分支 —— 批处理文件夹 / 单文件。
            //   真机：见文件头「真机接入指引」（首选 = 换成 AcquireImage 节点）。
            //        若坚持在本节点内取相机，取消下面 CAMERA-SKELETON 注释并按骨架补参数。
            // ───────────────────────────────────────────────────────────────────────
            // ── CAMERA-SKELETON（真机取图骨架：现为注释，接入时取消注释 + 补 TODO）──
            //   ★ 需要 using：Grayson.Vision.Contracts.Devices（ICamera / FrameEventArgs）
            //                 Grayson.Vision.Contracts.Devices.Enums（DeviceState）
            //
            //   // TODO-1 别名与相机参数建议做成节点参数（照抄 AcquireImageParam 的写法：
            //   //   [LogicalDeviceBinding(deviceType: DeviceCategory.Camera, ...)] public string CameraAlias）
            //   //   不要在流程层硬编码 —— 换工位/换相机时改代码就等于改流程语义。
            //   const string cameraAlias = "TopCam";
            //   var camera = context.GetHardware<ICamera>(cameraAlias);   // 逻辑别名 → 硬件实例
            //   if (camera == null)
            //       throw new InvalidOperationException(
            //           $"未找到逻辑相机 [{cameraAlias}]，请在设备池配置并确认与工位的 DeviceMappings 对得上");
            //   if (camera.State != DeviceState.Connected)
            //   {
            //       // 照抄 AcquireImageExecutor.EnsureCameraReady：先 CheckStatus()，再 Connect()
            //       EnsureCameraConnected(camera, cameraAlias, context);
            //   }
            //
            //   // TODO-2 曝光/增益/触发模式应从节点参数来（此处仅示意取值顺序）
            //   camera.SetExposureTime(5000);          // μs
            //   camera.SetGain(1.0);
            //   camera.ConfigureSoftwareTrigger();     // 一次性：相机侧配成软触发（标定/测量必须）
            //   camera.StopContinuousGrab();           // 兜底：与工位监视的 Live 连续采集抢流
            //   camera.StartGrabbing();
            //
            //   // TODO-3 先挂帧等待、再发软触发（防丢帧）；超时必须退订 FrameReceived
            //   //        这两个私有方法照抄 AcquireImageExecutor 同名实现，不要另造一套。
            //   var waitTask = WaitForSingleFrameAsync(camera, 3000, token);
            //   camera.SoftTrigger();
            //   var rawFrame = await waitTask.ConfigureAwait(false);
            //
            //   // TODO-4 与下游的接口保持不变：输出端口仍然是"object 图像"。
            //   //        转换口与 AcquireImage 一致，避免两条取图通路各写一份像素格式分支。
            //   var camConv = ImageBasicTool.FrameToHImage(rawFrame);
            //   if (!camConv.Success) { context.Log("❌ [图像读取/相机] 帧转换失败: " + camConv.Message); return; }
            //   context.SetOutputValue(node, PORT_OUT_IMAGE, camConv.Data);
            //   context.Log($"[图像读取/相机] 采集成功 FrameNum={rawFrame?.FrameNum} " +
            //               $"Size={rawFrame?.Width}x{rawFrame?.Height} Format={rawFrame?.PixelFormat}");
            //   Preview?.BeginScene();
            //   Preview?.AddBorrowed(camConv.Data);
            //   Preview?.AddText($"📷 {cameraAlias} #{rawFrame?.FrameNum}", 12, 12, "yellow");
            //   return;   // 相机分支到此为止，不要再走下面的文件解析
            // ───────────────────────────────────────────────────────────────────────
            string targetFilePath = null;

            if (param.IsBatchFolder)
            {
                // ★ 离线分支（演示/回归用）：按 CurrentImageIndex 逐张推进指针 ——
                //   LoopFolder=true 循环，false 则到末张后停住（工位监视页会一直显示最后那张）。
                //   真机没有"文件夹指针"这回事：每帧都应当来自一次触发（见文件头 TODO-2/TODO-3）。
                if (param.FileItems.Count == 0 && Directory.Exists(param.FolderPath))
                {
                    param.LoadFolderFiles();
                }

                if (param.FileItems.Count > 0)
                {
                    if (param.CurrentImageIndex < 0 || param.CurrentImageIndex >= param.FileItems.Count)
                    {
                        param.CurrentImageIndex = 0;
                    }

                    targetFilePath = param.FileItems[param.CurrentImageIndex];
                    param.SelectedFilePath = targetFilePath;

                    context.Log($"[图像读取] 批处理模式读取 [{param.CurrentImageIndex + 1}/{param.FileItems.Count}]: {Path.GetFileName(targetFilePath)}");

                    // 结合 LoopFolder 递增指针
                    if (param.LoopFolder)
                    {
                        param.CurrentImageIndex = (param.CurrentImageIndex + 1) % param.FileItems.Count;
                    }
                    else if (param.CurrentImageIndex < param.FileItems.Count - 1)
                    {
                        param.CurrentImageIndex++;
                    }
                }
                else
                {
                    context.Log($"⚠️ [图像读取] 批处理文件夹内无有效图片: {param.FolderPath}");
                }
            }
            else
            {
                targetFilePath = param.FilePath;
                if (!string.IsNullOrWhiteSpace(targetFilePath) && File.Exists(targetFilePath))
                {
                    context.Log($"[图像读取] 单图模式读取: {Path.GetFileName(targetFilePath)}");
                }
                else
                {
                    context.Log($"⚠️ [图像读取] 单图文件路径无效: {targetFilePath}");
                    targetFilePath = null;
                }
            }

            context.SetOutputValue(node, PORT_OUT_IMAGE, null);

            if (string.IsNullOrWhiteSpace(targetFilePath) || !File.Exists(targetFilePath))
            {
                Preview?.BeginScene();
                Preview?.AddText("⚠️ 未选择有效图片文件", 12, 12, "red");
                return;
            }

            // 加载图片文件为 object（运行时 HImage），输出到端口供下游消费
            // ★ 下游接口与相机通路**一致**：端口都是 dataType="object"（运行时 HImage）。
            //   所以真机改造时下游测量节点一行都不用改 —— 只换图源（见文件头「真机接入指引」）。
            //   相机分支若启用，会在上面的 CAMERA-SKELETON 处 return，不会走到这里。
            // 用 LoadImage（返回 Result<object>）而非 ReadImageFile（返回 Result<HObject>），
            // 避免 HObject 类型穿透到 Nodes 层导致 CS0012
            var loadRes = ImageBasicTool.LoadImage(targetFilePath);
            if (!loadRes.Success || loadRes.Data == null)
            {
                context.Log($"⚠️ [图像读取] 图片加载失败: {loadRes.Message}");
                Preview?.BeginScene();
                Preview?.AddText("⚠️ 图片加载失败: " + loadRes.Message, 12, 12, "red");
                return;
            }

            context.SetOutputValue(node, PORT_OUT_IMAGE, loadRes.Data);

            // 实时预览：显示加载的图片 + 文件名标注
            Preview?.BeginScene();
            Preview?.AddBorrowed(loadRes.Data);
            Preview?.AddText($"📁 {Path.GetFileName(targetFilePath)}", 12, 12, "yellow");
        }
    }
}