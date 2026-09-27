//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationMethodCatalog.cs
// 说 明: 标定方式总表 —— "标定"与"标定使用"之间的【唯一真源】。
//
// 【要解决的问题（2026-09-17 用户诉求：把标定↔标定使用之间的过程概念足够精简）】
//   新增一种标定方式，以前要动 6 处、横跨 4 个项目，而且没有任何一处是"总表"。
//   更糟的是前两处【各判一次同一件事，判据还分叉了】：
//     · 建档提示 StationProposalEngine.RecommendCalibrationForSlot  判"飞拍" 用 == "飞拍"
//     · 任务派生 CalibrationPlanEngineV2.DeriveFromSlot              判"飞拍" 用 Contains("飞拍")
//   于是同一个工位在"建档建议"与"任务卡"上说着两句不同的话（现场实例：ST_007 的 Cam_B
//   是下固定仰视，建档说"九点标定（固定相机）"、任务卡说"下相机吸件走位式"——两把尺子）。
//
// 【做法】把"建档条件 → 采集怎么做 → 产出什么 → 消费怎么用"写成【一行数据】。
//   四个出口都从同一行读 ⇒ **判断只发生一次，渲染发生四次**：
//     ① 建档提示   StationProposalEngine.RecommendCalibrationForSlot  → 读 row.ShortName / Basis
//     ② 任务派生   CalibrationPlanEngineV2.DeriveFromSlot              → 读 row.Quantity/PrimaryPath/HowTo
//     ③ 向导装配   CalibrationWizardViewModel.SelectStrategy / Step3DataTemplateSelector
//                                                                    → 读 row.WizardStrategy/StepTemplate
//     ④ 消费判定   CalibrationConsumptionContract.Resolve             → 读 row.Consumption
//   新增一种方式 = **加一行**（仅当确需新的采样动作时，才再加一个 ICalibrationStepStrategy）。
//
// 【三层词汇的分工（别再混）】
//   · 建档问卷    = 中文自由文本（InstallKind/Purpose/ShootMode/AxisFollows…）—— 外部输入，本文件不改它
//   · SlotFacts   = 把自由文本折叠成布尔事实 —— 【字符串匹配只允许发生在这里】
//   · 本表一行    = 事实 → (标定量, 采集路径, 消费口径, 向导策略) —— 【方式选择只允许发生在这里】
//   · 消费契约    = 口径 → 算式（CalibrationConsumptionContract）—— 判定权仍在它那里，本表只声明用哪一档
//
// 【加一行要写什么】照着 SlotRows / ToolRows 里最近的一行抄。最容易写错的四个字段：
//   · Quantity / PrimaryPath —— 产物与采样语义；决定向导怎么装配、档案怎么落盘
//   · Consumption            —— 生产时怎么把这个量用起来；null = 本量不直接产生口径
//                              （e/t/s 就是这种情况：它们是被 H 档消费的参数，不是口径本身）
//   · WizardStrategy / StepTemplate —— 向导第三步用哪套采样/拟合（键必须在 Known* 里注册）
//   · Match                  —— 建档条件（结构化布尔；**不许**再写字符串 Contains）
//   写完跑 CalibrationMethodCatalog.Validate()（纯脱机，不需要设备/档案/界面）。
//
// 【条件前置】槽上可以显式声明 <see cref="SlotFacts.MethodId"/>（= 本表某行的 Id）：
//   声明优先于一切推断 —— 这是"在建立工位档案时就把标定方式定下来"的落点。
//   声明了一个不存在的 Id 或不适用的方式 ⇒ **报错返回，静默退回推断是不允许的**
//   （"看起来生效了"比"没配"更危险）。
//===================================================================================
using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Station.Models;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>标定方式的作用域：单槽相机级 / 工位工具级。</summary>
    public enum MethodScope
    {
        /// <summary>相机槽级（H / s /跨相机映射）——每槽 0..1 条</summary>
        Slot = 0,

        /// <summary>工具级（e / t）——按工具头各 1 条，依赖相机级产物定坐标系</summary>
        Tool = 1,
    }

    /// <summary>
    /// 相机槽的**结构化事实**。
    ///
    /// ★ 本类存在的唯一理由：把"中文自由文本 → 布尔"的翻译**收敛到一处**。
    ///   在此之前同一件事被翻译了两遍（建档引擎一遍、计划引擎一遍），且规则不同 ⇒ 必然分叉。
    ///   以后要改判据（比如问卷选项枚举化了），**只改这里**，四个出口同时跟着变。
    /// </summary>
    public sealed class SlotFacts
    {
        public string SlotKey = "Cam_?";
        public string InstallKind = "";
        public string Purpose = "";
        public string ShootMode = "";
        public string AxisFollows = "";
        public string AxisToSurface = "";
        public bool IsDisabled;

        /// <summary>启用状态（停用槽不派生任何标定任务）</summary>
        public bool IsEnabled { get { return !IsDisabled; } }

        /// <summary>★条件前置：本槽显式声明的标定方式 Id（空 = 按下方事实推断）</summary>
        public string MethodId = "";

        // ---------- 折叠后的事实 ----------
        /// <summary>用途/安装含"引导/定位/纠偏"语义 —— 几何标定的用途门</summary>
        public bool IsGuidancePurpose;
        /// <summary>拍照方式含"飞拍/运动"语义</summary>
        public bool IsFly;
        /// <summary>相机随执行机构移动（眼在手上）</summary>
        public bool IsMovingMount;
        /// <summary>下固定/仰视（下相机）</summary>
        public bool IsDownLooking;
        /// <summary>斜拍</summary>
        public bool IsSlant;
        /// <summary>轴跟随含 Z（拍照高度随动 ⇒ 物距会变）</summary>
        public bool FollowsZ;
        /// <summary>是否已填安装方式（未填 ⇒ 不能静默当"固定相机"）</summary>
        public bool HasInstallAnswer;
        /// <summary>是否已填用途（未填 ⇒ 不能静默当"引导定位"）</summary>
        public bool HasPurposeAnswer;

        /// <summary>由档案槽折叠出事实（字符串匹配唯一发生地）。</summary>
        public static SlotFacts From(VisionSlotInfo slot)
        {
            var f = new SlotFacts();
            if (slot == null) return f;

            f.SlotKey = Norm.Trim(slot.SlotKey);
            if (f.SlotKey.Length == 0) f.SlotKey = "Cam_?";
            f.InstallKind = Norm.Trim(slot.InstallKind);
            f.Purpose = Norm.Trim(slot.Purpose);
            f.ShootMode = Norm.Trim(slot.ShootMode);
            f.AxisFollows = Norm.Trim(slot.AxisFollows);
            f.AxisToSurface = Norm.Trim(slot.AxisToSurface);
            f.IsDisabled = slot.IsDisabled;
            f.MethodId = Norm.Trim(slot.CalibrationMethodId);

            f.HasInstallAnswer = f.InstallKind.Length > 0;
            f.HasPurposeAnswer = f.Purpose.Length > 0;

            f.IsGuidancePurpose = Norm.IsGuidancePurpose(f.Purpose) || Norm.IsGuidancePurpose(f.InstallKind);
            f.IsFly = Norm.IsFly(f.ShootMode);
            f.IsMovingMount = Norm.IsMovingMount(f.InstallKind);
            f.IsDownLooking = f.InstallKind.Contains("下固定") || f.InstallKind.Contains("仰视");
            f.IsSlant = Norm.IsSlant(f.AxisToSurface) || Norm.IsSlant(f.InstallKind);
            f.FollowsZ = f.AxisFollows.Contains("Z");
            return f;
        }
    }

    /// <summary>
    /// 工位的**结构化事实**（跨槽方式的判据来源；单槽方式只用 <see cref="Slots"/> 里的一项）。
    /// </summary>
    public sealed class StationFacts
    {
        public string StationCode = "";
        public string TaskType = "";
        public string CameraMount = "";
        public string ShootMode = "";
        public string AngleNeed = "";
        public string ActuatorKind = "";
        public string ToolHeadCount = "";

        /// <summary>工具中心是否与回转轴同心（null = 未答；未答按偏心保守处理）</summary>
        public bool? ConcentricWithRotationAxis;

        /// <summary>启用中的槽（已剔除停用槽）</summary>
        public List<SlotFacts> Slots = new List<SlotFacts>();

        /// <summary>档案里原本有没有相机槽（false = 走单相机字段合成一个伪槽）</summary>
        public bool HasExplicitSlots;

        /// <summary>工位里有没有【启用中的】下固定/仰视槽 —— 上下机映射方式的前提</summary>
        public bool HasDownCamera;
        /// <summary>工位里有没有【启用中的】随动（眼在手上）槽</summary>
        public bool HasMovingCamera;

        /// <summary>
        /// ★★2026-09-17（ST_007）：工位里是否有【会命中「下相机走位式」】的下相机槽
        /// （= 有下固定/仰视槽 **且** 该槽用途过几何标定门）。
        ///
        /// 与 <see cref="HasDownCamera"/> 的区别，以及为什么不能直接用它：
        ///   「下相机像素旋转中心 R_cdown」这个任务依赖**下相机槽的 H_down**，而
        ///   <c>CalibrationPlanEngineV2.DeriveToolTasks</c> 在依赖帧取不到时是
        ///   <c>continue</c>（**静默跳过，连一条 problem 都不报**）。
        ///   所以判据必须与"该帧真的会被派生"对齐：只要下相机槽的用途/安装能命中
        ///   M_DownCameraWalk（IsGuidancePurpose &amp;&amp; IsDownLooking），H_down 就一定在任务列表里。
        ///   若改成"有下相机槽就算"，那么在"有下相机、但用途不是引导类（例如只做外观检测）"的工位上，
        ///   R_cdown 会被**悄悄丢掉**——而那正是"静默少一条任务"这类最难查的形态。
        ///
        /// 注：不额外检查槽上的显式 <see cref="SlotFacts.MethodId"/> 声明 —— 能声明在下固定仰视槽上的
        /// 方式只有 DownCameraWalk 一族（随动专属的上下机映射与本安装方式互斥），故不会误判。
        /// </summary>
        public bool HasDownCameraWalkSlot
        {
            get
            {
                foreach (var sf in Slots)
                {
                    if (sf.IsDownLooking && sf.IsGuidancePurpose) return true;
                }
                return false;
            }
        }

        /// <summary>是否带角度作业（AngleNeed 非空且不含"无角度"语义）</summary>
        public bool NeedAngle;

        /// <summary>角度需求原文里的判据说明（证据链用）</summary>
        public string AngleNeedBasis = "";

        /// <summary>工具头数（"1"/"2"/"多" → 数值）</summary>
        public int ToolCount { get { return Norm.ParseToolHeadCount(ToolHeadCount); } }

        /// <summary>工具中心偏心（未答=false 的保守取向：见 DeriveToolOffsetTasks 注释）</summary>
        public bool Eccentric { get { return ConcentricWithRotationAxis == false; } }

        /// <summary>
        /// 由工位档案折叠出事实。
        /// 无 CameraSlots 的旧单相机档案：用 CameraMount/ShootMode/TaskType **合成一个伪槽 Cam_01**
        /// —— 这样"槽级"与"单相机"走的是同一条判据、同一道用途门（旧代码两条分支判据不同，
        /// 单相机分支漏了用途门 ⇒ 空档案 ST_004/005/006 会凭空派生出一条"固定相机 H"任务）。
        /// </summary>
        public static StationFacts From(StationProfile profile)
        {
            var st = new StationFacts();
            if (profile == null) return st;

            var req = profile.Requirement ?? new StationProfileRequirement();
            st.StationCode = Norm.Trim(profile.StationCode);
            st.TaskType = Norm.Trim(req.TaskType);
            st.CameraMount = Norm.Trim(req.CameraMount);
            st.ShootMode = Norm.Trim(req.ShootMode);
            st.AngleNeed = Norm.Trim(req.AngleNeed);
            st.ActuatorKind = Norm.Trim(req.ActuatorKind);
            st.ToolHeadCount = Norm.Trim(req.ToolHeadCount);
            st.ConcentricWithRotationAxis = req.ConcentricWithRotationAxis;

            st.NeedAngle = st.AngleNeed.Length > 0 && !Norm.HasNoAngle(st.AngleNeed);
            // 与改动前的证据链文案逐字一致（Reason 会显示在任务卡与候选摘要上）。
            st.AngleNeedBasis = st.AngleNeed.Length > 0 ? "AngleNeed=" + st.AngleNeed : "AngleNeed=未答";

            st.HasExplicitSlots = req.CameraSlots != null && req.CameraSlots.Count > 0;
            if (st.HasExplicitSlots)
            {
                foreach (var slot in req.CameraSlots)
                {
                    if (slot == null) continue;
                    if (slot.IsDisabled) continue; // ★停用槽不参与任何推导/提示
                    st.Slots.Add(SlotFacts.From(slot));
                }
            }
            else
            {
                st.Slots.Add(SynthesizeSingleCamera(req));
            }

            foreach (var sf in st.Slots)
            {
                if (sf.IsMovingMount) st.HasMovingCamera = true;
                if (sf.IsDownLooking) st.HasDownCamera = true;
            }
            return st;
        }

        /// <summary>旧单相机档案 → 伪槽（Cam_01）。用途由任务类型映射，**不做默认放行**。</summary>
        private static SlotFacts SynthesizeSingleCamera(StationProfileRequirement req)
        {
            var f = new SlotFacts
            {
                SlotKey = "Cam_01",
                InstallKind = Norm.Trim(req.CameraMount),
                ShootMode = Norm.Trim(req.ShootMode),
                AxisToSurface = Norm.Trim(req.AxisToSurface),
                // 用途：只有"定位抓取"才等价于槽级问卷里的"引导定位"；其余任务类型没有几何标定诉求。
                // （旧单相机分支不判用途 ⇒ 空档案也派生 H；此处补上用途门。）
                Purpose = string.Equals(Norm.Trim(req.TaskType), "定位抓取", StringComparison.Ordinal)
                          ? "引导定位" : "",
            };
            f.HasInstallAnswer = f.InstallKind.Length > 0;
            f.HasPurposeAnswer = f.Purpose.Length > 0;
            f.IsGuidancePurpose = Norm.IsGuidancePurpose(f.Purpose) || Norm.IsGuidancePurpose(f.InstallKind);
            f.IsFly = Norm.IsFly(f.ShootMode);
            f.IsMovingMount = Norm.IsMovingMount(f.InstallKind);
            f.IsDownLooking = f.InstallKind.Contains("下固定") || f.InstallKind.Contains("仰视");
            f.IsSlant = Norm.IsSlant(f.AxisToSurface) || Norm.IsSlant(f.InstallKind);
            f.FollowsZ = false;
            return f;
        }
    }

    /// <summary>判据上下文：一行 Match 能看到的全部事实。</summary>
    public sealed class MethodContext
    {
        public StationFacts Station;
        public SlotFacts Slot;

        /// <summary>本槽已定的相机级方式（工具级行的依赖判据用；槽级行为 null）</summary>
        public CalibrationMethod CameraMethod;
    }

    /// <summary>
    /// 一种标定方式 = 一行数据。字段即"建档条件 → 采集 → 产出 → 消费"的完整描述。
    /// </summary>
    public sealed class CalibrationMethod
    {
        /// <summary>稳定 Id（档案里声明 <see cref="SlotFacts.MethodId"/> 用的就是它；改动 = 破坏存档）</summary>
        public string Id = "";

        /// <summary>任务卡显示名（{Slot} 会被替换成槽键）</summary>
        public string Name = "";

        /// <summary>建档提示用的一句话（人读）</summary>
        public string ShortName = "";

        /// <summary>
        /// 短标签（2~6 字的名词短语）——清单/资产名/步骤条这类"要极短"的位置用。
        /// ★ 有了它，出口就**不必再对 ShortName 做字符串截取**：
        ///   "九点标定（固定相机：像素 ↔ 机械平面映射）" 按第一个「：」截会得到
        ///   "九点标定（固定相机"（括号不闭合）—— 这类残句会直接显示给现场。
        ///   所以本字段不接受括号与冒号（Validate 会拦）。
        /// </summary>
        public string Tag = "";

        public MethodScope Scope = MethodScope.Slot;

        /// <summary>执行顺序（同一次派生内按此排序；越小越先）</summary>
        public int Order;

        // ---------- 产出与采集 ----------
        public CalibrationQuantity Quantity;
        public CalibrationAcquirePath PrimaryPath;
        public CalibrationAcquirePath[] AltPaths = new CalibrationAcquirePath[0];
        public EyeMode Layout = EyeMode.EyeToHand;

        /// <summary>消费口径（null = 本量不直接产生口径：它是被 H 档消费的参数）</summary>
        public ConsumptionKind? Consumption;

        // ---------- 向导装配 ----------
        /// <summary>向导策略键（必须在 <see cref="CalibrationMethodCatalog.KnownStrategies"/> 里）</summary>
        public string WizardStrategy = CalibrationMethodCatalog.StrategyNinePoint;

        /// <summary>第三步 DataTemplate 键（必须在 <see cref="CalibrationMethodCatalog.KnownTemplates"/> 里）</summary>
        public string StepTemplate = CalibrationMethodCatalog.TemplateNinePoint;

        // ---------- 人读描述（四处出口共用同一段话）----------
        /// <summary>产物名（人读）</summary>
        public string Produces = "";
        /// <summary>消费算式（人读；与契约 DescribeFormula 的说法保持一致）</summary>
        public string Formula = "";
        /// <summary>建档条件（人读）</summary>
        public string Condition = "";
        /// <summary>采集怎么做（任务卡的"执行方式"文案）</summary>
        public string HowTo = "";
        /// <summary>现场确认点（默认 Note；NoteOf 非空时以 NoteOf 为准）</summary>
        public string Note = "";

        // ---------- 依赖与判据 ----------
        /// <summary>依赖哪个标定量先完成（e/t 依赖 HandEye 定坐标系；null = 无依赖）</summary>
        public CalibrationQuantity? DependsOn;

        /// <summary>
        /// 工具级方式专用：布局是否**跟随主相机槽**（而不是用本行的 <see cref="Layout"/>）。
        /// e/t 的坐标系来自主相机 → 跟随；下相机像素旋转中心恒 EyeToHand → 不跟随。
        /// 单槽方式忽略本字段（用 <see cref="Layout"/>）。
        /// </summary>
        public bool LayoutFollowsCamera;

        /// <summary>
        /// 工具级方式专用：**每个工具头一条**？（false = 每工位只 1 条，NozzleKey 恒 "1"）
        /// e/t 逐吸嘴各标一条（各吸嘴的偏心独立）；下相机像素旋转中心是相机+轴的固有投影，只 1 条。
        /// </summary>
        public bool PerToolHead;

        /// <summary>
        /// 工具级方式专用：依赖的坐标系取自**下相机槽**的 H（false = 取主相机槽）。
        /// 只有"下相机像素旋转中心"依赖下相机 —— 它必须与同槽的 H_down 共用坐标系。
        /// </summary>
        public bool DependsOnDownCameraSlot;

        /// <summary>首选采集路径随上下文变化（null = 直接用 <see cref="PrimaryPath"/>）</summary>
        public Func<MethodContext, CalibrationAcquirePath> PathOf;

        /// <summary>执行方式文案随上下文变化（null = 直接用 <see cref="HowTo"/>）</summary>
        public Func<MethodContext, string> HowToOf;

        /// <summary>按上下文取首选路径</summary>
        public CalibrationAcquirePath PathFor(MethodContext c)
        {
            return PathOf != null ? PathOf(c) : PrimaryPath;
        }

        /// <summary>按上下文取执行方式文案（并把 {Slot} 替换成实际槽/吸嘴键）</summary>
        public string HowToFor(MethodContext c, string slotToken)
        {
            string t = HowToOf != null ? HowToOf(c) : HowTo;
            if (string.IsNullOrWhiteSpace(t)) t = HowTo;
            return t == null ? "" : t.Replace("{Slot}", slotToken ?? "");
        }

        /// <summary>主线必做？（false = 可选项，任务卡灰显/可跳过）</summary>
        public bool IsRequired = true;

        /// <summary>建档条件（结构化谓词）</summary>
        public Func<MethodContext, bool> Match;

        /// <summary>证据链（把"为什么用它"摊开给现场看；null = 用 Condition）</summary>
        public Func<MethodContext, string> ReasonOf;

        /// <summary>现场确认点（随上下文变化；null = 用 Note）</summary>
        public Func<MethodContext, string> NoteOf;

        /// <summary>把执行方式文案里的 {Slot} 替换成实际槽键</summary>
        public string HowToFor(string slotKey)
        {
            return HowTo == null ? "" : HowTo.Replace("{Slot}", slotKey ?? "");
        }

        /// <summary>把显示名里的 {Slot} 替换成实际槽键</summary>
        public string NameFor(string slotKey)
        {
            return Name == null ? "" : Name.Replace("{Slot}", slotKey ?? "");
        }
    }

    /// <summary>解析结果：命中的方式 + 依据 + 问题（问题非空 ⇒ 必须当作失败处理）。</summary>
    public sealed class MethodResolution
    {
        /// <summary>命中的方式（null = 本槽免几何标定 / 解析不出）</summary>
        public CalibrationMethod Method;

        /// <summary>是否来自槽上的显式声明</summary>
        public bool Declared;

        /// <summary>人读依据（建档提示与任务卡 Reason 共用同一句）</summary>
        public string Basis = "";

        /// <summary>非空 = 有问题（声明未注册 / 声明了但本工位条件不成立）——**不许静默退回**</summary>
        public string Problem = "";

        public bool Ok { get { return Method != null && Problem.Length == 0; } }

        /// <summary>本槽确实不需要几何标定（与"解析失败"区分开）</summary>
        public bool NoGeometryNeeded { get { return Method == null && Problem.Length == 0; } }
    }

    /// <summary>标定方式总表（静态无状态；四个出口共用同一份判据）。</summary>
    public static class CalibrationMethodCatalog
    {
        // ==================== 向导策略 / 模板键注册表 ====================
        // 键必须在这里登记：Validate() 会逐行核对，Step3DataTemplateSelector 逐键取模板。
        // ★ 登记在这里而不做成 enum，是为了"加方式不必改引擎"；代价是键写错会被 Validate 抓住。
        public const string StrategyNinePoint = "NinePoint";
        public const string StrategyPickPlace = "PickPlace";
        public const string StrategyRotation = "HandEyeWithRotation";
        public const string StrategyPixelScale = "PixelScale";
        public const string StrategyCheckerboard = "Checkerboard";
        /// <summary>★上下机映射专用策略（成对采样：同一 Mark 先后被上/下相机观测）</summary>
        public const string StrategyCrossCameraPair = "CrossCameraPair";

        public const string TemplateNinePoint = "NinePoint";
        public const string TemplateRotation = "HandEyeWithRotation";
        public const string TemplateCheckerboard = "Checkerboard";
        public const string TemplatePixelScale = "PixelScale";
        /// <summary>★上下机映射专用第三步模板</summary>
        public const string TemplateCrossCameraPair = "CrossCameraPair";

        public static readonly string[] KnownStrategies =
        {
            StrategyNinePoint, StrategyPickPlace, StrategyRotation,
            StrategyPixelScale, StrategyCheckerboard, StrategyCrossCameraPair,
        };

        public static readonly string[] KnownTemplates =
        {
            TemplateNinePoint, TemplateRotation, TemplateCheckerboard,
            TemplatePixelScale, TemplateCrossCameraPair,
        };

        // ==================== 采集路径 → 向导装配（出口3 的唯一真源）====================
        // 向导（CalibrationWizardViewModel.SelectStrategy / Step3DataTemplateSelector）**按采集路径**查这张表，
        // 不按方式行查。理由：
        //   · 同一个方式可能有几种走法 —— 固定相机 H 的「走位式」与「吸放式」就是两条路；
        //   · 走法决定"向导第三步让操作工做什么动作"（走位式=平移网格采点 / 吸放式=吸→放到命令位→回拍），
        //     而吸放走法**无法由槽事实推断** ⇒ 它不是独立的方式行，而是路径维度上的另一格。
        // 方式行照旧声明 WizardStrategy/StepTemplate（任务卡渲染与审核时读它），
        // **两处由 Validate 强制一致** —— 不是为了冗余，是因为两个出口的查找键不同（路径 vs 方式）。

        /// <summary>一条采集路径的向导装配。</summary>
        public sealed class PathWiring
        {
            public CalibrationAcquirePath Path;
            /// <summary>向导策略键（<see cref="KnownStrategies"/>）</summary>
            public string Strategy = "";
            /// <summary>第三步 DataTemplate 键（<see cref="KnownTemplates"/>）</summary>
            public string Template = "";
            /// <summary>人读名（与 CalibrationCardModels.PathOf 的说法保持一致）</summary>
            public string Name = "";
        }

        private static readonly PathWiring[] PathWirings =
        {
            W(CalibrationAcquirePath.NozzleTruthWalk,          StrategyNinePoint, TemplateNinePoint,     "吸嘴真值走位九点"),
            W(CalibrationAcquirePath.CameraTruthWalk,          StrategyNinePoint, TemplateNinePoint,     "固定相机走位九点"),
            W(CalibrationAcquirePath.PickPlaceReturn,          StrategyPickPlace, TemplateNinePoint,     "吸放回拍"),
            W(CalibrationAcquirePath.FlyPixelScale,            StrategyPixelScale, TemplatePixelScale,   "飞拍当量"),
            W(CalibrationAcquirePath.RotatePickPlace,          StrategyPickPlace, TemplateRotation,      "吸件转轴回拍"),
            W(CalibrationAcquirePath.RotateCameraView,         StrategyRotation,  TemplateRotation,      "相机固定观测转轴"),
            W(CalibrationAcquirePath.AlignTool,                StrategyNinePoint, TemplateNinePoint,     "对针"),
            W(CalibrationAcquirePath.ScaleWalk,                StrategyPixelScale, TemplatePixelScale,   "标距走位"),
            W(CalibrationAcquirePath.DownCameraWalk,           StrategyNinePoint, TemplateNinePoint,     "下相机吸件走位"),
            W(CalibrationAcquirePath.DownCameraPixelRotCenter, StrategyRotation,  TemplateRotation,      "下相机像素旋转中心"),
            W(CalibrationAcquirePath.CrossCameraPairWalk,      StrategyCrossCameraPair, TemplateCrossCameraPair, "上下机成对采样"),
        };

        private static PathWiring W(CalibrationAcquirePath p, string strategy, string template, string name)
        {
            return new PathWiring { Path = p, Strategy = strategy, Template = template, Name = name };
        }

        /// <summary>按采集路径取向导装配（未登记返回 null —— 调用方必须响亮失败，不许静默回退）</summary>
        public static PathWiring WiringOf(CalibrationAcquirePath p)
        {
            foreach (var w in PathWirings)
            {
                if (w.Path == p) return w;
            }
            return null;
        }

        /// <summary>按采集路径取向导策略键</summary>
        public static string StrategyOf(CalibrationAcquirePath p)
        {
            var w = WiringOf(p);
            return w == null ? "" : w.Strategy;
        }

        /// <summary>按采集路径取第三步模板键</summary>
        public static string TemplateOf(CalibrationAcquirePath p)
        {
            var w = WiringOf(p);
            return w == null ? "" : w.Template;
        }

        // ==================== 槽级行（相机级）====================

        /// <summary>
        /// 飞拍 → 像素当量 s。判据：拍照方式含"飞拍/运动"。
        /// （统一取 Contains 语义；此前建档用 ==、计划用 Contains，含"运动中成像"字样的档案会分叉。）
        /// 备选路径 标距走位 ScaleWalk：**精拍** s（已知标距走位测像素）—— 同一产物的另一条采集路，
        /// 由人在标定中心手动建档选定（不由槽事实推断），故只作 AltPath 而不另立方式行。
        /// </summary>
        private static readonly CalibrationMethod M_FlyPixelScale = new CalibrationMethod
        {
            Id = "FlyPixelScale",
            Name = "飞拍纠偏 s（{Slot}）",
            ShortName = "飞拍纠偏：像素当量 + 触发/相位补偿标定（运动中成像，出相对偏差即可）",
            Tag = "飞拍纠偏",
            Scope = MethodScope.Slot,
            Order = 10,
            Quantity = CalibrationQuantity.PixelScale,
            PrimaryPath = CalibrationAcquirePath.FlyPixelScale,
            AltPaths = new[] { CalibrationAcquirePath.ScaleWalk },
            Layout = EyeMode.EyeToHand,
            Consumption = null, // s 是被 H 档消费的参数，本身不构成口径
            WizardStrategy = StrategyPixelScale,
            StepTemplate = TemplatePixelScale,
            Produces = "s（像素当量 mm/px）",
            Formula = "相对偏差 Δ = s·(u − u_ref)（触发/相位补偿在配方层）",
            Condition = "拍照方式含『飞拍/运动』",
            HowTo = "固定相机对运动工件成像，出相对偏差：像素当量 + 触发/相位补偿（节奏在配方）",
            Note = "斜拍 + 飞拍：先解决畸变与打光，纠偏精度受运动抖动限制",
            NoteOf = c => c.Slot.IsSlant
                ? "斜拍 + 飞拍：先解决畸变与打光，纠偏精度受运动抖动限制"
                : null,
            Match = c => c.Slot.IsFly,
            ReasonOf = c => Reason("Purpose=" + Ans(c.Slot.Purpose), "ShootMode=" + Ans(c.Slot.ShootMode),
                                   "飞行成像 ⇒ 像素当量任务"),
        };

        /// <summary>
        /// 眼在手上（随动）→ H 走位式九点。
        /// ★真值 = **取景位**（相机引导点域）：靶固定、动的轴载着相机 ⇒ H(u) 只回答"相机该走到哪"，
        /// 吸嘴尖还差一个「相机↔吸嘴」的固定量 ⇒ **不是** ①档直吸。要么现场改走本行 AltPaths 的
        /// 【吸放式】（真值=放料命令位 ⇒ H 一次吸收掉），要么走 ④/⑤ 档（O/P_photo 出自 e 档）。
        /// </summary>
        private static readonly CalibrationMethod M_EyeInHandNozzleWalk = new CalibrationMethod
        {
            Id = "EyeInHandNozzleWalk",
            Name = "相机手眼 H · 走位式（{Slot}）",
            ShortName = "手眼标定（随动相机 ↔ 执行机构坐标换算；带角度需求再补旋转中心/偏心补偿）",
            Tag = "手眼",
            Scope = MethodScope.Slot,
            Order = 20,
            Quantity = CalibrationQuantity.HandEye,
            PrimaryPath = CalibrationAcquirePath.NozzleTruthWalk,
            Layout = EyeMode.EyeInHand,
            // ★★2026-09-17 二次复核校正：本方式**不**推出吸嘴域。
            //   同日第一次写的「真值=吸嘴尖落点 ⇒ H 已吸收 t ⇒ ①档」与采集实现矛盾：
            //   九点的靶是固定的、动的是载相机的轴，记录的是取景位（见 CalibrationDomainV2 枚举与本行 HowTo）。
            //   ⇒ 消费侧必须另有一个「相机↔吸嘴」项。两条路都登记在这里：
            //     · PrimaryPath 走位式 → ④/⑤ 档（O/P_photo 只能出自 e 档）⇒ 必须同时派生 e；
            //     · AltPaths 吸放式   → ①档直吸（放料命令位即吸嘴落点）⇒ 不需要 e、也不需要单独对针 t。
            //   AltPath 的选择由人定（不由槽事实推断，与 M_FlyPixelScale 的 ScaleWalk 同规）：
            //   相机下若有稳定靶能直接用工具尖/杆端对靶，走位式足够；只能靠吸放工件到命令位作真值时，用吸放式。
            AltPaths = new[] { CalibrationAcquirePath.PickPlaceReturn },
            WizardStrategy = StrategyNinePoint,
            StepTemplate = TemplateNinePoint,
            Produces = "H（像素↔机械平面 2×3；输出域=相机引导点）",
            Formula = "吸点 = H(u) +「相机↔吸嘴」项 —— 吸放式早已吸收进 H；走位式须 ④/⑤ 的 O/P_photo 或对针 t",
            Condition = "用途含引导/定位/纠偏 且 安装=眼在手上",
            HowTo = "眼在手上随动拍固定特征，3×3 网格走位拟合像素↔机械平面（整机共享 1 条）。★每格基准="
                  + "「工件特征成像在画面中央」（取景位）—— 不要去凑「吸嘴对准工件」：相机与吸嘴偏心几十 mm，对准了工件就出画面",
            Note = "斜拍安装：建议九点网格覆盖常用工作区并评估畸变影响",
            NoteOf = c => c.Slot.IsSlant
                ? "斜拍安装：建议九点网格覆盖常用工作区并评估畸变影响"
                : (c.Slot.FollowsZ ? "轴跟随含 Z：拍照高度必须回到标定高度（物距变 ⇒ 当量变）" : null),
            Match = c => c.Slot.IsGuidancePurpose && c.Slot.IsMovingMount,
            ReasonOf = c => Reason("Purpose=" + Ans(c.Slot.Purpose), "Install=" + Ans(c.Slot.InstallKind),
                                   "随动引导 ⇒ EyeInHand 走位式 H（真值=取景位 ⇒ H 在相机引导点域，吸点还要补「相机↔吸嘴」项）"),
        };

        /// <summary>
        /// ★★2026-09-17（ST_007 甲案）：眼在手上（随动）→ H 走【吸放式】。
        ///
        /// ── 为什么必须有这一条独立方式行（而不是复用走位式的 AltPath）────────────────
        /// 走位式与吸放式**都物理可采**，但只在前者上补不出「相机↔吸嘴」那一项：
        ///   · 走位式：靶固定、动的是载相机的轴 ⇒ 九点真值=**取景位** ⇒ H 停在**相机引导点域**，
        ///     吸点还差一个「相机↔吸嘴」固定量。补它要么靠 ④/⑤ 档的 O/P_photo（出自 e 档），
        ///     要么靠对针 t —— 而对针要求「压住工件的位姿」与「能拍到特征的位姿」**同源**。
        ///   · 吸放式：吸件 → 走到网格命令位放落 → 回拍照位成像 ⇒ 真值=**放料命令位**
        ///     ⇒ H 一次吸收掉该固定量 ⇒ ①档直吸 X_obj=H(u)，既不要 e、也不要单独对针。
        ///
        /// ★★ST_007 上「走位式 + 对针」这条路是**结构性做不到**的（2026-09-17 现场定案）：
        ///   该工位工作距离短、吸嘴紧邻相机，**压住特征时相机拍不到**（被吸嘴挡住）；
        ///   抬 Z 腾视野又不可行（相机 `AxisFollows=跟随XYZU`，抬 Z 即抬相机 ⇒ 物距变 ⇒ 当量变，
        ///   被向导的 Z 门禁拦下）；移 XY 腾视野则**破坏了「同源」**——
        ///   压住记录的是机位 A、拍到特征的是机位 B，H(p_tip) 与 R_n 已不在同一个 XY 上，
        ///   两者相减混进了「移开了多少」，不再等于「相机↔吸嘴」偏置。
        ///   ⇒ 三条腾视野的路（抬 Z / 移 XY / 不腾）在 007 上全断，只能改走吸放式。
        ///
        /// ── 选路纪律：只认声明，不做推断 ───────────────────────────────────────────
        /// 本行插在走位式**之后**，且 Match 判据与走位式**完全相同**（用途引导 + 随动安装）⇒
        /// 推断路径下**永远先命中走位式**，本行只能由**槽声明**选中（`ResolveSlot` 声明优先于推断）。
        /// 这是刻意的：两条路物理上都成立，选哪条取决于「现场能否把卡片/工件吸起来放落」，
        /// 属**工程选择**，与 `M_UpCameraViaDownCamera` 同规（不由推断替现场改标定方案）。
        /// ★ 若把本行挪到走位式**之前**，会让所有「随动 + 引导」工位（含 ST_007 既有派生）悄悄换路。
        ///
        /// ── 连带影响（改本行判据时必须一起看）────────────────────────────────────
        /// · `M_ToolRotation.Match` 的 `nozzleDomainH`：吸放式 ⇒ **免 e**（本行已把 t 吸收进 H）；
        /// · `M_ToolOffsetAlign.Match`：吸放式 ⇒ **免对针**（已在采集时吸收，再补即双重补偿）；
        /// · 消费侧 `CalibrationConsumptionContract.InferNozzleDomain` 首条判据认 `PickPlaceReturn`
        ///   ⇒ 判成吸嘴域 ⇒ ①档。**生产端不持档案路径**，故生产侧由槽声明
        ///   `HandEyeInNozzleDomain=true` 承载（`CalibrationArchiveSource.Hydrate` 从档案读入）。
        /// </summary>
        private static readonly CalibrationMethod M_EyeInHandPickPlace = new CalibrationMethod
        {
            Id = "EyeInHandPickPlace",
            Name = "相机手眼 H · 吸放式（{Slot}）",
            ShortName = "手眼标定（随动相机 · 吸放式：吸件放到网格命令位，真值=放料命令位 ⇒ H 一次吸收工具偏距，免旋转中心/免对针）",
            Tag = "吸放手眼",
            Scope = MethodScope.Slot,
            Order = 21,
            Quantity = CalibrationQuantity.HandEye,
            PrimaryPath = CalibrationAcquirePath.PickPlaceReturn,
            AltPaths = new[] { CalibrationAcquirePath.NozzleTruthWalk },
            Layout = EyeMode.EyeInHand,
            Consumption = ConsumptionKind.NozzleDomainDirect,
            WizardStrategy = StrategyPickPlace,
            StepTemplate = TemplateNinePoint,
            Produces = "H（像素↔机械平面 2×3；输出域=吸嘴尖落点）",
            Formula = "X_obj = H(u)，吸点 = H(u)（①档直吸：不叠 O、不用 P_photo、无 U 项）",
            Condition = "用途含引导/定位/纠偏 且 安装=眼在手上；且现场能把卡片/工件吸起并放落到网格点",
            HowTo = "吸放式九点：吸嘴到吸取位吸住卡片/工件 → 移到网格命令位放落（关真空）→ 回【固定拍照位】成像 → "
                  + "提取特征。真值 = 该格的**放料命令位**（工件就在那儿）⇒ 拟合出的 H 直接输出「吸嘴尖该送到的机械位」。"
                  + "★每格基准是『工件被放在哪』，不是『相机看到哪』—— 不要去凑取景位。"
                  + "★放料点用 Base + offset（不走 EyeInHand 反向偏移，否则 index↔像素配对被反向、矩阵符号整体翻转）；"
                  + "★真值取**实际反馈位置**（指令值≠落点）。",
            Note = "吸放式的现场前提：卡片/工件必须能被吸嘴吸起并放落，且 9 个网格点物理可达；"
                 + "全程不抬 Z（拍照位固定姿态 ⇒ 等效固定相机，物距恒定）。"
                 + "与走位式的取舍：本行免掉 e 与对针、精度只剩 H 的拟合残差（有门禁）；代价是 9 次真实吸放。",
            DependsOn = null,
            Match = c => c.Slot.IsGuidancePurpose && c.Slot.IsMovingMount,
            ReasonOf = c => Reason("Purpose=" + Ans(c.Slot.Purpose), "Install=" + Ans(c.Slot.InstallKind),
                                   "随动引导 + 槽声明吸放式 ⇒ 真值=放料命令位（P* 已吸收工具偏距）"
                                   + "⇒ H 在吸嘴域 ⇒ ①档直吸，免 e、免对针（抬起/移开腾视野都会破坏同源，故不走走位式+对针）"),
        };

        /// <summary>下固定（仰视）→ H 走位式，专供相对纠偏（语义与上相机绝对坐标不同）。</summary>
        private static readonly CalibrationMethod M_DownCameraWalk = new CalibrationMethod
        {
            Id = "DownCameraWalk",
            Name = "下相机手眼 H · 吸件走位式（{Slot}）",
            ShortName = "下相机吸件走九点（H_down：吸件悬空仰视，消费端只取相对偏差）",
            Tag = "下相机走位",
            Scope = MethodScope.Slot,
            Order = 30,
            Quantity = CalibrationQuantity.HandEye,
            PrimaryPath = CalibrationAcquirePath.DownCameraWalk,
            AltPaths = new[] { CalibrationAcquirePath.CameraTruthWalk },
            Layout = EyeMode.EyeToHand,
            Consumption = ConsumptionKind.DownCameraRelative,
            WizardStrategy = StrategyNinePoint,
            StepTemplate = TemplateNinePoint,
            Produces = "H_down（像素→机械）+ 像素旋转中心 R_cdown",
            Formula = "δ = H_down(R_img) − H_down(R_cdown)，放置位 = 固定位 − R(ΔU)·δ",
            Condition = "用途含引导/定位/纠偏 且 安装=下固定（仰视）",
            HowTo = "下相机仰视二次对位：吸嘴吸住带 Mark 的延伸杆/工件 → 移到下相机视野内 → 小范围 9 宫格平移走位 → 逐点记「机械位 + 下相机像素」→ 拟合 H_down（pixel→robot）。消费端只取相对偏差 ΔR=R_img−R_cdown，非绝对坐标",
            Note = "下相机仰视：吸住工件悬空成像，标定高度须与作业拍照高度一致（Z 影响成像比例）。配合 DownCameraPixelRotCenter 求像素旋转中心后，消费端做相对偏差二次纠偏",
            Match = c => c.Slot.IsGuidancePurpose && c.Slot.IsDownLooking,
            ReasonOf = c => Reason("Purpose=" + Ans(c.Slot.Purpose), "Install=" + Ans(c.Slot.InstallKind),
                                   "下固定仰视 ⇒ DownCameraWalk（吸件走位九点，真值=吸附工件落点）"),
        };

        /// <summary>固定相机（俯视工件面）→ H 走位式；真值=工具尖/延伸杆端落点 ⇒ H 直接给落点、无需 t。</summary>
        private static readonly CalibrationMethod M_FixedCameraTruthWalk = new CalibrationMethod
        {
            Id = "FixedCameraTruthWalk",
            Name = "相机手眼 H · 固定相机走位式（{Slot}）",
            ShortName = "九点标定（固定相机：像素 ↔ 机械平面映射）",
            Tag = "固定相机九点",
            Scope = MethodScope.Slot,
            Order = 40,
            Quantity = CalibrationQuantity.HandEye,
            PrimaryPath = CalibrationAcquirePath.CameraTruthWalk,
            AltPaths = new[] { CalibrationAcquirePath.PickPlaceReturn },
            Layout = EyeMode.EyeToHand,
            Consumption = ConsumptionKind.FixedCameraRodOffset,
            WizardStrategy = StrategyNinePoint,
            StepTemplate = TemplateNinePoint,
            Produces = "H（像素→机械，真值=工具尖/杆端落点）+ b（杆端→吸嘴）",
            Formula = "X_obj = H(u) + b，吸点 = X_obj（同心吸嘴：b 与 U 无关）",
            Condition = "用途含引导/定位/纠偏 且 安装不是眼在手上、也不是下固定",
            HowTo = "固定相机观测走位：吸嘴装延伸杆（或工件特征）依次走到 3×3 网格 9 个位置 → 逐点记机械反馈位与像素（真值=工具尖落点）",
            Note = "现场确认：相机下若无稳定靶、只能靠吸放工件到命令位作真值 → 备选路径 吸放式 PickPlaceReturn",
            NoteOf = c => c.Slot.IsSlant
                ? "斜拍安装：九点网格覆盖常用工作区并评估畸变影响"
                : "现场确认：相机下若无稳定靶、只能靠吸放工件到命令位作真值 → 备选路径 吸放式 PickPlaceReturn",
            Match = c => c.Slot.IsGuidancePurpose && !c.Slot.IsMovingMount && !c.Slot.IsDownLooking,
            ReasonOf = c => Reason("Purpose=" + Ans(c.Slot.Purpose), "Install=" + Ans(c.Slot.InstallKind),
                                   "固定引导 ⇒ EyeToHand 走位式 H（延伸杆/工具尖真值，H 直接输出落点，无需 t）"),
        };

        /// <summary>
        /// ★上下机映射（跨相机映射）：上相机像素经【下相机】当中介落到机械域。
        ///
        /// 场景（用户 2026-09-17 提出，用于 ST_007 基准相机映射工位）：
        ///   取料盘位置固定 + 摆放位置固定 + 眼在手上相机 + 下相机。
        ///   全程只做两件事：① 下相机像素→机械标定 H_down；② 上相机像素→下相机像素映射跨相机映射。
        ///   生产算式：δ = H_down(CrossCameraMap(u_up)) − H_down(R_cdown)。
        ///   ⇒ 上相机**不做自己的手眼标定**（省掉"随动相机够不到固定靶 / 拿不到吸嘴尖真值"这道难题），
        ///     靠下相机当中间坐标系。
        ///
        /// 声明优先：槽上 MethodId 写 "UpCameraViaDownCamera"。
        ///
        /// ★为什么**只能**靠声明、不做推断兜底（2026-09-17 定）：
        ///   本行 Order=50 排在"眼在手上走位式 H"（Order 20）之后，而后者对任何
        ///   「引导类用途 + 眼在手上」的槽都成立 ⇒ 只要随动槽有引导用途，推断永远先命中 20 那行，
        ///   本行的推断分支**根本不可达**（写了也白写）。反过来把它挪到 20 之前，就会让 ST_007 这类
        ///   「随动 + 有下相机」的**既有工位悄悄换掉派生结果**——那不是"发现更好的方式"，
        ///   是替现场改标定方案。所以：**跨相机映射是工程选择，只认声明**。
        ///   现场怎么知道可以声明它 ⇒ 由 Hints() 提示（且提示不改判定）。
        ///   Match 在本行因此不是"猜哪种方式"，而是**校验这条声明能不能成立**。
        /// </summary>
        private static readonly CalibrationMethod M_UpCameraViaDownCamera = new CalibrationMethod
        {
            Id = "UpCameraViaDownCamera",
            Name = "上下机映射（{Slot} → 下相机）",
            ShortName = "上下机映射：上相机像素经跨相机映射落到下相机，再由下相机 H 换算到机械（上相机不做自己的手眼标定）",
            Tag = "上下机映射",
            Scope = MethodScope.Slot,
            Order = 50,
            Quantity = CalibrationQuantity.CrossCameraMap,
            PrimaryPath = CalibrationAcquirePath.CrossCameraPairWalk,
            AltPaths = new CalibrationAcquirePath[0],
            Layout = EyeMode.EyeInHand,
            Consumption = ConsumptionKind.UpCameraViaDownCamera,
            WizardStrategy = StrategyCrossCameraPair,
            StepTemplate = TemplateCrossCameraPair,
            Produces = "跨相机映射（上相机像素 ↔ 下相机像素 2×3 仿射）",
            Formula = "δ = H_down(CrossCameraMap(u_up)) − H_down(R_cdown)，其后与⑥下相机相对纠偏同",
            Condition = "槽声明 UpCameraViaDownCamera；且 安装=眼在手上、工位有【启用的】下固定（仰视）相机槽",
            HowTo = "成对采样：同一 Mark（吸附在吸嘴上的延伸杆/工件）先在上相机拍照位拍 u_up，"
                  + "机械手再走到下相机基准位拍 u_down → 记一对 (u_up, u_down) → 换点位重复 3~9 对 → 拟合跨相机映射。"
                  + "需同时记录：机械手上相机拍照位、盲取位、下相机基准位。",
            Note = "① 上相机拍照位与下相机基准位必须各只有一个（跨相机映射只在采样位姿处成立）；"
                 + "② 下相机 H_down 与其像素旋转中心 R_cdown 必须已标定（本方式的机械域一侧全靠它）；"
                 + "③ 两台相机的物距须与作业时一致 —— 否则当量缩放使跨相机映射成为乘性偏差（过纠）。",
            DependsOn = CalibrationQuantity.HandEye, // 依赖下相机槽的 H_down
            IsRequired = true,
            Match = c =>
            {
                // ★只认声明：见本行上方注释。Match 的职责 = 这条声明能不能成立。
                if (c.Slot.MethodId.Length == 0) return false;  // 没声明 ⇒ 不猜（否则会改既有工位的派生）
                if (!c.Slot.IsMovingMount) return false;         //跨相机映射的中介语义只对随动相机成立
                if (!c.Station.HasDownCamera) return false;      // 没有下相机就没有中介
                return true;
            },
            ReasonOf = c => Reason(
                "Install=" + Ans(c.Slot.InstallKind),
                "工位下相机=" + (c.Station.HasDownCamera ? "有" : "无"),
                "槽显式声明「上下机映射」⇒ 上相机不做自己的手眼标定，经下相机中转"),
        };

        /// <summary>
        /// 槽级行。前 4 行按 Order 顺序做**推断**（飞拍 → 眼在手上 → 下相机 → 固定相机）；
        /// 末行（上下机映射）**不参与推断**，只在槽上有声明时命中（见该行注释）。
        /// </summary>
        private static readonly CalibrationMethod[] SlotRows =
        {
            M_FlyPixelScale,
            M_EyeInHandNozzleWalk,
            M_EyeInHandPickPlace,
            M_DownCameraWalk,
            M_FixedCameraTruthWalk,
            M_UpCameraViaDownCamera,
        };

        // ==================== 工具级行 ====================

        /// <summary>
        /// 工具回转 e（回转中心 + 偏心 + 基准角 U0）。
        /// 判据：带角度作业 且（工具偏心 或 多工具头 或 固定相机走位式引导 或 随动相机引导）。
        /// ★随动（EIH）引导同列：④/⑤ 档的 O 与 P_photo 只能由本段产出，缺它生产端口径闸拒绝开工。
        /// ★固定相机引导即使"同心+单吸嘴"也要做：运行时姿态补偿需要基准角 U0 与回转中心 O，
        ///   且"同心"只是档案假设，须由旋转采样实证（实测 e≈0 属正常）。
        /// </summary>
        private static readonly CalibrationMethod M_ToolRotation = new CalibrationMethod
        {
            Id = "ToolRotation",
            Name = "回转中心标定 e（工具头 {Slot}）",
            ShortName = "旋转中心标定 e（回转中心 O + 偏心 e + 基准角 U0）",
            Tag = "回转中心",
            Scope = MethodScope.Tool,
            Order = 110,
            Quantity = CalibrationQuantity.ToolRotation,
            PrimaryPath = CalibrationAcquirePath.RotateCameraView,
            AltPaths = new[] { CalibrationAcquirePath.RotatePickPlace },
            Layout = EyeMode.EyeToHand,
            Consumption = null, // e 是被 ⑤ 档消费的参数
            WizardStrategy = StrategyRotation,
            StepTemplate = TemplateRotation,
            Produces = "e（U0x,U0y,EccX,EccY,U0）",
            Formula = "⑤ 档消费：吸点 = X_obj − R(U_go − U0)·e",
            // ★2026-09-17：文案同步 Match（补"随动相机引导"一档）。
            //   原写「主相机=固定相机走位式」⇒ 随动相机（EIH）工位读到的是"我不需要本段"，
            //   而 ④/⑤ 档的 O 与 P_photo 恰恰只能从本段产出 ⇒ 解释错 = 替漏标背书。
            Condition = "带角度作业 且（工具偏心 或 多工具头 或 固定相机走位式引导 或 随动相机【非吸嘴域】引导）",
            HowTo = "绕回转轴多角度采样圆拟合：求回转中心投影 + 工具中心偏置 e + 基准角 U0（角度补偿用）",
            Note = "工具中心偏离回转轴线：按角度对位作业必须补偿工具中心偏置 e",
            DependsOn = CalibrationQuantity.HandEye,
            LayoutFollowsCamera = true,
            PerToolHead = true,
            // 路径随主相机 H 的【布局】与采集语义：
            //   ★ EIH（随动相机）→ 吸件转轴回拍：相机与吸嘴同体、转 U 时一起转，
            //     卡片若离开吸嘴就相对相机不动 ⇒ 图像上画不出圆 ⇒ RotateCameraView 物理上做不到。
            //   · 吸放式主相机（PickPlaceReturn）→ 吸件转轴再放料回拍。
            //   · 固定相机走位式 → 直接转轴由相机观测轨迹画圆（无放落）。
            // ★★2026-09-17（ST_007）修正：原判据只看 `PrimaryPath == PickPlaceReturn`，
            //   于是**随动相机**（PrimaryPath = NozzleTruthWalk）会拿到 RotateCameraView ——
            //   一套现场物理上执行不了的采样流程（向导照样装配出来，等到转轴时才发现画不出圆）。
            //   两条目标路径都已在 PathWirings 登记，向导侧也都有实现（PickPlaceCalibrationStrategy）。
            // ★ 本段只决定**怎么采**（在哪转、看不看得见圆），与"要不要派生本段"无关；
            //   后者见 Match 的域判据（吸放式免、走位式必做）。
            PathOf = c => c.CameraMethod == null
                ? CalibrationAcquirePath.RotateCameraView
                : c.CameraMethod.Layout == EyeMode.EyeInHand
                    ? CalibrationAcquirePath.RotatePickPlace
                    : c.CameraMethod.PrimaryPath == CalibrationAcquirePath.PickPlaceReturn
                        ? CalibrationAcquirePath.RotatePickPlace
                        : CalibrationAcquirePath.RotateCameraView,
            Match = c =>
            {
                if (!c.Station.NeedAngle) return false;
                if (c.CameraMethod == null) return false; // 无相机 H ⇒ e 无坐标系基准
                bool fixedGuidance = c.CameraMethod.PrimaryPath == CalibrationAcquirePath.CameraTruthWalk
                                     && c.CameraMethod.Layout == EyeMode.EyeToHand;
                // ★★2026-09-17 判据（经用户两轮复核后定）：本段该不该派生，只看
                //   ——「这个槽的 H 落在哪个【域】」，而域由 H 的采集路径直接定义：
                //     · PickPlaceReturn / RotatePickPlace（吸放式）—— 真值=放料命令位（**吸收 t**）
                //       ⇒ H 已在吸嘴域 ⇒ ①档直吸 X_obj = H(u)（不叠 O、不用 P_photo、无 U 项）
                //       ⇒ 本段（e）真的不需要。
                //     · 其余全部路径（**含** NozzleTruthWalk / DownCameraWalk / CameraTruthWalk）——
                //       靶固定、动的是**载相机的轴**，记录的是**取景位** ⇒ H 停在**相机引导点域**
                //       ⇒ 吸点还要补「相机↔吸嘴」那一项，而它只能由本段与对针产出
                //       （O/P_photo ← CameraCalibrationBundle.RotationCenterProfile：
                //         RotationCenterWx/Wy ← e 档 ToolCenterWx/Wy；PhotoBaseX/Y ← e 档 BasePosX/Y）
                //       ⇒ **必须派生**；否则 NeedO（= !IsNozzleDomainH && IsEyeInHand）成立而档案里没有 e
                //         ⇒ 口径闸「EIH 无 O」条 fail-closed 拒绝开工。
                //   ⚠⚠ 本行同日**前一版**写成 `Layout == EyeInHand` 无条件派生（把"相机装在哪"当判据），
                //     而**中间一版**又依「NozzleTruthWalk 真值=吸嘴尖落点 ⇒ H 吸收 t」把走位式 EIH
                //     **排除**在派生之外 —— 两版都错，且第二版更危险。
                //     那句"H 吸收 t"是 CalibrationDomainV2 枚举注释里的**错话**，与采集实现相反
                //     （走位式记录的是被移动那两根轴的反馈位=取景位；吸收工具偏距只属吸放式）。
                //     后果：ST_007（随动 + 同心单吸嘴 + 有角度）的 e 被误删 ⇒ `e = O − H(p_tip)` 里的
                //     p_tip 无从产出 ⇒ ④/⑤ 档只能降级。留痕见交付文档 §9（已作废）/ §10。
                //   ⇒ 本节判据与消费侧同源：见 CalibrationConsumptionContract 的
                //     ResolveConsumptionGate / ObjectBase（①档 = H(u)；④/⑤ = P_photo + O − H(u)）。
                bool nozzleDomainH = c.CameraMethod.PrimaryPath == CalibrationAcquirePath.PickPlaceReturn;
                bool eihNeedsO = c.CameraMethod.Layout == EyeMode.EyeInHand && !nozzleDomainH;
                return c.Station.Eccentric || c.Station.ToolCount >= 2 || fixedGuidance || eihNeedsO;
            },
            ReasonOf = c =>
            {
                bool fixedGuidance = c.CameraMethod != null
                                     && c.CameraMethod.PrimaryPath == CalibrationAcquirePath.CameraTruthWalk
                                     && c.CameraMethod.Layout == EyeMode.EyeToHand;
                // 与 Match 同源：走位式（含 NozzleTruthWalk）的 H 在相机引导点域 ⇒ 需要 O/P_photo
                bool eihNeedsO = c.CameraMethod != null && c.CameraMethod.Layout == EyeMode.EyeInHand
                                 && c.CameraMethod.PrimaryPath != CalibrationAcquirePath.RotatePickPlace
                                 && c.CameraMethod.PrimaryPath != CalibrationAcquirePath.PickPlaceReturn;
                return Reason(
                    c.Station.AngleNeedBasis,
                    "ConcentricWithRotationAxis=" + (c.Station.Eccentric ? "false(偏心)"
                        : c.Station.ConcentricWithRotationAxis == true ? "true(同心)" : "未答(按偏心保守)"),
                    "ToolHeadCount=" + Ans(c.Station.ToolHeadCount),
                    (c.Station.Eccentric ? "角度作业 且工具中心偏心"
                        : c.Station.ToolCount >= 2 ? "角度作业 且多工具头"
                        : fixedGuidance ? "角度作业 且固定相机引导（同心亦标：实证偏心 + 产出基准角 U0/回转中心 O）"
                        : eihNeedsO ? "角度作业 且随动相机引导、且该槽 H 不在吸嘴域（机械域 H ⇒ ④/⑤ 档的旋转中心 O 与拍照基准位 P_photo 只能出自本档）"
                        : ""),
                    "⇒ 每工具头 1 条 e；依赖相机 H(" + SlotOf(c) + ") 提供坐标系");
            },
            NoteOf = c => c.Station.Eccentric
                ? "工具中心偏离回转轴线：按角度对位作业必须补偿工具中心偏置 e"
                : (c.Station.ToolCount >= 2
                    ? "多工具头工位：各工具头相对回转中心的偏置独立，逐工具头标定"
                    : (c.CameraMethod != null && c.CameraMethod.Layout == EyeMode.EyeInHand
                        ? "随动相机（EIH）工位：只有该槽 H 尚未进吸嘴域（走位式：靶固定、动的是载相机的轴，"
                          + "记录的是取景位）才需要本段（④/⑤ 档的 O 与 P_photo 都出自本档）；"
                          + "若 H 走**吸放式**（PickPlaceReturn，真值=放料命令位、已吸收 t）"
                          + "⇒ ①档直吸 X_obj=H(u)，本段不派生。采样走「吸件转轴回拍」（相机随吸嘴转，看不见卡片画圆）"
                        : "档案标同心单吸嘴但仍需本段：① 实证偏心 e（同心是假设）② 产出 U0/O 供运行时姿态归正")),
        };

        /// <summary>
        /// 工具中心偏置（对针 t）：★定义原文见 CameraCalibrationBundle「对针偏距 t（**工具尖相对相机引导点**
        /// 的偏距）」。两种量法（同名两义、符号相反）：EyeToHandImage（δ=M_tool−H(A′)，消费 +t）/ 
        /// EyeInHandIndirect（TCO=H(u)−R_n，消费 −t，仅 EIH）。
        ///
        /// ★★判据（2026-09-17 修正）：**"H 有没有吸收 t"** —— 只有吸放式（PickPlaceReturn/RotatePickPlace）
        /// 的真值是吸嘴落点、把 t 吸收掉了；其余路径的 H 都停在**相机引导点域** ⇒ 必须有对针这一步。
        ///
        /// ★★2026-09-17 二次复核（回答"007 的 Cam_A 为什么没有 t"）：**这一步被消费的产物按布局不同**——
        ///   · ETH（固定相机 + 杆端域 ③档）：产物就是 **b**，消费式 吸点 = H(u) + b；判据见
        ///     `CameraCalibrationBundle.HasEthToolOffset`（**只认 EyeToHandImage** 这一种量法）。
        ///   · EIH（随动相机 ④/⑤档）：消费式 `X_obj = P_photo + O − H(u)`、`P_go = X_obj − R(U−U0)·e` 里
        ///     **没有 t 的位置**；被消费的是 **e**（`ToolOffsetPureW = O − H(p_tip)`，`CalibrationGeometry.SolveNozzleEcc`）。
        ///     而 e **只能由一次物理对针产出**（要有 p_tip）⇒ EIH 也必须派生本行 —— 它的存在理由**不是"产 TCO"**，
        ///     而是**"提供那次对针动作"**（见 `CalibrationWizardViewModel:6751-6816`：结算同时产 e 与 TCO）。
        ///   · TCO（`ToolOffsetWx/Wy`，EyeInHandIndirect 语义）**本产品已证伪**：含绝对坐标 R_n，
        ///     仿真最大误差 451mm（`CalibrationWizardViewModel:6754` 原文"已证伪，仅保留兼容与对照"）⇒
        ///     不要为了它去派生/收紧本行。
        /// ⇒ 结论：EIH 与 ETH **都**派生本行；差别只在 `HowToOf` 的量法（间接对针 / 图像对针）与产物标注。
        /// ⚠ 原判据 = `Station.Eccentric`（同心 ⇒ 直接跳过本行）：它把"要不要补工具偏距"错误地绑在**同心性**上。
        ///   同心性（`ConcentricWithRotationAxis`）讲的是「吸嘴尖 vs **回转轴**」，决定的是 e / O；
        ///   而"要不要补"取决于 **H 的域**（吸放式已吸收 vs 相机/杆端域未吸收）。两个后果不同、都不许混：
        ///     · ETH 同心工位被跳过 ⇒ ③档没有 b ⇒ 落点整片偏一个 |b|（本工位量级 ≈132mm）；
        ///     · EIH 同心工位被跳过 ⇒ 拿不到 p_tip ⇒ e 无从产出（⑤档只能降级，或硬拦）。
        ///   ★别把"同心"当免罪牌：**同心 ≠ H 已在吸嘴域**。相机↔吸嘴那个安装偏置在 EIH 消费式里
        ///   会自动相消（H 的平移列已含它），但在 ETH ③档里它就是必须补的 b —— 同一个词，两处作用不同。
        /// </summary>
        private static readonly CalibrationMethod M_ToolOffsetAlign = new CalibrationMethod
        {
            Id = "ToolOffsetAlign",
            Name = "工具对针（工具头 {Slot}）",
            ShortName = "工具对针（ETH 产 b / EIH 产 e；TCO 已证伪仅留档）",
            Tag = "对针",
            Scope = MethodScope.Tool,
            Order = 120,
            Quantity = CalibrationQuantity.ToolOffset,
            PrimaryPath = CalibrationAcquirePath.AlignTool,
            AltPaths = new CalibrationAcquirePath[0],
            // ★实际布局由 LayoutFollowsCamera 从主相机 H 取（HowToOf 里已有 EIH 分支 = 间接对针）；
            //   本行字面 Layout 只是默认值。
            Layout = EyeMode.EyeToHand,
            Consumption = null, // 数值不直接进 consumption 的分型枚举：ETH 经 HasEthToolOffset→b 被③档消费；EIH 经 e 被④/⑤档消费
            WizardStrategy = StrategyNinePoint,
            StepTemplate = TemplateNinePoint,
            Produces = "对针 → ETH 产 b（③档 |b| = 杆端→吸嘴尖）；EIH 产 e（④/⑤档；TCO 已证伪、仅留档）",
            Formula = "ETH③：吸点 = H(u) + b（b 取本对针直量）；EIH④/⑤：e = O − H(p_tip)，吸点 = X_obj − R(U−U0)·e（X_obj = P_photo + O − H(u)）",
            Condition = "H 未吸收工具偏距（H 停在相机引导点域）或 工具偏心 或 多工具头",
            HowTo = "间接对针（EIH）：JOG 工具尖压住工件特征记机械位 R_n → 抬 Z 拍同一特征并点选 p_tip → "
                  + "结算 e = O − H(p_tip)（★TCO = H(u) − R_n 含绝对坐标 R_n、已证伪，仅留档对照）；"
                  + "固定相机（ETH）用图像对针：步进移动 → 锁定 M_tool → 图像点选实际落点 → 结算 δ 作 b",
            Note = "角度作业 + 偏心：先完成回转中心标定(e档产 O)再做本对针（e 的参考角与基准角 U0 语义一致）",
            DependsOn = CalibrationQuantity.HandEye,
            LayoutFollowsCamera = true,
            PerToolHead = true,
            // 同一条对针、两种量法：眼在手上看不到工具尖 ⇒ 间接对针（产 p_tip → e）；固定相机能观测工具尖落点 ⇒ 图像对针（产 b）。
            HowToOf = c => c.CameraMethod != null && c.CameraMethod.Layout == EyeMode.EyeInHand
                ? "间接对针：JOG 工具尖压住工件特征记机械位 R_n → 抬 Z 拍同一特征并点选 p_tip → 结算 e = O − H(p_tip)"
                : "图像对针：步进移动 → 锁定 M_tool → 图像点选实际落点 → 结算 δ 作 b（③档 吸点 = H(u)+b）",
            Match = c =>
            {
                if (c.CameraMethod == null) return false;   // 无相机 H ⇒ 对针没有参照点
                // 吸放式（真值=放料命令位）⇒ 工具偏距已在采集时吸收 ⇒ 不必再对针（再补就是双重补偿）。
                // ★注：CameraMethod 只可能是**相机级方式**（ResolveToolMethods 取 Quantity==HandEye 的第一行，
                //   其 PrimaryPath ∈ {NozzleTruthWalk, DownCameraWalk, CameraTruthWalk}），所以这里**不要**
                //   再列 RotatePickPlace —— 那是 e 档的 AltPath，永远进不来（恒假判据比没判据更危险）。
                if (c.CameraMethod.PrimaryPath != CalibrationAcquirePath.PickPlaceReturn) return true;
                return c.Station.Eccentric || c.Station.ToolCount >= 2; // 多工具头/偏心：各头独立偏置
            },
            ReasonOf = c => Reason(
                c.CameraMethod != null
                && c.CameraMethod.PrimaryPath == CalibrationAcquirePath.PickPlaceReturn
                    ? "H 走吸放式（真值=放料命令位）⇒ 工具偏距已在采集时吸收；仅多工具头/偏心需逐头补"
                    : "H 停在相机引导点域 ⇒ 必须做一次物理对针：ETH 由它产 b（③档 吸点=H(u)+b）；"
                      + "EIH 由它产 p_tip ⇒ e = O − H(p_tip)（④/⑤档）",
                "ConcentricWithRotationAxis=" + (c.Station.Eccentric ? "false(偏心)"
                    : c.Station.ConcentricWithRotationAxis == true ? "true(同心)" : "未答"),
                "★同心只说明吸嘴尖在回转轴上（⇒ e 可≈0），**不等于** H 已在吸嘴域："
                    + "ETH ③档的 b 照样要补（本工位 ≈132mm）；EIH ④/⑤ 的 O/e 照样要标",
                "布局=" + (c.CameraMethod != null && c.CameraMethod.Layout == EyeMode.EyeInHand
                    ? "EyeInHand(间接对针:压特征+抬Z拍同点)" : "EyeToHand(图像对针)")),
            NoteOf = c => c.Station.NeedAngle
                ? "角度作业 + 偏心：先完成回转中心标定(e)再做本对针（TCO 参考角与基准角 U0 语义一致）"
                : "无角度作业偏心工位：九点 H + 本对针即可，TCO 作平移补偿，无需回转中心标定",
        };

        /// <summary>
        /// 下相机像素平面旋转中心（像素域，非机械域 e）。
        /// 判据：带角度作业 且 主相机是下相机走位式。产出像素圆心，供 ⑥ 算 ΔR = R_img − R_cdown。
        /// </summary>
        private static readonly CalibrationMethod M_DownCameraPixelRotCenter = new CalibrationMethod
        {
            Id = "DownCameraPixelRotCenter",
            Name = "下相机像素旋转中心（{Slot}）",
            ShortName = "下相机像素旋转中心 R_cdown（像素域拟合，非机械域 e）",
            Tag = "像素旋转中心",
            Scope = MethodScope.Tool,
            Order = 130,
            Quantity = CalibrationQuantity.ToolRotation,
            PrimaryPath = CalibrationAcquirePath.DownCameraPixelRotCenter,
            AltPaths = new CalibrationAcquirePath[0],
            Layout = EyeMode.EyeToHand,
            Consumption = ConsumptionKind.DownCameraRelative,
            WizardStrategy = StrategyRotation,
            StepTemplate = TemplateRotation,
            Produces = "R_cdown（像素圆心 Col/Row）",
            Formula = "⑥/⑦ 档消费：δ = H_down(R_img) − H_down(R_cdown)",
            // ★2026-09-17：文案同步 Match（原写"主相机=下相机走位式"= 旧判据）。
            //   文案与判据不一致时，现场读到的是"为什么需要"的解释 ⇒ 解释错 = 替错误背书。
            Condition = "带角度作业 且 工位有会走【下相机走位式】的槽（下固定仰视 · 用途过几何标定门）",
            HowTo = "机械手在下相机中心附近不动 → U 轴转 3 个角度（默认 -30°/0°/+30°）拍照 → 3 个 Mark 像素点在像素平面拟合圆 → 圆心 = 吸嘴旋转中心在下相机图像里的像素位置 P_rot_down(R,C)。消费端 ΔR=R_img−R_cdown 二次纠偏",
            Note = "依赖同槽下相机 H：像素旋转中心与其共用坐标系。拟合在像素平面完成，勿与上相机机械域旋转中心 e 混用",
            DependsOn = CalibrationQuantity.HandEye,
            PerToolHead = false,               // 相机+轴的固有投影，与吸嘴号无关
            DependsOnDownCameraSlot = true,    // 坐标系必须来自下相机槽的 H_down
            // ★★2026-09-17（ST_007）修正判据：从「主相机就是下相机」改为「工位里有会走下相机走位式的槽」。
            //   旧判据只在**单相机仰视工位**成立 ⇒ **复合工位（上相机 + 下相机）永远不派生本任务**，
            //   而复合工位的放置段恰恰靠 R_cdown 做相对纠偏：
            //     缺它 ⇒ CalibrationArchiveSource.ComputeDownAxis 记 NotCovered ⇒
            //     δ 退化成 H_down(R_img) − 拍照机位（工件刚性吸在吸嘴上 ⇒ 数学上恒 ≈0）
            //     ⇒ **位置纠偏看起来执行了、实际一步没动**。
            //   实测铁证：ST_002 的 Cam_C 档案 PrimaryPath=9(DownCameraPixelRotCenter) ——
            //   现场早已在标定中心手工建过这一条，而派生层从来没产出过它（出1/出2 与现场需求不一致）。
            //   为何用 HasDownCameraWalkSlot 而非 HasDownCamera：DeriveToolTasks 在取不到依赖帧时
            //   是**静默 continue**，判据必须与「依赖帧真的会被派生」对齐（见该属性注释）。
            Match = c => c.Station.NeedAngle && c.Station.HasDownCameraWalkSlot,
            ReasonOf = c => Reason(
                c.Station.AngleNeedBasis,
                "工位有下固定（仰视）槽、且该槽会走 DownCameraWalk（HasDownCameraWalkSlot=true）",
                "带角度作业 ⇒ 下相机像素旋转中心（像素域拟合，区别于机械域 e）；"
                + "无论下相机是不是主相机，相对纠偏都要它当 δ 的基准"),
        };

        /// <summary>工具级行（按 Order 执行：e → t → 下相机像素旋转中心）</summary>
        private static readonly CalibrationMethod[] ToolRows =
        {
            M_ToolRotation,
            M_ToolOffsetAlign,
            M_DownCameraPixelRotCenter,
        };

        /// <summary>全表（审核/自检/文档用）</summary>
        public static IEnumerable<CalibrationMethod> All
        {
            get
            {
                foreach (var m in SlotRows) yield return m;
                foreach (var m in ToolRows) yield return m;
            }
        }

        /// <summary>按 Id 取行（档案声明用）</summary>
        public static CalibrationMethod ById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            foreach (var m in All)
            {
                if (string.Equals(m.Id, id.Trim(), StringComparison.Ordinal)) return m;
            }
            return null;
        }

        /// <summary>
        /// ★2026-09-17 新增：**槽级**方式行（供工位问卷「标定方式」下拉渲染候选）。
        ///
        /// 存在的理由：`VisionSlotInfo.CalibrationMethodId` 此前是死字段——契约有、本表读、
        /// 但**没有任何 UI 能写**（全仓 grep 零写入点）⇒ 槽永远"没声明"⇒ 永远走推断。
        /// 现场后果：007 Cam_A（眼在手上 + 引导定位）被推断成走位式，用户在界面上
        /// 无论怎么改都进不了吸放式（该工位的正确方式是 `EyeInHandPickPlace`）。
        ///
        /// ★只返回槽级行（`Scope == Slot`）——工具级行（e/t/R_cdown）不能挂到相机槽上，
        ///   `ResolveSlot` 会明确拒绝（"是工具级方式，不能挂在相机槽上"），这里提前过滤掉，
        ///   免得下拉里出现一个选了就报错的选项。
        /// ★返回内部数组本身（只读契约：调用方不得改）——不额外的每次分配副本。
        /// </summary>
        public static IEnumerable<CalibrationMethod> SlotMethodOptions()
        {
            foreach (var m in SlotRows)
            {
                if (m != null && m.Scope == MethodScope.Slot) yield return m;
            }
        }

        // ==================== 解析入口（四个出口都只调这两个）====================

        /// <summary>
        /// 解析一个相机槽该用哪种标定方式（建档提示与任务派生**共用**）。
        /// 优先级：槽上显式声明 &gt; 表内条件。
        /// 声明写了但不可用（Id 不存在 / 本工位条件不成立）⇒ 返回带 Problem 的结果（**不静默退回**）。
        /// </summary>
        public static MethodResolution ResolveSlot(StationFacts st, SlotFacts sf)
        {
            var r = new MethodResolution();
            if (st == null || sf == null) { r.Problem = "输入为空"; return r; }

            if (sf.MethodId != null && sf.MethodId.Trim().Length > 0)
            {
                var declared = ById(sf.MethodId);
                if (declared == null)
                {
                    r.Problem = "槽 " + sf.SlotKey + " 声明了不存在的标定方式 Id=「" + sf.MethodId
                                + "」（不在总表里）⇒ 不退回推断。请改成总表里的 Id 或留空。";
                    return r;
                }
                if (declared.Scope != MethodScope.Slot)
                {
                    r.Problem = "槽 " + sf.SlotKey + " 声明的方式「" + declared.Id
                                + "」是工具级方式，不能挂在相机槽上。";
                    return r;
                }
                var ctxD = new MethodContext { Station = st, Slot = sf };
                if (!declared.Match(ctxD))
                {
                    r.Problem = "槽 " + sf.SlotKey + " 声明的方式「" + declared.Id
                                + "」在本工位条件不成立（" + declared.Condition + "）"
                                + "⇒ 不退回推断。请补条件或改声明。";
                    return r;
                }
                r.Method = declared;
                r.Declared = true;
                r.Basis = "槽声明「" + declared.ShortName + "」；" + BuildEvidence(declared, ctxD);
                return r;
            }

            foreach (var m in SlotRows)
            {
                var ctx = new MethodContext { Station = st, Slot = sf };
                if (m.Match != null && m.Match(ctx))
                {
                    r.Method = m;
                    r.Basis = BuildEvidence(m, ctx);
                    return r;
                }
            }

            // 没命中任何方式：区分"确实不需要"与"信息不够"
            if (!sf.IsGuidancePurpose && !sf.IsFly)
            {
                r.Basis = "用途=" + Ans(sf.Purpose) + "、拍照=" + Ans(sf.ShootMode)
                        + " ⇒ 既非引导/定位/纠偏、也非飞拍 ⇒ 免几何标定";
                return r;
            }
            if (!sf.HasInstallAnswer)
            {
                r.Basis = "用途=引导类，但安装方式未答 ⇒ 定不了采集语义（眼在手上/固定/下固定走法完全不同）";
                return r;
            }
            r.Basis = "安装=" + Ans(sf.InstallKind) + " 未命中任何已登记方式";
            return r;
        }

        /// <summary>
        /// 解析一个工位该用哪些工具级方式（按 Order 排序；依赖相机级结果提供坐标系）。
        /// </summary>
        public static List<CalibrationMethod> ResolveToolMethods(StationFacts st)
        {
            CalibrationMethod ignored;
            return ResolveToolMethods(st, out ignored);
        }

        /// <summary>
        /// 同上，并把**主相机的【方式行】**交出来（工具级行的判据要读它的 PrimaryPath/Layout）。
        /// 注意返回的是"方式行"而不是"任务规格"：判据只认方式，规格是渲染结果。
        /// </summary>
        public static List<CalibrationMethod> ResolveToolMethods(StationFacts st, out CalibrationMethod primaryCameraMethod)
        {
            primaryCameraMethod = null;
            var list = new List<CalibrationMethod>();
            if (st == null) return list;

            // 主相机 H = 本工位第一个解析出相机级方式的槽（与旧引擎 FindFirstHandEye 同序）
            CalibrationMethod primary = null;
            foreach (var sf in st.Slots)
            {
                var res = ResolveSlot(st, sf);
                if (res.Method != null && res.Method.Quantity == CalibrationQuantity.HandEye)
                {
                    primary = res.Method;
                    break;
                }
            }
            primaryCameraMethod = primary;
            if (primary == null) return list; // 无相机 H ⇒ 工具级无坐标系基准

            var ctx = new MethodContext { Station = st, Slot = null, CameraMethod = primary };
            foreach (var m in ToolRows)
            {
                if (m.Match != null && m.Match(ctx)) list.Add(m);
            }
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
            return list;
        }

        /// <summary>主相机槽键（工具级行的 DisplayName 里显示；null = 无）</summary>
        public static string PrimaryCameraSlotOf(StationFacts st)
        {
            if (st == null) return null;
            foreach (var sf in st.Slots)
            {
                var res = ResolveSlot(st, sf);
                if (res.Method != null && res.Method.Quantity == CalibrationQuantity.HandEye) return sf.SlotKey;
            }
            return null;
        }

        /// <summary>
        /// 给现场的**建议性提示**（不改变判定结果）。
        /// 用途：把"这个工位还能考虑哪种方式"摆到建档提示里，让人知道可声明什么，
        /// 而不是让引擎替现场默默换一种标定方式。
        /// </summary>
        public static List<string> Hints(StationFacts st, SlotFacts sf, MethodResolution resolved)
        {
            var hints = new List<string>();
            if (st == null || sf == null) return hints;

            bool isUpCameraViaDown = resolved != null && resolved.Method != null
                                     && resolved.Method.Id == M_UpCameraViaDownCamera.Id;

            // 已按上下机映射 → 不重复提示
            if (!isUpCameraViaDown && sf.IsMovingMount && st.HasDownCamera)
            {
                hints.Add("本槽是随动（眼在手上）相机、工位又有下固定相机 ⇒ 若随动相机够不到固定靶、"
                        + "或拿不到吸嘴尖真值，可用『上下机映射』方式：在槽上把 "
                        + nameof(SlotFacts.MethodId) + " 声明为 \"" + M_UpCameraViaDownCamera.Id
                        + "\"，上相机就不做自己的手眼标定，改以跨相机映射经下相机中转。");
            }
            if (sf.IsMovingMount && !st.HasDownCamera && !isUpCameraViaDown)
            {
                // 常见缺口：想用上下机映射但没有下相机
                hints.Add("若打算用『上下机映射』，本工位需要一个【启用的】下固定（仰视）相机槽作为中介。");
            }
            if (resolved != null && resolved.Method != null
                && resolved.Method.Quantity == CalibrationQuantity.HandEye
                && resolved.Method.Layout == EyeMode.EyeInHand && sf.FollowsZ)
            {
                hints.Add("轴跟随含 Z：本槽的 H 只在标定高度成立（物距变 ⇒ 当量变），"
                        + "生产时必须回到标定高度拍照。");
            }
            return hints;
        }

        // ==================== 自检（脱机；不需要设备/档案/界面）====================

        /// <summary>
        /// 总表与领域枚举的一致性检查。
        /// 返回 (errors, findings)：errors = 表自身坏了（必须修）；findings = 表暴露出的产品问题（需人决策）。
        /// </summary>
        public static void Validate(List<string> errors, List<string> findings)
        {
            if (errors == null || findings == null) return;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var prodKeys = new Dictionary<string, string>(StringComparer.Ordinal);   // "量|路径" → Id
            var artifactScopes = new Dictionary<string, string>(StringComparer.Ordinal); // 归集范围 → Id
            var tagKeys = new Dictionary<string, string>(StringComparer.Ordinal);        // Tag → Id

            foreach (var m in All)
            {
                if (string.IsNullOrWhiteSpace(m.Id)) errors.Add("有行的 Id 为空");
                else if (!ids.Add(m.Id)) errors.Add("Id 重复：" + m.Id);

                if (m.Match == null) errors.Add(m.Id + "：Match 为空（没有任何条件能命中它）");
                if (m.Produces.Length == 0) errors.Add(m.Id + "：Produces 未写（产物是什么）");
                if (m.Condition.Length == 0) errors.Add(m.Id + "：Condition 未写（建档条件）");
                if (m.HowTo.Length == 0) errors.Add(m.Id + "：HowTo 未写（现场怎么做）");

                // Tag 是清单/资产名的键：空=清单里出现空条目；含括号冒号=有人又会去截字符串
                if (m.Tag == null || m.Tag.Trim().Length == 0)
                    errors.Add(m.Id + "：Tag 未写（清单/资产名的短标签，2~6 字）");
                else if (m.Tag.IndexOf('（') >= 0 || m.Tag.IndexOf('(') >= 0 || m.Tag.IndexOf('：') >= 0)
                    errors.Add(m.Id + "：Tag「" + m.Tag + "」含括号/冒号 ⇒ 它是要嵌进句子里的短标签，不能自带标点");

                if (Array.IndexOf(KnownStrategies, m.WizardStrategy) < 0)
                    errors.Add(m.Id + "：WizardStrategy「" + m.WizardStrategy + "」未在 KnownStrategies 注册");
                if (Array.IndexOf(KnownTemplates, m.StepTemplate) < 0)
                    errors.Add(m.Id + "：StepTemplate「" + m.StepTemplate + "」未在 KnownTemplates 注册");

                if (m.PrimaryPath == CalibrationAcquirePath.AlignTool && m.Quantity != CalibrationQuantity.ToolOffset)
                    errors.Add(m.Id + "：AlignTool 路径只能配 ToolOffset 量");

                // 渲染字段与作用域的一致性：写错了不会报错，只会让任务卡显示得莫名其妙
                if (m.Scope == MethodScope.Slot)
                {
                    // DependsOnDownCameraSlot 对槽级是**合法**的（上下机映射靠它指向下相机槽的 H），
                    // 但 PerToolHead / LayoutFollowsCamera 是工具级专属概念。
                    if (m.PerToolHead || m.LayoutFollowsCamera)
                        errors.Add(m.Id + "：槽级方式不该设 PerToolHead / LayoutFollowsCamera");
                    if (m.Name.Length > 0 && m.Name.Contains("{Slot}") == false)
                        findings.Add("槽级方式 " + m.Id + " 的显示名没有 {Slot} 占位 ⇒ 多相机工位上两张卡会同名。");
                }

                foreach (var p in m.AltPaths)
                {
                    if (p == m.PrimaryPath) errors.Add(m.Id + "：备选路径与首选路径相同（" + p + "）");
                }

                // 方式行 ↔ 路径表必须一致。两处都要写、由本检查强制一致：
                // 向导（出口3）按【采集路径】查策略/模板，任务卡（出口2）按【方式行】渲染文案。
                // 不一致的症状是"任务卡说要平移采点、向导却让人吸放"——两把尺子的老毛病。
                var pw = WiringOf(m.PrimaryPath);
                if (pw == null)
                {
                    errors.Add(m.Id + "：首选路径 " + m.PrimaryPath + " 没在 PathWirings 登记"
                             + " ⇒ 向导装配取不到策略/模板");
                }
                else
                {
                    if (pw.Strategy != m.WizardStrategy)
                        errors.Add(m.Id + "：WizardStrategy「" + m.WizardStrategy + "」与路径表给 "
                                 + m.PrimaryPath + " 的策略「" + pw.Strategy + "」不一致");
                    if (pw.Template != m.StepTemplate)
                        errors.Add(m.Id + "：StepTemplate「" + m.StepTemplate + "」与路径表给 "
                                 + m.PrimaryPath + " 的模板「" + pw.Template + "」不一致");
                }
                foreach (var ap in m.AltPaths)
                {
                    if (WiringOf(ap) == null)
                        errors.Add(m.Id + "：备选路径 " + ap + " 没在 PathWirings 登记");
                }

                string tg;
                if (m.Tag != null && tagKeys.TryGetValue(m.Tag, out tg))
                    errors.Add("Tag 重复：「" + m.Tag + "」同时属于 " + tg + " 与 " + m.Id + "（清单里会分不清）");
                else if (m.Tag != null) tagKeys[m.Tag] = m.Id;

                string pk = m.Quantity + "|" + m.PrimaryPath;
                string owner;
                if (prodKeys.TryGetValue(pk, out owner))
                    errors.Add("两行产同一个(量,路径) 组合：" + owner + " 与 " + m.Id + "（" + pk + "）——派生时无法区分");
                else prodKeys[pk] = m.Id;

                // 产物归集范围（CalibrationArtifact.BuildArtifactId 的规则：槽级量按槽、吸嘴级量按吸嘴）。
                // ★只对【工具级】行检查：工具级方式是"一次派生能同时命中多行"（e 与下相机像素旋转中心
                //   会同时出现在任务列表里），两行落到同一 (量, 归集范围) 才真的互相覆盖。
                //   槽级方式由 ResolveSlot **首次命中即停**（且同槽只取一行）⇒ 多行同量同范围只是
                //   "不同安装方式各自的路线"，对槽级报撞车是**假阳性**。
                if (m.Scope == MethodScope.Tool)
                {
                    string akey = m.Quantity + "|nozzle";
                    string aowner;
                    if (artifactScopes.TryGetValue(akey, out aowner))
                    {
                        findings.Add("产物撞车：工具级方式「" + aowner + "」与「" + m.Id + "」都归到 "
                            + akey + "（吸嘴级归集）⇒ 同一工位同时派生时 ArtifactId 相同、后写的覆盖先写的。"
                            + "需要给其中一个量另立 CalibrationQuantity 才能分开，"
                            + "或者明确它们不该同时出现（改 Match 让二者互斥）。");
                    }
                    else artifactScopes[akey] = m.Id;
                }
            }

            // 可达性：每一档 ConsumptionKind 都应有方式声明它，否则生产端永远等不到它（死枚举）
            var declaredKinds = new HashSet<ConsumptionKind>();
            foreach (var m in All)
            {
                if (m.Consumption.HasValue) declaredKinds.Add(m.Consumption.Value);
            }
            foreach (ConsumptionKind k in Enum.GetValues(typeof(ConsumptionKind)))
            {
                if (!declaredKinds.Contains(k))
                    findings.Add("消费口径「" + k + "」（第 " + (int)k + " 档）没有任何标定方式声明它"
                               + " ⇒ 除非有别的写入点，它是不可达的（死枚举）。");
            }

            // 路径表必须覆盖枚举的每一个值：向导按路径查策略/模板，漏一条 ⇒ 该路径的会话在向导里装配不出东西。
            // （★这条是 errors 不是 findings：它是本表自身的完整性，不修就是留一个"现场才会炸"的洞。）
            foreach (CalibrationAcquirePath p in Enum.GetValues(typeof(CalibrationAcquirePath)))
            {
                if (WiringOf(p) == null)
                    errors.Add("PathWirings 缺采集路径 " + p + "（枚举值 " + (int)p + "）"
                             + " ⇒ 用这条路径的导则会话装配不出策略/模板。");
            }

            // 可达性：每条 CalibrationAcquirePath 至少有方式用到（首选或备选）
            var usedPaths = new HashSet<CalibrationAcquirePath>();
            foreach (var m in All)
            {
                usedPaths.Add(m.PrimaryPath);
                foreach (var p in m.AltPaths) usedPaths.Add(p);
            }
            // ★不在这里额外 add 某条路径：能推断出来的路线就该落在某行的 PrimaryPath/AltPaths 上。
            //   （旧写法手工补过 RotatePickPlace，而 M_ToolRotation.AltPaths 本来就有它 ⇒ 那行是重复的真源。）
            foreach (CalibrationAcquirePath p in Enum.GetValues(typeof(CalibrationAcquirePath)))
            {
                if (!usedPaths.Contains(p))
                    findings.Add("采集路径「" + p + "」没有任何方式使用它（预留/死枚举）。");
            }
        }

        // ==================== 内部助手 ====================

        private static string Ans(string v)
        {
            return string.IsNullOrWhiteSpace(v) ? "未答" : v.Trim();
        }

        private static string SlotOf(MethodContext c)
        {
            return c == null ? "?" : (PrimaryCameraSlotOf(c.Station) ?? "?");
        }

        private static string BuildEvidence(CalibrationMethod m, MethodContext c)
        {
            return m.ReasonOf != null ? m.ReasonOf(c) : m.Condition;
        }

        private static string Reason(params string[] parts)
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
}
