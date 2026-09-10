//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationPlanEngine.cs
// 说 明: 工位档案 → 标定计划 推导引擎（2026-09-05 P3 骨架，替换"槽级一次性候选"）。
//        判据主干（档案字段 → 标定任务集）：
//          ① 用途含 引导定位/纠偏？ 否(测量/检测/OCR…)→ 免几何标定（仅模板/当量）
//          ② 相机随执行机构走且特征不丢？ 随动→走位式九点 H；固定/送拍→吸放式 H；
//             飞拍纠偏（固定相机运动中成像）→ 像素当量+相位（辅助）
//          ③ 带角度对位 且 (工具偏心 / 多吸嘴)？ → 每吸嘴一条 工具旋转 e 任务
//        维度 = 槽 × 吸嘴：相机级任务按槽 1 条（整机共享），工具级任务按吸嘴各 1 条。
//        用户拍板项（2026-09-05）：ST_001 双工位滑台（ZMC 板卡，相机随动）仅主视觉
//        一套 H —— 无槽单相机路径天然只产 1 条相机任务，符合约定。
//        兼容边界：只产出派生任务，不落库；CalibrationProfile 由标定中心任务卡按
//        SuggestType/EyeMode/NozzleKey 创建（现有持久化链不动）。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Station.Models;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>标定计划推导引擎（静态无状态；输入档案 → 输出有序任务集）</summary>
    public static class CalibrationPlanEngine
    {
        // ==================== 主入口 ====================

        /// <summary>由工位档案推导标定计划（排序：相机级 → 工具级 → 辅助；可选任务末位）</summary>
        public static StationCalibrationPlan Build(StationProfile profile)
        {
            var plan = new StationCalibrationPlan();
            if (profile == null) return plan;
            plan.StationCode = profile.StationCode;
            plan.StationName = profile.StationName;

            var req = profile.Requirement ?? new StationProfileRequirement();
            bool hasSlots = req.CameraSlots != null && req.CameraSlots.Count > 0;

            var cameraTasks = new List<CalibrationTask>();
            if (hasSlots)
            {
                foreach (var slot in req.CameraSlots)
                {
                    if (slot == null) continue;
                    var t = BuildFromSlot(slot);
                    if (t != null) cameraTasks.Add(t);
                }
            }
            else
            {
                var t = BuildFromSingleCamera(req);
                if (t != null) cameraTasks.Add(t);
            }

            var toolTasks = BuildToolTasks(req, cameraTasks);
            var planTasks = new List<CalibrationTask>();
            planTasks.AddRange(cameraTasks);
            planTasks.AddRange(toolTasks);
            // 去 DisplayName 重复/兜底
            plan.Tasks = planTasks;
            return plan;
        }
        /// <summary>主线必做任务（不含可选）；UI 空态/进度用</summary>
        public static List<CalibrationTask> RequiredTasks(StationCalibrationPlan plan)
        {
            if (plan == null) return new List<CalibrationTask>();
            return plan.Tasks.Where(t => !t.IsOptional).ToList();
        }

        // ==================== 相机级任务推导 ====================

        private static CalibrationTask BuildFromSlot(VisionSlotInfo slot)
        {
            string install = slot.InstallKind ?? string.Empty;
            string purpose = slot.Purpose ?? string.Empty;
            string shoot = slot.ShootMode ?? string.Empty;
            string slotKey = string.IsNullOrWhiteSpace(slot.SlotKey) ? "Cam_?" : slot.SlotKey;

            // —— 判据① 用途门：非引导/纠偏类用途免几何标定 ——
            if (purpose.Length > 0
                && !purpose.Contains("引导") && !purpose.Contains("定位")
                && !purpose.Contains("纠偏") && !purpose.Contains("飞拍"))
            {
                return null; // 检测/测量/OCR/有无：只出模板/当量，不出几何标定任务
            }

            bool fly = string.Equals(shoot, "飞拍", StringComparison.Ordinal)
                       || purpose.Contains("飞拍") || purpose.Contains("纠偏");
            bool moving = slot.MovesWithActuator == true
                          || install.Contains("眼在手上") || install.Contains("随执行机构") || install.Contains("随动");
            bool slant = (slot.AxisToSurface ?? string.Empty).Contains("斜") || install.Contains("斜");

            if (fly)
            {
                return new CalibrationTask
                {
                    Kind = CalibrationTaskKind.FlyCorrection,
                    SlotKey = slotKey,
                    NozzleKey = "1",
                    DisplayName = $"飞拍纠偏（{slotKey}）",
                    Suggestion = "固定相机对运动工件成像，出相对偏差：像素当量 + 触发/相位补偿",
                    SuggestType = CalibrationType.PixelScale,
                    SuggestEyeMode = EyeMode.EyeToHand,
                    BoundStationCode = null,
                    Note = slant ? "斜拍 + 飞拍：先解决畸变与打光，纠偏精度受运动抖动限制" : null
                };
            }

            if (moving)
            {
                return new CalibrationTask
                {
                    Kind = CalibrationTaskKind.CameraHandEyeWalk,
                    SlotKey = slotKey,
                    NozzleKey = "1",
                    DisplayName = $"相机手眼 H · 走位式（{slotKey}）",
                    Suggestion = "眼在手上随动拍固定特征，3×3 网格走位拟合像素↔机械平面映射（整机共享 1 条）",
                    SuggestType = CalibrationType.NinePointHandEye,
                    SuggestEyeMode = EyeMode.EyeInHand,
                    BoundStationCode = null,
                    Note = slant ? "斜拍安装：建议九点网格覆盖常用工作区并评估畸变影响" : null
                };
            }

            // 固定相机 / 送拍引导定位 → 吸放式
            return new CalibrationTask
            {
                Kind = CalibrationTaskKind.CameraHandEyePickPlace,
                SlotKey = slotKey,
                NozzleKey = "1",
                DisplayName = $"相机 H · 吸放式（{slotKey}）",
                Suggestion = "固定/送拍相机：吸工件放 9 网格命令位 → 回拍，命令坐标↔像素拟合",
                SuggestType = CalibrationType.PickPlaceHandEye,
                SuggestEyeMode = EyeMode.EyeToHand,
                BoundStationCode = null,
                Note = "现场确认：若相机下为固定作业区且机构不进视野，可改 EyeToHand 走位式九点"
            };
        }

        /// <summary>无相机槽的旧单相机档案（CameraMount/ShootMode 字段）</summary>
        private static CalibrationTask BuildFromSingleCamera(StationProfileRequirement req)
        {
            string mount = req.CameraMount ?? string.Empty;
            string shoot = req.ShootMode ?? string.Empty;
            bool fly = string.Equals(shoot, "飞拍", StringComparison.Ordinal);
            bool moving = mount.Contains("眼在手上");

            if (fly)
            {
                return new CalibrationTask
                {
                    Kind = CalibrationTaskKind.FlyCorrection,
                    SlotKey = "Cam_01",
                    NozzleKey = "1",
                    DisplayName = "飞拍纠偏（Cam_01）",
                    Suggestion = "飞拍：运动中成像出相对偏差（像素当量+触发/相位补偿）",
                    SuggestType = CalibrationType.PixelScale,
                    SuggestEyeMode = EyeMode.EyeToHand,
                    BoundStationCode = null,
                    Note = null
                };
            }
            if (moving)
            {
                return new CalibrationTask
                {
                    Kind = CalibrationTaskKind.CameraHandEyeWalk,
                    SlotKey = "Cam_01",
                    NozzleKey = "1",
                    DisplayName = "相机手眼 H · 走位式（Cam_01）",
                    Suggestion = "眼在手上随动拍固定特征，3×3 网格走位拟合像素↔机械平面映射",
                    SuggestType = CalibrationType.NinePointHandEye,
                    SuggestEyeMode = EyeMode.EyeInHand,
                    BoundStationCode = null,
                    Note = null
                };
            }
            return new CalibrationTask
            {
                Kind = CalibrationTaskKind.CameraHandEyePickPlace,
                SlotKey = "Cam_01",
                NozzleKey = "1",
                DisplayName = "相机 H · 吸放式（Cam_01）",
                Suggestion = "固定相机引导：吸放式走位（吸工件放网格命令位 → 回拍）拟合像素↔机械平面",
                SuggestType = CalibrationType.PickPlaceHandEye,
                SuggestEyeMode = EyeMode.EyeToHand,
                BoundStationCode = null,
                Note = "现场确认：若相机下为固定作业区且机构不进视野，可改 EyeToHand 走位式九点"
            };
        }

        // ==================== 工具级任务推导 ====================

        /// <summary>
        /// 工具偏心/多吸嘴旋转任务：带角度对位 且 (偏心 或 工具头≥2) → 每吸嘴一条。
        /// SuggestType 跟随主相机通道：随动=HandEyeWithRotation（走位+旋转段），
        /// 固定/送拍=PickPlaceHandEye（吸放+旋转段）——旋转采样通道与相机装法匹配。
        /// </summary>
        private static List<CalibrationTask> BuildToolTasks(StationProfileRequirement req, List<CalibrationTask> cameraTasks)
        {
            var result = new List<CalibrationTask>();

            // 工具旋转任务归属的主视觉槽：取首个相机 H 任务的槽键 —— 旋转段与九点采样用同一相机
            string primaryCameraSlot = null;
            foreach (var ct in cameraTasks)
            {
                if (ct.Kind == CalibrationTaskKind.CameraHandEyeWalk
                    || ct.Kind == CalibrationTaskKind.CameraHandEyePickPlace)
                {
                    primaryCameraSlot = ct.SlotKey;
                    break;
                }
            }

            string angleNeed = req.AngleNeed ?? string.Empty;
            bool needAngle = angleNeed.Length > 0 && !angleNeed.Contains("无");
            if (!needAngle) return result;

            bool eccentric = req.ConcentricWithRotationAxis == false;
            int toolCount = ParseToolHeadCount(req.ToolHeadCount);
            if (!eccentric && toolCount < 2) return result; // 单吸嘴同心：U 旋转不引入偏心落点误差

            // 主相机通道：存在走位式相机槽 → 走位旋转；否则按吸放式
            bool anyMovingCamera = cameraTasks.Any(t =>
                t.Kind == CalibrationTaskKind.CameraHandEyeWalk
                || (t.Kind == CalibrationTaskKind.CameraHandEyePickPlace && t.SuggestEyeMode == EyeMode.EyeInHand));

            CalibrationType rotType = anyMovingCamera
                ? CalibrationType.HandEyeWithRotation
                : CalibrationType.PickPlaceHandEye;

            for (int i = 1; i <= toolCount; i++)
            {
                string nk = i.ToString();
                result.Add(new CalibrationTask
                {
                    Kind = CalibrationTaskKind.ToolRotationCalib,
                    SlotKey = primaryCameraSlot,
                    NozzleKey = nk,
                    DisplayName = $"旋转中心 / 偏心 e（吸嘴{nk}）",
                    Suggestion = "转 U 多角度采样圆拟合：求回转中心 + 吸嘴偏心 e + 基准角 U0（角度补偿用）",
                    SuggestType = rotType,
                    SuggestEyeMode = anyMovingCamera ? EyeMode.EyeInHand : EyeMode.EyeToHand,
                    BoundStationCode = null,
                    Note = eccentric
                        ? "档案标工具与旋转轴偏心：U 旋转对位必须补偿偏心 e"
                        : "多吸嘴工位：各吸嘴相对 U 回转中心偏心独立，逐吸嘴标定"
                });
            }
            return result;
        }

        private static int ParseToolHeadCount(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return 1;
            if (int.TryParse(v.Trim(), out int n))
            {
                return n >= 1 ? n : 1;
            }
            return v.Contains("多") ? 2 : 1;
        }

        // ==================== 显示辅助 ====================

        /// <summary>任务族徽标（短词，任务卡左侧 chip）</summary>
        public static string KindBadge(CalibrationTaskKind kind)
        {
            switch (kind)
            {
                case CalibrationTaskKind.CameraHandEyeWalk: return "相机H·走位";
                case CalibrationTaskKind.CameraHandEyePickPlace: return "相机H·吸放";
                case CalibrationTaskKind.ToolRotationCalib: return "工具偏心e";
                case CalibrationTaskKind.ToolOffsetCalib: return "对针P4";
                case CalibrationTaskKind.FlyCorrection: return "飞拍纠偏";
                case CalibrationTaskKind.LensDistortion: return "畸变预留";
                default: return kind.ToString();
            }
        }

        /// <summary>任务描述首句（卡副标题摘要，来自 Suggestion 冒号前段）</summary>
        public static string KindShortDesc(CalibrationTask t)
        {
            if (t == null) return string.Empty;
            string s = t.Suggestion ?? string.Empty;
            int idx = s.IndexOf("：", StringComparison.Ordinal);
            return idx > 0 ? s.Substring(0, idx) : s;
        }
    }
}
