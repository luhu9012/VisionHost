using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Grayson.Vision.Contracts.Ai;
using Grayson.Vision.Contracts.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Grayson.Vision.Plugins.Inference.OnnxRuntime
{
    /// <summary>
    /// ONNX Runtime 推理引擎插件（IInferenceProvider 的默认实现，Step-1）。
    ///
    /// 【职责】加载 .onnx 模型 → 把契约层的 InferenceInput（HWC float 0~255）
    /// 预处理成模型要的 NCHW 归一化张量 → 跑会话 → 按任务类型后处理成
    /// 契约层的 InferenceOutput（坐标已换算回原图）。
    ///
    /// 【支持的模型】（导出 ONNX 即可，训练侧自由）：
    ///   - 分类：resnet / efficientnet / yolov8-cls 等任意 [1, nc] 输出的模型
    ///   - 检测：yolov5 / yolov8-det（输出 [1,N,5+nc] 或 [1,4+nc,N]）
    ///   - 分割：单输出 [1,1,H,W] / [1,nc,H,W] 概率图（unet / pcb-seg 等）
    ///   - 异常检测：anomalib 的 PatchCore / paDiM 等（双输出"热力图+分数"或单输出热力图，
    ///     只需 OK 样本训练，阈值须用 OK 样本标定——见《AI模型训练与使用》文档）
    ///
    /// 【TODO 清单（按优先级）】
    ///   1. Letterbox 等比缩放：当前为直接拉伸（stretch），宽高比失真大的场景需改 letterbox；
    ///   2. YOLOv8-seg 双输出（检测头 + proto 掩码）拼接，当前只支持单输出分割；
    ///   3. GPU：换 Microsoft.ML.OnnxRuntime.DirectML / TensorRT 包后在此接入；
    ///   4. 模型热更新监听（文件变更自动 reload）。
    /// </summary>
    public class OnnxRuntimeInferenceProvider : IInferenceProvider
    {
        private readonly object _sync = new object();

        private InferenceSession _session;
        private InferenceModelOptions _options;

        // 模型输入的静态信息（Load 时从 ONNX 元数据解析）
        private string _inputName;
        private int _netChannels;      // 模型输入通道数（1 或 3）
        private int _netHeight;        // 模型输入高
        private int _netWidth;         // 模型输入宽
        private bool _isNhwc;          // 模型输入是否 NHWC 排布（少数 TFLite 转换模型）
        private string _outputName;

        public string ProviderId { get { return "OnnxRuntime"; } }

        public string DisplayName { get { return "ONNX Runtime 1.17.3 (CPU)"; } }

        public bool IsModelLoaded
        {
            get { lock (_sync) { return _session != null; } }
        }

        public string LoadedModelPath
        {
            get { lock (_sync) { return _options != null ? _options.ModelPath : null; } }
        }

        // ------------------------------------------------------------------
        // 模型加载
        // ------------------------------------------------------------------

        public Result Load(InferenceModelOptions options)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.ModelPath))
                return Result.Fail("模型路径为空，请在节点参数中配置 ModelPath");

            // 相对路径以程序运行目录为基准（与硬件插件的 dll 定位策略一致）
            string fullPath = Path.IsPathRooted(options.ModelPath)
                ? options.ModelPath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, options.ModelPath);

            if (!File.Exists(fullPath))
                return Result.Fail(string.Format("模型文件不存在: {0}", fullPath));

            lock (_sync)
            {
                // 幂等：同一路径重复 Load 时只刷新参数（阈值等），不重建会话
                if (_session != null && string.Equals(_options.ModelPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    _options = options;
                    _options.ModelPath = fullPath;
                    return Result.Ok();
                }

                try
                {
                    UnloadLocked();

                    // 【坑位提示】如果 onnxruntime.dll（原生库）没在程序目录，
                    // 这里会抛 DllNotFoundException —— 部署时必须带上原生 dll！
                    var session = new InferenceSession(fullPath);

                    ParseInputMetadata(session);
                    ParseOutputMetadata(session);

                    _session = session;
                    _options = options;
                    _options.ModelPath = fullPath;
                    return Result.Ok();
                }
                catch (Exception ex)
                {
                    // 加载失败必须把旧会话清掉，避免半初始化状态
                    UnloadLocked();
                    return Result.Fail(string.Format("模型加载失败: {0}", ex.Message), -2, ex);
                }
            }
        }

        /// <summary>解析模型输入元数据：输入名、通道数、输入尺寸、NCHW/NHWC 排布</summary>
        private void ParseInputMetadata(InferenceSession session)
        {
            var kv = session.InputMetadata.First();
            _inputName = kv.Key;
            var dims = kv.Value.Dimensions;

            if (dims == null || dims.Length < 3)
                throw new InvalidOperationException(
                    "不支持的模型输入形状（至少需要 3 维），当前: [" + string.Join(",", dims) + "]");

            _isNhwc = false;

            if (dims.Length == 4)
            {
                // [N,C,H,W]：第 2 维是 1/3 → NCHW；第 4 维是 1/3 → NHWC
                if (dims[1] == 1 || dims[1] == 3)
                {
                    _isNhwc = false;
                    _netChannels = dims[1];
                    _netHeight = dims[2];
                    _netWidth = dims[3];
                }
                else if (dims[3] == 1 || dims[3] == 3)
                {
                    _isNhwc = true;
                    _netChannels = dims[3];
                    _netHeight = dims[1];
                    _netWidth = dims[2];
                }
                else
                {
                    // 通道维既不是 1 也不是 3（动态维度导出时常见）——按 NCHW 猜，通道取 3
                    _isNhwc = false;
                    _netChannels = 3;
                    _netHeight = dims[2];
                    _netWidth = dims[3];
                }
            }
            else
            {
                // 3 维输入 [C,H,W]（无 batch 维）
                _isNhwc = false;
                _netChannels = dims[0];
                _netHeight = dims[1];
                _netWidth = dims[2];
            }

            // 符号维（动态尺寸）导出时 Dimensions 里是 0：用参数指定的尺寸兜底，再兜底 640
            if (_netChannels <= 0) _netChannels = 3;
            if (_netWidth <= 0) _netWidth = _options.InputSize > 0 ? _options.InputSize : 640;
            if (_netHeight <= 0) _netHeight = _options.InputSize > 0 ? _options.InputSize : 640;
        }

        private void ParseOutputMetadata(InferenceSession session)
        {
            _outputName = session.OutputMetadata.Keys.First();
        }

        // ------------------------------------------------------------------
        // 推理
        // ------------------------------------------------------------------

        public InferenceOutput Run(InferenceInput input)
        {
            lock (_sync)
            {
                if (_session == null || _options == null)
                    return InferenceOutput.Fail("模型未加载，请先调用 Load（节点会在执行前自动加载）");

                if (input == null || input.Pixels == null)
                    return InferenceOutput.Fail("输入图像数据为空");

                var sw = Stopwatch.StartNew();
                try
                {
                    // 1) 预处理：HWC(0~255) → 模型输入尺寸 → 归一化 → NCHW/NHWC
                    float[] tensorData = PreprocessHelper.ToModelTensor(
                        input, _netWidth, _netHeight, _netChannels,
                        _options.TaskType, _options.Mean, _options.Std, _isNhwc);

                    // 2) 组张量并执行会话
                    int[] shape = _isNhwc
                        ? new[] { 1, _netHeight, _netWidth, _netChannels }
                        : new[] { 1, _netChannels, _netHeight, _netWidth };

                    var tensor = new DenseTensor<float>(tensorData, shape);
                    using (var results = _session.Run(
                        new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) }))
                    {
                        sw.Stop();

                        // 3) 收集全部 float 输出（异常检测模型常为"热力图+分数"双输出，
                        //    其他任务取第一个即可——Decode 内部处理）
                        var outTensors = new List<Tensor<float>>();
                        foreach (var r in results)
                        {
                            var t = r.AsTensor<float>();
                            if (t != null) outTensors.Add(t);
                        }
                        if (outTensors.Count == 0)
                            return InferenceOutput.Fail("模型输出不是 float 张量，暂不支持");

                        // 4) 后处理：按任务类型解析输出张量（netW/netH 用于坐标还原）
                        //    【注意】必须在 using 块内完成解码——ORT 的输出张量
                        //    可能引用 results 的原生内存，出 using 块后失效
                        InferenceOutput output = PostprocessHelper.Decode(
                            outTensors.ToArray(), _options, input.Width, input.Height, _netWidth, _netHeight);

                        output.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                        return output;
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return InferenceOutput.Fail("推理异常: " + ex.Message);
                }
            }
        }

        public void Unload()
        {
            lock (_sync) { UnloadLocked(); }
        }

        private void UnloadLocked()
        {
            if (_session != null)
            {
                try { _session.Dispose(); } catch { }
                _session = null;
                _options = null;
            }
        }
    }
}
