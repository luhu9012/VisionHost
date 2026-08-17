using System;

namespace Grayson.Vision.Contracts.Infrastructure.Alarm
{
    /// <summary>
    /// 告警分级
    /// </summary>
    public enum AlarmSeverity
    {
        Information = 0,
        Warning = 1,
        Fault = 2
    }

    /// <summary>
    /// 告警条目
    /// </summary>
    public class AlarmItem
    {
        public string AlarmId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public AlarmSeverity Severity { get; set; }
        public int FaultCode { get; set; }
        public string StationId { get; set; }
        public string Source { get; set; }
        public string Message { get; set; }
        public string Suggestion { get; set; }
    }

    /// <summary>
    /// 告警输出目标
    /// </summary>
    public interface IAlarmSink
    {
        void Raise(AlarmItem alarm);
    }
}
