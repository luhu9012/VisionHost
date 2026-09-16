//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationConsumptionContract.cs
// 说 明: ★★标定"消费口径"的唯一真源（2026-09-15 定案）。
//
//   ── 为什么要有这个文件 ─────────────────────────────────────────────────────
//   同一份九点矩阵 H，"它落在哪个域"决定生产端要不要再叠 O / 要不要补 b。
//   分错的后果不是"精度差一点"，而是**差一个杆长**（本工位 |b| ≈ 132mm），
//   而且 RMS 抓不到（整体平移不改变残差）。
//
//   历史上这条判据被写在三个地方、各写一遍：
//     ① 校验台（CalibrationVerifierViewModel + CameraCalibrationBundle）
//     ② 发布链（CalibrationManagerViewModel，把档案声明翻成工位配置字段）
//     ③ 生产引擎（VisionPickPlaceProcess.PickAnchor / MoveToWorkAsync）
//   三者【靠巧合一致】——现场就是这么出现"校验台反复压中、一上生产就偏一截"的：
//   校验台按 `吸点 = H(u) + b` 算，生产端却因为 `HasRodOffset` 没发出去而只走 `H(u)`。
//
//   本文件把这套判据收敛成**一个入口**：Resolve（判分型）→ ResolveObjectBase（物位）
//   → ResolveCommand（吸点）。三方都只调这三个函数，不许在自己那边再抄一遍 switch。
//
//   ── 三方各自的用法 ────────────────────────────────────────────────────────
//   · 校验台：从 CameraCalibrationBundle 组装 ConsumptionInputs → Resolve → 显示
//             "系统判定 + 判据"，并在日志里与人工强制口径做对照。
//   · 发布链：同一个 Resolve，把结果翻成工位配置的扁平字段（含 ConsumptionTag 留痕）。
//   · 生产端：FromPublishedConfig 读回扁平字段 → 同样的 ResolveObjectBase/ResolveCommand，
//             并把"实读口径"与发布时写下的 ConsumptionTag 对账（漂移即 ERROR）。
//
//   ── 报告优先于打断 ────────────────────────────────────────────────────────
//   本文件只**判定与留痕**，不做 UI 弹窗、不抛异常：Blockers 交给调用方决定
//   是"拦下来"（发布链）还是"降级并显式告警"（生产端，绝不许静默猜）。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Calibration.Models;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>
    /// 消费口径分型（**唯一的**口径枚举；<see cref="CalibrationSolveMode"/> 只是它的"界面强制"视图）。
    /// </summary>
    public enum ConsumptionKind
    {
        /// <summary>① H 已消杆（吸嘴域）：X_obj = H(u)，吸点 = H(u)。不叠 O、不用 P_photo、无 U 项。</summary>
        NozzleDomainDirect = 0,

        /// <summary>② 固定相机直接拍工件本体：X_obj = H(u)，吸点 = H(u)（工具偏距已吸收进 H）。</summary>
        EthDirect = 1,

        /// <summary>
        /// ③ ★固定相机 + 杆端域（延伸杆辅助标定）：吸点 = H(u) + Sign·b。
        /// b = 杆端 mark → 吸嘴尖；同心吸嘴在 U 回转轴上 ⇒ b 与 U 无关。
        /// ★本工位（002 复合工位 Cam_A）就是这一档。
        /// </summary>
        FixedCameraRodOffset = 2,

        /// <summary>④ EIH（眼在手）同轴吸嘴：X_obj = P_photo + O − H(u)，吸点 = X_obj（免 U 旋转项）。</summary>
        EihDirect = 3,

        /// <summary>⑤ EIH（眼在手）偏心吸嘴：吸点 = X_obj − R(U_go − U0)·e。</summary>
        EihWithRotation = 4,

        /// <summary>⑥ 下相机仰视二次对位：**相对纠偏** δ = H_down(R_img) − H_down(R_cdown)，与上面五档语义正交。</summary>
        DownCameraRelative = 5,
    }

    /// <summary>
    /// 口径判定的输入（由调用方从自己的数据源填）。
    /// 刻意做成与存储解耦的纯 DTO：校验台从档案填、生产端从工位配置填，判定函数只有一份。
    /// </summary>
    public sealed class ConsumptionInputs
    {
        // ---------- 身份 / 场景 ----------
        /// <summary>本槽是否下相机（H.PrimaryPath ∈ {DownCameraWalk, DownCameraPixelRotCenter}）</summary>
        public bool IsDownCamera;

        /// <summary>相机是否眼在手（随机械手走）</summary>
        public bool IsEyeInHand;

        /// <summary>H 的采集路径是否 CameraTruthWalk（固定相机"走位式"，真值=工具尖落点 ⇒ 杆端域候选）</summary>
        public bool HasTruthWalkPath;

        // ---------- 档案的两个显式声明（null = 未声明） ----------
        /// <summary>H 是否已消杆（已在吸嘴域）</summary>
        public bool? HandEyeInNozzleDomain;

        /// <summary>吸嘴是否与 U 回转轴同轴</summary>
        public bool? NozzleAxisCoaxial;

        // ---------- 自动推断用的"标定条件"（声明缺失时用来推断，声明存在时只做佐证） ----------
        /// <summary>e 档旋转拟合圆的半径（mm）。判据：卡片被吸嘴吸住画 1~2mm；延伸杆画 100+mm。</summary>
        public double? RotationFitRadiusMm;

        /// <summary>H/e 档的特征模板名（含"杆/mark"⇒ 杆端域；含"卡片/工件"⇒ 工件域）</summary>
        public string FeatureTemplateName;

        // ---------- 产物齐备性 ----------
        /// <summary>旋转中心 O 是否齐备</summary>
        public bool HasRotationCenter;

        /// <summary>真吸嘴偏心 e 是否齐备且非零</summary>
        public bool HasEcc;

        /// <summary>对针 t（EyeToHandImage 直量）是否可用 —— b 的首选来源</summary>
        public bool HasToolOffsetDirect;

        /// <summary>杆端偏心 ToolEccW 是否非零 —— b 的次选来源（旋转标定顺带产出）</summary>
        public bool HasRodOffsetCandidate;

        // ---------- 数值 ----------
        /// <summary>b 的 X（mm，H 域）—— 已由 ResolveRodOffset 按"t 优先"解析过</summary>
        public double RodOffsetWx;
        /// <summary>b 的 Y（mm，H 域）</summary>
        public double RodOffsetWy;
        /// <summary>b 的来源说明（日志用）</summary>
        public string RodOffsetSource;
        /// <summary>b 的符号（工位配置项，靠现场 A/B 判定；默认 +1）</summary>
        public double RodOffsetSign = 1.0;

        /// <summary>
        /// 工位配置里是否【显式判定过】b 的符号（发布链写过 <c>RodOffsetSignDeclared</c>）。
        ///
        /// ★★ 为什么必须与数值分开（2026-09-16 现场发现；与 ConsumptionTag 缺席被当成「一致」完全同构）：
        ///   <c>RodOffsetSign</c> 的代码默认就是 <c>1f</c> ⇒ 键缺席时取默认 ⇒ 日志打出
        ///   「b 符号 = +1（取工位配置）」，**看起来像已确认**，实际是「**从来没人判定过**」。
        ///   而符号选错偏 <c>2|b|</c>（本工位 264mm），比不补 b（偏 1|b|）**更危险** ⇒ 不能靠默认值蒙。
        ///   ⇒ 「值是 +1」与「判定过是 +1」必须是两个不同的答案。
        /// 默认 false = 调用方不声称已判定（口径闸据此硬拦）；只有生产端有权置 true。
        /// </summary>
        public bool RodOffsetSignDeclared;

        /// <summary>拍照机位 P_photo（仅 EIH 档用）</summary>
        public double PhotoBaseX, PhotoBaseY;
        /// <summary>旋转中心 O（仅 EIH 档用）</summary>
        public double RotCenterWx, RotCenterWy;
        /// <summary>真吸嘴偏心 e（仅 EihWithRotation 档用）</summary>
        public double EccX, EccY;
        /// <summary>基准角 U0（e 的参考角）</summary>
        public double U0Deg;

        // ---------- 下相机 ----------
        /// <summary>像素旋转中心 R_cdown 经 H_down 映射后的机械位（发布链算好的常量）</summary>
        public double DownAxisWx, DownAxisWy;
        /// <summary>下相机图像域是否已发布的 R_cdown 常量齐备</summary>
        public bool HasDownAxis;
        /// <summary>下相机拍照机位（δ 的兜底基准；仅在"老配置没发轴常量"时使用）</summary>
        public double DownPhotoX, DownPhotoY;

        /// <summary>
        /// 浅拷贝。
        /// 用途：调用方每次"走位"要覆盖某个数值（双吸嘴的 e 不同），必须**另起一个实例**——
        /// 直接写 `var x = inp; x.EccX = ...` 是**改共享实例**（同标识≠同实例：赋值点与读取点
        /// 落在同一个对象上，上一次调用写进去的偏心会漏给下一次）。字段全是值类型/字符串 ⇒ 浅拷贝足够。
        /// </summary>
        public ConsumptionInputs Clone()
        {
            return (ConsumptionInputs)MemberwiseClone();
        }
    }

    /// <summary>
    /// 发布标签对账的**三态**结果。
    ///
    /// ★★为什么必须是三态而不是"null = 一致"（2026-09-16 现场实锤）：
    ///   旧写法 <c>DiffAgainstTag</c> 把"工位配置里没有标签"返回 null = 「一致或无法比较」，
    ///   调用方那句 <c>if (diff != null) 报错</c> 于是**放过了最危险的状态——从未发布过**。
    ///   现场后果：ST_002 从没按口径契约发布过 ⇒ 生产端无声明 ⇒ 静默落到 ② 直吸 H(u)，
    ///   而该工位 H 是【杆端域】、b=(5.943,132.172)mm ⇒ **少补 132mm ⇒ 引导定位撞机**。
    ///   ⇒ "没对过账" 与 "对过且一致" 必须是两个不同的答案，且前者要能被拦下来。
    /// </summary>
    public enum TagReconcileStatus
    {
        /// <summary>工位配置里没有发布标签 ⇒ 该工位**从未按口径契约发布过**（≠ 一致）</summary>
        NoTag = 0,

        /// <summary>标签存在且与实读口径一致</summary>
        Match = 1,

        /// <summary>标签存在但与实读口径不一致（被别的相机覆盖 / 被手工改过 / 发布时就算错了）</summary>
        Mismatch = 2,
    }

    /// <summary>对账结果（三态 + 人话说明）。</summary>
    public sealed class TagReconcile
    {
        /// <summary>三态</summary>
        public TagReconcileStatus Status { get; set; }

        /// <summary>说明（含两侧标签与下一步处方；NoTag/Mismatch 时必有内容）</summary>
        public string Message { get; set; } = "";

        /// <summary>是否"对过账且一致"（唯一可以安静放行的状态）</summary>
        public bool Ok => Status == TagReconcileStatus.Match;
    }

    /// <summary>口径判定结果：分型 + 三个布尔标志 + 判据 + 阻断项（纯数据，不含 UI 行为）。</summary>
    public sealed class ConsumptionDecision
    {
        /// <summary>分型</summary>
        public ConsumptionKind Kind { get; set; }

        /// <summary>H 已在吸嘴域 ⇒ 直吸</summary>
        public bool NozzleDomain { get; set; }
        /// <summary>需 O 补偿（EIH 专属）</summary>
        public bool NeedO { get; set; }
        /// <summary>带 R(U−U0)·e 旋转项</summary>
        public bool RotationTerm { get; set; }
        /// <summary>固定相机杆端域：补 b</summary>
        public bool RodOffsetTerm { get; set; }
        /// <summary>b 的符号（+1 / −1）</summary>
        public double RodOffsetSign { get; set; } = 1.0;

        /// <summary>生效口径的**来源**：FromProfile = 自动判定；其余 = 界面强制（A/B 对照）</summary>
        public CalibrationSolveMode Source { get; set; } = CalibrationSolveMode.FromProfile;

        /// <summary>是否自动判定（未被人工强制覆盖）</summary>
        public bool AutoDerived => Source == CalibrationSolveMode.FromProfile;

        /// <summary>一句话算式（人话）</summary>
        public string Formula { get; set; } = "";

        /// <summary>为什么这么判（自动判定的依据链，逐条列出）</summary>
        public string Basis { get; set; } = "";

        /// <summary>档案声明摘要（勾了/没勾/未声明）</summary>
        public string DeclSummary { get; set; } = "";

        /// <summary>阻断项：这些成立时生产**必然错**，发布链应拦、生产端应显式告警后降级</summary>
        public List<string> Blockers { get; } = new List<string>();

        /// <summary>告警项：可疑但未必错（例如"声明吸嘴域但库里有 O"）</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>分型中文名</summary>
        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case ConsumptionKind.NozzleDomainDirect: return "①吸嘴域直吸";
                    case ConsumptionKind.EthDirect: return "②固定相机直拍工件";
                    case ConsumptionKind.FixedCameraRodOffset: return "③固定相机+杆端域(补b)";
                    case ConsumptionKind.EihDirect: return "④EIH同轴(补O)";
                    case ConsumptionKind.EihWithRotation: return "⑤EIH偏心(补O+U项)";
                    case ConsumptionKind.DownCameraRelative: return "⑥下相机相对纠偏";
                    default: return Kind.ToString();
                }
            }
        }

        /// <summary>落痕标签：发布链写进工位配置、生产端读回来对账（口径漂移即 ERROR）</summary>
        public string Tag =>
            $"{(int)Kind}|{KindText}|b={(RodOffsetTerm ? RodOffsetSign.ToString("+0;-0") : "n/a")}";

        /// <summary>
        /// 是否属【固定相机族】（① 吸嘴域直吸 / ② 直拍工件 / ③ 杆端域补 b）。
        /// 判据用途：这三个档的共同点是"吸点由 H 域 + 是否补 b 决定"，也正是**最容易静默分叉**的一族
        /// （EIH 两档有 O 兜底、下相机只做相对纠偏，都不会因"少一个常量"而整片偏 —— 固定相机族会）。
        /// </summary>
        public bool IsFixedCameraFamily =>
            Kind == ConsumptionKind.NozzleDomainDirect
            || Kind == ConsumptionKind.EthDirect
            || Kind == ConsumptionKind.FixedCameraRodOffset;

        /// <summary>
        /// 一致性对账（**三态**，生产端专用）：把"工位配置实读的口径"与发布时留下的标签比。
        ///
        /// 唯一允许安静放行的状态是 <see cref="TagReconcileStatus.Match"/>。
        /// 「标签缺席」是 <see cref="TagReconcileStatus.NoTag"/> —— "没对过账"，不是"对过账且一致"。
        /// </summary>
        public TagReconcile Reconcile(string publishedTag)
        {
            string mine = Tag;

            if (string.IsNullOrWhiteSpace(publishedTag))
            {
                return new TagReconcile
                {
                    Status = TagReconcileStatus.NoTag,
                    Message =
                        "工位配置里【没有口径发布标签】⇒ 本工位**从未按口径契约发布过**（不是「一致」，是「没对过账」）。"
                        + $"按当前工位配置实读口径 = [{mine}]。"
                        + "处方：到标定中心选中本工位【上相机】的 H 档案，点一次【发布到工位】（发布链会同时写下 "
                        + "HandEyeInNozzleDomain / HasRodOffset / b 与 ConsumptionTag）；"
                        + "若该工位确实是「直吸」口径，也请显式发布一次把它冻成标签，否则下一次改动仍查不出分叉。",
                };
            }

            if (string.Equals(mine, publishedTag.Trim(), StringComparison.Ordinal))
            {
                return new TagReconcile
                {
                    Status = TagReconcileStatus.Match,
                    Message = $"口径对账 OK：实读 [{mine}] 与发布标签一致。",
                };
            }

            return new TagReconcile
            {
                Status = TagReconcileStatus.Mismatch,
                Message = $"发布时口径标签为 [{publishedTag}]，按当前工位配置实读为 [{mine}]",
            };
        }

        /// <summary>
        /// 兼容旧签名：**只用于"必须静默"的历史调用点**。
        /// ⚠ 新代码请用 <see cref="Reconcile"/> —— 本方法把 NoTag 折叠成 null，会再次放过"从未发布"。
        /// </summary>
        /// <returns>null = 一致或无法比较（老配置无标签）；否则返回差异说明</returns>
        public string DiffAgainstTag(string publishedTag)
        {
            var r = Reconcile(publishedTag);
            return r.Status == TagReconcileStatus.Mismatch ? r.Message : null;
        }
    }

    /// <summary>
    /// ★★消费口径契约：判分型 + 物位 + 吸点，全部收敛在这里。
    /// </summary>
    public static class CalibrationConsumptionContract
    {
        /// <summary>长杆判据阈值：旋转拟合圆半径超过这个值就认为标定特征装在延伸杆上（卡片只有 1~2mm）。</summary>
        public const double LongRodRadiusMm = 20.0;

        // ==================== ① 判分型 ====================

        /// <summary>
        /// 判分型。默认 FromProfile = 读声明；声明缺失时按"标定条件"自动推断，并把依据写进 Basis。
        /// </summary>
        /// <param name="inp">输入（调用方从自己的数据源填）</param>
        /// <param name="forced">界面强制口径（默认 FromProfile = 自动判定）</param>
        public static ConsumptionDecision Resolve(ConsumptionInputs inp,
            CalibrationSolveMode forced = CalibrationSolveMode.FromProfile)
        {
            if (inp == null) throw new ArgumentNullException(nameof(inp));

            var dec = new ConsumptionDecision { Source = forced, RodOffsetSign = inp.RodOffsetSign };

            // ---- 界面强制（A/B 对照用）----
            switch (forced)
            {
                case CalibrationSolveMode.NozzleDomainDirect:
                    dec.Kind = ConsumptionKind.NozzleDomainDirect;
                    dec.Basis = "界面强制：按『H 已在吸嘴域』直吸";
                    break;
                case CalibrationSolveMode.RodEndWithRotation:
                    dec.Kind = ConsumptionKind.EihWithRotation;
                    dec.Basis = "界面强制：按『EIH 杆端域 + O/e 完整补偿』";
                    break;
                case CalibrationSolveMode.RodEndNoRotation:
                    dec.Kind = ConsumptionKind.EihDirect;
                    dec.Basis = "界面强制：按『EIH + O 补偿、免 U 项』";
                    break;
                case CalibrationSolveMode.FixedCameraPlusRodOffset:
                    dec.Kind = ConsumptionKind.FixedCameraRodOffset;
                    dec.RodOffsetSign = +1.0;
                    dec.Basis = "界面强制：按『固定相机 + b 正向』";
                    break;
                case CalibrationSolveMode.FixedCameraMinusRodOffset:
                    dec.Kind = ConsumptionKind.FixedCameraRodOffset;
                    dec.RodOffsetSign = -1.0;
                    dec.Basis = "界面强制：按『固定相机 + b 反向』（反面对照）";
                    break;
                default:
                    ResolveAuto(inp, dec);
                    break;
            }

            ApplyFlags(inp, dec);
            FillTexts(inp, dec);
            CollectDeclIssues(inp, dec);
            return dec;
        }

        /// <summary>
        /// ★★2026-09-16：按【另一份判定】改写分型 —— 让"口径真源"可以定在**相机槽的标定档案**上。
        ///
        /// 【为什么生产端需要它】
        ///   口径的天然归属是**相机槽**（工位 + Cam_x），而校验台正是工作在槽粒度上的：
        ///   校验成功那一刻，校验界面用的口径就是"这一槽该怎么消费"的结论。
        ///   但工位过程配置是**一套扁平字段 + 发布快照**：它只是那一刻的**副本**，
        ///   发布动作一旦漏做/被别的槽覆盖，副本就过期，而生产端会安静地按过期副本执行
        ///   （ST_002 实测：快照退 ②直吸 H(u)，档案是 ③杆端域，差 |b|=132.305mm ⇒ 撞机）。
        ///
        /// 【语义边界（务必分清）】
        ///   · 只搬 **分型**与**类别标志**；**数值一概不动** —— b / O / e / P_photo / 轴投影常量
        ///     仍取 `inp`（= 工位配置里的发布快照值）。
        ///   · **b 的符号不走档案**：符号是"这台机器的现场 A/B 结论"（档案里没有该字段），
        ///     由 `sign` 显式传入（工位配置 RodOffsetSign）。
        ///   · Blockers / Warnings / Formula 按**改写后的分型**重算（否则会拿旧分型的结论告警）。
        /// </summary>
        /// <param name="snapshot">工位配置实读的口径（发布快照）</param>
        /// <param name="archive">槽级档案判定（提供分型）</param>
        /// <param name="inp">数值输入（工位配置的发布快照，原样用）</param>
        /// <param name="sign">b 的符号（工位配置现场值）</param>
        /// <param name="sourceText">来源说明（日志用，如「槽 Cam_A 档案」）</param>
        public static ConsumptionDecision RewriteKindFrom(
            ConsumptionDecision snapshot, ConsumptionDecision archive, ConsumptionInputs inp,
            double sign, string sourceText)
        {
            if (snapshot == null) return null;
            if (archive == null) return snapshot;

            string snapshotTag = snapshot.Tag;   // 快照原判（留痕用）

            var d = new ConsumptionDecision
            {
                Kind = archive.Kind,
                RodOffsetSign = sign,
                Source = archive.Source,   // 保留"自动判定 / 界面强制"的来源属性
            };
            ApplyFlags(inp, d);
            FillTexts(inp, d);
            CollectDeclIssues(inp, d);
            d.Basis = $"口径真源={sourceText}：{archive.Basis}"
                    + $"｜（本工位的发布快照原判为 [{snapshotTag}]，已按档案改写）";
            return d;
        }

        /// <summary>自动判定（FromProfile 路径）：只用"声明 + 标定条件"，不猜数值。</summary>
        private static void ResolveAuto(ConsumptionInputs inp, ConsumptionDecision dec)
        {
            var why = new List<string>();

            // 下相机语义正交，最先分流
            if (inp.IsDownCamera)
            {
                dec.Kind = ConsumptionKind.DownCameraRelative;
                why.Add("H 的采集路径是下相机专属（DownCameraWalk/PixelRotCenter）⇒ 下相机相对纠偏档");
                dec.Basis = string.Join("；", why);
                return;
            }

            bool? decl = inp.HandEyeInNozzleDomain;
            bool inferred = false;
            if (!decl.HasValue)
            {
                decl = InferNozzleDomain(inp, why);
                inferred = true;
            }

            if (decl == true)
            {
                dec.Kind = ConsumptionKind.NozzleDomainDirect;
                why.Add(inferred
                    ? "H 域未声明 ⇒ 按标定条件推断为【吸嘴域】"
                    : "档案声明『H 已在吸嘴域』");
                dec.Basis = string.Join("；", why);
                return;
            }

            // 杆端/工件域
            if (decl == false) why.Add(inferred
                ? "H 域未声明 ⇒ 按标定条件推断为【杆端域】"
                : "档案声明『H 在杆端域』");
            else why.Add("H 域未声明且推断不出 ⇒ 保守按【杆端域】");

            if (inp.IsEyeInHand)
            {
                bool coaxial = inp.NozzleAxisCoaxial == true;
                dec.Kind = coaxial ? ConsumptionKind.EihDirect : ConsumptionKind.EihWithRotation;
                why.Add(coaxial
                    ? "相机眼在手 + 声明『吸嘴与 U 轴同轴』⇒ 免 U 旋转项"
                    : "相机眼在手" + (inp.NozzleAxisCoaxial.HasValue
                        ? " + 声明『偏心吸嘴』⇒ 保留 R(U−U0)·e"
                        : " + 同轴性未声明 ⇒ 保守保留 R(U−U0)·e"));
                dec.Basis = string.Join("；", why);
                return;
            }

            // 固定相机
            if (inp.HasTruthWalkPath)
            {
                dec.Kind = ConsumptionKind.FixedCameraRodOffset;
                why.Add("固定相机 + 采集路径 CameraTruthWalk（真值=工具尖落点）⇒ 杆端域，补 b");
                dec.Basis = string.Join("；", why);
                return;
            }

            dec.Kind = ConsumptionKind.EthDirect;
            why.Add("固定相机且采集路径非走位式 ⇒ 视为直接拍工件本体，直吸 H(u)");
            dec.Basis = string.Join("；", why);
        }

        /// <summary>
        /// 声明缺失时按"标定条件"推断 H 落在哪个域（把判据摊开写，便于现场反驳）。
        /// 判据三件套（强度从高到低）：
        ///   ① 旋转拟合圆半径：卡片被吸嘴吸住只会画 1~2mm；延伸杆要画 100+mm。
        ///   ② 特征模板名：含"杆/mark"⇒ 杆端域；含"卡片/工件"⇒ 工件域。
        ///   ③ 采集路径：CameraTruthWalk（走位式，真值=工具尖落点）⇒ 杆端域候选。
        /// 返回 null = 推不出来（此时按保守处理，并在 Basis 里说明）。
        /// </summary>
        private static bool? InferNozzleDomain(ConsumptionInputs inp, List<string> why)
        {
            if (inp.RotationFitRadiusMm.HasValue)
            {
                double r = inp.RotationFitRadiusMm.Value;
                if (Math.Abs(r) > LongRodRadiusMm)
                {
                    why.Add($"旋转拟合圆半径 {r:F3}mm > {LongRodRadiusMm:F0}mm（卡片吸在吸嘴上只画 1~2mm）⇒ 标定特征在长杆上 ⇒ 杆端域");
                    return false;
                }
                if (Math.Abs(r) > 1e-6)
                {
                    why.Add($"旋转拟合圆半径 {r:F3}mm（短杆/卡片量级）⇒ 工件域，不像是杆端");
                    return true;
                }
            }

            string tpl = inp.FeatureTemplateName;
            if (!string.IsNullOrWhiteSpace(tpl))
            {
                if (tpl.IndexOf("杆", StringComparison.Ordinal) >= 0
                    || tpl.IndexOf("延申", StringComparison.Ordinal) >= 0
                    || tpl.IndexOf("延伸", StringComparison.Ordinal) >= 0
                    || tpl.IndexOf("mark", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    why.Add($"特征模板名「{tpl}」含杆/mark 字样 ⇒ 杆端域");
                    return false;
                }
                if (tpl.IndexOf("卡片", StringComparison.Ordinal) >= 0
                    || tpl.IndexOf("工件", StringComparison.Ordinal) >= 0)
                {
                    why.Add($"特征模板名「{tpl}」指工件/卡片 ⇒ 工件域");
                    return true;
                }
            }

            if (inp.HasTruthWalkPath)
            {
                why.Add("采集路径为 CameraTruthWalk（固定相机走位式）⇒ 杆端域候选");
                return false;
            }

            why.Add("（旋转半径/模板名/采集路径都不足以定域 ⇒ 交由调用方按保守处理）");
            return null;
        }

        /// <summary>
        /// 分型 → 四个布尔标志（唯一映射；三方都从这里取标志位）。
        ///
        /// ★★U 旋转项 R(U−U0)·e 由谁决定（2026-09-15 定案，这条容易搞错）：
        ///   **只由 `NozzleAxisCoaxial` 声明决定** —— 它问的本来就是"转 U 时吸嘴尖在 XY 上动不动"。
        ///     · 同轴（true）  ⇒ 不动 ⇒ 任何分型都不该有这一项。
        ///     · 偏心（false）⇒ 动   ⇒ 只要 e 非零就必须带这一项，与"H 在哪个域"无关。
        ///     · 未声明(null)  ⇒ 保守按偏心处理（保留该项），与改动前行为一致。
        ///   ⚠ 为什么不能"按分型一刀切"：修改前校验台在『吸嘴域直吸』档里直接令 rotTerm=false，
        ///     而生产端 MoveToWorkAsync 只认 NozzleAxisCoaxial ⇒ **偏心吸嘴+吸嘴域 H 时两边不一致**（又一处默认分叉）。
        ///     现在两边共用本函数，规则只剩上面一条。
        ///   EihDirect / EihWithRotation 两档例外：它们的定义本身就是"免 U 项 / 带 U 项"，
        ///   优先于声明（这是界面强制口径做 A/B 对照时的语义）。
        /// </summary>
        private static void ApplyFlags(ConsumptionInputs inp, ConsumptionDecision dec)
        {
            dec.NozzleDomain = false; dec.NeedO = false; dec.RotationTerm = false; dec.RodOffsetTerm = false;

            // 声明驱动的 U 项（未声明=false ⇒ 保守保留）
            bool rotByDecl = inp.NozzleAxisCoaxial != true;

            switch (dec.Kind)
            {
                case ConsumptionKind.NozzleDomainDirect:
                    dec.NozzleDomain = true;
                    dec.RotationTerm = rotByDecl;
                    break;
                case ConsumptionKind.EthDirect:
                    dec.RotationTerm = rotByDecl;
                    break;
                case ConsumptionKind.FixedCameraRodOffset:
                    dec.RodOffsetTerm = true;
                    dec.RotationTerm = rotByDecl;
                    break;
                case ConsumptionKind.EihDirect:
                    dec.NeedO = true;               // 定义即"同轴免 U 项"
                    break;
                case ConsumptionKind.EihWithRotation:
                    dec.NeedO = true; dec.RotationTerm = true;
                    break;
                case ConsumptionKind.DownCameraRelative:
                    break;
            }
        }

        /// <summary>填算式文案与声明摘要。</summary>
        private static void FillTexts(ConsumptionInputs inp, ConsumptionDecision dec)
        {
            string sign = dec.RodOffsetSign < 0 ? "-" : "+";
            switch (dec.Kind)
            {
                case ConsumptionKind.NozzleDomainDirect:
                    dec.Formula = "X_obj = H(u)，吸点 = H(u)（H 已消杆；不叠 O、不用 P_photo、无 U 项）";
                    break;
                case ConsumptionKind.EthDirect:
                    dec.Formula = "X_obj = H(u)，吸点 = H(u)（直接拍工件本体，工具偏距已吸收进 H）";
                    break;
                case ConsumptionKind.FixedCameraRodOffset:
                    dec.Formula = $"X_obj = H(u) {sign} b，吸点 = X_obj（同心吸嘴：b 与 U 无关）";
                    break;
                case ConsumptionKind.EihDirect:
                    dec.Formula = "X_obj = P_photo + O − H(u)，吸点 = X_obj（同轴吸嘴：免 U 旋转项）";
                    break;
                case ConsumptionKind.EihWithRotation:
                    dec.Formula = "X_obj = P_photo + O − H(u)，吸点 = X_obj − R(U_go − U0)·e";
                    break;
                case ConsumptionKind.DownCameraRelative:
                    dec.Formula = "δ = H_down(R_img) − H_down(R_cdown)，放置位 = 固定位 − R(ΔU)·δ";
                    break;
            }

            dec.DeclSummary = $"H吸嘴域={TriState(inp.HandEyeInNozzleDomain)}"
                            + $"  吸嘴同轴={TriState(inp.NozzleAxisCoaxial)}"
                            + $"  眼在手={YesNo(inp.IsEyeInHand)}"
                            + $"  走位式H={YesNo(inp.HasTruthWalkPath)}"
                            + $"  O={YesNo(inp.HasRotationCenter)}  e={YesNo(inp.HasEcc)}"
                            + $"  b候选={YesNo(inp.HasRodOffsetCandidate || inp.HasToolOffsetDirect)}"
                            + (inp.RotationFitRadiusMm.HasValue ? $"  旋转半径={inp.RotationFitRadiusMm.Value:F3}mm" : "");
        }

        /// <summary>把"必然错"和"可疑"分开列出来（调用方决定拦还是降级）。</summary>
        private static void CollectDeclIssues(ConsumptionInputs inp, ConsumptionDecision dec)
        {
            switch (dec.Kind)
            {
                case ConsumptionKind.FixedCameraRodOffset:
                    if (!inp.HasRodOffsetCandidate && !inp.HasToolOffsetDirect)
                    {
                        dec.Blockers.Add(
                            "固定相机 + 杆端域：H 只把【杆端 mark】送到像素上，命令到 H(u) 时吸嘴尖并不在点上——"
                            + "必须补 b = 杆端→吸嘴偏移，但档案里既无对针 t 也无杆端偏心 ToolEccW。"
                            + "按现状生产会整片偏一个 |b|（本工位量级 ≈132mm）。"
                            + "取 b 的两条路：① 对针专窗做 EyeToHand 图像对针（直量，最准）；② 旋转标定顺带产出的 ToolEccW（推算，残差 1~3mm）。");
                    }
                    break;

                case ConsumptionKind.EihDirect:
                case ConsumptionKind.EihWithRotation:
                    if (!inp.HasRotationCenter)
                    {
                        dec.Blockers.Add(
                            "眼在手（EIH）：物位式 X_obj = P_photo + O − H(u) 需要旋转中心 O，但档案缺 O ⇒ "
                            + "生产会退化为视觉直吸，落点不可信。请先完成旋转中心标定（并在对针专窗求 e）。");
                    }
                    if (dec.Kind == ConsumptionKind.EihWithRotation && !inp.HasEcc)
                    {
                        dec.Warnings.Add("偏心吸嘴档但无偏心量 e ⇒ 吸点 = X_obj，仍会偏一个杆端/吸嘴偏心量。");
                    }
                    break;

                case ConsumptionKind.NozzleDomainDirect:
                    if (inp.HasRotationCenter)
                    {
                        dec.Warnings.Add(
                            "声明『H 已在吸嘴域』，但库里同时存在旋转中心 O —— 若 H 其实是**未消杆**的原始矩阵，"
                            + "再叠 O/e 就是双重补偿（错几毫米且 RMS 抓不到）。请用『点特征 + 低速到位 + 看落下的是吸嘴尖还是杆端』做一次判定。");
                    }
                    break;

                case ConsumptionKind.DownCameraRelative:
                    if (!inp.HasDownAxis)
                    {
                        dec.Blockers.Add(
                            "下相机相对纠偏需要常量 H_down(R_cdown)（像素旋转中心经下相机矩阵映射后的机械位），"
                            + "但工位配置里没有它 ⇒ 生产端的 δ 只能退化成 H_down(R_img) − 拍照机位。"
                            + "★注意这个退化量在数学上恒 ≈0（工件刚性吸在吸嘴上，机器人一动它跟着动，"
                            + "H_down(R_img) 必然等于当时机位）⇒ 位置纠偏**看起来执行了、实际不动**。"
                            + "请在标定中心对下相机档案重新执行一次发布，把该常量写入工位配置。");
                    }
                    break;
            }
        }

        // ==================== ② 像素 → 物位（X_obj） ====================

        /// <summary>
        /// 像素经矩阵映射后的 w = H(u) → 工件特征真实位置 X_obj。
        /// 这是**生产引擎 PickAnchor** 与**校验台的 obj**共用的同一个函数。
        /// </summary>
        /// <param name="inp">输入（数值齐备）</param>
        /// <param name="dec">口径判定结果（由 Resolve 得到）</param>
        /// <param name="wX">H(u).X</param>
        /// <param name="wY">H(u).Y</param>
        /// <param name="trace">逐行留痕（可为 null）</param>
        public static (double X, double Y) ResolveObjectBase(
            ConsumptionInputs inp, ConsumptionDecision dec, double wX, double wY, Action<string> trace = null)
        {
            switch (dec.Kind)
            {
                case ConsumptionKind.NozzleDomainDirect:
                    trace?.Invoke($"  X_obj 口径=吸嘴域直吸（H 已消杆）：X_obj=H(u)=({wX:F3},{wY:F3})（不叠 O、不用 P_photo）");
                    return (wX, wY);

                case ConsumptionKind.EthDirect:
                    trace?.Invoke($"  X_obj 口径=固定相机直拍工件：X_obj=H(u)=({wX:F3},{wY:F3})");
                    return (wX, wY);

                case ConsumptionKind.FixedCameraRodOffset:
                {
                    if (!inp.HasRodOffsetCandidate && !inp.HasToolOffsetDirect)
                    {
                        trace?.Invoke("  ⚠ X_obj 口径=固定相机杆端域，但 b 未标定 ⇒ 本次退化为 X_obj=H(u)，"
                                      + "落点会偏一个 |b| 量级（不是换算错，是标定产物缺 b）");
                        return (wX, wY);
                    }
                    double bx = dec.RodOffsetSign * inp.RodOffsetWx;
                    double by = dec.RodOffsetSign * inp.RodOffsetWy;
                    double mag = Math.Sqrt(bx * bx + by * by);
                    if (mag < 1e-6)
                    {
                        trace?.Invoke("  ⚠ X_obj 口径=固定相机杆端域：字段已开但 b≈0 ⇒ 本次退化为 X_obj=H(u)，"
                                      + "请检查发布链是否真的写了 RodOffsetWx/Wy");
                        return (wX, wY);
                    }
                    trace?.Invoke($"  X_obj 口径=固定相机杆端域补 b：X_obj=H(u)+b=({wX + bx:F3},{wY + by:F3})，"
                                  + $"b=({bx:F3},{by:F3}) |b|={mag:F3}mm 符号={(dec.RodOffsetSign < 0 ? "-" : "+")}1"
                                  + $"（来源：{inp.RodOffsetSource ?? "未记录"}；同心吸嘴 b 与 U 无关，符号若反会偏 2|b|）");
                    return (wX + bx, wY + by);
                }

                case ConsumptionKind.EihDirect:
                case ConsumptionKind.EihWithRotation:
                {
                    if (!inp.HasRotationCenter)
                    {
                        trace?.Invoke("  ⚠ X_obj 口径=EIH 叠 O，但档案缺旋转中心 O ⇒ 退化 X_obj=H(u)，落点不可信");
                        return (wX, wY);
                    }
                    var o = CalibrationGeometry.ObjectBaseV2(wX, wY, inp.PhotoBaseX, inp.PhotoBaseY,
                        inp.RotCenterWx, inp.RotCenterWy, needsOCompensation: true);
                    trace?.Invoke($"  X_obj 口径=EIH：O=({inp.RotCenterWx:F3},{inp.RotCenterWy:F3}) "
                                  + $"X_obj=P_photo+O−H(u)=({o.X:F3},{o.Y:F3})");
                    return (o.X, o.Y);
                }

                default:
                    // 下相机不走绝对物位（语义正交），调用方应走 ResolveDownCameraOffset。
                    trace?.Invoke("  ⚠ 下相机口径不产生绝对物位（相对纠偏），本函数返回 H(u) 原值");
                    return (wX, wY);
            }
        }

        // ==================== ③ 物位 → 吸点（command） ====================

        /// <summary>
        /// 物位 X_obj → 机械手命令位（吸点）。
        /// 这是**生产引擎 MoveToWorkAsync** 与**校验台的 cmd**共用的同一个函数。
        /// </summary>
        /// <param name="inp">输入</param>
        /// <param name="dec">口径判定结果</param>
        /// <param name="objX">物位 X</param>
        /// <param name="objY">物位 Y</param>
        /// <param name="uGoDeg">作业回转角（U_go）</param>
        /// <param name="trace">逐行留痕（可为 null）</param>
        public static (double X, double Y) ResolveCommand(
            ConsumptionInputs inp, ConsumptionDecision dec, double objX, double objY,
            double uGoDeg, Action<string> trace = null)
        {
            // ★注意：dec.RotationTerm 已经是"最终要不要带这一项"的结论（自动档里已把"同轴"折进去了，
            //   强制档 ④/⑤ 则不受声明影响），这里只再确认一次"有没有 e 可带"。
            bool applyRot = dec.RotationTerm && inp.HasEcc;

            if (!applyRot)
            {
                if (dec.RotationTerm && !inp.HasEcc)
                    trace?.Invoke("  ⚠ 偏心吸嘴档但无 e ⇒ 吸点=物位，仍偏一个偏心量");
                else if (inp.NozzleAxisCoaxial == true)
                    trace?.Invoke($"  吸点口径=同轴（档案声明）：免 R(U−U0)·Ecc，吸点=物位({objX:F3},{objY:F3})");
                else
                    trace?.Invoke($"  吸点口径=免 U 项：吸点=物位({objX:F3},{objY:F3})");
                return (objX, objY);
            }

            var c = CalibrationGeometry.CommandFor(objX, objY, inp.EccX, inp.EccY, uGoDeg, inp.U0Deg);
            trace?.Invoke($"  吸点口径=偏心吸嘴：e=({inp.EccX:F3},{inp.EccY:F3}) "
                          + $"吸点=X_obj−R({uGoDeg - inp.U0Deg:F2}°)·e=({c.X:F3},{c.Y:F3})");
            return (c.X, c.Y);
        }

        // ==================== ④ 下相机相对纠偏 ====================

        /// <summary>
        /// 下相机相对纠偏：δ = H_down(R_img) − H_down(R_cdown)。
        /// ★与上相机绝对定位语义正交，是下相机**唯一有意义**的口径。
        /// </summary>
        /// <param name="imgWx">卡片特征像素经 H_down 映射后的 X（= H_down(R_img).X，即节点 OutputX）</param>
        /// <param name="imgWy">同上 Y</param>
        /// <param name="axisWx">像素旋转中心经 H_down 映射后的 X（= H_down(R_cdown).X，发布链算好的常量）</param>
        /// <param name="axisWy">同上 Y</param>
        public static (double Dx, double Dy) ResolveDownCameraOffset(
            double imgWx, double imgWy, double axisWx, double axisWy)
            => CalibrationGeometry.DownCameraOffset(imgWx, imgWy, axisWx, axisWy);

        // ==================== ⑤ 从"生产配置"反读口径 ====================

        /// <summary>
        /// 生产端专用：从工位配置的扁平字段反读口径（**不做推断**——推断是发布链的事，
        /// 生产端只负责"执行 + 与发布标签对账"）。
        /// </summary>
        public static ConsumptionDecision FromPublishedConfig(
            bool isDownCamera, bool handEyeInNozzleDomain, bool needsOCompensation, bool cameraMountEih,
            bool hasRodOffset, bool nozzleAxisCoaxial, double rodOffsetSign)
        {
            var inp = new ConsumptionInputs
            {
                IsDownCamera = isDownCamera,
                HandEyeInNozzleDomain = handEyeInNozzleDomain,
                NozzleAxisCoaxial = nozzleAxisCoaxial,
                RodOffsetSign = rodOffsetSign,
                IsEyeInHand = needsOCompensation || cameraMountEih,
            };
            bool needO = needsOCompensation || cameraMountEih;

            var dec = new ConsumptionDecision { Source = CalibrationSolveMode.FromProfile, RodOffsetSign = rodOffsetSign };
            if (isDownCamera)
            {
                dec.Kind = ConsumptionKind.DownCameraRelative;
                dec.Basis = "读工位配置：下相机段";
            }
            else if (handEyeInNozzleDomain)
            {
                dec.Kind = ConsumptionKind.NozzleDomainDirect;
                dec.Basis = "读工位配置：HandEyeInNozzleDomain=true（发布链已写）";
            }
            else if (needO)
            {
                dec.Kind = nozzleAxisCoaxial ? ConsumptionKind.EihDirect : ConsumptionKind.EihWithRotation;
                dec.Basis = "读工位配置：NeedsOCompensation/CameraMountEih=true（发布链已写）";
            }
            else if (hasRodOffset)
            {
                dec.Kind = ConsumptionKind.FixedCameraRodOffset;
                dec.Basis = "读工位配置：HasRodOffset=true（发布链已写）";
            }
            else
            {
                dec.Kind = ConsumptionKind.EthDirect;
                dec.Basis = "读工位配置：无 O、无 b ⇒ 直吸 H(u)";
            }

            dec.NozzleDomain = dec.Kind == ConsumptionKind.NozzleDomainDirect;
            dec.NeedO = dec.Kind == ConsumptionKind.EihDirect || dec.Kind == ConsumptionKind.EihWithRotation;
            dec.RodOffsetTerm = dec.Kind == ConsumptionKind.FixedCameraRodOffset;
            // U 旋转项：EIH 两档由分型定义；其余分型由『吸嘴是否与 U 轴同轴』的声明决定（见 ApplyFlags 注释）
            dec.RotationTerm = dec.Kind == ConsumptionKind.EihWithRotation
                               || (dec.Kind != ConsumptionKind.EihDirect && nozzleAxisCoaxial != true);
            dec.Formula = DescribeFormula(dec.Kind, dec.RodOffsetSign);
            return dec;
        }

        /// <summary>按分型给算式文案（发布链 / 生产端日志共用）。</summary>
        public static string DescribeFormula(ConsumptionKind kind, double rodOffsetSign = 1.0)
        {
            switch (kind)
            {
                case ConsumptionKind.NozzleDomainDirect: return "X_obj=H(u)，吸点=H(u)";
                case ConsumptionKind.EthDirect: return "X_obj=H(u)，吸点=H(u)";
                case ConsumptionKind.FixedCameraRodOffset: return $"X_obj=H(u){(rodOffsetSign < 0 ? "-" : "+")}b，吸点=X_obj";
                case ConsumptionKind.EihDirect: return "X_obj=P_photo+O−H(u)，吸点=X_obj";
                case ConsumptionKind.EihWithRotation: return "X_obj=P_photo+O−H(u)，吸点=X_obj−R(U−U0)·e";
                case ConsumptionKind.DownCameraRelative: return "δ=H_down(R_img)−H_down(R_cdown)";
                default: return "";
            }
        }

        // ==================== ⑥ b 的来源解析（t 优先，ToolEccW 兜底） ====================

        /// <summary>
        /// 解析 b（杆端→吸嘴偏移）。**发布链与校验台必须都调这里**——
        /// 两侧各选一次来源就会分叉（历史事故：发布端取"被发布档案"的 ToolEccW，消费端取 e 档）。
        /// 优先级：对针 t（EyeToHandImage 直量，无拟合残差） &gt; 旋转标定 ToolEccW（推算，残差 1~3mm）。
        /// </summary>
        /// <returns>src == null 表示两个来源都没有（b 未标定）</returns>
        public static (double Bx, double By, string Src) ResolveRodOffset(
            double toolOffsetWx, double toolOffsetWy, bool hasToolOffsetDirect,
            double toolEccWx, double toolEccWy, bool hasRodOffsetCandidate)
        {
            if (hasToolOffsetDirect
                && (Math.Abs(toolOffsetWx) > 1e-9 || Math.Abs(toolOffsetWy) > 1e-9))
                return (toolOffsetWx, toolOffsetWy, "对针 t（EyeToHandImage 直量）");

            if (hasRodOffsetCandidate
                && (Math.Abs(toolEccWx) > 1e-9 || Math.Abs(toolEccWy) > 1e-9))
                return (toolEccWx, toolEccWy, "旋转标定杆端偏心 ToolEccW（推算）");

            return (0, 0, null);
        }

        // ==================== 小工具 ====================

        /// <summary>把判定结果渲染成"给现场看的一段话"（校验台提示 / 发布日志 / 生产启动横幅共用）。</summary>
        public static string DescribeDecision(ConsumptionDecision dec)
        {
            var lines = new List<string>
            {
                $"[口径] {dec.KindText}（{(dec.AutoDerived ? "系统自动判定" : "人工强制/对照")}）",
                $"       算式: {dec.Formula}",
                $"       依据: {dec.Basis}",
            };
            if (!string.IsNullOrWhiteSpace(dec.DeclSummary))
                lines.Add($"       档案: {dec.DeclSummary}");
            foreach (var b in dec.Blockers) lines.Add($"       ⛔ {b}");
            foreach (var w in dec.Warnings) lines.Add($"       ⚠ {w}");
            return string.Join("\n", lines);
        }

        private static string YesNo(bool b) => b ? "是" : "否";
        private static string TriState(bool? b) => !b.HasValue ? "未声明" : (b.Value ? "是" : "否");
    }
}
