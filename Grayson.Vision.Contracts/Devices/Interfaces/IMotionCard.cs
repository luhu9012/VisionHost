using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices.Enums;

namespace Grayson.Vision.Contracts.Devices
{
    #region 关联枚举与数据结构

    /// <summary>轴运动状态</summary>
    public enum AxisState
    {
        Idle = 0,           // 空闲
        Moving = 1,         // 正在运动
        Error = 2           // 异常/报警
    }

    /// <summary>轴硬件状态标志（可按位组合）</summary>
    [Flags]
    public enum AxisStatusFlags
    {
        Normal = 0,
        FwdLimit = 1 << 0,  // 正限位触发
        RevLimit = 1 << 1,  // 负限位触发
        Alarm = 1 << 2,     // 伺服报警
        HomeSwitch = 1 << 3 // 原点信号
    }

    /// <summary>基础运动参数配置</summary>
    public struct MotionParam
    {
        public float Speed { get; set; }      // 运行速度 (Units/s)
        public float Accel { get; set; }       // 加速度 (Units/s^2)
        public float Decel { get; set; }       // 减速度 (Units/s^2)

        public float Lspeed { get; set; }          // 起始速度
        public float Unit { get; set; }         // 脉冲当量
        public float Sramp { get; set; }             // S曲线速度
        public float CreepSpeed { get; set; }    // 爬行速度/回零慢速 (Units/s)
    }

    /// <summary>插补轨迹坐标点结构</summary>
    public struct InterpolationPoint
    {
        public float[] Position { get; set; }   // 多轴坐标数组
    }

    #endregion

    /// <summary>运动控制卡统一接口</summary>
    public interface IMotionCard : IDevice
    {
        #region 1. 轴基本配置与参数设置

        /// <summary>设置轴类型 (Pulse, EtherCAT, Encoder等)</summary>
        Result SetAxisType(int axis, int atype);

        /// <summary>设置脉冲当量 (1mm对应多少脉冲)</summary>
        Result SetUnits(int axis, float units);

        /// <summary>设置轴基础运动参数 (速度/加减速)</summary>
        Result SetMotionParam(int axis, MotionParam param);

        /// <summary>
        /// 回读轴基础运动参数 (速度/加减速/当量等)。
        /// 供硬件控制台等 UI 显示控制器当前真实参数；不支持的控制器返回 Fail（调用方回退默认值）。
        /// </summary>
        Result<MotionParam> GetMotionParam(int axis);

        /// <summary>设置软限位</summary>
        Result SetSoftLimits(int axis, float positiveLimit, float negativeLimit);

        /// <summary>
        /// 回读轴软限位。Data 为 float[2]：[0]=正软限位，[1]=负软限位。
        /// 不支持的控制器返回 Fail（调用方回退默认值）。
        /// </summary>
        Result<float[]> GetSoftLimits(int axis);

        /// <summary>设置轴指令位置 (DPOS)</summary>
        Result SetCommandPosition(int axis, float position);

        /// <summary>读取轴指令位置 (DPOS)</summary>

        Result<float> GetCommandPosition(int axis);

        /// <summary>读取轴反馈/编码器位置 (MPOS/Encoder)</summary>
        Result<float> GetFeedbackPosition(int axis);

        /// <summary>读取轴当前运行速度</summary>
        Result<float> GetCurrentSpeed(int axis);

        /// <summary>读取轴状态标志</summary>
        Result<AxisStatusFlags> GetAxisStatus(int axis);

        /// <summary>查询轴是否处于空闲状态</summary>
        Result<bool> IsAxisIdle(int axis);

        #endregion

        #region 2. 轴使能与停止控制

        /// <summary>设置伺服使能</summary>
        Result SetAxisEnable(int axis, bool enable);

        /// <summary>单轴停止 (按照设置减速停止)</summary>
        Result StopAxis(int axis);

        /// <summary>全轴急停/快速停止</summary>
        Result RapidStop();

        #endregion

        #region 3. 单轴运动 & JOG & 回零

        /// <summary>单轴点动运动 (JOG/VMOVE)</summary>
        /// <param name="axis">轴号</param>
        /// <param name="direction">1 为正向，-1 为负向</param>
        Result JogMove(int axis, int direction);

        /// <summary>单轴相对位置运动 (Move)</summary>
        Result MoveRelative(int axis, float distance, float speed);

        /// <summary>单轴绝对位置运动 (MoveAbs)</summary>
        Result MoveAbsolute(int axis, float position, float speed);

        /// <summary>
        /// 【门型运动 Jump】先垂直抬到 limZ → 在 limZ 高度水平移动到目标 XY → 垂直降到目标 Z。
        ///
        /// 用途：**吸持工件时的平移**。与逐轴 <see cref="MoveAbsolute"/> 的本质区别：
        ///   ① 四轴一次性下发，末端不走"逐轴拆两步"的 L 形（不在中间拐角点停一次）；
        ///   ② 可指定**安全通过高度 limZ**，从制度上避免"低位横穿"刮碰。
        /// 实测依据（ST_002）：同一对点，L 形路径第二段最紧点离内圈边界仅 1.5mm（贴边飞过），
        /// 门型走直线的最紧点余量 31.3mm。
        ///
        /// 属 PTP 类运动（速度档为百分比）。**不支持的卡必须返回 Fail**，
        /// 由调用方显式降级为"分段走位"，不允许静默按单轴平移顶替。
        /// </summary>
        /// <param name="limZ">水平段高度 (mm)，必须【高于目标 Z】，否则门型失效（退化成贴地横穿）</param>
        Result MoveJump(float x, float y, float z, float u, float limZ, float speed);

        /// <summary>轴单轴回零 (Home/Datum)</summary>
        /// <param name="axis">轴号</param>
        /// <param name="homeMode">回零模式</param>
        Result Home(int axis, int homeMode);

        #endregion

        #region 4. IO 配置与读写

        /// <summary>读取数字输入口 (IN)</summary>
        Result<bool> GetInput(int ioNum);

        /// <summary>设置数字输出口 (OUT)</summary>
        Result SetOutput(int ioNum, bool state);

        /// <summary>读取数字输出口状态</summary>
        Result<bool> GetOutput(int ioNum);

        /// <summary>配置轴的硬件信号映射 (原点、正负限位、报警信号分配)</summary>
        Result ConfigAxisSignals(int axis, int homeIo, int fwdIo, int revIo, int alarmIo);

        /// <summary>设置输入口信号极性反转</summary>
        Result SetInputInvert(int ioNum, bool isInverted);

        /// <summary>读取模拟量输入 (AD)</summary>
        Result<float> GetAnalogInput(int ioNum);

        /// <summary>设置模拟量输出 (DA)</summary>
        Result SetAnalogOutput(int ioNum, float value);

        #endregion

        #region 5. 多轴插补 & 连续插补 (Interpolation)

        /// <summary>多轴直线插补 (相对/绝对)</summary>
        /// <param name="axisList">参与插补的轴列表</param>
        /// <param name="targetPositions">各轴目标终点坐标数组</param>
        /// <param name="speed">合成运动速度</param>
        /// <param name="isAbsolute">是否为绝对坐标系统</param>
        Result LineInterpolation(int[] axisList, float[] targetPositions, float speed, bool isAbsolute = true);

        /// <summary>圆弧插补 (通过起点、中点、终点三点决定空间圆弧)</summary>
        /// <param name="axisList">参与插补的轴列表</param>
        /// <param name="midPoint">弧线中点坐标数组</param>
        /// <param name="endPoint">弧线终点坐标数组</param>
        /// <param name="speed">合成运动速度</param>
        /// <param name="isAbsolute">是否为绝对坐标系统</param>
        Result CircularInterpolation(int[] axisList, float[] midPoint, float[] endPoint, float speed, bool isAbsolute = true);

        /// <summary>连续路径插补 (将多段路径压入缓冲区顺序执行)</summary>
        /// <param name="axisList">参与插补的轴列表</param>
        /// <param name="points">插补点轨迹序列</param>
        /// <param name="speed">合成运动速度</param>
        Result ContinuousInterpolation(int[] axisList, List<InterpolationPoint> points, float speed);

        #endregion
    }

    /// <summary>
    /// 运动卡【主动探活】可选能力（2026-09-16 新增）。
    ///
    /// 【为什么需要】
    ///   `IDevice.State` 只反映"当初连上了"。当通信对端进程/脚本任务崩溃、或中途断网时，
    ///   TCP 会进入**半死**状态：本地套接字仍报 Connected，直到下一次 Write 抛 IOException
    ///   才现形 ⇒ 故障被推迟到"第一条业务指令"上，现场只看到业务侧报"通道忙/无应答"，
    ///   真因（对端已不在了）被完全淹没。
    ///
    /// 【实现约定】
    ///   · 必须是**轻量往返**（如 Epson TCP 通道的 PING→PONG），毫秒级、无副作用、允许只读；
    ///   · 通道正被其它命令占用、无法判定时**必须返回 true**（宁可漏报，不可误报，
    ///     否则会在机械手运动中途把连接断掉）；
    ///   · 只支持 TCP/网络型通道的设备需要实现；本地卡/仿真通道不必实现本接口。
    ///
    /// 【调用方约定】
    ///   · 只在**业务循环开跑前**（无运动在途时）调用，返回 false ⇒ 应断开并重连，重连后仍
    ///     false 则向上抛带明确处置指引的异常（别把"对端已死"含糊成"通道忙"）。
    /// </summary>
    public interface IMotionCardHealthProbe
    {
        /// <summary>主动探活。false = 通道已死（应断开重连）；true = 存活或无法判定。</summary>
        bool ProbeAlive(int timeoutMs = 1000);
    }
}