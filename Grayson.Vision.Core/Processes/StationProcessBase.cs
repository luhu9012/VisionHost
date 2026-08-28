using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
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
                    Log("⛔ 全轴急停：RapidStop 已下发（所有轴立即减速停止）。");
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
