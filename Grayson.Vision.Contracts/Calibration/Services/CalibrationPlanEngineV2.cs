//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationPlanEngineV2.cs
// 说 明: 标定计划推导引擎 v2（2026-09-05 重设计 P0；输入工位档案 → 输出 CalibrationTaskSpec 列表）。
//        与旧 CalibrationPlanEngine 的差异（破坏式收敛）：
//          · 判据结果直接产出"物理量 + 采集路径"（不再映射回旧 CalibrationType）；
//          · 判据输入归一化助手（Norm* 系列）替代散落的字符串.Contains 咒语，Reason 携带
//            档案证据链，供标定中心任务卡"为什么需要它"追溯展示；
//          · 工具偏距 t 只在 EyeToHand 布局派生且为【可选】任务（用户拍板：EyeInHand 相机
//            与吸嘴同体，观测不到吸嘴尖对准态，图像对针物理不成立）；
//          · 相机级任务（H/s）按槽各 1 条整机共享；工具级任务（e/t）按吸嘴各 1 条，
//            e 依赖首个相机 H（DependentArtifactRef 承载）。
//        兼容边界（P0）：旧引擎保留不动（UI 消费面未切换）；本引擎为纯静态领域服务，
//        可由 console runner 直接调用做数据级回归。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Station.Models;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>标定计划推导引擎 v2（静态无状态；档案 → 有序任务规格集）</summary>
    public static class CalibrationPlanEngineV2
    {
        // ==================== 主入口 ====================

        /// <summary>
        /// 由工位档案推导标定任务规格集。
        /// 排序约定（2026-09-08 定稿，依赖序即执行序）：相机级 H → 工具级旋转中心 e → 工具级对针 t（e_nozzle）。
        /// 判据由工位前置条件唯一裁决：相机安装(眼在手上/外)→H 路径；角度需求→是否派生 e；
        /// 工具与旋转轴同心性→是否派生 t（对针求吸嘴 TCP 偏心）。
        /// </summary>
        public static List<CalibrationTaskSpec> Derive(StationProfile profile)
        {
            var specs = new List<CalibrationTaskSpec>();
            if (profile == null) return specs;

            var req = profile.Requirement ?? new StationProfileRequirement();
            bool hasSlots = req.CameraSlots != null && req.CameraSlots.Count > 0;

            // —— 相机级（槽级；每槽 0..2 条：H 必做引导 / s 飞拍） ——
            var cameraSpecs = new List<CalibrationTaskSpec>();
            if (hasSlots)
            {
                foreach (var slot in req.CameraSlots)
                {
                    if (slot == null) continue;
                    var s = DeriveFromSlot(profile, slot);
                    if (s != null) cameraSpecs.Add(s);
                }
            }
            else
            {
                var s = DeriveFromSingleCamera(profile, req);
                if (s != null) cameraSpecs.Add(s);
            }
            specs.AddRange(cameraSpecs);

            // —— 工具级旋转中心 e（依赖 H；带角度且(偏心|多吸嘴)才派生） ——
            var toolSpecs = DeriveToolTasks(profile, req, cameraSpecs);
            specs.AddRange(toolSpecs);

            // —— 工具级对针 t = 吸嘴 TCP 偏心 e_nozzle（偏心工具必做；依赖 e(带角度时) 或 H） ——
            var offsetSpecs = DeriveToolOffsetTasks(profile, req, cameraSpecs);
            specs.AddRange(offsetSpecs);

            return specs;
        }

        /// <summary>主线必做规格（不含可选；任务卡进度统计用）</summary>
        public static List<CalibrationTaskSpec> RequiredSpecs(IEnumerable<CalibrationTaskSpec> specs)
        {
            var list = new List<CalibrationTaskSpec>();
            if (specs == null) return list;
            foreach (var s in specs)
            {
                if (s != null && s.IsRequired) list.Add(s);
            }
            return list;
        }

        // ==================== 相机级 ====================

        /// <summary>相机槽判据（单槽 → 0/1 条相机级任务）</summary>
        private static CalibrationTaskSpec DeriveFromSlot(StationProfile profile, VisionSlotInfo slot)
        {
            string purpose = Norm.Trim(slot.Purpose);
            string shoot = Norm.Trim(slot.ShootMode);
            string install = Norm.Trim(slot.InstallKind);
            string slotKey = Norm.Trim(slot.SlotKey);
            if (string.IsNullOrWhiteSpace(slotKey)) slotKey = "Cam_?";

            bool isGuidance = Norm.IsGuidancePurpose(purpose) || Norm.IsGuidancePurpose(install);
            bool isFly = Norm.IsFly(shoot) || Norm.IsFly(purpose);

            // —— 用途门：非引导/纠偏/飞拍类用途（测量/检测/OCR…）免几何标定 ——
            if (!isGuidance && !isFly) return null;

            bool moving = Norm.IsMovingMount(slot.MovesWithActuator, install);
            bool slant = Norm.IsSlant(slot.AxisToSurface) || Norm.IsSlant(install);
            string station = Norm.Trim(profile.StationCode);

            // 飞拍 → 像素当量 s（相对偏差；触发/相位补偿归配方层）
            if (isFly)
            {
                return new CalibrationTaskSpec
                {
                    SpecId = Key(station, "s", slotKey, null),
                    StationCode = station,
                    Quantity = CalibrationQuantity.PixelScale,
                    SlotKey = slotKey,
                    NozzleKey = "1",
                    Layout = EyeMode.EyeToHand,
                    PrimaryPath = CalibrationAcquirePath.FlyPixelScale,
                    DisplayName = "飞拍纠偏 s（" + slotKey + "）",
                    Suggestion = "固定相机对运动工件成像，出相对偏差：像素当量 + 触发/相位补偿（节奏在配方）",
                    Reason = ReasonJoin(
                        "Purpose=" + (purpose.Length > 0 ? purpose : "未答"),
                        "ShootMode=" + (shoot.Length > 0 ? shoot : "未答"),
                        "飞行成像 ⇒ 像素当量任务"),
                    Note = slant ? "斜拍 + 飞拍：先解决畸变与打光，纠偏精度受运动抖动限制" : null
                };
            }

            // 眼在手上 / 随动 → H 走位式（真值=吸嘴尖落点，吸收 t）
            if (moving)
            {
                return new CalibrationTaskSpec
                {
                    SpecId = Key(station, "H", slotKey, null),
                    StationCode = station,
                    Quantity = CalibrationQuantity.HandEye,
                    SlotKey = slotKey,
                    NozzleKey = "1",
                    Layout = EyeMode.EyeInHand,
                    PrimaryPath = CalibrationAcquirePath.NozzleTruthWalk,
                    DisplayName = "相机手眼 H · 走位式（" + slotKey + "）",
                    Suggestion = "眼在手上随动拍固定特征，3×3 网格走位拟合像素↔机械平面（整机共享 1 条；真值=吸嘴尖落点）",
                    Reason = ReasonJoin(
                        "Purpose=" + (purpose.Length > 0 ? purpose : "未答"),
                        "MovesWithActuator=" + (slot.MovesWithActuator == true ? "true(随动)" : "false"),
                        "随动引导 ⇒ EyeInHand 走位式 H（吸嘴对格点真值，H 已吸收工具偏距 t）"),
                    Note = slant ? "斜拍安装：建议九点网格覆盖常用工作区并评估畸变影响" : null
                };
            }

            // 固定相机引导（EyeToHand）→ 吸放式 H 首选（真值=放料命令位，吸收 t）；
            // 备选=相机中心走位式（该路径下工具尖偏距需 t 补——见可选 t 卡）
            var fixedSpec = new CalibrationTaskSpec
            {
                SpecId = Key(station, "H", slotKey, null),
                StationCode = station,
                Quantity = CalibrationQuantity.HandEye,
                SlotKey = slotKey,
                NozzleKey = "1",
                Layout = EyeMode.EyeToHand,
                PrimaryPath = CalibrationAcquirePath.PickPlaceReturn,
                DisplayName = "相机 H · 吸放式（" + slotKey + "）",
                Suggestion = "固定/送拍相机：吸工件放 9 网格命令位 → 回拍，命令坐标↔像素拟合（真值=命令位）",
                Reason = ReasonJoin(
                    "Purpose=" + (purpose.Length > 0 ? purpose : "未答"),
                    "MovesWithActuator=" + (slot.MovesWithActuator == true ? "true(随动)" : "false(固定)"),
                    "固定引导 ⇒ EyeToHand H（吸放式首推，吸收工具偏距 t）"),
                Note = "现场确认：若机构不进相机视野（无放料回拍条件），改备选路径 相机中心走位式，并启用可选 t 对针卡"
            };
            fixedSpec.AltPaths.Add(CalibrationAcquirePath.CameraTruthWalk);
            return fixedSpec;
        }

        /// <summary>无相机槽旧单相机档案（CameraMount/ShootMode 字段；语义与槽级一致）</summary>
        private static CalibrationTaskSpec DeriveFromSingleCamera(StationProfile profile, StationProfileRequirement req)
        {
            string mount = Norm.Trim(req.CameraMount);
            string shoot = Norm.Trim(req.ShootMode);
            string station = Norm.Trim(profile.StationCode);
            bool isFly = Norm.IsFly(shoot);
            bool moving = Norm.IsMovingMount(null, mount);

            if (isFly)
            {
                return new CalibrationTaskSpec
                {
                    SpecId = Key(station, "s", "Cam_01", null),
                    StationCode = station,
                    Quantity = CalibrationQuantity.PixelScale,
                    SlotKey = "Cam_01",
                    Layout = EyeMode.EyeToHand,
                    PrimaryPath = CalibrationAcquirePath.FlyPixelScale,
                    DisplayName = "飞拍纠偏 s（Cam_01）",
                    Suggestion = "飞拍：运动中成像出相对偏差（像素当量 + 触发/相位补偿）",
                    Reason = ReasonJoin("ShootMode=飞拍", "飞行成像 ⇒ 像素当量任务")
                };
            }

            if (moving)
            {
                return new CalibrationTaskSpec
                {
                    SpecId = Key(station, "H", "Cam_01", null),
                    StationCode = station,
                    Quantity = CalibrationQuantity.HandEye,
                    SlotKey = "Cam_01",
                    Layout = EyeMode.EyeInHand,
                    PrimaryPath = CalibrationAcquirePath.NozzleTruthWalk,
                    DisplayName = "相机手眼 H · 走位式（Cam_01）",
                    Suggestion = "眼在手上随动拍固定特征，3×3 网格走位拟合像素↔机械平面（真值=吸嘴尖落点）",
                    Reason = ReasonJoin("CameraMount=" + mount, "随动引导 ⇒ EyeInHand 走位式 H")
                };
            }

            var singleFixed = new CalibrationTaskSpec
            {
                SpecId = Key(station, "H", "Cam_01", null),
                StationCode = station,
                Quantity = CalibrationQuantity.HandEye,
                SlotKey = "Cam_01",
                Layout = EyeMode.EyeToHand,
                PrimaryPath = CalibrationAcquirePath.PickPlaceReturn,
                DisplayName = "相机 H · 吸放式（Cam_01）",
                Suggestion = "固定相机引导：吸放式走位（吸工件放网格命令位 → 回拍）拟合像素↔机械平面",
                Reason = ReasonJoin("CameraMount=" + mount, "固定引导 ⇒ EyeToHand H（吸放式首推）")
            };
            singleFixed.AltPaths.Add(CalibrationAcquirePath.CameraTruthWalk);
            return singleFixed;
        }

        // ==================== 工具级 e ====================

        /// <summary>
        /// 工具回转 e（回转中心标定）：角度作业 且 (工具中心偏心 或 工具头≥2) → 每工具头一条。
        /// 路径随主相机 H 的采集语义（2026-09-06 修正：不再按 eyeInHand 参杂吸放语义）：
        ///   主 H 吸放式（PickPlaceReturn，放料命令位真值，相机/机构回拍照位）→ RotatePickPlace
        ///     （吸件转回转轴 → 放料 → 回拍测位移；仅吸放档案适用）；
        ///   其余（走位式：EyeInHand 随动 NozzleTruthWalk / EyeToHand 相机中心 CameraTruthWalk）
        ///     → RotateCameraView（工具吸附延伸杆/治具特征，直接转回转轴由相机观测轨迹画圆，无放落）。
        /// </summary>
        private static List<CalibrationTaskSpec> DeriveToolTasks(StationProfile profile,
            StationProfileRequirement req, List<CalibrationTaskSpec> cameraSpecs)
        {
            var result = new List<CalibrationTaskSpec>();

            string angleNeed = Norm.Trim(req.AngleNeed);
            bool needAngle = angleNeed.Length > 0 && !Norm.HasNoAngle(angleNeed);
            if (!needAngle) return result;

            bool eccentric = req.ConcentricWithRotationAxis == false;
            int toolCount = Norm.ParseToolHeadCount(req.ToolHeadCount);
            if (!eccentric && toolCount < 2) return result; // 单工具头同心：回转不引入偏置落点误差

            // 主相机 H 槽（旋转采样与九点用同一相机；e 的坐标系依赖它）
            var primaryH = FindFirstHandEye(cameraSpecs);
            if (primaryH == null) return result; // 无相机 H → e 无坐标系基准（不可能）

            string station = Norm.Trim(profile.StationCode);
            bool pickPlaceChain = primaryH.PrimaryPath == CalibrationAcquirePath.PickPlaceReturn;

            for (int i = 1; i <= toolCount; i++)
            {
                string nk = i.ToString();
                result.Add(new CalibrationTaskSpec
                {
                    SpecId = Key(station, "e", primaryH.SlotKey, nk),
                    StationCode = station,
                    Quantity = CalibrationQuantity.ToolRotation,
                    SlotKey = primaryH.SlotKey,
                    NozzleKey = nk,
                    Layout = primaryH.Layout,
                    PrimaryPath = pickPlaceChain
                        ? CalibrationAcquirePath.RotatePickPlace
                        : CalibrationAcquirePath.RotateCameraView,
                    DependentArtifactRef = primaryH.ArtifactRef,
                    DisplayName = "回转中心标定 e（工具头 " + nk + "）",
                    Suggestion = "绕回转轴多角度采样圆拟合：求回转中心投影 + 工具中心偏置 e + 基准角 U0（角度补偿用）",
                    Reason = ReasonJoin(
                        "AngleNeed=" + (angleNeed.Length > 0 ? angleNeed : "未答"),
                        "ConcentricWithRotationAxis=" + (eccentric ? "false(偏心)" : "true(同心)"),
                        "ToolHeadCount=" + req.ToolHeadCount,
                        "角度作业 " + (eccentric ? "且工具中心偏心" : "且多工具头") + " ⇒ 每工具头 1 条 e；依赖相机 H(" + primaryH.SlotKey + ") 提供坐标系"),
                    Note = eccentric
                        ? "工具中心偏离回转轴线：按角度对位作业必须补偿工具中心偏置 e"
                        : "多工具头工位：各工具头相对回转中心的偏置独立，逐工具头标定"
                });
            }
            return result;
        }

        // ==================== 工具级 t（TCP 对针 = 工具中心偏置 TCO） ====================

        /// <summary>
        /// 工具中心偏置 TCO 任务 t（2026-09-08 语义重定义：从「EyeToHand 固定相机图像对针」扩展为
        /// 「工具中心(TCP)相对回转轴中心的世界系偏置量」——不同相机布局用不同采集方式求解同一物理量；
        /// 业界同义术语：TCP 对针 / Tool Center Offset；这是工具偏心对落点补偿的平移分量）。
        /// 派生判据（取代旧「仅 EyeToHand 固定相机」）：
        ///   ① 工具中心与回转轴【偏心】(ConcentricWithRotationAxis==false) → TCP 不在回转轴线上，
        ///      按像素走 H 落点落在回转中心而非工具尖，必须补 TCO；每个工具头派 1 条对针 t；
        ///   ② 工具中心【同心】→ TCO 恒为 0，H 输出即工具尖落点，无需对针（不派生）。
        /// 采集方式按相机布局分支（向导装配期按 spec.Layout 分发）：
        ///   · EyeInHand（相机随动，工具尖不可入视野）→ 间接对针：JOG 工具尖压住工件特征记机械位 R_n
        ///     → 抬 Z 回标定高度(XY 不动)相机拍同一特征得 u_feature → TCO = H(u_feature) − R_n；
        ///   · EyeToHand（固定相机能观测工具尖落点）→ 图像对针：步进→锁定 M_tool→点选实际落点→结算 δ。
        /// 执行依赖：仅相机 H（TCO 公式只需像素↔机械换算）。
        /// 任务排序（Derive 输出序即执行序）：H → e(角度作业时) → t；角度作业工位 e 先行是为了让
        /// TCO 的参考角与回转中心基准角 U0 语义一致（向导引导文案提示先完成 e 再做对针）。
        /// </summary>
        private static List<CalibrationTaskSpec> DeriveToolOffsetTasks(StationProfile profile,
            StationProfileRequirement req, List<CalibrationTaskSpec> cameraSpecs)
        {
            var result = new List<CalibrationTaskSpec>();

            // 判据①：工具中心必须偏离回转轴线才需要 TCO 对针（同心时 TCO=0，H 输出即工具尖落点）
            bool eccentric = req.ConcentricWithRotationAxis == false;
            if (!eccentric) return result;

            // 判据②：必须有相机 H 提供像素↔机械换算基准
            var handEye = FindFirstHandEye(cameraSpecs);
            if (handEye == null) return result;

            int toolCount = Norm.ParseToolHeadCount(req.ToolHeadCount);
            string station = Norm.Trim(profile.StationCode);
            bool eyeInHand = handEye.Layout == EyeMode.EyeInHand;
            bool needAngle = Norm.Trim(req.AngleNeed).Length > 0 && !Norm.HasNoAngle(req.AngleNeed);

            for (int i = 1; i <= toolCount; i++)
            {
                string nk = i.ToString();
                result.Add(new CalibrationTaskSpec
                {
                    SpecId = Key(station, "t", handEye.SlotKey, nk),
                    StationCode = station,
                    Quantity = CalibrationQuantity.ToolOffset,
                    SlotKey = handEye.SlotKey,
                    NozzleKey = nk,
                    Layout = handEye.Layout,
                    PrimaryPath = CalibrationAcquirePath.AlignTool,
                    DependentArtifactRef = handEye.ArtifactRef, // e_nozzle 公式只依赖 H
                    IsRequired = true, // 偏心工位对针是主线必做（落点补偿缺它不可用）
                    DisplayName = "工具中心偏置 TCO（工具头 " + nk + "）",
                    Suggestion = eyeInHand
                        ? "间接对针：JOG 工具尖压住工件特征记机械位 R_n → 抬 Z 拍同一特征 → TCO = H(u) − R_n"
                        : "图像对针：步进移动 → 锁定 M_tool → 图像点选实际落点 → 结算 δ 写入 TCO",
                    Reason = ReasonJoin(
                        "ConcentricWithRotationAxis=false(工具中心偏心)",
                        "工具中心偏离回转轴线 ⇒ 每工具头 1 条对针 t（TCP 相对回转轴中心的偏置）",
                        "布局=" + (eyeInHand ? "EyeInHand(间接对针:压特征+抬Z拍同点)" : "EyeToHand(图像对针)")),
                    Note = needAngle
                        ? "角度作业 + 偏心：先完成回转中心标定(e)再做本对针（TCO 参考角与基准角 U0 语义一致）"
                        : "无角度作业偏心工位：九点 H + 本对针即可，TCO 作平移补偿，无需回转中心标定"
                });
            }
            return result;
        }

        // ==================== 内部助手 ====================

        private static CalibrationTaskSpec FindFirstHandEye(IEnumerable<CalibrationTaskSpec> cameraSpecs)
        {
            if (cameraSpecs == null) return null;
            foreach (var s in cameraSpecs)
            {
                if (s != null && s.Quantity == CalibrationQuantity.HandEye) return s;
            }
            return null;
        }

        private static string Key(string station, string q, string slot, string nozzle)
        {
            string scope = nozzle != null
                ? "n" + (string.IsNullOrWhiteSpace(nozzle) ? "1" : nozzle)
                : (string.IsNullOrWhiteSpace(slot) ? "Cam_01" : slot);
            return string.Format("{0}|{1}|{2}",
                string.IsNullOrWhiteSpace(station) ? "?" : station, q, scope);
        }

        private static string ReasonJoin(params string[] parts)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in parts)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (sb.Length > 0) sb.Append("；");
                sb.Append(p);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 判据输入归一化助手（v2：把档案字符串自由文本判定收敛为单一真值来源；
    /// 后续问卷选项枚举化后，本类保留为兼容读取层）。
    /// </summary>
    public static class Norm
    {
        public static string Trim(string v)
        {
            return v == null ? string.Empty : v.Trim();
        }

        /// <summary>用途含"引导定位/纠偏"语义（几何标定门）</summary>
        public static bool IsGuidancePurpose(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.Contains("引导") || text.Contains("定位") || text.Contains("纠偏");
        }

        /// <summary>飞拍/运动成像语义</summary>
        public static bool IsFly(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.Contains("飞拍") || text.Contains("运动");
        }

        /// <summary>相机随执行机构移动语义（眼在手上）</summary>
        public static bool IsMovingMount(bool? movesWithActuator, string installText)
        {
            if (movesWithActuator == true) return true;
            if (movesWithActuator == false) return false;
            if (string.IsNullOrWhiteSpace(installText)) return false;
            return installText.Contains("眼在手上") || installText.Contains("随执行机构")
                   || installText.Contains("随动") || installText.Contains("移动相机");
        }

        /// <summary>斜拍语义</summary>
        public static bool IsSlant(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.Contains("斜") || text.Contains("倾斜");
        }

        /// <summary>角度需求含"无角度"语义</summary>
        public static bool HasNoAngle(string angleNeed)
        {
            if (string.IsNullOrWhiteSpace(angleNeed)) return false;
            return angleNeed.Contains("无角度") || angleNeed.Contains("不需要")
                   || angleNeed.Contains("不带") || angleNeed.Contains("无需");
        }

        /// <summary>工具头数解析（"1"/"2"/"多" → 数值）</summary>
        public static int ParseToolHeadCount(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return 1;
            string s = v.Trim();
            int n;
            if (int.TryParse(s, out n)) return n >= 1 ? n : 1;
            return s.Contains("多") ? 2 : 1;
        }
    }
}
