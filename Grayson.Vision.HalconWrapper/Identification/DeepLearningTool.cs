using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Grayson.Vision.Contracts.Ai;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Identification
{
    /// <summary>
    /// DL 推理请求参数（节点参数 → 本工具 → IInferenceProvider 的中间载体）。
    /// 节点层把 UI 参数收集到这里，工具层不直接依赖 Nodes 的参数模型。
    /// </summary>
    public class DlInferenceRequest
    {
        /// <summary>ONNX 模型路径（支持相对路径，以运行目录为基准）</summary>
        public string ModelPath { get; set; }

        /// <summary>任务类型（0=分类 1=检测 2=分割 3=异常检测，对应 Contracts.InferenceTaskType）</summary>
        public InferenceTaskType TaskType { get; set; }

        /// <summary>置信度阈值 0~1（异常检测任务=异常分数 NG 判定线，需 OK 样本标定）</summary>
        public double ConfidenceThreshold { get; set; } = 0.5;

        /// <summary>NMS IoU 阈值 0~1（仅检测任务用）</summary>
        public double IouThreshold { get; set; } = 0.45;

        /// <summary>异常检测专用：热力图二值化阈值（0~1，对归一化热力图取阈值生成缺陷 Region）</summary>
        public double AnomalyRegionThreshold { get; set; } = 0.5;

        /// <summary>是否优先 GPU（当前 CPU 版插件忽略）</summary>
        public bool UseGpu { get; set; }

        /// <summary>类别标签（可选，顺序与训练导出一致）</summary>
        public List<string> Labels { get; set; }

        /// <summary>检测输出布局提示（auto / yolov5 / yolov8）</summary>
        public string LayoutHint { get; set; } = "auto";
    }

    /// <summary>
    /// DL 推理结果（HalconWrapper 层的聚合输出：结构化数据 + HALCON 显示对象）。
    /// ResultsList 保持 object 兼容旧行为，实际是强类型集合，下游按 TaskType 取。
    /// </summary>
    public class DlInferenceResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;

        /// <summary>推理耗时（毫秒）</summary>
        public double ElapsedMs { get; set; }

        /// <summary>任务类型</summary>
        public InferenceTaskType TaskType { get; set; }

        /// <summary>分类结果（降序，[0] 为 Top-1）</summary>
        public List<ClassificationLabel> Classification { get; set; }

        /// <summary>检测框（已过阈值 + NMS，原图坐标）</summary>
        public List<DetectionBox> Detections { get; set; }

        /// <summary>分割概率图（模型输出分辨率；异常检测任务复用放归一化热力图）</summary>
        public SegmentationMask Segmentation { get; set; }

        /// <summary>异常检测任务：整图异常分数（原始值，越大越异常，无固定值域）</summary>
        public double AnomalyScore { get; set; }

        /// <summary>异常检测任务：分数是否超过阈值判为 NG</summary>
        public bool IsAnomaly { get; set; }

        /// <summary>
        /// HALCON 显示/量测对象（object 类型避免公共 API 暴露 halcondotnet 强类型，MC1000 约束）：
        /// 检测=所有框的 Region 并集；分割=二值化后放大到原图尺寸的 Region；分类=Top1 名称。
        /// </summary>
        public object ResultRegion { get; set; }

        /// <summary>ResultRegion 是否为有效 HALCON Region（供节点层判断，避免其引用 HalconDotNet）</summary>
        public bool HasRegion { get; set; }

        /// <summary>检出数量（分类=Top1 类别号；检测=框数；分割=Region 面积>0 时为 1）</summary>
        public int Count { get; set; }
    }

    /// <summary>
    /// 深度学习推理工具：HALCON 图像 ↔ AI 推理引擎 的桥接层。
    ///
    /// 【职责】
    ///   1. HObject → InferenceInput：GetImagePointer1/3 取裸像素 + Marshal.Copy 立即拷出
    ///      （【铁律】HALCON 的原生内存在其 GC 期间可能被移动，绝不能跨调用持有裸指针，
    ///       必须在同一个同步块里立即 Copy 成托管数组，拷完再干别的）；
    ///   2. 通过 InferenceProviderRegistry 取推理引擎（自动扫描 Plugins.Inference.*.dll）；
    ///   3. InferenceOutput → HALCON 对象：检测框 GenRectangle1、分割 mask/异常热力图
    ///      GenImage1+Threshold+ZoomRegion；
    ///   4. finally 无条件 Dispose 本地句柄（对齐标定服务的算子代码模板，零所有权负担）。
    /// </summary>
    public static class DeepLearningTool
    {
        /// <summary>超过该阈值的分类结果视为 NG（分类任务的便捷判定）</summary>
        public const float DEFAULT_NG_THRESHOLD = 0.5f;

        public static DlInferenceResult RunInference(object image, DlInferenceRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ModelPath))
                return Fail("推理请求为空或未配置模型路径");

            var hobj = image as HObject;
            if (hobj == null || !hobj.IsInitialized())
                return Fail("输入图像为空或不是有效的 HObject");

            // ---------- 1) 解析推理引擎插件 ----------
            // GetDefault 内部会自动扫描运行目录的 Plugins.Inference.*.dll（幂等，只扫一次）
            IInferenceProvider provider = InferenceProviderRegistry.GetDefault();
            if (provider == null)
                return Fail("未找到 AI 推理引擎插件：请确认 Plugins.Inference.OnnxRuntime.dll " +
                            "及 onnxruntime.dll 已随程序部署到运行目录");

            // ---------- 2) 图像像素拷出（HObject → 托管 float[]） ----------
            InferenceInput input;
            try
            {
                input = ExtractPixels(hobj);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(DeepLearningTool), "图像像素提取失败", ex);
                return Fail("图像像素提取失败: " + ex.Message);
            }

            // ---------- 3) 加载模型 + 推理 ----------
            var options = new InferenceModelOptions
            {
                ModelPath = request.ModelPath,
                TaskType = request.TaskType,
                ConfidenceThreshold = (float)request.ConfidenceThreshold,
                AnomalyRegionThreshold = (float)request.AnomalyRegionThreshold,
                IouThreshold = (float)request.IouThreshold,
                UseGpu = request.UseGpu,
                Labels = request.Labels ?? new List<string>(),
                LayoutHint = string.IsNullOrEmpty(request.LayoutHint) ? "auto" : request.LayoutHint
            };

            var loadResult = provider.Load(options); // 同路径重复调用幂等，只刷新参数
            if (!loadResult.Success)
                return Fail("模型加载失败: " + loadResult.Message);

            InferenceOutput output = provider.Run(input);
            if (output == null || !output.Success)
                return Fail(output != null ? output.Message : "推理返回空结果");

            // ---------- 4) 推理结果 → HALCON 对象 ----------
            try
            {
                return BuildResult(output, input.Width, input.Height);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(DeepLearningTool), "推理结果转换失败", ex);
                return Fail("推理结果转换失败: " + ex.Message);
            }
        }

        // ==================================================================
        // HObject → InferenceInput
        // ==================================================================

        /// <summary>
        /// 把 HALCON 图像拷贝成契约层的输入结构。
        /// 支持：单通道灰度 byte、三通道 RGB byte；其他位深自动转 byte 再取。
        /// </summary>
        private static InferenceInput ExtractPixels(HObject image)
        {
            // 多对象容器只取第一张（流程上游通常是单图输出）
            HOperatorSet.CountObj(image, out HTuple countT);
            if (countT.I > 1)
            {
                HOperatorSet.SelectObj(image, out HObject first, 1);
                try
                {
                    return ExtractSingleImagePixels(first);
                }
                finally
                {
                    first.Dispose(); // 像素已在内部拷出，句柄立即归还
                }
            }

            return ExtractSingleImagePixels(image);
        }

        private static InferenceInput ExtractSingleImagePixels(HObject image)
        {
            // 位深归一化：非 byte 图（int2/real 等）先转 byte，GetImagePointer 才能按 1 字节取
            HOperatorSet.GetImagePointer1(image, out _, out HTuple type, out _, out _);
            HObject work = image;
            HObject converted = null;
            if (type.S != "byte")
            {
                HOperatorSet.ConvertImageType(image, out converted, "byte");
                work = converted;
            }

            try
            {
                HTuple channelsT;
                HOperatorSet.CountChannels(work, out channelsT);
                int channels = channelsT.I;

                if (channels >= 3)
                {
                    // 三通道：HOperatorSet.GetImagePointer3 返回 R/G/B 三个平面指针
                    // （指针在 HTuple 里，用 .IP 取 IntPtr）
                    HOperatorSet.GetImagePointer3(work, out HTuple pR, out HTuple pG, out HTuple pB,
                        out _, out HTuple wT, out HTuple hT);
                    int w3 = wT.I, h3 = hT.I;

                    int n = w3 * h3;
                    var r = new byte[n];
                    var g = new byte[n];
                    var b = new byte[n];
                    // 【铁律】拿到指针后立即 Marshal.Copy，不做任何 HALCON 调用，
                    // 防止 HALCON GC 在期间移动原生内存
                    Marshal.Copy(pR.IP, r, 0, n);
                    Marshal.Copy(pG.IP, g, 0, n);
                    Marshal.Copy(pB.IP, b, 0, n);

                    // 平面 → HWC 交错排布（契约层的统一格式）
                    var pixels = new float[n * 3];
                    for (int i = 0; i < n; i++)
                    {
                        pixels[i * 3] = r[i];
                        pixels[i * 3 + 1] = g[i];
                        pixels[i * 3 + 2] = b[i];
                    }
                    return InferenceInput.Create(w3, h3, InferencePixelFormat.Rgb, pixels);
                }
                else
                {
                    // 灰度：GetImagePointer1（单平面指针）
                    HOperatorSet.GetImagePointer1(work, out HTuple p, out _, out HTuple wT1, out HTuple hT1);
                    int w1 = wT1.I, h1 = hT1.I;

                    int n = w1 * h1;
                    var gray = new byte[n];
                    Marshal.Copy(p.IP, gray, 0, n); // 同上：立即拷出

                    var pixels = new float[n];
                    for (int i = 0; i < n; i++) pixels[i] = gray[i];
                    return InferenceInput.Create(w1, h1, InferencePixelFormat.Gray, pixels);
                }
            }
            finally
            {
                if (converted != null) converted.Dispose();
            }
        }

        // ==================================================================
        // InferenceOutput → DlInferenceResult（含 HALCON 显示对象）
        // ==================================================================

        private static DlInferenceResult BuildResult(InferenceOutput output, int srcW, int srcH)
        {
            var result = new DlInferenceResult
            {
                Success = true,
                Message = output.Message,
                ElapsedMs = output.ElapsedMs,
                TaskType = output.TaskType,
                Classification = output.Classification,
                Detections = output.Detections,
                Segmentation = output.Segmentation,
                AnomalyScore = output.AnomalyScore,
                IsAnomaly = output.IsAnomaly
            };

            switch (output.TaskType)
            {
                case InferenceTaskType.Classification:
                    BuildClassificationRegion(result);
                    break;

                case InferenceTaskType.ObjectDetection:
                    BuildDetectionRegion(result);
                    break;

                case InferenceTaskType.Segmentation:
                    BuildSegmentationRegion(result, srcW, srcH);
                    break;

                case InferenceTaskType.AnomalyDetection:
                    BuildAnomalyRegion(result, srcW, srcH);
                    break;
            }

            return result;
        }

        /// <summary>分类：Top-1 结果摘要（ResultRegion 存名称文本，便于下游显示/记录）</summary>
        private static void BuildClassificationRegion(DlInferenceResult result)
        {
            if (result.Classification == null || result.Classification.Count == 0)
            {
                result.Message = "分类输出为空";
                return;
            }

            var top = result.Classification[0];
            result.Count = 1;
            result.Message = string.Format("{0}: {1:P1}", top.ClassName, top.Score);

            // 没有空间对象可生成；Top-1 名称放 ResultRegion 供下游文本输出
            result.ResultRegion = top.ClassName;
        }

        /// <summary>检测：所有框 GenRectangle1 → ConcatObj 合成一个 Region 容器</summary>
        private static void BuildDetectionRegion(DlInferenceResult result)
        {
            var boxes = result.Detections;
            if (boxes == null || boxes.Count == 0)
            {
                result.Count = 0;
                result.Message = "未检出缺陷";
                return;
            }

            HObject merged = null;
            try
            {
                foreach (var box in boxes)
                {
                    HObject rect;
                    HOperatorSet.GenRectangle1(out rect,
                        Math.Max(0, box.Y1), Math.Max(0, box.X1),
                        Math.Max(0.01, box.Y2), Math.Max(0.01, box.X2));

                    if (merged == null)
                        merged = rect;
                    else
                    {
                        HOperatorSet.ConcatObj(merged, rect, out HObject tmp);
                        rect.Dispose();
                        merged.Dispose();
                        merged = tmp;
                    }
                }

                result.Count = boxes.Count;
                result.ResultRegion = merged; // 所有权移交结果对象，由调用方/引擎负责生命周期
                result.HasRegion = true;
                merged = null;
            }
            finally
            {
                if (merged != null) merged.Dispose();
            }
        }

        /// <summary>
        /// 分割：概率图 → byte 图 → Threshold 二值 Region → ZoomRegionSize 放大到原图尺寸。
        /// 【映射说明】mask 是模型输出分辨率（如 640×640），Region 放大后才能贴合原图。
        /// </summary>
        private static void BuildSegmentationRegion(DlInferenceResult result, int srcW, int srcH)
        {
            var mask = result.Segmentation;
            if (mask == null || mask.ScoreMap == null)
            {
                result.Message = "分割输出为空";
                return;
            }

            HObject img = null, region = null, zoomed = null;
            var handle = GCHandle.Alloc(new byte[0], GCHandleType.Pinned); // 占位，下面重建
            try
            {
                int n = mask.MaskWidth * mask.MaskHeight;
                var bytes = new byte[n];
                float thr = mask.Threshold;
                bool anyDefect = false;
                for (int i = 0; i < n; i++)
                {
                    if (mask.ScoreMap[i] >= thr) { bytes[i] = 255; anyDefect = true; }
                    else bytes[i] = 0;
                }

                if (!anyDefect)
                {
                    result.Count = 0;
                    result.Message = "分割未检出缺陷区域";
                    return;
                }

                // byte[] → HALCON 图：固定后用 HImage("byte",w,h,IntPtr) 构造
                // （等价 GenImage1，内部会拷贝像素数据）
                handle.Free();
                handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                img = new HImage("byte", mask.MaskWidth, mask.MaskHeight, handle.AddrOfPinnedObject());

                HOperatorSet.Threshold(img, out region, 128, 255);

                // 放大到原图尺寸：HALCON 19.11 只有 ZoomRegion（按"缩放系数"），
                // 没有 ZoomRegionSize（按"目标尺寸"，那是 23.xx 的新算子）
                double scaleX = (double)srcW / mask.MaskWidth;
                double scaleY = (double)srcH / mask.MaskHeight;
                HOperatorSet.ZoomRegion(region, out zoomed, scaleX, scaleY);

                result.Count = 1;
                result.ResultRegion = zoomed; // 所有权移交
                result.HasRegion = true;
                zoomed = null;
                result.Message = "检出缺陷区域";
            }
            finally
            {
                if (zoomed != null) zoomed.Dispose();
                if (region != null) region.Dispose();
                if (img != null) img.Dispose();
                if (handle.IsAllocated) handle.Free();
            }
        }

        /// <summary>
        /// 异常检测：归一化热力图 → 二值 Region → 放大到原图尺寸。
        /// 【业务语义】只有判 NG（分数 ≥ 阈值）才输出 Region——
        /// 归一化热力图永远有最大值 1.0，若不看分数直接出区域，OK 图也会"框出最异常处"，
        /// 那对下游"有 Region 即 NG"的流程约定是误报。OK 时 Region 为空、Count=0。
        /// 无热力图的纯分数模型（单标量输出）：只做 NG/OK 判定，无区域输出。
        /// </summary>
        private static void BuildAnomalyRegion(DlInferenceResult result, int srcW, int srcH)
        {
            // NG 判定线在插件侧已比过（IsAnomaly = 分数 ≥ ConfidenceThreshold）
            if (!result.IsAnomaly)
            {
                result.Count = 0;
                result.HasRegion = false;
                // Message 保留插件生成的 "异常分数 x < 阈值 y → OK"
                return;
            }

            var mask = result.Segmentation;
            if (mask == null || mask.ScoreMap == null)
            {
                // 纯分数模型：NG 但无热力图，无区域可给
                result.Count = 1; // Count=1 表示 NG，下游用 DetectionResults 文本判断更稳
                return;
            }

            // 热力图 → 二值化 Region（复用分割的模板：byte 图 → Threshold → ZoomRegion 放大）
            HObject img = null, region = null, zoomed = null;
            var handle = GCHandle.Alloc(new byte[0], GCHandleType.Pinned);
            try
            {
                int n = mask.MaskWidth * mask.MaskHeight;
                var bytes = new byte[n];
                float thr = mask.Threshold; // 归一化热力图的二值化阈值（AnomalyRegionThreshold）
                bool any = false;
                for (int i = 0; i < n; i++)
                {
                    if (mask.ScoreMap[i] >= thr) { bytes[i] = 255; any = true; }
                    else bytes[i] = 0;
                }

                if (any)
                {
                    handle.Free();
                    handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    img = new HImage("byte", mask.MaskWidth, mask.MaskHeight, handle.AddrOfPinnedObject());
                    HOperatorSet.Threshold(img, out region, 128, 255);

                    double scaleX = (double)srcW / mask.MaskWidth;
                    double scaleY = (double)srcH / mask.MaskHeight;
                    HOperatorSet.ZoomRegion(region, out zoomed, scaleX, scaleY);

                    result.ResultRegion = zoomed; // 所有权移交
                    result.HasRegion = true;
                    zoomed = null;
                }

                result.Count = 1; // NG
                // Message 保留插件生成的 "异常分数 x ≥ 阈值 y → NG"
            }
            finally
            {
                if (zoomed != null) zoomed.Dispose();
                if (region != null) region.Dispose();
                if (img != null) img.Dispose();
                if (handle.IsAllocated) handle.Free();
            }
        }

        private static DlInferenceResult Fail(string message)
        {
            LogBus.Warn(nameof(DeepLearningTool), message);
            return new DlInferenceResult { Success = false, Message = message };
        }
    }
}
