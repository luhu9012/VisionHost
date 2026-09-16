using System;
using System.Collections.Generic;
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
    //      SPEED/SPEEDS/ACCELS/HAND/HAND?/POINT?/OUT/IN?/OUT?）。
    //      ★ 真身文件 = D:\EpsonRC70\projects\H\mainTCP.prg（RC+ 实际编译运行的就是它，
    //        GBK 无 BOM + 纯 CRLF，改完需在 RC+ 里【停止任务→重新编译→运行】才生效）；
    //        仓库根 Epson_mainTCP_真机版_2026-09-11.prg 是它的留档副本。
    //        注意 Epson_新版main560指令集脚本_真机版.prg 是【已过期的旧副本】，别照着它改。
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
    //   MOVE x,y,z,u    → DONE                      PTP 关节插补（Go，手系按脚本 HAND 设定）
    //   LMOVE x,y,z,u   → DONE                      CP 直线（Move，手系按脚本 HAND 设定）
    //   MOTOR ON|OFF    → OK                        伺服上/下电
    //   HOME            → DONE                      回原点（默认 XY(0,0,0,0)）
    //   STOP            → OK                        Halt 减速停止
    //   SPEED n         → OK                        PTP 速度百分比 1~100（只管 Go）
    //   SPEEDS v        → OK                        CP 直线速度 mm/s 1~2000（只管 LMOVE）
    //   ACCELS v        → OK                        CP 直线加减速度 mm/s^2 1~10000
    //   HAND L|R        → OK | ERR                  设脚本运动手系（L=左手 Lefty / R=右手 Righty）
    //   HAND?           → HAND L,2                  读手系：脚本设定,RC+ 当前位姿手系(1=Righty 2=Lefty)
    //   POINT? n        → POINT n,x,y,z,u,标签      读 RC+ 示教点（只读）；未定义 → POINT n,UNDEF,Err
    //   OUT n,0|1       → OK                        数字输出（n 为 1 基编号）
    //   IN? n           → IN 0|1                    数字输入读取（1 基）
    //   OUT? n          → OUT 0|1                   输出状态回读（1 基）
    //   其他            → ERR 未知指令: xxx         协议版本过旧的明确信号
    //
    // 注意：
    //   - ★MOVE(Go) 是【异步】的：脚本发完 Go 就立刻回 DONE，DONE 只表示"指令已受理"，
    //     【不代表到位】。要真到位必须靠 POS? 轮询比对；LMOVE(Move) 才是同步到位后回 DONE。
    //     （旧注释写的"DONE 在 Go 完成后才发出"是错的。）
    //     代价是运动中收不到 STOP（单任务串行）。TODO(接实机)：如需运动中可急停，
    //     请在 SPEL+ 工程里用 Xqt 把 Go 放后台任务，主循环继续收 STOP → Halt。
    //   - ★速度【双轨】，两套参数各管各的，发错等于没设：
    //       PTP(Go)  ← Speed(%) / Accel         → 用 SPEED n
    //       CP(LMOVE)← SpeedS(mm/s) / AccelS(mm/s^2) → 用 SPEEDS v / ACCELS v
    //     见 MoveTo 里的分流。脚本侧 SpeedS/AccelS 默认 150mm/s / 1500mm/s^2。
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

        /// <summary>
        /// 抢 _ioLock 的最长等待（毫秒）。命令与状态轮询共用同一条 TCP 行协议，必须串行；
        /// 原来 TryEnter(50) 一次失败就返回 null（上层按"无应答"处理）——轮询每 250ms 抢一次锁，
        /// 就能把批量读点中的"某一次"打断（2026-09-11 现场"某个点读不到"的真因）。
        /// 改为在预算内重试抢锁：短抖动被吸收，长占用（同步运动等）仍快速放弃不拖住 UI。
        /// </summary>
        private const int IOLockWaitMs = 400;

        /// <summary>急停等安全指令的抢锁预算（毫秒）——只在【非阻断】路径（StopAxis 等）用。
        /// ★★2026-09-16 结论：这个预算治不了 MOVE 持锁（同步运动持锁最长 20s，8s 也抢不到）。
        /// 真正的急停通道 Halt() 已改为【绕开 _ioLock 直接写】+ 打断在飞等待，见 Halt 与 WriteLineRaw。</summary>
        private const int SafetyLockWaitMs = 8000;

        /// <summary>运动失败后回读位置的应答超时（毫秒）。只为诊断，取短值避免二次拖长时间。</summary>
        private const int StopProbeTimeoutMs = 1500;

        /// <summary>诊断回读的抢锁预算（毫秒）。此刻原事务已退出 _ioLock，通常瞬时可得；
        /// 若被别的线程占用（如 UI 轮询）也不值得久等——诊断信息缺失好过长等。</summary>
        private const int StopProbeLockWaitMs = 300;

        /// <summary>连接建立超时（毫秒）</summary>
        private const int ConnectTimeoutMs = 3000;

        /// <summary>后台轮询读位置的应答超时（毫秒）。★ 2026-09-16：脚本僵死时 POS? 若沿用
        /// PosQueryTimeoutMs(5s)，250ms 定时器的轮询会把通道几乎连续占满（每次占锁 5s 等超时），
        /// 业务命令只等 400ms 锁 ⇒ 全部"通道忙未发送"，真因（脚本任务已停）被淹没。
        /// 轮询宁可快速放弃、少一次刷新，也不能霸占业务命令的通道。</summary>
        private const int PollPositionsTimeoutMs = 800;

        /// <summary>主动探活（PING→PONG）的应答超时（毫秒）</summary>
        private const int ProbeAliveTimeoutMs = 1200;

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

        /// <summary>
        /// 应答行文本编码（AUTO / UTF8 / GBK / ASCII）。
        /// 用途：读 RC+ 示教点标签（POINT?）时中文可能按控制器区域设置编码输出。
        /// AUTO（默认）= 严格 UTF-8 优先、失败退 GBK；纯 ASCII 应答两种编码结果一致。
        /// 由 EpsonRobot 从设备配置 TextEncoding=… 注入，连接前设置生效。
        /// </summary>
        public string TextEncodingName { get; set; } = "AUTO";

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
            // ★ 2026-09-16：链路重置 ⇒ 之前登记的"急停迟到应答"不可能再到达，清账，
            //   否则重连后的第一条指令会白白吃掉一行（甚至等到超时）。
            Interlocked.Exchange(ref _stopRepliesPending, 0);
            return null;
        }

        public void Dispose() => Close();

        #endregion

        #region 行协议收发核心

        /// <summary>
        /// 发送一行指令并阻塞读取一行应答（线程安全：一发一收必须串行）。
        ///
        /// 【expectPrefix —— 2026-09-11 新增，修"某个点读不到"】
        ///   传入期望前缀（如 "POINT 7,"）后：读到【不匹配】的行视为"上一条指令的迟到应答"，
        ///   丢弃并继续等剩余超时（而不是立刻返回）。这样协议错位不再把下一条指令判成无应答。
        ///   传 null = 保持旧的"读到任意一行即返回"行为（DONE/OK/PONG 这类无歧义应答）。
        ///
        /// 返回 null = 通信超时/异常/通道忙；否则为应答原文（已去掉行尾 \r\n）。
        /// ★ 所有失败路径都写 Debug 日志（含原因）—— 否则"没反应"在日志里是隐形的，
        ///   现场只能靠猜（这正是本 bug 排查困难的根源）。
        /// </summary>
        private string _lastRxReply = string.Empty;

        /// <summary>
        /// 诊断位（ThreadStatic ⇒ 无跨线程竞态）：本线程【上一次】SendCommand 是否因
        /// "抢不到 IO 锁"而根本没发出去。调用方在 SendCommand 返回后立刻读它，即可把
        /// "通道忙（跳过）"与"发出去了但脚本没应答"区分开 —— 前者不该算断线。
        /// </summary>
        [ThreadStatic] private static bool _lastSendSkippedByLock;

        /// <summary>本线程上一次 SendCommand 用掉的抢锁预算（ms）——只为把归因日志写准。</summary>
        [ThreadStatic] private static int _lastLockBudgetMs;

        /// <summary>
        /// 诊断位：本线程上一次 SendCommand 是否【被急停打断】而放弃等待（2026-09-16）。
        /// 与"无应答"必须分开报：被急停打断是预期行为，不是通信故障。
        /// </summary>
        [ThreadStatic] private static bool _lastAbortedByHalt;

        /// <summary>
        /// 写锁：只保证"一行指令的写出"原子（不与其它写交错成半行）。
        /// ★ 2026-09-16：急停通道会绕开 _ioLock 直接写，但只要写仍走这里，协议就不会被写坏。
        /// 锁序：_ioLock → _writeLock（前者在外）。急停通道【只取 _writeLock】，不会死锁。
        /// </summary>
        private readonly object _writeLock = new object();

        /// <summary>
        /// 急停请求序号（2026-09-16）。每次 STOP 下发自增；SendCommand 在进入时记录当时的值，
        /// 等待期间发现序号变了 ⇒ 说明"我还在等应答的时候来了急停" ⇒ 立刻放弃等待。
        /// 用序号而不是 bool：新起的命令天然持有新序号，不会被历史急停误伤（无需清标志）。
        /// </summary>
        private long _haltSeq;

        /// <summary>
        /// 尚未被消费的"急停应答"条数（2026-09-16）。Halt 不等自己的 "OK"，所以它必然**迟到**；
        /// 控制器是 FIFO 的 ⇒ 它排在下一条指令的应答之前。不在协议层吃掉它，
        /// 下一条"期待 OK"的指令（关真空/抬 Z/MOTOR）会把它当成自己的应答 ⇒ **秒成功但实际没执行**。
        /// </summary>
        private int _stopRepliesPending;

        /// <summary>最近一次急停下发时刻——用来给"迟到应答"设保鲜期，避免永久吞掉正常应答。</summary>
        private DateTime _lastStopUtc = DateTime.MinValue;

        /// <summary>
        /// 尝试把一行应答判定为"急停的迟到应答"并消费掉。返回 true = 已消费（调用方应继续读下一行）。
        /// ★ 保鲜期 60s：超期就认定那条应答已丢失，把计数清零后不再吞 —— 宁可多一直等，
        ///   也不能永远吃掉正常应答（那会把"指令没执行"伪装成"成功"）。
        /// </summary>
        private bool TryConsumeStopReply(out string stale)
        {
            stale = null;
            if (Interlocked.CompareExchange(ref _stopRepliesPending, 0, 0) <= 0) return false;
            if ((DateTime.UtcNow - _lastStopUtc).TotalSeconds > 60.0)
            {
                Interlocked.Exchange(ref _stopRepliesPending, 0);
                return false;
            }
            Interlocked.Decrement(ref _stopRepliesPending);
            return true;
        }

        private string SendCommand(string command, int timeoutMs = ReplyTimeoutMs, string expectPrefix = null, int lockWaitMs = IOLockWaitMs)
        {
            _lastSendSkippedByLock = false;   // 每次调用先清，避免读到上一次的残留
            _lastAbortedByHalt = false;
            _lastLockBudgetMs = lockWaitMs;
            long myHaltSeq = Interlocked.Read(ref _haltSeq);   // ★ 本次事务的"急停基线"

            // ⚠ 防御①：脚本侧运行时错误（如 2345 IO 越界）会崩 mainTCP 任务并断开 TCP，
            // 断线竞态下 _tcp.Connected 可能短暂为 true 而 _stream 已被置 null → 必须双检
            if (!IsOpen || _stream == null)
            {
                Debug.WriteLine($"[EpsonTcp] ✗ {command} 未发送：通道未连接");
                return null;
            }

            // ⚠ 防御②（2026-09-02 UI 防卡死 / 2026-09-11 加固）：命令与轮询共用 _ioLock。
            // 若命令持锁等待应答（如 MOVE 等 DONE 最多 20s），UI 线程的轮询会阻塞在 lock 上
            // → 界面冻结只能强关。故用短超时抢锁 —— 但【必须在预算内重试】：
            // 原来 TryEnter(50) 一次失败即返回 null，而轮询每 250ms 就抢一次锁，
            // 于是批量读点总会在"某一次"被打断，上层误判成"某个点读不到"。
            if (!TryEnterIoLock(lockWaitMs))
            {
                _lastSendSkippedByLock = true;   // ★ 区分"根本没发出去"与"发出去了但没应答"
                Debug.WriteLine($"[EpsonTcp] ✗ {command} 未发送：通道忙（{lockWaitMs}ms 内未取得 IO 锁）");
                return null; // 通道忙：跳过本轮（调用方按无应答处理）
            }
            try
            {
                // ★★2026-09-16：写之前先清"已放弃事务的迟到应答"。
                //   依据：协议是纯请求-应答，脚本从不主动推送；此刻我方已持有 _ioLock
                //   ⇒ 通道上不可能存在属于"进行中事务"的应答 ⇒ 缓冲里若有字节，只可能来自
                //   已被放弃的事务（超时放弃 / 被急停打断）。不清它，它就会被下一条指令吞掉。
                try { if (_stream != null && _stream.DataAvailable) DrainPendingBytes(); } catch { }

                try
                {
                    var sw = Stopwatch.StartNew();

                    // 发送：一行指令 + CRLF（SPEL+ Line Input 以换行结束）
                    // ★ 2026-09-16：经 WriteLineRaw（_writeLock）写出，保证命令行原子。
                    WriteLineRaw(command);

                    // ★ 2026-09-16：运动类指令"先报已发出"——MOVE 是同步阻塞的（脚本到位才回
                    //   DONE），此前日志只有收到应答才打一行 ⇒ 长运动/卡死期间日志一片空白，
                    //   看上去像"什么都没发生"。现在起止两端都有痕迹，卡在哪一眼可见。
                    if (command.StartsWith("MOVE", StringComparison.OrdinalIgnoreCase) ||
                        command.StartsWith("LMOVE", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[EpsonTcp] ▶ {command} 已发出，等待 DONE（同步运动：控制器到位后才应答，上限 {timeoutMs}ms）");
                    }

                    // 接收：逐字节读到 '\n'（兼容 \r\n 与 \n）
                    // ★ 2026-09-11 改：收集【原始字节】再整行解码 —— 点标签(POINT?)可能含中文，
                    //   原先的 (char)buf[0] 会把 GBK/UTF-8 多字节字符拆成两个乱码字节。
                    //   纯 ASCII 应答（DONE/OK/POS/IN/OUT …）解码结果与原先完全一致，对既有指令零影响。
                    var buf = new byte[1];
                    string reply = null;
                    int mismatched = 0;                     // 被丢弃的错位应答条数（诊断用）
                    bool abortedByHalt = false;             // 被急停打断（内层循环置位）

                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        // ★★2026-09-16 急停抢占：等待应答期间若收到 STOP 请求，立刻放弃本次等待。
                        //   【为什么必须这样】MOVE 是同步阻塞的（脚本 SafeGo→Go 到位才回 DONE），
                        //   它持着 _ioLock 最长 20s；若在这里干等满，业务侧要 20s 后才知道"急停了"，
                        //   期间安全收尾/状态机全部卡住。这里最多 5ms 就放弃（见下方 Sleep(5)）。
                        //   放弃 ≠ 撤销运动：STOP 指令已经写出，控制器处理完后自然执行；
                        //   我们只是不再替一个注定被丢弃的应答守夜。
                        if (Interlocked.Read(ref _haltSeq) != myHaltSeq)
                        {
                            _lastAbortedByHalt = true;
                            DrainPendingBytes();
                            Debug.WriteLine($"[EpsonTcp] ⛔ {command} 等待被急停打断（STOP 已下发；本事务放弃等待应答）");
                            return null;
                        }

                        var rxBytes = new List<byte>(64);
                        bool lineDone = false;

                        // ---- 收一整行（在剩余超时预算内）----
                        // 用 Available 做非阻塞轮询，避免 Read 阻塞卡死收不到数据的场景
                        while (sw.ElapsedMilliseconds < timeoutMs)
                        {
                            // ★★2026-09-16（离线夹具抓出的真 bug）：急停检查必须【在内层也做】。
                            //   只放外层是不够的 —— 对端不应答时，内层会一直空转到 20s 超时才退出，
                            //   外层那句检查等于"等超时后才执行"，业务侧照样卡 20s（夹具 F/H 项失败即此）。
                            //   这里每 ~5ms（或每个字节）核一次，最多 5ms 就能把等待打断。
                            if (Interlocked.Read(ref _haltSeq) != myHaltSeq) { abortedByHalt = true; break; }

                            if (_stream.DataAvailable)
                            {
                                int n = _stream.Read(buf, 0, 1);
                                if (n <= 0) break;
                                byte b = buf[0];
                                if (b == (byte)'\n') { lineDone = true; break; }   // 行结束
                                if (b != (byte)'\r') rxBytes.Add(b);               // 忽略回车
                            }
                            else
                            {
                                Thread.Sleep(5);
                            }
                        }

                        if (abortedByHalt)
                        {
                            _lastAbortedByHalt = true;
                            DrainPendingBytes();
                            Debug.WriteLine($"[EpsonTcp] ⛔ {command} 等待被急停打断（STOP 已下发；本事务放弃等待应答）");
                            return null;
                        }

                        if (!lineDone || rxBytes.Count == 0) break;   // 超时/断流：没收到完整行

                        string line = DecodeLine(rxBytes.ToArray());

                        // ★★2026-09-16：急停的迟到应答先吃掉（它 FIFO 排在本指令应答之前）。
                        if (TryConsumeStopReply(out string staleStopReply))
                        {
                            mismatched++;
                            Debug.WriteLine($"[EpsonTcp] ⟲ {command} 丢弃急停的迟到应答: {staleStopReply}");
                            continue;
                        }

                        if (expectPrefix != null &&
                            !line.StartsWith(expectPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            // ★ 上一条指令的迟到应答（或某次超时后残留）→ 丢弃它继续等，
                            //   而不是把它当成这一条的应答去解析（那才会产生"序号/坐标解析失败"）。
                            mismatched++;
                            Debug.WriteLine($"[EpsonTcp] ⟲ {command} 丢弃错位应答: {line}");
                            continue;
                        }
                        reply = line;

                        // ★ 2026-09-16：读到应答后再核一次急停基线。万一 STOP 是在"本轮顶部的检查
                        //   之后、这一行到达之前"发出的，这行应答可能属于 STOP 或已失效
                        //   ⇒ 不能当作本指令的结果（否则会给上层一个"假的成功"）。
                        if (Interlocked.Read(ref _haltSeq) != myHaltSeq)
                        {
                            _lastAbortedByHalt = true;
                            Debug.WriteLine($"[EpsonTcp] ⛔ {command} 应答到达时已收到急停请求 ⇒ 本次结果作废");
                            return null;
                        }
                        break;
                    }

                    if (reply == null)
                    {
                        // 超时无应答：脚本应答可能迟到（如正在执行同步运动）——迟到应答的字节
                        // 会留在流缓冲里，污染下一条指令的应答（协议错位 → 误报解析失败）。
                        // 协议为纯请求-应答、脚本从不主动推送 → 超时放弃后清空残留是安全的。
                        DrainPendingBytes();
                        Debug.WriteLine($"[EpsonTcp] ✗ {command} 无应答（{timeoutMs}ms 超时" +
                                        (mismatched > 0 ? $"，其间丢弃 {mismatched} 条错位应答" : string.Empty) +
                                        "）");
                        return null;
                    }

                    // ★ 2026-09-11：去重打印只对高频轮询 POS? 生效。其它指令（尤其 POINT?/CHECK）
                    //   每条都必须打 —— 否则"某个点没反应"在日志里完全看不见，无法定位。
                    bool isPoll = string.Equals(command, "POS?", StringComparison.OrdinalIgnoreCase);
                    if (!isPoll || reply != _lastRxReply)
                    {
                        Debug.WriteLine($"[EpsonTcp] TX: {command}  RX: {reply}");
                    }
                    _lastRxReply = reply; // 更新缓存
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
        /// 写出一行指令（自动补 CRLF）。★ 2026-09-16：**所有**写出都必须经此方法。
        ///
        /// 【为什么单独抽出来】急停通道（Halt）要绕开 _ioLock —— 因为 _ioLock 会被 MOVE
        ///   这种"等 DONE 最长 20s"的事务整段持有，急停若也去抢锁就必然被丢弃（2026-09-16
        ///   实测：8000ms 预算仍抢不到）。但绕开 _ioLock 不等于可以裸写：两条线程同时
        ///   Write 同一 socket 有交错成半行指令的风险，_writeLock 只护住"一次写出"这一点点，
        ///   绝不会被长事务长期占用。锁序恒为 _ioLock → _writeLock，故无死锁。
        ///
        /// 抛 IOException 是刻意的：调用方（SendCommand）靠它判定"连接已死"并 Close()。
        /// </summary>
        private void WriteLineRaw(string command)
        {
            byte[] tx = Encoding.ASCII.GetBytes(command + "\r\n");
            lock (_writeLock)
            {
                var s = _stream;
                if (s == null) throw new IOException("通道未建立（_stream 为 null）");
                s.Write(tx, 0, tx.Length);
                s.Flush();
            }
        }

        /// <summary>
        /// 运动指令失败后的【现场定位】（2026-09-16 新增，只读，不改任何状态）。
        ///
        /// 【为什么需要】MOVE 无应答时，日志只有一句"无应答（超时或连接断开）"，而真实原因
        ///   至少有四种，处置完全不同：
        ///     ① 脚本任务崩了/断线        → 需 RC+ 重启任务
        ///     ② 任务活着但【仍阻塞在该运动里】→ 运动没结束（机械卡阻 / 控制器接受了却不回）
        ///     ③ 运动其实已完成，只是应答丢了 → 位置已到目标附近
        ///     ④ 根本没动（被拒/未受理）   → 目标不可达或控制器未执行
        ///   判据：超时后立刻补发一次 POS?。能应答 ⇒ 任务已脱离运动（用实际位置区分③④）；
        ///   无应答但连接仍在 ⇒ ②（这是"卡在运动里"的硬证据，不是脚本死亡）；
        ///   连接被 Close ⇒ ①。三者互斥，不许混着猜。
        /// </summary>
        private string ProbeWhereItStopped(float tx, float ty, float tz, float tu)
        {
            try
            {
                // 运动前的影子值（MoveTo 成功才更新 _pos，失败时它仍是运动前的读数）
                float[] before = { _pos[0], _pos[1], _pos[2], _pos[3] };

                string reply = SendCommand("POS?", StopProbeTimeoutMs, "POS", lockWaitMs: StopProbeLockWaitMs);

                if (_lastAbortedByHalt)
                    return "定位诊断：本次等待被【急停】打断（STOP 已写出）。注意 SPEL+ 单任务串行——"
                         + "若运动正在执行，控制器要等它结束后才轮到 STOP 执行。";

                if (reply != null && reply.StartsWith("POS", StringComparison.OrdinalIgnoreCase))
                {
                    float[] now = { _pos[0], _pos[1], _pos[2], _pos[3] };
                    float[] tgt = { tx, ty, tz, tu };
                    string[] nm = { "X", "Y", "Z", "U" };
                    float dMax = 0; int dAxis = -1;      // 与运动前比：有没有动
                    float eMax = 0; int eAxis = -1;      // 与目标比：到没到
                    for (int i = 0; i < 4; i++)
                    {
                        float d = Math.Abs(now[i] - before[i]);
                        if (d > dMax) { dMax = d; dAxis = i; }
                        float e = Math.Abs(now[i] - tgt[i]);
                        if (e > eMax) { eMax = e; eAxis = i; }
                    }
                    string pos = $"({now[0]:F3},{now[1]:F3},{now[2]:F3},{now[3]:F3})";
                    if (eMax <= 0.05f)
                        return $"定位诊断：POS? 可应答，实际位置 {pos} 已≈目标 ⇒ 运动其实已完成，只是 DONE 应答丢失；"
                             + $"下次运动前建议重新探活。";
                    if (dMax <= 0.05f)
                        return $"定位诊断：POS? 可应答，实际位置 {pos} 与运动前几乎相同 ⇒ 该运动【根本没执行】"
                             + $"（控制器未受理/被拒），不是卡在半途。";
                    return $"定位诊断：POS? 可应答，实际停在 {pos}（各轴距目标最大偏差 {eMax:F3}mm @{nm[eAxis]}，"
                         + $"相比运动前 {nm[dAxis]} 动了 {dMax:F3}mm）⇒ 运动已停下但【未到目标位】，属「停在半途」。";
                }

                if (!IsOpen || _stream == null)
                    return "定位诊断：连接已断开（脚本任务崩溃/断网）——需在 RC+ 中停止任务→重新编译→运行。";

                return $"定位诊断：POS? 也在 {StopProbeTimeoutMs}ms 内无应答，但连接仍在 ⇒ RC+ 脚本任务"
                     + $"【仍阻塞在该运动里】（Go 未返回）：运动没结束，也不是脚本死亡。"
                     + $"现场看机器人实际姿态/有无卡阻，必要时用控制器侧急停回路。";
            }
            catch (Exception ex)
            {
                return "定位诊断异常: " + ex.Message;
            }
        }

        /// <summary>
        /// 在预算内重试抢 IO 锁（见 IOLockWaitMs 的说明）。
        /// 一次 TryEnter 的等待刻意取短，循环重试——这样短抖动会被吸收，
        /// 而真正的长占用（同步运动持锁 20s）仍然快速放弃，不拖住调用方。
        /// </summary>
        private bool TryEnterIoLock(int budgetMs)
        {
            var sw = Stopwatch.StartNew();
            do
            {
                if (Monitor.TryEnter(_ioLock, 25)) return true;
            }
            while (sw.ElapsedMilliseconds < budgetMs);
            return false;
        }

        /// <summary>
        /// 一整行原始字节 → 字符串（按 TextEncodingName 解码）。
        /// ★ 2026-09-11 新增：为读 RC+ 示教点标签（可能是中文）而引入。
        ///   AUTO 策略：严格 UTF-8 解码成功就用它；抛 DecoderFallbackException 说明不是
        ///   合法 UTF-8 → 按中文环境常见的 GBK(936) 解。DONE/OK/POS/IN/OUT 等纯 ASCII
        ///   应答在两种编码下结果相同，因此对既有指令行为零影响。
        /// </summary>
        private string DecodeLine(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            string mode = (TextEncodingName ?? "AUTO").Trim().ToUpperInvariant();
            try
            {
                switch (mode)
                {
                    case "UTF8":
                    case "UTF-8":
                        return new UTF8Encoding(false, true).GetString(bytes).Trim();
                    case "GBK":
                    case "936":
                        return Encoding.GetEncoding(936).GetString(bytes).Trim();
                    case "ASCII":
                        return Encoding.ASCII.GetString(bytes).Trim();
                }
                try
                {
                    return new UTF8Encoding(false, true).GetString(bytes).Trim();
                }
                catch (DecoderFallbackException)
                {
                    return Encoding.GetEncoding(936).GetString(bytes).Trim();
                }
            }
            catch
            {
                // 兜底（如平台不支持 GBK 代码页）：退 ASCII，至少指令能继续跑
                return Encoding.ASCII.GetString(bytes).Trim();
            }
        }

        /// <summary>
        /// 发送指令并校验应答。
        /// 期望应答匹配 expect 开头（大小写不敏感）→ 返回 null（成功）；
        /// 否则返回带应答内容的错误消息。
        /// </summary>
        private string SendAndExpect(string command, string expect, int timeoutMs = ReplyTimeoutMs, int lockWaitMs = IOLockWaitMs)
        {
            string reply = SendCommand(command, timeoutMs, null, lockWaitMs);
            if (reply == null)
            {
                // ★★2026-09-16 归因修正：以前三种情况统一报"无应答（超时或连接断开）"，
                //   把"根本没发出去"和"被急停打断"都说成通信故障 —— 现场照这句去查断线，
                //   永远查不到锁竞争/急停这两条真因。三种形态必须分开报。
                if (_lastAbortedByHalt)
                    return $"指令 [{command}] 被急停中止（等待应答期间收到 STOP 请求）";
                if (_lastSendSkippedByLock)
                    return $"指令 [{command}] 未发出：通道忙（{_lastLockBudgetMs}ms 内未取得 IO 锁；"
                         + $"当前有指令正在占锁等待应答，同步运动最长 20s）";
                return $"指令 [{command}] 无应答（{timeoutMs}ms 超时或连接断开）";
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
        ///
        /// ★ 2026-09-15：由 private 放开为 public。**本方法没有缓存 —— 每次调用都是一次真实
        ///   POS? 往返**（缓存逻辑在调用方 GetPosition 里，按 PosCacheMs=100ms 节流）。
        ///
        /// 【为什么需要"无缓存读"】
        /// 到位判据必须用实时值。若用带缓存的口径连续读两次，两次拿到的是同一份影子值，
        /// 差值恒 0 → 会被误判成"已停稳"。本通道 IsMotionDone() 又恒为 true
        /// （见类内 IsMotionDone 注释），两者叠加等于一道【空闸】。
        /// EpsonRobot.GetPositionsFresh / MoveToPtpAndWait 依赖本方法拿实时值。
        /// 只读位置（不需要判到位）的调用方仍应走 GetPosition，省一次往返。
        /// </summary>
        public string QueryPositions()
        {
            // POS? 走短超时：脚本正常时毫秒级应答；脚本忙（同步运动）时快速失败放弃，
            // 避免长时间占锁（轮询场景 UI/后台节奏被拖住）。失败沿用旧影子值即可。
            // ★ 2026-09-16：带上应答前缀 "POS" —— 急停的迟到 "OK" 会落在流里，前缀过滤会把它
            //   当【错位应答】丢弃；否则它会被解析成 "POS? 应答格式异常: OK"。
            string reply = SendCommand("POS?", PosQueryTimeoutMs, "POS");
            if (reply == null) return "POS? 无应答";
            return ApplyPosReply(reply);
        }

        /// <summary>
        /// 【后台轮询专用】读四轴位置（2026-09-16 新增）。
        ///
        /// 【为什么必须与 QueryPositions 分开】
        ///   2026-09-16 现场：RC+ 脚本任务崩溃后 TCP 半死（写第一次才现形）。此时 250ms 定时器
        ///   的轮询每轮 POS? 都要等满 PosQueryTimeoutMs(5s) 才放弃、且【持锁等满这 5s】，
        ///   于是通道被轮询几乎连续占满；而业务命令（关真空/走位）抢锁预算只有 400ms
        ///   ⇒ 日志刷满"通道忙（400ms 内未取得 IO 锁）"，真因被淹没，安全收尾的关真空也发不出去。
        ///
        ///   本方法两条新规矩：
        ///     ① 抢锁预算 = 0：真命令优先，轮询抢不到就【跳过本轮】（绝不反过来占住通道）；
        ///     ② 应答超时 = PollPositionsTimeoutMs(800ms)：对端僵死时快速放弃，不长期占锁。
        ///
        /// 返回：null = 成功（或本轮跳过）。lockSkipped = true 表示【本轮被真命令占用而跳过】，
        /// 这不是故障，调用方不要计入断线判定（否则会把"工位正在跑"误报成"通信中断"）。
        /// </summary>
        public string QueryPositionsYield(out bool lockSkipped, int timeoutMs = PollPositionsTimeoutMs)
        {
            lockSkipped = false;
            if (!IsOpen || _stream == null) return "控制器未连接";

            // ★ 2026-09-16：同 QueryPositions，带 "POS" 前缀把急停的迟到 OK/其它残留当错位应答丢弃。
            string reply = SendCommand("POS?", timeoutMs, "POS", lockWaitMs: 0);
            lockSkipped = _lastSendSkippedByLock;
            if (lockSkipped) return null;      // 跳过本轮：沿用旧影子值（无副作用）
            // ★ 2026-09-16：被急停打断同样算"本轮跳过"——它不是通信故障，
            //   不该计进断线判定（否则按一次急停就多攒一次"通信中断"计数）。
            if (_lastAbortedByHalt) { lockSkipped = true; return null; }
            if (reply == null) return "POS? 无应答";
            return ApplyPosReply(reply);
        }

        /// <summary>
        /// 主动探活：PING → PONG（2026-09-16 新增，配 `IMotionCardHealthProbe`）。
        ///
        /// 【为什么需要】`IDevice.State == Connected` 只说明"当初连上了"。对端脚本任务崩溃
        /// 或断网时，TCP 是**半死**的：套接字在本地仍报 Connected，直到第一次 Write 抛
        /// IOException 才现形 ⇒ 故障被推迟到第一条业务指令（现场表现：Phase0 关真空即失败）。
        /// 这里用一次轻量往返主动判定，失败即可由调用方断开重连。
        ///
        /// 抢不到锁时返回 true（通道正被真命令占用，无法判定 —— 不作误报）。
        /// </summary>
        public bool ProbeAlive(int timeoutMs = ProbeAliveTimeoutMs)
        {
            if (!IsOpen || _stream == null) return false;

            string reply = SendCommand("PING", timeoutMs, "PONG", lockWaitMs: 0);
            if (_lastSendSkippedByLock) return true;   // 通道忙 ⇒ 无法判定，按存活处理
            return reply != null;
        }

        /// <summary>解析 "POS x,y,z,u" 并刷新影子位置。返回 null=成功；非 null=错误消息。</summary>
        private string ApplyPosReply(string reply)
        {
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

        /// <summary>
        /// 读 RC+ 示教点（只读，2026-09-11 新增）。
        /// 协议：POINT? n → "POINT n,x,y,z,u,标签"；点未定义 → "POINT n,UNDEF,Err"。
        /// 标签固定为【最后一段】，故按最多 6 段切分（标签内允许逗号）；标签可为空
        /// （该点号没注册标签时脚本回空串，坐标照常返回）。
        /// 返回 null = 成功（结果写进 point）；非 null = 错误消息。
        /// </summary>
        public string QueryPoint(int index, out EpsonPointRead point)
        {
            point = null;
            if (!IsOpen) return "控制器未连接";

            // 走短超时：读点不涉及运动，不应长时间占锁（与 POS? 同口径）。
            // ★ 2026-09-11 加固：单次失败【不再直接交回上层】—— 上层（调试台同步/任何批量读点）
            //   曾在第一处失败就整轮中止，现场看到的就是"读到一半没了、某个点读不到"。
            //   这里自己做最多 QueryPointAttempts 次重试；期望前缀精确到 "POINT {index},"，
            //   使上一条的迟到应答会被 SendCommand 丢弃（不会张冠李戴到本条上）。
            const int QueryPointAttempts = 3;
            string reply = null;
            string lastErr = null;
            for (int attempt = 1; attempt <= QueryPointAttempts && reply == null; attempt++)
            {
                reply = SendCommand($"POINT? {index}", PosQueryTimeoutMs, $"POINT {index},");
                if (reply == null)
                {
                    lastErr = $"POINT? {index} 无应答（第 {attempt}/{QueryPointAttempts} 次：通道忙或脚本未应答）";
                    // 退避：让在途的那一轮（轮询/上一次超时）先把应答收干净，再重发
                    if (attempt < QueryPointAttempts) Thread.Sleep(120 * attempt);
                }
            }
            if (reply == null) return lastErr;

            if (!reply.StartsWith("POINT", StringComparison.OrdinalIgnoreCase))
            {
                // RC+ 侧给的是可读 ERR（如 point index out of range）→ 原样透传
                return $"POINT? {index} 失败，控制器应答: {reply}";
            }

            string payload = reply.Substring(5).Trim();     // 去掉 "POINT"
            string[] parts = payload.Split(new[] { ',' }, 6);
            if (parts.Length < 2) return $"POINT? 应答格式异常: {reply}";

            int idx;
            if (!int.TryParse(parts[0].Trim(), out idx)) return $"POINT? 序号解析失败: {reply}";

            var p = new EpsonPointRead { Index = idx };
            string head = parts[1].Trim();
            bool isUndef = string.Equals(head, "UNDEF", StringComparison.OrdinalIgnoreCase);

            if (isUndef || parts.Length < 5)
            {
                p.Defined = false;      // 未定义点：不是错误，交由上层显示"(P? 未定义)"
                // 新版脚本回 "POINT n,UNDEF,Err"（Err=SPEL+ 错误号，如 2513 标签未注册）；
                // 旧版脚本只有两段 → ErrorCode 为 null，上层据此提示"脚本可能未重新编译"。
                p.ErrorCode = isUndef && parts.Length >= 3 ? parts[2].Trim() : null;
                point = p;
                return null;
            }

            var v = new float[4];
            for (int i = 0; i < 4; i++)
            {
                if (!float.TryParse(parts[i + 1].Trim(), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out v[i]))
                {
                    return $"POINT? 坐标解析失败（第{i + 1}轴）: {reply}";
                }
            }
            p.X = v[0]; p.Y = v[1]; p.Z = v[2]; p.U = v[3];
            p.Label = parts.Length >= 6 ? parts[5].Trim() : string.Empty;
            p.Defined = true;
            point = p;
            return null;
        }

        /// <summary>
        /// 零运动 PTP 可达性判定（只读，2026-09-11 新增）。
        /// 协议：CHECK x,y,z,u → "CHECK OK" | "CHECK NG"。
        /// 脚本内部走 SPEL+ 原生 TargetOK()，【不驱动电机】，可安全反复调用。
        ///
        /// ★重要限制（官方原文）："The motion trajectory to the target point is not
        ///   considered" —— TargetOK 只保证【终点】合法，不保证 Go 的弧线 / Move 的直线上
        ///   不越出活动范围。故本方法用作【预筛】：不通过 ⇒ 一定不能走；通过 ≠ 一定走得完。
        ///
        /// 返回 null = 成功（结果写进 reachable）；非 null = 错误消息。
        /// </summary>
        public string QueryCheck(float x, float y, float z, float u, out bool reachable)
        {
            reachable = false;
            if (!IsOpen) return "控制器未连接";

            // 坐标格式化为不变文化（避免系统区域设置把小数点变逗号）
            string coord = string.Format(CultureInfo.InvariantCulture,
                "{0:F3},{1:F3},{2:F3},{3:F3}", x, y, z, u);

            // 走短超时：纯计算不涉及运动，不应长时间占锁（与 POS? / POINT? 同口径）。
            // ★ 同 POINT? 加固：带期望前缀（丢弃错位应答）+ 2 次重试 —— 可达域扫描一次要发
            //   数百次 CHECK，单次抖动不该让整个方向被误判成"不可达"。
            const int CheckAttempts = 2;
            string reply = null;
            string lastErr = null;
            for (int attempt = 1; attempt <= CheckAttempts && reply == null; attempt++)
            {
                reply = SendCommand($"CHECK {coord}", PosQueryTimeoutMs, "CHECK");
                if (reply == null)
                {
                    lastErr = $"CHECK {coord} 无应答（第 {attempt}/{CheckAttempts} 次：通道忙或脚本未应答）";
                    if (attempt < CheckAttempts) Thread.Sleep(120 * attempt);
                }
            }
            if (reply == null) return lastErr;

            string head = reply.Trim();
            if (head.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase))
            {
                string tail = head.Substring(5).Trim();
                if (tail.Equals("OK", StringComparison.OrdinalIgnoreCase)) { reachable = true; return null; }
                if (tail.Equals("NG", StringComparison.OrdinalIgnoreCase)) { reachable = false; return null; }
                return $"CHECK 应答格式异常: {reply}";
            }

            // 脚本未更新（未知指令）/ OnErr 兜底返回 —— 原样透传便于现场定位
            return $"CHECK 失败，控制器应答: {reply}";
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

            // ★ 速度必须按运动类型分流（2026-09-11 修复）：
            //   SPEL+ 的速度是【双轨】的 —— Speed(%)/Accel 只作用于 PTP(Go)，
            //   直线 Move 归 SpeedS(mm/s)/AccelS(mm/s^2)。此前一律发 SPEED 百分比，
            //   等于对 LMOVE 完全没设速度（不改不报错，但速度不受控）。
            if (speed > 0)
            {
                if (linear)
                {
                    // CP 直线：脚本侧 SpeedS 单位就是 mm/s，业务层传的也是 mm/s → 直接下发。
                    // 脚本内钳位 1~2000，这里同步钳位避免发送必然被夹的垃圾值。
                    int mms = (int)Math.Max(1, Math.Min(2000, speed));
                    SendAndExpect($"SPEEDS {mms}", "OK"); // 失败不阻断：沿用控制器当前速度
                }
                else
                {
                    // PTP：脚本侧 Speed 是百分比，业务层 mm/s 无严格映射。
                    // TODO(实机标定)：按机型行程把 mm/s 换算为准确的 Speed 百分比
                    int pct = (int)Math.Max(1, Math.Min(100, speed));
                    SendAndExpect($"SPEED {pct}", "OK"); // 失败不阻断：沿用控制器当前速度
                }
            }

            // 四轴坐标格式化为不变文化（避免系统区域设置把小数点变逗号）
            string coord = string.Format(CultureInfo.InvariantCulture,
                "{0:F3},{1:F3},{2:F3},{3:F3}", x, y, z, u);

            // ★ MOVE(=Go) 的 DONE 语义按【异步】对待最安全：收到 DONE 只代表"指令已受理"。
            //   ★★ 2026-09-15 补记：本通道有个更隐蔽的坑 —— IsMotionDone() 恒 true，
            //   而 GetPosition 带 100ms 影子缓存、上层 SettleAfterIdle 两次读间隔只有 15ms
            //   ⇒ 两次读到同一份缓存，差值恒 0 ⇒ 恒判"已停稳"。三者叠加使上层的
            //   WaitAxesIdle 对 PTP 变成【空闸】，走 PTP 后立刻采图会拍到运动中的帧
            //   （2026-09-10 现场"拖尾/圆度骤减"即此形态）。
            //   ⇒ 走 PTP 要真到位，请用 EpsonRobot.MoveToPtpAndWait（内部按实时 POS? 收敛判定）。
            //   LMOVE(=Move) 是同步到位后才回 DONE，不受此坑影响。
            string err = SendAndExpect((linear ? "LMOVE " : "MOVE ") + coord, "DONE");
            if (err != null)
            {
                // ★★2026-09-16：失败必须带现场定位。此前只报一句"无应答（超时或连接断开）"，
                //   而"任务崩了 / 任务卡在运动里 / 其实到了只是应答丢了 / 根本没动"四种形态
                //   在这句话里长得一模一样，现场只能靠猜（本日 Phase3.6 卡死即此）。
                //   急停路径走快路：不在急停中再补一次回读占通道（机器人在不在动，现场一眼可见）。
                if (_lastAbortedByHalt)
                    return err + "｜急停已下发：本事务已放弃等待（STOP 已写出，控制器将在当前运动结束后执行）。";
                return err + "｜" + ProbeWhereItStopped(x, y, z, u);
            }

            // 运动完成，更新指令位置影子
            _pos[0] = x; _pos[1] = y; _pos[2] = z; _pos[3] = u;
            return null;
        }

        /// <summary>
        /// 【门型运动 JUMP】先抬到 limZ → 在 limZ 高度水平移动 → 降到目标 Z（2026-09-16 新增）。
        ///
        /// 【为什么加它】吸持工件后的平移原为"逐轴拆两步"(先 X 后 Y 各发一条 MOVE)，
        ///   末端走 L 形：① 在中间拐角点停一次；② 实测该工位 L 形第二段【贴内圈边界飞过】
        ///   （最紧点余量仅 1.5mm），而门型走同一对点的直线余量 31.3mm。
        ///   ⇒ 吸持平移改用门型：既短、又固定有"先抬到安全高度"这一步。
        ///
        /// 【两个判据都在脚本侧硬拦，这里只负责下发】：
        ///   · limZ <= 目标 Z ⇒ 脚本回 ERR（门型失效，拒绝静默退化）；
        ///   · limZ 超 Z 行程 ⇒ 脚本回 ERR。
        /// 属 PTP 类（Jump 受 SPEED 百分比控制，不是 SPEEDS）。
        /// </summary>
        public string MoveJump(float x, float y, float z, float u, float limZ, float speed)
        {
            if (!IsOpen) return "控制器未连接";

            // ★ 门型有效性闸（判据唯一源 EpsonJumpGuard）：**必须在本层就拦**。
            //   离线夹具正是靠这条抓出了缺口：原先只把闸放在 EpsonRobot 与脚本里，
            //   直接调适配层的路径（以及将来任何新调用点）会把 limZ 低于目标 Z 的门型
            //   原样发出去 —— 装成"门型"其实是横穿。早失败，也省一趟网络往返。
            string guard = EpsonJumpGuard.Check(z, limZ);
            if (guard != null) return guard;

            // Jump 是 PTP 类运动 → 速度走 SPEED 百分比（与 MoveTo 的非 linear 分支同源口径）
            if (speed > 0)
            {
                int pct = (int)Math.Max(1, Math.Min(100, speed));
                SendAndExpect($"SPEED {pct}", "OK"); // 失败不阻断：沿用控制器当前速度
            }

            // 五段：x,y,z,u,limz（脚本 Token$ 段数不足时返回空串，故 limz 可省略）
            string coord = string.Format(CultureInfo.InvariantCulture,
                "{0:F3},{1:F3},{2:F3},{3:F3},{4:F3}", x, y, z, u, limZ);

            string err = SendAndExpect("JUMP " + coord, "DONE");
            if (err != null)
            {
                if (_lastAbortedByHalt)
                    return err + "｜急停已下发：本事务已放弃等待（STOP 已写出，控制器将在当前运动结束后执行）。";
                return err + "｜" + ProbeWhereItStopped(x, y, z, u);
            }

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

        /// <summary>
        /// 减速停止（急停通道）。★★2026-09-16 重写：**不再抢 _ioLock**。
        ///
        /// 【为什么必须绕开锁】MOVE 是同步阻塞的（脚本 SafeGo → Go，到位才回 DONE），它整段
        ///   持有 _ioLock 最长 20s。急停若也去抢锁，只能在预算耗尽后放弃 —— 现场实测：
        ///   预算从 400ms 提到 8000ms，仍然「STOP 未发送：通道忙（8000ms 内未取得 IO 锁）」
        ///   连续两次。**提高预算治不了，只有绕开锁能治。**
        ///
        /// 现行为三步：
        ///   ① 递增 _haltSeq ⇒ 正在等应答的事务（就是那条 MOVE）最多 5ms 内放弃等待，
        ///      业务侧立刻拿到"被急停中止"，不必干等 20s；
        ///   ② 立即写出 STOP（只取 _writeLock，保证命令行原子）；
        ///   ③ 不等应答 —— SPEL+ 单任务串行：脚本执行 Go 期间根本不读 socket，
        ///      STOP 要等运动结束后才会被读到并执行。等它的 "OK" 毫无意义，反而再占住通道。
        ///
        /// 【诚实边界】本指令只能"尽快排队"。能立刻切断运动的只有控制器侧急停回路
        ///   （RC+ 的 E-Stop 输入 / 示教器急停），软件 STOP 抢占不了已在执行的 Go。
        /// </summary>
        public string Halt()
        {
            if (!IsOpen || _stream == null)
            {
                Debug.WriteLine("[EpsonTcp] ✗ 急停 STOP 未发出：通道未连接");
                return "控制器未连接，急停指令未能发出";
            }

            Interlocked.Increment(ref _haltSeq);   // ① 让在飞事务立即放弃等待

            try
            {
                WriteLineRaw("STOP");              // ② 绕开 _ioLock，立即写出
                // ★ 记账：STOP 的 "OK" 必然迟到（我们不等它），登记一笔待消费的迟到应答，
                //   由后续第一条指令在协议层丢弃它 —— 否则会把"没执行的指令"伪装成成功。
                _lastStopUtc = DateTime.UtcNow;
                Interlocked.Increment(ref _stopRepliesPending);
                Debug.WriteLine("[EpsonTcp] ⛔ 急停 STOP 已下发（绕开 IO 锁、不等应答）；"
                              + "SPEL+ 单任务串行 ⇒ 若运动正在执行，控制器将在其结束后才执行 STOP。");
                return null;                       // ③ "已写出"就算成功，不假装知道执行结果
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EpsonTcp] ✗ 急停 STOP 写出失败: {ex.Message}");
                try { Close(); } catch { }
                return $"急停指令写出失败: {ex.Message}";
            }
        }

        /// <summary>指令为同步应答（DONE 后才返回）→ 到这里运动必然已完成</summary>
        public bool IsMotionDone() => true;

        /// <summary>数字输出。业务 0 基号 → SPEL+ 物理口（EpsonIoMap：业务0=吸嘴1真空→Out(15)、业务1=吸嘴2真空→Out(14)）。
        /// ⚠ 业务号不是物理口号：填 15 会走 default「+1」发成 OUT 16 —— 2026-09-16 现场实测 Out(16) 越界令脚本任务崩溃、TCP 断开。</summary>
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

    /// <summary>
    /// RC+ 示教点读取结果（POINT? 应答的解析产物）。
    /// Defined=false 表示该点号在 RC+ 点文件里没有定义（不是通信错误）。
    /// </summary>
    internal sealed class EpsonPointRead
    {
        public int Index { get; set; }
        public bool Defined { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float U { get; set; }
        public string Label { get; set; }
        /// <summary>SPEL+ 错误号（仅 Defined=false 时可能非空；旧版脚本为 null）</summary>
        public string ErrorCode { get; set; }
    }
}
