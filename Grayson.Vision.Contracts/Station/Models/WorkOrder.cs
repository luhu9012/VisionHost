using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 一次独立工单（一次触发/一帧/一个物料）的强类型上下文与生命周期的追踪对象。
    /// </summary>
    public class WorkOrder
    {
        /// <summary>工单唯一标识</summary>
        public string WorkOrderId { get; }

        /// <summary>所属工位 ID</summary>
        public string StationId { get; }

        /// <summary>批次/物料号</summary>
        public string BatchId { get; set; }

        /// <summary>触发来源描述，例如 "PLC_TRIGGER", "MANUAL", "SCHEDULER"</summary>
        public string TriggerSource { get; set; }

        /// <summary>当前状态</summary>
        public WorkOrderStatus Status { get; private set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; }

        /// <summary>开始执行时间</summary>
        public DateTime? StartedAt { get; private set; }

        /// <summary>结束时间</summary>
        public DateTime? CompletedAt { get; private set; }

        /// <summary>异常或终止原因</summary>
        public Exception Exception { get; private set; }

        /// <summary>故障码</summary>
        public int FaultCode { get; private set; }

        /// <summary>节点执行快照（节点 ID -> 快照），可配置是否保存输入输出</summary>
        public Dictionary<string, WorkOrderNodeSnapshot> NodeSnapshots { get; }
            = new Dictionary<string, WorkOrderNodeSnapshot>();

        /// <summary>
        /// 工单执行时绑定的配方追溯信息（哪个版本、谁批准的）。
        /// 用于行业追溯要求，如汽车 IATF、医疗 GxP。
        /// </summary>
        public Recipe.Traceability.RecipeTraceabilityEntry RecipeTraceability { get; set; }

        /// <summary>
        /// 工单执行结果—— 新的统一模型
        /// 
        /// 用途:
        /// - 存储 OK/NG 判定结果
        /// - 存储测量数据和图像
        /// - 存储时间分解信息
        /// - UI 显示和数据库持久化
        /// 
        /// 迁移说明:
        /// 原来的 ResultData + QualityData 合并到此模型
        /// 通过 Result.Measurements.ConfidenceScore 等字段支持旧数据
        /// </summary>
        public WorkOrderResult Result { get; set; }

        /// <summary>
        /// [已过时] 工单执行的结果数据 
        /// 
        /// 保留此字段用于向后兼容，新代码应使用 Result 字段
        /// 此字段可在迁移期间作为中间转换层
        /// </summary>
        [Obsolete("使用 Result 字段代替。此字段保留用于向后兼容。")]
        public WorkOrderResultData ResultData { get; set; }

        /// <summary>
        /// [已过时] 工单的质量统计数据
        /// 
        /// 保留此字段用于向后兼容，新代码应使用 Result 字段
        /// 质量数据现在包含在 WorkOrderResult.Measurements 中
        /// </summary>
        [Obsolete("使用 Result 字段代替。此字段保留用于向后兼容。")]
        public WorkOrderQualityData QualityData { get; set; }

        private readonly Stopwatch _stopwatch = new Stopwatch();

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public WorkOrder(string workOrderId, string stationId, string batchId = null, string triggerSource = null)
        {
            WorkOrderId = workOrderId ?? Guid.NewGuid().ToString("N");
            StationId = stationId;
            BatchId = batchId ?? WorkOrderId;
            TriggerSource = triggerSource ?? "Unknown";
            CreatedAt = DateTime.UtcNow;
            Status = WorkOrderStatus.Created;
        }

        public void MarkRunning()
        {
            if (Status != WorkOrderStatus.Created) return;
            Status = WorkOrderStatus.Running;
            StartedAt = DateTime.UtcNow;
            _stopwatch.Restart();
        }

        public void MarkCompleted(bool isOk = true)
        {
            _stopwatch.Stop();
            Status = isOk ? WorkOrderStatus.Completed_OK : WorkOrderStatus.Completed_NG;
            CompletedAt = DateTime.UtcNow;
        }

        public void MarkAborted(WorkOrderStatus abortStatus, string reason = null, Exception ex = null, int faultCode = -1)
        {
            _stopwatch.Stop();
            Status = abortStatus;
            CompletedAt = DateTime.UtcNow;
            FaultCode = faultCode;
            Exception = ex;
            if (!string.IsNullOrEmpty(reason))
            {
                // 将 reason 作为内部信息保留，不暴露新属性以保持兼容性；可后续扩展。
            }
        }

        public WorkOrderNodeSnapshot AddOrUpdateNodeSnapshot(string nodeId, string nodeName)
        {
            if (!NodeSnapshots.TryGetValue(nodeId, out var snapshot))
            {
                snapshot = new WorkOrderNodeSnapshot(nodeId, nodeName);
                NodeSnapshots[nodeId] = snapshot;
            }
            return snapshot;
        }
    }

    /// <summary>
    /// 单个节点在工单中的一次执行快照
    /// </summary>
    public class WorkOrderNodeSnapshot
    {
        public string NodeId { get; }
        public string NodeName { get; }
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public TimeSpan Elapsed => CompletedAt - StartedAt;
        public Dictionary<string, object> Inputs { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Outputs { get; set; } = new Dictionary<string, object>();
        public bool IsSkipped { get; set; }
        public bool IsFailed { get; set; }
        public string ErrorMessage { get; set; }

        public WorkOrderNodeSnapshot(string nodeId, string nodeName)
        {
            NodeId = nodeId;
            NodeName = nodeName;
        }
    }
}
