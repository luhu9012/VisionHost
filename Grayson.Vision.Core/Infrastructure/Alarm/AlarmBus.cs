using Grayson.Vision.Contracts.Infrastructure.Alarm;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Core.Infrastructure.Alarm
{
    /// <summary>
    /// 告警总线默认实现：收集告警并分发到多个 Sink（文件、UI、IO、OPC UA 等）。
    /// </summary>
    public class AlarmBus
    {
        private readonly List<IAlarmSink> _sinks = new List<IAlarmSink>();

        public static readonly AlarmBus Instance = new AlarmBus();

        public void RegisterSink(IAlarmSink sink)
        {
            if (sink == null) return;
            lock (_sinks) { if (!_sinks.Contains(sink)) _sinks.Add(sink); }
        }

        public void UnregisterSink(IAlarmSink sink)
        {
            if (sink == null) return;
            lock (_sinks) { _sinks.Remove(sink); }
        }

        public void Raise(AlarmSeverity severity, int faultCode, string stationId, string source, string message, string suggestion = null)
        {
            var alarm = new AlarmItem
            {
                Severity = severity,
                FaultCode = faultCode,
                StationId = stationId,
                Source = source,
                Message = message,
                Suggestion = suggestion
            };

            LogBus.Warn("Alarm" , $"[{stationId}] [{severity}] {source}: {message}");

            IAlarmSink[] snapshot;
            lock (_sinks) { snapshot = _sinks.ToArray(); }
            foreach (var sink in snapshot)
            {
                try
                {
                    sink.Raise(alarm);
                }
                catch (Exception ex)
                {
                    LogBus.Error("AlarmBus", $"告警 Sink 分发失败: {ex.Message}", ex);
                }
            }
        }
    }
}
