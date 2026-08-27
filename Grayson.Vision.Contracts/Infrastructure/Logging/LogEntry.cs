using System;

namespace Grayson.Vision.Contracts.Infrastructure.Logging
{
    /// <summary>
    /// 统一日志条目模型（结构化）。
    /// 设计要点：
    /// 1. 上下文维度：StationId / WorkOrderId / NodeId / TraceId 让日志可按「工位 / 工单 / 节点 / 链路」检索与聚合，
    ///    为清洗、AI 分析提供机器可读的结构化基础（不引入业务模块枚举，分类仍用字符串 Category，兼容旧调用点）；
    /// 2. 时间戳统一使用本地时间 DateTime.Now（旧 WorkOrderLogEntry 用 UtcNow 会造成 8 小时偏差，已统一）；
    /// 3. 所有字段均可选：不填不影响现有调用，填了即为分析维度。
    /// </summary>
    public class LogEntry
    {
        /// <summary>发生时间（本地时间，统一格式）</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>级别：Debug / Info / Warn / Error / Fatal</summary>
        public LogLevel Level { get; set; }

        /// <summary>分类（自由字符串，如 "System" / "Station" / "Halcon" / "IO"，兼容旧调用点）</summary>
        public string Category { get; set; }

        /// <summary>日志正文</summary>
        public string Message { get; set; }

        /// <summary>异常对象（可选）</summary>
        public Exception Exception { get; set; }

        // ==================== 上下文维度（可选，结构化检索与分析的关键） ====================

        /// <summary>工位 ID（如 "S01"）</summary>
        public string StationId { get; set; }

        /// <summary>工单 ID</summary>
        public string WorkOrderId { get; set; }

        /// <summary>流程节点 ID（节点执行失败时定位到具体算子）</summary>
        public string NodeId { get; set; }

        /// <summary>链路 ID：一次工单处理 / 一次操作贯穿的关联键，可串起该次全链路日志
        /// （TODO：后续可在 WorkOrder 创建处自动生成并传递，当前由调用方按需填写）</summary>
        public string TraceId { get; set; }

        /// <summary>线程 ID（自动补全，用于并发问题排查）</summary>
        public string ThreadId { get; set; }

        /// <summary>操作人（权限 / 配方审批等管理类操作场景）</summary>
        public string UserName { get; set; }

        /// <summary>附加结构化数据（检测指标 / 耗时 / 坐标等 JSON 文本，供分析直接消费）</summary>
        public string JsonData { get; set; }

        /// <summary>
        /// 文本渲染（FileLogSink 使用；上下文非空时附加 [Station:xx][WO:xx][Node:xx][Trace:xx] 便于人读排查）
        /// </summary>
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder(128);
            sb.Append('[').Append(Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] [")
              .Append(Level).Append("] [").Append(Category ?? "General").Append(']');
            if (!string.IsNullOrEmpty(StationId)) sb.Append(" [Station:").Append(StationId).Append(']');
            if (!string.IsNullOrEmpty(WorkOrderId)) sb.Append(" [WO:").Append(WorkOrderId).Append(']');
            if (!string.IsNullOrEmpty(NodeId)) sb.Append(" [Node:").Append(NodeId).Append(']');
            if (!string.IsNullOrEmpty(TraceId)) sb.Append(" [Trace:").Append(TraceId).Append(']');
            sb.Append(' ').Append(Message);
            if (Exception != null) sb.Append("\nException: ").Append(Exception);
            return sb.ToString();
        }
    }
}
