//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationLegacyMapper.cs
// 说 明: 旧标定体系 → v2 领域模型 迁移映射器（2026-09-05 重设计 P0；纯数据函数，不触持久化）。
//        旧 CalibrationProfile（泛型容器 + CalibrationType 混合语义）→ 0..N 条 CalibrationArtifact：
//          · NinePointHandEye / PickPlaceHandEye(无旋转段) → H
//          · HandEyeWithRotation → H + e（走位九点 + 旋转段拆两条）
//          · PickPlaceHandEye(探测到旋转段结果) → H + e
//          · PixelScale → s；Checkerboard2D / CameraLensDistortion → 畸变(预留)
//          · IsToolOffsetCalibrated（对针结果）→ t；时效按布局+采集方式（2026-09-08）：
//            EyeInHand 布局仅放行 EyeInHandIndirect（间接对针）结果；LegacyUnknown（旧档案默认）/
//            EyeToHandImage（图像对针残留）→ State=Expired（相机随动观测不到工具尖，图像对针物理不成立，
//            旧结果不可信，须按间接对针重标并给原因）
//        状态推断（保守，配合发布硬门禁）：IsCalibrated=false → Draft；
//        已标定但无校验记录 → SampleComplete（发布前须补残差体检）；
//        有校验记录 → Verified（等待"发布"动作，勿自动 Published）。
//===================================================================================
using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Calibration.Models;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>旧体系迁移映射器（静态纯函数）</summary>
    public static class CalibrationLegacyMapper
    {
        /// <summary>
        /// 单条旧 Profile → 0..N 条 v2 Artifact。
        /// legacy 可为 null；返回空表则调用方自行告警。
        /// </summary>
        public static List<CalibrationArtifact> ToArtifacts(CalibrationProfile legacy)
        {
            var result = new List<CalibrationArtifact>();
            if (legacy == null) return result;

            string station = Norm.Trim(legacy.BoundStationCode);
            string nozzle = Norm.Trim(legacy.NozzleKey);
            if (string.IsNullOrWhiteSpace(nozzle)) nozzle = "1";
            string slot = GuessSlotKey(legacy);
            EyeMode layout = legacy.EyeMode;

            // —— t（TCP 对针结果 = 工具中心偏置 TCO；P4/2026-09-08）——
            bool hasT = legacy.IsToolOffsetCalibrated
                        || Math.Abs(legacy.ToolOffsetWx) > 1e-9
                        || Math.Abs(legacy.ToolOffsetWy) > 1e-9;
            if (hasT)
            {
                bool eyeInHand = layout == EyeMode.EyeInHand;
                // 时效判定（2026-09-08）：EyeInHand 布局下只有『间接对针』(EyeInHandIndirect) 结果有效；
                //   LegacyUnknown(旧档案反序列化) / EyeToHandImage(图像对针残留) → Expired——
                //   相机随动观测不到工具尖，图像对针物理不成立，旧结果须按间接对针重标。
                bool staleInHand = eyeInHand
                                   && legacy.ToolOffsetMethod != ToolOffsetMethod.EyeInHandIndirect;
                var t = NewArtifact(station, CalibrationQuantity.ToolOffset, slot, nozzle, legacy);
                t.ToolOffsetWx = legacy.ToolOffsetWx;
                t.ToolOffsetWy = legacy.ToolOffsetWy;
                t.DependentArtifactId = CalibrationArtifact.BuildArtifactId(
                    station, CalibrationQuantity.HandEye, slot, null);
                t.State = staleInHand ? CalibrationArtifactState.Expired : InheritState(legacy);
                if (staleInHand)
                {
                    t.LegacyTypeName = t.LegacyTypeName + " (EyeInHand 历史对针结果已过期：该结果为旧图像对针/未知来源残留——相机与工具同体观测不到工具尖，请用『间接对针』重标：工具压特征记基准位 R_n → 抬 Z 拍同点 → TCO = H(u) − R_n)";
                }
                result.Add(t);
            }

            // —— 主类型 → 相机级/工具旋转产物 ——
            switch (legacy.Type)
            {
                case CalibrationType.NinePointHandEye:
                    // EyeInHand 走位 H；EyeToHand 亦映射 H（路径差异留标定中心按档案重挂）
                    result.Add(BuildH(station, slot, legacy));
                    break;

                case CalibrationType.PickPlaceHandEye:
                    // 吸放式 H；探测旋转段结果 → 追加 e
                    result.Add(BuildH(station, slot, legacy));
                    if (HasRotationResult(legacy))
                    {
                        result.Add(BuildE(station, slot, nozzle, legacy));
                    }
                    break;

                case CalibrationType.HandEyeWithRotation:
                    result.Add(BuildH(station, slot, legacy));
                    result.Add(BuildE(station, slot, nozzle, legacy));
                    break;

                case CalibrationType.PixelScale:
                    result.Add(BuildS(station, slot, legacy));
                    break;

                case CalibrationType.Checkerboard2D:
                case CalibrationType.CameraLensDistortion:
                    var dist = NewArtifact(station, CalibrationQuantity.LensDistortion, slot, nozzle, legacy);
                    dist.HomMatFilePath = legacy.HomMatFilePath;
                    dist.State = CalibrationArtifactState.Draft; // 预留能力未接线，一律草稿
                    result.Add(dist);
                    break;

                default:
                    // 未知/未定义类型：仅当有矩阵文件时按 H 兜底，避免静默丢数据
                    if (!string.IsNullOrWhiteSpace(legacy.HomMatFilePath))
                    {
                        result.Add(BuildH(station, slot, legacy));
                    }
                    break;
            }

            return result;
        }

        // ==================== 分产构建 ====================

        private static CalibrationArtifact BuildH(string station, string slot, CalibrationProfile legacy)
        {
            var h = NewArtifact(station, CalibrationQuantity.HandEye, slot, "1", legacy);
            h.HomMatFilePath = legacy.HomMatFilePath;
            h.PrimaryPath = legacy.EyeMode == EyeMode.EyeInHand
                ? CalibrationAcquirePath.NozzleTruthWalk
                : CalibrationAcquirePath.PickPlaceReturn;
            h.State = InheritState(legacy);
            return h;
        }

        private static CalibrationArtifact BuildE(string station, string slot, string nozzle, CalibrationProfile legacy)
        {
            var e = NewArtifact(station, CalibrationQuantity.ToolRotation, slot, nozzle, legacy);
            // 2026-09-06 语义对齐（Planner/EngineV2 同口径）：e 卡 PrimaryPath 必须显式——
            //   真·吸放档案(PickPlaceHandEye) → RotatePickPlace(放落回拍)；
            //   走位档案(HandEyeWithRotation 含 EyeInHand) → RotateCameraView(延伸杆观测画圆)。
            //   此前依赖枚举默认值(=NozzleTruthWalk H 走位路径)，与旋转会话语义不符。
            e.PrimaryPath = legacy.Type == CalibrationType.PickPlaceHandEye
                ? CalibrationAcquirePath.RotatePickPlace
                : CalibrationAcquirePath.RotateCameraView;
            e.RotCenterX = legacy.ToolCenterWx;
            e.RotCenterY = legacy.ToolCenterWy;
            // 旧体系 ToolCenterPx/Py 为像素域；偏心矢量见旧向导输出字段（ToolCenterWx/Wy 为机械域中心）。
            // 旧 PickPlaceHandEye 的 e 结果若存于 ToolCenterWx/Wy 机械坐标则原样搬入，否则保持 0 待重标。
            e.EccentricEx = 0;
            e.EccentricEy = 0;
            e.U0AngleDeg = legacy.CalibU0 ?? 0;
            e.CalibU0 = legacy.CalibU0;
            e.DependentArtifactId = CalibrationArtifact.BuildArtifactId(
                station, CalibrationQuantity.HandEye, slot, null);
            e.State = InheritState(legacy);
            e.Note = "迁移自旧 HandEyeWithRotation/PickPlaceHandEye 旋转段；偏心矢量字段旧体系无独立落点，请以校验台残差复验或重新标定确认";
            return e;
        }

        private static CalibrationArtifact BuildS(string station, string slot, CalibrationProfile legacy)
        {
            var s = NewArtifact(station, CalibrationQuantity.PixelScale, slot, "1", legacy);
            s.HomMatFilePath = legacy.HomMatFilePath;
            s.PrimaryPath = CalibrationAcquirePath.FlyPixelScale;
            s.State = InheritState(legacy);
            return s;
        }

        private static CalibrationArtifact NewArtifact(string station, CalibrationQuantity q,
            string slot, string nozzle, CalibrationProfile legacy)
        {
            return new CalibrationArtifact
            {
                ArtifactId = CalibrationArtifact.BuildArtifactId(station, q, slot, nozzle),
                StationCode = station,
                BoundDeviceId = legacy.BoundDeviceId,
                Quantity = q,
                SlotKey = slot,
                NozzleKey = nozzle,
                Layout = legacy.EyeMode,
                LegacyProfileId = legacy.Id,
                LegacyTypeName = legacy.Type.ToString(),
                FeatureType = legacy.FeatureType,
                FeatureTemplateName = legacy.FeatureTemplateName,
                TemplateMinScore = legacy.TemplateMinScore,
                BindXAxisIndex = legacy.BindXAxisIndex,
                BindYAxisIndex = legacy.BindYAxisIndex,
                BindRotationAxisIndex = legacy.BindRotationAxisIndex,
                InvertXAxis = legacy.InvertXAxis,
                InvertYAxis = legacy.InvertYAxis,
                CalibZ = legacy.CalibZ,
                CalibU0 = legacy.CalibU0,
                Samples = legacy.Samples ?? new List<CalibrationSampleModel>(),
                Verifications = legacy.VerificationRecords ?? new List<CalibrationVerificationRecord>(),
                MatrixFingerprint = legacy.HomMatFilePath // P0 以路径作指纹占位；后续改内容哈希
            };
        }

        // ==================== 判定与状态 ====================

        /// <summary>旧 Profile 是否带旋转段结果（PickPlaceHandEye 向导旋转段可选：跑了才有输出）</summary>
        private static bool HasRotationResult(CalibrationProfile legacy)
        {
            return legacy.HasToolOffset
                   || Math.Abs(legacy.ToolCenterWx) > 1e-9
                   || Math.Abs(legacy.ToolCenterWy) > 1e-9
                   || (legacy.CalibU0.HasValue && Math.Abs(legacy.CalibU0.Value) > 1e-9);
        }

        /// <summary>状态推断（保守 + 发布硬门禁配合）：见文件头注释</summary>
        private static CalibrationArtifactState InheritState(CalibrationProfile legacy)
        {
            if (legacy == null || !legacy.IsCalibrated) return CalibrationArtifactState.Draft;
            bool hasVerification = legacy.VerificationRecords != null && legacy.VerificationRecords.Count > 0;
            return hasVerification ? CalibrationArtifactState.Verified : CalibrationArtifactState.SampleComplete;
        }

        /// <summary>
        /// 槽键猜测：旧 Profile 无槽概念。CameraId 形如 "Cam_*" 视为槽键，
        /// 否则（设备 ID 如 Basler_Camera_xxx）用 "Cam_01" 占位（标定中心按档案槽表重挂）。
        /// </summary>
        private static string GuessSlotKey(CalibrationProfile legacy)
        {
            string cam = Norm.Trim(legacy.CameraId);
            if (cam.StartsWith("Cam_", StringComparison.OrdinalIgnoreCase)) return cam;
            return "Cam_01";
        }
    }
}
