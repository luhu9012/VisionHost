namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 「巴斯勒相机 + Epson 4 轴 SCARA（双吸嘴）」麻将分拣工位的位置与节拍配置。
    ///
    /// 轴号约定（与 EpsonRobot 插件一致）：
    ///   0 = X（SCARA 大臂，mm） 1 = Y（小臂，mm）
    ///   2 = Z（吸嘴上下，mm，向下为负） 3 = U（末端旋转，deg）
    ///
    /// 双吸嘴几何约定（重要）：
    ///   - 吸嘴1 为「基准吸嘴」：视觉标定输出（CalibrationApply）的物理坐标
    ///     默认以吸嘴1 为工具参考点，机器人 XY = 目标物理坐标 + NozzleToolOffset；
    ///   - 吸嘴2 相对吸嘴1 有固定机械间距（Nozzle2OffsetX/Y）：
    ///     吸嘴2 要到目标点时，机器人 XY = 目标物理坐标 - 吸嘴2偏移 + NozzleToolOffset。
    ///
    /// 双牌定位策略：
    ///   视觉流中 ShapeMatch 每次输出一个最佳匹配（麻将牌在料盘中按阵列摆放），
    ///   第一块由视觉直接定位，第二块按阵列节距（TilePitchX/Y）推算。
    /// </summary>
    public class MahjongDualNozzleConfig
    {
        // ============================================================
        // 设备绑定
        // ============================================================

        /// <summary>Epson 机械手在工位管理中绑定的逻辑别名</summary>
        public string CardAlias { get; set; } = "EpsonRobot";

        // ---- 轴号（EpsonRobot 约定：0=X 1=Y 2=Z 3=U）----
        public int AxisX { get; set; } = 0;
        public int AxisY { get; set; } = 1;
        public int AxisZ { get; set; } = 2;
        public int AxisU { get; set; } = 3;

        /// <summary>作业时 U 轴角度（麻将牌通常无需旋转，保持 0）</summary>
        public float WorkU { get; set; } = 0f;

        // ============================================================
        // 双吸嘴几何（机器人坐标系，mm）
        // ============================================================

        /// <summary>吸嘴1 相对视觉标定参考点（吸嘴1 为基准时填 0）的 X 偏置</summary>
        public float NozzleToolOffsetX { get; set; } = 0f;
        /// <summary>吸嘴1 相对视觉标定参考点的 Y 偏置</summary>
        public float NozzleToolOffsetY { get; set; } = 0f;

        /// <summary>吸嘴2 相对吸嘴1 的 X 间距（同臂双吸嘴，通常为固定值，如 30mm）</summary>
        public float Nozzle2OffsetX { get; set; } = 30f;
        /// <summary>吸嘴2 相对吸嘴1 的 Y 间距</summary>
        public float Nozzle2OffsetY { get; set; } = 0f;

        // ============================================================
        // 视觉与双牌定位
        // ============================================================

        /// <summary>第二块麻将牌相对第一块的 X 节距（料盘阵列间距，mm）</summary>
        public float TilePitchX { get; set; } = 0f;
        /// <summary>第二块麻将牌相对第一块的 Y 节距（mm）</summary>
        public float TilePitchY { get; set; } = 0f;

        /// <summary>ShapeMatch 匹配分数下限（低于此值判 NG）</summary>
        public double MinScore { get; set; } = 0.5;

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
