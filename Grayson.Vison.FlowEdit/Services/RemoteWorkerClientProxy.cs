using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Station.Interfaces; // 引入 NodeExecutionErrorEventArgs
using Grayson.Vision.Contracts.Station.Models;           // 引入 NodeEventArgs
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Ipc.Models;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vison.FlowEdit.Services
{
    /// <summary>
    /// 远程 WorkerHost 进程 IPC 代理客户端
    /// 基于 NamedPipeClientStream 实现命令下发与事件监听
    /// </summary>
    public class RemoteWorkerClientProxy : IWorkerClient
    {
        public string StationId { get; }
        public bool IsConnected => _clientStream != null && _clientStream.IsConnected;
        /// <summary>
        /// 远程 Worker 的本地缓存状态
        /// </summary>
        public StationState CurrentState { get; private set; } = StationState.Stopped;

        private readonly string _pipeName;
        private NamedPipeClientStream _clientStream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private CancellationTokenSource _cts;

        public event EventHandler<StationState> OnStateChanged;
        public event EventHandler<ImageRenderEventArgs> OnFrameRendered;
        public event EventHandler<ChainCompletedEventArgs> OnExecutionCompleted;
        public event EventHandler<string> OnLogReceived;

        // 🌟 节点生命周期事件代理 (类型已对齐 IWorkerClient)
        public event EventHandler<NodeEventArgs> OnNodeExecuting;
        public event EventHandler<NodeEventArgs> OnNodeExecuted;
        public event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError;

        public RemoteWorkerClientProxy(string stationId)
        {
            StationId = stationId ?? "Station_01";
            _pipeName = $"Grayson_Vision_Pipe_{StationId}";
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                _clientStream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                LogBus.Info("IPC", $"正在连接远程 Worker 管道 [{_pipeName}]...");

                await _clientStream.ConnectAsync(3000);
                _reader = new StreamReader(_clientStream, Encoding.UTF8);
                _writer = new StreamWriter(_clientStream, Encoding.UTF8) { AutoFlush = true };

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ListenServerEventsAsync(_cts.Token));

                LogBus.Info("IPC", $"成功连接到工位 [{StationId}] 远程 Worker 进程！");
                return true;
            }
            catch (Exception ex)
            {
                LogBus.Error("IPC", $"连接远程 Worker 失败: {ex.Message}");
                return false;
            }
        }

        public async Task LoadRecipeAsync(FlowProcessModel recipe) => await SendCommandAsync("LoadRecipe", recipe);
        public async Task StartAsync() => await SendCommandAsync<object>("Start", null);
        public async Task StopAsync() => await SendCommandAsync<object>("Stop", null);
        public async Task TriggerOnceAsync(string batchId = null) => await SendCommandAsync("TriggerOnce", batchId ?? Guid.NewGuid().ToString("N"));

        public async Task StepNodeAsync(FlowNodeBase node) => await SendCommandAsync("StepNode", node);
        private async Task SendCommandAsync<T>(string action, T payload)
        {
            if (!IsConnected)
            {
                LogBus.Warn("IPC", $"工位 [{StationId}] 管道未连接，无法发送命令 [{action}]");
                return;
            }

            try
            {
                var msg = new IpcMessage
                {
                    StationId = StationId,
                    MessageType = IpcMessageType.Command,
                    Action = action,
                    PayloadJson = payload != null ? JsonConvert.SerializeObject(payload) : null
                };

                string jsonStr = JsonConvert.SerializeObject(msg);
                await _writer.WriteLineAsync(jsonStr);
                LogBus.Debug("IPC", $"已向 Worker 下发指令: {action}");
            }
            catch (Exception ex)
            {
                LogBus.Error("IPC", $"发送指令 [{action}] 发生异常: {ex.Message}");
            }
        }

        private async Task ListenServerEventsAsync(CancellationToken token)
        {
            try
            {
                while (IsConnected && !token.IsCancellationRequested)
                {
                    string line = await _reader.ReadLineAsync();
                    if (line == null) break;

                    var msg = JsonConvert.DeserializeObject<IpcMessage>(line);
                    if (msg != null && msg.MessageType == IpcMessageType.EventBroadcast)
                    {
                        ProcessBroadcastEvent(msg.Action, msg.PayloadJson);
                    }
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn("IPC", $"Worker 事件监听中断: {ex.Message}");
            }
        }

        private void ProcessBroadcastEvent(string action, string payloadJson)
        {
            switch (action)
            {
                case "OnStateChanged":
                    if (Enum.TryParse<StationState>(payloadJson, out var state))
                    {
                        CurrentState = state; // 🌟 接收到远程状态广播后更新本地属性
                        OnStateChanged?.Invoke(this, state);
                    }
                    
                    break;

                case "OnFrameRendered":
                    var frameArgs = JsonConvert.DeserializeObject<ImageRenderEventArgs>(payloadJson);
                    OnFrameRendered?.Invoke(this, frameArgs);
                    break;

                case "OnNodeExecuting":
                    var executingNode = JsonConvert.DeserializeObject<FlowNodeBase>(payloadJson);
                    if (executingNode != null)
                        OnNodeExecuting?.Invoke(this, new NodeEventArgs(StationId, executingNode));
                    break;

                case "OnNodeExecuted":
                    var executedNode = JsonConvert.DeserializeObject<FlowNodeBase>(payloadJson);
                    if (executedNode != null)
                        OnNodeExecuted?.Invoke(this, new NodeEventArgs(StationId, executedNode));
                    break;

                case "OnExecutionError":
                    var errPayload = JsonConvert.DeserializeObject<NodeErrorPayload>(payloadJson);
                    if (errPayload != null)
                    {
                        var errArgs = new NodeExecutionErrorEventArgs(errPayload.Node, new Exception(errPayload.ErrorMessage));
                        OnExecutionError?.Invoke(this, errArgs);
                    }
                    break;
                case "OnExecutionCompleted":
                    var completedArgs = JsonConvert.DeserializeObject<ChainCompletedEventArgs>(payloadJson);
                    if (completedArgs != null)
                    {
                        OnExecutionCompleted?.Invoke(this, completedArgs);
                    }
                    break;
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _reader?.Dispose();
            _writer?.Dispose();
            _clientStream?.Dispose();
        }
    }
}