using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Plugins.Robot.Epson
{
    // ======================================================================
    // Epson 机器人 SDK 适配层
    //
    // 设计目的：
    //   EpsonRobot（对外实现 IMotionCard / IIoDevice 契约）不直接引用
    //   RCAPINet.Spel 类型，而是通过本适配层访问控制器。好处：
    //   1. 本机未安装 Epson RC+（无 spelnet64.dll）时工程依然可编译，
    //      自动切换“离线仿真机械手”，业务流可先行联调；
    //   2. RC+ 大版本 API 差异（RC+ 7 / RC+ 8）只需修改 SpeLNetAdapter.cs
    //      一个文件，业务代码零改动。
    //
    // 轴约定（SCARA 4 轴）：0 = X（大臂）、1 = Y（小臂）、2 = Z（上下）、3 = U（旋转）。
    // IO 约定：外部一律使用 0 基编号；适配层内部负责换算成 SPEL+ 的 1 基编号。
    // ======================================================================

    /// <summary>
    /// Epson 控制器 SDK 适配器接口。
    /// 约定：返回 null 表示成功，非 null 为错误消息（对接 Result.Fail）。
    /// </summary>
    internal interface IEpsonSdkController : IDisposable
    {
        /// <summary>是否仿真适配层</summary>
        bool IsSimulated { get; }

        /// <summary>当前是否已连接控制器</summary>
        bool IsOpen { get; }

        /// <summary>连接控制器（address 为控制器 IP 或 "localhost"）</summary>
        string Open(string address);

        /// <summary>断开连接</summary>
        string Close();

        /// <summary>伺服上电/下电（MotorsOn）</summary>
        string SetMotorsOn(bool on);

        /// <summary>
        /// 四轴全坐标定位运动。
        /// x/y/z/u 为各轴目标位置（mm / 度）；speed &lt;= 0 时使用当前速度；
        /// linear = true 走 CP 直线插补（Move），false 走 PTP（Go）。
        /// </summary>
        string MoveTo(float x, float y, float z, float u, float speed, bool linear);

        /// <summary>回机械原点（Home）</summary>
        string Home();

        /// <summary>减速停止当前运动（Halt）</summary>
        string Halt();

        /// <summary>读取轴位置（真实层为指令位置跟踪，见实现注释）</summary>
        float GetPosition(int axis);

        /// <summary>当前运动是否已完成</summary>
        bool IsMotionDone();

        /// <summary>数字输出（0 基编号，适配层内部 +1 转 SPEL+ 1 基）</summary>
        string SetOutput(int ioNumber, bool state);

        /// <summary>数字输入读取（0 基编号）；读取失败返回 null</summary>
        bool? GetInput(int ioNumber);
    }

    /// <summary>Epson SDK 工厂</summary>
    internal interface IEpsonSdk
    {
        /// <summary>是否仿真适配层</summary>
        bool IsSimulated { get; }

        /// <summary>创建控制器适配实例（未连接，随后调用 Open）</summary>
        IEpsonSdkController CreateController();
    }

    /// <summary>
    /// SDK 工厂入口：按 DLLLib 下 SDK DLL 是否存在自动选择适配层（优先级从高到低）：
    ///   1. DLLLib\spelnet64.dll → RC+ 8.0 真实 SDK（EPSON_SPEL 宏，SpeLNetAdapter.cs）；
    ///   2. DLLLib\RCAPINet.dll  → RC+ 7.x 真实 SDK（EPSON_SPEL7 宏，RCAPINet7Adapter.cs）
    ///      ★ 当前调试形态：本机装有 RC+ 7.5.1 模拟器（D:\EpsonRC70），走这一层；
    ///   3. 都没有 → 离线仿真机械手（运动瞬时完成、IO 输入回环输出）。
    /// </summary>
    internal static class EpsonSdkFactory
    {
        private static IEpsonSdk _instance;
        private static readonly object _lock = new object();

        public static IEpsonSdk Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
#if EPSON_SPEL
                        _instance = new SpeLNetSdk();
#elif EPSON_SPEL7
                        _instance = new RCAPINet7Sdk();
#else
                        // 未检测到任何 Epson SDK DLL，使用离线仿真机械手：
                        // 运动瞬时完成、IO 输入回环输出，业务流可完整走通。
                        _instance = new SimulatedEpsonSdk();
#endif
                    }
                    return _instance;
                }
            }
        }
    }

    /// <summary>
    /// Epson IO 业务号 → SPEL+ 物理端口号映射（现场实测 2026-09-02）。
    ///
    /// 业务约定（EpsonRobot / 调试台 / 工位业务流统一使用 0 基业务号）：
    ///   0 = 吸嘴1 真空阀 → SPEL+ Out(15)   （On/Off 15）
    ///   1 = 吸嘴2 真空阀 → SPEL+ Out(14)   （On/Off 14，单吸嘴机型无此阀）
    /// 其他业务号（预留 IO）按「业务号 + 1」换算为 SPEL+ 1 基端口。
    ///
    /// ⚠ 真空阀接的可能是机械手控制器 IO 板或远程/本地 IO 模块，端口号因机型/接线而异，
    ///   换机接线后只需改本映射表，业务层零改动。
    /// </summary>
    internal static class EpsonIoMap
    {
        public static int SpelPort(int ioNumber)
        {
            switch (ioNumber)
            {
                case 0: return 15;   // 吸嘴1 真空
                case 1: return 14;   // 吸嘴2 真空
                default: return ioNumber + 1;
            }
        }
    }

    // ======================================================================
    // 离线仿真适配层
    // ======================================================================

    /// <summary>
    /// Epson 机器人离线仿真实现。
    ///
    /// 用途：本机未安装 RC+ / 未接真实控制器时，提供行为完整的“虚拟 SCARA”：
    /// - 运动指令瞬时完成（IsMotionDone 恒为 true）；
    /// - 输出状态软件记录；输入直接回环输出状态（便于测试 WaitSignal 类节点）；
    /// - 位置采用指令位置跟踪，与真实层行为一致。
    /// </summary>
    internal sealed class SimulatedEpsonSdk : IEpsonSdk
    {
        public bool IsSimulated => true;

        public IEpsonSdkController CreateController()
        {
            return new SimulatedEpsonController();
        }
    }

    internal sealed class SimulatedEpsonController : IEpsonSdkController
    {
        private readonly float[] _pos = new float[4];           // X Y Z U 指令位置
        private readonly Dictionary<int, bool> _outputs = new Dictionary<int, bool>();
        private bool _open;
        private bool _motorsOn;

        public bool IsSimulated => true;
        public bool IsOpen => _open;

        public string Open(string address)
        {
            _open = true;
            Debug.WriteLine($"[SimEpson] 虚拟 Epson 控制器 {address} 已连接（离线仿真模式）");
            return null;
        }

        public string Close()
        {
            _open = false;
            return null;
        }

        public string SetMotorsOn(bool on)
        {
            _motorsOn = on;
            return null;
        }

        public string MoveTo(float x, float y, float z, float u, float speed, bool linear)
        {
            if (!_open) return "控制器未连接";
            if (!_motorsOn) return "伺服未上电，无法运动";
            // 仿真：瞬时到位
            _pos[0] = x; _pos[1] = y; _pos[2] = z; _pos[3] = u;
            return null;
        }

        public string Home()
        {
            if (!_open) return "控制器未连接";
            for (int i = 0; i < 4; i++) _pos[i] = 0f;
            return null;
        }

        public string Halt() => null;

        public float GetPosition(int axis)
        {
            return (axis >= 0 && axis < 4) ? _pos[axis] : 0f;
        }

        public bool IsMotionDone() => true;

        public string SetOutput(int ioNumber, bool state)
        {
            _outputs[ioNumber] = state;
            Debug.WriteLine($"[SimEpson] Out({ioNumber + 1}) = {(state ? "On" : "Off")}");
            return null;
        }

        /// <summary>仿真输入 = 输出回环（未写过的口默认 Off）</summary>
        public bool? GetInput(int ioNumber)
        {
            bool v;
            return _outputs.TryGetValue(ioNumber, out v) ? v : false;
        }

        public void Dispose()
        {
            Close();
        }
    }
}
