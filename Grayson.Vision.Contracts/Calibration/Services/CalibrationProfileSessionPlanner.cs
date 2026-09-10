//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationProfileSessionPlanner.cs
// 说 明: 旧 CalibrationProfile → 可执行 v2 会话规划器（2026-09-05 重设计 P2 落地）。
//        背景：P1b 入口（TryBuildSessionSpec）只认"单物理量"profile（九点=H、像素当量=s）；
//        旋转混合 profile（HandEyeWithRotation / PickPlaceHandEye 一窗含 H+e 两产物）只能回退
//        旧兼容四步。P2 目标：旋转混合也能拆"段"独立重标——H 段（矩阵）与 e 段（旋转偏心）
//        各走一条 StepDef v2 会话，消除"一条 profile 又走位又旋转"的混合执行。
//        本类 = 纯派生视图（不触持久化），供：
//          · 向导入口（段选择：返回 >1 条 → UI 先问"本次标哪段"）；
//          · P3 标定中心任务卡（artifact 拆分后由单量任务卡直达）；
//        与 CalibrationLegacyMapper.ToArtifacts 的分条口径保持一致（同源同果），
//        但产出 TaskSpec（含布局/采集路径/依赖/证据链），而非落库 Artifact。
//===================================================================================
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    using Grayson.Vision.Contracts.Calibration.Models;

    /// <summary>
    /// 旧 profile → v2 可执行会话规划器（静态纯函数）。
    /// 规则对齐引擎 v2（CalibrationPlanEngineV2）与迁移器（CalibrationLegacyMapper）：
    ///   NinePointHandEye            → H×1（EyeInHand 走位 NozzleTruthWalk / EyeToHand 走位 CameraTruthWalk）
    ///   HandEyeWithRotation         → H×1 + e×1（旋转段；EyeInHand 链 RotatePickPlace / 否则 RotateCameraView）
    ///   PickPlaceHandEye            → H×1(吸放 PickPlaceReturn) + e×1(RotatePickPlace 吸放旋转)
    ///   PixelScale                  → s×1(ScaleWalk)
    ///   Checkerboard2D/LensDistortion → 空（向导不可执行；类型入口已拦截）
    ///   t（TCP 对针 = 工具中心偏置 TCO）：旧 profile 已带对针结果(IsToolOffsetCalibrated/ToolOffset 非零)
    ///     → 可独立重标 t×1（2026-09-08 放开 EyeInHand——间接对针：示教基准位 R_n → 抬Z拍同点 →
    ///     TCO = H(u) − R_n；EyeToHand 保持图像对针）。采集方式由 spec.Layout 在向导装配期分发。
    /// </summary>
    public static class CalibrationProfileSessionPlanner
    {
        /// <summary>
        /// profile → 0..N 条可执行会话 spec（按执行顺序）。
        /// 返回 >1 条（H+e / H+e+t）时，调用方应先让用户选择"本次标哪一段"再启动对应会话。
        /// </summary>
        public static List<CalibrationTaskSpec> PlanSessions(CalibrationProfile p)
        {
            var result = new List<CalibrationTaskSpec>();
            if (p == null) return result;

            string station = Norm.Trim(p.BoundStationCode);
            string nozzle = Norm.Trim(p.NozzleKey);
            if (string.IsNullOrWhiteSpace(nozzle)) nozzle = "1";
            string slot = GuessSlotKey(p);
            string hRef = CalibrationArtifact.BuildArtifactId(station, CalibrationQuantity.HandEye, slot, null);

            switch (p.Type)
            {
                case CalibrationType.NinePointHandEye:
                    result.Add(BuildH(station, slot, p, pickPlace: false));
                    break;

                case CalibrationType.HandEyeWithRotation:
                    // 走位式九点 H + 旋转段 e（旧一窗两段 → 拆两条独立会话）
                    result.Add(BuildH(station, slot, p, pickPlace: false));
                    result.Add(BuildE(station, slot, nozzle, p, hRef));
                    break;

                case CalibrationType.PickPlaceHandEye:
                    // 吸放式 H（真值=命令位，已吸收工具偏距）+ 吸放旋转 e（可选加强，类型语义自带）
                    result.Add(BuildH(station, slot, p, pickPlace: true));
                    result.Add(BuildE(station, slot, nozzle, p, hRef));
                    break;

                case CalibrationType.PixelScale:
                    result.Add(new CalibrationTaskSpec
                    {
                        SpecId = CalibrationArtifact.BuildArtifactId(station, CalibrationQuantity.PixelScale, slot, "1"),
                        StationCode = station,
                        Quantity = CalibrationQuantity.PixelScale,
                        SlotKey = slot,
                        NozzleKey = "1",
                        Layout = p.EyeMode,
                        PrimaryPath = CalibrationAcquirePath.ScaleWalk,
                        IsRequired = true,
                        DisplayName = "像素当量 s 标定（相机 " + slot + "）",
                        Reason = "旧 PixelScale 方案重标（像素当量 mm/px）"
                    });
                    break;
                default:
                    // 棋盘/畸变等占位：无可执行会话（类型入口已拦截，双保险）
                    break;
            }

            // —— 工具中心偏置 t（TCP 对针重标：EyeInHand 间接 / EyeToHand 图像；仅已有对针结果的旧档案重标用）——
            bool hasT = p.IsToolOffsetCalibrated
                        || Math.Abs(p.ToolOffsetWx) > 1e-9
                        || Math.Abs(p.ToolOffsetWy) > 1e-9;
            if (hasT && result.Count > 0)
            {
                bool eyeInHand = p.EyeMode == EyeMode.EyeInHand;
                var t = new CalibrationTaskSpec
                {
                    SpecId = CalibrationArtifact.BuildArtifactId(station, CalibrationQuantity.ToolOffset, slot, nozzle),
                    StationCode = station,
                    Quantity = CalibrationQuantity.ToolOffset,
                    SlotKey = slot,
                    NozzleKey = nozzle,
                    Layout = p.EyeMode,
                    PrimaryPath = CalibrationAcquirePath.AlignTool,
                    DependentArtifactRef = hRef,
                    IsRequired = false,
                    DisplayName = eyeInHand
                        ? "工具中心偏置 TCO（工具头 " + nozzle + " · EyeInHand 间接对针）"
                        : "工具中心偏置 TCO（工具头 " + nozzle + " · EyeToHand 图像对针）",
                    Reason = eyeInHand
                        ? "旧 profile 已带对针结果，可独立重标；EyeInHand 间接对针：工具尖压特征记基准位 R_n → 抬 Z 拍同点 → TCO = H(u) − R_n（相机随动看不到工具尖，走间接接力）"
                        : "旧 profile 已带对针结果，可独立重标；EyeToHand 图像对针（固定相机观测工具尖落点）"
                };
                result.Add(t);
            }

            return result;
        }

        /// <summary>旧 profile 是否带旋转段结果（与迁移器同判据）</summary>
        public static bool HasRotationResult(CalibrationProfile p)
        {
            if (p == null) return false;
            return p.HasToolOffset
                   || Math.Abs(p.ToolCenterWx) > 1e-9
                   || Math.Abs(p.ToolCenterWy) > 1e-9
                   || (p.CalibU0.HasValue && Math.Abs(p.CalibU0.Value) > 1e-9);
        }

        /// <summary>槽键猜测：旧 Profile 无槽概念（与迁移器同实现）</summary>
        public static string GuessSlotKey(CalibrationProfile p)
        {
            if (p == null) return "Cam_01";
            string cam = Norm.Trim(p.CameraId);
            if (cam.StartsWith("Cam_", StringComparison.OrdinalIgnoreCase)) return cam;
            return "Cam_01";
        }

        // ==================== 分段构建 ====================

        private static CalibrationTaskSpec BuildH(string station, string slot, CalibrationProfile p, bool pickPlace)
        {
            if (pickPlace)
            {
                return new CalibrationTaskSpec
                {
                    SpecId = CalibrationArtifact.BuildArtifactId(station, CalibrationQuantity.HandEye, slot, "1"),
                    StationCode = station,
                    Quantity = CalibrationQuantity.HandEye,
                    SlotKey = slot,
                    NozzleKey = "1",
                    Layout = p.EyeMode,
                    PrimaryPath = CalibrationAcquirePath.PickPlaceReturn,
                    IsRequired = true,
                    DisplayName = "相机 H 吸放式（相机 " + slot + "）",
                    Reason = "PickPlace 档案的 H 段（吸工件 → 放命令位 → 回拍照位；真值=命令位，已吸收工具偏距）"
                };
            }

            bool eyeInHand = p.EyeMode == EyeMode.EyeInHand;
            return new CalibrationTaskSpec
            {
                SpecId = CalibrationArtifact.BuildArtifactId(station, CalibrationQuantity.HandEye, slot, "1"),
                StationCode = station,
                Quantity = CalibrationQuantity.HandEye,
                SlotKey = slot,
                NozzleKey = "1",
                Layout = p.EyeMode,
                PrimaryPath = eyeInHand
                    ? CalibrationAcquirePath.NozzleTruthWalk
                    : CalibrationAcquirePath.CameraTruthWalk,
                IsRequired = true,
                DisplayName = eyeInHand
                    ? "相机 H 走位式（相机 " + slot + "）"
                    : "相机 H 相机中心走位（相机 " + slot + "）",
                Reason = eyeInHand
                    ? "EyeInHand 档案的 H 段（吸嘴对格点走位；真值=吸嘴尖落点，H 吸收工具偏距，无需 t）"
                    : "EyeToHand 档案的 H 段（平台走位、特征入视野中心；工具尖偏距需配合可选 t 对针卡）"
            };
        }

        private static CalibrationTaskSpec BuildE(string station, string slot, string nozzle,
            CalibrationProfile p, string hRef)
        {
            bool pickPlaceRotate = p.Type == CalibrationType.PickPlaceHandEye;
            // 2026-09-06 修正：走位档案（含 EyeInHand 随动）的 e 一律观测式（延伸杆/治具直接转 U 画圆），
            // 不再误派吸放放料回拍语义（该语义仅 PickPlaceHandEye 吸放档案适用）
            var e = new CalibrationTaskSpec
            {
                SpecId = CalibrationArtifact.BuildArtifactId(station, CalibrationQuantity.ToolRotation, slot, nozzle),
                StationCode = station,
                Quantity = CalibrationQuantity.ToolRotation,
                SlotKey = slot,
                NozzleKey = nozzle,
                Layout = p.EyeMode,
                PrimaryPath = pickPlaceRotate
                    ? CalibrationAcquirePath.RotatePickPlace
                    : CalibrationAcquirePath.RotateCameraView,
                DependentArtifactRef = hRef,
                IsRequired = true,
                DisplayName = "旋转中心 / 偏心 e（吸嘴 " + nozzle + "）",
                Reason = (pickPlaceRotate
                        ? "吸放式档案的旋转段（吸件转 U → 放料 → 回拍测位移 → 圆拟合偏心）"
                        : (p.EyeMode == EyeMode.EyeInHand
                            ? "走位式档案的旋转段（吸嘴吸附延伸杆/治具，随 U 转相机观测轨迹画圆）"
                            : "走位式档案的旋转段（固定相机观测旋转轨迹画圆）"))
                        + "；依赖 H(" + slot + ") 提供机械坐标系"
            };
            return e;
        }
    }
}
