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
        /// <summary>相机安装方式：true=眼在手 EIH（相机随机械手 XY 动）→ X_obj = P_photo + O − H(u)；false=固定相机 ETH → X_obj = H(u)。</summary>
        public bool CameraMountEih { get; set; } = false;
        /// <summary>拍照基准位 X（mm，命令位）——仅 EIH 需要</summary>
        public float PhotoBaseX { get; set; } = 0f;
        /// <summary>拍照基准位 Y（mm，命令位）——仅 EIH 需要</summary>
        public float PhotoBaseY { get; set; } = 0f;
        /// <summary>旋转中心 O 的 X（命令位域）= 三点定圆圆心经 H 映射（档案 ToolCenterWx）。仅 EIH 需要。</summary>
        public float RotCenterWx { get; set; } = 0f;
        /// <summary>旋转中心 O 的 Y（命令位域）= 档案 ToolCenterWy。仅 EIH 需要。</summary>
        public float RotCenterWy { get; set; } = 0f;

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
