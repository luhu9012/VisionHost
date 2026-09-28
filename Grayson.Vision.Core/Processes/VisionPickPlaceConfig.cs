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
        // 吸嘴几何：★已随 R6d 整体退役（范式1 发布链删除）
        // ============================================================
        //   历史：本类曾承载『吸嘴偏心 e / 旋转中心 O / 杆端偏移 b / b 符号 / 基准角 U0 / 拍照基准位』
        //   与『口径标签』，由标定页「发布偏心到业务配置」写入，生产端按
        //   四个互斥开关（O 补偿 / 杆端偏移 / H 已吸嘴域 / 同轴）自行挑一档算式
        //   四个开关自行挑一档算式消费 ⇒ 病根 = 一套扁平字段 + 复合工位多相机互相覆盖，
        //   口径漂移无异常可察觉（ST_002 少补 |b|=132mm 撞机）。
        //   现在：几何唯一真源 = 链图 Recipes\Workstations\{工位}\Calib\Chain.json
        //   （链标定向导产出），生产端只做链求值 ResolvePixelToWorld / ResolveFlangeTarget。
        //   存量工位 JSON 里的旧键已无字段载体 ⇒ 自动失效（不报错、不参与计算）。
        // 视觉与双目标定位（2026-09-08 语义）：
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

        // ===== ★2026-09-28 R6d：下相机像素基准字段退役 =====
        //   历史：『下相机像素旋转中心 R_cdown』+『R_cdown 映射后的机械位』两个量
        //   由发布链算好落盘，生产端做 δ = H_down(R_img) − (AxisWx,AxisWy)。
        //   现在：δ = Chain(卡片像素) − Chain(DeltaRefPixel)，差分基准像素由链标定向导实测写入
        //   链图下相机节点（ChainCameraNode 的差分基准像素字段）⇒ 本类不再存这两个量。
        //   ★『下相机标定高度 Z』仍保留：开工前预检拿它比对『拍照高度 vs 标定高度』。
        /// <summary>
        /// ★下相机九点标定时的 Z 高度（档案 CalibZ，mm）。
        /// 只用于"开工前预检"：与 <see cref="DownCameraZ"/> 相差过大即报警（像素当量失真），
        /// 不再靠现场试出来。0 = 未设置（不参与比较；范式2 下由链标定向导/工位档案填入）。
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
