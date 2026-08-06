using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Flow.Nodes;
using System;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Station.Interfaces
{
    /// <summary>
    /// Worker 客户端客户端统一接口
    /// 无论底层是本地嵌入式 Worker 还是 IPC 远程 Worker 进程，UI 统一调用此契约
    /// </summary>
    public interface IWorkerClient : IDisposable
    {
        string StationId { get; }
        bool IsConnected { get; }

        /// <summary>
        /// 客户端代理当前记录的工位状态
        /// </summary>
        StationState CurrentState { get; }

        Task<bool> ConnectAsync();
        Task LoadRecipeAsync(FlowProcessModel recipe);
        Task StartAsync();
        Task StopAsync();
        Task TriggerOnceAsync(string batchId = null);
        Task StepNodeAsync(FlowNodeBase node);

        // 状态与渲染事件
        event EventHandler<StationState> OnStateChanged;
        event EventHandler<ImageRenderEventArgs> OnFrameRendered;
        event EventHandler<string> OnLogReceived;
        event EventHandler<ChainCompletedEventArgs> OnExecutionCompleted;

        // 🌟 统一为强类型事件
        event EventHandler<NodeEventArgs> OnNodeExecuting;
        event EventHandler<NodeEventArgs> OnNodeExecuted;
        event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError;
    }
}