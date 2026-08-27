using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.WorkOrderTracking;
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
        Task PauseAsync();
        Task ResumeAsync();
        Task TriggerOnceAsync(string batchId = null);
        Task StepNodeAsync(FlowNodeBase node);

        /// <summary>
        /// 注入节点实时预览显示上下文（编辑器属性面板调试专用）。
        /// null 表示清除注入；仅本地嵌入式代理支持，IPC 远端代理为 no-op
        /// （远端进程无法持有本地窗口句柄，静默退化为主视图显示）。
        /// </summary>
        void SetPreviewContext(IFlowPreviewContext preview);
        Task EmergencyStopAsync(string reason = null);

        /// <summary>
        /// 工单级复位：终止当前工单并释放本次占用设备，不改变工位全局状态。
        /// </summary>
        Task WorkOrderResetAsync();

        /// <summary>
        /// 工位软复位：停止运行、执行 ResetBlueprint（回安全点、IO 复位、清空队列），不重新初始化硬件句柄。
        /// </summary>
        Task SoftResetAsync();

        /// <summary>
        /// 硬件全复位：关闭所有设备句柄并重新 Open，用于断连后恢复。
        /// </summary>
        Task HardwareResetAsync();

        /// <summary>
        /// 工单追踪器；可用于 UI 读取最近工单快照。
        /// </summary>
        IWorkOrderTracker WorkOrderTracker { get; }

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