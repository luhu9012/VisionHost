using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Flow.Contexts;           
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Station.Models;    
using Grayson.Vision.Contracts.Station.WorkOrderTracking;
using Grayson.Vision.Core;
using System;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Client.Proxy
{
    /// <summary>
    /// 本地嵌入式后台线程 Worker 代理
    /// 在 WPF 同一进程后台运行，适合无独立进程依赖的快捷调试
    /// </summary>
    public class EmbeddedWorkerClientProxy : IWorkerClient
    {
        public string StationId { get; }
        public bool IsConnected => true;

        private readonly StationWorker _worker;
        private readonly bool _ownsWorker;

        public StationState CurrentState => _worker?.State ?? StationState.Stopped;

        public IWorkOrderTracker WorkOrderTracker => _worker?.WorkOrderTracker;

        public event EventHandler<StationState> OnStateChanged;
        public event EventHandler<ImageRenderEventArgs> OnFrameRendered;
        public event EventHandler<ChainCompletedEventArgs> OnExecutionCompleted;
        public event EventHandler<string> OnLogReceived;

        // 🌟 节点生命周期事件代理 (类型已对齐 IWorkerClient)
        public event EventHandler<NodeEventArgs> OnNodeExecuting;
        public event EventHandler<NodeEventArgs> OnNodeExecuted;
        public event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError;

        /// <summary>
        /// 由外部传入已有 StationWorker（推荐，由 StationHostRuntime 统一管理生命周期）。
        /// </summary>
        public EmbeddedWorkerClientProxy(StationWorker worker)
        {
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            StationId = worker.StationId;
            _ownsWorker = false;
            BindWorkerEvents();
        }

        /// <summary>
        /// 兼容旧的无参构造：内部创建新的 StationWorker。
        /// </summary>
        public EmbeddedWorkerClientProxy(string stationId)
        {
            StationId = stationId;
            _worker = new StationWorker(stationId);
            _ownsWorker = true;
            BindWorkerEvents();
        }

        private void BindWorkerEvents()
        {
            if (_worker == null) return;

            _worker.OnStateChanged += (s, e) => OnStateChanged?.Invoke(this, e);
            _worker.OnFrameRendered += (s, e) => OnFrameRendered?.Invoke(this, e);
            _worker.OnExecutionCompleted += (s, e) => OnExecutionCompleted?.Invoke(this, e);
            _worker.OnNodeExecuting += (s, args) => OnNodeExecuting?.Invoke(this, args);
            _worker.OnNodeExecuted += (s, args) => OnNodeExecuted?.Invoke(this, args);
            _worker.OnExecutionError += (s, args) => OnExecutionError?.Invoke(this, args);
        }

        private void UnbindWorkerEvents()
        {
            if (_worker == null) return;

            _worker.OnStateChanged -= (s, e) => OnStateChanged?.Invoke(this, e);
            _worker.OnFrameRendered -= (s, e) => OnFrameRendered?.Invoke(this, e);
            _worker.OnExecutionCompleted -= (s, e) => OnExecutionCompleted?.Invoke(this, e);
            _worker.OnNodeExecuting -= (s, args) => OnNodeExecuting?.Invoke(this, args);
            _worker.OnNodeExecuted -= (s, args) => OnNodeExecuted?.Invoke(this, args);
            _worker.OnExecutionError -= (s, args) => OnExecutionError?.Invoke(this, args);
        }

        public Task<bool> ConnectAsync() => Task.FromResult(true);
        public Task LoadRecipeAsync(FlowProcessModel recipe) => _worker.LoadRecipeAsync(recipe);
        public Task StartAsync() => Task.Run(() => _worker.StartAsync());
        public Task StopAsync() => Task.Run(() => _worker.StopAsync());
        public Task PauseAsync() => Task.Run(() => _worker.PauseAsync());
        public Task ResumeAsync() => Task.Run(() => _worker.ResumeAsync());
        public Task TriggerOnceAsync(string batchId = null) => Task.Run(() => _worker.TriggerOnceAsync(batchId));
        public Task EmergencyStopAsync(string reason = null) => Task.Run(() => _worker.EmergencyStopAsync(reason));

        public Task StepNodeAsync(FlowNodeBase node) => Task.Run(() => _worker.StepNodeAsync(node));

        public void Dispose()
        {
            UnbindWorkerEvents();
            if (_ownsWorker)
            {
                _worker?.Dispose();
            }
        }
    }
}