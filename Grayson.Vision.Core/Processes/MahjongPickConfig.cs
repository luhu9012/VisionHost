namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 双滑台工件吸取业务过程的工位位置与节拍配置。
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
        /// <summary>左工台拍照位：左Y轴(轴3)绝对坐标，工件对准相机视野中心</summary>
        public float LeftPhotoY { get; set; } = 0f;
        /// <summary>左工台拍照位：X轴(轴1)绝对坐标，相机对准左工台</summary>
        public float LeftPhotoX { get; set; } = 0f;
        /// <summary>吸取时 X 轴附加偏移 = 实测的「相机对准工件 → 移 X 轴让吸嘴对准」偏距（-980）。
        /// 标定矩阵输出的是"工件处于相机视野中心"的 X 轴坐标，吸嘴在相机 X 负方向 980 →
        /// 吸嘴对准坐标 = 矩阵输出 + NozzleXOffset。吸嘴与 Z 轴同轴无偏心，但相机与吸嘴存在 X 向偏距。
        /// ⚠ 勿清零：此值由现场"相机对准→吸嘴对准"实测得出，清零会导致吸嘴偏离工件。</summary>
        public float NozzleXOffset { get; set; } = -1010.3f;
        /// <summary>吸取时左Y轴附加偏移（实测相机-吸嘴 Y 向残差，-5；一般很小或为 0）</summary>
        public float NozzleYOffset { get; set; } = 66.2f;

        /// <summary>
        /// 视觉引导坐标是否做「关于拍照位镜像」修正。默认 false（保持旧行为，仅模板点位正确）。
        ///
        /// 背景（"工件只有放在模板点位才成功"的核心原因）：
        ///   标定矩阵输出 (wx, wy) 的物理含义是——"若 Mark 仍在标定位置，要把轴开到 wx
        ///   才能让它出现在当前工件的像素处"；而吸取要的是"追上已经挪窝的目标"。
        ///   两者关于拍照位镜像：
        ///       正确落点 = 2 × 拍照位 − 矩阵输出
        ///   工件恰在标定（模板）点位时 wx = 拍照位，两套公式相等，所以旧代码只在那个点位命中；
        ///   一旦偏移 d，旧代码误差就是 2d（Y 轴更糟：左Y 是工台轴，移动会带着工件一起走，
        ///   方向反 + 位移叠加，净偏 2d）。
        ///
        /// ⚠ 切换此开关后务必先单步观察 XY 落点（Z 已抬至安全高度）确认方向，再投自动运行。
        ///    运行时日志会同时打印两套公式的落点，现场无需真走位即可 A/B 对照判断。
        /// </summary>
        public bool MirrorGuideEnabled { get; set; } = false;

        /// <summary>
        /// 示教模式：Phase2 只计算并打印落点，**不执行 XY 走位与 Z 下探吸取**，流程安全结束。
        ///
        /// 用途：人工手动点动轴，让吸嘴正对工件背面圆/十字的中心，再从界面读回轴坐标
        /// 代入日志给出的公式，即可一次标定出 NozzleXOffset / NozzleYOffset。
        ///
        /// 为什么用「吸嘴对准工件」当基准，而不是「相机视野中心」：
        ///   视野中心在屏幕上没有精确参照，肉眼只能估到 ±10px 量级；吸嘴对准工件背面
        ///   图案则可以下探目测、也能用真空吸附一试便知，精度高一个量级。而且真正需要
        ///   精确的正是这一步——标定矩阵只管相对量，绝对基准全靠 NozzleOffset 兜。
        /// </summary>
        public bool TeachMode { get; set; } = true;

        // ---- Z 轴高度 ----
        /// <summary>Z 轴安全高度（XY 运动时 Z 必须在此之上）</summary>
        public float SafeZ { get; set; } = -20f;
        /// <summary>Z 轴吸取下探高度</summary>
        public float PickZ { get; set; } = 110f;
        /// <summary>Z 轴放料下探高度</summary>
        public float PlaceZ { get; set; } = 40f;

        // ---- 右工台（摆盘放料）----
        public float RightPlaceX { get; set; } = 1700f;
        public float RightPlaceY { get; set; } = 0f;

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
        public int VacuumSenseTimeoutMs { get; set; } = 3000;
        /// <summary>NG（视觉/吸取失败）时红灯+蜂鸣器提示时长 ms，到时自动回黄灯待机</summary>
        public int NgIndicateMs { get; set; } = 3000;
        /// <summary>运行周期内点亮启动按钮指示灯（OUT4/OUT5，指示设备工作中）</summary>
        public bool LightStartLamps { get; set; } = true;
        /// <summary>周期开始前记录各轴原点信号状态（诊断用，不影响流程）</summary>
        public bool LogOriginOnStart { get; set; } = true;
        /// <summary>开真空后保压等待 ms（真空检知未启用时的保压时长）</summary>
        public int VacuumOnDelayMs { get; set; } = 2000;
        /// <summary>
        /// 真空检知到位后的额外保压 ms（吸住后再等一会才抬 Z）。
        /// 真空表检出只代表真空度门槛刚过，吸附力仍在爬升——立即抬 Z 有概率带不牢
        /// （表面略不平/密封圈未完全贴合时尤其明显）。0=检知即抬。
        /// 注意：新增属性对已建工位立即生效（LiteDB 旧 JSON 缺此字段时回落到本默认值）。
        /// </summary>
        public int VacuumDwellAfterSenseMs { get; set; } = 400;
        /// <summary>关真空(破空)后等待 ms（确保脱落）</summary>
        public int VacuumOffDelayMs { get; set; } = 150;
        /// <summary>到位后机械稳定等待 ms（拍照前）</summary>
        public int SettleMs { get; set; } = 150;
        /// <summary>ShapeMatch 匹配分数下限（低于此值判定 NG）</summary>
        public double MinScore { get; set; } = 0.4;
        /// <summary>XY 运动速度（单位与轴 UNITS 一致，通常 mm/s）；&lt;=0 时沿用轴当前速度</summary>
        public float XySpeed { get; set; } = 1000f;

        /// <summary>Z 轴速度（通常比 XY 慢）；&lt;=0 时沿用轴当前速度</summary>
        public float ZSpeed { get; set; } = 300f;

        // ---- 待机位 ----
        public float StandbyX { get; set; } = 0f;
        public float StandbyY { get; set; } = 0f;
    }
}
