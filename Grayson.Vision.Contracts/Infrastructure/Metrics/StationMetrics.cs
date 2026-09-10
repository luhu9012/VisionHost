using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Infrastructure.Metrics
{
    /// <summary>
    /// 工位运行指标聚合。每个工位持有一个实例，周期输出给 UI/OPC UA/MES。
    /// </summary>
    public class StationMetrics
    {
        public string StationId { get; }

        public long TotalWorkOrders => _totalWorkOrders;
        public long OkCount => _okCount;
        public long NgCount => _ngCount;
        public long TimeoutCount => _timeoutCount;
        public long DeviceReconnectCount => _deviceReconnectCount;

        /// <summary>
        /// 已定案的完整周期数(业务过程=整个 RunProcessOnceAsync 结束；纯视觉链=整链结束)。
        /// 由调用方在「最终判定点」显式 RecordCycleFinalized 递增——与通用计数 RecordWorkOrderCompleted
        /// 解耦：业务过程内部的视觉段完成也会走通用计数，但不算定案，避免 UI 在机械段结束前误报最终结果。
        /// UI 轮询消费它以可靠刷新 RESULT，规避依赖 IsProcessBusy 边沿漏采导致卡『进行中』。
        /// </summary>
        public long FinalizedWorkOrders => _finalizedWorkOrders;

        public double OkRate => _totalWorkOrders == 0 ? 0 : (double)_okCount / _totalWorkOrders;

        /// <summary>最近一次业务周期耗时(毫秒)；-1 = 尚无记录。</summary>
        public long LastCycleTimeMs => _lastCycleTimeMs;

        /// <summary>最近一次业务周期结果：0=未知，1=OK，-1=NG。</summary>
        public int LastWorkOrderResult => _lastWorkOrderResult;

        /// <summary>节点 ID -> 平均耗时(毫秒)</summary>
        public IReadOnlyDictionary<string, double> AvgNodeElapsedMs => _avgNodeElapsedMs;

        /// <summary>节点 ID -> 最大耗时(毫秒)</summary>
        public IReadOnlyDictionary<string, double> MaxNodeElapsedMs => _maxNodeElapsedMs;

        /// <summary>队列长度（流水线调度器使用）</summary>
        public int QueueLength => _queueLength;

        private long _totalWorkOrders;
        private long _okCount;
        private long _ngCount;
        private long _timeoutCount;
        private long _deviceReconnectCount;
        private long _finalizedWorkOrders;
        private long _lastCycleTimeMs = -1;
        private int _lastWorkOrderResult;
        private int _queueLength;

        private readonly ConcurrentDictionary<string, AvgAccumulator> _nodeAccumulators
            = new ConcurrentDictionary<string, AvgAccumulator>();

        private readonly ConcurrentDictionary<string, double> _avgNodeElapsedMs
            = new ConcurrentDictionary<string, double>();

        private readonly ConcurrentDictionary<string, double> _maxNodeElapsedMs
            = new ConcurrentDictionary<string, double>();

        public StationMetrics(string stationId)
        {
            StationId = stationId;
        }

        public void RecordWorkOrderCompleted(bool isOk)
        {
            System.Threading.Interlocked.Increment(ref _totalWorkOrders);
            if (isOk) System.Threading.Interlocked.Increment(ref _okCount);
            else System.Threading.Interlocked.Increment(ref _ngCount);
            System.Threading.Interlocked.Exchange(ref _lastWorkOrderResult, isOk ? 1 : -1);
        }

        /// <summary>记录一次业务周期结果与耗时（含 RecordWorkOrderCompleted 语义）。</summary>
        public void RecordWorkOrderCompleted(bool isOk, long cycleTimeMs)
        {
            System.Threading.Interlocked.Increment(ref _totalWorkOrders);
            if (isOk) System.Threading.Interlocked.Increment(ref _okCount);
            else System.Threading.Interlocked.Increment(ref _ngCount);
            System.Threading.Interlocked.Exchange(ref _lastWorkOrderResult, isOk ? 1 : -1);
            System.Threading.Interlocked.Exchange(ref _lastCycleTimeMs, cycleTimeMs);
        }

        /// <summary>在「完整周期最终判定」时递增定案计数。用于 UI 可靠消费最终 RESULT。</summary>
        public void RecordCycleFinalized()
        {
            System.Threading.Interlocked.Increment(ref _finalizedWorkOrders);
        }

        public void RecordTimeout()
        {
            System.Threading.Interlocked.Increment(ref _timeoutCount);
        }

        public void RecordDeviceReconnect()
        {
            System.Threading.Interlocked.Increment(ref _deviceReconnectCount);
        }

        public void RecordNodeElapsed(string nodeId, TimeSpan elapsed)
        {
            if (string.IsNullOrEmpty(nodeId)) return;

            var acc = _nodeAccumulators.AddOrUpdate(nodeId,
                _ => new AvgAccumulator { Count = 1, TotalMs = elapsed.TotalMilliseconds },
                (_, existing) =>
                {
                    existing.Count++;
                    existing.TotalMs += elapsed.TotalMilliseconds;
                    return existing;
                });

            _avgNodeElapsedMs[nodeId] = acc.TotalMs / acc.Count;

            _maxNodeElapsedMs.AddOrUpdate(nodeId, elapsed.TotalMilliseconds,
                (_, currentMax) => Math.Max(currentMax, elapsed.TotalMilliseconds));
        }

        public void SetQueueLength(int length)
        {
            _queueLength = Math.Max(0, length);
        }

        public StationMetricsSnapshot Snapshot()
        {
            var avgDict = new Dictionary<string, double>();
            foreach (var kv in AvgNodeElapsedMs) avgDict[kv.Key] = kv.Value;

            var maxDict = new Dictionary<string, double>();
            foreach (var kv in MaxNodeElapsedMs) maxDict[kv.Key] = kv.Value;

            return new StationMetricsSnapshot
            {
                StationId = StationId,
                TotalWorkOrders = TotalWorkOrders,
                OkCount = OkCount,
                NgCount = NgCount,
                TimeoutCount = TimeoutCount,
                DeviceReconnectCount = DeviceReconnectCount,
                OkRate = OkRate,
                AvgNodeElapsedMs = avgDict,
                MaxNodeElapsedMs = maxDict,
                QueueLength = QueueLength
            };
        }

        private class AvgAccumulator
        {
            public int Count;
            public double TotalMs;
        }
    }

    /// <summary>
    /// 指标快照，适合跨 UI/网络传输。
    /// </summary>
    public class StationMetricsSnapshot
    {
        public string StationId { get; set; }
        public long TotalWorkOrders { get; set; }
        public long OkCount { get; set; }
        public long NgCount { get; set; }
        public long TimeoutCount { get; set; }
        public long DeviceReconnectCount { get; set; }
        public double OkRate { get; set; }
        public Dictionary<string, double> AvgNodeElapsedMs { get; set; }
        public Dictionary<string, double> MaxNodeElapsedMs { get; set; }
        public int QueueLength { get; set; }
    }
}
