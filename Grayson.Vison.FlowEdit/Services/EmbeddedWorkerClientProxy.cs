using Grayson.Vision.Contracts.Business.Engine;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Events;           
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.Business.Enums;    
using Grayson.Vision.Core;
using System;
using System.Threading.Tasks;

namespace Grayson.Vison.FlowEdit.Services
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

        public StationState CurrentState => _worker?.State ?? StationState.Stopped;

        public event EventHandler<StationState> OnStateChanged;
        public event EventHandler<ImageRenderEventArgs> OnFrameRendered;
        public event EventHandler<string> OnLogReceived;

        // 🌟 节点生命周期事件代理 (类型已对齐 IWorkerClient)
        public event EventHandler<NodeEventArgs> OnNodeExecuting;
        public event EventHandler<NodeEventArgs> OnNodeExecuted;
        public event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError;

        public EmbeddedWorkerClientProxy(string stationId)
        {
            StationId = stationId;
            _worker = new StationWorker(stationId);

            _worker.Mode = WorkMode.Debug; // 默认调试模式，可根据需要调整

            // 订阅 StationWorker 的底层事件并转发
            _worker.OnStateChanged += (s, e) => OnStateChanged?.Invoke(this, e);
            _worker.OnFrameRendered += (s, e) => OnFrameRendered?.Invoke(this, e);

            // 🌟 转发逻辑类型完全对齐
            _worker.OnNodeExecuting += (s, args) => OnNodeExecuting?.Invoke(this, args);
            _worker.OnNodeExecuted += (s, args) => OnNodeExecuted?.Invoke(this, args);
            _worker.OnExecutionError += (s, args) => OnExecutionError?.Invoke(this, args);
        }

        public Task<bool> ConnectAsync() => Task.FromResult(true);
        public Task LoadRecipeAsync(FlowProcessModel recipe) => _worker.LoadRecipeAsync(recipe);
        public Task StartAsync() => Task.Run(() => _worker.StartAsync());
        public Task StopAsync() => Task.Run(() => _worker.StopAsync());
        public Task TriggerOnceAsync(string batchId = null) => Task.Run(() => _worker.TriggerOnceAsync(batchId));

        public Task StepNodeAsync(FlowNodeBase node) => Task.Run(() => _worker.StepNodeAsync(node));
        public void Dispose() => _worker?.Dispose();
    }
}