using System;
using System.Collections.Generic;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Abstractions
{
    /// <summary>
    /// ★ 特征提取门面。
    ///
    /// 它<b>只吃裸帧（byte[]）与像素坐标</b>，因此：
    ///   · 视图模型/编排器完全不知道 HALCON 存在（显示控件的公共面里不能出现 HWindow，同理）；
    ///   · 算法层可以拿一个"假提取器"离线跑完整流程（回归测试无需相机、无需 HALCON）。
    ///
    /// 实现方（<c>Imaging/HalconMarkExtractor</c>）必须遵守主项目已验证的提取纪律：
    ///   ROI 局部搜索优先 → 多候选择近 → 全图降级重试 → 参考半径 ±40% 滤伪 → 质量分报告。
    /// 少一条就是能力倒退。
    /// </summary>
    public interface IFeatureExtractor
    {
        /// <summary>是否仿真实现（界面要如实告诉用户"这里的识别是真跑的，图是合成的"）。</summary>
        bool IsSimulated { get; }

        /// <summary>
        /// 当前参考 Mark 半径（px）。&lt;=1 表示尚未记录。
        /// ★ 它是<b>跨调用状态</b>：由首个成功识别的圆自动写入，此后用于滤掉半径不符的伪特征
        ///   （反光点/螺丝/字符）。换板或换相机必须调用 <see cref="ResetMarkReference"/>。
        /// </summary>
        double ReferenceRadiusPx { get; }

        /// <summary>重置参考半径（换板 / 换相机 / 重进标定步骤时必须调）。</summary>
        void ResetMarkReference();

        /// <summary>
        /// 提取一次特征。
        /// </summary>
        /// <param name="rawGray">单通道 8 位灰度裸帧。</param>
        /// <param name="expectedPixelX">期望位置（列 = 图像 X）。&lt;=0 表示无期望 → 全图搜索。</param>
        /// <param name="expectedPixelY">期望位置（行 = 图像 Y）。&lt;=0 表示无期望。</param>
        /// <param name="sampleIndex">采样点序号（从 1 开始；纯预览传 0）。</param>
        /// <param name="trace">过程轨迹输出（可为 null = 不需要过程叠加）。</param>
        CalibObservation Extract(byte[] rawGray, int width, int height, MarkSpec spec,
            double expectedPixelX, double expectedPixelY, int sampleIndex, ExtractionTrace trace);
    }

    /// <summary>
    /// 模板训练门面（"框选 → 训练 → 预览"里的中间那一步）。
    /// ★ 模板库由工具<b>自建</b>，独立模式下完全可用。
    /// </summary>
    public interface ITemplateTrainer
    {
        /// <summary>已训练模板的键列表。</summary>
        IList<string> TemplateKeys { get; }

        bool HasTemplate(string key);

        TemplateSpec GetTemplate(string key);

        /// <summary>从一帧 + 一个 ROI 训练模板。<paramref name="spec"/> 的 Key/ROI 必须已填。</summary>
        bool Train(byte[] rawGray, int width, int height, TemplateSpec spec, out string error);

        /// <summary>移除模板（释放原生句柄）。</summary>
        void RemoveTemplate(string key);

        /// <summary>把模板落盘（.shm / .ncm）。</summary>
        bool TrySave(string key, string path, out string error);

        /// <summary>从盘上读回模板。</summary>
        bool TryLoad(string key, string path, string modelKind, out string error);
    }

    /// <summary>
    /// ★ 宿主模板库接缝（可选注入）。
    /// 嵌入模式下主项目已有全局模板库，能复用就复用（避免"同一个工件在工具里教一遍又一遍"）；
    /// 独立模式下为 null → 走工具自建库。
    /// <b>null 是合法状态，不是错误。</b>
    /// </summary>
    public interface IExternalTemplateLibrary
    {
        /// <summary>模板库名称（日志/产物溯源用）。</summary>
        string Name { get; }

        IList<string> ListKeys();

        /// <summary>用宿主模板在给定帧上匹配。失败返回 false 并给出原因。</summary>
        bool TryMatch(string key, byte[] rawGray, int width, int height,
            double expectedPixelX, double expectedPixelY,
            out Vec2 pixel, out double score, out string error);
    }
}
