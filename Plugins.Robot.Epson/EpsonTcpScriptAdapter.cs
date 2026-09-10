using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Plugins.Robot.Epson
{
    // ======================================================================
    // Epson TCP 脚本协议适配层（OpenNet TCP 通信通道）
    //
    // ⚠⚠ 真机实测结论（2026-09-02，现场 RC+ 7.5 + 真实控制器）：
    //   1. 本通道在【实机】可用：控制器 OpenNet As Server 后监听在控制器自身
    //      IP 上。现场实测：SetNet 写 192.168.3.20（错误 IP），控制器仍监听在
    //      真实 IP 192.168.3.11:8000 —— Server 模式下 SetNet 的 IP 参数不影响
    //      监听地址；上位机连接串必须用【控制器的真实可达 IP】。
    //   2. 脚本必须用【新版指令集】（PING/POS?/MOVE/LMOVE/MOTOR/HOME/STOP/
    //      SPEED/OUT/IN?/OUT?，见项目根目录 Epson_新版main560指令集脚本_真机版.prg）。
    //      旧版脚本（Input #201, X$,Y$,Z$,U$ 只认 4 个裸坐标）收到 PING 会
    //      卡死（Input 阻塞）且不回 PONG → 上位机握手失败。
    //   3. 模拟器（RC+ Simulator）下本通道是否可用未实测，联调请优先走
    //      RCAPINet SDK 通道；详见《Epson通信模式选择_场景与业务流配合.md》。
    //
    // 背景：
    //   RCAPINet（RC+ 7.5 SPEL.NET SDK）连 RC+ 运行时会受单客户端限制
    //   （错误 1800：RC+ GUI 占用连接时 API 连不上），排查成本高；而实机
    //   工程里用 OpenNet 开一个 TCP 服务器（如 8000/502 端口）写几行 SPEL+
    //   就能与上位机通信。本适配层即走这条路：
    //
    //   上位机（本类）                     RC+ 工程（SPEL+ 脚本，见文档）
    //   ─────────────                     ────────────────────────────
    //   TCP 连接 ip:port        ───────▶  OpenNet #201 As Server
    //   发送一行指令 + \r\n     ───────▶  Line Input #201, cmd$
    //   阻塞读一行应答          ◀───────  Print #201, "DONE" 等
    //
    // 指令集（一行一指令、一行一应答，大小写不敏感）：
    //   PING            → PONG                      连接握手/心跳
    //   POS?            → POS x,y,z,u               读当前位置（CurPos）
    //   MOVE x,y,z,u    → DONE                      PTP 四轴定位（Go /R）
    //   LMOVE x,y,z,u   → DONE                      CP 直线（Move）
    //   MOTOR ON|OFF    → OK                        伺服上/下电
    //   HOME            → DONE                      回原点（默认 XY(0,0,0,0)）
    //   STOP            → OK                        Halt 减速停止
    //   SPEED n         → OK                        设定速度百分比 1~100
    //   OUT n,0|1       → OK                        数字输出（n 为 1 基编号）
    //   IN? n           → IN 0|1                    数字输入读取（1 基）
    //   OUT? n          → OUT 0|1                   输出状态回读（1 基）
    //   其他            → ERR 未知指令: xxx         协议版本过旧的明确信号
    //
    // 注意：
    //   - 应答 DONE 在 Go 运动完成后才发出 → 指令天然同步（IsMotionDone 恒 true）；
    //     代价是运动中收不到 STOP（单任务串行）。TODO(接实机)：如需运动中可急停，
    //     请在 SPEL+ 工程里用 Xqt 把 Go 放后台任务，主循环继续收 STOP → Halt。
    //   - 速度单位差异：上位机业务层传 mm/s，SPEL+ Speed 为百分比（1~100）。
    //     此处做近似钳位（模拟器无所谓精确速度），TODO(实机标定)：按机型行程换算。
    // ======================================================================

    /// <summary>
    /// TCP 脚本协议控制器适配层。
    /// 约定与 RCAPINet7Adapter 一致：方法返回 null = 成功，非 null = 错误消息。
    /// </summary>
    internal sealed class EpsonTcpScriptController : IEpsonSdkController
    {
        #region 常量

        /// <summary>指令应答默认超时（毫秒）。PTP 运动耗时与距离相关，模拟器瞬时完成</summary>
        private const int ReplyTimeoutMs = 20000;

        /// <summary>位置轮询超时（毫秒）。POS? 正常是毫秒级应答，脚本忙时才慢；
        /// 短超时让轮询快速失败放弃，避免长时间占锁拖住运动命令/轮询节奏。</summary>
        private const int PosQueryTimeoutMs = 5000;

        /// <summary>连接建立超时（毫秒）</summary>
        private const int ConnectTimeoutMs = 3000;

        /// <summary>指令位置影子缓存的有效期——MoveAbsolute 需连续读 4 轴位置，
        /// 缓存避免同一次组点发 4 次 POS? 查询</summary>
        private const int PosCacheMs = 100;

        #endregion

        #region 内部状态

        private TcpClient _tcp;
        private NetworkStream _stream;
        private readonly object _ioLock = new object();     // 行协议一发一收，必须串行
        private readonly float[] _pos = new float[4];       // X/Y/Z/U 指令位置影子
        private readonly bool[] _outputs = new bool[64];    // 输出状态软件跟踪（1 基编号→数组 0 基存）
        private DateTime _lastPosQuery = DateTime.MinValue; // 上次 POS? 成功时刻
        private bool _motorsOn;

        public bool IsSimulated => false;                    // 走真实 TCP 通信，不算离线仿真
        public bool IsOpen => _tcp != null && _tcp.Connected;

        #endregion

        #region 连接 / 断开

        /// <summary>
        /// 连接 RC+ 工程的 OpenNet 服务器。
        /// address 格式："ip:port"（如 127.0.0.1:502）或 "localhost"（默认端口 502）。
        /// 连接后发 PING 握手验证对端跑的是新版指令集脚本。
        /// </summary>
        public string Open(string address)
        {
            try
            {
                // ---- 解析地址 ----
                string host = address;
                int port = 502; // 默认端口（与 SPEL+ SetNet #201 ... 502 对应，可改）
                if (!string.IsNullOrEmpty(address))
                {
                    int idx = address.LastIndexOf(':');
                    if (idx > 0 && int.TryParse(address.Substring(idx + 1), out int p))
                    {
                        host = address.Substring(0, idx);
                        port = p;
                    }
                }
                if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    host = "127.0.0.1";
                }

                // ---- 建立 TCP 连接 ----
                var client = new TcpClient();
                var asyncResult = client.BeginConnect(host, port, null, null);
                if (!asyncResult.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    client.Close();
                    return $"TCP 连接 {host}:{port} 超时（{ConnectTimeoutMs}ms）。" +
                           "请确认 RC+ 已启动、工程已编译运行（Run 模式）且 SPEL+ 脚本已执行到 WaitNet。";
                }
                client.EndConnect(asyncResult);

                _tcp = client;
                _stream = client.GetStream();
                _stream.ReadTimeout = ReplyTimeoutMs;
                _stream.WriteTimeout = ReplyTimeoutMs;

                // ---- PING 握手：确认对端是新版指令集脚本 ----
                // 旧版脚本（只认 4 个数字）收到 "PING" 不会回 PONG → 这里直接报可读错误
                string pong = SendCommand("PING", 3000);
                if (!string.Equals(pong, "PONG", StringComparison.OrdinalIgnoreCase))
                {
                    string detail = pong ?? "无应答（超时）";
                    Dispose();
                    return $"TCP 已连通但握手失败（收到: {detail}）。" +
                           "RC+ 工程里跑的可能是旧版脚本——请用文档中的新版 main560 指令集脚本替换后重新运行。";
                }

                // ---- 连接成功：同步一次真实位置 ----
                if (QueryPositions() != null)
                {
                    // 位置读不到不阻断连接（可能 CurPos 在下电时不可读），影子位置从 0 开始
                    Debug.WriteLine("[EpsonTcp] 连接成功但初始 POS? 读取失败，位置影子从 0 开始");
                }

                Debug.WriteLine($"[EpsonTcp] 已连接 RC+ OpenNet 服务器 {host}:{port}");
                return null;
            }
            catch (Exception ex)
            {
                Dispose();
                return $"TCP 连接 [{address}] 失败: {ex.Message}";
            }
        }

        public string Close()
        {
            try
            {
                _stream?.Close();
                _tcp?.Close();
            }
            catch { /* 关闭过程不抛 */ }
            _stream = null;
            _tcp = null;
            return null;
        }

        public void Dispose() => Close();

        #endregion

        #region 行协议收发核心

        /// <summary>
        /// 发送一行指令并阻塞读取一行应答（线程安全：一发一收必须串行）。
        /// 返回 null = 通信超时/异常/通道忙；否则为应答原文（已去掉行尾 \r\n）。
        /// </summary>
        private string _lastRxReply = string.Empty;
        private string SendCommand(string command, int timeoutMs = ReplyTimeoutMs)
        {
            // ⚠ 防御①：脚本侧运行时错误（如 2345 IO 越界）会崩 mainTCP 任务并断开 TCP，
            // 断线竞态下 _tcp.Connected 可能短暂为 true 而 _stream 已被置 null → 必须双检
            if (!IsOpen || _stream == null) return null;

            // ⚠ 防御②（2026-09-02 UI 防卡死）：命令（后台 Task）与轮询（UI DispatcherTimer）
            // 共用 _ioLock。若命令持锁等待应答（如 MOVE 等 DONE 最多 20s），UI 线程的轮询会
            // 阻塞在 lock 上 → 界面冻结只能强关。改 Monitor.TryEnter 短超时：拿不到锁立即
            // 返回 null（本轮跳过）——轮询丢一轮无副作用；命令若撞忙由上层报错/重试。
            if (!Monitor.TryEnter(_ioLock, 50))
            {
                return null; // 通道忙：跳过本轮（调用方按无应答处理）
            }
            try
            {
                try
                {
                    var sw = Stopwatch.StartNew();

                    // 发送：一行指令 + CRLF（SPEL+ Line Input 以换行结束）
                    byte[] tx = Encoding.ASCII.GetBytes(command + "\r\n");
                    _stream.Write(tx, 0, tx.Length);
                    _stream.Flush();

                    // 接收：逐字节读到 '\n'（兼容 \r\n 与 \n）
                    var sb = new StringBuilder(64);
                    var buf = new byte[1];
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        // 用 Available 做非阻塞轮询，避免 Read 阻塞卡死收不到数据的场景
                        if (_stream.DataAvailable)
                        {
                            int n = _stream.Read(buf, 0, 1);
                            if (n <= 0) break;
                            char c = (char)buf[0];
                            if (c == '\n') break;          // 行结束
                            if (c != '\r') sb.Append(c);   // 忽略回车
                        }
                        else
                        {
                            Thread.Sleep(5);
                        }
                    }

                    if (sb.Length == 0)
                    {
                        // 超时无应答：脚本应答可能迟到（如正在执行同步运动）——迟到应答的字节
                        // 会留在流缓冲里，污染下一条指令的应答（协议错位 → 误报解析失败）。
                        // 协议为纯请求-应答、脚本从不主动推送 → 超时放弃后清空残留是安全的。
                        DrainPendingBytes();
                        return null;
                    }
                    string reply = sb.ToString().Trim();
                    // 判断和上一次是否一样，不一样才打印
                    if (reply != _lastRxReply)
                    {
                        Debug.WriteLine($"[EpsonTcp] TX: {command}  RX: {reply}");
                        _lastRxReply = reply; // 更新缓存
                    }
                    return reply;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[EpsonTcp] 通信异常 TX: {command} → {ex.Message}");

                    // ⚠ 防御③（断线感知）：写/读抛出 Socket/IO 异常 = 连接已死（脚本崩、
                    // 网线断、RC+ 停任务）。立即 Close 把 _tcp/_stream 置 null → IsOpen=false，
                    // 上层（设备池心跳/调试台轮询）据此感知断线、自动置 Disconnected。
                    // 仅超时（无应答但连接活着）不关闭——脚本可能只是忙（如同步运动）。
                    if (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
                    {
                        try { Close(); } catch { }
                    }
                    return null;
                }
            }
            finally
            {
                Monitor.Exit(_ioLock);
            }
        }

        /// <summary>清空流缓冲中残留的应答字节（超时放弃后调用，防协议错位）</summary>
        private void DrainPendingBytes()
        {
            try
            {
                var tmp = new byte[512];
                while (_stream != null && _stream.DataAvailable)
                {
                    if (_stream.Read(tmp, 0, tmp.Length) <= 0) break;
                }
            }
            catch { /* 清理过程不抛 */ }
        }

        /// <summary>
        /// 发送指令并校验应答。
        /// 期望应答匹配 expect 开头（大小写不敏感）→ 返回 null（成功）；
        /// 否则返回带应答内容的错误消息。
        /// </summary>
        private string SendAndExpect(string command, string expect, int timeoutMs = ReplyTimeoutMs)
        {
            string reply = SendCommand(command, timeoutMs);
            if (reply == null)
            {
                return $"指令 [{command}] 无应答（超时或连接断开）";
            }
            if (reply.StartsWith(expect, StringComparison.OrdinalIgnoreCase))
            {
                return null; // DONE / OK / PONG ...
            }
            return $"指令 [{command}] 失败，控制器应答: {reply}";
        }

        #endregion

        #region 位置查询

        /// <summary>
        /// 发 POS? 刷新四轴位置影子。成功返回 null，失败返回错误消息。
        /// 应答格式：POS x,y,z,u
        /// </summary>
        private string QueryPositions()
        {
            // POS? 走短超时：脚本正常时毫秒级应答；脚本忙（同步运动）时快速失败放弃，
            // 避免长时间占锁（轮询场景 UI/后台节奏被拖住）。失败沿用旧影子值即可。
            string reply = SendCommand("POS?", PosQueryTimeoutMs);
            if (reply == null) return "POS? 无应答";

            // 解析 "POS x,y,z,u"
            if (reply.StartsWith("POS", StringComparison.OrdinalIgnoreCase))
            {
                string payload = reply.Substring(3).Trim();
                string[] parts = payload.Split(',');
                if (parts.Length == 4)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (float.TryParse(parts[i].Trim(), NumberStyles.Float,
                                           CultureInfo.InvariantCulture, out float v))
                        {
                            _pos[i] = v;
                        }
                    }
                    _lastPosQuery = DateTime.UtcNow;
                    return null;
                }
            }
            return $"POS? 应答格式异常: {reply}";
        }

        /// <summary>读单轴位置（带短时缓存：连续读 4 轴只发一次查询）</summary>
        public float GetPosition(int axis)
        {
            if ((DateTime.UtcNow - _lastPosQuery).TotalMilliseconds > PosCacheMs)
            {
                QueryPositions(); // 失败则沿用旧影子值
            }
            return (axis >= 0 && axis < 4) ? _pos[axis] : 0f;
        }

        #endregion

        #region IEpsonSdkController 实现

        public string SetMotorsOn(bool on)
        {
            string err = SendAndExpect(on ? "MOTOR ON" : "MOTOR OFF", "OK");
            if (err == null) _motorsOn = on;
            return err;
        }

        public string MoveTo(float x, float y, float z, float u, float speed, bool linear)
        {
            if (!IsOpen) return "控制器未连接";

            // 速度近似换算：业务层 mm/s → SPEL+ 百分比（1~100 钳位）。
            // TODO(实机标定)：按机型行程把 mm/s 换算为准确的 Speed 百分比
            if (speed > 0)
            {
                int pct = (int)Math.Max(1, Math.Min(100, speed));
                SendAndExpect($"SPEED {pct}", "OK"); // 失败不阻断：沿用控制器当前速度
            }

            // 四轴坐标格式化为不变文化（避免系统区域设置把小数点变逗号）
            string coord = string.Format(CultureInfo.InvariantCulture,
                "{0:F3},{1:F3},{2:F3},{3:F3}", x, y, z, u);

            string err = SendAndExpect((linear ? "LMOVE " : "MOVE ") + coord, "DONE");
            if (err != null) return err;

            // 运动完成，更新指令位置影子
            _pos[0] = x; _pos[1] = y; _pos[2] = z; _pos[3] = u;
            return null;
        }

        public string Home()
        {
            if (!IsOpen) return "控制器未连接";
            string err = SendAndExpect("HOME", "DONE");
            if (err != null) return err;

            for (int i = 0; i < 4; i++) _pos[i] = 0f;
            return null;
        }

        /// <summary>减速停止。注意：SPEL+ 单任务串行，运动完成后才收到本指令（见类头注释 TODO）</summary>
        public string Halt()
        {
            if (!IsOpen) return "控制器未连接";
            return SendAndExpect("STOP", "OK");
        }

        /// <summary>指令为同步应答（DONE 后才返回）→ 到这里运动必然已完成</summary>
        public bool IsMotionDone() => true;

        /// <summary>数字输出。业务 0 基号 → SPEL+ 物理口（EpsonIoMap：吸嘴1=15/吸嘴2=14）</summary>
        public string SetOutput(int ioNumber, bool state)
        {
            if (!IsOpen) return "控制器未连接";
            int port = EpsonIoMap.SpelPort(ioNumber);
            string err = SendAndExpect($"OUT {port},{(state ? 1 : 0)}", "OK");
            if (err == null && port >= 1 && port <= _outputs.Length)
            {
                _outputs[port - 1] = state;
            }
            return err;
        }

        /// <summary>数字输入读取。外部 0 基 → SPEL+ 1 基 In(n)</summary>
        public bool? GetInput(int ioNumber)
        {
            if (!IsOpen) return null;
            string reply = SendCommand($"IN? {ioNumber + 1}");
            // 应答格式：IN 0 或 IN 1
            if (reply != null && reply.StartsWith("IN", StringComparison.OrdinalIgnoreCase))
            {
                string v = reply.Substring(2).Trim();
                if (v == "1") return true;
                if (v == "0") return false;
            }
            return null; // 读取失败
        }

        #endregion
    }
}
