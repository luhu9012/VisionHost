using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Station.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Station.Interfaces
{
    /// <summary>
    /// 工作流调度器契约：负责把工位触发信号转换为对执行链的单次/连续/单步驱动。
    /// StationWorker 只关注生命周期状态机，调度策略由 IWorkflowScheduler 实现决定。
    /// </summary>
    public interface IWorkflowScheduler : IDisposable
    {
        /// <summary>所属工位 ID</summary>
        string StationId { get; }

        /// <summary>当前调度模式</summary>
        WorkMode Mode { get; set; }

        /// <summary>当前是否处于运行中</summary>
        bool IsRunning { get; }

        /// <summary>
        /// 加载配方/执行链；由 StationWorker 触发。
        /// </summary>
        Task LoadExecutionChainAsync(ExecutionChain chain);

        /// <summary>
        /// 启动调度（例如开始监听 PLC 触发或进入连续运行）。
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 停止调度。
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// 手动触发一次执行（单帧/单物料）。
        /// </summary>
        Task TriggerOnceAsync(string batchId = null);

        /// <summary>
        /// 单步执行指定节点（调试用）。
        /// </summary>
        Task StepNodeAsync(FlowNodeBase node);

        /// <summary>
        /// 暂停调度（保持当前状态，不处理新触发）。
        /// </summary>
        Task PauseAsync();

        /// <summary>
        /// 从暂停状态恢复。
        /// </summary>
        Task ResumeAsync();

        /// <summary>
        /// 执行链完成事件
        /// </summary>
        event EventHandler<ChainCompletedEventArgs> OnExecutionCompleted;

        /// <summary>
        /// 节点开始/结束执行事件
        /// </summary>
        event EventHandler<NodeEventArgs> OnNodeExecuting;
        event EventHandler<NodeEventArgs> OnNodeExecuted;

        /// <summary>
        /// 执行错误事件
        /// </summary>
        event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError;
    }

    // 注意：WorkMode 统一复用 Grayson.Vision.Contracts.Station.Enums.WorkMode
    // 这里不再重复定义，以避免命名冲突。
}
