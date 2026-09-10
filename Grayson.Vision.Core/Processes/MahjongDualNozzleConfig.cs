namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 「巴斯勒相机 + Epson 4 轴 SCARA（双吸嘴）」工件分拣工位的位置与节拍配置。
    ///
    /// 轴号约定（与 EpsonRobot 插件一致）：
    ///   0 = X（SCARA 大臂，mm） 1 = Y（小臂，mm）
    ///   2 = Z（吸嘴上下，mm，向下为负） 3 = U（末端旋转，deg）
    ///
    /// 双吸嘴几何约定（2026-09-08 定案，唯一真源 = CalibrationGeometry，人话版见 标定语义_人话版_2026-09-08.md）：
    ///   - 每吸嘴只认【一个】几何量：Ecc（Nozzle1Ecc/Nozzle2Ecc，世界 mm，U0=ToolAlignU 参考）
    ///     = 真吸嘴偏心 e = O − H(p_tip)。O 来自三点定圆旋转标定（半径=杆长，扔掉，故杆长/偏心无影响），
    ///     p_tip 来自一次物理对针。★ 不需要同心短杆；★ 偏心延伸杆测出的 ToolEccW 是杆末端偏心，不是 e。
    ///   - ⚠ Nozzle1Tco/Nozzle2Tco（旧 TCO）已废弃，发布链一律清零，不得参与计算（v5 消费式仿真误差 451mm）。
    ///   - 视觉输出 w = H(像素)，口径是「机械手该停哪」，不是工件真位：
    ///       眼在手 EIH：工件真位 X_obj = P_photo + O − H(u)
    ///       固定相机 ETH：X_obj = H(u)
    ///     吸取回转中心 C = X_obj − R(WorkU − U0)·Ecc1（U 不转时旋转项就是 Ecc 本身）。
    ///   - 放料：工件被吸持后其中心随 U 绕回转中心转 = C + R(姿态U)·Ecc →
    ///     落点 C = 放料位 − R(U_place − U0)·Ecc（同一式，换姿态角即可）。
    ///
    /// 双牌定位策略：
    ///   视觉流中 ShapeMatch 每次输出一个最佳匹配（工件牌在料盘中按阵列摆放），
    ///   第一块由视觉直接定位，第二块按阵列节距（TilePitchX/Y）推算。
    /// </summary>
    public class MahjongDualNozzleConfig
    {
        // ============================================================
        // 设备绑定
        // ============================================================

        /// <summary>Epson 机械手在工位管理中绑定的逻辑别名</summary>
        public string CardAlias { get; set; } = "EpsonRobot";

        // ============================================================
        // 吸嘴数量形态（重要：决定业务时序走单吸嘴还是双吸嘴）
        // ============================================================

        /// <summary>
        /// 单吸嘴模式开关。
        /// - true  = 当前硬件只装了 1 个吸嘴（机枪手单吸嘴配置）：
        ///   一次循环只吸/放 1 块牌（Phase2 吸取 → Phase4 放料），吸嘴2 相位全部跳过；
        /// - false = 双吸嘴形态：一次循环搬运 2 块牌（Phase3/Phase5 生效）。
        /// TODO(现场)：双吸嘴治具装上后改 false，并核对 Nozzle2OffsetX/Y 机械间距。
        /// </summary>
        public bool UseSingleNozzle { get; set; } = true;

        // ---- 轴号（EpsonRobot 约定：0=X 1=Y 2=Z 3=U）----
        public int AxisX { get; set; } = 0;
        public int AxisY { get; set; } = 1;
        public int AxisZ { get; set; } = 2;
        public int AxisU { get; set; } = 3;

        /// <summary>作业时 U 轴角度（吸料阶段的吸嘴朝向）。走位按 C = X_obj − R(WorkU − U0)·e：U≠U0 时偏心要跟着转，定案式已含该项，任意 U 角都准。</summary>
        public float WorkU { get; set; } = 0f;

        /// <summary>
        /// 摆盘放料角（°）：吸住工件移出料区后、放料前把 U 轴转到该角度再放下，
        /// 保证每次放料的吸嘴朝向一致（统一放置姿态）。吸嘴近似同轴（吸持点≈U 轴）时可直接用；
        /// 若吸嘴偏心显著，转 U 会引起工件中心平移，需配合旋转中心/偏心补偿（后续按需接入）。
        /// </summary>
        public float PlaceU { get; set; } = 0f;

        // ============================================================
        // 视觉实测角归正（2026-09-06 M1 新增；固定角归正之外的策略二）
        // ============================================================
        // 背景：麻将牌在来料盘中姿态可能随机（0°/90°/180°/270° 形状同形，纯形状只能归正到
        // 90° 粒度内的最近姿态；若要求牌面字朝向一致需配方尾部叠 OCR/朝向判别）。
        // 公式：U_place = PlaceU − Sign·(MatchAngle − RefAngleDeg)。
        //   MatchAngle = ShapeMatch 输出的模板相对实测姿态旋转角；把偏差反向补掉使工件按
        //   RefAngleDeg（模板建时参考姿态，通常 0°）落盘。
        // 符号：相机朝下拍（固定上相机/眼在手）= +1；朝上拍（固定下相机检知吸起工件）视轴镜像
        //   取 −1。符号错误表现为"越归正越歪"，现场翻转 AngleCorrectionSign 即可。

        /// <summary>视觉实测角归正开关（true = 读 MatchAngle 归正放料角；false = 固定角 PlaceU）</summary>
        public bool EnableVisionAngleCorrection { get; set; } = false;

        /// <summary>模板参考角 θ_ref（°）：模板建时的正位姿态角，通常 0。</summary>
        public float RefAngleDeg { get; set; } = 0f;

        /// <summary>归正方向符号（±1）：相机朝下=+1；朝上(下相机)=−1。</summary>
        public float AngleCorrectionSign { get; set; } = 1f;

        // ============================================================
        // 吸嘴几何（唯一来源=标定管理页「📤 发布偏心到业务配置」）
        // 2026-09-03 重构：旧 NozzleToolOffsetX/Y（固定U人工示教）与 Nozzle2OffsetX/Y（吸嘴间距）
        // 已退役删除；2026-09-08 定案：每吸嘴只认【一个】几何量 Ecc = 真吸嘴偏心 e：
        //   Ecc = e = O − H(p_tip)，U0=ToolAlignU 参考。
        //   · O  = 三点定圆旋转标定的【圆心】（半径=杆长，丢弃 → 杆长/杆偏心不影响）
        //   · p_tip = 一次物理对针：U=U0 时吸嘴尖压住特征，抬 Z 拍到它
        //   ★ 不需要同心短杆；★ 偏心延伸杆测出的 Profile.ToolEccWx/Wy 是【杆末端】偏心，不是 e。
        // 消费（2026-09-08 定案，唯一真源 CalibrationGeometry，与校验台同一口径）：
        //   X_obj = P_photo + O − H(u)   （EIH 眼在手）
        //   X_obj = H(u)                 （ETH 固定相机）
        //   C     = X_obj − R(姿态U − U0)·Ecc   （吸取 WorkU；放料 U_place，同一式）
        // 双吸嘴：吸嘴1/2 各一套 Ecc，共用同一 U 轴回转中心。
        // ============================================================

        /// <summary>吸嘴1 真吸嘴偏心 e 世界 X（mm，U0=ToolAlignU 参考；★偏心延伸杆测得 ToolEccW 是杆末端偏心，不是 e）</summary>
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
        /// 走位按 C = X_obj − R(姿态U − U0)·e：U 不转时旋转项就是 e 本身；U≠U0 必须转，否则差十几 mm。
        /// </summary>
        public float ToolAlignU { get; set; } = 0f;

        /// <summary>
        /// 吸放式标定放料基准角（°）= 向导「初始吸取位 U」(PickBaseU) 快照（旧语义参考角，保留兼容，不参与计算）。
        /// </summary>
        public float CalibPlaceU { get; set; } = 0f;

        // ===== 2026-09-08 定案：相机安装方式 + 拍照基准位 + 旋转中心 O（决定 X_obj 怎么算）=====
        /// <summary>
        /// 相机安装方式：true=眼在手 EIH（相机随机械手 XY 动，H 输出是"命令位域"）→
        /// X_obj = P_photo + O − H(u)；false=固定相机 ETH（相机固定于料盘上方）→ X_obj = H(u)。
        /// ⚠ 填错会导致"看起来标定都对、走位系统性偏移"。麻将工位默认 false（相机固定于料盘上方）。
        /// </summary>
        public bool CameraMountEih { get; set; } = false;

        /// <summary>拍照基准位 X（mm，命令位）——仅 EIH 需要：视觉触发瞬间机械手所在 XY。若为 0 且 EIH=true，请在日志中核对是否已正确填写。</summary>
        public float PhotoBaseX { get; set; } = 0f;
        /// <summary>拍照基准位 Y（mm，命令位）——仅 EIH 需要</summary>
        public float PhotoBaseY { get; set; } = 0f;

        /// <summary>旋转中心 O 的 X（命令位域）= 三点定圆圆心经 H 映射（标定档案 ToolCenterWx）。仅 EIH 需要。</summary>
        public float RotCenterWx { get; set; } = 0f;
        /// <summary>旋转中心 O 的 Y（命令位域）= 标定档案 ToolCenterWy。仅 EIH 需要。</summary>
        public float RotCenterWy { get; set; } = 0f;

        // ============================================================
        // 视觉与双牌定位
        // ============================================================

        /// <summary>第二块工件牌相对第一块的 X 节距（料盘阵列间距，mm）</summary>
        public float TilePitchX { get; set; } = 0f;
        /// <summary>第二块工件牌相对第一块的 Y 节距（mm）</summary>
        public float TilePitchY { get; set; } = 0f;

        /// <summary>ShapeMatch 匹配分数下限（低于此值判 NG）</summary>
        public double MinScore { get; set; } = 0.5;

        /// <summary>
        /// 示教模式开关（2026-09-03，偏心公式验证用）：
        /// - true  = Phase1 照常触发视觉流定位并打印「理论落点换算公式」，但**不执行 XY 走位与
        ///   Z 下探吸取/放料**，流程安全结束（仅回待机位）——供人工把机械手移到「吸嘴1 对准
        ///   麻将中心」后读回 X*/Y*，与理论落点对照（差异即偏心/基准角参数方向错误或需要发布）。
        /// - false = 正常完整执行 走位 → 吸取 → 放料。
        /// 理论落点（吸嘴1，2026-09-08 定案式）：
        ///   X_obj = P_photo + O − H(u)（EIH）/ H(u)（ETH）
        ///   C*    = X_obj − R(WorkU − U0)·Ecc1
        ///     （WorkU==U0==0 默认 → C* = X_obj − Ecc1；Ecc1=0 未发布时退化为视觉直吸，仅 U 恒=U0 才准）。
        /// </summary>
        public bool TeachMode { get; set; } = false;

        // ============================================================
        // Z 轴高度与速度
        // ============================================================

        /// <summary>Z 轴安全高度（XY 运动前 Z 必须在此之上）</summary>
        public float SafeZ { get; set; } = 50f;
        /// <summary>Z 轴吸取下探高度</summary>
        public float PickZ { get; set; } = -2f;
        /// <summary>Z 轴放料下探高度</summary>
        public float PlaceZ { get; set; } = -1f;

        /// <summary>XY 运动速度 mm/s；0 = 使用控制器当前速度</summary>
        public float XySpeed { get; set; } = 0f;
        /// <summary>Z 轴运动速度（比 XY 慢）；0 = 使用控制器当前速度</summary>
        public float ZSpeed { get; set; } = 0f;

        // ============================================================
        // 放料位（摆盘工位，两块牌的放置坐标）
        // ============================================================

        public float Place1X { get; set; } = 200f;
        public float Place1Y { get; set; } = 100f;
        public float Place2X { get; set; } = 230f;
        public float Place2Y { get; set; } = 100f;

        // ============================================================
        // 真空 IO 与节拍
        // ============================================================

        /// <summary>吸嘴1 真空阀输出口（EpsonRobot 0 基编号）</summary>
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
