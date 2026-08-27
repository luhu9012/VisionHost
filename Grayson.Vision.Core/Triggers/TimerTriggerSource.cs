//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TimerTriggerSource.cs
// 说 明: 定时器触发源——按固定周期自动产生触发信号。
//        适用于无 PLC 的离线检测、来料传感器常开、或固定节拍流水线场景。
//===================================================================================

using System;
using System.Threading;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.Triggers;

namespace Grayson.Vision.Core.Triggers
{
    /// <summary>
    /// 定时器触发源：按配置周期（TimerIntervalMs）自动产生触发信号。
    /// 等价于旧版 SimpleTriggerScheduler 的 Debug 连续循环，但可在生产模式下使用。
    /// </summary>
    public class TimerTriggerSource : TriggerSourceBase
    {
        public override TriggerSourceType SourceType => TriggerSourceType.Timer;

        private Timer _timer;

        public TimerTriggerSource(string stationId) : base(stationId)
        {
        }

        public override void Configure(TriggerSourceConfig config, IDevicePool devicePool = null)
        {
            SetConfig(config ?? new TriggerSourceConfig());

            // 确保 Timer 源边沿设为 None（定时器不检测边沿，每次 Tick 都是一次触发）
            if (Config.Edge == TriggerEdge.Rising || Config.Edge == TriggerEdge.Falling)
            {
                LogBus.Info("TriggerSource", $"[{StationId}] Timer 源边沿模式 {Config.Edge} 被覆盖为 None（定时器无电平跳变）");
                Config.Edge = TriggerEdge.None;
            }
        }

        public override void Start()
        {
            if (IsRunning) return;
            IsRunning = true;

            var interval = Config.TimerIntervalMs > 0 ? Config.TimerIntervalMs : 1000;
            _timer = new Timer(OnTimerTick, null, interval, interval);

            LogBus.Info("TriggerSource", $"[{StationId}] Timer 触发源已启动，周期 {interval}ms");
        }

        public override void Stop()
        {
            IsRunning = false;
            var timer = _timer;
            _timer = null;

            if (timer != null)
            {
                // .NET Framework 4.7.2 的 Timer.Dispose 无 TimeSpan 重载，
                // 先 Change(停止回调) 再 Dispose 实现等价效果
                timer.Change(Timeout.Infinite, Timeout.Infinite);
                timer.Dispose();
            }
            LogBus.Info("TriggerSource", $"[{StationId}] Timer 触发源已停止");
        }

        private void OnTimerTick(object state)
        {
            if (!IsRunning) return;

            try
            {
                // 定时器源：每次 Tick 都是一次"信号为 true"
                // 基类的边沿检测（Config.Edge=None）会让它直接走到丢帧策略
                RaiseSignal(true);
            }
            catch (Exception ex)
            {
                RaiseError("Timer.Tick", ex, recoverable: true);
            }
        }

        public override void Dispose()
        {
            Stop();
            base.Dispose();
        }
    }
}
