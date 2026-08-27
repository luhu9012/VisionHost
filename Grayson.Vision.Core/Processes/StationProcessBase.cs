using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
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
    /// 业务过程通过 TriggerOnceAsync 复用调度器跑视觉流，
    /// 跑完后从执行链节点的输出端口 DataValue 读回视觉结果。
    /// </summary>
    public abstract class StationProcessBase
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

        /// <summary>
        /// 运动控制卡实例（首次访问时按别名解析，解析失败返回 null）
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
                        Log($"⚠️ 未解析到运动控制卡 [{CardAlias}]，请检查工位设备绑定。");
                    }
                }
                return _card;
            }
        }

        /// <summary>
        /// 业务过程入口（子类实现完整时序）
        /// </summary>
        public abstract Task<bool> RunAsync(CancellationToken token = default);

        #region 运动与 IO 辅助

        /// <summary>
        /// 单轴绝对运动并等待到位（到位后再等稳定时间，避免运动残余振动）。
        /// </summary>
        /// <param name="axis">轴号</param>
        /// <param name="position">绝对目标位置</param>
        /// <param name="speed">速度；&lt;=0 时使用轴当前参数</param>
        /// <param name="settleMs">到位后稳定等待 ms</param>
        /// <param name="timeoutMs">运动超时 ms（超时抛异常终止过程）</param>
        protected async Task MoveAbsAsync(int axis, float position, float speed = 0,
            int settleMs = 100, int timeoutMs = 30000, CancellationToken token = default)
        {
            if (Card == null) throw new InvalidOperationException($"运动卡 [{CardAlias}] 不可用");

            var moveRes = speed > 0 ? Card.MoveAbsolute(axis, position, speed) : Card.MoveAbsolute(axis, position, 0);
            if (!moveRes.Success) throw new InvalidOperationException($"轴{axis} 发起运动失败: {moveRes.Message}");

            await WaitAxisIdleAsync(axis, timeoutMs, token).ConfigureAwait(false);

            if (settleMs > 0) await Task.Delay(settleMs, token).ConfigureAwait(false);

            Log($"轴{axis} → {position:F3} 到位");
        }

        /// <summary>
        /// 轮询等待轴空闲（含 100ms 稳定确认：连续两次空闲才认为真正停止）。
        /// </summary>
        protected async Task WaitAxisIdleAsync(int axis, int timeoutMs = 30000, CancellationToken token = default)
        {
            if (Card == null) throw new InvalidOperationException($"运动卡 [{CardAlias}] 不可用");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool stableOnce = false;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                token.ThrowIfCancellationRequested();

                var statusRes = Card.GetAxisStatus(axis);
                if (statusRes.Success && statusRes.Data.HasFlag(AxisStatusFlags.Alarm))
                {
                    throw new InvalidOperationException($"轴{axis} 伺服报警，业务过程中止");
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
        /// 数字输出（真空阀 / 吹气阀等）
        /// </summary>
        protected void SetOutput(int ioNum, bool state)
        {
            if (Card == null) throw new InvalidOperationException($"运动卡 [{CardAlias}] 不可用");
            var res = Card.SetOutput(ioNum, state);
            if (!res.Success) throw new InvalidOperationException($"IO{ioNum} 输出 {state} 失败: {res.Message}");
            Log($"IO{ioNum} = {(state ? "ON" : "OFF")}");
        }

        #endregion

        #region 视觉流调用与结果读取

        /// <summary>
        /// 触发当前加载的视觉流（复用调度器 TriggerOnceAsync 链路，
        /// 与编辑器/工位监视页的"单次执行"完全一致）。
        /// </summary>
        protected Task RunVisionFlowAsync(string batchId = null)
        {
            return Worker.TriggerOnceAsync(batchId);
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
