using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Ai
{
    /// <summary>
    /// AI 推理引擎统一契约（Step-1 落地的核心接口）。
    ///
    /// 【架构定位】对齐硬件层的 IHardwarePlugin 模式：
    ///   - Contracts 只定义接口（本文件），不引用 OnnxRuntime/TensorRT 等任何第三方库；
    ///   - 具体引擎做成独立插件程序集（如 Plugins.Inference.OnnxRuntime），
    ///     运行时由 InferenceProviderRegistry 反射扫描加载（见 InferenceProviderRegistry.cs）；
    ///   - 上层（HalconWrapper.DeepLearningTool / Nodes.DlInferenceExecutor）只面向本接口编程，
    ///     未来把 ONNX 换成 TensorRT / OpenVINO / HALCON 自带 DL，节点代码一行不用改。
    ///
    /// 【线程模型】Run 允许在后台算子线程调用（节点引擎的既有模型），
    /// 实现方需保证 Run 线程安全（至少保证同一实例串行调用安全，节点链是顺序执行的）。
    /// </summary>
    public interface IInferenceProvider
    {
        /// <summary>引擎唯一标识，如 "OnnxRuntime"、"TensorRT"、"HalconDl"（用于注册表检索）</summary>
        string ProviderId { get; }

        /// <summary>显示名称，如 "ONNX Runtime (CPU)"，供 UI 展示</summary>
        string DisplayName { get; }

        /// <summary>当前是否已加载模型</summary>
        bool IsModelLoaded { get; }

        /// <summary>当前已加载模型的路径（未加载为 null）</summary>
        string LoadedModelPath { get; }

        /// <summary>
        /// 加载模型。重复调用：若 ModelPath 与当前一致则直接复用（幂等）；
        /// 不一致则卸载旧模型再加载新模型。
        /// </summary>
        /// <param name="options">模型路径、任务类型、归一化参数、阈值等</param>
        Result Load(InferenceModelOptions options);

        /// <summary>
        /// 执行一次推理。输入图像会被缩放到模型要求尺寸（插件内部完成），
        /// 输出坐标已换算回输入图像的原图坐标系。
        /// 【约定】未 Load 就调用 Run 属于调用方错误，返回 Fail 结果。
        /// </summary>
        InferenceOutput Run(InferenceInput input);

        /// <summary>卸载模型、释放推理会话（程序退出/切换配方时调用）</summary>
        void Unload();
    }
}
