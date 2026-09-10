//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationDomainV2.cs
// 说 明: 标定体系 v2 领域模型（2026-09-05 重设计 P0 落地，破坏式收敛）。
//        背景：旧双轨枚举（CalibrationType 6 历史值 + CalibrationTaskKind 6 族）语义重叠、
//        靠人肉映射；Profile 是"泛型容器 + 大量旁挂字段"，无产物生命周期。
//        v2 单一模型：
//          · 标定物理量 CalibrationQuantity（H/e/t/s/畸变）—— 取代双轨语义；
//          · 采集路径 CalibrationAcquirePath —— 布局决定"怎么采"，路径即向导模板选择键；
//          · 任务规格 CalibrationTaskSpec —— 档案需求 → 可执行任务（向导会话的输入）；
//          · 标定产物 CalibrationArtifact —— 一件产物 = 一条 Profile（收敛旁挂字段 +
//            State 生命周期 + 证据内聚），旧 profile 经 CalibrationLegacyMapper 迁移。
//        判据铁律（用户拍板 2026-09-05）：EyeInHand 相机与吸嘴同体 → 观测不到吸嘴尖
//        对准态 → 图像对针 t 物理不成立 → ToolOffset 只在 EyeToHand 派生（可选），
//        EyeInHand 旧对针结果迁移为 Expired。
//===================================================================================
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Calibration.Models
{
    /// <summary>
    /// 标定物理量（v2 领域名词，取代 CalibrationType/CalibrationTaskKind 双轨语义）。
    /// 归属粒度：H/s/畸变 相机级（按槽 1 条整机共享）；e/t 工具级（按吸嘴各 1 条）。
    /// </summary>
    public enum CalibrationQuantity
    {
        /// <summary>H：像素↔机械平面 2D 仿射（含方向/尺度/平移）</summary>
        HandEye = 0,

        /// <summary>e：旋转中心 (U0x,U0y) + 偏心矢量 + 基准角 U0（带角度对位补偿）</summary>
        ToolRotation = 1,

        /// <summary>t：工具尖相对相机引导点的固定偏距 ToolOffset（仅 EyeToHand 图像对针）</summary>
        ToolOffset = 2,

        /// <summary>s：像素当量（mm/px；飞拍纠偏 / 纯当量换算）</summary>
        PixelScale = 3,

        /// <summary>镜头畸变前置（预留；无真实角点检测前不可执行）</summary>
        LensDistortion = 4
    }

    /// <summary>
    /// 标定产物生命周期状态（发布门禁 v2）：
    /// Draft(建卡) → SampleComplete(采完) → Verified(残差体检过) → Published(发布可用)；
    /// 任一环节重做/设备变更/依赖过期 → Expired。
    /// </summary>
    public enum CalibrationArtifactState
    {
        Draft = 0,
        SampleComplete = 1,
        Verified = 2,
        Published = 3,
        Expired = 4
    }

    /// <summary>
    /// 采集路径（布局预筛后的采样语义；向导按此装配步骤模板）。
    /// 前缀约定：NozzleTruth*=真值=吸嘴尖落点（H 吸收工具偏距）；CameraTruth*=真值=相机中心
    /// （工具偏距需 t 补偿）；PickPlace*=真值=放料命令位（吸收工具偏距）。
    /// </summary>
    public enum CalibrationAcquirePath
    {
        /// <summary>EyeInHand 走位九点：吸嘴对格点/回拍照位，真值=吸嘴落点（H 吸收 t）</summary>
        NozzleTruthWalk = 0,

        /// <summary>EyeToHand 走位九点：平台/工件走网格，真值=相机中心（工具尖偏距需 t 补）</summary>
        CameraTruthWalk = 1,

        /// <summary>EyeToHand 吸放式：吸工件放网格命令位 → 回拍（真值=命令位，吸收 t）</summary>
        PickPlaceReturn = 2,

        /// <summary>飞拍纠偏 → 像素当量 s（固定相机对运动工件，相对偏差）</summary>
        FlyPixelScale = 3,

        /// <summary>e：吸件转 U 各角度放料 → 回拍照位测位移 → 圆拟合（EyeInHand 典型）</summary>
        RotatePickPlace = 4,

        /// <summary>e：相机固定观测旋转投影 → 圆周拟合（EyeToHand 典型）</summary>
        RotateCameraView = 5,

        /// <summary>t：固定相机对针（EyeToHand 专属；单次对准写 ToolOffset）</summary>
        AlignTool = 6,

        /// <summary>s：精拍像素当量（已知标距走位测像素）</summary>
        ScaleWalk = 7
    }

    /// <summary>
    /// 任务规格 = 档案需求 → 一次可执行标定任务的完整描述（计划引擎 v2 的产出，
    /// 向导会话与任务卡的握手对象）。纯派生视图，不落库。
    /// </summary>
    public class CalibrationTaskSpec
    {
        /// <summary>规格键（{StationCode}|{Quantity}|{Slot|Nozzle}，计划内唯一）</summary>
        public string SpecId { get; set; }

        /// <summary>归属工位代码</summary>
        public string StationCode { get; set; }

        /// <summary>标定物理量（H/e/t/s/畸变）</summary>
        public CalibrationQuantity Quantity { get; set; }

        /// <summary>相机槽键（相机级任务；工具级可空或作上下文）</summary>
        public string SlotKey { get; set; }

        /// <summary>吸嘴/工具通道键（e/t 恒为实际吸嘴号；相机级恒 "1"）</summary>
        public string NozzleKey { get; set; } = "1";

        /// <summary>布局（派生时从档案固化；向导不可自由改）</summary>
        public EyeMode Layout { get; set; }

        /// <summary>首选采集路径</summary>
        public CalibrationAcquirePath PrimaryPath { get; set; }

        /// <summary>备选采集路径（布局预筛；向导提供切换并标注差异）</summary>
        public List<CalibrationAcquirePath> AltPaths { get; set; } = new List<CalibrationAcquirePath>();

        /// <summary>依赖产物引用（e/t 依赖 H：格式 "{StationCode}|HandEye|{SlotKey}"；无依赖=空）</summary>
        public string DependentArtifactRef { get; set; }

        /// <summary>主线必做？（false=可选项，如 EyeToHand 的 t 对针，任务卡灰显/可跳过）</summary>
        public bool IsRequired { get; set; } = true;

        /// <summary>档案证据链（人读：为何需要、为何用此路径——任务卡追溯展示）</summary>
        public string Reason { get; set; }

        /// <summary>任务显示名</summary>
        public string DisplayName { get; set; }

        /// <summary>执行方式人读文案</summary>
        public string Suggestion { get; set; }

        /// <summary>推导备注/现场确认点</summary>
        public string Note { get; set; }

        /// <summary>产物键（落库后 ArtifactId；便于派生状态反查：存在且 State≥Verified = 完成）</summary>
        public string ArtifactRef
        {
            get { return CalibrationArtifact.BuildArtifactId(StationCode, Quantity, SlotKey, NozzleKey); }
        }
    }

    /// <summary>
    /// 标定产物（v2 收敛后的一条 Profile）。
    /// ArtifactId 稳定键 = {StationCode}|{Quantity}|{SlotKey|NozzleKey}：
    ///   H/s/畸变 → 槽级；e/t → 吸嘴级（NozzleKey 入键）。
    /// </summary>
    public class CalibrationArtifact
    {
        public string ArtifactId { get; set; }

        public string StationCode { get; set; }
        public string StationName { get; set; }
        public string BoundDeviceId { get; set; }

        /// <summary>备注/迁移说明（过期原因、现场确认点等）</summary>
        public string Note { get; set; }

        public CalibrationQuantity Quantity { get; set; }
        public string SlotKey { get; set; }
        public string NozzleKey { get; set; } = "1";
        public EyeMode Layout { get; set; }

        /// <summary>实际采用的采集路径（向导完成时固化）</summary>
        public CalibrationAcquirePath PrimaryPath { get; set; }

        /// <summary>依赖产物 Id（e 依赖其坐标系 H；t 依赖对针基准 H）</summary>
        public string DependentArtifactId { get; set; }

        // —— 迁移溯源（旧 Type 转 v2 时留档）——
        public string LegacyProfileId { get; set; }
        public string LegacyTypeName { get; set; }

        // ===== 结果组（按 Quantity 使用相关组；其余保持默认） =====
        /// <summary>H / s：矩阵文件路径（生产 CalibrationApply 消费协议不变）</summary>
        public string HomMatFilePath { get; set; }

        /// <summary>e：机械域旋转中心 X（mm）</summary>
        public double RotCenterX { get; set; }

        /// <summary>e：机械域旋转中心 Y（mm）</summary>
        public double RotCenterY { get; set; }

        /// <summary>e：偏心矢量 X（mm，吸嘴相对 U 回转中心）</summary>
        public double EccentricEx { get; set; }

        /// <summary>e：偏心矢量 Y（mm）</summary>
        public double EccentricEy { get; set; }

        /// <summary>e：旋转基准角 U0（°；旋转补偿 R(U−U0)·e）</summary>
        public double U0AngleDeg { get; set; }

        /// <summary>t：工具偏距 X（mm）</summary>
        public double ToolOffsetWx { get; set; }

        /// <summary>t：工具偏距 Y（mm）</summary>
        public double ToolOffsetWy { get; set; }

        /// <summary>s：像素当量（µm/px 或 mm/px，取标定时的单位约定）</summary>
        public double PixelScale { get; set; }

        /// <summary>本次标定的拍照/放料面 Z 高度（mm；矩阵对高度敏感，换高度须重标）</summary>
        public double? CalibZ { get; set; }

        /// <summary>旋转段 U 基准读数（°）</summary>
        public double? CalibU0 { get; set; }

        // ===== 采集配置 =====
        public CalibrationFeatureType FeatureType { get; set; } = CalibrationFeatureType.CircleMark;
        public string FeatureTemplateName { get; set; }
        public double TemplateMinScore { get; set; } = 0.6;

        public int BindXAxisIndex { get; set; } = 1;
        public int BindYAxisIndex { get; set; } = 3;
        public int BindRotationAxisIndex { get; set; } = 3;
        public bool InvertXAxis { get; set; }
        public bool InvertYAxis { get; set; }

        // ===== 证据与生命周期 =====
        /// <summary>采集快照（离线残差体检源；向导保存时打包）</summary>
        public List<CalibrationSampleModel> Samples { get; set; } = new List<CalibrationSampleModel>();

        /// <summary>校验记录（校验台/残差体检结果挂账）</summary>
        public List<CalibrationVerificationRecord> Verifications { get; set; } = new List<CalibrationVerificationRecord>();

        public CalibrationArtifactState State { get; set; } = CalibrationArtifactState.Draft;

        /// <summary>矩阵内容指纹（发布时记录；设备变更/文件替换可对比失效）</summary>
        public string MatrixFingerprint { get; set; }

        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? PublishedAt { get; set; }
        public string PublishedBy { get; set; }

        // ===== 便捷 =====
        public bool IsPublished => State == CalibrationArtifactState.Published;
        public bool IsUsable => State == CalibrationArtifactState.Verified || State == CalibrationArtifactState.Published;
        public bool IsExpired => State == CalibrationArtifactState.Expired;

        /// <summary>最近一次校验记录（方法形态——防序列化框架把计算属性当字段持久化，见旧坑）</summary>
        public CalibrationVerificationRecord GetLatestVerification()
        {
            CalibrationVerificationRecord best = null;
            if (Verifications == null) return null;
            foreach (var v in Verifications)
            {
                if (best == null || v.VerifiedTime > best.VerifiedTime) best = v;
            }
            return best;
        }

        /// <summary>稳定产物键（槽级：{code}|{quantity}|{slot}；吸嘴级：{code}|{quantity}|n{nozzle}）</summary>
        public static string BuildArtifactId(string stationCode, CalibrationQuantity quantity,
            string slotKey, string nozzleKey)
        {
            string scope = quantity == CalibrationQuantity.HandEye
                           || quantity == CalibrationQuantity.PixelScale
                           || quantity == CalibrationQuantity.LensDistortion
                ? (string.IsNullOrWhiteSpace(slotKey) ? "Cam_01" : slotKey)
                : "n" + (string.IsNullOrWhiteSpace(nozzleKey) ? "1" : nozzleKey);
            return string.Format("{0}|{1}|{2}",
                string.IsNullOrWhiteSpace(stationCode) ? "?" : stationCode,
                quantity.ToString(), scope);
        }
    }
}
