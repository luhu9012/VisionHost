//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationProfileSanityCheck.cs
// 说 明: 工位档案自洽校验（2026-09-05 P3 骨架：档案→标定推导的前置门）。
//        背景：档案 CameraSlots 由向导手工填写，出现过"安装方式(InstallKind)与
//        轴跟随(AxisFollows)不一致"的矛盾——固定相机不可能随轴移动，照此推导
//        会把"手眼走位式"错误套到固定相机上（走位方向/镜像语义全错）。
//        职责：静态纯函数给出 修正(Fix)/警告(Warning) 清单；ApplyAndPersist 负责
//        落盘写回（用户拍板：校验+修正写回，而非仅警告）。
//        边界：只修正"语义可自证的矛盾字段"（安装方式↔轴跟随），其余仅警告不擅改。
//===================================================================================
using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Station.Models;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>自洽校验报告</summary>
    public class SanityCheckReport
    {
        /// <summary>已自动修正的条目（人读；空=无需修正）</summary>
        public List<string> Fixes { get; } = new List<string>();

        /// <summary>仅提示的警告（不自动改，需现场确认）</summary>
        public List<string> Warnings { get; } = new List<string>();

        public bool HasFixes => Fixes.Count > 0;
        public bool HasWarnings => Warnings.Count > 0;
        public bool IsClean => !HasFixes && !HasWarnings;
    }

    /// <summary>
    /// 档案自洽校验引擎（静态无状态）。
    /// v1 规则集：
    ///   R2 斜拍光轴 → 畸变前置提示（Warning，不擅改）
    ///   R3 槽 Purpose=飞拍纠偏 但 ShootMode≠飞拍 → 一致性提示（Warning）
    ///   R4 单相机旧字段 CameraMount 与首槽 CameraSlots 语义并存时 → 提示优先消费槽（Warning）
    ///   R5 未填执行机构 → 提示轴约定无法预设（Warning）
    ///   R6 轴跟随 AxisFollows ↔ 安装方式 InstallKind 联动（Fix）：
    ///      固定相机（上/下固定/斜拍）→ AxisFollows 恒"固定（不随动）"；眼在手上 → 须明确跟随哪些轴。
    /// ★ 2026-09-12 起：随动/固定的唯一判据是 InstallKind（MovesWithActuator 已删除），
    ///   原 R1「安装方式↔随动标志联动」规则随之移除（无需再修正冗余的随动布尔字段）。
    /// </summary>
    public static class StationProfileSanityCheck
    {
        /// <summary>只检查不落盘（静态纯函数）</summary>
        public static SanityCheckReport Analyze(StationProfile profile)
        {
            var r = new SanityCheckReport();
            if (profile == null) return r;
            var req = profile.Requirement;
            if (req == null) return r;

            // —— R2 斜拍 → 畸变前置提示 ——
            if (req.CameraSlots != null)
            {
                for (int i = 0; i < req.CameraSlots.Count; i++)
                {
                    var slot = req.CameraSlots[i];
                    if (slot == null) continue;
                    string install = slot.InstallKind ?? string.Empty;
                    string slotTag = string.IsNullOrWhiteSpace(slot.SlotKey) ? $"槽#{i + 1}" : slot.SlotKey;

                    bool fixedMount = install.Contains("固定") || install.Contains("斜");
                    bool movingMount = install.Contains("眼在手上") || install.Contains("随执行机构") || install.Contains("随动");

                    // —— R2 斜拍 → 畸变前置提示 ——
                    string axis = slot.AxisToSurface ?? string.Empty;
                    if (axis.Contains("斜") || install.Contains("斜"))
                    {
                        r.Warnings.Add($"{slotTag}：斜拍成像存在透视/畸变，建议先做畸变矫正（CameraLensDistortion 预留，当前仅提示）；垂直光轴标定更稳");
                    }

                    // —— R3 飞拍一致性 ——
                    // 只认 purpose 显式含"飞拍"：静止对位"纠偏"（精拍）不属飞拍，不应误报警。
                    string purpose = slot.Purpose ?? string.Empty;
                    if (purpose.Contains("飞拍")
                        && !string.Equals(slot.ShootMode, "飞拍", StringComparison.Ordinal)
                        && !string.Equals(slot.ShootMode, "飞拍", StringComparison.OrdinalIgnoreCase))
                    {
                        r.Warnings.Add($"{slotTag}：用途为「{purpose}」但拍照方式=「{slot.ShootMode ?? "(未填)"}」，飞拍应在运动中成像，请现场确认");
                    }

                    // —— R6 轴跟随 ↔ 安装方式 联动（2026-09-12 增补，Fix）——
                    // 固定相机（上/下固定/斜拍）→ AxisFollows 恒"固定（不随动）"；眼在手上 → 须明确跟随哪些轴。
                    string axisFollows = slot.AxisFollows ?? string.Empty;
                    if (fixedMount)
                    {
                        if (!string.IsNullOrWhiteSpace(axisFollows) && !axisFollows.Contains("固定"))
                        {
                            slot.AxisFollows = "固定（不随动）";
                            r.Fixes.Add($"{slotTag}：安装为「{install}」（固定相机不随任何轴），轴跟随「{axisFollows}」→ 固定（不随动）");
                        }
                    }
                    else if (movingMount && string.IsNullOrWhiteSpace(axisFollows))
                    {
                        r.Warnings.Add($"{slotTag}：眼在手上相机未填「轴跟随」，请明确随哪些轴（跟随XY / 跟随XYZU / 跟随XY不随ZU…），否则手眼标定消费语义可能有偏差");
                    }
                }
            }

            // —— R4 无槽单相机旧字段 ——
            bool hasSlots = req.CameraSlots != null && req.CameraSlots.Count > 0;
            if (hasSlots && !string.IsNullOrWhiteSpace(req.CameraMount))
            {
                r.Warnings.Add("档案同时存在 旧单相机字段(CameraMount) 与 相机槽列表：推导以槽列表为准，首槽安装方式应与 CameraMount 语义一致");
            }

            // —— R5 执行机构 ——
            if (string.IsNullOrWhiteSpace(req.ActuatorKind) || req.ActuatorKind.Contains("待确认"))
            {
                r.Warnings.Add("执行机构未确认：标定向导无法预设轴约定（SCARA 旋转轴=U(3)/ZMC 旋转轴=0），请回档案补填");
            }

            return r;
        }

        /// <summary>检查 + 修正写回档案（调用方确认后使用；返回是否发生修正）</summary>
        public static SanityCheckReport ApplyAndPersist(StationProfile profile)
        {
            var report = Analyze(profile); // 直接改内存对象字段
            if (report.HasFixes && profile != null)
            {
                try
                {
                    new StationProfileRepository().Save(profile); // Save 内部会刷 UpdatedTime
                }
                catch (Exception ex)
                {
                    report.Warnings.Add("修正写回失败: " + ex.Message);
                }
            }
            return report;
        }
    }
}
