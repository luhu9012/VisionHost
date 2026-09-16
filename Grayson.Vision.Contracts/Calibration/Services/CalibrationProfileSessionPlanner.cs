//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationProfileSessionPlanner.cs
// 说 明: CalibrationProfile → 可执行 v2 会话规划器（单产物语义）。
//        profile 唯一承载 v2 物理量（Quantity）与采集路径（PrimaryPath），
//        直接派生单条可执行会话 spec（含下相机专属 DownCameraWalk / DownCameraPixelRotCenter）。
//        本类 = 纯派生视图（不触持久化），供：
//          · 向导入口（v2 单量会话直达）；
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
    /// CalibrationProfile → v2 可执行会话规划器（静态纯函数）。
    /// 规则对齐引擎 v2（CalibrationPlanEngineV2）与迁移器（CalibrationLegacyMapper）：
    ///   HandEye                      → H×1（EyeInHand 走位 NozzleTruthWalk / EyeToHand 走位 CameraTruthWalk /
    ///                                  下相机 DownCameraWalk）
    ///   ToolRotation                 → e×1（走位 RotateCameraView / 吸放 RotatePickPlace / 下相机 DownCameraPixelRotCenter）
    ///   PixelScale                   → s×1(ScaleWalk / FlyPixelScale)
    ///   ToolOffset / LensDistortion  → 不在此派生（t 独立对针窗；畸变预留）
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

            // ★2026-09-12 单产物语义：profile 唯一承载 v2 物理量（Quantity）与采集路径（PrimaryPath），
            //   含下相机专属 DownCameraWalk / DownCameraPixelRotCenter，直接派生单条会话 spec，
            //   旧 CalibrationType 混合语义已删除。
            var single = BuildFromQuantity(p, station, slot, nozzle, hRef);
            if (single != null) result.Add(single);
            return result; // 单产物 profile：不再叠加 t（t 独立成 profile）
        }

        /// <summary>
        /// 旧 profile 是否带旋转段结果（用于旧档迁移判断）。
        ///
        /// ★★2026-09-15 修正：**删掉 `p.HasToolOffset`**（同 CalibrationCardDeriver 两处判据的理由）——
        ///   它是向导在"会话规格含旋转段"时的自动**声明**（一进会话就置 true），不是结果；
        ///   拿它当证据会把"声明了没做成"判成"有旋转结果"，让迁移/补标流程少跑一段（假绿）。
        /// ⚠ 当前全仓**无调用方**（保留公开 API 以兼容外部/后续迁移；确认不需要可删）。
        ///   注意：本判据与 CalibrationCardDeriver.HasRotationEvidence{OnProfile}、
        ///   CalibrationManagerViewModel.HasRotationGeometry 是**四份意图不同的**判据
        ///   （旧档迁移 / 草稿卡入口 / e 卡数据证据 / 发布门），改动其一时不要顺手改其余（曾因此分叉）。
        /// </summary>
        public static bool HasRotationResult(CalibrationProfile p)
        {
            if (p == null) return false;
            return Math.Abs(p.ToolCenterWx) > 1e-9
                   || Math.Abs(p.ToolCenterWy) > 1e-9
                   || (p.CalibU0.HasValue && Math.Abs(p.CalibU0.Value) > 1e-9);
        }

        /// <summary>
        /// 槽键解析（2026-09-11 修正）：优先取显式槽字段 <see cref="CalibrationProfile.CameraSlotKey"/>；
        /// 未指定才回退旧猜测（CameraId 以 "Cam_" 开头）。
        /// ⚠ 旧实现只看 CameraId——而向导绑物理相机后 CameraId 会变成设备名，恒回退 "Cam_01"，
        ///   复合工位上下相机两条标定因此撞同一个槽（产物 ArtifactId 相同、任务卡互相顶掉）。
        /// </summary>
        public static string GuessSlotKey(CalibrationProfile p)
        {
            if (p == null) return "Cam_01";
            string slot = Norm.Trim(p.CameraSlotKey);
            if (!string.IsNullOrWhiteSpace(slot)) return slot;
            string cam = Norm.Trim(p.CameraId);
            if (cam.StartsWith("Cam_", StringComparison.OrdinalIgnoreCase)) return cam;
            return "Cam_01";
        }

        // ==================== ★★2026-09-15 实例键（相机槽）闸门 —— 全仓唯一判据 ====================
        //
        // 背景：复合工位（一台设备挂上相机 Cam_A + 下相机 Cam_C）产生的多份档案
        //   **BoundStationCode 完全相同**。而"同工位继承 / 借九点矩阵 / 借 e / 借 O / 发布几何"
        //   这一族谓词历史上有 8 处只看工位 ⇒ 上下相机张冠李戴（借到别人的 H 矩阵、
        //   杆端偏心 b≈106mm、旋转中心 O）。
        //
        // ⚠ 为什么必须收到这里：同一判据曾在**向导**（IsSameCameraSlot）、**管理页**
        //   （IsSameCameraSlotStrict）、**消费门面**（DeclaredOtherSlot）**各写一遍**
        //   —— 这正是"判据写两遍 = 靠巧合正确"的形态：三份副本只要有**一份**被改动或漏用，
        //   分叉就发生在边界上（"槽没写"的旧档案），而现有数据都远离边界 ⇒ 门禁不会红。
        //
        // ★ 边界语义（最容易做错的一条）：必须区分两种"不同"——
        //     · 对方【没写】槽键   → 这是**缺信息**，允许借用/继承（并由消费端自述来源）；
        //     · 对方**【写了别的槽】** → 这是**另一台相机**的产物，绝不许借用（张冠李戴）。
        //   把两者混成一个"不相等就不借"会误伤旧档案；混成"相等才借"则漏掉边界。

        /// <summary>显式声明的槽键（未声明返回空串；不做任何猜测——猜测用 <see cref="GuessSlotKey"/>）</summary>
        public static string DeclaredSlot(CalibrationProfile p) => Norm.Trim(p?.CameraSlotKey);

        /// <summary>
        /// 两个档案是否**明确属于同一个相机槽**（两侧都必须显式声明且相等）。
        /// 用途：继承/共享的**强闸门**（宁可不继承，也不张冠李戴）。
        /// 任一未声明槽 ⇒ false（"不知道是不是同一台" ≠ "是同一台"）。
        /// </summary>
        public static bool IsSameSlot(CalibrationProfile a, CalibrationProfile b)
        {
            string sa = DeclaredSlot(a);
            string sb = DeclaredSlot(b);
            return sa.Length > 0 && sb.Length > 0
                   && string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 该档案是否**显式声明了别的**相机槽（借入闸门用）。
        /// 语义：true = 明说是另一台相机 ⇒ 绝不允许借入；false = 同槽 或 槽未声明（旧档案）⇒ 允许借入。
        /// </summary>
        public static bool DeclaresOtherSlot(CalibrationProfile p, string targetSlotKey)
        {
            string s = DeclaredSlot(p);
            string t = Norm.Trim(targetSlotKey);
            return s.Length > 0 && t.Length > 0
                   && !string.Equals(s, t, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>槽显示文案（日志用）：显式槽 / "(槽未指定)"</summary>
        public static string SlotText(CalibrationProfile p)
        {
            string s = DeclaredSlot(p);
            return s.Length > 0 ? s : "(槽未指定)";
        }

        /// <summary>
        /// ★2026-09-12 按 profile 显式声明的 v2 物理量 + 采集路径派生单条会话 spec。
        /// 覆盖下相机专属路径（DownCameraWalk / DownCameraPixelRotCenter）。
        /// </summary>
        private static CalibrationTaskSpec BuildFromQuantity(CalibrationProfile p,
            string station, string slot, string nozzle, string hRef)
        {
            var q = p.Quantity;
            var path = p.PrimaryPath ?? DefaultPathFor(q, p.EyeMode);

            switch (q)
            {
                case CalibrationQuantity.HandEye:
                    return new CalibrationTaskSpec
                    {
                        SpecId = CalibrationArtifact.BuildArtifactId(station, q, slot, "1"),
                        StationCode = station,
                        Quantity = q,
                        SlotKey = slot,
                        NozzleKey = "1",
                        Layout = p.EyeMode,
                        PrimaryPath = path,
                        IsRequired = true,
                        DisplayName = path == CalibrationAcquirePath.DownCameraWalk
                            ? "下相机 H 吸件走位（" + slot + "）"
                            : "相机 H 走位式（" + slot + "）",
                        Reason = path == CalibrationAcquirePath.DownCameraWalk
                            ? "下固定仰视：吸件走位九点，真值=吸附工件落点，消费端只取相对偏差"
                            : "按 profile 声明的 v2 物理量 HandEye 派生"
                    };
                case CalibrationQuantity.ToolRotation:
                    return new CalibrationTaskSpec
                    {
                        SpecId = CalibrationArtifact.BuildArtifactId(station, q, slot, nozzle),
                        StationCode = station,
                        Quantity = q,
                        SlotKey = slot,
                        NozzleKey = nozzle,
                        Layout = p.EyeMode,
                        PrimaryPath = path,
                        DependentArtifactRef = hRef,
                        IsRequired = true,
                        DisplayName = path == CalibrationAcquirePath.DownCameraPixelRotCenter
                            ? "下相机像素旋转中心（" + slot + "）"
                            : "旋转中心 / 偏心 e（吸嘴 " + nozzle + "）",
                        Reason = path == CalibrationAcquirePath.DownCameraPixelRotCenter
                            ? "下相机像素域拟合圆求吸嘴旋转中心像素位置（区别于机械域 e）"
                            : "按 profile 声明的 v2 物理量 ToolRotation 派生"
                    };
                case CalibrationQuantity.PixelScale:
                    return new CalibrationTaskSpec
                    {
                        SpecId = CalibrationArtifact.BuildArtifactId(station, q, slot, "1"),
                        StationCode = station,
                        Quantity = q,
                        SlotKey = slot,
                        NozzleKey = "1",
                        Layout = p.EyeMode,
                        PrimaryPath = path,
                        IsRequired = true,
                        DisplayName = "像素当量 s 标定（" + slot + "）",
                        Reason = "按 profile 声明的 v2 物理量 PixelScale 派生"
                    };
                default:
                    return null; // ToolOffset / LensDistortion：不在此派生（t 独立窗，畸变预留）
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
                    return CalibrationAcquirePath.ScaleWalk;
                default:
                    return CalibrationAcquirePath.CameraTruthWalk;
            }
        }
    }
}
