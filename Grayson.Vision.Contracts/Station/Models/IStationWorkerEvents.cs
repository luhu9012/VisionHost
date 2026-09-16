using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Flow.Nodes;
using System;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 节点基础执行事件参数
    /// </summary>
    public class NodeEventArgs : EventArgs
    {
        public string StationId { get; set; }
        public FlowNodeBase Node { get; set; }

        public NodeEventArgs(string stationId, FlowNodeBase node)
        {
            StationId = stationId;
            Node = node;
        }
    }

    // 执行链事件枚举
    public enum ChainExecutionResult
    {
        Success,       // 顺利完成到最后一个节点
        Failed,        // 节点运行报错中止
        Canceled,      // 手动 Stop 取消
        StepEndReached // 单步到达链末尾
    }
    public class ChainCompletedEventArgs : EventArgs
    {
        public ChainExecutionResult Result { get; }
        public Exception Exception { get; }
        /// <summary>
        /// 执行链总耗时（毫秒）
        /// </summary>
        public double ExecutionTimeMs { get; }

        /// <summary>
        /// 是否"分段执行"完成（复合工位业务过程把一条链拆成多段分别跑）。
        /// 分段完成只更新 LastChainResult 供失败分流，不触发状态机回退/工单最终落库
        /// （那应由整条业务周期 RunProcessOnceAsync 统一负责）。
        /// </summary>
        public bool IsSegment { get; }

        public ChainCompletedEventArgs(ChainExecutionResult result, Exception exception = null, double executionTimeMs = 0, bool isSegment = false)
        {
            Result = result;
            Exception = exception;
            ExecutionTimeMs = executionTimeMs;
            IsSegment = isSegment;
        }
    }

    /// <summary>
    /// 统一汇总的工位事件暴露接口
    /// </summary>
    public interface IStationWorkerEvents
    {
        event EventHandler<StationState> OnStateChanged;
        event EventHandler<ImageRenderEventArgs> OnFrameRendered;
        event EventHandler<NodeEventArgs> OnNodeExecuting;
        event EventHandler<NodeEventArgs> OnNodeExecuted;
        event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError;
        event EventHandler<ChainCompletedEventArgs> OnExecutionCompleted;
        event EventHandler<string> OnLogReceived;
    }
}