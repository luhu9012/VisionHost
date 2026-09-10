//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationProfile.cs
// 说 明: 工位方案对象（StationProfile）—— v1.1 §0.6 破坏式数据模型骨架。
//        工位 = 一个"视觉方案"对象：需求元数据（问卷答案，允许待确认/草稿）+ 结构字段 +
//        资产引用 + 派生建议（可重算）。
//        ⚠ 骨架期定位（2026-09-05 P0）：
//          · 本模型承载"向导产出/需求元数据/推导建议"，与 StationConfigModel（运行时配置）
//            并存：StationProfile 挂 StationId/StationCode 关联，不做字段级合并；
//          · 运行时仍消费 StationConfigModel，迁移到"Profile 直驱运行时"留后续 P 段；
//          · JSON 落盘 Config\StationProfiles\{StationId}.json（草稿 Draft 无 StationId 用 ProfileId 命名）。
//===================================================================================
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>方案生命周期状态：Draft=草稿（向导存草稿，未落真实工位）/ Active=已创建工位（随工位配置落盘）</summary>
    public enum StationProfileStatus
    {
        /// <summary>草稿：只落 StationProfile JSON，不建运行时工位；可被向导"载入草稿"续编</summary>
        Draft = 0,

        /// <summary>已激活：已转 StationConfigModel 落盘并（可）注册运行时；Profile 作为需求元数据存档</summary>
        Active = 1
    }

    /// <summary>
    /// 相机槽（视觉节点）需求元数据 —— 多相机工位的"每台相机装哪、干什么用"描述。
    /// 消费方：① 建议引擎按槽推导标定候选（标定中心据此批量建 Profile）；
    ///         ② 装配旅程按槽展开模板/标定待办；③ 模板/配方映射的资产归属上下文。
    /// ⚠ 边界（2026-09-05 决策）：本类只描述"静态拓扑 + 用途语义"，不承载协作时序 / 飞拍触发节奏
    ///   —— 那些属于配方流程层（RecipeModel.MainProcess 多 AcquireImage 节点已承载），禁止复制进工位静态。
    /// </summary>
    public class VisionSlotInfo
    {
        /// <summary>槽标识：Cam_A / Cam_B（工位内唯一，供装配/资产归属引用）</summary>
        public string SlotKey { get; set; }

        /// <summary>相机安装方式：眼在手上 / 上固定 / 下固定 / 侧斜拍（自由文本，见 StationProposalEngine 选项）</summary>
        public string InstallKind { get; set; }

        /// <summary>用途职能：引导定位 / 飞拍纠偏 / 尺寸测量 / 缺陷检测 / OCR / 有无检测 …</summary>
        public string Purpose { get; set; }

        /// <summary>拍照方式：精拍 / 飞拍（槽级；与旧单相机字段 ShootMode 同语义）</summary>
        public string ShootMode { get; set; }

        /// <summary>是否随执行机构移动（眼在手上=true；固定安装=false）。null=未答</summary>
        public bool? MovesWithActuator { get; set; }

        /// <summary>光轴与工面：垂直拍摄 / 斜拍（斜拍建议畸变矫正前置）</summary>
        public string AxisToSurface { get; set; }

        /// <summary>备注（相机型号倾向 / FOV 诉求等，自由文本）</summary>
        public string Remark { get; set; }
    }

    /// <summary>
    /// 需求问卷答案（v1.1 §0.2 分组字段全集；字符串字段=自由文本，未答/空 = 视为"待确认"，见 IsConfirmed 化约定）。
    /// 所有字段可空：向导可跳答；下游消费方（工作台 S 页）对空字段显示"待补"角标（T5 决议）。
    /// ⚠ 多相机/多执行机构拓扑（2026-09-05 增补）：ActuatorKind + CameraSlots 表达工位静态拓扑；
    ///   旧单相机字段 CameraMount/ShootMode 保留——无 CameraSlots 时仍按单相机消费，有槽时首槽与之语义一致（向导自动回填）。
    /// </summary>
    public class StationProfileRequirement
    {
        // —— ① 视觉任务 ——
        /// <summary>检测对象类型：定位抓取 / 尺寸测量 / 外观缺陷 / OCR·字符 / 有无检测 / 高度·3D / 多任务组合 / 其他·待确认</summary>
        public string TaskType { get; set; }

        // —— ② 成像 ——
        /// <summary>相机安装方式：眼在手上(随动) / 眼在手外(固定) —— 决定标定类型（手眼 vs 九点）</summary>
        public string CameraMount { get; set; }

        /// <summary>拍照方式：精拍(静止) / 飞拍(运动中)</summary>
        public string ShootMode { get; set; }

        /// <summary>光轴与工面：垂直拍摄 / 斜拍</summary>
        public string AxisToSurface { get; set; }

        /// <summary>打光方式：背光 / 正面环形光 / 同轴光 / 无(环境)</summary>
        public string Lighting { get; set; }

        /// <summary>光学参数（可行性校验用）：FOV(mm)</summary>
        public string FovMm { get; set; }

        /// <summary>光学参数：工作距离(mm)</summary>
        public string WorkDistanceMm { get; set; }

        /// <summary>光学参数：最小特征(mm) —— 结合 FOV/传感器推算像素当量做可行性告警</summary>
        public string MinFeatureMm { get; set; }

        // —— ③ 工具 ——
        /// <summary>工具头数量：1 / 2(双吸嘴) / 多 —— 影响工位结构与标定 Profile 数</summary>
        public string ToolHeadCount { get; set; }

        /// <summary>工具与法兰/旋转轴同心？true=同心 / false=偏心 —— 是否需要旋转中心标定+偏心补偿</summary>
        public bool? ConcentricWithRotationAxis { get; set; }

        /// <summary>角度需求：抓取/放置是否带角度 —— 影响模板角度范围与输出→旋转轴</summary>
        public string AngleNeed { get; set; }

        // —— ④ 判定与节拍 ——
        /// <summary>判定输出：OK/NG(IO/PLC) / 测量值 / MES / 引导坐标 —— 影响流程末端节点选择</summary>
        public string VerdictOutput { get; set; }

        /// <summary>节拍要求（件/分钟）—— 影响精拍/飞拍取舍与多相机并行</summary>
        public string CyclePerMin { get; set; }

        // —— ⑤ 执行机构与视觉拓扑（2026-09-05 增补；决定标定轴约定/槽级建议，不承载运行时序）——
        /// <summary>工位主力执行机构形态：机器人(SCARA/6轴) / 运动板卡直驱 / PLC+外部执行器 / 其他·待确认。
        /// 影响：标定向导的轴约定默认值（旋转轴索引、Z 槽位、走位方向检查）——Epson SCARA 与 ZMC 直驱轴语义不同，错轴有撞机风险。</summary>
        public string ActuatorKind { get; set; }

        /// <summary>相机槽列表（多相机静态拓扑）。空=单相机（走旧 CameraMount 字段）；非空时首槽与 CameraMount/ShootMode 语义一致。</summary>
        public List<VisionSlotInfo> CameraSlots { get; set; } = new List<VisionSlotInfo>();

        /// <summary>任意关键字段是否仍处于"待确认"（=问卷有留空未答项）。序列化后由消费方自行判定；
        /// 此处保留便捷计算属性（需结合 IsAnswered 规则，属性留实现层判定）。</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool HasPendingAnswers =>
            string.IsNullOrWhiteSpace(TaskType)
            || string.IsNullOrWhiteSpace(CameraMount)
            || string.IsNullOrWhiteSpace(ShootMode);
    }

    /// <summary>
    /// 工位方案对象（v1.1 §0.6 破坏式骨架）。
    /// 结构块字段与 StationConfigModel 对应（同一工位的两种视图：方案/需求 vs 运行时配置）。
    /// </summary>
    public class StationProfile
    {
        /// <summary>方案唯一标识（无工位前即草稿也有）</summary>
        public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>生命周期状态</summary>
        public StationProfileStatus Status { get; set; } = StationProfileStatus.Draft;

        /// <summary>0.5 行业模板引用（空=自由问卷）</summary>
        public string IndustryTemplateCode { get; set; }

        // —— 结构块（转 StationConfigModel 的最小字段，激活时写入）——
        public string StationId { get; set; }
        public string StationCode { get; set; }
        public string StationName { get; set; }
        public string LineId { get; set; }
        public string LineName { get; set; }
        /// <summary>是否启用（转运行时配置用，默认 true）</summary>
        public bool IsEnabled { get; set; } = true;
        /// <summary>通讯超时（ms，默认沿用 3000）</summary>
        public int TimeoutMs { get; set; } = 3000;

        // —— 需求元数据 ——
        /// <summary>0.2 问卷答案（可空=尚未填写问卷）</summary>
        public StationProfileRequirement Requirement { get; set; } = new StationProfileRequirement();

        // —— 派生建议（可重算；向导实时推导/工作台刷新时覆盖）——
        /// <summary>建议流程骨架（视觉链步骤文本描述，如"采图→匹配→坐标换算→CalibrationApply"）</summary>
        public string SuggestedFlowSkeleton { get; set; }

        /// <summary>建议标定方案（如"九点标定(眼在手外)" / "手眼标定(眼在手上)"）</summary>
        public string CalibrationSuggestion { get; set; }

        /// <summary>建议资产占位（模板*N / 示教点等，人读文本，供工作台 S 页生成待办）</summary>
        public List<string> AssetSuggestions { get; set; } = new List<string>();

        /// <summary>可行性告警（如"最小特征像素当量不足 N px"）；空=无告警</summary>
        public List<string> FeasibilityWarnings { get; set; } = new List<string>();

        // —— 审计 ——
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime UpdatedTime { get; set; } = DateTime.Now;

        /// <summary>草稿 JSON 文件名（Draft 无 StationId 用 ProfileId；Active 用 StationId 便于按工位检索）</summary>
        [Newtonsoft.Json.JsonIgnore]
        public string ProfileFileName => Status == StationProfileStatus.Draft
            ? ProfileId + ".json"
            : (string.IsNullOrWhiteSpace(StationId) ? ProfileId : StationId) + ".json";
    }
}
