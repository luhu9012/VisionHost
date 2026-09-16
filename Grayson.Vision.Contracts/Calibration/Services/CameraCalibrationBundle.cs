//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CameraCalibrationBundle.cs
// 说 明: ★相机级标定消费门面（2026-09-12 新增，回答"一个相机一个完整映射"的诉求）。
//
//        背景与动机：
//          v2 标定体系按【物理量】H/e/t/s 分条存储（归属粒度不同：H/s 相机级、e/t 吸嘴级；
//          采集方式不同、依赖与过期独立）——这是标定/过期的正确组织，不能改。
//          但【消费端】要的是"一句话拿到像素→吸嘴命令位的完整映射"，而不是自己逐条查
//          H、再叠 e、再叠 t 各抄一份公式。历史上校验台、视觉链节点、生产引擎三处各拼各的，
//          就是反复调不准的根源（见 CalibrationGeometry 头注"走过弯路"）。
//
//        本门面 = 纯计算聚合视图，不落库、不改存储：
//          把 (StationCode, SlotKey) 下已发布的一条 H + 关联 e（吸嘴级）+ t + s 聚合成一个
//          Bundle，暴露统一方法 SolvePixelToCommand / SolvePixelToObject，内部只调
//          CalibrationGeometry（唯一真源公式），矩阵映射通过注入的 mapper 委托完成——
//          Contracts 层不反向依赖 HalconWrapper，由调用方（校验台/视觉链）注入 ICalibrationService。
//
//        与发布链、生产引擎的关系：
//          · 发布链把 e/O/U0 写进工位业务配置（MahjongDualNozzleConfig 等），生产引擎按
//            CalibrationGeometry 消费——本门面与它们【同源同果】，只是给"有 CalibrationProfile
//            集合在手的消费端"（校验台、视觉链节点）一个统一入口，减少手抄。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Calibration.Models;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>
    /// 相机级标定消费门面：聚合某工位某相机槽下已发布的 H/e/t/s，提供
    /// "像素 → 吸嘴命令位"一步到位换算。存储按物理量分条不动，消费在此收敛。
    /// </summary>
    public class CameraCalibrationBundle
    {
        /// <summary>矩阵映射委托：像素(col,row) → 世界(wx,wy)。由调用方注入（包装 ICalibrationService.MapPixelToWorld）。</summary>
        public delegate bool PixelMapper(double px, double py, out double wx, out double wy, out string error);

        /// <summary>工位代码</summary>
        public string StationCode { get; }

        /// <summary>相机槽键（Cam_A / Cam_C …）</summary>
        public string SlotKey { get; }

        /// <summary>H（像素↔机械 2D 仿射）产物；null=未发布</summary>
        public CalibrationProfile H { get; }

        /// <summary>e（旋转中心 + 吸嘴偏心）产物；null=未发布。吸嘴级，此处取该槽关联的第一份。</summary>
        public CalibrationProfile E { get; }

        /// <summary>t（工具偏距）产物；null=未发布</summary>
        public CalibrationProfile T { get; }

        /// <summary>s（像素当量）产物；null=未发布</summary>
        public CalibrationProfile S { get; }

        /// <summary>
        /// 聚合说明（空=无异常）。e/t 是吸嘴级产物，常出现在非本槽键/未绑工位的会话档案里；
        /// 聚合时若从槽外借用，在此记录来源——消费端应把它打进日志，不许"安静地丢"或"安静地借"。
        /// </summary>
        public string AggregationNotes { get; private set; }

        private readonly PixelMapper _mapper;

        public CameraCalibrationBundle(
            string stationCode, string slotKey,
            CalibrationProfile h, CalibrationProfile e, CalibrationProfile t, CalibrationProfile s,
            PixelMapper mapper)
        {
            StationCode = stationCode;
            SlotKey = slotKey;
            H = h; E = e; T = t; S = s;
            _mapper = mapper;
        }

        // ==================== 就绪状态 ====================

        /// <summary>矩阵文件是否可用（H 存在且有可映射矩阵）</summary>
        public bool IsMatrixReady => H != null && !string.IsNullOrWhiteSpace(H.HomMatFilePath);

        /// <summary>是否眼在手 EIH（相机随机械手走）；默认保守按 EIH（与 CalibrationGeometry 约定一致）。</summary>
        public bool IsEyeInHand => H?.EyeMode == EyeMode.EyeInHand;

        /// <summary>
        /// ★2026-09-15 纠偏（推翻 2026-09-12 的"ETH+杆也叠 O"）：是否需 O 补偿（EIH 专属）。
        ///
        /// 理由一（可证伪的数值）：O 补偿式 X_obj = P_photo + O − H(u) 里含【拍照机位 P_photo】。
        ///   只有"相机随机械手走"（EIH）时，物位才可能与机械手位置相关；固定相机下工件在台面上不动，
        ///   把 P_photo 混进物位在物理上说不通。实测反证（复合工位 Cam_A，2026-09-15）：
        ///   H(u)=(288.245,97.896)、P_photo=(322.245,101.567)、O=(273.0785,−20.5211)
        ///   ⇒ X_obj=(307.08,−16.85)mm；而本工位正确物位 = H(u)+b = (255.53,199.13)（见 b 的推导）——
        ///   该式偏出约 220mm，量级≈|b|，说明它算的根本不是"工件在世界里的位置"。
        ///   ⚠ 注意别拿"命令位范围"当工件世界坐标：本工位 flange/命令位在 Y≈97~124，
        ///     而【工件世界】在 Y≈180~230（差的就是杆端偏移 b=(−32.7,+101.2)）——两者别混。
        /// 理由二（平台自相矛盾）：枚举文档 CalibrationDomainV2.CalibrationAcquirePath 对
        ///   CameraTruthWalk 的约定是"真值=相机中心（工具尖偏距需 **t** 补偿）"——
        ///   补的是 t，不是 O/e。O/e 那套是给 EIH 的。
        /// 理由三（几何，2026-09-15 补）：旋转阶段只转 U 不动 XY（MoveRotationTo），
        ///   故映射后采样点 {H(p_θ)} 的圆心 = P_rotate − b（不是回转轴本身）；
        ///   而 Φ(u) = H(u) + b 与 {Φ(p_θ)} 圆心 = P_rotate 是同一件事的两面 ⇒ O 天然带 −b 偏移，
        ///   拿它当物位正好错一段 b。这也是"固定相机用 b、不用 O"的最强依据。
        /// 故：固定相机不再走 O 补偿；其杆端偏距按约定用 t 补（见 IsFixedCameraRodDomain）。
        /// </summary>
        public bool NeedsOCompensation =>
            !IsNozzleDomainH && IsEyeInHand;

        /// <summary>
        /// ★2026-09-15：固定相机(非 EIH) + 杆端域 H（CameraTruthWalk）——即"相机固定 + 延伸杆辅助标定"。
        /// 按平台自己的约定（CalibrationDomainV2 枚举文档 + 对针 t 定义 t = P_tip − H(p_tip)）：
        ///   吸点 = H(u) + t。
        /// t 未标(t=0) ⇒ 退化为 H(u)，与"缺 O 退化直吸"同一结果（零回归），但会显式提示落点仍偏杆端 r。
        /// </summary>
        public bool IsFixedCameraRodDomain =>
            !IsNozzleDomainH && !IsEyeInHand
            && H?.PrimaryPath == CalibrationAcquirePath.CameraTruthWalk;

        /// <summary>对针偏距 t（工具尖相对相机引导点）是否已标定且非零</summary>
        public bool HasToolOffset =>
            T != null && T.IsToolOffsetCalibrated
            && (Math.Abs(T.ToolOffsetWx) > 1e-9 || Math.Abs(T.ToolOffsetWy) > 1e-9);

        /// <summary>
        /// ★2026-09-15：对针 t 的【采集方式】——同名两义，这是唯一能判"该加还是该减"的依据。
        ///   ToolOffsetMethod.EyeToHandImage    ：δ = M_tool − H(A')（固定相机：工具对准特征锁命令位 − 矩阵输出）
        ///                                        ⇒ 消费 **+t**（本分支）
        ///   ToolOffsetMethod.EyeInHandIndirect ：TCO = H(p_tip) − R_n（EIH 间接对针，含绝对坐标 R_n）
        ///                                        ⇒ 消费 **−t**，且只属 EIH 分支，绝不可当固定相机的 b 用
        ///   LegacyUnknown / 未标              ：不参与消费（宁可当没有，也不猜符号）
        /// 踩坑背景：同一个字段名 ToolOffsetWx/Wy 被两种对针写入了符号相反的两种量，
        ///   消费端只看"非零"就套公式 ⇒ 差 2 倍，且没有任何闸门能发现。
        /// </summary>
        public ToolOffsetMethod? ToolOffsetKind =>
            (T != null && T.IsToolOffsetCalibrated) ? T.ToolOffsetMethod : (ToolOffsetMethod?)null;

        /// <summary>固定相机（EyeToHand）图像对针偏距是否可用（仅 EyeToHandImage 语义 + 非零）</summary>
        public bool HasEthToolOffset =>
            HasToolOffset && ToolOffsetKind == ToolOffsetMethod.EyeToHandImage;

        /// <summary>
        /// ★2026-09-15：杆端 Mark 相对【U 回转轴】的固定偏移 b（H 域/命令位域，θ=0≈U0 参考）。
        ///
        /// 语义（固定相机 + 延伸杆辅助标定的关键量）：
        ///   H(u) 的定义是"把【杆端 mark】送到像素 u 的机械命令位"（九点真值记的就是这个），
        ///   而同心吸嘴就坐在 U 回转轴上 ⇒ 把【吸嘴尖】送到同一像素要再补这段 mark→轴 的位移：
        ///       **吸嘴命令 = H(u) + b**（b 与 U 无关：同心吸嘴在轴上，转 U 不动轴）。
        ///   物理含义 = "延伸杆的长度/偏心"，|b| ≈ 杆端 mark 到吸嘴轴的距离（本工位 106.4mm）。
        ///
        /// 与 e / t 的区别与联系（这三个量最容易被混成一个，务必分清）：
        ///   · 吸嘴偏心 e       = ToolOffsetPureW = O − H(p_tip)：吸嘴尖相对轴的偏心，**同心吸嘴 = 0**，
        ///                        必须"物理对针"才有值。⚠ 没有任何"e"能推算出 b，用 e 推是错方向。
        ///   · 对针 t（EyeToHand）= M_tool − H(A')：与 b **是同一个物理量**（都是 mark→吸嘴尖的位移），
        ///                        只是量法不同；故 t 已标时优先用 t（直接量，无拟合偏差）。
        ///   · 杆端偏心 ToolEccW = M·ToolEccPx = 像素偏心矢经 H 映射 = 本属性 b：
        ///                        旋转标定【顺带就测出来了】，不需要额外上机动作。
        /// ⚠ 精度（2026-09-15 纠正，先前误归因）：平台自测的"像素定圆再映射 ≈ 8mm 偏差"是
        ///   【O】的偏差（把定圆圆心一次性映射过去，偏差原样带出）；**b 不受该 8mm 影响**——
        ///   b = H(c+e) − H(c) 是同一次仿射映射的两点相减，映射的常数项在差分里抵消；
        ///   ToolEccPx 又是各采样点去旋转后取均值，对称覆盖（0/90/180/270）时定圆中心偏差也抵消。
        ///   实测佐证：O 与 H(像素圆心) 差 8.2404mm，而本工位 |b| 与"mark−O"完全自洽（Δ=0.0000）。
        ///   b 的真实残差量级 = 各向异性/剪切在差分上的残差（≈1~3mm，实测 2.32% 各向异性）+ 提取噪声。
        ///   ⇒ b 属**推算值**：定方向、定量级、做落点验收够用；要亚毫米再做一次对针（t 直量，无此残差）。
        /// </summary>
        public bool HasRodOffset =>
            E != null && (Math.Abs(E.ToolEccWx) > 1e-9 || Math.Abs(E.ToolEccWy) > 1e-9);

        /// <summary>杆端→吸嘴偏移 b 的 X（mm，H 域）</summary>
        public double RodOffsetWx => E?.ToolEccWx ?? 0.0;

        /// <summary>杆端→吸嘴偏移 b 的 Y（mm，H 域）</summary>
        public double RodOffsetWy => E?.ToolEccWy ?? 0.0;

        /// <summary>杆端→吸嘴偏移 b 的来源档案名（日志用）</summary>
        public string RodOffsetSource => E?.Name;

        /// <summary>
        /// ★★2026-09-15：槽内【任一档案】声明了「把 b 带进生产」(RodOffsetInProduction == true)。
        ///
        /// 为什么必须按【槽】聚合、不能只看被发布的那一份：
        ///   工位过程配置是**扁平字段**（无相机槽维度），同一槽的 H 档与 e 档会**各发布一次**，
        ///   两次都写同一批键（HasRodOffset / RodOffsetWx / RodOffsetWy）⇒ **后发的覆盖先发的**。
        ///   而「把 b 带进生产」语义上属于**该相机槽**（"这个槽的 H 是杆端域、要不要补 b"），
        ///   不是"这一份档案要不要补"。若只看被发布档案自身：
        ///     先发 H（声明 true → 写 HasRodOffset=true），再发 e（声明 null → 写 HasRodOffset=false）
        ///     ⇒ 开关被**静默抹回 false**，现场勾了也白勾，且没有任何报错。
        ///   故取"或"：窗口内任一档声明了，就认为该槽要 b（开关只会被显式 true 打开，不会被"没声明"关掉）。
        /// ⚠ 反过来说：想关掉它，得把**槽内所有档案**的同名字段都置 false，而不是只改一份。
        /// </summary>
        public bool RodOffsetInProductionDeclared =>
            H?.RodOffsetInProduction == true
            || E?.RodOffsetInProduction == true
            || T?.RodOffsetInProduction == true
            || S?.RodOffsetInProduction == true;

        /// <summary>
        /// ★2026-09-15：H 档案是否【声明已在吸嘴域】（延伸杆的偏心 b 已在标定阶段扣掉）。
        /// 为 true 时消费直接 X_obj = H(u)——不再叠 O、不用 P_photo、不做 U 项。
        /// 未声明（null）时为 false，保持改动前的旧行为（按 PrimaryPath 判）。
        /// </summary>
        public bool IsNozzleDomainH => H?.HandEyeInNozzleDomain == true;

        /// <summary>
        /// ★2026-09-15：吸嘴与 U 轴是否同轴的【声明值】（未声明 = null）。
        /// 声明可能落在 H 档案上，也可能落在吸嘴级 e/t 档案上（取决于标定会话写在哪），
        /// 故按 H → E → T 取第一个非 null 的声明。
        /// </summary>
        public bool? NozzleAxisCoaxialDeclared =>
            H?.NozzleAxisCoaxial ?? E?.NozzleAxisCoaxial ?? T?.NozzleAxisCoaxial;

        /// <summary>
        /// ★2026-09-15：吸嘴与 U 轴同轴（转 U 时吸嘴尖 XY 原地不动）。
        /// 为 true ⇒ 消费**免掉** R(U−U0)·e 这一项（U 只决定姿态）。
        /// 为 false/null（未声明）⇒ 保守保留该项，与改动前行为一致。
        /// </summary>
        public bool NozzleAxisCoaxial => NozzleAxisCoaxialDeclared == true;

        /// <summary>
        /// ★2026-09-15：是否需要 R(U−U0)·e 旋转补偿项。
        /// 同轴吸嘴不需要；未声明时保守按"需要"处理（旧档不受影响）。
        /// </summary>
        public bool NeedsRotationTerm => !NozzleAxisCoaxial;

        /// <summary>是否下相机（仰视二次对位，消费语义与上相机正交）</summary>
        public bool IsDownCamera =>
            H != null && (H.PrimaryPath == CalibrationAcquirePath.DownCameraWalk
                          || H.PrimaryPath == CalibrationAcquirePath.DownCameraPixelRotCenter);

        /// <summary>
        /// ★ 旋转中心 O 的归属档案（2026-09-12 修复）：O 是吸嘴级（ToolRotation）产物，
        /// 标定时写进 e 档案（见向导 ToolRotation 会话落库），并非 H 档案。旧档兼容：e 无 O 时回退 H。
        /// </summary>
        public CalibrationProfile RotationCenterProfile =>
            (E != null && E.HasRotationCenter) ? E
            : (H != null && H.HasRotationCenter) ? H
            : null;

        /// <summary>旋转中心 O 是否齐备（读 e 档案，回退 H，见 RotationCenterProfile）</summary>
        public bool HasRotationCenter => RotationCenterProfile != null;

        /// <summary>旋转中心 O 的 X（H 域/命令位域）；未标定返回 0</summary>
        public double RotationCenterWx => RotationCenterProfile?.ToolCenterWx ?? 0.0;

        /// <summary>旋转中心 O 的 Y（H 域/命令位域）；未标定返回 0</summary>
        public double RotationCenterWy => RotationCenterProfile?.ToolCenterWy ?? 0.0;

        /// <summary>真吸嘴偏心 e 是否齐备</summary>
        public bool HasEcc =>
            E != null && E.IsNozzleEccCalibrated
            && (Math.Abs(E.ToolOffsetPureWx) > 1e-9 || Math.Abs(E.ToolOffsetPureWy) > 1e-9);

        /// <summary>
        /// ★★2026-09-15（第 5 处接缝）：下相机像素旋转中心 R_cdown 的【归属档案】。
        ///
        /// 产出侧：向导在 DownCameraPixelRotCenter(Path=9) 会话里把拟合圆心写进
        ///   **该会话自己的档案** = e/ToolRotation 档案（`CalibrationWizardViewModel` 的
        ///   `TargetProfile.DownRotCenterCol/Row = centerPx/centerPy`，全仓唯一赋值点）。
        /// 消费侧旧实现：只读 `H.DownRotCenter*`，而 H = Path=8 的 HandEye 档案 —— 那个字段
        ///   **从来不会被赋值** ⇒ HasDownRotCenter 恒 false ⇒ 下相机方案永远显示
        ///   "⚠ 未标定像素旋转中心 R_cdown——相对纠偏不可用"，哪怕刚标完。
        ///   形态 = "产物落在 A 档案、消费只认 B 档案"，与 TheMemory 里
        ///   「接缝存在≠被走过」的第三种形状同族（算得出、说不出的变体：写得进、读不着）。
        ///
        /// 解析顺序：H → E。H 在前只为兼容"旧档/手工把 R_cdown 抄进 H"的情形；
        /// 设计归属是 E（Path=9 会话产物），故两者都没有时为 null（真未标）。
        /// </summary>
        public CalibrationProfile DownRotCenterProfile =>
            (H != null && H.DownRotCenterRow.HasValue && H.DownRotCenterCol.HasValue) ? H
            : (E != null && E.DownRotCenterRow.HasValue && E.DownRotCenterCol.HasValue) ? E
            : null;

        /// <summary>
        /// 下相机像素旋转中心 R_cdown 是否齐备。
        /// ★2026-09-15：改为走 <see cref="DownRotCenterProfile"/>（H → E），此前只认 H ⇒ 恒 false。
        /// </summary>
        public bool HasDownRotCenter => DownRotCenterProfile != null;

        // ==================== 口径解析（唯一入口，勿在消费端重抄 switch） ====================

        /// <summary>
        /// ★★2026-09-15：口径判定的**唯一入口**（转发到 <see cref="CalibrationConsumptionContract"/>）。
        ///
        /// 为什么要把判据搬出去：这条判据原先被写在三处（校验台 / 发布链 / 生产引擎），
        /// 三者【靠巧合一致】——现场因此出现"校验台反复压中、一上生产就偏一个 |b|"。
        /// 现在三方都调 CalibrationConsumptionContract.Resolve，谁都不许再自己写一遍 switch。
        /// </summary>
        public ConsumptionDecision ResolveDecision(
            CalibrationSolveMode mode = CalibrationSolveMode.FromProfile)
            => CalibrationConsumptionContract.Resolve(BuildInputs(), mode);

        /// <summary>
        /// 把本门面的 H/E/T/S 摊成口径判定的输入 DTO。
        /// 数值只做"取哪一份档案"的选择，不做任何推断——推断在契约里。
        /// </summary>
        public ConsumptionInputs BuildInputs()
        {
            var rod = CalibrationConsumptionContract.ResolveRodOffset(
                T?.ToolOffsetWx ?? 0.0, T?.ToolOffsetWy ?? 0.0, HasEthToolOffset,
                E?.ToolEccWx ?? 0.0, E?.ToolEccWy ?? 0.0, HasRodOffset);

            return new ConsumptionInputs
            {
                IsDownCamera = IsDownCamera,
                IsEyeInHand = IsEyeInHand,
                HasTruthWalkPath = H?.PrimaryPath == CalibrationAcquirePath.CameraTruthWalk,
                HandEyeInNozzleDomain = H?.HandEyeInNozzleDomain,
                NozzleAxisCoaxial = NozzleAxisCoaxialDeclared,

                // 自动推断用的"标定条件"（声明缺失时才被读到）
                RotationFitRadiusMm = E?.RotationFitRadiusMm ?? H?.RotationFitRadiusMm,
                FeatureTemplateName = E?.FeatureTemplateName ?? H?.FeatureTemplateName,

                HasRotationCenter = HasRotationCenter,
                HasEcc = HasEcc,
                HasToolOffsetDirect = HasEthToolOffset,
                HasRodOffsetCandidate = HasRodOffset,

                RodOffsetWx = rod.Bx,
                RodOffsetWy = rod.By,
                RodOffsetSource = rod.Src,

                PhotoBaseX = RotationCenterProfile?.BasePosX ?? 0.0,
                PhotoBaseY = RotationCenterProfile?.BasePosY ?? 0.0,
                RotCenterWx = RotationCenterWx,
                RotCenterWy = RotationCenterWy,
                EccX = E?.ToolOffsetPureWx ?? 0.0,
                EccY = E?.ToolOffsetPureWy ?? 0.0,
                U0Deg = H?.CalibU0 ?? 0.0,
            };
        }

        /// <summary>
        /// ★2026-09-15：把"口径"解析成四个布尔标志（吸嘴域直吸 / 需 O 补偿 / 带 U 旋转项 / 补 b）。
        /// 兼容旧签名——**内部已改为转发契约**，行为与 <see cref="ResolveDecision"/> 完全一致。
        /// 判据写两遍就会在边界上分叉（"靠巧合正确"），故必须同源。
        /// </summary>
        public (bool NozzleDomain, bool NeedO, bool RotationTerm, bool ToolOffsetTerm) ResolveFlags(
            CalibrationSolveMode mode = CalibrationSolveMode.FromProfile)
        {
            var d = ResolveDecision(mode);
            return (d.NozzleDomain, d.NeedO, d.RotationTerm, d.RodOffsetTerm);
        }

        /// <summary>
        /// ★2026-09-15：固定相机杆端域里 b 的符号（与 ResolveFlags 同属"口径解析唯一入口"，勿在消费端另判）。
        ///   +1 = 吸点 = H(u) + b（本次推导结论：命令到 H(u) 只把【杆端 mark】送到像素上，
        ///        要送【吸嘴尖】得再往 mark→轴 的方向补这段位移）
        ///   −1 = 吸点 = H(u) − b（外部方案第 4/5 步的取法，作为反面对照保留）
        /// </summary>
        public static double RodOffsetSign(CalibrationSolveMode mode) =>
            mode == CalibrationSolveMode.FixedCameraMinusRodOffset ? -1.0 : 1.0;

        // ==================== 核心换算 ====================

        /// <summary>
        /// 像素点 → 工件特征真实位置 X_obj 与吸嘴命令位 P_go（一步到位）。
        ///
        /// ★2026-09-15 分型重写：口径不再只按 PrimaryPath 一刀切，改为四档显式口径
        /// （<see cref="CalibrationSolveMode"/>），默认 FromProfile = 读档案的两个声明：
        ///   · H 已在吸嘴域（HandEyeInNozzleDomain=true）→ 直吸：X_obj=H(u)，吸点=H(u)
        ///   · 杆端域 + 同轴吸嘴（NozzleAxisCoaxial=true）→ X_obj=P_photo+O−H(u)，吸点=X_obj（免 U 项）
        ///   · 杆端域 + 偏心吸嘴                          → 定案式：吸点=X_obj−R(U_go−U0)·e
        ///   · 下相机                                     → 相对纠偏（语义正交，不受本次改动影响）
        /// 修的问题：现场把旋转结果融合进 H 后，平台又按"杆端域"补一遍 O/e ⇒ 双重补偿 + 混入
        /// P_photo ⇒ 校验必然不对。Trace 里给出"另一种口径的对照落点与差值"，便于一眼判分型。
        /// </summary>
        /// <param name="pixelX">像素列 col</param>
        /// <param name="pixelY">像素行 row</param>
        /// <param name="photoX">拍照瞬间机械位 X（需求 O 的口径才用）</param>
        /// <param name="photoY">拍照瞬间机械位 Y</param>
        /// <param name="uGoDeg">作业回转角（偏心吸嘴才用）</param>
        /// <param name="mode">消费口径；FromProfile=跟随档案声明（默认，兼容旧调用）</param>
        public CalibrationSolveResult Solve(double pixelX, double pixelY,
            double photoX, double photoY, double uGoDeg,
            CalibrationSolveMode mode = CalibrationSolveMode.FromProfile)
        {
            var trace = new List<string>();
            trace.Add("[口径] " + DescribeSolveMode(mode));

            if (!IsMatrixReady)
                return CalibrationSolveResult.Fail("未发布九点矩阵 H（HomMatFilePath 缺失）", Join(trace));
            if (_mapper == null)
                return CalibrationSolveResult.Fail("未注入像素→世界映射器", Join(trace));

            // ① 像素 → 矩阵裸输出 w = H(u)
            if (!_mapper(pixelX, pixelY, out double wx, out double wy, out string mapErr))
                return CalibrationSolveResult.Fail("像素映射失败: " + mapErr, Join(trace));

            double u0 = H.CalibU0 ?? 0.0;

            trace.Add($"输入: 像素(col,row)=({pixelX:F1},{pixelY:F1})  P_photo=({photoX:F3},{photoY:F3})"
                      + $"  U_go={uGoDeg:F2}°  U0={u0:F2}°");
            trace.Add($"矩阵: H(u)=({wx:F3},{wy:F3})mm  档案「{H?.Name}」槽={SlotKey}"
                      + $"  Path={(H?.PrimaryPath.HasValue == true ? H.PrimaryPath.Value.ToString() : "未记录")}");

            // —— 下相机：相对纠偏（不做绝对定位；与上相机绝对定位语义正交，本分支未改）——
            if (IsDownCamera)
            {
                // ★2026-09-15：R_cdown 改从【归属档案】取（H → E）。e/Path=9 档案才是它的设计归属，
                //   旧实现只读 H 导致"刚标完仍报缺 R_cdown"（详见 DownRotCenterProfile 注释）。
                var rcp = DownRotCenterProfile;
                if (rcp == null)
                    return CalibrationSolveResult.Fail(
                        "下相机未标定像素旋转中心 R_cdown，相对纠偏不可用"
                        + "（已查 H 与 e/Path=9 两处归属档案，均为 null）", Join(trace));
                trace.Add($"R_cdown 来源: 档案「{rcp.Name}」(Quantity={rcp.Quantity}"
                          + $"  Path={(rcp.PrimaryPath.HasValue ? rcp.PrimaryPath.Value.ToString() : "未记录")}"
                          + $"  槽={CalibrationProfileSessionPlanner.GuessSlotKey(rcp)})"
                          + $"  =({rcp.DownRotCenterCol.Value:F2},{rcp.DownRotCenterRow.Value:F2})px");
                if (!_mapper(rcp.DownRotCenterCol.Value, rcp.DownRotCenterRow.Value,
                        out double rotWx, out double rotWy, out string rotErr))
                    return CalibrationSolveResult.Fail("像素旋转中心映射失败: " + rotErr, Join(trace));
                var d = CalibrationGeometry.DownCameraOffset(wx, wy, rotWx, rotWy);
                double dcx = photoX - d.Dx;
                double dcy = photoY - d.Dy;
                trace.Add($"下相机: R_cdown=({rotWx:F3},{rotWy:F3})  δ=({d.Dx:F3},{d.Dy:F3})  吸点=({dcx:F3},{dcy:F3})");
                return CalibrationSolveResult.Ok(objX: dcx, objY: dcy, cmdX: dcx, cmdY: dcy,
                    mode: $"下相机相对纠偏：δ=H_down(R_img)−H_down(R_cdown)，吸嘴反向移动 δ 让工件居中"
                          + $"（R_cdown 源「{rcp.Name}」）",
                    trace: Join(trace));
            }

            // —— 上相机：按生效口径分型（解析入口唯一：CalibrationConsumptionContract）——
            var dec = ResolveDecision(mode);
            var inp = BuildInputs();
            bool nozzleDomain = dec.NozzleDomain;
            bool needO = dec.NeedO;
            bool rotTerm = dec.RotationTerm;

            trace.Add($"档案声明: 吸嘴域H={YesNo(IsNozzleDomainH)}"
                      + $"  同轴={TriState(NozzleAxisCoaxialDeclared)}"
                      + $"  | 眼在手EIH={YesNo(IsEyeInHand)}"
                      + $"  | 聚合: O={YesNo(HasRotationCenter)} e={YesNo(HasEcc)}"
                      + $"  b杆端={YesNo(HasRodOffset)}"
                      + $"  t对针={YesNo(HasToolOffset)}({ToolOffsetKind})");
            trace.Add($"生效口径: 吸嘴域直吸={YesNo(nozzleDomain)}"
                      + $"  需O补偿={YesNo(needO)}  带U旋转项={YesNo(rotTerm)}"
                      + $"  固定相机杆端补b={YesNo(dec.RodOffsetTerm)}"
                      + (dec.RodOffsetTerm ? $"（b符号={dec.RodOffsetSign:+0;-0;}）" : ""));
            // ★2026-09-15：把"为什么判成这一档"摊开写进 Trace（自动判定/人工强制 + 判据链 + 阻断项）。
            //   现场不对时，读这段就知道是"声明错"还是"聚合丢产物"还是"公式分型错"，不必再靠推演。
            trace.Add("判据链: " + dec.Basis);
            foreach (var blk in dec.Blockers) trace.Add("⛔ " + blk);
            foreach (var wn in dec.Warnings) trace.Add("⚠ " + wn);

            double objX, objY, cmdX, cmdY;
            string modeText;

            if (nozzleDomain)
            {
                var o = CalibrationConsumptionContract.ResolveObjectBase(inp, dec, wx, wy, trace.Add);
                var c = CalibrationConsumptionContract.ResolveCommand(inp, dec, o.X, o.Y, uGoDeg, trace.Add);
                objX = o.X; objY = o.Y; cmdX = c.X; cmdY = c.Y;
                modeText = "吸嘴域直吸：X_obj=H(u)，吸点=H(u)（H 已消杆；不叠 O、不用 P_photo、无 U 项）";
                trace.Add($"结果: X_obj=({objX:F3},{objY:F3})  吸点=({cmdX:F3},{cmdY:F3})");
                trace.Add(BuildContrast(wx, wy, cmdX, cmdY, photoX, photoY, uGoDeg, u0));
            }
            else if (!needO)
            {
                objX = wx; objY = wy;
                if (dec.RodOffsetTerm)
                {
                    // ★2026-09-15（二修）：固定相机 + 杆端域 H（延伸杆辅助标定）。
                    //   吸嘴命令 = H(u) + b，b = "杆端 mark → 吸嘴尖"的固定位移（同心吸嘴在轴上 ⇒ 与 U 无关）。
                    //   b 的两条来源，优先级 = 直接量 > 推算：
                    //     ① 对针 t（EyeToHandImage 语义 δ=M_tool−H(A')）——同一物理量的直接测量，符号为 +
                    //     ② 旋转标定的杆端偏心 ToolEccW —— 旋转标定【顺带就测出来了】，不用额外上机
                    //   ⚠ 不能用 e（ToolOffsetPureW）去推 b：e 是"吸嘴偏心"，同心吸嘴恒为 0，方向都不同。
                    //   ⚠ EyeInHandIndirect 的 TCO = H(u)−R_n 符号相反（属 EIH 分支），本分支不许采用。
                    //   ★算式已交给契约（与生产引擎同一函数），此处只负责把过程写进 Trace。
                    string bSrc = inp.RodOffsetSource;
                    // 契约里的 b 已经按"t 优先"解析好了（ResolveRodOffset），此处直接用
                    double braw = Math.Sqrt(inp.RodOffsetWx * inp.RodOffsetWx + inp.RodOffsetWy * inp.RodOffsetWy);

                    if (bSrc != null && braw > 1e-9)
                    {
                        var o = CalibrationConsumptionContract.ResolveObjectBase(inp, dec, wx, wy, trace.Add);
                        var c = CalibrationConsumptionContract.ResolveCommand(inp, dec, o.X, o.Y, uGoDeg, trace.Add);
                        objX = o.X; objY = o.Y; cmdX = c.X; cmdY = c.Y;

                        double sgn = dec.RodOffsetSign;
                        double bx = sgn * inp.RodOffsetWx, by = sgn * inp.RodOffsetWy;
                        double bb = Math.Sqrt(bx * bx + by * by);
                        trace.Add($"杆端→吸嘴偏移: b=({bx:F3},{by:F3})mm  |b|={bb:F3}mm  来源[{bSrc}]"
                                  + (sgn < 0 ? "（符号 −1：反面对照口径）" : ""));
                        modeText = $"固定相机+延伸杆：吸点 = H(u) {(sgn < 0 ? "-" : "+")} b = ({cmdX:F3},{cmdY:F3})，"
                                 + $"b=({bx:F3},{by:F3})mm |b|={bb:F3}mm（{bSrc}）";
                        if (!HasEthToolOffset)
                        {
                            trace.Add("ℹ b 取的是推算值（ToolEccW = 像素偏心矢经 H 映射）："
                                      + "⚠ 平台自测的\"像素定圆再映射 ≈8mm\"是【O】的偏差，b 不受影响"
                                      + "（b 是同一映射的两点相减，常数项抵消）；b 的残差≈各向异性差分残差(1~3mm)。"
                                      + "要亚毫米精度请到【对针专窗】做一次 EyeToHand 图像对针，b 会自动改用直量的 t。");
                            if (ToolOffsetKind == ToolOffsetMethod.EyeInHandIndirect)
                            {
                                trace.Add("⚠ 档案里的对针量是 EyeInHandIndirect 的 TCO(= H(u)−R_n，符号相反)，本分支已忽略它。");
                            }
                        }
                    }
                    else
                    {
                        cmdX = objX; cmdY = objY;   // 退化：吸点=H(u)（明确告警，落点不可信）
                        modeText = "⚠ 固定相机+延伸杆但【既无对针 t、也无杆端偏心 b】：吸点=H(u)，落点仍偏【杆端 r】——"
                                 + "请先在旋转标定(或对针)里测出『杆端 mark→吸嘴轴』的偏移，再回到校验台。";
                        trace.Add("⚠ 无 b：吸点=H(u)，杆端偏距未补，落点不可信（≠换算错误，是标定产物缺 b）");
                        trace.Add("   取 b 的两条路：① 对针专窗做 EyeToHand 图像对针（直量，最准）；"
                                  + "② 重跑一次旋转中心标定（其 ToolEccW 就是 b，顺带得到；残差≈1~3mm，非 8mm——8mm 是 O 的偏差）。");
                    }
                }
                else
                {
                    cmdX = wx; cmdY = wy;
                    modeText = "ETH 直接拍工件本体：X_obj=H(u)，吸点=H(u)（工具偏距已吸收进 H）";
                }
                trace.Add($"结果: X_obj=({objX:F3},{objY:F3})  吸点=({cmdX:F3},{cmdY:F3})");
            }
            else
            {
                string scene = IsEyeInHand ? "EIH 眼在手" : "固定相机+延伸杆标定";
                if (!HasRotationCenter)
                {
                    objX = wx; objY = wy; cmdX = wx; cmdY = wy;
                    trace.Add("⚠ 退化: 需 O 补偿但档案无旋转中心 O → 退化为视觉直吸，落点不可信");
                    return CalibrationSolveResult.Ok(objX, objY, cmdX, cmdY,
                        mode: $"⚠ {scene} 缺旋转中心 O——退化为视觉直吸（落点不可信，请先补旋转标定求 O）",
                        trace: Join(trace));
                }

                // ★算式交给契约（与生产引擎 PickAnchor / MoveToWorkAsync 同一函数），此处只留 Trace 文案
                var o = CalibrationConsumptionContract.ResolveObjectBase(inp, dec, wx, wy, null);
                objX = o.X; objY = o.Y;
                trace.Add($"杆端域: O=({RotationCenterWx:F3},{RotationCenterWy:F3})"
                          + $"(来源「{RotationCenterProfile?.Name}」)  X_obj=P_photo+O−H(u)=({objX:F3},{objY:F3})");

                var c = CalibrationConsumptionContract.ResolveCommand(inp, dec, objX, objY, uGoDeg, null);
                cmdX = c.X; cmdY = c.Y;

                if (dec.RotationTerm && HasEcc)
                {
                    trace.Add($"偏心吸嘴: e=({E.ToolOffsetPureWx:F3},{E.ToolOffsetPureWy:F3})"
                              + $"  吸点=X_obj−R({uGoDeg - u0:F2}°)·e=({cmdX:F3},{cmdY:F3})");
                    modeText = $"{scene} 定案式：X_obj=P_photo+O−H(u)，吸点=X_obj−R(U−U0)·e";
                }
                else if (!dec.RotationTerm)
                {
                    trace.Add("同轴吸嘴: 免 R(U−U0)·e 项（吸嘴在轴上，转 U 时 XY 不动）");
                    modeText = $"{scene} 同轴吸嘴：X_obj=P_photo+O−H(u)，吸点=X_obj（免 U 旋转项）";
                }
                else
                {
                    trace.Add("⚠ 无偏心 e: 吸点=特征真位，仍偏杆端 r —— 请补对针求 e");
                    modeText = $"{scene}：X_obj=P_photo+O−H(u)（无偏心 e，吸点=特征真位，仍偏杆端 r）";
                }
                trace.Add($"结果: 吸点=({cmdX:F3},{cmdY:F3})");
            }

            return CalibrationSolveResult.Ok(objX, objY, cmdX, cmdY, modeText, Join(trace));
        }

        // ==================== 诊断辅助（Trace 用） ====================

        private static string Join(List<string> lines) => string.Join("\n", lines);

        private static string YesNo(bool b) => b ? "是" : "否";

        private static string TriState(bool? b) => !b.HasValue ? "未声明" : (b.Value ? "是" : "否");

        private static string DescribeSolveMode(CalibrationSolveMode m)
        {
            if (m == CalibrationSolveMode.NozzleDomainDirect) return "吸嘴域直吸（界面强制）";
            if (m == CalibrationSolveMode.RodEndWithRotation) return "杆端域 + O/e 完整补偿（界面强制，EIH 式）";
            if (m == CalibrationSolveMode.RodEndNoRotation) return "杆端域 + O 补偿、免 U 旋转项（界面强制，EIH 式）";
            if (m == CalibrationSolveMode.FixedCameraPlusRodOffset) return "固定相机 + b 正向：吸点=H(u)+b（界面强制）";
            if (m == CalibrationSolveMode.FixedCameraMinusRodOffset) return "固定相机 + b 反向：吸点=H(u)−b（界面强制·反面对照）";
            return "跟随档案声明（FromProfile）";
        }

        /// <summary>
        /// 反面对照：同一个像素点若改走"完整 O/e 补偿"口径，吸点会落到哪、差多少。
        /// 用途：日志里一眼判定"口径分型到底影不影响落点"——
        ///   Δ≈0 说明两种口径等价（选哪个都对）；Δ 几毫米说明分型直接决定落点，声明错了必错。
        /// 这也是本轮"分型怀疑"的判据本身：不靠推演，靠两个口径的实际差值说话。
        /// </summary>
        private string BuildContrast(double wx, double wy, double directX, double directY,
            double photoX, double photoY, double uGoDeg, double u0)
        {
            if (!HasRotationCenter)
                return "对照: 档案无 O → 分不出 O/e 口径（此时分型无风险）";
            var o = CalibrationGeometry.ObjectBaseV2(wx, wy, photoX, photoY,
                RotationCenterWx, RotationCenterWy, needsOCompensation: true);
            double tx = o.X, ty = o.Y;
            string eccTxt = "（无 e，吸点=X_obj）";
            if (HasEcc)
            {
                var c = CalibrationGeometry.CommandFor(o.X, o.Y,
                    E.ToolOffsetPureWx, E.ToolOffsetPureWy, uGoDeg, u0);
                tx = c.X; ty = c.Y;
                eccTxt = $"再减 R({uGoDeg - u0:F2}°)·e";
            }
            return $"对照: 若改走 O/e 口径 → 吸点=({tx:F3},{ty:F3}) {eccTxt}"
                   + $"  Δ=({tx - directX:F3},{ty - directY:F3})mm"
                   + "  ← Δ 大即说明口径分型直接决定落点，请核对档案声明";
        }

        // ==================== 静态聚合工厂 ====================

        /// <summary>
        /// 从一组 CalibrationProfile 聚合出某 (工位, 槽) 的消费门面。
        /// 聚合规则（按物理量 Quantity 分型，与 BuildArtifactId 的粒度一致）：
        ///   H/s/畸变 → 槽级（SlotKey 匹配）；e/t → 吸嘴级（先同槽、找不到再全工位+未绑工位兜底，
        ///   借用来源写入 AggregationNotes，按 UpdatedAt 最近优先）。
        ///   ★2026-09-15：兜底池排除【显式声明了别的槽】的档案——槽不同即"另一台相机的产物"，
        ///   借进来就是张冠李戴（复合工位上下相机）。被拒的档案也写进 notes，不安静丢。
        /// 兼容旧档案（Quantity 未显式声明时按结果字段反推）。
        /// </summary>
        /// <param name="profiles">同一数据源（通常 ICalibrationService.GetAllProfiles()）</param>
        /// <param name="stationCode">工位代码</param>
        /// <param name="slotKey">相机槽键（Cam_A / Cam_C …）</param>
        /// <param name="mapper">像素→世界映射器（注入 ICalibrationService.MapPixelToWorld 包装）</param>
        /// <returns>门面（H 可能为 null，表示该槽尚未发布九点矩阵）</returns>
        public static CameraCalibrationBundle Build(
            IEnumerable<CalibrationProfile> profiles,
            string stationCode, string slotKey,
            PixelMapper mapper)
        {
            var list = profiles?.Where(p => p != null).ToList() ?? new List<CalibrationProfile>();
            string station = string.IsNullOrWhiteSpace(stationCode) ? "" : stationCode.Trim();
            string slot = string.IsNullOrWhiteSpace(slotKey) ? "Cam_01" : slotKey.Trim();

            // 该工位所有档案（宽松：工位码空则匹配所有）
            var inStation = list.Where(p =>
                string.IsNullOrWhiteSpace(station)
                || string.Equals(p.BoundStationCode, station, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 槽匹配：显式槽字段优先，否则 GuessSlotKey（与迁移器/规划器同口径）
            IEnumerable<CalibrationProfile> InSlot()
            {
                return inStation.Where(p =>
                    string.Equals(CalibrationProfileSessionPlanner.GuessSlotKey(p), slot,
                        StringComparison.OrdinalIgnoreCase));
            }
            var slotProfiles = InSlot().ToList();

            CalibrationProfile Pick(Func<CalibrationProfile, bool> pred) =>
                slotProfiles.Where(pred).OrderByDescending(p => p.UpdatedAt).FirstOrDefault();

            var notes = new List<string>();

            // H：★2026-09-15 严格化（根因修复）——只认【声明 Quantity==HandEye】的档案。
            //
            // 旧写法：`Quantity==HandEye || (有 HomMatFilePath && 非 s/畸变)`，再按 UpdatedAt 取最新。
            // 坑（实测 Cam_A，有数字）：e/t 档案为了"自包含"会把 H 的矩阵路径拷进自己的
            //   HomMatFilePath（见向导 e 会话落库），于是它也满足谓词；而 Pick 取 UpdatedAt 最新——
            //   Cam_A 的 e 档案 17:00:43 比 H 档案 16:56:07 晚 4.5 分钟 ⇒ **H 实际指向 e 档案**。
            //   后果链：口径分型读的是 H.PrimaryPath ⇒ 读到 5(RotateCameraView 旋转会话)
            //   而不是 1(CameraTruthWalk 九点) ⇒ "固定相机+延伸杆"被静默判成"直接拍工件(直吸)"
            //   ⇒ 少补 b=杆端偏心 106.39mm，且日志一切正常（H=已标、RMS 也对，因为矩阵文件同一个）。
            //   ⇒ 现象与"档案声明写错"一模一样，只在聚合这一层——所以必须卡死声明，不许靠"有没有矩阵"猜。
            var h = Pick(p => p.Quantity == CalibrationQuantity.HandEye);
            if (h != null && string.IsNullOrWhiteSpace(h.HomMatFilePath))
            {
                // 声明了 H 但还没挂矩阵：交给下面的兜底，避免"有档案却用不了"
                notes.Add($"槽内 H 档案「{h.Name}」未挂矩阵文件(HomMatFilePath 空)，已跳过它继续找可用矩阵");
                h = null;
            }
            if (h == null)
            {
                // 兜底（仅老档案 Quantity 未落盘时）：必须排除 e/t/s/畸变——它们同样可能带矩阵路径
                h = Pick(p => !string.IsNullOrWhiteSpace(p.HomMatFilePath)
                              && p.Quantity != CalibrationQuantity.PixelScale
                              && p.Quantity != CalibrationQuantity.LensDistortion
                              && p.Quantity != CalibrationQuantity.ToolRotation
                              && p.Quantity != CalibrationQuantity.ToolOffset);
                if (h != null)
                {
                    notes.Add($"⚠ H 由【兜底】命中「{h.Name}」(Quantity={h.Quantity} 未声明为 HandEye)——"
                              + "口径分型(PrimaryPath)取自这份档案，若它其实是 e/t 档案，补偿会整体走错，请核对");
                }
            }

            // ── e/t：吸嘴级产物，聚合口径 ──
            // 先在同槽找（Pick）；找不到再【全工位 + 未绑工位档案】兜底。背景（2026-09-15 复合工位）：
            // e 标定会话的槽键/工位码与 H 不一致时，旧逻辑静默丢 e → 校验台永远显示 H-only。
            // 兜底借用必须显式记录来源（AggregationNotes），由消费端打进日志——不安静借、不安静丢。
            // 注意：兜底池不把"未绑工位"档案混进 inStation 本体（H/s 槽级产物保持严格），只对吸嘴级放宽。

            // ★★2026-09-15（复合工位，第 5 处"同工位≠同相机"）：借用池必须排除
            //   【显式声明了别的槽】的档案。
            //   槽外兜底的本意是救"老档案槽键/工位码没写"（GuessSlotKey 只能猜 Cam_01）——
            //   那种情况下"槽未知"，借来还能由消费端自述来源。但旧谓词不区分
            //   「槽没写」与「槽写的是别的相机」⇒ 复合工位上 Cam_C 的 bundle 会借到
            //   **Cam_A 的 e**（其 ToolEccW 是 Cam_A 的杆端偏心 b≈106mm）并静默当成本槽产物，
            //   Cam_C 的 e 还没标时更会一直"有值"。判据：显式槽 ≠ 目标槽 ⇒ 拒绝借用。
            //   拒绝也要留痕（不安静丢）：把被拒档案写进 notes，现场一眼能看出"为什么没借到"。
            bool DeclaresOtherSlot(CalibrationProfile p)
                => !string.IsNullOrWhiteSpace(p.CameraSlotKey)
                   && !string.Equals(p.CameraSlotKey.Trim(), slot, StringComparison.OrdinalIgnoreCase);
            List<string> CrossSlotRefused(Func<CalibrationProfile, bool> pred, string qty)
            {
                var refused = inStation.Concat(list.Where(p => string.IsNullOrWhiteSpace(p.BoundStationCode)))
                    .Where(p => !slotProfiles.Contains(p) && DeclaresOtherSlot(p) && pred(p))
                    .Select(p => $"「{p.Name}」(槽={p.CameraSlotKey.Trim()})")
                    .Distinct().ToList();
                return refused.Count == 0 ? null
                    : new List<string> { $"⚠ 有 {qty} 档案明确属于其它相机槽，已拒绝跨槽借用（槽不同 ⇒ 是另一台相机的产物，不是同一个量）："
                                         + string.Join("、", refused) };
            }

            var ePred = (Func<CalibrationProfile, bool>)(p => p.Quantity == CalibrationQuantity.ToolRotation
                              || p.IsNozzleEccCalibrated
                              || (Math.Abs(p.ToolOffsetPureWx) > 1e-9
                                  || Math.Abs(p.ToolOffsetPureWy) > 1e-9));
            var e = Pick(ePred);
            if (e == null)
            {
                var pool = inStation.Concat(list.Where(p =>
                        string.IsNullOrWhiteSpace(p.BoundStationCode)))
                    .Where(p => !slotProfiles.Contains(p) && !DeclaresOtherSlot(p));
                e = pool.Where(ePred).OrderByDescending(p => p.UpdatedAt).FirstOrDefault();
                if (e != null)
                    notes.Add($"e 从槽外档案「{e.Name}」借用（工位={e.BoundStationCode ?? "(未绑)"}，槽={CalibrationProfileSessionPlanner.GuessSlotKey(e)}）——请核对该档案确属本工位吸嘴，必要时回向导补绑工位/槽");
                else
                {
                    var refused = CrossSlotRefused(ePred, "e");
                    if (refused != null) notes.AddRange(refused);
                }
            }

            var tPred = (Func<CalibrationProfile, bool>)(p => p.Quantity == CalibrationQuantity.ToolOffset
                              || p.IsToolOffsetCalibrated
                              || (Math.Abs(p.ToolOffsetWx) > 1e-9
                                  || Math.Abs(p.ToolOffsetWy) > 1e-9));
            var t = Pick(tPred);
            if (t == null)
            {
                var pool = inStation.Concat(list.Where(p =>
                        string.IsNullOrWhiteSpace(p.BoundStationCode)))
                    .Where(p => !slotProfiles.Contains(p) && !DeclaresOtherSlot(p));
                t = pool.Where(tPred).OrderByDescending(p => p.UpdatedAt).FirstOrDefault();
                if (t != null)
                    notes.Add($"t 从槽外档案「{t.Name}」借用（工位={t.BoundStationCode ?? "(未绑)"}，槽={CalibrationProfileSessionPlanner.GuessSlotKey(t)}）——请核对归属");
                else
                {
                    var refused = CrossSlotRefused(tPred, "t");
                    if (refused != null) notes.AddRange(refused);
                }
            }

            var s = Pick(p => p.Quantity == CalibrationQuantity.PixelScale);

            return new CameraCalibrationBundle(station, slot, h, e, t, s, mapper)
            {
                AggregationNotes = notes.Count > 0 ? string.Join("；", notes) : null
            };
        }
    }

    /// <summary>
    /// ★2026-09-15：消费口径（决定像素→吸点的算式分型）。
    /// 存在意义：同一份 H 档案，"它落在哪个域"决定要不要叠 O/e——
    /// 分错直接差几毫米，且 RMS 抓不到（融合错了只是整体平移常量）。
    /// 所以口径必须能在界面上强制指定，用于 A/B 对照实测，而不是只能猜档案。
    /// </summary>
    public enum CalibrationSolveMode
    {
        /// <summary>跟随档案声明（默认；HandEyeInNozzleDomain / NozzleAxisCoaxial）</summary>
        FromProfile = 0,

        /// <summary>强制按"H 已在吸嘴域"直吸：X_obj=H(u)，吸点=H(u)（不叠 O、不用 P_photo、无 U 项）</summary>
        NozzleDomainDirect = 1,

        /// <summary>强制按"杆端域 + 完整 O/e 补偿"：吸点 = P_photo+O−H(u) − R(U−U0)·e</summary>
        RodEndWithRotation = 2,

        /// <summary>强制按"杆端域 + O 补偿、免 U 旋转项"（同轴吸嘴在杆端域）：吸点 = P_photo+O−H(u)</summary>
        RodEndNoRotation = 3,

        /// <summary>
        /// ★2026-09-15：强制"固定相机 + 延伸杆"的【正向】：吸点 = H(u) + b。
        /// b = 杆端 mark → 吸嘴尖（同心吸嘴 = 杆端相对回转轴的偏心），来自对针 t 或 ToolEccW。
        /// ⚠ 与 RodEndNoRotation(②) 的区别：②是 EIH 的"叠 O"式，本项是固定相机的"加 b"式——
        ///   二者数学形式不同、物理前提不同，混用就是这次现场踩的坑。
        /// </summary>
        FixedCameraPlusRodOffset = 4,

        /// <summary>
        /// ★2026-09-15：强制"固定相机 + 延伸杆"的【反向】：吸点 = H(u) − b。
        /// 存在意义 = 反面对照：现场用同一点分别走④/⑤，只有一个是"吸嘴正好落在点上"，
        /// 另一个会落在 2|b|（≈213mm）之外的反方向 ⇒ 符号一次判死，不必靠推演。
        /// </summary>
        FixedCameraMinusRodOffset = 5,
    }

    /// <summary>
    /// 门面换算结果：obj=工件特征真位，cmd=吸嘴命令位，mode=分型说明（日志/提示用）。
    /// </summary>
    public class CalibrationSolveResult
    {
        public bool Success { get; private set; }
        public double ObjX { get; private set; }
        public double ObjY { get; private set; }
        public double CmdX { get; private set; }
        public double CmdY { get; private set; }
        public string Mode { get; private set; }
        public string Error { get; private set; }

        /// <summary>
        /// ★2026-09-15：换算过程逐行留痕（口径 → 输入 → 矩阵 → 声明 → 生效口径 → 结果 → 反面对照）。
        /// 用途：现场不对时直接读这串就能定位是"档案声明错"还是"聚合丢产物"还是"公式分型错"，
        /// 不必再靠推演。消费端应把它整段打进日志（保持换行，便于比对）。
        /// </summary>
        public string Trace { get; private set; }

        public static CalibrationSolveResult Ok(double objX, double objY, double cmdX, double cmdY, string mode) =>
            Ok(objX, objY, cmdX, cmdY, mode, null);

        public static CalibrationSolveResult Ok(double objX, double objY, double cmdX, double cmdY,
            string mode, string trace) =>
            new CalibrationSolveResult
            {
                Success = true, ObjX = objX, ObjY = objY, CmdX = cmdX, CmdY = cmdY,
                Mode = mode, Trace = trace
            };

        public static CalibrationSolveResult Fail(string error) => Fail(error, null);

        public static CalibrationSolveResult Fail(string error, string trace) =>
            new CalibrationSolveResult { Success = false, Error = error, Trace = trace };
    }
}
