using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Ipc.Models;
using Newtonsoft.Json;

namespace Grayson.Vision.WorkerHost
{
    /// <summary>
    /// 基于命名管道 (Named Pipe) 的 IPC 通信服务端
    /// 用于 WorkerHost 进程与 WpfUI 主进程之间的低延迟双向通信
    /// </summary>
    public class NamedPipeIpcServer : IDisposable
    {
        private readonly string _pipeName;
        private CancellationTokenSource _cts;
        private NamedPipeServerStream _pipeServer;

        /// <summary>
        /// 收到 UI 发来的控制命令回调 (Command, PayloadJson)
        /// </summary>
        public event Action<string, string> OnCommandReceived;

        public NamedPipeIpcServer(string stationId)
        {
            // 为不同工位创建独立的命名管道名称
            _pipeName = $"Grayson_Vision_Pipe_{stationId}";
        }

        /// <summary>
        /// 启动管道监听线程
        /// </summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ListenAsync(_cts.Token));
        }

        private async Task ListenAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _pipeServer = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    Console.WriteLine($"[IPC Server] 正在管道 [{_pipeName}] 监听 UI 连接...");
                    await Task.Factory.FromAsync(_pipeServer.BeginWaitForConnection, _pipeServer.EndWaitForConnection, null);
                    Console.WriteLine($"[IPC Server] UI 客户端已成功连接！");

                    using (var reader = new StreamReader(_pipeServer, Encoding.UTF8))
                    {
                        while (_pipeServer.IsConnected && !token.IsCancellationRequested)
                        {
                            string line = await reader.ReadLineAsync();
                            if (line == null) break;

                            var msg = JsonConvert.DeserializeObject<IpcMessage>(line);
                            if (msg != null && msg.MessageType == IpcMessageType.Command)
                            {
                                OnCommandReceived?.Invoke(msg.Action, msg.PayloadJson);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Console.WriteLine($"[IPC Server Error] 管道连接异常: {ex.Message}");
                        await Task.Delay(1000, token); // 异常时稍作停顿后重新等待连接
                    }
                }
                finally
                {
                    _pipeServer?.Dispose();
                }
            }
        }

        /// <summary>
        /// 向 UI 客户端异步广播事件 (状态变更、图像渲染等)
        /// </summary>
        public void BroadcastEvent<T>(string action, T payload)
        {
            if (_pipeServer == null || !_pipeServer.IsConnected) return;

            try
            {
                var msg = new IpcMessage
                {
                    MessageType = IpcMessageType.EventBroadcast,
                    Action = action,
                    PayloadJson = JsonConvert.SerializeObject(payload)
                };

                string jsonStr = JsonConvert.SerializeObject(msg) + "\n";
                byte[] bytes = Encoding.UTF8.GetBytes(jsonStr);

                lock (_pipeServer)
                {
                    if (_pipeServer.IsConnected)
                    {
                        _pipeServer.Write(bytes, 0, bytes.Length);
                        _pipeServer.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IPC Broadcast Error] 广播消息失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _pipeServer?.Dispose();
        }
    }
}