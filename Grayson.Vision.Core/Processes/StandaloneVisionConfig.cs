//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StandaloneVisionConfig.cs
// 说 明: 「独立视觉任务引擎 StandaloneVision」配置（stage9-2a，2026-09-09）。
//        定位：深度学习推理 / 外观测量 等【不依赖工位硬件】任务的运行载体——
//        由工位监视【▶ 启动】+ 定时器触发源驱动：每周期取一张本地图 → 视觉链
//        （ReadImageFile → DlInference …）→ 判据 → CSV/日志，无运动/IO/相机依赖。
//        配置语义 = 判据关键词与结果输出，不承载节点链（链在配方，由 FlowEdit 编排）。
//===================================================================================
namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 独立视觉任务配置（ProcessConfigJson 字段级补丁叠加，改字段即生效）。
    /// </summary>
    public class StandaloneVisionConfig
    {
        /// <summary>
        /// 周期结果 CSV 落盘路径（相对运行目录或绝对路径）。
        /// 每周期追加一行：时间,工位,图像文件,判定,摘要（含检出错）。空=不写 CSV。
        /// </summary>
        public string OutputCsvPath { get; set; } = @"Config\StandaloneOutput\{StationId}.csv";

        /// <summary>是否写周期 CSV（默认开）</summary>
        public bool EnableCsv { get; set; } = true;

        /// <summary>
        /// 判据关键词：结果摘要包含该词 → NG。
        /// 分类/异常检测任务由推理节点输出 "…→ NG/OK"，默认 "NG" 即覆盖；
        /// 缺陷检测（检测/分割类）请配合 NgOnDetect 使用。
        /// </summary>
        public string NgKeyword { get; set; } = "NG";

        /// <summary>判据关键词：结果摘要包含该词 → 直接 OK（默认覆盖 "未检出" 的 OK 语义）</summary>
        public string OkKeyword { get; set; } = "未检出";

        /// <summary>
        /// 检测/分割/异常类任务：推理有检出（摘要非"未检出/无…"）即判 NG（缺陷/异常语义）。
        /// 若你的模型检出的是"ok 类"正类而非缺陷，请置 false 改走关键词判据。
        /// </summary>
        public bool NgOnDetect { get; set; } = true;

        /// <summary>摘要为空时的默认判定（true=OK，false=NG；建议 false 便于发现链路异常）</summary>
        public bool EmptySummaryAsOk { get; set; } = false;

        /// <summary>
        /// 分类任务：摘要以 "{OkClassName}:" 开头 → OK（如镁片分类模型 "ok"）。
        /// 模板级 VerdictRule 优先于此处；留空=不启用类名前缀判据。
        /// </summary>
        public string OkClassName { get; set; }

        /// <summary>分类任务：摘要以 "{NgClassName}:" 开头 → NG（如 "ng"）。</summary>
        public string NgClassName { get; set; }

        /// <summary>周期日志前缀（工位日志检索用）</summary>
        public string LogTag { get; set; } = "独立视觉";
    }
}
