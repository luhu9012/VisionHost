using System;
using System.Collections.Generic;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Abstractions
{
    /// <summary>
    /// 设备操作结果。★ 铁律：<see cref="RawCode"/> 必须保留控制器原始错误码
    /// （4001 / 4007 / 2997 …），不允许改写成"操作失败"。
    /// </summary>
    public sealed class OpResult
    {
        public bool Ok;
        public int? RawCode;
        public string Message;

        public static OpResult Success()
        {
            return new OpResult { Ok = true };
        }

        public static OpResult Fail(string message, int? rawCode = null)
        {
            return new OpResult { Ok = false, Message = message, RawCode = rawCode };
        }

        public CalibError ToError(CalibFailureKind kind = CalibFailureKind.MotionRejected)
        {
            var e = CalibError.Create(kind, string.IsNullOrEmpty(Message) ? kind.ToString() : Message);
            if (RawCode.HasValue)
            {
                e.WithRawCode(RawCode.Value);
            }

            return e;
        }

        public override string ToString()
        {
            return Ok ? "OK" : string.Format("FAIL{0}: {1}", RawCode.HasValue ? " raw=" + RawCode.Value : string.Empty, Message);
        }
    }

    /// <summary>
    /// 零运动校核结果（<c>CHECK</c>）：只问控制器"这个点合法吗"，<b>不发车</b>。
    /// ★ 上位机没有臂长与关节限位，一律让控制器回答，绝不自己写逆解或奇异判断。
    /// </summary>
    public sealed class ReachCheckResult
    {
        /// <summary>null = 控制器未答复（该指令不被支持 / 断线）。</summary>
        public bool? TargetOk;

        public int? RawCode;
        public string Message;

        public bool Answered
        {
            get { return TargetOk.HasValue; }
        }

        public bool IsReachable
        {
            get { return TargetOk.HasValue && TargetOk.Value; }
        }

        public CalibError ToError(int sampleIndex)
        {
            var e = CalibError.Create(
                CalibFailureKind.Unreachable,
                Answered ? "目标点不可达（零运动校核返回 NG）" : "目标点可达性无法判定（控制器未答复）");
            if (RawCode.HasValue)
            {
                e.WithRawCode(RawCode.Value);
            }

            return e.WithSample(sampleIndex);
        }
    }

    /// <summary>
    /// 安全守卫：软限位 / Z 范围 / 可达域三重检查。
    /// ★ 注意软限位的权威在控制器侧；上位机的设置接口是空实现（假装成功）。
    ///   因此本工具<b>只做只读校验</b>，写限位一律明确失败，不假成功。
    /// </summary>
    public interface ISafetyGuard
    {
        /// <summary>返回 null 表示通过。</summary>
        CalibError Validate(MotionPose target);
    }

    /// <summary>
    /// 运动门面。
    /// ★ 关键运动语义（Epson RC+ 实测结论，不得改变）：
    ///   · <c>MOVE</c>(=Go) 是<b>异步</b>的：回 DONE 只代表"已受理"，不代表到位；
    ///     标定要精确到点，一律用 <c>LMOVE</c>(=Move) 同步到位。
    ///   · <c>/L</c> <c>/R</c> 是<b>手系</b>，不是"不等待"。左手工位强制右手解会报 4007 / 崩任务 / 断连。
    ///   · CP 速度（SpeedS/AccelS）与 PTP 速度（Speed/Accel）是两套参数，且会被 INIT / MOTOR ON 复位。
    /// </summary>
    public interface IMotionGateway
    {
        /// <summary>运行环境标识：Simulation / ContractsDevice / LocalDevice。</summary>
        string EnvironmentKind { get; }

        bool IsSimulated { get; }

        /// <summary>当前位（下发目标）。</summary>
        MotionPose GetPosition();

        /// <summary>★ 控制器<b>反馈位</b>。标定真值一律取这个（不是下发值）。</summary>
        MotionPose GetFeedbackPosition();

        Handedness GetHandedness();

        /// <summary>同步直线运动到目标位（LMOVE）。</summary>
        OpResult MoveLinear(MotionPose target, double speedMmPerSec, double accelMmPerSec2);

        /// <summary>同步旋转到目标角（保持 XY 不动）。</summary>
        OpResult MoveRotate(double absoluteU, double speedDegPerSec, double accelDegPerSec2);

        OpResult Home();

        OpResult Stop();

        /// <summary>以 mm/s 与 mm/s² 设定 CP 速度（管 Move）。</summary>
        OpResult ApplyCpSpeed(double speedMmPerSec, double accelMmPerSec2);

        /// <summary>零运动校核：只问合法性，不发车。</summary>
        ReachCheckResult CheckTarget(MotionPose target);
    }

    /// <summary>
    /// 取图门面。
    /// ★ 硬约束：标定采样必须"走位 → 软触发 → 本点新帧"，
    ///   <b>禁用连续自由流</b>。连续流下"等新帧"等于等一个随机时刻：
    ///   慢帧必超时，还可能拿到<b>上一个位置</b>的帧 —— 这是历史上最难查的一类标定错误。
    /// </summary>
    public interface ICameraGateway
    {
        string EnvironmentKind { get; }

        bool IsSimulated { get; }

        int Width { get; }

        int Height { get; }

        /// <summary>切到软触发模式（每次取图前必须已配置）。</summary>
        OpResult ConfigureSoftwareTrigger();

        /// <summary>软触发并取一帧（阻塞到新帧到达或超时）。返回 null 表示超时/失败。</summary>
        byte[] GrabFrame(int timeoutMs, out CalibError error);
    }

    /// <summary>
    /// ★ 可选能力：由调用方指定"这一张要拍第几个姿态"。<b>只有仿真需要实现它</b>。
    ///
    /// 为什么必须有：人工摆板链会<b>重拍</b> —— 一张没找到板就再按一次快门，此时操作员
    /// 摆的<b>还是同一个姿态</b>。真机上这天然成立（板是操作员摆的，不会因为多按一次快门
    /// 就自己换个角度）；但仿真的相机会在每次取图时往前走一格，于是重拍会让
    /// "提示说的是第 3 张" 和 "实际渲染的是第 4 个姿态" 错位。
    ///
    /// ★ 这个错位的隐蔽之处：残差、κ、主点<b>全都正常</b>，唯一露出来的地方是覆盖度判据
    ///   说"一张都没斜" —— 极容易被误读成"操作员没摆好"，然后去改提示文案，方向完全错。
    ///   所以让编排器在每次取图前<b>显式告诉相机要拍哪个姿态</b>，重拍时自然重画同一张。
    /// </summary>
    public interface IPoseScriptedCamera
    {
        /// <summary>指定下一次取图用第 <paramref name="index"/> 个姿态（0 起）。</summary>
        void SelectPose(int index);
    }

    /// <summary>标定产物存储。★ 只写自己的目录，<b>不写主项目的库</b>。</summary>
    public interface ICalibStore
    {
        /// <summary>存储根目录。</summary>
        string RootDir { get; }

        /// <summary>保存采样帧留档，返回落盘路径（失败返回 null，不影响标定流程）。</summary>
        string SaveFrame(string sessionId, int sampleIndex, byte[] encodedBytes, string extension);

        /// <summary>保存会话全文（可回放）。</summary>
        string SaveSession(string sessionId, string json);

        /// <summary>保存中性导出产物（.calib.json + .tup）。返回导出目录。</summary>
        string SaveExport(string sessionId, string exportJson, HomMat2D? h);

        /// <summary>列出已有标定（首屏用）。</summary>
        IList<string> ListExports();
    }

    /// <summary>
    /// ★ 可选能力：在本次会话的导出目录里再写一份<b>附加产物</b>
    /// （内参链的 <c>intrinsics.json</c> 走这里）。
    ///
    /// 为什么做成可选接口而不是往 <see cref="ICalibStore"/> 里加方法：
    /// 加方法等于让所有已有实现（含宿主实现）当场编译不过 —— 而"多写一个文件"
    /// 显然不该有这种破坏性。宿主若不实现，工具只提示"这份附加产物没落盘"，不影响主流程。
    /// </summary>
    public interface ICalibStoreExtras
    {
        /// <summary>写附加文件，返回落盘路径；失败返回 null。</summary>
        string SaveExtraFile(string sessionId, string fileName, string content);
    }

    /// <summary>日志。向导"卡哪说哪"依赖这里的结构化步骤。</summary>
    public interface ICalibLog
    {
        void Info(string message);

        void Warn(string message);

        void Error(string message, Exception ex = null);

        /// <summary>步骤级日志（会进会话 Steps 记录）。</summary>
        void Step(string stepKey, string message);
    }

    /// <summary>
    /// 用户交互。★ 向导全靠它：所有要用户拍板的地方都从这里走，
    /// 便于仿真模式下自动作答（回归测试可无人跑完整流程）。
    /// </summary>
    public interface IUserPrompt
    {
        bool Confirm(string title, string message);

        void Inform(string title, string message);

        void Warn(string title, string message);

        void ShowError(string title, string message, CalibError error);

        /// <summary>选择一项，返回索引；取消返回 -1。</summary>
        int Select(string title, string message, IList<string> options);
    }

    /// <summary>
    /// ★ 宿主回调发布（产物落地的唯一接缝）。
    /// 嵌入模式下由主项目实现：把 <see cref="CalibExport"/> 转成 CalibrationArtifact 落库。
    /// <b>null 表示没有宿主</b> → 退化为"只导出文件"，独立模式即走这条路。
    /// 这样工具侧零耦合，嵌入时又全自动。
    /// </summary>
    public interface ICalibrationPublisher
    {
        /// <summary>发布标定产物。返回是否成功。</summary>
        bool Publish(CalibExport export, out string message);

        /// <summary>发布者名称（用于日志与产物溯源）。</summary>
        string Name { get; }
    }

    /// <summary>
    /// ★★ 防腐层根接口：宿主注入的一切都从这里来。
    /// 这是整个工具唯一"知道外部世界长什么样"的地方。
    /// </summary>
    public interface IVisualCalibEnvironment
    {
        /// <summary>环境标识：Simulation / ContractsDevice / LocalDevice。</summary>
        string Kind { get; }

        IMotionGateway Motion { get; }

        ICameraGateway Camera { get; }

        ICalibStore Store { get; }

        ICalibLog Log { get; }

        IUserPrompt Prompt { get; }

        /// <summary>可为 null（无宿主 → 退化为文件导出）。</summary>
        ICalibrationPublisher Publisher { get; }

        /// <summary>可注入的时钟（便于测试）。</summary>
        DateTime Now { get; }

        /// <summary>读取/推导当前物理拓扑（向导第 2 步回显用）。</summary>
        CalibTopology ReadTopology();

        /// <summary>
        /// ★★ 把用户在第 2 步确认/改写过的拓扑写回去。
        ///
        /// 为什么这个方法是必需的（修前它不存在 —— 于是「我改」在架构上就根本做不到）：
        ///   第 2 步的设计是「我推导 → 你确认 / 纠正」。可这个防腐层接口**只给了读**：
        ///   <see cref="ReadTopology"/> 的注释写着"向导第 2 步回显用"，而底层实现
        ///   <c>SimulatedEnvironment.ApplyTopology</c> 的注释明明写着
        ///   「允许上层改写拓扑（向导第 2 步用户『我改』）」—— **上层却够不着它**。
        ///   于是界面写着「不对，我改」，而"改"没有任何通路。
        ///   **读写不对称的接口，等于把那个功能判了死刑。**
        ///
        /// ★ 语义：传入的是<b>用户确认过的完整拓扑</b>（不是"改哪一项"的补丁），
        ///   实现方可以整体替换。
        /// ★ 返回 false = 这次写回没被接受。实现方有权拒绝（例如工位配置是只读的），
        ///   而调用方**必须把失败如实告诉用户**，不许静默按旧值继续 ——
        ///   否则用户以为改生效了，实际跑的还是旧参数。
        /// </summary>
        bool ApplyTopology(CalibTopology topology);
    }
}
