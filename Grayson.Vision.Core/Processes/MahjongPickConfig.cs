namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 双滑台麻将吸取业务过程的工位位置与节拍配置。
    /// 所有位置为轴绝对坐标（单位与轴脉冲当量一致，通常 mm）。
    /// 轴号约定：0=Z(升降) 1=X(左右) 2=右Y(右工台前后) 3=左Y(左工台前后)。
    /// </summary>
    public class MahjongPickConfig
    {
        /// <summary>运动控制卡逻辑别名（工位管理绑定名）</summary>
        public string CardAlias { get; set; } = "MotionCard";

        // ---- 轴号 ----
        public int AxisZ { get; set; } = 0;
        public int AxisX { get; set; } = 1;
        public int AxisRightY { get; set; } = 2;
        public int AxisLeftY { get; set; } = 3;

        // ---- 左工台（拍照+吸取）----
        /// <summary>左工台拍照位：左Y轴(轴3)绝对坐标，麻将对准相机视野中心</summary>
        public float LeftPhotoY { get; set; } = 0f;
        /// <summary>左工台拍照位：X轴(轴1)绝对坐标，相机对准左工台</summary>
        public float LeftPhotoX { get; set; } = 0f;
        /// <summary>吸取时 X 轴附加偏移（相机-吸嘴 X 方向机械偏置，若标定已以吸嘴为参考则填 0）</summary>
        public float NozzleXOffset { get; set; } = 0f;
        /// <summary>吸取时左Y轴附加偏移（若标定输出已含 Y 修正则填 0）</summary>
        public float NozzleYOffset { get; set; } = 0f;

        // ---- Z 轴高度 ----
        /// <summary>Z 轴安全高度（XY 运动时 Z 必须在此之上）</summary>
        public float SafeZ { get; set; } = 0f;
        /// <summary>Z 轴吸取下探高度</summary>
        public float PickZ { get; set; } = -10f;
        /// <summary>Z 轴放料下探高度</summary>
        public float PlaceZ { get; set; } = -8f;

        // ---- 右工台（摆盘放料）----
        public float RightPlaceX { get; set; } = 0f;
        public float RightPlaceY { get; set; } = 0f;

        // ---- 真空与节拍 ----
        /// <summary>真空阀输出口编号</summary>
        public int VacuumIo { get; set; } = 0;
        /// <summary>开真空后保压等待 ms（确保吸牢）</summary>
        public int VacuumOnDelayMs { get; set; } = 200;
        /// <summary>关真空(破空)后等待 ms（确保脱落）</summary>
        public int VacuumOffDelayMs { get; set; } = 150;
        /// <summary>到位后机械稳定等待 ms（拍照前）</summary>
        public int SettleMs { get; set; } = 150;
        /// <summary>ShapeMatch 匹配分数下限（低于此值判定 NG）</summary>
        public double MinScore { get; set; } = 0.5;
        /// <summary>XY 运动速度；0 = 使用轴默认参数</summary>
        public float XySpeed { get; set; } = 0f;
        /// <summary>Z 轴速度（通常比 XY 慢）；0 = 使用轴默认参数</summary>
        public float ZSpeed { get; set; } = 0f;

        // ---- 待机位 ----
        public float StandbyX { get; set; } = 0f;
    }
}
