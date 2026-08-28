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
        public float LeftPhotoY { get; set; } = -49f;
        /// <summary>左工台拍照位：X轴(轴1)绝对坐标，相机对准左工台</summary>
        public float LeftPhotoX { get; set; } = -5f;
        /// <summary>吸取时 X 轴附加偏移（相机-吸嘴 X 方向机械偏置，若标定已以吸嘴为参考则填 0）</summary>
        public float NozzleXOffset { get; set; } = -900f;
        /// <summary>吸取时左Y轴附加偏移（若标定输出已含 Y 修正则填 0）</summary>
        public float NozzleYOffset { get; set; } =0f;

        // ---- Z 轴高度 ----
        /// <summary>Z 轴安全高度（XY 运动时 Z 必须在此之上）</summary>
        public float SafeZ { get; set; } = -20f;
        /// <summary>Z 轴吸取下探高度</summary>
        public float PickZ { get; set; } = 126f;
        /// <summary>Z 轴放料下探高度</summary>
        public float PlaceZ { get; set; } = 0f;

        // ---- 右工台（摆盘放料）----
        public float RightPlaceX { get; set; } = 1700f;
        public float RightPlaceY { get; set; } = 280f;

        // ---- IO 映射（默认值按本机接线表，可在工位配置 JSON 覆盖）----
        // ── 输入（IN）──
        /// <summary>IN0: 左Y轴(轴3)原点信号</summary>
        public int InLeftYHome { get; set; } = 0;
        /// <summary>IN1: Z轴(轴0)原点信号</summary>
        public int InZHome { get; set; } = 1;
        /// <summary>IN2: X轴(轴1)原点信号</summary>
        public int InXHome { get; set; } = 2;
        /// <summary>IN3: 右Y轴(轴2)原点信号</summary>
        public int InRightYHome { get; set; } = 3;
        /// <summary>IN9: 急停按钮（触发电平见 EStopActiveHigh）</summary>
        public int InEStop { get; set; } = 9;
        /// <summary>IN10: 右启动按钮</summary>
        public int InRightStart { get; set; } = 10;
        /// <summary>IN11: 左启动按钮</summary>
        public int InLeftStart { get; set; } = 11;
        /// <summary>IN12: 复位按钮</summary>
        public int InReset { get; set; } = 12;
        /// <summary>IN13: Z轴吸嘴真空按钮（手动真空）</summary>
        public int InVacuumButton { get; set; } = 13;
        /// <summary>IN16: Z轴真空检知（真空表，有效电平见 VacuumSenseActiveHigh）</summary>
        public int InVacuumSense { get; set; } = 16;

        // ── 输出（OUT）──
        /// <summary>OUT0: 三色灯绿灯</summary>
        public int OutLightGreen { get; set; } = 0;
        /// <summary>OUT1: 三色灯黄灯</summary>
        public int OutLightYellow { get; set; } = 1;
        /// <summary>OUT2: 三色灯红灯</summary>
        public int OutLightRed { get; set; } = 2;
        /// <summary>OUT3: 三色灯蜂鸣器</summary>
        public int OutBuzzer { get; set; } = 3;
        /// <summary>OUT4: 右启动按钮指示灯</summary>
        public int OutRightStartLamp { get; set; } = 4;
        /// <summary>OUT5: 左启动按钮指示灯</summary>
        public int OutLeftStartLamp { get; set; } = 5;
        /// <summary>OUT6: Z轴吸嘴真空电磁阀（true=吸住，false=放开）</summary>
        public int VacuumIo { get; set; } = 6;

        // ---- 行为参数 ----
        /// <summary>急停按钮触发电平：true=IN9 高电平表示按下（点动常开接法）；常闭触点接法请设 false</summary>
        public bool EStopActiveHigh { get; set; } = true;
        /// <summary>真空检知有效电平：true=IN16 高电平表示已吸住（真空表输出 24V）</summary>
        public bool VacuumSenseActiveHigh { get; set; } = true;
        /// <summary>是否启用真空检知确认（false=不读 IN16，仅按 VacuumOnDelayMs 延时保压；未接真空表的设备用）</summary>
        public bool EnableVacuumSense { get; set; } = true;
        /// <summary>开真空后等待真空检知超时 ms（超时判定吸取失败 NG）</summary>
        public int VacuumSenseTimeoutMs { get; set; } = 500;
        /// <summary>NG（视觉/吸取失败）时红灯+蜂鸣器提示时长 ms，到时自动回黄灯待机</summary>
        public int NgIndicateMs { get; set; } = 3000;
        /// <summary>运行周期内点亮启动按钮指示灯（OUT4/OUT5，指示设备工作中）</summary>
        public bool LightStartLamps { get; set; } = true;
        /// <summary>周期开始前记录各轴原点信号状态（诊断用，不影响流程）</summary>
        public bool LogOriginOnStart { get; set; } = true;
        /// <summary>开真空后保压等待 ms（真空检知未启用时的保压时长）</summary>
        public int VacuumOnDelayMs { get; set; } = 200;
        /// <summary>关真空(破空)后等待 ms（确保脱落）</summary>
        public int VacuumOffDelayMs { get; set; } = 150;
        /// <summary>到位后机械稳定等待 ms（拍照前）</summary>
        public int SettleMs { get; set; } = 150;
        /// <summary>ShapeMatch 匹配分数下限（低于此值判定 NG）</summary>
        public double MinScore { get; set; } = 0.5;
        /// <summary>XY 运动速度（单位与轴 UNITS 一致，通常 mm/s）；&lt;=0 时沿用轴当前速度</summary>
        public float XySpeed { get; set; } = 70f;
        /// <summary>Z 轴速度（通常比 XY 慢）；&lt;=0 时沿用轴当前速度</summary>
        public float ZSpeed { get; set; } = 30f;

        // ---- 待机位 ----
        public float StandbyX { get; set; } = 0f;
    }
}
