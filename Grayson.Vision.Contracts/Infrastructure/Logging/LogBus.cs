using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Infrastructure.Logging
{
    /// <summary>日志级别（由低到高：Debug → Fatal）</summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
        Fatal
    }

    /// <summary>
    /// 全局静态日志总线（门面）。
    /// 兼容性承诺：现有调用点（LogBus.Debug / Info / Warn / Error / WorkOrder）签名与行为保持兼容，零改动；
    /// 内部经 LogRouter 路由：级别过滤 → 处理器管道 → Sink 广播（订阅者隔离）→ OnLogProduced（旧 UI 订阅者）。
    /// 结构化扩展：新增 Emit(LogEntry) 与带上下文的重载，可按工位 / 工单 / 节点 / 链路记录分析维度。
    /// </summary>
    public static class LogBus
    {
        /// <summary>全局日志发布事件（兼容旧 UI 订阅者，如 FlowVm 日志列表、IpcLogSink；异常已被 LogRouter 隔离）</summary>
        public static event Action<LogEntry> OnLogProduced;

        /// <summary>
        /// 需要丢弃的日志分类（用于关闭图像大日志等，如 SuppressedCategories.Add("Image")）。
        /// 注意：运行中增删为 best-effort（不保证并发一致性），主要在启动期 / 配置期使用。
        /// </summary>
        public static HashSet<string> SuppressedCategories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 日志写盘 / 输出失败告警事件（参数：sinkName、累计失败次数、异常）。
        /// UI 订阅后可在磁盘满等异常时一次性提示；订阅者只做界面提示，切勿在此写日志（防递归）。
        /// </summary>
        public static event Action<string, int, Exception> LoggingFailed;

        /// <summary>供 LogRouter / FileLogSink / JsonLogSink 上报写失败（内部调用，异常隔离不反抛）</summary>
        internal static void RaiseLoggingFailed(string sinkName, int failures, Exception ex)
        {
            try { LoggingFailed?.Invoke(sinkName, failures, ex); }
            catch { /* 告警订阅者异常直接忽略 */ }
        }

        /// <summary>
        /// 供 LogRouter 触发旧 UI 订阅者事件（C# 事件只能在声明类内部 invoke，故提供内部转发方法）。
        /// 订阅者异常已被隔离，不影响日志主链路。
        /// </summary>
        internal static void RaiseOnLogProduced(LogEntry entry)
        {
            try { OnLogProduced?.Invoke(entry); }
            catch { /* UI 订阅者异常不影响链路 */ }
        }

        /// <summary>核心日志投递入口（构造条目 → LogRouter 路由）</summary>
        public static void Emit(LogLevel level, string category, string message, Exception exception = null)
        {
            LogRouter.Publish(new LogEntry
            {
                Level = level,
                Category = category ?? "General",
                Message = message,
                Exception = exception
            });
        }

        /// <summary>
        /// 带上下文维度的完整入口（推荐用于工位 / 工单 / 节点相关日志，为结构化分析提供维度）。
        /// 例：LogBus.Emit(LogLevel.Error, "Station", "抓拍超时", ex, stationId: "S01", workOrderId: "WO001", nodeId: "Grab");
        /// </summary>
        public static void Emit(LogLevel level, string category, string message, Exception exception,
            string stationId = null, string workOrderId = null, string nodeId = null,
            string traceId = null, string jsonData = null, string userName = null)
        {
            LogRouter.Publish(new LogEntry
            {
                Level = level,
                Category = category ?? "General",
                Message = message,
                Exception = exception,
                StationId = stationId,
                WorkOrderId = workOrderId,
                NodeId = nodeId,
                TraceId = traceId,
                JsonData = jsonData,
                UserName = userName
            });
        }

        /// <summary>直接发布自定义条目（最灵活：可任意组合上下文与附加结构化数据）</summary>
        public static void Emit(LogEntry entry)
        {
            if (entry == null) return;
            if (string.IsNullOrEmpty(entry.Category)) entry.Category = "General";
            LogRouter.Publish(entry);
        }

        /// <summary>兼容旧入口：WorkOrderLogEntry → 统一 LogEntry（上下文字段自动映射）</summary>
        public static void Emit(WorkOrderLogEntry entry)
        {
            if (entry == null) return;
            LogRouter.Publish(new LogEntry
            {
                Timestamp = entry.Timestamp,
                Level = entry.Level,
                Category = entry.Category ?? "WorkOrder",
                Message = entry.Message,
                Exception = entry.Exception,
                StationId = entry.StationId,
                WorkOrderId = entry.WorkOrderId,
                NodeId = entry.NodeId
            });
        }

        // ==================== 便捷方法（签名保持兼容，现有调用点零改动） ====================

        public static void Debug(string category, string message) => Emit(LogLevel.Debug, category, message);
        public static void Info(string category, string message) => Emit(LogLevel.Info, category, message);
        public static void Warn(string category, string message) => Emit(LogLevel.Warn, category, message);
        public static void Error(string category, string message, Exception ex = null) => Emit(LogLevel.Error, category, message, ex);

        /// <summary>记录工单级日志（自动进入统一结构化模型，含工位 / 工单 / 节点维度）</summary>
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
