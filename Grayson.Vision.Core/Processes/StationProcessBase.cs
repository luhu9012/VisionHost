using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Interfaces;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.Processes;
using Grayson.Vision.Contracts.Station.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 工位业务过程基类。
    ///
    /// 职责分层约定：
    /// - 编辑器节点流（配方）只编排「视觉段」——换产品需要调参的部分
    ///   （采图 → 匹配 → 标定换算），节点编排交给 FlowEdit；
    /// - 本类用代码写死「运动/IO 时序」——由机械结构决定、不随产品变化的
    ///   拍照位/吸取/放料节拍，时序调整直接改代码。
    ///
    /// 业务过程通过 Worker.RunVisionChainAsync 直连调度器跑视觉流（避免
    /// TriggerOnceAsync 的过程分流递归），跑完后从执行链节点的输出端口
    /// DataValue 读回视觉结果。
    /// </summary>
    public abstract class StationProcessBase : IStationProcess
    {
        /// <summary>所属工位 Worker（触发视觉流、读执行链、访问设备）</summary>
        protected readonly StationWorker Worker;

        /// <summary>运动控制卡逻辑别名（工位管理中绑定的卡名）</summary>
        protected readonly string CardAlias;

        private IMotionCard _card;

        protected StationProcessBase(StationWorker worker, string cardAlias)
        {
            Worker = worker ?? throw new ArgumentNullException(nameof(worker));
            CardAlias = cardAlias ?? throw new ArgumentNullException(nameof(cardAlias));
        }

        /// <summary>业务过程唯一键（如 "MahjongPick"），与 StationConfigModel.ProcessKey 对应。</summary>
        public abstract string ProcessKey { get; }

        /// <summary>
        /// 业务过程入口（子类实现完整时序）。
        /// 返回 true = 成功；false = 业务 NG（不触发 Faulted）；抛异常 = 异常（Worker 记 Faulted）。
        /// </summary>
        public abstract Task<bool> RunAsync(CancellationToken token = default);

        /// <summary>
        /// 安全收尾：停止/急停时由 Worker 调用，尽力回到安全姿态（抬 Z、关真空）。
        /// 默认实现为空，子类应按机械结构 override（如抬 Z 至安全高度 + 关闭所有真空阀）。
        /// </summary>
        public virtual Task SafeStopAsync(CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// 急停：控制器级全轴急停（RapidStop），立即停住所有已下发的轴运动。
        /// 背景：上位机取消任务（CancellationToken）并不会停住 ZMC 控制器已缓冲的轴运动，
        /// 轴会继续跑完整个运动——急停必须显式下发全轴急停指令。
        /// 急停语义 = 立即停住并锁定，**不再下发任何运动指令**（不同于 SafeStopAsync 的"安全收尾抬 Z"）；
        /// 复位/抬 Z 由操作员确认机械安全后手动执行。
        /// 子类可 override：先关闭真空等负载保护，再调用 base 下发全轴急停。
        /// </summary>
        public virtual Task EmergencyStopAsync(CancellationToken token = default)
        {
            try
            {
                if (Card == null)
                {
                    Log("⚠️ 急停：运动卡不可用，跳过全轴急停指令。");
                    return Task.CompletedTask;
                }

                var res = Card.RapidStop();
                if (res.Success)
                    Log("⛔ 全轴急停：RapidStop 已下发（所有轴立即减速停止）。"
                        + "⚠ 本通道为单任务串行控制时，该指令只能【排队】：运动正在执行时，控制器要等它结束才执行急停；"
                        + "真正能立即切断的只有控制器侧急停回路（E-Stop 输入/示教器急停）。");
                else
                    Log($"⚠️ 全轴急停指令下发失败: {res.Message}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ 全轴急停异常: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// ★★2026-09-16 新增：业务循环开跑前的【口径解析 + 跨源核对 + 兜底拦截】。
        ///
        /// 【一句话语义（现场就该记住这一条）】
        ///   **口径的真源 = 该工位相机槽的标定档案**（= 校验台校验成功时用的那一份裁定）；
        ///   工位过程配置只是**发布那一刻的快照副本**，过期了不会再安静地生效。
        ///
        /// 【为什么必须这样（撞机事故的正因）】
        ///   原实现把"工位配置快照"当唯一真源：档案里明明写着「③杆端域、b=(5.943,132.172)mm」，
        ///   但只要**发布动作漏做/被别的槽覆盖**，快照里就什么都没有 ⇒ 生产端静默退 ②直吸 H(u)
        ///   ⇒ 引导定位扎在离工件 132.305mm 的地方 ⇒ 撞机（2026-09-16 ST_002 实测）。
        ///   而"生产端自己跟自己比"永远查不出来：ConsumptionTag 与 HasRodOffset 写在**同一套扁平字段**里，
        ///   **同源**，一起错时永远"一致"。互校两量必须**不同源** —— 这里读的档案就是第二把尺子。
        ///
        /// 【处置（把"口径不对 ⇒ 撞机"改成"口径不对 ⇒ 要么按档案走、要么站着不动并说清处方"）】
        ///   ① 档案可得 ⇒ **按档案分型执行**（数值仍取工位配置快照：b/O/e 是数值发布物，不抢它的权）；
        ///      与快照不一致 ⇒ 打 ERROR 级「快照已过期」并给处方（重新发布），但**不拦**——口径跟校验结果走。
        ///   ② 档案不可得 ⇒ 用快照，并显式标明"口径真源=快照（本工位没定位到上相机槽档案）"。
        ///   ③ 硬拦仅四条，都是"确定会偏 / 只能靠赌"的情形（有据不放过）：
        ///        A. 生效 ③ 杆端域，但 b 数值拿不到 ⇒ 一定偏 |b|；
        ///        B. 生效 EIH，但旋转中心 O 拿不到 ⇒ 一定退化成直吸；
        ///        C. 档案里的**独立证据**指向杆端域（旋转拟合半径 > 20mm，或特征模板名含「杆/延申/mark」），
        ///           而生效分型却不带 b ⇒ 自相矛盾（这两条判据与契约的 InferNozzleDomain 同源）；
        ///        D. 生效 ③ 且 b 数值齐备，但**符号从来没判定过**（RodOffsetSign 键缺席 = 吃着默认 +1）
        ///           ⇒ 选错偏 2|b|（比不补更危险）。这条是 2026-09-16 补的：符号的"值"与"是否判定过"
        ///           在数值上无法区分，而发布链的同值剪枝还会把现场选的 +1 静默剪掉。
        ///   ④ "从未发布过"（无标签）不再等于"一致"：显式留痕，且档案不可得时写明"判据失效：没核过"。
        ///
        /// ★ 措辞纪律（不要替错误背书）：拦住 = **不进入生产运动序列**，
        ///   不等于"整条业务一步没动" —— 异常兜底仍会发一条【原地抬 Z 至安全高度】
        ///   （`EpsonRobot.MoveAbsolute` 用控制器实时位置补全其余三轴 ⇒ 是原地抬，不是平移）。
        /// </summary>
        /// <returns>本工位**最终生效**的口径（调用方必须用它，不要再用快照那份）</returns>
        protected ConsumptionDecision ResolveConsumptionGate(
            ConsumptionDecision snapshot, ConsumptionInputs inp, string publishedTag, string processLabel)
        {
            if (snapshot == null) return null;   // 该过程没有口径概念（纯 IO/时序工位）

            var rec = snapshot.Reconcile(publishedTag);
            var audit = ConsumptionPublishAudit.Audit(Worker?.StationId, snapshot);

            // ---- ① 定真源 ----
            ConsumptionDecision effective = snapshot;
            string sourceText;
            if (audit.ArchiveFound && audit.ArchiveDecision != null)
            {
                // 分型取档案，符号取工位配置（符号是本机的现场 A/B 结论，档案里没有该字段）
                effective = CalibrationConsumptionContract.RewriteKindFrom(
                    snapshot, audit.ArchiveDecision, inp, snapshot.RodOffsetSign,
                    $"槽 {audit.SlotKey} 档案");
                sourceText = $"槽 {audit.SlotKey} 标定档案（与校验台同一份判定）";
            }
            else
            {
                sourceText = "工位配置发布快照（本工位未定位到上相机槽档案）";
            }

            string sig = $"{rec.Status}|{audit.ArchiveFound}|{audit.Conflict}|{effective.Tag}";
            bool changed = sig != _lastConsumptionGateSig;
            _lastConsumptionGateSig = sig;

            Log($"[口径闸] {processLabel} 生效口径 = [{effective.Tag}] {effective.Formula}");
            Log($"[口径闸]   口径真源 = {sourceText}；b 符号 = "
                + (effective.RodOffsetTerm
                    ? (effective.RodOffsetSign < 0 ? "-1" : "+1")
                      + (inp.RodOffsetSignDeclared
                            ? "（工位配置显式判定）"
                            : "（⚠ 这是代码默认值：现场从未判定过）")
                    : "n/a"));

            // ---- ② 快照是否过期 ----
            if (audit.ArchiveFound && audit.Conflict)
            {
                Log($"[口径闸] ❌ 工位配置【发布快照已过期】：快照判 [{snapshot.Tag}]，档案判 [{audit.ArchiveTag}]。"
                    + "本次已按【档案】执行（口径跟校验结果走，不再被过期快照改写）。"
                    + "处方：到标定中心对本槽的 H 档案点一次【发布到工位】，把快照刷新成档案口径，消除分叉。");
            }

            if (changed)
                foreach (var l in audit.Lines) Log("[口径闸] " + l);

            if (rec.Status == TagReconcileStatus.NoTag && !audit.ArchiveFound)
            {
                Log("[口径闸] ⚠ 工位配置里没有口径发布标签，且档案不可得 ⇒ **判据失效：没核过，不是通过了**。"
                    + "本次按快照口径执行；请到标定中心对本工位拍照相机对应的档案发布一次。");
            }

            // ---- ③ 硬拦：只剩"确定会偏"的情形 ----
            if (effective.RodOffsetTerm)
            {
                double mag = Math.Sqrt(inp.RodOffsetWx * inp.RodOffsetWx + inp.RodOffsetWy * inp.RodOffsetWy);
                if (mag < 1e-9)
                {
                    throw new InvalidOperationException(
                        $"【口径闸】拒绝执行：生效口径 [{effective.Tag}] 是【固定相机 + 杆端域】，"
                        + "吸点 = H(u) + b，但工位配置里 b 的数值拿不到（RodOffsetWx/Wy 均为 0）"
                        + "⇒ 继续运动必然整片偏一个 |b|"
                        + (audit.RodOffsetMagnitudeMm > 1.0
                            ? $"，而档案里量到的 |b| = {audit.RodOffsetMagnitudeMm:F3}mm"
                            : "")
                        + "。处置：到标定中心选中本槽的 H 档案点【发布到工位】"
                        + "（发布链会从同槽 e 档案取 ToolEccW 写入 b；首次接入会要求你确认 b 的符号）。");
                }

                // ★★ 2026-09-16 新增（与 ConsumptionTag 缺席被当成「一致」同构的漏洞）：
                //   符号的**值**（默认 1f）与「是否判定过」在数值上无法区分 —— 键缺席时取默认，
                //   日志打出「b 符号 = +1（取工位配置）」看着像已确认，实际**从来没人判定过**。
                //   更糟的是发布链 SetFields 的**同值剪枝**：现场选 +1 写进去的值与代码默认相同
                //   ⇒ 被静默剪掉、键根本落不了盘（选 −1 却写得进 ⇒ 症状不对称）。
                //   处置上 fail-closed：选错符号偏 2|b|（本工位 264mm），比不补 b（偏 1|b|）更危险，
                //   所以宁可站着不动，也不拿默认值去赌。
                if (!inp.RodOffsetSignDeclared)
                {
                    throw new InvalidOperationException(
                        $"【口径闸】拒绝执行：生效口径 [{effective.Tag}] 要补 b，但工位配置里"
                        + "【从来没有判定过 b 的符号】—— RodOffsetSign 键不存在，一直吃着代码默认 +1。"
                        + (audit.RodOffsetMagnitudeMm > 1.0
                            ? $"符号选错的代价是 2|b| = {2 * audit.RodOffsetMagnitudeMm:F3}mm"
                            : "符号选错的代价是 2|b|")
                        + "，比不补 b（偏 1|b|）更危险，所以不猜。"
                        + "处置：① 到校验台【高级·反面对照】用口径 ④（吸点 = H(u)+b）与 ⑤（吸点 = H(u)−b）"
                        + "各点同一个像素，只有【吸嘴尖正好落在点上】的那个符号是对的（另一个落在 2|b| 之外）；"
                        + "② 记下符号后到标定中心重新【发布到工位】，发布链会把选择写进"
                        + " RodOffsetSign + RodOffsetSignDeclared 两个键（后者默认 false，所以写得进、不会被同值剪枝吞掉）。");
                }
            }

            if (effective.NeedO && !inp.HasRotationCenter)
            {
                throw new InvalidOperationException(
                    $"【口径闸】拒绝执行：生效口径 [{effective.Tag}] 是【眼在手 EIH】，"
                    + "X_obj = P_photo + O − H(u) 必须有旋转中心 O，但工位配置里 O 拿不到 ⇒ "
                    + "继续运动会静默退化成直吸 H(u)，落点不可信。"
                    + "处置：先完成该槽的旋转中心标定，再发布到工位。");
            }

            if (effective.IsFixedCameraFamily && !effective.RodOffsetTerm && audit.RodEndEvidence)
            {
                throw new InvalidOperationException(
                    $"【口径闸】拒绝执行：档案里的【独立证据】指向杆端域（{audit.RodEndEvidenceText}），"
                    + $"但生效口径 [{effective.Tag}] 不带 b 项 —— 两者自相矛盾。"
                    + (audit.RodOffsetMagnitudeMm > 1.0
                        ? $"同槽量出的杆端偏心 |b| = {audit.RodOffsetMagnitudeMm:F3}mm（来源「{audit.RodOffsetSource ?? "未记录"}」）。"
                        : "")
                    + "处置：在校验台对该槽点【采纳系统判定】把 H 域固化成声明，"
                    + "并把档案 RodOffsetInProduction 置 true 后重新发布；再用口径 ④/⑤ 做 A/B 确认符号。");
            }

            // ---- ④ 阻断项/告警照旧全部打出（不静默）----
            foreach (var blk in effective.Blockers) Log($"⛔ {blk}");
            foreach (var wn in effective.Warnings) Log($"⚠ {wn}");

            return effective;
        }

        /// <summary>口径闸上次的结论签名（只在变化时打明细，防连续生产刷屏）</summary>
        private string _lastConsumptionGateSig;

        /// <summary>
        /// ★ 2026-09-16 新增：业务循环开跑前的【运动卡通道探活】。
        ///
        /// 【为什么必须单独探一次】
        ///   `EnsureCardConnected` 只看 `State`，而半死 TCP（RC+ 脚本任务崩溃 / 中途断网）
        ///   在第一次 Write 抛 IOException 之前仍报 Connected ⇒ 故障被推迟到第一条业务指令。
        ///   现场（2026-09-16 ST_002）：Phase0 关真空即报"通道忙/无应答"，随后整屏
        ///   "400ms 内未取得 IO 锁"，真因（对端脚本已停）被彻底淹没。
        ///
        /// 【行为】
        ///   探活 → 失败则断开重连（走 EnsureCardConnected 的正常路径）→ 再探一次；
        ///   仍不通则抛带处置指引的异常（明确告诉现场去 RC+ 重启 mainTCP 任务）。
        ///   只在 Phase0（无运动在途）调用，故断开是安全的。
        /// </summary>
        protected void EnsureMotionCardAlive()
        {
            IMotionCard card;
            try
            {
                card = Card;   // 触发解析 + 自动连接（失败会抛）
            }
            catch
            {
                throw;         // 解析/连接失败已有明确异常，原样上抛
            }

            if (!(card is IMotionCardHealthProbe probe)) return;   // 本地卡/仿真通道不做探活

            if (probe.ProbeAlive())
            {
                Log($"运动卡 [{CardAlias}] 通道探活通过。");
                return;
            }

            Log($"⚠️ 运动卡 [{CardAlias}] 探活失败（连接状态 [{card.State}]，但 PING 无应答）——RC+ 脚本任务可能已停止，断开重连...");
            try { card.Disconnect(); } catch (Exception ex) { Log($"⚠️ 断开旧连接失败: {ex.Message}"); }
            _card = null;          // 清缓存 → 下次 Card 访问重新解析 + 重连

            card = Card;           // 重连（失败即抛"连接失败"异常）
            if (card is IMotionCardHealthProbe probe2 && !probe2.ProbeAlive())
            {
                throw new InvalidOperationException(
                    $"运动卡 [{CardAlias}] 通信未建立：TCP 已连上但 PING 无应答。" +
                    "RC+ 侧脚本任务（mainTCP）很可能已停止运行 —— 请在 RC+ 中【停止任务 → 重新运行该任务】后重试；" +
                    "若任务正在运行，请检查其是否因指令错误（如 IO 越界）而中断。");
            }
            Log($"运动卡 [{CardAlias}] 重连并探活通过。");
        }

        /// <summary>
        /// 运动控制卡实例（首次访问时按别名解析；别名未命中时按类型回退；解析成功后自动连接）。
        /// 连接失败抛出带错误详情的异常并清空缓存，下一次访问会重新解析+重试连接。
        /// </summary>
        protected IMotionCard Card
        {
            get
            {
                if (_card == null)
                {
                    _card = Worker.Context.ResolveDevice(CardAlias) as IMotionCard;
                    if (_card == null)
                    {
                        // 回退：按类型扫描工位上下文已注册设备，取第一个 IMotionCard。
                        // 免疫逻辑名拼写差异（如配置别名 "MotionCard" vs 工位绑定名 "MotionCard1"）。
                        _card = ResolveMotionCardByType();
                        if (_card != null)
                        {
                            Log($"⚠️ 逻辑别名 [{CardAlias}] 未命中，已按类型回退解析到工位绑定的运动卡（建议将工位绑定逻辑名统一为 [{CardAlias}]）");
                        }
                        else
                        {
                            Log($"⚠️ 未解析到运动控制卡 [{CardAlias}]，请检查工位设备绑定。");
                        }
                    }

                    if (_card != null)
                    {
                        EnsureCardConnected(_card);
                    }
                }
                return _card;
            }
        }

        /// <summary>
        /// 确保运动卡已建立通信连接（未连接则自动 Connect，仿相机节点 EnsureCameraReady 模式）。
        /// 背景：工位创建/启动流程不调用 OpenAllAsync，运动卡若从未 Connect，
        /// CardHandle 恒为 IntPtr.Zero，zmcaux 零句柄调用全部返回 20009（网口通信失败）。
        /// 连接失败：清空 _card 缓存并抛出带错误详情的异常，便于直接定位（控制器上电/网线/网段/IP）。
        /// </summary>
        private void EnsureCardConnected(IMotionCard card)
        {
            try
            {
                if (card.State == DeviceState.Connected || card.State == DeviceState.Opened)
                    return;

                Log($"运动卡 [{CardAlias}] 当前状态 [{card.State}]，尝试自动连接...");
                var res = card.Connect();
                if (res?.Success != true)
                {
                    var msg = $"运动卡 [{CardAlias}] 连接失败: {res?.Message ?? "未知错误"}";
                    Log($"⚠️ {msg}（请检查控制器上电/网线/网段/IP）");
                    _card = null; // 清缓存，下次访问重新解析+重试连接
                    throw new InvalidOperationException(msg);
                }
                Log($"运动卡 [{CardAlias}] 连接成功。");
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _card = null;
                throw new InvalidOperationException($"运动卡 [{CardAlias}] 自动连接异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 按类型回退解析：遍历工位上下文已注册逻辑设备，返回第一个 IMotionCard 实例。
        /// 工位在同一时刻只会绑定一张运动控制卡，按类型回退是安全的。
        /// </summary>
        private IMotionCard ResolveMotionCardByType()
        {
            try
            {
                foreach (var key in Worker.Context.DeviceManager.GetDeviceStates().Keys)
                {
                    if (Worker.Context.ResolveDevice(key) is IMotionCard card)
                    {
                        return card;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ 按类型回退解析运动卡失败: {ex.Message}");
            }
            return null;
        }

        #region 运动与 IO 辅助

        /// <summary>
        /// 单轴绝对运动并等待到位（到位后再等稳定时间，避免运动残余振动）。
        /// </summary>
        /// <param name="axis">轴号</param>
        /// <param name="position">绝对目标位置</param>
        /// <param name="speed">速度；&lt;=0 时沿用轴当前速度（严禁传 0：ZMC SetSpeed(0) 后 MoveAbs 不运动）</param>
        /// <param name="settleMs">到位后稳定等待 ms</param>
        /// <param name="timeoutMs">运动超时 ms（超时抛异常终止过程）</param>
        protected async Task MoveAbsAsync(int axis, float position, float speed = 0,
            int settleMs = 100, int timeoutMs = 180000, CancellationToken token = default)
        {
            if (Card == null) throw new InvalidOperationException($"运动卡 [{CardAlias}] 不可用");

            // speed<=0 → 传 -1，由驱动层沿用轴当前速度（驱动层内部兜底，绝不下发 0）
            var effectiveSpeed = speed > 0 ? speed : -1f;
            Log($"轴{axis} 运动至 {position:F3}，速度 {(speed > 0 ? speed.ToString("F1") : "沿用当前")} ...");

            var moveRes = Card.MoveAbsolute(axis, position, effectiveSpeed);
            if (!moveRes.Success) throw new InvalidOperationException($"轴{axis} 发起运动失败: {moveRes.Message}");

            await WaitAxisIdleAsync(axis, timeoutMs, token).ConfigureAwait(false);

            if (settleMs > 0) await Task.Delay(settleMs, token).ConfigureAwait(false);

            Log($"轴{axis} → {position:F3} 到位");
        }

        /// <summary>
        /// 【门型走位】先抬到安全高度 limZ → 在 limZ 高度水平移动到 (x,y) → 降到目标 z（四轴一次性下发）。
        ///
        /// ★为什么平移要用它，而不是"连调两次 <see cref="MoveAbsAsync"/>"：
        ///   逐轴调用会在中途的拐角点停一次，末端走 L 形。用本工位实测可达域核算，
        ///   那条 L 形路径（取料位→下相机位）的第二段最紧点离内圈边界只剩 1.5mm（贴边飞过）；
        ///   门型走同一对点的直线，最紧点余量 31.3mm。
        ///
        /// ★返回 false = 本运动卡不提供门型（<see cref="IMotionCard.MoveJump"/> 返回 Fail）。
        ///   调用方**必须显式降级并留痕**，不许静默穿过 ——
        ///   本方法刻意不替调用方猜轴号（轴号是工位配置的东西）。
        /// </summary>
        // ============================================================
        // 门型走位所需的工位参数 —— 由派生类转发自己的工位配置
        // ★刻意做成一组属性而不是"各过程各写一份门型逻辑"：
        //   本项目吃过"同一条判据写两遍 ⇒ 两边分叉 ⇒ 靠巧合正确"的亏。
        //   门型的高度裁决必须只有一份。
        // ============================================================

        // ★★2026-09-16 加固：这 8 个参数【刻意不给默认值】，读不到就抛。
        //   理由：给 AxisX=0 / AxisZ=2 / XySpeed=0 这类"看起来合理"的假默认值，
        //   等于给"派生类漏 override"发了一张免罪牌 —— 会静默按【错轴】运动。
        //   本机存在两套完全不同的轴布局：Epson SCARA = 0=X/1=Y/2=Z/3=U；
        //   双滑台 = 0=Z/1=X/2=右Y/3=左Y（见 MahjongPickConfig）——
        //   照基类默认值走就是"想动 X 结果动了 Z"，是撞机生成器。
        //   ★同时删除原 `SafeZ` 属性：它一个读者都没有（调用方传的是各工位 _cfg.SafeZ），
        //   留着就是"有配置项、没人读"的假接线。

        private static InvalidOperationException GantryParamMissing(string name) =>
            new InvalidOperationException(
                $"门型走位参数 [{name}] 未配置：派生类必须 override [{name}]，"
                + "把本工位的轴号/速度/高度转发过来。"
                + "（基类刻意不给默认值——'看起来合理'的假默认会让漏配静默按错轴运动）");

        /// <summary>X 轴号（派生类必须 override）</summary>
        protected virtual int AxisX => throw GantryParamMissing(nameof(AxisX));
        /// <summary>Y 轴号（派生类必须 override）</summary>
        protected virtual int AxisY => throw GantryParamMissing(nameof(AxisY));
        /// <summary>Z 轴号（派生类必须 override）</summary>
        protected virtual int AxisZ => throw GantryParamMissing(nameof(AxisZ));
        /// <summary>U 轴号（派生类必须 override）</summary>
        protected virtual int AxisU => throw GantryParamMissing(nameof(AxisU));
        /// <summary>门型水平段高度（必须高于目标 Z；本工位参考值 -30；派生类必须 override）</summary>
        protected virtual float JumpLimZ => throw GantryParamMissing(nameof(JumpLimZ));
        /// <summary>XY 运动速度（Epson 侧为 Speed 百分比档；派生类必须 override）</summary>
        protected virtual float XySpeed => throw GantryParamMissing(nameof(XySpeed));
        /// <summary>Z 轴运动速度（派生类必须 override）</summary>
        protected virtual float ZSpeed => throw GantryParamMissing(nameof(ZSpeed));
        /// <summary>到位后稳定等待 ms（派生类必须 override）</summary>
        protected virtual int SettleMs => throw GantryParamMissing(nameof(SettleMs));

        /// <summary>
        /// 【门型走位】先抬到安全高度 → 水平走到 (x,y) → 降到目标 z（一次四轴下发）。
        ///
        /// ★这个方法是<see cref="StationProcessBase"/>里**唯一**该被用来做平面/复合位移的手段。
        ///   不要为了"把 XY 各自挪到位"去连调两次 <see cref="MoveAbsAsync"/> —— 那会走 L 形
        ///   （现场事故：X 先横移 174mm、Y 停在原处，实测那条 L 形路径第二段离内圈边界只剩 1.5mm）。
        ///
        /// ★卡不支持门型 ⇒ **显式降级**为"抬 Z → XY → 降 Z"并留痕，绝不静默按原路穿过。
        /// </summary>
        /// <param name="u">目标 U；null = 保持当前 U 不动</param>
        protected async Task MoveGantryAsync(double x, double y, double z, CancellationToken token,
            double? u = null)
        {
            float limZ = ResolveGantryLimZ(z);
            double uu = u ?? GetAxisPos(AxisU);

            if (await TryMoveJumpAsync(x, y, z, uu, limZ, XySpeed, SettleMs, token).ConfigureAwait(false))
                return;

            Log("⚠ [门型] 本卡不支持门型运动 ⇒ 降级为分段走位：先抬 Z → XY 走位 → 再降 Z。"
                + "注意 XY 段仍是逐轴 L 形，路径敏感时请改用支持门型(JUMP)的运动卡。");
            await MoveAbsAsync(AxisZ, limZ, ZSpeed, settleMs: SettleMs, token: token).ConfigureAwait(false);
            await MoveAbsAsync(AxisX, (float)x, XySpeed, SettleMs, token: token).ConfigureAwait(false);
            await MoveAbsAsync(AxisY, (float)y, XySpeed, SettleMs, token: token).ConfigureAwait(false);
            await MoveAbsAsync(AxisZ, (float)z, ZSpeed, settleMs: SettleMs, token: token).ConfigureAwait(false);
        }

        /// <summary>
        /// 取门型水平段高度：以工位配置 <see cref="JumpLimZ"/> 为准，并保证它落在 (目标Z, 0) 开区间内。
        ///
        /// ★Z 域顶=0、向下为负是本机（Epson SCARA）约定。若最终仍给不出有效高度 ⇒ **抛异常**：
        ///   宁可一步不动，也不许"没有安全高度还硬平移" —— 那正是本次撞机的形态。
        ///   （反面教材：静默兜底成一个仍然无效的值，然后照走。）
        /// </summary>
        protected float ResolveGantryLimZ(double targetZ)
        {
            float limZ = JumpLimZ;
            if (limZ > -1f)
            {
                Log($"⚠ [门型] 配置 JumpLimZ={limZ:F3} 已到/超过 Z 域顶(0) ⇒ 本次钳到 -1.000");
                limZ = -1f;
            }
            if (limZ <= targetZ)
            {
                float fb = (float)Math.Min(-1.0, targetZ + 30.0);
                Log($"⚠ [门型] 配置 JumpLimZ={JumpLimZ:F3} 未高于目标 Z={targetZ:F3} ⇒ 本次兜底为 {fb:F3}"
                    + "（目标 Z + 30mm）。这说明工位配置里的安全高度是错的，请改正（别依赖兜底）。");
                limZ = fb;
            }
            if (limZ <= targetZ)
            {
                throw new InvalidOperationException(
                    $"门型走位无法成立：水平段高度 limZ={limZ:F3} 不高于目标 Z={targetZ:F3}。"
                    + "处置：到工位配置里把 JumpLimZ 设成【严格高于目标 Z、且低于 Z 域顶 0】的值"
                    + "（本机参考：SafeZ=工位安全高度、Z 域顶=0）。"
                    + "拒绝执行是为了不重演『没有安全高度还硬平移』那次撞机。");
            }
            return limZ;
        }

        /// <returns>true=已按门型执行并等到位；false=本卡不支持门型，请调用方降级</returns>
        protected async Task<bool> TryMoveJumpAsync(double x, double y, double z, double u, float limZ,
            float speed = 0, int settleMs = 100, CancellationToken token = default)
        {
            if (Card == null) throw new InvalidOperationException($"运动卡 [{CardAlias}] 不可用");

            Log($"[门型] → ({x:F3},{y:F3},{z:F3}) U={u:F3}｜水平段高度 limZ={limZ:F3}"
                + "（先抬到 limZ → 水平走 → 降到目标；不走逐轴 L 形）");

            var res = Card.MoveJump((float)x, (float)y, (float)z, (float)u, limZ, speed);
            if (!res.Success)
            {
                Log($"⚠ [门型] 本运动卡不支持门型运动：{res.Message}");
                return false;
            }

            if (settleMs > 0) await Task.Delay(settleMs, token).ConfigureAwait(false);
            Log($"[门型] 到位 ({x:F3},{y:F3},{z:F3})");
            return true;
        }

        /// <summary>
        /// 轮询等待轴空闲（含 100ms 稳定确认：连续两次空闲才认为真正停止）。
        /// 报警/限位触发时立即失败，防止撞限位后 IDLE=true 被误判为"到位"。
        /// </summary>
        protected async Task WaitAxisIdleAsync(int axis, int timeoutMs = 30000, CancellationToken token = default)
        {
            if (Card == null) throw new InvalidOperationException($"运动卡 [{CardAlias}] 不可用");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool stableOnce = false;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                token.ThrowIfCancellationRequested();

                // 硬件急停轮询：子类按接线 override（读急停按钮输入口）。命中立即中止等待，
                // 由业务过程 catch(OCE) 统一补触发 Worker 级急停（ErrorLocked + 红灯蜂鸣）。
                if (PollEmergencyStop())
                    throw new OperationCanceledException("检测到硬件急停信号，运动等待中止");

                var statusRes = Card.GetAxisStatus(axis);
                if (statusRes.Success)
                {
                    if (statusRes.Data.HasFlag(AxisStatusFlags.Alarm))
                        throw new InvalidOperationException($"轴{axis} 伺服报警，业务过程中止");
                    if (statusRes.Data.HasFlag(AxisStatusFlags.FwdLimit))
                        throw new InvalidOperationException($"轴{axis} 触发正向限位，运动被机械阻挡，业务过程中止");
                    if (statusRes.Data.HasFlag(AxisStatusFlags.RevLimit))
                        throw new InvalidOperationException($"轴{axis} 触发反向限位，运动被机械阻挡，业务过程中止");
                }

                var idleRes = Card.IsAxisIdle(axis);
                if (idleRes.Success && idleRes.Data)
                {
                    if (stableOnce)
                    {
                        return; // 连续两次采样空闲 → 真正到位
                    }
                    stableOnce = true;
                }
                else
                {
                    stableOnce = false;
                }

                await Task.Delay(20, token).ConfigureAwait(false);
            }

            throw new TimeoutException($"轴{axis} 等待到位超时 ({timeoutMs}ms)");
        }

        /// <summary>
        /// 读取轴反馈位置（编码器实际位置）
        /// </summary>
        protected float GetAxisPos(int axis)
        {
            if (Card == null) throw new InvalidOperationException($"运动卡 [{CardAlias}] 不可用");
            var res = Card.GetFeedbackPosition(axis);
            if (!res.Success) throw new InvalidOperationException($"轴{axis} 读取位置失败: {res.Message}");
            return res.Data;
        }

        /// <summary>
        /// 数字输出（真空阀 / 吹气阀 / 色灯等）
        /// </summary>
        protected void SetOutput(int ioNum, bool state)
        {
            if (Card == null) throw new InvalidOperationException($"运动卡 [{CardAlias}] 不可用");
            var res = Card.SetOutput(ioNum, state);
            if (!res.Success) throw new InvalidOperationException($"IO{ioNum} 输出 {state} 失败: {res.Message}");
            Log($"IO{ioNum} = {(state ? "ON" : "OFF")}");
        }

        /// <summary>
        /// 读取数字输入口（IN）。**静默读取不写日志**——轮询场景（原点/急停/检知采样）
        /// 逐条打日志会刷屏，日志由调用方按业务语义输出；读取失败抛异常终止过程。
        /// </summary>
        protected bool GetInput(int ioNum)
        {
            if (Card == null) throw new InvalidOperationException($"运动卡 [{CardAlias}] 不可用");
            var res = Card.GetInput(ioNum);
            if (!res.Success) throw new InvalidOperationException($"IN{ioNum} 读取失败: {res.Message}");
            return res.Data;
        }

        /// <summary>
        /// 硬件急停轮询钩子：子类按接线 override（如读取急停按钮输入口），
        /// 返回 true 表示检测到急停信号。WaitAxisIdleAsync 运动等待循环内每 20ms 采样一次，
        /// 命中立即中止等待并抛 OperationCanceledException；业务过程在 catch(OCE) 中
        /// 依据内部标记补触发 Worker 级急停（ErrorLocked + 红灯蜂鸣）。
        /// 默认不轮询（false）——纯视觉链/无急停按钮接线的过程零行为变化。
        /// </summary>
        protected virtual bool PollEmergencyStop() => false;

        /// <summary>
        /// 工位复位通知（接口实现）：Worker.SoftResetAsync 复位完成后调用。
        /// 默认空实现；子类可 override 恢复指示状态（清报警灯/蜂鸣器、回待机灯）。
        /// </summary>
        public virtual Task OnResetAsync(CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        #endregion

        #region 视觉流调用与结果读取

        /// <summary>
        /// 触发当前加载的视觉流（直连调度器 TriggerOnceAsync 链路，
        /// 与编辑器/工位监视页的"单次执行"完全一致）。
        /// 注意：不能走 Worker.TriggerOnceAsync——过程模式下该方法会再次分流
        /// 进入本过程造成"过程 → 过程"递归，必须经 Worker 的直连通道。
        /// </summary>
        protected async Task RunVisionFlowAsync(string batchId = null)
        {
            await Worker.RunVisionChainAsync(batchId).ConfigureAwait(false);

            // 🌟 设备级失败分流：执行链 Failed（相机打开失败/节点异常等）绝不能伪装成"视觉 NG"。
            //    调度器把节点异常捕获在工单层、Task 本身正常返回，这里必须显式核对结果，
            //    抛出后由派生类 catch 分支走"设备异常"收尾（红灯+蜂鸣持续，等待操作员复位）。
            if (Worker.LastChainResult == ChainExecutionResult.Failed)
            {
                throw new InvalidOperationException(
                    $"视觉链执行失败（设备/节点异常）: {Worker.LastChainError ?? "详见执行日志"}");
            }
        }

        /// <summary>
        /// 分段执行视觉链 [startIndex, endIndexExclusive)（复合工位"上相机段 → 机械动作 →
        /// 下相机段"交错节拍专用）。失败分流逻辑与 RunVisionFlowAsync 一致。
        /// </summary>
        protected async Task RunVisionFlowSegmentAsync(int startIndex, int endIndexExclusive, string batchId = null)
        {
            await Worker.RunVisionChainRangeAsync(startIndex, endIndexExclusive, batchId).ConfigureAwait(false);

            if (Worker.LastChainResult == ChainExecutionResult.Failed)
            {
                throw new InvalidOperationException(
                    $"视觉链分段 [{startIndex},{endIndexExclusive}) 执行失败（设备/节点异常）: {Worker.LastChainError ?? "详见执行日志"}");
            }
        }

        /// <summary>
        /// 异常/停止兜底：尽力把指定轴抬至安全高度（防吸着料悬停低位），
        /// 失败仅记录日志不抛出，保证调用方（catch 块）能正常结束。
        /// </summary>
        protected async Task EmergencyRaiseZAsync(int axis, float safeZ, float zSpeed,
            CancellationToken token = default)
        {
            try
            {
                if (Card != null)
                {
                    Card.MoveAbsolute(axis, safeZ, zSpeed);
                    await WaitAxisIdleAsync(axis, 10000, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ 异常回安全高度失败，请手动确认轴{axis}状态: {ex.Message}");
            }
        }

        /// <summary>
        /// 按节点类型在当前执行链中查找节点（视觉流结果读取入口）
        /// </summary>
        protected FlowNodeBase FindNode(NodeType type)
        {
            return Worker.ActiveExecutionChain?.Nodes?.FirstOrDefault(n => n.Type == type && n.Enable);
        }

        /// <summary>
        /// 递归查找节点（穿透 CompositeFlow 子流程）：复合工位配方把视觉链封装在
        /// Group 子流程里，顶层 ActiveExecutionChain.Nodes 只有 CompositeFlow 节点，
        /// 必须下钻 SubProcess 才能找到内部的 ShapeMatch / CalibrationApply。
        /// 按配方顺序返回第一个匹配节点（复合流程先返回上相机段，后返回下相机段）。
        /// </summary>
        protected FlowNodeBase FindNodeRecursive(NodeType type)
        {
            var top = FindNode(type);
            if (top != null) return top;

            foreach (var node in Worker.ActiveExecutionChain?.Nodes ?? Enumerable.Empty<FlowNodeBase>())
            {
                if (node is CompositeFlowNode composite && composite.SubProcess != null)
                {
                    var found = FindNodeInProcess(composite.SubProcess, type);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// 在指定子流程内查找第一个匹配 NodeType 的启用节点（不继续穿透更深层子流程）。
        /// </summary>
        protected FlowNodeBase FindNodeInProcess(FlowProcessModel process, NodeType type)
        {
            if (process?.Nodes == null) return null;
            foreach (var node in process.Nodes)
            {
                if (node.Type == type && node.Enable) return node;
            }
            return null;
        }

        /// <summary>
        /// 按 DisplayName 关键字定位顶层 CompositeFlow 子流程节点（如"下相机"）。
        /// 返回其 SubProcess，供引擎读取该段视觉链内的节点结果。
        /// </summary>
        protected FlowProcessModel FindSubProcessByKeyword(string keyword)
        {
            foreach (var node in Worker.ActiveExecutionChain?.Nodes ?? Enumerable.Empty<FlowNodeBase>())
            {
                if (node is CompositeFlowNode composite
                    && composite.SubProcess != null
                    && !string.IsNullOrEmpty(composite.DisplayName)
                    && composite.DisplayName.Contains(keyword))
                {
                    return composite.SubProcess;
                }
            }
            return null;
        }

        /// <summary>
        /// 【分段索引】按 DisplayName 关键字定位顶层 CompositeFlow 子流程在**执行链中的下标**（-1=未找到）。
        /// 分段执行 <see cref="RunVisionFlowSegmentAsync"/> 的边界依赖该下标（业务过程不能再用
        /// 硬编码的 0/1 —— 画布重排后顺序会变，硬编码会静默跑错段）。
        /// </summary>
        protected int FindSubProcessIndexByKeyword(string keyword)
        {
            var nodes = Worker.ActiveExecutionChain?.Nodes;
            if (nodes == null) return -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] is CompositeFlowNode composite
                    && composite.SubProcess != null
                    && !string.IsNullOrEmpty(composite.DisplayName)
                    && composite.DisplayName.Contains(keyword))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>顶层执行链节点总数（分段执行的右边界）。</summary>
        protected int TopLevelChainNodeCount => Worker.ActiveExecutionChain?.Nodes?.Count ?? 0;

        /// <summary>
        /// 在**指定顶层段**内查找节点（该段本身是目标类型就直接返回；是 CompositeFlow 则下钻其子流程）。
        /// ★ 复合工位读结果必须用它而不是 FindNodeRecursive：两个子流程里各有一个 ShapeMatch，
        ///   按"链里第一个"去找会读到另一段的节点（上/下相机结果串台）。
        /// </summary>
        protected FlowNodeBase FindNodeInSegment(int segmentIndex, NodeType type)
        {
            var nodes = Worker.ActiveExecutionChain?.Nodes;
            if (nodes == null || segmentIndex < 0 || segmentIndex >= nodes.Count) return null;
            var node = nodes[segmentIndex];
            if (node == null || !node.Enable) return null;
            if (node.Type == type) return node;
            if (node is CompositeFlowNode composite && composite.SubProcess != null)
                return FindNodeInProcess(composite.SubProcess, type);
            return null;
        }

        /// <summary>执行链里是否存在 CompositeFlow 子流程节点（判断"复合配方"还是"传统单链配方"）。</summary>
        protected bool HasCompositeFlowNode()
        {
            var nodes = Worker.ActiveExecutionChain?.Nodes;
            return nodes != null && nodes.Any(n => n is CompositeFlowNode);
        }

        /// <summary>
        /// 读取节点参数模型上的字符串字段（用于业务过程侧"开工前预检"：模板名/矩阵路径等）。
        /// ⚠ ParameterModel 是 object（具体 Param 类型在 Grayson.Vision.Nodes，Core 不引用该程序集），
        ///   故只能走反射；任何一步失败都返回 null，绝不抛异常（预检不能把执行链搞挂）。
        /// </summary>
        protected static string ReadNodeParamString(FlowNodeBase node, string propertyName)
        {
            try
            {
                var model = node?.ParameterModel;
                if (model == null) return null;
                var prop = model.GetType().GetProperty(propertyName);
                return prop?.GetValue(model) as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 在指定子流程内读取第一个匹配 NodeType 节点的输出端口值（辅助复合工位分段读取结果）。
        /// </summary>
        protected T GetSubProcessOutValue<T>(FlowProcessModel subProcess, NodeType type, string portName)
        {
            var node = FindNodeInProcess(subProcess, type);
            return GetOutValue<T>(node, portName);
        }

        /// <summary>
        /// 读取节点输出端口当前值（视觉流跑完后调用）
        /// </summary>
        protected T GetOutValue<T>(FlowNodeBase node, string portName)
        {
            var port = node?.OutputPorts?.FirstOrDefault(
                p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase));
            return port?.DataValue is T typed ? typed : default;
        }

        #endregion

        protected void Log(string message)
        {
            LogBus.Info("StationProcess", $"[{Worker.StationId}] {message}");
        }
    }
}
