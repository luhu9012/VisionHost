
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Infrastructure.Logging
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
        Fatal
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public LogLevel Level { get; set; }
        public string Category { get; set; } // 例如: "Engine", "Halcon", "IO", "System"
        public string Message { get; set; }
        public Exception Exception { get; set; }

        public override string ToString()
        {
            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] [{Category}] {Message}" +
                   (Exception != null ? $"\nException: {Exception}" : "");
        }
    }

    public static class LogBus
    {
        /// <summary>
        /// 全局日志发布事件
        /// </summary>
        public static event Action<LogEntry> OnLogProduced;

        /// <summary>
        /// 需要丢弃的日志分类（用于关闭图像大日志等）。
        /// 注意：线程安全只是 best-effort，主要用于运行时过滤。
        /// </summary>
        public static HashSet<string> SuppressedCategories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 核心日志投递入口
        /// </summary>
        public static void Emit(LogLevel level, string category, string message, Exception exception = null)
        {
            if (SuppressedCategories.Contains(category ?? string.Empty))
                return;

            var entry = new LogEntry
            {
                Level = level,
                Category = category ?? "General",
                Message = message,
                Exception = exception
            };
            // 1. 在 VS 开发调试模式下，默认直接输出到 Visual Studio 的 Output 窗口
            #if DEBUG
            System.Diagnostics.Debug.WriteLine(entry.ToString());
            #endif

            // 2. 触发事件，给 FileLogSink 或 UI (FlowVm) 订阅
            OnLogProduced?.Invoke(entry);
        }

        public static void Emit(WorkOrderLogEntry entry)
        {
            if (entry == null) return;
            if (SuppressedCategories.Contains(entry.Category ?? string.Empty))
                return;

            #if DEBUG
            System.Diagnostics.Debug.WriteLine(entry.ToString());
            #endif

            OnLogProduced?.Invoke(new LogEntry
            {
                Timestamp = entry.Timestamp,
                Level = entry.Level,
                Category = $"WO:{entry.Category}",
                Message = $"[Station:{entry.StationId ?? "-"}] [WO:{entry.WorkOrderId ?? "-"}] [Node:{entry.NodeId ?? "-"}] {entry.Message}",
                Exception = entry.Exception
            });
        }

        // 便捷扩展方法
        public static void Debug(string category, string message) => Emit(LogLevel.Debug, category, message);
        public static void Info(string category, string message) => Emit(LogLevel.Info, category, message);
        public static void Warn(string category, string message) => Emit(LogLevel.Warn, category, message);
        public static void Error(string category, string message, Exception ex = null) => Emit(LogLevel.Error, category, message, ex);

        /// <summary>
        /// 记录工单级日志
        /// </summary>
        public static void WorkOrder(LogLevel level, string category, string stationId, string workOrderId,
            string message, string nodeId = null, Exception ex = null)
        {
            Emit(new WorkOrderLogEntry
            {
                Level = level,
                Category = category,
                StationId = stationId,
                WorkOrderId = workOrderId,
                NodeId = nodeId,
                Message = message,
                Exception = ex
            });
        }
    }
}
