//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TriggerSourceBase.cs
// 说 明: 触发源基类——实现信号整形（边沿检测/防抖/丢帧策略/节拍统计）的公共逻辑。
//        各具体触发源（Manual/Timer/PlcBit）继承此类，只需实现信号采集部分。
//===================================================================================

using System;
using System.Collections.Concurrent;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.Triggers;

namespace Grayson.Vision.Core.Triggers
{
    /// <summary>
    /// 触发源抽象基类：封装信号整形 + 丢帧策略 + 节拍统计。
    /// 子类只需实现 Start/Stop/Configure 中的信号采集逻辑，然后调 RaiseSignal(bool) 即可。
    /// </summary>
    public abstract class TriggerSourceBase : ITriggerSource
    {
        public string StationId { get; }
        public abstract TriggerSourceType SourceType { get; }
        public bool IsRunning { get; protected set; }

        protected TriggerSourceConfig Config { get; private set; }

        // ===== 信号整形状态 =====
        private bool _lastSignal;           // 上一次信号电平（用于边沿检测）
        private DateTime _lastEdgeTime;     // 上一次有效边沿时间（用于防抖）
        private bool _hasFirstSignal;       // 是否已收到过首次信号（首次不检测边沿）

        // ===== 丢帧策略状态 =====
        private volatile bool _isExecuting; // 当前是否正在执行中
        private volatile bool _hasPending;  // 是否有 1 个待执行信号被缓存

        // ===== 节拍统计 =====
        private long _totalTriggered;
        private long _totalExecuted;
        private long _droppedCount;
        private DateTime? _lastTriggerTime;
        private double _lastCycleMs;
        private readonly ConcurrentQueue<double> _intervalHistory = new ConcurrentQueue<double>();
        private const int MaxIntervalSamples = 20;

        // ===== 事件 =====
        public event EventHandler<TriggerEventArgs> OnTriggered;
        public event EventHandler<TriggerErrorEventArgs> OnError;

        protected TriggerSourceBase(string stationId)
        {
            StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
        }

        /// <summary>
        /// 子类实现配置逻辑（如建立 PLC 连接引用、设置定时器周期等）。
        /// </summary>
        public abstract void Configure(TriggerSourceConfig config,
            Grayson.Vision.Contracts.Devices.Services.IDevicePool devicePool = null);

        /// <summary>
        /// 子类实现启动逻辑（如开始 PLC 轮询、启动定时器）。
        /// </summary>
        public abstract void Start();

        /// <summary>
        /// 子类实现停止逻辑（如停止轮询、释放定时器）。
        /// </summary>
        public abstract void Stop();

        /// <summary>
        /// 手动触发——直接进入信号整形管线（Manual 源的正常路径，其他源的"测试触发"入口）。
        /// </summary>
        public void ManualTrigger()
        {
            // 手动触发绕过边沿检测和防抖（人点按钮本身就是离散事件），直接走到丢帧策略
            ProcessTriggeredSignal();
        }

        // ===== 信号采集入口：子类轮询到信号后调此方法 =====

        /// <summary>
        /// 子类采集到信号后调用此方法，传入当前电平。
        /// 基类负责边沿检测 + 防抖 + 丢帧策略。
        /// </summary>
        protected void RaiseSignal(bool currentSignal)
        {
            if (!IsRunning) return;

            // 首次信号：初始化基线，不触发
            if (!_hasFirstSignal)
            {
                _hasFirstSignal = true;
                _lastSignal = currentSignal;
                return;
            }

            // 1. 边沿检测
            bool edgeDetected = DetectEdge(_lastSignal, currentSignal);
            _lastSignal = currentSignal;

            if (!edgeDetected) return;

            // 2. 防抖检测
            if (Config.DebounceMs > 0)
            {
                var now = DateTime.Now;
                if ((now - _lastEdgeTime).TotalMilliseconds < Config.DebounceMs)
                {
                    // 在防抖窗口内，视为抖动，丢弃
                    return;
                }
                _lastEdgeTime = now;
            }

            // 3. 通过边沿+防抖，进入丢帧策略
            ProcessTriggeredSignal();
        }

        /// <summary>
        /// 边沿检测：根据配置的 Edge 模式判断是否有效触发。
        /// </summary>
        private bool DetectEdge(bool oldSignal, bool newSignal)
        {
            switch (Config.Edge)
            {
                case TriggerEdge.None:
                    // 不检测边沿——信号为 true 时持续触发（实际由丢帧策略控制频率）
                    return newSignal;
                case TriggerEdge.Rising:
                    return !oldSignal && newSignal;   // false→true
                case TriggerEdge.Falling:
                    return oldSignal && !newSignal;   // true→false
                case TriggerEdge.Both:
                    return oldSignal != newSignal;    // 任意跳变
                default:
                    return false;
            }
        }

        /// <summary>
        /// 信号已通过边沿+防抖，进入丢帧策略判定。
        /// </summary>
        private void ProcessTriggeredSignal()
        {
            _totalTriggered++;
            _lastTriggerTime = DateTime.Now;

            // 丢帧策略：执行中收到新信号时的处理
            if (_isExecuting)
            {
                switch (Config.DropStrategy)
                {
                    case DropStrategy.DropOldest:
                        // 保留最新：标记一个 pending，等当前执行完成后响应当前最新信号
                        _hasPending = true;
                        _droppedCount++;
                        LogBus.Debug("TriggerSource",
                            $"[{StationId}] 执行中收到触发信号，DropOldest 策略：标记 pending");
                        break;
                    case DropStrategy.DropNewest:
                        // 丢弃最新：直接忽略，当前执行完成后再等下一个信号
                        _droppedCount++;
                        LogBus.Debug("TriggerSource",
                            $"[{StationId}] 执行中收到触发信号，DropNewest 策略：丢弃");
                        break;
                    case DropStrategy.QueueOne:
                        // 只排一帧：与 DropOldest 类似但语义更明确
                        _hasPending = true;
                        _droppedCount++;
                        break;
                }
                return;
            }

            // 空闲：直接发出触发事件
            FireTriggered();
        }

        /// <summary>
        /// 发出触发事件并标记执行中。
        /// 宿主收到事件后调 TriggerOnceAsync，执行完成后调 MarkExecutionComplete 恢复。
        /// </summary>
        private void FireTriggered()
        {
            _isExecuting = true;
            _totalExecuted++;

            // 记录触发间隔（用于统计平均间隔）
            if (_lastTriggerTime.HasValue)
            {
                var interval = (DateTime.Now - _lastTriggerTime.Value).TotalMilliseconds;
                _intervalHistory.Enqueue(interval);
                while (_intervalHistory.Count > MaxIntervalSamples)
                {
                    double _;
                    _intervalHistory.TryDequeue(out _);
                }
            }

            try
            {
                OnTriggered?.Invoke(this, new TriggerEventArgs
                {
                    SourceType = SourceType,
                    Timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                // 宿主回调异常不应打断触发源
                LogBus.Error("TriggerSource", $"[{StationId}] OnTriggered 回调异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 宿主执行完成后调用此方法，恢复"可接受新触发"状态。
        /// 如果有 pending 信号，自动再触发一次。
        /// </summary>
        public void MarkExecutionComplete(double cycleMs = 0)
        {
            _lastCycleMs = cycleMs;
            _isExecuting = false;

            // DropOldest/QueueOne 策略下，有 pending 信号则再触发一次
            if (_hasPending)
            {
                _hasPending = false;
                LogBus.Info("TriggerSource", $"[{StationId}] 执行完成，响应 pending 信号自动触发");
                FireTriggered();
            }
        }

        /// <summary>
        /// 子类调用此方法上报异常。
        /// </summary>
        protected void RaiseError(string source, Exception ex, bool recoverable = true)
        {
            LogBus.Error("TriggerSource", $"[{StationId}] 触发源异常 ({source}): {ex.Message}", ex);
            try
            {
                OnError?.Invoke(this, new TriggerErrorEventArgs
                {
                    Source = source,
                    Error = ex,
                    IsRecoverable = recoverable
                });
            }
            catch { /* 告警订阅者异常忽略 */ }
        }

        /// <summary>
        /// 子类在 Configure 完成后调用此方法保存配置引用。
        /// </summary>
        protected void SetConfig(TriggerSourceConfig config)
        {
            Config = config ?? new TriggerSourceConfig();
        }

        public TriggerStats GetStats()
        {
            double avgInterval = 0;
            if (_intervalHistory.Count > 0)
            {
                double sum = 0;
                foreach (var v in _intervalHistory) sum += v;
                avgInterval = sum / _intervalHistory.Count;
            }

            return new TriggerStats
            {
                TotalTriggered = _totalTriggered,
                TotalExecuted = _totalExecuted,
                DroppedCount = _droppedCount,
                LastTriggerTime = _lastTriggerTime,
                LastCycleMs = _lastCycleMs,
                AvgIntervalMs = avgInterval
            };
        }

        public virtual void Dispose()
        {
            IsRunning = false;
        }
    }
}
