//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationProfileSanityCheck.cs
// 说 明: 工位档案自洽校验（2026-09-05 P3 骨架：档案→标定推导的前置门）。
//        背景：档案 CameraSlots 由向导手工填写，出现过"下固定/上固定相机却标
//        MovesWithActuator=true"的矛盾——固定相机不可能随执行机构移动，照此推导
//        会把"手眼走位式"错误套到固定相机上（走位方向/镜像语义全错）。
//        职责：静态纯函数给出 修正(Fix)/警告(Warning) 清单；ApplyAndPersist 负责
//        落盘写回（用户拍板：校验+修正写回，而非仅警告）。
//        边界：只修正"语义可自证的矛盾字段"（安装方式↔随动标志），其余仅警告不擅改。
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
    ///   R1 相机槽安装方式 ↔ MovesWithActuator 联动（Fix）：
    ///      固定安装(上固定/下固定/侧斜)→ false；随动安装(眼在手上/随执行机构)→ true；
    ///      null 且可判定 → 按安装方式填齐（消除推导二义）。
    ///   R2 斜拍光轴 → 畸变前置提示（Warning，不擅改）
    ///   R3 槽 Purpose=飞拍纠偏 但 ShootMode≠飞拍 → 一致性提示（Warning）
    ///   R4 单相机旧字段 CameraMount 与首槽 CameraSlots 语义并存时 → 提示优先消费槽（Warning）
    ///   R5 未填执行机构 → 提示轴约定无法预设（Warning）
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

            // —— R1 槽级 安装方式 ↔ 随动标志 ——
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

                    if (fixedMount && slot.MovesWithActuator == true)
                    {
                        slot.MovesWithActuator = false;
                        r.Fixes.Add($"{slotTag}：安装为「{install}」（固定相机不会随执行机构移动），随动标志 true→false");
                    }
                    else if (movingMount && slot.MovesWithActuator == false)
                    {
                        slot.MovesWithActuator = true;
                        r.Fixes.Add($"{slotTag}：安装为「{install}」（随执行机构移动），随动标志 false→true");
                    }
                    else if (slot.MovesWithActuator == null && (fixedMount || movingMount))
                    {
                        slot.MovesWithActuator = fixedMount ? false : true;
                        r.Fixes.Add($"{slotTag}：随动标志缺失，按安装方式「{install}」补填 {slot.MovesWithActuator.Value}");
                    }

                    // —— R2 斜拍 → 畸变前置提示 ——
                    string axis = slot.AxisToSurface ?? string.Empty;
                    if (axis.Contains("斜") || install.Contains("斜"))
                    {
                        r.Warnings.Add($"{slotTag}：斜拍成像存在透视/畸变，建议先做畸变矫正（CameraLensDistortion 预留，当前仅提示）；垂直光轴标定更稳");
                    }

                    // —— R3 飞拍纠偏一致性 ——
                    string purpose = slot.Purpose ?? string.Empty;
                    if ((purpose.Contains("飞拍") || purpose.Contains("纠偏"))
                        && !string.Equals(slot.ShootMode, "飞拍", StringComparison.Ordinal)
                        && !string.Equals(slot.ShootMode, "飞拍", StringComparison.OrdinalIgnoreCase))
                    {
                        r.Warnings.Add($"{slotTag}：用途为「{purpose}」但拍照方式=「{slot.ShootMode ?? "(未填)"}」，飞拍纠偏应在运动中成像，请现场确认");
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
