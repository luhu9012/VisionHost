//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationProposalEngine.cs
// 说 明: 新建工位向导的"方案推导引擎"（v1.1 §0.3 规则表 + §0.5 行业模板，骨架期实现）。
//        · 行业模板 = 事实表（预填问卷 + 规则选择 + 文案）——骨架期先用 C# 只读表，
//          P 段按文档抽为 JSON 事实表（Data\IndustryTemplates\*.json），只加表不改程序；
//        · 推导函数：问卷答案 → 建议流程骨架 / 建议标定方案 / 资产占位清单 / 可行性告警，
//          写入 StationProfile 的派生建议字段（可重算，工作台刷新时再次调用）。
//        · 可行性告警为粗估：按 1600px 宽传感器估算像素当量，精确值待 S1 绑定相机后复核。
//===================================================================================
using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Station.Models;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>行业方案模板事实表行（v1.1 §0.5；骨架期 C# 内嵌，P 段抽 JSON）</summary>
    public class IndustryTemplateDef
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string Icon { get; set; }
        /// <summary>一句话说明（向导卡片副标题）</summary>
        public string Description { get; set; }
        /// <summary>预填任务类型</summary>
        public string TaskType { get; set; }
        /// <summary>预填相机安装</summary>
        public string CameraMount { get; set; }
        /// <summary>预填拍照方式</summary>
        public string ShootMode { get; set; }
        /// <summary>预填打光</summary>
        public string Lighting { get; set; }
        /// <summary>预填判定输出</summary>
        public string VerdictOutput { get; set; }
    }

    /// <summary>任务类型 → 方案推导结论</summary>
    public class ProposalResult
    {
        public string FlowSkeleton { get; set; }
        public string Calibration { get; set; }
        public List<string> Assets { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>新建工位向导推导引擎（纯函数无状态；输入问卷 → 输出 StationProfile 派生建议）</summary>
    public static class StationProposalEngine
    {
        // ==================== 0.5 行业方案模板（骨架期内嵌，P 段抽 JSON）====================
        public static readonly List<IndustryTemplateDef> IndustryTemplates = new List<IndustryTemplateDef>
        {
            new IndustryTemplateDef
            {
                Code = "pick_place", Name = "定位抓取引导", Icon = "🎯",
                Description = "麻将/工件对位：识别目标位姿→换算→引导机械手/轴抓取",
                TaskType = "定位抓取", CameraMount = "眼在手上", ShootMode = "精拍",
                Lighting = "正面环形光", VerdictOutput = "引导坐标"
            },
            new IndustryTemplateDef
            {
                Code = "measure", Name = "尺寸测量", Icon = "📏",
                Description = "轮廓/孔径/间距：定位基准后卡尺/拟合量测，输出数值+OKNG",
                TaskType = "尺寸测量", CameraMount = "眼在手外", ShootMode = "精拍",
                Lighting = "背光", VerdictOutput = "测量值"
            },
            new IndustryTemplateDef
            {
                Code = "defect", Name = "外观/缺陷检测", Icon = "🔎",
                Description = "表面瑕疵：Blob/模板比对/DL 检出缺陷，输出 OK/NG",
                TaskType = "外观缺陷", CameraMount = "眼在手外", ShootMode = "精拍",
                Lighting = "正面环形光", VerdictOutput = "OK/NG"
            },
            new IndustryTemplateDef
            {
                Code = "read_ocr", Name = "读取追溯", Icon = "🔤",
                Description = "条码/字符/OCR：圈读取区读取内容上报 MES",
                TaskType = "OCR·字符", CameraMount = "眼在手外", ShootMode = "精拍",
                Lighting = "同轴光", VerdictOutput = "MES"
            },
            new IndustryTemplateDef
            {
                Code = "presence", Name = "有无/装配检测", Icon = "✅",
                Description = "快速判定目标有无/是否装配到位，输出 OK/NG",
                TaskType = "有无检测", CameraMount = "眼在手外", ShootMode = "精拍",
                Lighting = "正面环形光", VerdictOutput = "OK/NG"
            }
        };

        /// <summary>按 Code 取行业模板（未知返回 null）</summary>
        public static IndustryTemplateDef GetTemplate(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            foreach (var t in IndustryTemplates)
            {
                if (string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        /// <summary>取任务类型枚举集合（下拉/角标提示用）</summary>
        public static readonly string[] TaskTypeOptions =
        {
            "定位抓取", "尺寸测量", "外观缺陷", "OCR·字符", "有无检测", "高度·3D", "多任务组合", "其他·待确认"
        };

        public static readonly string[] CameraMountOptions = { "眼在手上", "眼在手外" };
        public static readonly string[] ShootModeOptions = { "精拍", "飞拍" };
        public static readonly string[] LightingOptions = { "背光", "正面环形光", "同轴光", "无(环境)" };
        public static readonly string[] VerdictOutputOptions = { "OK/NG", "测量值", "MES", "引导坐标" };

        // ==================== 拓扑选项（2026-09-05 增补：执行机构 + 相机槽）====================
        /// <summary>执行机构形态（工位主力执行器语义域；影响标定轴约定与走位提示）</summary>
        public static readonly string[] ActuatorKindOptions =
        {
            "SCARA/6轴机器人", "运动板卡直驱", "PLC + 外部执行器", "其他·待确认"
        };

        /// <summary>相机槽安装方式（首项=随执行机构；与旧 CameraMount "眼在手上/眼在手外" 语义衔接）</summary>
        public static readonly string[] CameraInstallOptions =
        {
            "眼在手上（随执行机构）", "上固定（俯视工面）", "下固定（仰视工面）", "侧斜拍（斜视角）"
        };

        /// <summary>相机槽用途职能（决定标定建议类型与流程中该相机的职责语义）</summary>
        public static readonly string[] CameraPurposeOptions =
        {
            "引导定位", "对位纠偏", "尺寸测量", "缺陷检测", "OCR·字符", "有无检测"
        };

        public static readonly string[] CameraAxisSurfaceOptions = { "垂直拍摄", "斜拍" };

        /// <summary>
        /// 相机与轴的跟随关系（★2026-09-12 增补，G5 缺口）：相机随执行机构哪些轴联动。
        /// 空串=待确认；固定=不随任何轴（眼在手外）；跟随X/XY/XYZ/XYU/XYZU… = 眼在手上但仅随指定轴。
        /// 轴名取自执行机构轴约定（SCARA: X/Y/Z/U；双滑台: X/Y左/Y右/Z…）。
        /// </summary>
        public static readonly string[] AxisFollowsOptions =
        {
            string.Empty,
            "固定（不随动）",
            "跟随X",
            "跟随XY",
            "跟随XYZ",
            "跟随XYU",
            "跟随XYZU"
        };

        /// <summary>
        /// 按相机槽推导建议标定（静态纯函数；RebuildProposal 聚合 + 标定中心候选清单共用）。
        /// 判据优先级：纠偏/飞拍 > 随动(眼在手上) > 斜拍 > 固定引导。
        /// </summary>
        public static string RecommendCalibrationForSlot(VisionSlotInfo slot)
        {
            if (slot == null) return "标定待定（先补相机槽信息）";
            string purpose = slot.Purpose ?? string.Empty;
            string install = slot.InstallKind ?? string.Empty;
            string axis = slot.AxisToSurface ?? string.Empty;
            string axisFollows = slot.AxisFollows ?? string.Empty;
            // 飞拍仅以 ShootMode 为权威判据（purpose"飞拍纠偏"里的"纠偏"是静止对位语义，不触发飞拍）
            bool fly = string.Equals(slot.ShootMode, "飞拍");
            bool moving = install.Contains("眼在手上");
            bool slant = axis.Contains("斜") || install.Contains("斜");
            // ★ 2026-09-12 轴跟随：含 Z → 相机随 Z 升降（拍照须回标定高度）；否则不随 Z
            bool followsZ = axisFollows.Contains("Z");

            if (fly)
                return "飞拍纠偏：像素当量 + 触发/相位补偿标定（运动中成像，出相对偏差即可）";
            if (moving)
                return "手眼标定（随动相机 ↔ 执行机构坐标换算；带角度需求再补旋转中心/偏心补偿）"
                       + (axisFollows.Length > 0
                           ? $" —— 轴跟随「{axisFollows}」" + (followsZ ? "（随 Z：拍照须回标定高度）" : "（不随 Z：成像与 Z 无关）")
                           : "（轴跟随未填，建议明确随哪些轴）");
            if (slant)
                return "九点标定（斜拍视角，建议先做畸变矫正 CameraLensDistortion 前置）";
            return "九点标定（固定相机：像素 ↔ 机械平面映射）";
        }

        /// <summary>按执行机构形态产出轴约定提示（标定向导防错；空=不提示）</summary>
        public static string RecommendActuatorHint(string actuatorKind)
        {
            if (string.IsNullOrWhiteSpace(actuatorKind)) return string.Empty;
            if (actuatorKind.Contains("机器人"))
                return "执行机构=机器人：标定向导请按 SCARA 约定选轴（旋转轴=U(3号)、Z=2号、吸嘴1 真空 OUT15）";
            if (actuatorKind.Contains("板卡"))
                return "执行机构=运动板卡：标定轴槽位按现场轴表配置（ZMC 双滑台约定 0号=Z/旋转）";
            if (actuatorKind.Contains("PLC"))
                return "执行机构=PLC：走位由 PLC 程序承载，标定只建立像素关系，采集同步走 PLC 触发";
            return string.Empty;
        }

        // ==================== 0.3 任务 → 方案推导规则 ====================
        /// <summary>由任务类型推导方案基线（不考虑成像/工具微调）</summary>
        private static ProposalResult BaseProposal(string taskType)
        {
            var p = new ProposalResult();
            switch (taskType)
            {
                case "定位抓取":
                    p.FlowSkeleton = "采图 → 模板匹配 → 坐标换算(标定) → CalibrationApply → 移动";
                    p.Assets.Add("定位模板×1（可加特征点）");
                    p.Assets.Add("CalibrationProfile×1");
                    p.Assets.Add("示教点/目标位姿");
                    break;
                case "尺寸测量":
                    p.FlowSkeleton = "采图 → 定位基准(模板) → 量测(卡尺/拟合) → 判据 → OKNG";
                    p.Assets.Add("基准定位模板×1");
                    p.Assets.Add("量测 ROI×N（特征面）");
                    break;
                case "外观缺陷":
                    p.FlowSkeleton = "采图 → 检测(Blob/模板比对/DL) → 判定 → OK/NG";
                    p.Assets.Add("良品参考模板/样本集");
                    p.Assets.Add("缺陷判定判据");
                    break;
                case "OCR·字符":
                    p.FlowSkeleton = "采图 → 定位(可选) → 读取(OCR/条码) → 上报文本";
                    p.Assets.Add("读取区特征面×N");
                    p.Assets.Add("字符/条码模板（如需定位）");
                    break;
                case "有无检测":
                    p.FlowSkeleton = "采图 → 快速判定(Blob/灰度) → OK/NG";
                    p.Assets.Add("判定区域 ROI");
                    break;
                case "高度·3D":
                    p.FlowSkeleton = "采图(3D) → 高度图处理 → 判据 → OKNG";
                    p.Assets.Add("3D 相机接入（占位/待定）");
                    p.Warnings.Add("高度/3D 依赖 3D 相机与 SDK，当前为占位方案，需评估相机选型");
                    break;
                case "多任务组合":
                    p.FlowSkeleton = "多任务链：主定位 + 子检测，逐段确认";
                    p.Assets.Add("主定位模板 + 各子任务资产（按子任务类型推导）");
                    p.Warnings.Add("多任务组合需在工作台 S4 逐段细化视觉链，向导仅给主链建议");
                    break;
                default: // null/空/其他·待确认
                    p.FlowSkeleton = "待确认：先明确检测任务类型，向导才能推导建议视觉链";
                    p.Calibration = "待确认（按任务与相机安装方式再定）";
                    p.Warnings.Add("任务类型未确认：请在向导①视觉任务 选择检测对象类型（或从行业模板开始）");
                    break;
            }
            return p;
        }

        /// <summary>
        /// 综合推导（向导字段任一变化/存草稿/创建工位前调用）：
        /// 任务类型基线 + 成像（标定选型/飞拍提示/光照）+ 工具（角度/偏心）+ 光学（像素当量告警）。
        /// 输出写回 profile 派生字段（可重算）。
        /// </summary>
        public static void RebuildProposal(StationProfile profile)
        {
            if (profile == null) return;
            profile.AssetSuggestions = new List<string>();
            profile.FeasibilityWarnings = new List<string>();

            var req = profile.Requirement ?? new StationProfileRequirement();
            var p = BaseProposal(req.TaskType);

            // —— 标定选型（2026-09-05 起）：有相机槽 → 逐槽推导聚合；无槽 → 旧单相机逻辑 ——
            bool hasSlots = req.CameraSlots != null && req.CameraSlots.Count > 0;
            string calib = p.Calibration ?? string.Empty;
            if (hasSlots)
            {
                var slotLines = new List<string>();
                var slotAssets = new List<string>();
                foreach (var slot in req.CameraSlots)
                {
                    if (slot == null) continue;
                    string tag = string.IsNullOrWhiteSpace(slot.SlotKey) ? "相机槽" : slot.SlotKey;
                    string install = string.IsNullOrWhiteSpace(slot.InstallKind) ? "安装待定" : slot.InstallKind;
                    string purpose = string.IsNullOrWhiteSpace(slot.Purpose) ? "用途待定" : slot.Purpose;
                    string rec = RecommendCalibrationForSlot(slot);
                    slotLines.Add($"{tag}（{install} · {purpose}）→ {rec}");
                    string shortRec = rec.Contains("：") ? rec.Substring(0, rec.IndexOf("：")) : rec;
                    slotAssets.Add($"{tag} {shortRec}标定 ×1（{install}）");
                }
                p.Assets.RemoveAll(a => a.Contains("CalibrationProfile")); // 基线单条占位由槽级清单替代
                p.Assets.AddRange(slotAssets);
                calib = slotLines.Count > 0 ? string.Join("\n", slotLines) : calib;
            }
            else if (string.IsNullOrWhiteSpace(calib))
            {
                if (string.Equals(req.CameraMount, "眼在手上")) calib = "手眼标定（随动相机与工具同轴换算）";
                else if (string.Equals(req.CameraMount, "眼在手外")) calib = "九点标定（像素↔机械平面映射）";
                else calib = "待确认（需先定相机安装方式：眼在手上→手眼 / 眼在手外→九点）";
            }
            p.Calibration = calib;

            // —— 执行机构轴约定提示（防错：SCARA 与板卡轴语义不同，错轴有撞机风险）——
            string actHint = RecommendActuatorHint(req.ActuatorKind);
            if (!string.IsNullOrWhiteSpace(actHint) && !p.Warnings.Contains(actHint)) p.Warnings.Add(actHint);

            // —— 飞拍提示 ——
            if (string.Equals(req.ShootMode, "飞拍"))
            {
                p.Assets.Add("飞拍标定/运动补偿（选配）");
                p.Warnings.Add("飞拍模式需要运动中拍照标定或闪光触发；节拍允许时优先精拍（静止成像最稳）");
            }
            // —— 光照提示 ——
            if (string.Equals(req.Lighting, "无(环境)"))
            {
                p.Warnings.Add("无主动光源：光照随环境漂移风险高——模板匹配建议开启平场校正，或补环形/同轴光");
            }
            // —— 角度需求 ——
            if (!string.IsNullOrWhiteSpace(req.AngleNeed) && !req.AngleNeed.Contains("无"))
            {
                p.Assets.Add("匹配角度范围按需求开启（任意角度→-180~180）");
            }
            // —— 工具偏心 ——
            if (req.ConcentricWithRotationAxis == false)
            {
                p.Warnings.Add("工具偏心：需做旋转中心标定 + 偏心补偿（对针/示教按 NozzleToolOffset 处理）");
                p.Assets.Add("旋转中心标定 / 偏心补偿参数");
            }
            // —— 光学可行性粗估：min 特征像素 = (MinFeatureMm/FovMm) × 1600 ——
            if (TryParseMm(req.FovMm, out double fov) && TryParseMm(req.MinFeatureMm, out double mf) && fov > 0)
            {
                const double sensorWidthPx = 1600.0; // 粗估（2MP 级别），精确值以 S1 实际绑定相机为准
                double pxPerMm = sensorWidthPx / fov;
                double featurePx = mf * pxPerMm;
                if (featurePx < 5)
                {
                    p.Warnings.Add($"最小特征 {mf:0.##}mm 在 FOV {fov:0}mm 下仅约 {featurePx:0.#}px（粗估）：远低于可靠识别下限，强烈建议缩小 FOV 或换高分辨率相机");
                }
                else if (featurePx < 10)
                {
                    p.Warnings.Add($"最小特征 {mf:0.##}mm 在 FOV {fov:0}mm 下约 {featurePx:0.#}px（粗估，按 1600px 宽）：偏小，建议缩小 FOV 或提高分辨率；<10px 模板匹配不稳");
                }
            }
            else if (TryParseMm(req.MinFeatureMm, out _) && !TryParseMm(req.FovMm, out _))
            {
                p.Warnings.Add("已填最小特征但缺 FOV：填 FOV(mm) 后可做像素当量可行性校验");
            }

            // —— 写回派生字段 ——
            profile.SuggestedFlowSkeleton = p.FlowSkeleton;
            profile.CalibrationSuggestion = p.Calibration;
            profile.AssetSuggestions = p.Assets;
            profile.FeasibilityWarnings = p.Warnings;
        }

        private static bool TryParseMm(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return double.TryParse(s.Trim().Replace("mm", ""), out v);
        }
    }
}
