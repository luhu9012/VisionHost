using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
// 解决 IDevice 命名空间冲突
using IContractDevice = Grayson.Vision.Contracts.Devices.IDevice;

namespace Plugins.Robot.Epson
{
    /// <summary>
    /// Epson 机器人插件工厂。
    ///
    /// 插件发现机制：程序集名 Plugins.Robot.Epson 含 "Plugin" 字样，
    /// 启动时被 DevicePluginManager 反射扫描加载；
    /// Supports(MotionCard, "Epson") 命中后由本工厂创建设备实例。
    ///
    /// 与相机插件不同：机器人不通过 USB/GigE 枚举发现，而是连接
    /// RC+ 控制器（IP 地址或本机 RC+ 仿真）。因此：
    /// - EnumerateDevices 返回常见控制器地址候选（本机 192.168.1.10 +
    ///   localhost），用户在设备管理界面选择实际使用的地址；
    /// - CreateDevice(deviceId) 中 deviceId 即控制器地址，EpsonRobot
    ///   在 Connect 时用它建立 RC+ 连接。
    /// </summary>
    public class EpsonPlugin : IHardwarePlugin
    {
        /// <summary>
        /// RC+ 探测串行锁（2026-09-01 v3 新增）。
        /// RC+ 同一时刻只允许一个 API 客户端；若用户快速多次点击"扫描"，
        /// 并发进入 ProbeRc7Connections 的多个 Spel 客户端会互相串扰——
        /// 现场日志表现为：连接号不断递增、同一连接读到不同机型（数据污染）、
        /// 报"控制器已连接到RC+"，最终扫描列表里 Epson 设备消失。
        /// 在插件层强制串行化，配合 UI 层防重入（IsScanning）双保险。
        /// </summary>
        private static readonly object _rcProbeLock = new object();

        public string BrandName => "Epson";
        public DeviceCategory Category => DeviceCategory.MotionCard;
        public string Version => "1.0.0";

        /// <summary>品牌专用插件高优先级，优先于通用协议插件接管</summary>
        public int Priority => 100;

        public bool Supports(DeviceCategory category, string brand)
        {
            return category == DeviceCategory.MotionCard &&
                   string.Equals(brand, BrandName, StringComparison.OrdinalIgnoreCase);
        }

        public void Initialize() { }

        /// <summary>
        /// 枚举可连接的 Epson 控制器（v5：TCP 脚本协议探测 + RC+ SDK 探测双通道，2026-09-02）。
        ///
        /// ── 为什么扫不到 Epson？──（现场现象：RC+ 7.5 已打开，扫描只有 Basler，Epson 消失）
        /// 日志表现为：连接 [9] 'Ethernet 1' Type=Ethernet 试连失败: 错误 1800, 控制器已连接到RC+。
        /// 原理：RC+ 的 SPEL .NET API（RCAPINet/spelnet64）是【单客户端会话】模型——
        /// 同一时刻只允许一个 API 客户端连接控制器。当 RC+ 软件自身的调试器/连接管理器
        /// 已占用该连接（或上一个进程异常退出没释放客户端）时，我们 SDK 探测就必然 1800。
        /// 这是 RC+ 平台限制，不是插件 bug。
        ///
        /// ── 双通道探测设计 ──
        /// 通道①【TCP 脚本协议】（无条件执行，本方案核心）：
        ///   连 RC+ 工程里 OpenNet 开的 TCP 服务器（约定 127.0.0.1:502，见 EpsonTcpScriptAdapter）。
        ///   该通道走【独立 TCP 端口 + ASCII 行协议（PING→PONG 握手）】，不占用 RC+ 的 API
        ///   会话，因此【不产生 1800】。模拟器联调（当前形态）/无 SDK 机器都能扫到。
        ///   导入时通过 DeviceInfo.ExtraInfo 携带 ConnectionString（Protocol=TCP;...），
        ///   连接阶段 EpsonRobot 识别到该串即走 TCP 传输层（与模拟器联调首选通道闭环）。
        /// 通道②【RC+ SDK 探测】（EPSON_SPEL7 / EPSON_SPEL 宏下执行）：
        ///   针对真实工业控制器（IP 连接）。试连需占用 RC+ API 会话——若报 1800，
        ///   请到 RC+ 中先断开控制器的连接占用，或直接用通道①的 TCP 方式。
        ///
        /// v2~v4 演进记录：v1 硬编码 localhost mock → v2 真实试连（GetConnectionInfo 枚举+
        /// 逐个 Connect）→ v3 加 _rcProbeLock 串行锁 + DeviceId 唯一化（Virtual 用连接名，
        /// Ethernet 用 IP）→ v4 只探测 Ethernet 连接（USB 无驱动报 1808 超时纯浪费时间；
        /// Virtual 是 RC+ 自带 Sample 演示工程不是真实设备；现场扫描 101s→<2s）→
        /// v5 前置 TCP 脚本协议通道，绕开 RC+ 单客户端 1800 限制。
        /// </summary>
        public Result<List<DeviceInfo>> EnumerateDevices()
        {
            var list = new List<DeviceInfo>();
            try
            {
                // ---- 通道①：TCP 脚本协议探测（无条件；不占 RC+ API 会话，无 1800）----
                var tcpProbe = ProbeTcpScriptServer();
                if (tcpProbe != null)
                {
                    list.Add(tcpProbe);
                }

                // ---- 通道②：RC+ SDK 真实探测（需 SDK DLL；面向真实控制器 IP）----
#if EPSON_SPEL7
                // ---- 真实探测：本机 RC+（含模拟器）----
                var probe = ProbeRc7Connections();
                if (probe != null)
                {
                    list.AddRange(probe);
                }
#elif EPSON_SPEL
                // TODO(RC+8)：SpeLNet SDK 的真实探测（RC+8 API 不同，
                // 需用 Connections 集合枚举），当前暂列本机候选。
                list.Add(new DeviceInfo
                {
                    DeviceId = "localhost",
                    ModelName = "Epson RC+ 8 (Local)",
                    Category = Category,
                    BrandName = BrandName
                });
#else
                // 无 SDK：TCP 通道已在上面探测过，这里明确提示其余通道不可用
                Debug.WriteLine("[Epson] 本机未检测到 Epson SDK（DLLLib\\RCAPINet.dll / spelnet64.dll），" +
                                "SDK 探测通道跳过；TCP 脚本协议通道结果见上。");
#endif
                return Result<List<DeviceInfo>>.Ok(list);
            }
            catch (Exception ex)
            {
                return Result<List<DeviceInfo>>.Fail($"枚举 Epson 控制器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 通道①：TCP 脚本协议探测（模拟器/简易实机联调首选，2026-09-02 v5 新增）。
        ///
        /// 原理：RC+ 工程里用 SPEL+ 的 OpenNet 开一个 TCP 服务器（如端口 502，配合新版
        /// main560 指令集脚本），上位机发一行指令收一行应答（详见 EpsonTcpScriptAdapter）。
        /// 与 RC+ SDK 探测的本质区别：
        ///   - SDK 探测：RCAPINet.Spel 是 RC+ 的【API 客户端】→ 受单客户端限制 → 易 1800；
        ///   - TCP 探测：TcpClient 直连 OpenNet 端口 → 是【普通 TCP 连接】→ 不受限制。
        /// 因此模拟器联调场景（RC+ GUI 开着、连接被占用）下，只有 TCP 通道能稳定扫到。
        ///
        /// 探测流程：连接 127.0.0.1:502 → 发 "PING" → 3s 内收到 "PONG" 即认定对端是
        /// 新版指令集脚本（旧版脚本只认数字，收 PING 不回 PONG → 探测失败，避免误报）。
        /// 复用 EpsonTcpScriptController.Open（内部已含超时/握手/断开），失败静默返回 null。
        ///
        /// 成功时 DeviceInfo：
        ///   - DeviceId = "127.0.0.1:502"（物理标识，与 SDK 探测的 IP/连接名天然不冲突）；
        ///   - ExtraInfo = "Protocol=TCP;IP=127.0.0.1;Port=502" → 扫描对话框导入时透传为
        ///     ConnectionString → EpsonRobot.Connect 识别 Protocol=TCP 即走 TCP 传输层。
        /// </summary>
        private static DeviceInfo ProbeTcpScriptServer()
        {
            var ctrl = new EpsonTcpScriptController();
            try
            {
                string err = ctrl.Open("192.168.3.11:8000");
                if (err != null)
                {
                    // 对端不可达/不是新版脚本：不产生扫描噪音，静默跳过
                    Debug.WriteLine($"[Epson] TCP 脚本服务器探测失败 (192.168.3.11:8000): {err}");
                    return null;
                }

                Debug.WriteLine("[Epson] TCP 脚本服务器探测成功 (192.168.3.11:8000) → 将以 TCP 协议连接");
                return new DeviceInfo
                {
                    DeviceId = "192.168.3.11:8000",
                    ModelName = "Epson RC+ OpenNet TCP 脚本服务器 (192.168.3.11:8000)",
                    Category = DeviceCategory.MotionCard,
                    BrandName = "Epson",
                    ExtraInfo = "Protocol=TCP;IP=192.168.3.11;Port=8000"
                };
            }
            finally
            {
                try { ctrl.Close(); } catch { }
            }
        }

#if EPSON_SPEL7
        /// <summary>
        /// RC+ 7.x 真实探测：枚举 RC+ 配置的连接并逐个试连。
        /// 返回试连成功的 DeviceInfo 列表（每个带机型/工程实锤信息）；
        /// RC+ 不可达时返回 null（注意：不是抛异常——扫描环节失败=扫不到）。
        ///
        /// v3：整体加 _rcProbeLock 串行锁——RC+ 只允许单 API 客户端，
        /// 并发探测会污染连接表（见类注释）。锁定粒度覆盖"初始化→枚举→
        /// 逐个试连→断开"全过程，保证同一时刻只有一个 Spel 实例在活动。
        /// </summary>
        private static List<DeviceInfo> ProbeRc7Connections()
        {
            lock (_rcProbeLock)
            {
                RCAPINet.Spel spel = null;
                try
                {
                    spel = new RCAPINet.Spel();
                    spel.Initialize();

                    // 读 RC+ 真实配置的连接清单（RC+ 没开时这里抛 SpelException）
                    RCAPINet.SpelConnectionInfo[] connections = spel.GetConnectionInfo();
                    if (connections == null || connections.Length == 0)
                    {
                        Debug.WriteLine("[Epson] RC+ 中没有配置任何连接（[Setup]-[控制器]），扫描跳过");
                        return null;
                    }

                    var found = new List<DeviceInfo>();
                    foreach (var c in connections)
                    {
                        // v4（2026-09-01）：只探测工业以太网（Ethernet）连接。
                        // - USB：无 USB 驱动器时报 1808 需等超时，纯浪费时间（现场实测每轮数秒）；
                        // - Virtual：RC+ 自带的 Sample 演示项目（C4/G6/N2/T6 Sample 等），
                        //   不是真实设备；逐个试连既慢又反复占用 RC+ 单客户端会话（报 1800）。
                        // 用户现场只有一台真实机械手，走 Ethernet 扫描即可，扫描耗时 101s→<2s。
                        if (c.ConnectionType != RCAPINet.SpelConnectionType.Ethernet)
                        {
                            Debug.WriteLine($"[Epson] 跳过 {c.ConnectionType} 连接 [{c.ConnectionNumber}] '{c.ConnectionName}'（只扫工业以太网）");
                            continue;
                        }

                        try
                        {
                            // 逐个试连（用连接号，最可靠）
                            spel.Connect(c.ConnectionNumber);

                            // 连接成功：读机型/工程名做实锤，证明这不是 mock
                            string model = null;
                            string project = null;
                            try
                            {
                                var ri = spel.GetRobotInfo(1);
                                if (ri != null) model = ri.RobotModel;
                            }
                            catch { /* 模拟器无 robot 时忽略 */ }
                            try
                            {
                                var ci = spel.GetControllerInfo();
                                if (ci != null) project = ci.ProjectName;
                            }
                            catch { /* 工程名读不到忽略 */ }

                            // DeviceId 唯一化规则（v3）：
                            // - Virtual（本机模拟器）→ 用连接名（如 "C4 Sample"），
                            //   不再统一写成 "localhost"（多个虚拟控制器全重名，
                            //   扫描列表无法区分、导入时后几行报"物理 ID 重复"）；
                            // - Ethernet/USB（真实控制器）→ 优先 IP，其次连接名。
                            // EpsonRobot.Connect 走 RCAPINet7Controller.Open 智能匹配：
                            // 连接名会被 Connect(名称) 命中，IP 会被按 IP 匹配，均可用。
                            string deviceId = c.ConnectionType == RCAPINet.SpelConnectionType.Virtual
                                ? c.ConnectionName
                                : (string.IsNullOrEmpty(c.ConnectionIPAddress) ? c.ConnectionName : c.ConnectionIPAddress);

                            found.Add(new DeviceInfo
                            {
                                DeviceId = deviceId,
                                ModelName = $"RC+ {c.ConnectionType} #{c.ConnectionNumber} '{c.ConnectionName}'" +
                                            (model != null ? $" / {model}" : "") +
                                            (project != null ? $" / 工程 {project}" : ""),
                                Category = DeviceCategory.MotionCard,
                                BrandName = "Epson"
                            });

                            Debug.WriteLine($"[Epson] 探测成功: [{c.ConnectionNumber}] '{c.ConnectionName}' " +
                                            $"Type={c.ConnectionType} Model={model} Project={project}");
                        }
                        catch (Exception ex)
                        {
                            // 单个连接试连失败：记录后继续试下一个（例如真实控制器网线没插）
                            Debug.WriteLine($"[Epson] 连接 [{c.ConnectionNumber}] '{c.ConnectionName}' " +
                                            $"Type={c.ConnectionType} 试连失败: {ex.Message}");
                        }
                        finally
                        {
                            // 探测完立即断开，不占用 RC+ 连接（同一时刻 RC+ 只允许一个 API 客户端）
                            try { spel.Disconnect(); } catch { }
                        }
                    }
                    return found;
                }
                catch (Exception ex)
                {
                    // RC+ 未启动 / API 服务未就绪 → 扫不到（返回 null 而不是 mock）
                    Debug.WriteLine($"[Epson] RC+ 不可达，扫描跳过: {ex.Message}。" +
                                    "（请确认 RC+ 已启动、工程已打开）");
                    return null;
                }
                finally
                {
                    try { if (spel != null) spel.Dispose(); } catch { }
                }
            }
        }
#endif

        /// <summary>
        /// 按控制器地址创建设备实例。
        /// deviceId 为 RC+ 控制器 IP（如 192.168.1.10）或 "localhost"；
        /// 无论 UI 刚扫描创建，还是 LiteDB 离线恢复，均返回可用 IDevice。
        /// </summary>
        public IContractDevice CreateDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;

            var device = new EpsonRobot(deviceId)
            {
                BrandName = BrandName,
                Category = Category
            };

            // 默认初始参数（供属性面板展示/覆盖）
            device.SetParam("Speed", 100.0);            // 整体速度百分比（0~100）
            device.SetParam("Accel", 100.0);            // 整体加速度百分比
            device.SetParam("AxisCount", EpsonRobot.AxisCount); // 4 轴 SCARA

            return device;
        }

        public void Shutdown() { }
    }
}
