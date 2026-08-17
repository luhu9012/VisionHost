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

        public double OkRate => _totalWorkOrders == 0 ? 0 : (double)_okCount / _totalWorkOrders;

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
