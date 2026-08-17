using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Recipe.Traceability;
using Grayson.Vision.Contracts.Station.Models;
using System;

namespace Grayson.Vision.Core.Flow
{
    /// <summary>
    /// 工单级执行上下文：
    /// - 封装单个 WorkOrder 的生命周期与快照
    /// - 内部持有 EngineContext 用于端口数据传递与节点事件
    /// - 节点写操作只影响 EngineContext；写工位全局参数需单独申请
    /// </summary>
    public class WorkOrderExecutionContext : IDisposable
    {
        /// <summary>当前工单</summary>
        public WorkOrder WorkOrder { get; }

        /// <summary>本次工单内的端口/变量管线（以前的 ExecutionContext 实例）</summary>
        public ExecutionContext EngineContext { get; }

        /// <summary>单次触发节拍上下文</summary>
        public FrameCycleContext CycleContext { get; }

        /// <summary>是否保存每个节点的输入输出快照</summary>
        public bool EnableNodeSnapshots { get; set; } = true;

        /// <summary>
        /// 绑定配方追溯快照；可在工单开始时指定，用于后续审计。
        /// </summary>
        public RecipeTraceabilityEntry RecipeTraceability { get; set; }

        public WorkOrderExecutionContext(WorkOrder workOrder, ExecutionContext engineContext = null)
        {
            WorkOrder = workOrder ?? throw new ArgumentNullException(nameof(workOrder));
            EngineContext = engineContext ?? new ExecutionContext();
            CycleContext = new FrameCycleContext { BatchId = workOrder.BatchId };

            WireEngineEvents();
        }

        /// <summary>
        /// 将配方追溯快照写入当前工单的 WorkOrder，并触发一次快照记录。
        /// </summary>
        public void BindRecipeTraceability(RecipeTraceabilityEntry traceability)
        {
            WorkOrder.RecipeTraceability = traceability;
        }

        private void WireEngineEvents()
        {
            EngineContext.OnNodeExecuting += (s, node) =>
            {
                WorkOrder.MarkRunning();
                if (node == null) return;

                if (EnableNodeSnapshots)
                {
                    var snapshot = WorkOrder.AddOrUpdateNodeSnapshot(node.NodeId, node.DisplayName);
                    snapshot.StartedAt = DateTime.UtcNow;
                }
            };

            EngineContext.OnNodeExecuted += (s, node) =>
            {
                if (node == null) return;

                if (EnableNodeSnapshots)
                {
                    var snapshot = WorkOrder.AddOrUpdateNodeSnapshot(node.NodeId, node.DisplayName);
                    snapshot.CompletedAt = DateTime.UtcNow;
                }
            };

            EngineContext.OnExecutionError += (s, e) =>
            {
                if (e?.Node != null)
                {
                    var snapshot = WorkOrder.AddOrUpdateNodeSnapshot(e.Node.NodeId, e.Node.DisplayName);
                    snapshot.IsFailed = true;
                    snapshot.ErrorMessage = e.Exception?.Message;
                }
                WorkOrder.MarkAborted(WorkOrderStatus.Abort_Error,
                    ex: e?.Exception,
                    faultCode: -1001);
            };
        }

        public NodeExecutionContext CreateNodeContext(Func<string, object> hardwareResolver)
        {
            return new NodeExecutionContext(EngineContext, CycleContext, hardwareResolver);
        }

        public void Dispose()
        {
            // EngineContext 是共享传入或新创建的，不需要在这里 Dispose
        }
    }
}
