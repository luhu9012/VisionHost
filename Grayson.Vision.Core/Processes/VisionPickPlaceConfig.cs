namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 「通用视觉定位取放」业务过程配置 —— VisionPickPlace（2026-09-06 建档，替代复制专用过程）。
    ///
    /// 设计定位（平台边界设计 T 任务模板 PICK-PLACE 的过程侧落地）：
    ///   本类把 MahjongPick（ZMC 龙门镜像）与 MahjongDualNozzle（Epson SCARA 双吸嘴）
    ///   两套专用过程中的「可配置运动节拍」收敛为一张配置表；视觉定位段仍由配方
    ///   （AcquireImage → ShapeMatch → CalibrationApply）编排，换产品只动配方不动本类。
    ///
    /// 支持的机台形态（通过字段组合表达，无需新增 C# 类）：
    ///   A. Epson 4 轴 SCARA + 双吸嘴（S2 MahjongDualNozzle 同款）：UseSingleNozzle=false，
    ///      每吸嘴偏心 Ecc1/Ecc2 ≠ 0，U 轴回转中心=吸嘴中心带偏距，双目标按节距推算。
    ///   B. Epson 4 轴 SCARA + 单同心吸嘴（S3 新机）：UseSingleNozzle=true + Ecc1=0 ——
    ///      吸嘴中心与 U 轴回转中心/相机中心重合，吸取点即工件中心，无偏心补偿。
    ///   C. 无 U 轴龙门/XYZ（MahjongPick 迁移预留）：AxisU 保留但角度相关开关全关，
    ///      由 MahjongPickProcess 保持现有现场行为，待镜像逻辑确认后按需迁移。
    ///
    /// 角度策略（放料前统一工件姿态，三种互斥组合）：
    ///   1. 固定角归正（策略一）：EnableVisionAngleCorrection=false → U 统一转到 PlaceU 再放，
    ///      来料姿态固定/正位时使用（MahjongDualNozzle 现状）。
    ///   2. 视觉实测角归正（策略二）：EnableVisionAngleCorrection=true →
    ///      U_place = PlaceU − Sign·(MatchAngle − RefAngleDeg)。来料姿态随机时，读取
    ///      ShapeMatch.MatchAngle 端口按实测旋转补偿，使工件按 RefAngleDeg 参考姿态落盘。
    ///      ⚠ 矩形/方向性工件有 90° 歧义（0°/90°/180°/270° 形状同形）：若要求"字朝向一致"
    ///      需在配方尾部叠 OCR/颜色朝向判别；纯形状匹配只能归正到 90° 粒度内的最近姿态。
    ///   3. 下相机检知归正（S3 组合工位，v1.1 预留）：固定下相机检知吸起工件姿态后二次归正，
    ///      需要第二段视觉链支持（配方暂单链线性），引擎 v1 不实现，见任务模板族文档。
    ///
    /// 视觉链契约（与 MahjongDualNozzle 一致，工位绑定配方必须含）：
    ///   AcquireImage(相机别名=配方内选择，可眼在手/固定相机) → ShapeMatch(工件模板) → CalibrationApply(像素→机器人坐标)
    ///   每周期视觉流只输出「一个最佳匹配目标」（世界系工件中心 w + 匹配分数），
    ///   第二目标（双吸嘴）由 第一目标 + TilePitchX/Y 阵列节距推算。
    ///
    /// 轴号约定（默认 EpsonRobot：0=X 1=Y 2=Z 3=U，可覆盖）。
    /// 字段语义与 MahjongDualNozzleConfig 对齐（吸放式旋转标定偏心体系，2026-09-03 重构后唯一口径）。
    /// </summary>
    public class VisionPickPlaceConfig
    {
        // ============================================================
        // 设备绑定
        // ============================================================

        /// <summary>机械手在工位管理中绑定的逻辑别名（EpsonRobot / MotionCard…）</summary>
        public string CardAlias { get; set; } = "EpsonRobot";

        // ============================================================
        // 任务形态（一次循环搬运几个目标）
        // ============================================================

        /// <summary>
        /// 单目标模式开关。
        /// - true  = 一次循环搬运 1 个目标（单吸嘴同心 S3 / 单吸嘴 S2 调试形态）；
        /// - false = 双吸嘴形态：一次循环搬运 2 个目标（第 2 目标 = 第 1 目标 + 节距推算）。
        /// </summary>
        public bool UseSingleNozzle { get; set; } = true;

        // ============================================================
        // 轴号（默认 Epson 约定：0=X 1=Y 2=Z 3=U）
        // ============================================================
        public int AxisX { get; set; } = 0;
        public int AxisY { get; set; } = 1;
        public int AxisZ { get; set; } = 2;
        public int AxisU { get; set; } = 3;

        /// <summary>吸取时 U 轴姿态角（°）。同心吸嘴吸取与姿态无关，任意；偏心吸嘴=吸嘴朝向。</summary>
        public float WorkU { get; set; } = 0f;

        /// <summary>
        /// 放料目标姿态角（°）：吸住工件后、放料前把 U 转到该角度（角度策略关闭时的统一落点角）。
        /// 开启视觉实测角归正时，最终转角 = PlaceU − Sign·(MatchAngle − RefAngleDeg)。
        /// </summary>
        public float PlaceU { get; set; } = 0f;

        /// <summary>视觉实测角归正的「参考角」θ_ref（°）：模板建时的正位姿态角，通常 0。</summary>
        public float RefAngleDeg { get; set; } = 0f;

        /// <summary>
        /// 视觉实测角归正开关（角度策略二）。
        /// - false = 固定角归正：U 直接转 PlaceU（来料姿态固定/正位）；
        /// - true  = 读取 ShapeMatch.MatchAngle，放料角 U_place = PlaceU − Sign·(MatchAngle − RefAngleDeg)，
        ///   使随机姿态工件按 RefAngleDeg 参考姿态落盘。
        /// 前置：视觉链 ShapeMatch 必须存在且输出 MatchAngle 端口（模板匹配自动带角度）。
        /// </summary>
        public bool EnableVisionAngleCorrection { get; set; } = false;

        /// <summary>
        /// 角度归正方向符号（±1）。相机朝下拍（常规上相机）= 视觉角度与 U 增量同向时取 +1；
        /// 相机朝上拍（固定下相机检知吸起工件）视轴镜像取 −1；符号错误表现为"越归正越歪"，
        /// 现场在示教面板上翻转该值即可，无需改代码。
        /// </summary>
        public float AngleCorrectionSign { get; set; } = 1f;

        // ============================================================
        // 吸嘴几何（2026-09-08 定案，同 MahjongDualNozzleConfig）
        // ============================================================
        //   只认【一个】几何量：Ecc = 真吸嘴偏心 e（U0=ToolAlignU 参考）
        //     e = O − H(p_tip)：O 来自三点定圆旋转标定，p_tip 来自一次物理对针；
        //     ★ 不需要同心短杆；★ 偏心延伸杆测出的 ToolEccW 是杆末端偏心，不是 e，别混用。
        //   ⚠ 下面的 TCO 字段已废弃（发布时清零），仅为兼容旧档保留，不得参与计算。
        // 消费（唯一真源 CalibrationGeometry，与校验台同口径）：
        //   X_obj = P_photo + O − H(u)   （EIH 眼在手）
        //   X_obj = H(u)                 （ETH 固定相机）
        //   C     = X_obj − R(姿态U − U0)·Ecc   （吸取 armU=WorkU，放料 armU=U_place）

        /// <summary>吸嘴1 真吸嘴偏心 e 世界 X（mm，U0=ToolAlignU 参考；同心吸嘴=0；★偏心延伸杆测得 ToolEccW 是杆末端偏心，不是 e）</summary>
        public float Nozzle1EccX { get; set; } = 0f;
        /// <summary>吸嘴1 真吸嘴偏心 e 世界 Y（mm，U0 参考）</summary>
        public float Nozzle1EccY { get; set; } = 0f;
        /// <summary>吸嘴2 真吸嘴偏心 e 世界 X（mm，U0 参考）</summary>
        public float Nozzle2EccX { get; set; } = 0f;
        /// <summary>吸嘴2 真吸嘴偏心 e 世界 Y（mm，U0 参考）</summary>
        public float Nozzle2EccY { get; set; } = 0f;

        /// <summary>【已废弃 2026-09-08】旧 TCO 字段，发布时一律清零，仅兼容旧档读取，禁止参与计算</summary>
        public float Nozzle1TcoX { get; set; } = 0f;
        /// <summary>【已废弃 2026-09-08】旧 TCO 字段，禁止参与计算</summary>
        public float Nozzle1TcoY { get; set; } = 0f;
        /// <summary>【已废弃 2026-09-08】旧 TCO 字段，禁止参与计算</summary>
        public float Nozzle2TcoX { get; set; } = 0f;
        /// <summary>【已废弃 2026-09-08】旧 TCO 字段，禁止参与计算</summary>
        public float Nozzle2TcoY { get; set; } = 0f;

        /// <summary>
        /// 偏心 e 的基准角 U0（°）= 物理对针时机械手的 U 角（默认 0）。
        /// 走位按 C = X_obj − R(姿态U − U0)·e，U 不转时旋转项就是 e 本身。
        /// </summary>
        public float ToolAlignU { get; set; } = 0f;

        /// <summary>
        /// 放料/旋转标定基准角（°）记录位；定案式下不再参与吸取计算，仅供旧档与记录参考。
        /// </summary>
        public float CalibPlaceU { get; set; } = 0f;

        // ===== 2026-09-08 定案：相机安装方式 + 拍照基准位 + 旋转中心 O（决定 X_obj 怎么算）=====
        /// <summary>相机安装方式：true=眼在手 EIH（相机随机械手 XY 动）；false=固定相机 ETH。控制拍照基准位回位（EnsurePhotoBaseAsync）。</summary>
        public bool CameraMountEih { get; set; } = false;
        /// <summary>拍照基准位 X（mm，命令位）——仅 EIH 需要</summary>
        public float PhotoBaseX { get; set; } = 0f;
        /// <summary>拍照基准位 Y（mm，命令位）——仅 EIH 需要</summary>
        public float PhotoBaseY { get; set; } = 0f;
        /// <summary>旋转中心 O 的 X（命令位域）= 三点定圆圆心经 H 映射（档案 ToolCenterWx）。需 O 补偿时使用。</summary>
        public float RotCenterWx { get; set; } = 0f;
        /// <summary>旋转中心 O 的 Y（命令位域）= 档案 ToolCenterWy。需 O 补偿时使用。</summary>
        public float RotCenterWy { get; set; } = 0f;

        /// <summary>
        /// ★2026-09-12：X_obj 是否需叠旋转中心 O + 偏心 e 补偿（决定消费式）。
        /// 两种场景（与 CameraCalibrationBundle.NeedsOCompensation 同源同果）：
        ///   ① EIH 眼在手（相机随机械手走）        → true（X_obj = P_photo + O − H(u)）
        ///   ② ETH 固定相机（直接拍工件 / 延伸杆辅助）→ **false**（X_obj = H(u) 或 H(u)+b，见下）
        /// ★2026-09-15 二修：固定相机【不再叠 O】。理由（可证伪）：O 补偿式里含【拍照机位 P_photo】，
        ///   只有"相机随机械手走"(EIH) 时才成立；固定相机下工件在台面上不动，把 P_photo 混进物位在物理上
        ///   说不通（实测反证：X_obj Y 比工作区低约 120mm，飞出可达域）。
        ///   ⇒ 固定相机若 H 标的是【杆端 mark】，靠 <see cref="HasRodOffset"/> 补 b，不是靠 O。
        /// 默认 false；发布链按档案声明写（仅 EIH 写 true）。
        /// </summary>
        public bool NeedsOCompensation { get; set; } = false;

        // ===== 2026-09-15：固定相机 + 延伸杆标定的『杆端→吸嘴偏移 b』通路 =====
        /// <summary>
        /// ★2026-09-15：是否启用『杆端→吸嘴偏移 b』补偿（固定相机 + 延伸杆辅助标定专用）。
        ///
        /// 背景：延伸杆辅助标定时，九点矩阵 H 的域是【杆端 mark】——命令到 H(u) 时落在特征上的是杆端，
        ///   不是吸嘴尖。同心吸嘴坐在 U 回转轴上 ⇒ 送吸嘴尖要补一个**与 U 无关**的常量位移 b：
        ///       **吸点 = H(u) + b**
        ///   b 的来源：① 对针（EyeToHandImage）直量 t = M_tool − H(A')；② 旋转标定顺带产出的
        ///   ToolEccW（= 映射域定圆的偏心矢，见档案 ToolEccWx/Wy）。
        ///
        /// ⚠ 与 NeedsOCompensation / HandEyeInNozzleDomain 的互斥关系（三者只能命中一个）：
        ///   · HandEyeInNozzleDomain=true（H 已消杆）→ 直吸 H(u)，**不要再补 b**（否则双重补偿）
        ///   · NeedsOCompensation=true（EIH）        → P_photo + O − H(u)，与 b 无关
        ///   · 本字段=true（固定相机 + 杆端域）      → H(u) + Sign·b
        /// 默认 false = 与本次改动前完全一致（零回归）。发布链按档案声明写。
        /// ⚠ 符号默认 +1（由 ToolEccW 定义 + 旋转采样只转 U 的几何反推得出）；若现场 A/B 发现应取反，
        ///   改 <see cref="RodOffsetSign"/> 即可，不必改代码。
        /// </summary>
        public bool HasRodOffset { get; set; } = false;

        /// <summary>杆端→吸嘴偏移 b 的 X（mm，命令位域；档案 ToolEccWx）。仅在 <see cref="HasRodOffset"/> 时生效。</summary>
        public float RodOffsetWx { get; set; } = 0f;
        /// <summary>杆端→吸嘴偏移 b 的 Y（mm，命令位域；档案 ToolEccWy）。仅在 <see cref="HasRodOffset"/> 时生效。</summary>
        public float RodOffsetWy { get; set; } = 0f;
        /// <summary>
        /// b 的符号（+1 / −1）。默认 +1（推导结论：吸点 = H(u) + b）。
        /// 现场 A/B 判定法：用同一像素各走一次 ±b，只有一个是"吸嘴正好落在点上"；选错会偏 2|b|（不会安静通过）。
        /// </summary>
        public float RodOffsetSign { get; set; } = 1f;

        /// <summary>
        /// ★★2026-09-16：现场**是否真的判定过** <see cref="RodOffsetSign"/>。
        ///
        /// 【为什么必须单独有这个标志】两个原因叠加，缺一个都会骗人：
        ///   ① <see cref="RodOffsetSign"/> 的代码默认就是 <c>1f</c> ⇒ 键缺席时取默认，
        ///      日志打「符号 = +1」看起来像已确认，实际是**从来没人判定过**（同 ConsumptionTag 缺席的病）。
        ///   ② 发布链写字段时走 <c>ProcessConfigOverlay.SetFields(..., codeDefaults)</c> 的**同值剪枝**：
        ///      现场选「+1」写进去的值**与代码默认相同 ⇒ 被静默剪掉、键根本落不了盘**；
        ///      而选「−1」能落盘 ⇒ 症状是**不对称的**（同值剪枝把"显式选择"和"默认"抹平了）。
        ///      ⇒ 用这个默认 <c>false</c> 的布尔做标志：写 <c>true</c> 永远 ≠ 默认 ⇒ **永不被剪枝**。
        ///
        /// 判据：<c>false</c> = 没判定过（消费端硬拦，因为选错偏 2|b|，比不补更危险）；
        ///       <c>true</c>  = 现场用校验台口径 ④/⑤ 做过 A/B 并选定。
        /// 默认 false ⇒ 未声明的老配置不会被误判成"已判定"。
        /// </summary>
        public bool RodOffsetSignDeclared { get; set; } = false;

        /// <summary>
        /// ★2026-09-15：九点矩阵 H 是否已在【吸嘴域】（延伸杆偏心已在标定阶段扣掉）。
        /// 三种场景（与 CameraCalibrationBundle.IsNozzleDomainH 同源同果）：
        ///   true  = H 已消杆：命令到 H(u) 即让吸嘴对准像素 u ⇒ 消费直接 X_obj = H(u)，
        ///           **不叠 O、不用 P_photo**（本条优先于 NeedsOCompensation / CameraMountEih）。
        ///   false = 默认，未声明 → 行为与本次改动前完全一致（旧档不受影响）。
        /// ⚠ 与 NeedsOCompensation 的分工：后者描述"H 在杆端域、需叠 O 把杆消掉"；
        ///   本字段描述"H 已经在吸嘴域、那一步早在标定里做完了"。两者同时 true 就是【双重补偿】。
        /// </summary>
        public bool HandEyeInNozzleDomain { get; set; } = false;

        /// <summary>
        /// ★2026-09-15：吸嘴是否与 U 回转轴同轴。true = 转 U 时吸嘴尖在 XY 上原地不动
        /// ⇒ 消费**免掉** R(U−U0)·Ecc 这一项（U 只决定姿态）。
        /// false = 默认（未声明 / 偏心吸嘴）→ 保留该项，行为与改动前一致。
        /// 判据：绕 U 转 30° 前后各测同一不动特征，吸嘴尖偏差 d0、d30 都 ≈0 且 d30−d0 ≈0。
        /// </summary>
        public bool NozzleAxisCoaxial { get; set; } = false;

        // ===== ★2026-09-15：口径对齐用的留痕字段（发布链写、生产端对账）=====
        // 动机：上面这几个开关（HandEyeInNozzleDomain / NeedsOCompensation / CameraMountEih /
        //   HasRodOffset / NozzleAxisCoaxial）**必须同时成立**才代表一个正确的口径；
        //   而工位配置是【一套扁平字段】，任何一个漏发/被覆盖，生产端就会安静地走另一档。
        //   所以发布链把"判定出来的口径"写成一行标签留在这里，生产端启动时把"实读出来的口径"
        //   与之对账 —— 不一致即 ERROR（口径漂移不许静默）。

        /// <summary>
        /// 发布链写下的口径标签（格式 "分型号|分型名|b=+0"，见 CalibrationConsumptionContract.Decision.Tag）。
        /// 空 = **从未按口径契约发布过**。
        ///
        /// ★★2026-09-16 语义修正：空**不再**表示"不做对账 / 放行"。
        ///   旧注释写的是「老配置（未发布过），生产端不做对账」——而"从未发布"恰恰是最危险的状态：
        ///   本工位 H 是杆端域（|b|≈132mm），没有声明 ⇒ 生产端静默退 ②直吸 H(u) ⇒ 少补一个杆长 ⇒ 撞机。
        ///   现在空标签 = TagReconcileStatus.NoTag（与「一致」分开），并交给
        ///   StationProcessBase.EnforceConsumptionGate 处置：与标定档案交叉核对，冲突即拦下不许运动。
        /// </summary>
        public string ConsumptionTag { get; set; } = "";

        /// <summary>口径的一句话算式（发布链写，供生产端日志原样打印）</summary>
        public string ConsumptionFormula { get; set; } = "";

        // ============================================================
        // 视觉与双目标定位
        // ============================================================

        /// <summary>第 2 目标相对第 1 目标的 X 节距（料盘阵列间距 mm；双目标形态）</summary>
        public float TilePitchX { get; set; } = 0f;
        /// <summary>第 2 目标相对第 1 目标的 Y 节距（mm）</summary>
        public float TilePitchY { get; set; } = 0f;

        /// <summary>ShapeMatch 匹配分数下限（低于此值判 NG，回待机不抛异常）</summary>
        public double MinScore { get; set; } = 0.5;

        /// <summary>
        /// 示教模式开关：true = 触发视觉并打印理论落点换算公式，不执行走位/吸取（人工对照用）。
        /// </summary>
        public bool TeachMode { get; set; } = false;

        // ============================================================
        // 下相机二次校准段（S3 复合工位：上相机引导抓取 + 下相机纠偏放置）
        // ============================================================

        /// <summary>
        /// 下相机二次校准开关。true = 吸取后移到下相机上方拍卡片底面，
        /// 测位置/角度偏差，纠偏后按固定位放置（复合工位"取-校-放"三段节拍）。
        /// false = 传统单相机流程（上相机引导直接放料，下相机段跳过）。
        /// </summary>
        public bool EnableDownCameraCorrection { get; set; } = false;

        /// <summary>
        /// 下相机视场中心对应的机械位 X（mm，命令位）：吸取后把卡片移到该位正上方拍照。
        /// 标定下相机九点时，走位网格的中心即此点。
        /// </summary>
        public float DownCameraX { get; set; } = 0f;

        /// <summary>
        /// 下相机视场中心对应的机械位 Y（mm，命令位）。
        /// </summary>
        public float DownCameraY { get; set; } = 0f;

        /// <summary>
        /// 下相机拍照时机械手的 Z 高度（mm）：下相机仰视拍卡片底面，卡片需在标定高度。
        /// 相机随 Z 的工位须与标定时 Z 一致，否则像素当量失真。
        /// </summary>
        public float DownCameraZ { get; set; } = 0f;

        // ===== ★2026-09-15：下相机相对纠偏的使能常量（让"标定好了能用起来"）=====
        // 背景（本轮定位的结构性问题）：生产端原先算 δ = H_down(R_img) − 拍照机位。
        //   但 H_down 的定义就是"机器人停在哪、卡片 mark 就在哪个像素"，工件刚性吸在吸嘴上、
        //   机器人一动它跟着动 ⇒ H_down(R_img) 恒等于当时机位 ⇒ **δ 恒 ≈0**，
        //   位置纠偏"看起来执行了、实际从不动"。真正有意义的只有相对【吸嘴旋转轴投影】的差分：
        //       δ = H_down(R_img) − H_down(R_cdown)
        //   这正是校验台/门面一直在用的算式，但生产端连 R_cdown 都没有 ⇒ 无法同口径。
        //   下面三个字段把该常量在【发布时】算好（矩阵没变它就是常量），生产端只做减法。

        /// <summary>
        /// 下相机像素旋转中心 R_cdown 的列坐标（像素）。吸嘴旋转轴在下相机图像里的投影，
        /// 由 DownCameraPixelRotCenter 会话在像素平面拟合圆求得（档案 DownRotCenterCol）。
        /// 仅供发布链换算 <see cref="DownCameraAxisWx"/> 用；生产端消费的是下面那对机械位常量。
        /// </summary>
        public float DownCameraRotCenterCol { get; set; } = 0f;

        /// <summary>下相机像素旋转中心 R_cdown 的行坐标（像素），语义见 <see cref="DownCameraRotCenterCol"/></summary>
        public float DownCameraRotCenterRow { get; set; } = 0f;

        /// <summary>
        /// ★像素旋转中心经下相机矩阵映射后的机械位 X（= H_down(R_cdown).X，mm）。
        /// 与 <see cref="DownCameraAxisWx"/> 一起构成相对纠偏的基准点：
        ///   δ = (标定转换节点输出 H_down(R_img)) − (本值, DownCameraAxisWy)
        /// ★发布时算好、矩阵不变即为常量；生产端**不再**用拍照机位当基准。
        /// </summary>
        public float DownCameraAxisWx { get; set; } = 0f;

        /// <summary>像素旋转中心经下相机矩阵映射后的机械位 Y（= H_down(R_cdown).Y，mm）</summary>
        public float DownCameraAxisWy { get; set; } = 0f;

        /// <summary>
        /// ★下相机九点标定时的 Z 高度（档案 CalibZ，mm）。
        /// 只用于"开工前预检"：与 <see cref="DownCameraZ"/> 相差过大即报警（像素当量失真），
        /// 不再靠现场试出来。0 = 未发布（不参与比较）。
        /// </summary>
        public float DownCameraCalibZ { get; set; } = 0f;

        /// <summary>
        /// 下相机段匹配分数下限（低于此值判纠偏 NG，本次跳过放置/回待机）。
        /// </summary>
        public double DownCameraMinScore { get; set; } = 0.5;

        /// <summary>
        /// 下相机段角度归正方向符号（±1）。下相机仰视拍卡片底面=视轴镜像，通常取 −1；
        /// 若"越归正越歪"则翻转此值。默认 −1（与上相机 +1 相反）。
        /// </summary>
        public float DownCameraAngleSign { get; set; } = -1f;

        /// <summary>
        /// 放置时是否用下相机测得的偏差做位置纠偏。
        /// true = 放置位 = 固定放置位 − 下相机实测偏差（把卡片纠回正位）；
        /// false = 下相机只测角度、位置仍放固定位。
        /// </summary>
        public bool EnableDownCameraPositionCorrection { get; set; } = true;

        /// <summary>
        /// 放置时是否用下相机测得的偏差做角度纠偏。
        /// true = 放料角 = PlaceU − Sign·(下相机MatchAngle − RefAngleDeg)。
        /// </summary>
        public bool EnableDownCameraAngleCorrection { get; set; } = true;

        // ============================================================
        // Z 轴高度与速度
        // ============================================================

        /// <summary>
        /// Z 轴安全高度（XY 运动前 Z 必须在此之上；本机 Z 域顶=0、向下为负，本例现场配 -50）。
        /// ★★2026-09-16 实测：默认值写的是 +50，在本机【不可能有效】（已超出 Z 域顶）—— 陈旧的兜底值。
        ///   实测现存工位档案【全都带 SafeZ 键】（-10.0 与 -50.0 两种），故该默认值当前走不到；
        ///   万一某档案缺键，会拿到 +50 并被下游【抛异常拒绝】（`ResolveGantryLimZ` 判定 limZ 不高于
        ///   目标 Z；`MoveAbsAsync` 被脚本以 `ERR Z out of range(-144~0)` 拒后抛异常）—— 都是"响"的，不静默。
        ///   是否改成现场实际值（-50f）按机型定夺；改前请先确认不存在"新建且不带该键"的工位。
        /// </summary>
        public float SafeZ { get; set; } = 50f;

        /// <summary>
        /// 【门型走位 JUMP】水平段高度 mm —— 平移时先把工件抬到该 Z 再水平走，到了再下降。
        ///
        /// ★这是【安全通过高度】，不是速度。取值要求：严格高于路径上一切障碍顶面，
        ///   且低于 Z 域顶(0)。本工位参考：SafeZ=-50、下相机拍照 Z=-101.965 ⇒ 默认 -30（比 SafeZ 再高 20mm）。
        /// ★Z 域顶=0（向下为负）。现场若仍出现刮碰，把它调高（更接近 0）。
        /// ★它必须【高于目标 Z】，否则门型失效（退化成贴着目标高度横穿）。运行期发现越界会
        ///   自动兜底为"目标 Z+30mm"并打 WARN —— 那只是不让流程卡死的兜底，别依赖它。
        /// </summary>
        public float JumpLimZ { get; set; } = -30f;
        /// <summary>Z 轴吸取下探高度</summary>
        public float PickZ { get; set; } = -2f;
        /// <summary>Z 轴放料下探高度</summary>
        public float PlaceZ { get; set; } = -1f;

        /// <summary>XY 运动速度 mm/s；0 = 使用控制器当前速度</summary>
        public float XySpeed { get; set; } = 0f;
        /// <summary>Z 轴运动速度（比 XY 慢）；0 = 使用控制器当前速度</summary>
        public float ZSpeed { get; set; } = 0f;

        // ============================================================
        // 放料位（摆盘工位固定放置坐标；单目标用 Place1，双目标加 Place2）
        // ============================================================

        public float Place1X { get; set; } = 200f;
        public float Place1Y { get; set; } = 100f;
        public float Place2X { get; set; } = 230f;
        public float Place2Y { get; set; } = 100f;

        // ============================================================
        // 真空 IO 与节拍
        // ============================================================

        /// <summary>吸嘴1 真空阀输出口</summary>
        public int VacuumIo1 { get; set; } = 0;
        /// <summary>吸嘴2 真空阀输出口</summary>
        public int VacuumIo2 { get; set; } = 1;

        /// <summary>开真空保压等待 ms（确保吸牢）</summary>
        public int VacuumOnDelayMs { get; set; } = 200;
        /// <summary>关真空(破空)等待 ms（确保脱落）</summary>
        public int VacuumOffDelayMs { get; set; } = 150;
        /// <summary>到位后机械稳定等待 ms</summary>
        public int SettleMs { get; set; } = 150;

        // ---- 待机位 ----
        public float StandbyX { get; set; } = 150f;
        public float StandbyY { get; set; } = 0f;
    }
}
