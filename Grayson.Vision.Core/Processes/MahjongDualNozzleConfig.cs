namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 「巴斯勒相机 + Epson 4 轴 SCARA（双吸嘴）」工件分拣工位的位置与节拍配置。
    ///
    /// 轴号约定（与 EpsonRobot 插件一致）：
    ///   0 = X（SCARA 大臂，mm） 1 = Y（小臂，mm）
    ///   2 = Z（吸嘴上下，mm，向下为负） 3 = U（末端旋转，deg）
    ///
    /// 几何约定：★几何类字段已随 R6d 全部退役。唯一真源 = 链图 Chain.json（范式2，链标定向导产出）：
    ///   工件真位 / 吸点命令位一律由链求值给出（ResolvePixelToWorld / ResolveFlangeTarget）；
    ///   双吸嘴 = 链图 Nozzle1/Nozzle2 两个工具节点（偏移带符号内蕴；同轴 = 偏心为 0 的特例）。
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
        // ============================================================
        // 吸嘴几何：★已随 R6d 整体退役（范式1 发布链删除）
        // ============================================================
        //   历史：Ecc(e)/O/b 符号/拍照基准位/口径标签由标定页发布写入，生产端按四开关挑一档算式。
        //   现在：几何唯一真源 = 链图 recipes\Workstations...\Calib\Chain.json（链标定向导产出），
        //   生产端只做链求值（双吸嘴 = 链图 Nozzle1/Nozzle2 工具节点，偏移带符号内蕴）。
        //   存量工位 JSON 里的旧键已无字段载体 ⇒ 自动失效（不报错、不参与计算）。
        // 视觉与双牌定位：
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

        /// <summary>
        /// Z 轴安全高度（XY 运动前 Z 必须在此之上；本机 Z 域顶=0、向下为负，现场配 -50）。
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
        ///   且低于 Z 域顶(0)。本机（Epson SCARA）Z 域顶=0、向下为负；现场 SafeZ 配 -50 ⇒ 默认 -30。
        /// ★它必须【高于目标 Z】，否则门型失效（退化成贴着目标高度横穿）—— 运行期发现越界会
        ///   兜底为"目标 Z+30mm"，仍无效则**直接拒绝执行**（宁可不动，不可乱动）。
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
