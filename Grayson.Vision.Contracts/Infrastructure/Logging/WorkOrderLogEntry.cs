using System;

namespace Grayson.Vision.Contracts.Infrastructure.Logging
{
    /// <summary>
    /// 绑定到工单的日志条目，支持按工位 ID、工单 ID、节点 ID 索引。
    /// </summary>
    public class WorkOrderLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public LogLevel Level { get; set; }

        /// <summary>固定分类：Device / Scheduler / WorkOrder / Node / System</summary>
        public string Category { get; set; }

        /// <summary>工位 ID</summary>
        public string StationId { get; set; }

        /// <summary>工单 ID</summary>
        public string WorkOrderId { get; set; }

        /// <summary>节点 ID（可选）</summary>
        public string NodeId { get; set; }

        /// <summary>消息</summary>
        public string Message { get; set; }

        /// <summary>异常</summary>
        public Exception Exception { get; set; }

        public override string ToString()
        {
            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] [{Category}] " +
                   $"[Station:{StationId ?? "-"}] [WO:{WorkOrderId ?? "-"}] [Node:{NodeId ?? "-"}] {Message}" +
                   (Exception != null ? $"\nException: {Exception}" : "");
        }
    }
}
