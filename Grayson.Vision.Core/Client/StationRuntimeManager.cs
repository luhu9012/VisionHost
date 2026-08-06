using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Station.Interfaces;

namespace Grayson.Vision.Core.Client
{
    /// <summary>
    /// 全局多工位运行期总管 (UI 调用的直接门面)
    /// </summary>
    public class StationRuntimeManager : IDisposable
    {
        private readonly StationProcessManager _processManager = new StationProcessManager();
        private readonly ConcurrentDictionary<string, IWorkerClient> _clients = new ConcurrentDictionary<string, IWorkerClient>();

        /// <summary>
        /// 统一启动并连接工位 (不论是线程模式还是进程模式)
        /// </summary>
        public async Task<IWorkerClient> CreateAndConnectStationAsync(string stationId, WorkerConnectMode mode)
        {
            if (_clients.TryGetValue(stationId, out var existingClient))
                return existingClient;

            // 1. 如果是远程 IPC 模式，先用 ProcessManager 拉起外部 WorkerHost.exe
            if (mode == WorkerConnectMode.RemoteIpc)
            {
                _processManager.StartWorkerProcess(stationId);
                await Task.Delay(500); // 稍微等待 PipeServer 初始化完毕
            }

            // 2. 使用 Factory 创建客户端代理 (Embedded Proxy 内部会自动实例化 StationWorker)
            var client = WorkerClientFactory.CreateClient(stationId, mode);
            await client.ConnectAsync();

            _clients[stationId] = client;
            return client;
        }

        public IWorkerClient GetClient(string stationId) => _clients.TryGetValue(stationId, out var client) ? client : null;

        public void Dispose()
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
            _clients.Clear();

            _processManager.Dispose(); // 退出时统一清理/关闭外部进程
        }
    }
}