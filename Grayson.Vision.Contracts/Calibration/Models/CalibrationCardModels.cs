//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationCardModels.cs
// 说 明: 标定任务卡视图模型（P3 标定中心收敛；2026-09-05）。
//        背景：拍板①"一任务一 Artifact"、拍板④"发布门禁 + 旁路留痕"落地到标定中心——
//        旧"一条 profile + 三个平行大按钮(对针/校验/向导)"收敛为"profile → 0..N 张
//        任务卡(按物理量拆分) + 卡内动作(引导/校验/对针/发布/重新标定)"。
//        状态机（派生态，见 CalibrationCardDeriver）：
//          Draft → SampleComplete → Verified → Published；布局不适用/依赖失效 → Expired。
//        ⚠ 派生视图先行：状态由 profile 现存字段 + VerificationRecords(Kind) 推断，
//        不新建 Artifact 表（破坏式"Profile 表 → Artifact 表"迁移留 P4 现场做）。
//===================================================================================
using Grayson.Vision.Contracts.Calibration.Models;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>
    /// 标定任务卡（派生视图模型）：一张卡 = 一条物理量产物（H/e/t/s/畸变），
    /// 承载量徽标、布局/槽/吸嘴、采集路径、生命周期状态与依赖 H 状态。
    /// 纯数据 DTO（不依赖 WPF），UI 侧包装为 VM 显示/命令。
    /// </summary>
    public class CalibrationCardModel
    {
        /// <summary>产物稳定键（{StationCode}|{Quantity}|{Slot|nNozzle}；对标 CalibrationArtifact.ArtifactId）</summary>
        public string ArtifactId { get; set; }

        /// <summary>归属旧 profile（未建档任务卡为 null）</summary>
        public string ProfileId { get; set; }
        public string ProfileName { get; set; }

        public CalibrationQuantity Quantity { get; set; }

        /// <summary>量徽标（H/e/t/s/畸变，卡片角标用）</summary>
        public string QuantityBadge { get; set; }

        /// <summary>量中文名（如 手眼矩阵 H / 旋转偏心 e / 工具对针 t / 像素当量 s）</summary>
        public string QuantityText { get; set; }

        public EyeMode Layout { get; set; }

        /// <summary>眼在手上 / 眼在手外</summary>
        public string LayoutText { get; set; }

        /// <summary>相机槽键（相机级产物）</summary>
        public string SlotKey { get; set; }

        /// <summary>吸嘴键（工具级产物）</summary>
        public string NozzleKey { get; set; }

        /// <summary>作用域人读（"相机槽 Cam_01" / "吸嘴 2"）</summary>
        public string ScopeText { get; set; }

        /// <summary>采集路径</summary>
        public CalibrationAcquirePath PrimaryPath { get; set; }

        /// <summary>采集路径人读</summary>
        public string PathText { get; set; }

        /// <summary>主线必做？（false=可选项，如 EyeToHand 的 t，卡可跳过）</summary>
        public bool IsRequired { get; set; }

        /// <summary>为什么需要它（档案证据 / 迁移说明）</summary>
        public string Reason { get; set; }

        /// <summary>产物状态（Draft/SampleComplete/Verified/Published/Expired）</summary>
        public CalibrationArtifactState State { get; set; }

        /// <summary>状态人读（草稿/采样完成/已验证/已发布/已过期）</summary>
        public string StateText { get; set; }

        /// <summary>状态详情（过期原因 / 待办提示 / 旁路留痕）</summary>
        public string StateDetail { get; set; }

        /// <summary>是否为旁路发布（留痕展示用）</summary>
        public bool BypassPublished { get; set; }

        /// <summary>依赖产物 Id（e/t 依赖 H；独立任务为空）</summary>
        public string DependentArtifactId { get; set; }

        /// <summary>依赖是否可用（H 已发布/已验证/采样完成 = 可配合）</summary>
        public bool DepOk { get; set; } = true;

        /// <summary>依赖状态人读（"依赖 H ✓ 已发布" / "依赖 H ✗ 未完成，先标 H 段" / 空=无依赖）</summary>
        public string DepText { get; set; }

        /// <summary>是否有可用数据（State != Draft：可校验/可发布；Draft 只能引导首标）</summary>
        public bool HasData { get; set; }

        /// <summary>EyeInHand 布局过期对针（t 卡专有：相机与吸嘴同体 → 图像对针不成立）</summary>
        public bool EyeInHandTExpired { get; set; }

        /// <summary>最近一次校验记录时间（null=无）——状态卡辅助展示</summary>
        public System.DateTime? LatestVerifiedTime { get; set; }
    }

    /// <summary>任务卡显示辅助（静态文字映射；UI 与 runner 断言共用）</summary>
    public static class CalibrationCardText
    {
        /// <summary>量徽标（ASCII，runner/断言友好）</summary>
        public static string BadgeOf(CalibrationQuantity q)
        {
            switch (q)
            {
                case CalibrationQuantity.HandEye: return "H";
                case CalibrationQuantity.ToolRotation: return "e";
                case CalibrationQuantity.ToolOffset: return "t";
                case CalibrationQuantity.PixelScale: return "s";
                default: return "畸变";
            }
        }

        /// <summary>量中文名</summary>
        public static string NameOf(CalibrationQuantity q)
        {
            switch (q)
            {
                case CalibrationQuantity.HandEye: return "手眼矩阵 H";
                case CalibrationQuantity.ToolRotation: return "旋转偏心 e";
                case CalibrationQuantity.ToolOffset: return "工具对针 t";
                case CalibrationQuantity.PixelScale: return "像素当量 s";
                default: return "镜头畸变（预留）";
            }
        }

        /// <summary>状态人读</summary>
        public static string StateOf(CalibrationArtifactState s)
        {
            switch (s)
            {
                case CalibrationArtifactState.Draft: return "草稿";
                case CalibrationArtifactState.SampleComplete: return "采样完成";
                case CalibrationArtifactState.Verified: return "已验证";
                case CalibrationArtifactState.Published: return "已发布";
                case CalibrationArtifactState.Expired: return "已过期";
                default: return "未知";
            }
        }

        /// <summary>布局人读</summary>
        public static string LayoutOf(EyeMode m)
        {
            return m == EyeMode.EyeInHand ? "眼在手上" : "眼在手外";
        }

        /// <summary>采集路径人读</summary>
        public static string PathOf(CalibrationAcquirePath p)
        {
            switch (p)
            {
                case CalibrationAcquirePath.NozzleTruthWalk: return "吸嘴真值走位";
                case CalibrationAcquirePath.CameraTruthWalk: return "相机真值走位";
                case CalibrationAcquirePath.PickPlaceReturn: return "吸放回拍";
                case CalibrationAcquirePath.FlyPixelScale: return "飞拍当量";
                case CalibrationAcquirePath.RotatePickPlace: return "旋转吸放采样";
                case CalibrationAcquirePath.RotateCameraView: return "旋转观测采样";
                case CalibrationAcquirePath.AlignTool: return "图像对针";
                case CalibrationAcquirePath.ScaleWalk: return "标距走位";
                default: return "-";
            }
        }
    }
}
