#if EPSON_SPEL7
// ============================================================================
// Epson RC+ 7.5.1 SPEL .NET SDK（RCAPINet.dll）真实适配层 —— RCAPINet.Spel
//
// 启用条件：解决方案根目录 DLLLib\RCAPINet.dll 存在
//（编译宏 EPSON_SPEL7 由 csproj 按该文件是否存在自动定义）。
//
// ⚠ 与 RC+ 8.0（spelnet64.dll，见 SpeLNetAdapter.cs）的 API 差异
//   已通过 dnfile 反射 D:\EpsonRC70\exe\RCAPINet.dll 逐一核实：
//   1. 连接：RC+7 必须走老三样 —— Initialize() → Project(可选) → Connect(编号)；
//      RC+8 的 Connections[0].ConnectionString 写法在 7.5.1 里不存在（会编译失败）；
//   2. Go/Move：RC+7.5.1 的 RCAPINet.Spel **没有** (x,y,z,u) 四 double 重载！
//      只有 Go(SpelPoint) / Go(string 点表达式) / Go(int 点号) ——
//      四轴运动必须先构造 RCAPINet.SpelPoint 并赋 X/Y/Z/U 属性；
//   3. Speed：RC+7.5.1 里是**方法** Speed(int)，不是属性（GetSpeed() 读）；
//   4. WaitRobot：RC+7.5.1 的 RCAPINet.Spel **没有** WaitRobot！
//      Go/Move 默认同步阻塞（AsyncMode 属性默认 false，见 RC+ API 手册），
//      指令返回即到位，无需额外等待；
//   5. Halt：需要任务号（Halt(TaskNumber)）——RCAPINet 的指令在 CommandTask
//      属性指定的任务里执行，停止时用该任务号。
//
// 模拟器使用步骤（当前调试形态：本机 RC+ 7.5.1 Simulator 替代真实机械手）：
//   1. 启动 D:\EpsonRC70\GUI\EPSON_RC+.exe（或桌面快捷方式）；
//   2. 新建/打开一个工程（建议在 D:\EpsonRC70\projects 下，含一台 SCARA 机器人，
//      本工位机型按现场合同选，如 LS3-401S / T6 等，先随便选四轴 SCARA 即可联调）；
//   3. 菜单 [Run] 确认运行模式 = Simulator（模拟器），并编译工程无错误；
//   4. 保持 RC+ 打开，设备管理里 Epson 设备 DeviceId 填 "localhost"；
//   5. 工位启动 → 本适配层 Initialize + Connect(1) 连上本机 RC+ 运行时。
//   TODO(接实机)：真实控制器把 DeviceId 换成 RC+ [工具]-[控制器] 里配置的
//   连接号/连接名（Connect(ConnectionName) 重载），或在下方 Open 里扩展 IP 分支。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Plugins.Robot.Epson
{
    /// <summary>RC+ 7.5.1（RCAPINet）工厂适配器</summary>
    internal sealed class RCAPINet7Sdk : IEpsonSdk
    {
        public bool IsSimulated => false;

        public IEpsonSdkController CreateController()
        {
            return new RCAPINet7Controller();
        }
    }

    /// <summary>
    /// RCAPINet.Spel 单控制器适配器（默认 Robot 1）。
    /// 外部契约与 RC+8 适配层（SpeLNetController）/离线仿真层完全一致：
    /// 轴号 0=X 1=Y 2=Z 3=U；IO 外部 0 基 ↔ SPEL+ 1 基。
    /// </summary>
    internal sealed class RCAPINet7Controller : IEpsonSdkController
    {
        private RCAPINet.Spel _spel;

        /// <summary>指令位置跟踪（X Y Z U）——真实反馈见 GetPosition 注释 TODO</summary>
        private readonly float[] _pos = new float[4];

        /// <summary>输出口状态跟踪（SPEL+ 无输出回读指令，软件记录供 GetOutput 用）</summary>
        private readonly Dictionary<int, bool> _outputs = new Dictionary<int, bool>();

        public bool IsSimulated => false;

        public bool IsOpen
        {
            get { return _spel != null; }
        }

        /// <summary>
        /// 连接控制器（RC+ 7 智能连接，2026-08-31 v2 重写）。
        ///
        /// v1 直接 Connect(1) 在模拟器上抛 SpelException，根因：
        /// 连接号 1 不一定存在——RC+ 的连接是在
        /// [Setup]-[当前连接]/[控制器] 里配置的，连接号因工程而异。
        ///
        /// v2 流程（全部基于 RCAPINet 反射核实过的 API）：
        ///   1. new Spel() + Initialize()；
        ///   2. GetConnectionInfo() 枚举 RC+ 里**真实配置**的全部连接
        ///      （返回 SpelConnectionInfo[]，含连接号/名称/类型/IP）；
        ///   3. 按 address 智能选择：
        ///      - "localhost"/空 → 本机模拟器：优先 ConnectionType 为 Local 的，
        ///        没有则退回 1 号连接（RC+ 默认）；
        ///      - IP 地址 → 匹配 ConnectionIPAddress 的连接；
        ///      - 其他文本 → 按连接名 Connect(名称)；
        ///   4. 连接成功读 GetCurrentConnectionInfo / RobotModel 做验证实锤。
        ///
        /// 失败时返回的错误文本带：失败步骤 + ErrorNumber + GetErrorMessage
        /// 翻译（RC+ 错误码 → 英文描述）+ 可用连接清单，方便现场直接定位。
        /// TODO(接实机)：真实控制器接入时 DeviceId 填 RC+ 里配置的连接名
        /// 或控制器 IP 即可，无需改代码。
        /// </summary>
        public string Open(string address)
        {
            try
            {
                _spel = new RCAPINet.Spel();

                // 第 1 步：初始化（RC+8 无此步骤，RC+7 必须）
                _spel.Initialize();

                // 第 2 步：枚举 RC+ 已配置的真实连接（可能抛 SpelException，单独捕获）
                RCAPINet.SpelConnectionInfo[] connections = null;
                try
                {
                    connections = _spel.GetConnectionInfo();
                }
                catch (Exception ex)
                {
                    // GetConnectionInfo 失败通常说明 RC+ 没开或 API 服务未就绪
                    return DescribeError("GetConnectionInfo（读取 RC+ 连接配置）", ex,
                        "请确认 RC+ 已启动且工程已打开（Run 模式 = Simulator）");
                }

                // 第 3 步：按地址选择目标连接
                int? useNumber = null;      // 按连接号连
                string useName = null;      // 按连接名连
                var diag = new System.Text.StringBuilder();

                if (connections != null)
                {
                    foreach (var c in connections)
                    {
                        diag.Append($"[{c.ConnectionNumber}] '{c.ConnectionName}' " +
                                    $"Type={c.ConnectionType} IP={c.ConnectionIPAddress}; ");

                        bool isLocal = string.IsNullOrWhiteSpace(address) ||
                                       string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase);

                        if (isLocal)
                        {
                            // 本机模拟器：优先 Virtual（虚拟控制器）连接，其次 1 号
                            if (c.ConnectionType == RCAPINet.SpelConnectionType.Virtual)
                            {
                                useNumber = c.ConnectionNumber;
                                break;
                            }
                            if (c.ConnectionNumber == 1 && !useNumber.HasValue)
                            {
                                useNumber = 1;
                            }
                        }
                        else if (!string.IsNullOrEmpty(c.ConnectionIPAddress) &&
                                 string.Equals(c.ConnectionIPAddress.Trim(), address.Trim(),
                                               StringComparison.OrdinalIgnoreCase))
                        {
                            // 真实控制器：按 IP 匹配
                            useNumber = c.ConnectionNumber;
                            break;
                        }
                        else if (string.Equals(c.ConnectionName, address, StringComparison.OrdinalIgnoreCase))
                        {
                            // 按连接名匹配
                            useName = c.ConnectionName;
                            break;
                        }
                    }
                }

                // 没有匹配项时的兜底：地址既不是 localhost 也不是已知 IP/连接名
                // → 当作连接名直接连（让 RC+ 报出具体错误）
                if (!useNumber.HasValue && useName == null)
                {
                    bool isLocal = string.IsNullOrWhiteSpace(address) ||
                                   string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase);
                    if (isLocal)
                    {
                        useName = address ?? "localhost";
                    }
                    else
                    {
                        useName = address;
                    }
                }

                // 第 4 步：执行连接
                try
                {
                    if (useName != null)
                    {
                        _spel.Connect(useName);
                    }
                    else
                    {
                        _spel.Connect(useNumber.Value);
                    }
                }
                catch (Exception ex)
                {
                    string how = useName != null ? $"Connect(\"{useName}\")" : $"Connect({useNumber})";
                    return DescribeError(how, ex,
                        "RC+ 可用连接: " + (diag.Length > 0 ? diag.ToString() : "(无)"));
                }

                // 第 5 步：选择机器人 + 验证连接（读型号做实锤）
                _spel.Robot = 1;

                string modelInfo = "";
                try
                {
                    var ri = _spel.GetRobotInfo(1);
                    if (ri != null && !string.IsNullOrEmpty(ri.RobotModel))
                    {
                        modelInfo = $"，机型 {ri.RobotModel}";
                    }
                }
                catch { /* 型号读取失败不影响连接结果 */ }

                Debug.WriteLine($"[Epson] RC+7 连接成功{modelInfo}（{(useName != null ? "名称 " + useName : "连接号 " + useNumber)}）");

                return null;
            }
            catch (Exception ex)
            {
                _spel = null;
                return DescribeError("Initialize（RC+ 初始化）", ex,
                    "请确认 RC+ 已启动（erc70.exe）且 API 服务（erc70PServer.exe）在运行");
            }
        }

        /// <summary>
        /// 把 SpelException 翻译成带错误码的可读描述。
        /// SpelException.ErrorNumber = RC+ 错误码；
        /// Spel.GetErrorMessage(错误码) = RC+ 官方错误描述（需在已创建的 Spel 实例上调用）。
        /// </summary>
        private string DescribeError(string step, Exception ex, string hint)
        {
            string detail = ex.Message;
            int errNum = -1;

            var se = ex as RCAPINet.SpelException;
            if (se == null && ex is System.Reflection.TargetInvocationException &&
                ex.InnerException is RCAPINet.SpelException)
            {
                se = (RCAPINet.SpelException)ex.InnerException;
            }
            if (se != null)
            {
                errNum = se.ErrorNumber;
                detail = se.Message;
                // 尝试用 RC+ 官方翻译替换（失败保留原始 Message）
                try
                {
                    string official = _spel != null ? _spel.GetErrorMessage(se.ErrorNumber) : null;
                    if (!string.IsNullOrEmpty(official))
                    {
                        detail = official;
                    }
                }
                catch { /* 翻译失败就裸报 */ }
            }

            string text = $"步骤 {step} 失败: {detail}";
            if (errNum >= 0)
            {
                text += $"（SpelError #{errNum}）";
            }
            if (!string.IsNullOrEmpty(hint))
            {
                text += "。" + hint;
            }
            return text;
        }

        public string Close()
        {
            try
            {
                if (_spel != null)
                {
                    _spel.Disconnect();
                    _spel = null;
                }
                return null;
            }
            catch (Exception ex)
            {
                return $"断开 Epson 控制器异常: {ex.Message}";
            }
        }

        /// <summary>伺服上/下电（模拟器下为空操作，真实控制器下电后运动指令会报错）</summary>
        public string SetMotorsOn(bool on)
        {
            if (_spel == null) return "控制器未连接";
            try
            {
                _spel.MotorsOn = on;
                return null;
            }
            catch (Exception ex)
            {
                return $"伺服 {(on ? "上电" : "下电")}失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 四轴全坐标定位：PTP 用 Go，CP 直线插补用 Move。
        /// RC+7.5.1 无四 double 重载 → 构造 SpelPoint 赋 X/Y/Z/U 后传入。
        /// Go/Move 默认同步阻塞（AsyncMode=false），返回即到位。
        /// speed 单位 mm/s（SPEL+ Speed 指令口径）；&lt;=0 时不修改当前速度。
        /// TODO(现场)：SCARA 的 Speed 口径按机型手册核对（部分机型为百分比），
        /// 若为百分比需把业务层的 mm/s 换算后再调 _spel.Speed(...)。
        /// </summary>
        public string MoveTo(float x, float y, float z, float u, float speed, bool linear)
        {
            if (_spel == null) return "控制器未连接";
            try
            {
                if (speed > 0)
                {
                    _spel.Speed((int)speed);   // RC+7.5.1：Speed 是方法不是属性
                }

                var pt = new RCAPINet.SpelPoint
                {
                    X = x,
                    Y = y,
                    Z = z,
                    U = u
                };

                if (linear)
                {
                    _spel.Move(pt);   // CP 直线插补（同步阻塞）
                }
                else
                {
                    _spel.Go(pt);     // PTP 关节插补（同步阻塞，最快路径）
                }

                // 指令位置跟踪（供 GetPosition 读取）
                _pos[0] = x; _pos[1] = y; _pos[2] = z; _pos[3] = u;
                return null;
            }
            catch (Exception ex)
            {
                return $"机械手运动失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 【门型运动 Jump】本适配器（RCAPINet7 直连）暂未实现 —— 返回错误而不是"退化成 Go 顶替"。
        ///
        /// ★为什么不顶替：`Go(pt)` 会把"先抬到安全高度"和"不走逐轴 L 形"这两件事**同时丢掉**，
        ///   而调用方会以为拿到了门型 —— 正是本次要修的病灶形态（吸持工件低位横穿）。
        ///   返回错误后，上层 <see cref="StationProcessBase.TryMoveJumpAsync"/> 会**显式降级**
        ///   为"抬 Z → XY → 降 Z"并打 WARN 留痕。
        /// 【若要在此落地】RCAPINet.Spel 若提供 Jump 点运动，按
        ///   `_spel.Jump(pt)` + 预先设定的全局 LimZ（SPEL+ 的 LimZ 语句）实现；
        ///   判据必须与脚本 SafeJump 同源（limZ 必须高于目标 Z，否则拒绝）。
        /// </summary>
        public string MoveJump(float x, float y, float z, float u, float limZ, float speed)
        {
            return "本适配器(RCAPINet7 直连)暂未实现门型运动(JUMP)：请改用 TCP 脚本通道，"
                 + "或在此按 RCAPINet.Spel 的 Jump 语义落地（LimZ 判据须与脚本 SafeJump 同源）";
        }

        /// <summary>
        /// 回机械原点。RC+7.5.1 Home() 无参重载（整轴回零）。
        /// TODO(现场)：如工程里定义了分组回零/安全回零顺序，建议在 SPEL+ 工程
        /// 写一个 HomeAll 函数后用 _spel.Execute 调用，避免 Z 带料直接横移。
        /// </summary>
        public string Home()
        {
            if (_spel == null) return "控制器未连接";
            try
            {
                _spel.Home();
                for (int i = 0; i < 4; i++) _pos[i] = 0f;
                return null;
            }
            catch (Exception ex)
            {
                return $"回原点失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 减速停止当前运动。RC+7.5.1 Halt 需要任务号 ——
        /// RCAPINet 指令在 CommandTask 属性指定的任务里执行，停止同一任务即可。
        /// </summary>
        public string Halt()
        {
            if (_spel == null) return "控制器未连接";
            try
            {
                _spel.Halt(_spel.CommandTask);
                return null;
            }
            catch (Exception ex)
            {
                return $"停止运动失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 读取轴位置。当前返回软件跟踪的指令位置（每次 MoveTo 更新）。
        /// TODO(接实机)：如需编码器真实反馈（例如碰撞检测、断电位置恢复），
        /// 可在 SPEL+ 工程里封装函数返回 PPos，经 RCAPINet Execute/Eval 通道读取；
        /// 模拟器联调阶段指令位置足够（模拟器位置与指令一致）。
        /// </summary>
        public float GetPosition(int axis)
        {
            return (axis >= 0 && axis < 4) ? _pos[axis] : 0f;
        }

        /// <summary>运动是否完成（Go/Move 同步阻塞返回，恒为 true）</summary>
        public bool IsMotionDone()
        {
            return true;
        }

        /// <summary>
        /// 数字输出。业务 0 基号 → SPEL+ 物理口（EpsonIoMap：吸嘴1=15/吸嘴2=14，现场实测 2026-09-02）。
        /// 例：业务层写 0 号口（吸嘴1真空）→ 控制器 On(15)。
        /// TODO(现场)：换机接线后若真空阀端口变化，只需改 EpsonSdkAdapters.cs 的 EpsonIoMap。
        /// </summary>
        public string SetOutput(int ioNumber, bool state)
        {
            if (_spel == null) return "控制器未连接";
            try
            {
                int port = EpsonIoMap.SpelPort(ioNumber);
                if (state) _spel.On(port);
                else _spel.Off(port);

                _outputs[ioNumber] = state;
                return null;
            }
            catch (Exception ex)
            {
                return $"输出 On/Off({EpsonIoMap.SpelPort(ioNumber)}) 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 数字输入读取。外部 0 基编号 → SPEL+ 1 基编号（In 指令）。
        /// TODO(现场)：模拟器输入恒为 Off；接实机后若需「真空检知反馈」
        /// 请确认检知传感器接的位号并回到这里核对换算。
        /// </summary>
        public bool? GetInput(int ioNumber)
        {
            if (_spel == null) return null;
            try
            {
                return Convert.ToBoolean(_spel.In(ioNumber + 1));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Epson] 读输入 In({ioNumber + 1}) 失败: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
#endif
