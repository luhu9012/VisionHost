using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.WorkOrderTracking;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Grayson.Vision.Core.Station
{
    /// <summary>
    /// 工单追踪器：保存最近 N 个工单，供 UI/MES/质量回溯查询。
    /// </summary>
    public class WorkOrderTracker : IWorkOrderTracker
    {
        private readonly ConcurrentDictionary<string, WorkOrder> _workOrders
            = new ConcurrentDictionary<string, WorkOrder>();

        /// <summary>最大保留工单数</summary>
        public int MaxHistory { get; set; } = 1000;

        public void Track(WorkOrder workOrder)
        {
            if (workOrder == null) return;
            _workOrders[workOrder.WorkOrderId] = workOrder;
            TrimIfNeeded();
        }

        public WorkOrder GetWorkOrder(string workOrderId)
        {
            if (string.IsNullOrEmpty(workOrderId)) return null;
            _workOrders.TryGetValue(workOrderId, out var wo);
            return wo;
        }

        public IReadOnlyList<WorkOrder> GetRecentWorkOrders(int count = 50)
        {
            return _workOrders.Values
                .OrderByDescending(x => x.CreatedAt)
                .Take(count)
                .ToList();
        }

        IReadOnlyList<WorkOrderSnapshot> IWorkOrderTracker.GetRecentWorkOrders(int count)
        {
            return GetRecentWorkOrders(count)
                .Select(MapToSnapshot)
                .ToList();
        }

        private static WorkOrderSnapshot MapToSnapshot(WorkOrder wo)
        {
            if (wo == null) return null;
            return new WorkOrderSnapshot
            {
                WorkOrderId = wo.WorkOrderId,
                StationId = wo.StationId,
                BatchId = wo.BatchId,
                RecipeName = wo.RecipeTraceability?.RecipeName ?? "—",
                StatusText = wo.Status.ToString(),
                IsOk = wo.ResultData?.IsOk,
                CycleTimeMs = wo.ResultData?.CycleTimeMs ?? wo.Elapsed.TotalMilliseconds,
                ImagePath = wo.ResultData?.ImagePaths?.FirstOrDefault(),
                ImagePaths = wo.ResultData?.ImagePaths?.ToList(),
                ErrorMessage = !string.IsNullOrWhiteSpace(wo.ResultData?.ErrorMessage)
                    ? wo.ResultData.ErrorMessage
                    : wo.Exception?.Message,
                CreatedAt = wo.CreatedAt.ToLocalTime()
            };
        }

        public IReadOnlyList<WorkOrder> GetWorkOrdersByStatus(params WorkOrderStatus[] statuses)
        {
            if (statuses == null || statuses.Length == 0)
                return _workOrders.Values.ToList();

            return _workOrders.Values
                .Where(x => statuses.Contains(x.Status))
                .OrderByDescending(x => x.CreatedAt)
                .ToList();
        }

        private void TrimIfNeeded()
        {
            if (_workOrders.Count <= MaxHistory) return;

            var oldest = _workOrders.Values
                .OrderBy(x => x.CreatedAt)
                .Take(_workOrders.Count - MaxHistory)
                .Select(x => x.WorkOrderId)
                .ToList();

            foreach (var id in oldest)
            {
                if (id == null) continue;
                _workOrders.TryRemove(id, out _);
            }
        }
    }
}
