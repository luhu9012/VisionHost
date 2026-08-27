//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: ITriggerSource.cs
// 说 明: 触发源抽象接口——外部信号（PLC/IO/定时器/手动）的统一适配层。
//        调度器保持纯执行器不动，触发源负责"信号从哪来→信号整形→调 TriggerOnceAsync"。
//===================================================================================

using System;
using Grayson.Vision.Contracts.Devices.Services;

namespace Grayson.Vision.Contracts.Station.Triggers
{
    /// <summary>
    /// 触发源节拍统计快照（供 UI 监控页展示）。
    /// </summary>
    public class TriggerStats
    {
        /// <summary>总触发次数（含被丢弃的）</summary>
        public long TotalTriggered { get; set; }

        /// <summary>实际执行次数（排除丢弃的）</summary>
        public long TotalExecuted { get; set; }

        /// <summary>丢弃次数（执行中收到的多余信号被丢弃）</summary>
        public long DroppedCount { get; set; }

        /// <summary>最近触发时间</summary>
        public DateTime? LastTriggerTime { get; set; }

        /// <summary>最近一次执行耗时（毫秒）</summary>
        public double LastCycleMs { get; set; }

        /// <summary>平均触发间隔（毫秒，基于最近 20 次触发）</summary>
        public double AvgIntervalMs { get; set; }
    }

    /// <summary>
    /// 触发源统一抽象。
    /// <para>各实现负责监听自己的信号来源（PLC 轮询/定时器/手动按钮/TCP 等），</para>
    /// <para>信号整形（边沿检测/防抖/丢帧策略）后通过 <see cref="OnTriggered"/> 事件通知宿主。</para>
    /// <para>宿主（StationHostRuntime）订阅该事件 → 调用 <see cref="IWorkflowScheduler.TriggerOnceAsync"/>。</para>
    /// </summary>
    public interface ITriggerSource : IDisposable
    {
        /// <summary>触发源所属工位 ID</summary>
        string StationId { get; }

        /// <summary>触发源类型</summary>
        TriggerSourceType SourceType { get; }

        /// <summary>是否已启动（正在监听信号）</summary>
        bool IsRunning { get; }

        /// <summary>
        /// 配置并初始化触发源（在 Start 之前调用）。
        /// </summary>
        /// <param name="config">触发源配置</param>
        /// <param name="devicePool">全局设备池（PlcBit 源用于获取 PLC 设备实例，其他源可传 null）</param>
        void Configure(TriggerSourceConfig config, IDevicePool devicePool = null);

        /// <summary>
        /// 启动信号监听（如开始 PLC 轮询、启动定时器等）。
        /// 工位 StartAsync 时调用。
        /// </summary>
        void Start();

        /// <summary>
        /// 停止信号监听并释放轮询线程/定时器。
        /// 工位 StopAsync 时调用。
        /// </summary>
        void Stop();

        /// <summary>
        /// 手动产生一次触发信号（Manual 源的正常路径，也可用于其他源的"测试触发"按钮）。
        /// </summary>
        void ManualTrigger();

        /// <summary>
        /// 获取节拍统计快照（供 UI 展示）。
        /// </summary>
        TriggerStats GetStats();

        /// <summary>
        /// 有效触发事件（经过边沿检测+防抖+丢帧策略后，确认需要执行的一次触发）。
        /// 宿主订阅此事件 → 调用 TriggerOnceAsync。
        /// </summary>
        event EventHandler<TriggerEventArgs> OnTriggered;

        /// <summary>
        /// 触发源异常事件（PLC 断连、轮询超时等，供 UI 告警）。
        /// </summary>
        event EventHandler<TriggerErrorEventArgs> OnError;
    }

    /// <summary>
    /// 触发事件参数。
    /// </summary>
    public class TriggerEventArgs : EventArgs
    {
        /// <summary>触发来源类型</summary>
        public TriggerSourceType SourceType { get; set; }

        /// <summary>工单批次 ID（可选，用于工单追踪；为空时由调度器自动生成）</summary>
        public string BatchId { get; set; }

        /// <summary>触发时间戳</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 触发源异常事件参数。
    /// </summary>
    public class TriggerErrorEventArgs : EventArgs
    {
        /// <summary>异常来源描述</summary>
        public string Source { get; set; }

        /// <summary>异常对象</summary>
        public Exception Error { get; set; }

        /// <summary>是否可恢复（如 PLC 断连通常可恢复，定时器异常通常不可恢复）</summary>
        public bool IsRecoverable { get; set; } = true;
    }
}
