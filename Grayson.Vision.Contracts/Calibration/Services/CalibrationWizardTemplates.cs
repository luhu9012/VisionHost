//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationWizardTemplates.cs
// 说 明: 标定向导 v2 任务模板装配器（2026-09-05 重设计 P1a）。
//        输入 CalibrationTaskSpec（引擎 v2 产物）→ 输出有序 List&lt;CalibrationStepDef&gt;。
//        模板序列（角色序）随"物理量 × 采集路径"装配，替代旧固定四步壳：
//          H(走位/吸放/相机中心)      → Bind → Origin → Feature → SampleGrid → Compute → Verify
//          e(回转中心标定)            → Bind → Origin → Feature → SampleRotate → Compute → Verify
//          t(TCP 对针=TCO, 按布局)    → EyeInHand: Bind → Origin(示教基准位R_n) → Feature → AlignTool(间接对针) → Compute → Verify
//                                     EyeToHand: Bind → Feature → AlignTool(图像对针) → Compute → Verify
//          s(飞拍/标距当量)          → Bind → SampleGrid → Compute → Verify
//          畸变(预留)                → 空模板（面板显示"暂不可执行"原因）
//        2026-09-08：t 不再限 EyeToHand——EyeInHand 用"间接对针"求同一物理量（工具中心偏置 TCO）。
//        Origin 步文案按布局/路径变化（走位=基准位；吸放式=吸取位+拍照位；对针=示教基准位 R_n）。
//===================================================================================
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    using Grayson.Vision.Contracts.Calibration.Models;

    /// <summary>向导任务模板装配器（静态纯函数）</summary>
    public static class CalibrationWizardTemplates
    {
        /// <summary>
        /// 按任务规格装配向导步骤序列。spec 为 null / 畸变 → 返回空表（调用方按量提示）。
        /// </summary>
        public static List<CalibrationStepDef> BuildFor(CalibrationTaskSpec spec)
        {
            var steps = new List<CalibrationStepDef>();
            if (spec == null) return steps;

            switch (spec.Quantity)
            {
                case CalibrationQuantity.HandEye:
                    return BuildHandEye(spec);
                case CalibrationQuantity.ToolRotation:
                    return BuildToolRotation(spec);
                case CalibrationQuantity.ToolOffset:
                    // 2026-09-08：TCP 对针不再限制 EyeToHand——EyeInHand 间接对针已支持
                    // （示教基准位 R_n → 抬Z拍同点 → TCO = H(u)−R_n），模板内按 spec.Layout 分发采集方式
                    return BuildToolOffset(spec);
                case CalibrationQuantity.PixelScale:
                    return BuildPixelScale(spec);
                case CalibrationQuantity.LensDistortion:
                    return steps; // 预留：无可执行步骤
                default:
                    return steps;
            }
        }

        /// <summary>模板可执行？空模板(畸变预留)返回 false，UI 灰显</summary>
        public static bool IsExecutable(CalibrationTaskSpec spec)
        {
            return spec != null && BuildFor(spec).Count > 0;
        }

        // ==================== H（相机级） ====================

        private static List<CalibrationStepDef> BuildHandEye(CalibrationTaskSpec spec)
        {
            bool eyeInHand = spec.Layout == EyeMode.EyeInHand
                             || spec.PrimaryPath == CalibrationAcquirePath.NozzleTruthWalk;
            bool pickPlace = spec.PrimaryPath == CalibrationAcquirePath.PickPlaceReturn;

            var steps = new List<CalibrationStepDef>
            {
                new CalibrationStepDef(WizardStepRole.BindDevices,
                    "绑定相机与运动轴",
                    eyeInHand
                        ? "确认随动相机槽 " + SlotText(spec.SlotKey) + " 与走位轴（X/Y）绑定；Epson 设备轴号已按机械手档案自动预填"
                        : "确认固定相机槽 " + SlotText(spec.SlotKey) + " 与机构走位轴（X/Y）绑定；Epson 设备轴号已按档案预填",
                    "bind", false, null)
            };

            // Origin 步：文案按路径
            string originTitle, originGuide, originPanel = "origin";
            if (pickPlace)
            {
                originTitle = "定义吸取位与固定拍照位";
                originGuide = "示教初始吸取位（吸嘴在此吸住工件，真空检知确认）；再示教固定拍照位（每次放料后机构回此位成像）——两者是本任务走位的机械基准";
                originPanel = "origin_pickplace";
            }
            else
            {
                originTitle = eyeInHand ? "设基准位（Mark 对准视野中心）" : "设相机中心基准（平台格点对准）";
                originGuide = eyeInHand
                    ? "移动机构使标定特征对准相机视野中心，记录当前机械位（网格中心基准）。注意：本路径真值=吸嘴尖落点，H 将吸收工具偏距，无需再做对针 t"
                    : "将平台/工件特征移到固定相机视野中心记录机械位（相机中心真值路径——若采用本路径，工具尖偏距需配合可选 t 对针卡）";
                originPanel = "origin_walk";
            }
            steps.Add(new CalibrationStepDef(WizardStepRole.DefineOrigin, originTitle, originGuide,
                originPanel, false, null));

            steps.Add(new CalibrationStepDef(WizardStepRole.DefineFeature,
                "配置标定特征",
                pickPlace
                    ? "选择工件/治具上可稳定提取的特征：圆 Mark（提取圆心）/ 十字 Mark / 模板匹配（复用全局模板库，对反光工件首选）"
                    : "选择标定特征（圆 Mark / 十字 Mark / 模板匹配）；特征必须落在 3×3 网格每个位置的视野内",
                "feature", false, null));

            steps.Add(new CalibrationStepDef(WizardStepRole.SampleGrid,
                pickPlace ? "吸放式网格采集" : "走位网格采集",
                pickPlace
                    ? "逐格：吸住工件 → 移到命令位放落 → 回拍照位成像 → 提取特征。命令坐标即真值（已含工具偏距，勿再叠加 t）"
                    : eyeInHand
                        ? "逐格走位使特征对准相机视野中心（吸嘴对格点语义）→ 成像 → 记录机械位与像素。共 3×3=9 点（可自动全采）"
                        : "逐格走位平台使特征落在相机视野中心 → 成像 → 记录机械位与像素。共 3×3=9 点（可自动全采）",
                eyeInHand ? "grid_walk" : (pickPlace ? "grid_pickplace" : "grid_cameratruth"),
                true, null));

            steps.Add(BuildCompute());
            steps.Add(BuildVerify());
            return steps;
        }

        // ==================== e（工具级旋转） ====================

        private static List<CalibrationStepDef> BuildToolRotation(CalibrationTaskSpec spec)
        {
            string depText = string.IsNullOrWhiteSpace(spec.DependentArtifactRef)
                ? "前置相机 H"
                : "相机 H(" + spec.DependentArtifactRef + ")";
            bool pickPlaceRotate = spec.PrimaryPath == CalibrationAcquirePath.RotatePickPlace;

            var steps = new List<CalibrationStepDef>
            {
                new CalibrationStepDef(WizardStepRole.BindDevices,
                    "绑定旋转轴与吸嘴",
                    "确认旋转轴 U（Epson=3号轴）与吸嘴通道 n" + spec.NozzleKey + " 绑定；旋转采样与坐标换算使用主相机槽 " + SlotText(spec.SlotKey),
                    "bind", false, "依赖：" + depText + " 已发布（提供机械坐标系）")
            };
            if (pickPlaceRotate)
            {
                steps.Add(new CalibrationStepDef(WizardStepRole.DefineOrigin,
                    "设放料回拍照位",
                    "示教固定放料位与回拍照位：吸嘴吸住工件转 U 至各角度后放落，回拍照位成像测工件位移（吸放式旋转段语义）",
                    "origin_rotate_pickplace", false, null));
            }
            else
            {
                steps.Add(new CalibrationStepDef(WizardStepRole.DefineOrigin,
                    "确认旋转观测就绪",
                    "吸嘴吸附延伸杆/治具特征（特征须随 U 旋转保持可提取）：确认相机槽 " + SlotText(spec.SlotKey) + " 在 -45°~+45° 转角内视野都能覆盖特征轨迹；移动相机(EyeInHand)亦适用——转角过大特征出视野时收窄采样角",
                    "origin_rotate_camview", false, null));
            }
            steps.Add(new CalibrationStepDef(WizardStepRole.DefineFeature,
                "配置旋转观测特征",
                "选择旋转采样用的特征（延伸杆/治具上的工件图案模板匹配首选；或吸嘴上可观测标记圆/十字）；特征须随 U 旋转保持可提取",
                "feature", false, null));
            steps.Add(new CalibrationStepDef(WizardStepRole.SampleRotate,
                "旋转角度采样",
                pickPlaceRotate
                    ? "吸住工件 → 依次转 U = {-45°, 0°, +45°}（默认 3 点、跨度 90°，过覆盖门控）→ 每角度放落回拍 → 提取工件中心。偏心大时转大角度特征会出视野/模板失配，默认收窄；现场特征清晰且跨度可拉大时应尽量拉开分散（越均匀圆心拟合越稳，可在表内改）"
                    : "保持延伸杆/治具特征在视野内，依次转 U = {-45°, 0°, +45°}（默认 3 点）成像 → 提取特征轨迹点（特征须全程在视野且模板角度范围够；≥3 点不共线即可拟合，越分散圆心越稳）",
                "rotate", true, null));
            steps.Add(BuildCompute());
            steps.Add(BuildVerify());
            return steps;
        }

        // ==================== t（TCP 对针 = 工具中心偏置 TCO；EyeInHand 间接 / EyeToHand 图像） ====================

        /// <summary>
        /// TCP 对针步骤模板（2026-09-08 语义重定义）：从「EyeToHand 专属图像对针」扩展为
        /// 「任意相机布局下的工具中心偏置 TCO（Tool Center Offset）」——采集方式按布局分发，
        /// 求解的是同一物理量（工具中心 TCP 相对回转轴中心的世界系偏置），发布语义一致：
        ///   · EyeInHand（相机随动，工具尖不可入视野）→ 间接对针：
        ///     ① DefineOrigin 示教基准位：JOG 工具尖压住工件特征记机械位 R_n（TCP 此刻=特征点）；
        ///     ② DefineFeature 配置同一特征（抬 Z 后相机要能识别它）；
        ///     ③ AlignTool 执行对针：抬 Z 回标定高度(XY 不动)相机拍同一特征得像素 u_feature，
        ///        结算 TCO = H(u_feature) − R_n。
        ///        数学依据：H 是相机↔机械的 2D 仿射；抬 Z 不改变 XY，故「特征点真位 H(u_feature)」
        ///        与「工具尖真位 R_n + TCO」是同一物理点 → TCO = H(u_feature) − R_n。
        ///   · EyeToHand（固定相机能观测工具尖落点）→ 图像对针：步进→锁 M_tool→点选落点→结算 δ。
        /// 角色复用：两布局的对针执行均落在 WizardStepRole.AlignTool（EyeToHand=align 面板，
        /// EyeInHand=align_eih 面板）；EyeInHand 多一步 DefineOrigin 用于示教基准位 R_n。
        /// </summary>
        private static List<CalibrationStepDef> BuildToolOffset(CalibrationTaskSpec spec)
        {
            bool eyeInHand = spec.Layout == EyeMode.EyeInHand;
            string depText = string.IsNullOrWhiteSpace(spec.DependentArtifactRef)
                ? "前置相机 H"
                : "相机 H(" + spec.DependentArtifactRef + ")";
            var steps = new List<CalibrationStepDef>
            {
                new CalibrationStepDef(WizardStepRole.BindDevices,
                    "绑定对针相机与工具头",
                    eyeInHand
                        ? "确认随动相机槽 " + SlotText(spec.SlotKey) + " 能拍到工件特征。注意：相机与工具头同装于执行器末端，工具压住特征时相机拍不到工具尖——本布局用『间接对针』（压特征记机械位 + 抬 Z 拍同点），无需直接观测工具"
                        : "确认固定相机槽 " + SlotText(spec.SlotKey) + " 能观测工具尖落点（EyeToHand 图像对针前提）",
                    "bind", false, "依赖：" + depText + " 已发布（TCO = H(u) − R_n 需要像素↔机械换算）")
            };

            if (eyeInHand)
            {
                // ---- EyeInHand 间接对针：示教基准位 R_n → 配置特征 → 抬Z拍同点结算 TCO ----
                steps.Add(new CalibrationStepDef(WizardStepRole.DefineOrigin,
                    "示教对针基准位（工具压住特征）",
                    "JOG 工具头 " + spec.NozzleKey + " 的尖端对准/轻压当前工件特征中心（可用塞尺/千分表辅助确认刚好接触），点击【📍 记基准位 R_n】记录机械坐标。原理：此位是『工具 TCP 恰好落在特征点上』的唯一时刻——相机拍不到没关系，机械读数已隐含工具位置；抬 Z 后相机拍到的同一特征点与此时压住的点是同一物理点",
                    "align_origin", false, null));
                steps.Add(new CalibrationStepDef(WizardStepRole.DefineFeature,
                    "配置特征（抬 Z 后成像用）",
                    "配置工件上的圆 Mark/十字/模板特征：抬 Z 回标定高度后相机要能稳定识别它（它与你刚才用工具压住的是同一物理点，须清晰、无遮挡、光照稳定）",
                    "feature", false, null));
                steps.Add(new CalibrationStepDef(WizardStepRole.AlignTool,
                    "间接对针（抬 Z 拍同点 → 结算 TCO）",
                    "① 已示教基准位 R_n 后，抬 Z 回标定高度（XY 不动）→ 抓拍定格；② 点选画面中刚才压住的那个特征；③ 点【🔍 结算 TCO】。系统自动算 TCO = H(u_feature) − R_n 并写入档案（H 用标定 Z 高度的映射，抬 Z 到位是前提）",
                    "align_eih", true, null));
            }
            else
            {
                // ---- EyeToHand 图像对针：固定相机步进→锁定→点选落点 ----
                steps.Add(new CalibrationStepDef(WizardStepRole.DefineFeature,
                    "配置对针特征",
                    "固定相机视野内放置可辨识对准特征（工具头自带标记 / 标定针 / 工件角点）；特征中心即「落点」判定基准",
                    "feature", false, null));
                steps.Add(new CalibrationStepDef(WizardStepRole.AlignTool,
                    "图像对针（步进→锁定→点选）",
                    "步进 JOG 移动工具头到特征上方 → 锁定 M_tool（读轴反馈）→ 移开工具抓拍自动画理论点 → 图像点选实际落点 A' → 界面给出 δ_world → 写入 ToolOffset(TCO)",
                    "align", false, null));
            }
            steps.Add(BuildCompute());
            steps.Add(BuildVerify());
            return steps;
        }

        // ==================== s（像素当量） ====================

        private static List<CalibrationStepDef> BuildPixelScale(CalibrationTaskSpec spec)
        {
            bool fly = spec.PrimaryPath == CalibrationAcquirePath.FlyPixelScale;
            var steps = new List<CalibrationStepDef>
            {
                new CalibrationStepDef(WizardStepRole.BindDevices,
                    "绑定相机与走位轴",
                    "确认 " + (fly ? "固定相机（飞拍纠偏）" : "像素当量相机") + " 槽 " + SlotText(spec.SlotKey) + " 与已知位移轴绑定",
                    "bind", false, null)
            };
            steps.Add(new CalibrationStepDef(WizardStepRole.SampleGrid,
                fly ? "飞拍标距采样" : "标距走位采样",
                fly
                    ? "工件以固定速度通过相机视野：多帧采样已知位移的工件特征，标定像素当量 + 触发相位（运动补偿细节在配方层）"
                    : "平台/标尺走已知距离（如 10mm 网格），逐点成像测像素位移 → 换算 mm/px",
                "grid_scale", true, null));
            steps.Add(BuildCompute());
            steps.Add(BuildVerify());
            return steps;
        }

        // ==================== 公共尾步 ====================

        private static CalibrationStepDef BuildCompute()
        {
            return new CalibrationStepDef(WizardStepRole.ComputeFit,
                "拟合计算与几何自检",
                "执行拟合（仿射/圆拟合/对针 δ 结算）；自检方向符号/尺度/残差 RMS——自检不过锁定发布按钮，先回采样步修正",
                "compute", false, "采样点数满足最小要求（网格 ≥3 非共线 / 旋转 ≥3 点）");
        }

        private static CalibrationStepDef BuildVerify()
        {
            return new CalibrationStepDef(WizardStepRole.VerifyAndPublish,
                "验收与发布",
                "发布门禁：残差体检（离线读采样快照重投影，RMS/MAX 阈值）必过；通过后发布 artifact 供配方消费。现场可勾\"我知道风险\"旁路（留痕）",
                "verify", false, null);
        }

        private static string SlotText(string slotKey)
        {
            return string.IsNullOrWhiteSpace(slotKey) ? "?" : slotKey;
        }
    }
}
