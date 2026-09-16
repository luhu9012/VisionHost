//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationLegacyMapper.cs
// 说 明: 旧标定体系 → v2 领域模型 迁移映射器（纯数据函数，不触持久化）。
//        旧 CalibrationProfile（单产物容器 + CalibrationQuantity 语义）→ 0..2 条 CalibrationArtifact：
//          · 主物理量（Quantity）→ 单产物：HandEye→H、ToolRotation→e、PixelScale→s、LensDistortion→畸变(预留)
//          · IsToolOffsetCalibrated（对针结果）→ t；时效按布局+采集方式：
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

            // —— 主物理量 → 相机级/工具旋转产物 ——
            // ★2026-09-12 单产物语义：profile 唯一承载 Quantity（v2 物理量），按单产物直接映射，
            //   覆盖下相机专属路径（DownCameraWalk / DownCameraPixelRotCenter），旧 CalibrationType 已删除。
            var art = BuildFromQuantity(station, slot, nozzle, legacy);
            if (art != null) result.Add(art);
            return result;
        }

        // ==================== 分产构建 ====================

        /// <summary>★2026-09-12 按 profile 声明的 v2 物理量构建单产物 artifact（含下相机专属路径）</summary>
        private static CalibrationArtifact BuildFromQuantity(string station, string slot, string nozzle,
            CalibrationProfile legacy)
        {
            var q = legacy.Quantity;
            var path = legacy.PrimaryPath ?? DefaultPathFor(q, legacy.EyeMode);
            switch (q)
            {
                case CalibrationQuantity.HandEye:
                    var h = NewArtifact(station, q, slot, "1", legacy);
                    h.HomMatFilePath = legacy.HomMatFilePath;
                    h.PrimaryPath = path;
                    h.State = InheritState(legacy);
                    return h;
                case CalibrationQuantity.ToolRotation:
                    var e = NewArtifact(station, q, slot, nozzle, legacy);
                    e.PrimaryPath = path;
                    e.RotCenterX = legacy.ToolCenterWx;
                    e.RotCenterY = legacy.ToolCenterWy;
                    e.EccentricEx = 0;
                    e.EccentricEy = 0;
                    e.U0AngleDeg = legacy.CalibU0 ?? 0;
                    e.CalibU0 = legacy.CalibU0;
                    e.DependentArtifactId = CalibrationArtifact.BuildArtifactId(
                        station, CalibrationQuantity.HandEye, slot, null);
                    e.State = InheritState(legacy);
                    return e;
                case CalibrationQuantity.PixelScale:
                    var s = NewArtifact(station, q, slot, "1", legacy);
                    s.HomMatFilePath = legacy.HomMatFilePath;
                    s.PrimaryPath = path;
                    s.State = InheritState(legacy);
                    return s;
                default:
                    return null; // ToolOffset / LensDistortion 不走此分支（t 独立 / 畸变预留）
            }
        }

        private static CalibrationAcquirePath DefaultPathFor(CalibrationQuantity q, EyeMode layout)
        {
            switch (q)
            {
                case CalibrationQuantity.HandEye:
                    return layout == EyeMode.EyeInHand
                        ? CalibrationAcquirePath.NozzleTruthWalk
                        : CalibrationAcquirePath.CameraTruthWalk;
                case CalibrationQuantity.ToolRotation:
                    return CalibrationAcquirePath.RotateCameraView;
                case CalibrationQuantity.PixelScale:
                    return CalibrationAcquirePath.FlyPixelScale;
                default:
                    return CalibrationAcquirePath.CameraTruthWalk;
            }
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
                LegacyTypeName = legacy.Quantity.ToString(),
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

        // ==================== 状态 ====================

        /// <summary>状态推断（保守 + 发布硬门禁配合）：见文件头注释</summary>
        private static CalibrationArtifactState InheritState(CalibrationProfile legacy)
        {
            if (legacy == null || !legacy.IsCalibrated) return CalibrationArtifactState.Draft;
            bool hasVerification = legacy.VerificationRecords != null && legacy.VerificationRecords.Count > 0;
            return hasVerification ? CalibrationArtifactState.Verified : CalibrationArtifactState.SampleComplete;
        }

        /// <summary>
        /// 槽键解析（2026-09-11 修正）：优先取显式槽字段 CameraSlotKey，
        /// 否则 CameraId 形如 "Cam_*" 视为槽键，否则用 "Cam_01" 占位。
        /// ⚠ 与 CalibrationProfileSessionPlanner.GuessSlotKey 同口径（复合工位上下相机分槽的前提）。
        /// </summary>
        private static string GuessSlotKey(CalibrationProfile legacy)
        {
            string slot = Norm.Trim(legacy.CameraSlotKey);
            if (!string.IsNullOrWhiteSpace(slot)) return slot;
            string cam = Norm.Trim(legacy.CameraId);
            if (cam.StartsWith("Cam_", StringComparison.OrdinalIgnoreCase)) return cam;
            return "Cam_01";
        }
    }
}
