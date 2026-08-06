
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Core.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Client
{
    /// <summary>
    /// 全局/多工位 Client 管理器 (WpfUI / FlowEdit 共用)
    /// </summary>
    public class WorkerClientManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, IWorkerClient> _clients
            = new ConcurrentDictionary<string, IWorkerClient>();

        /// <summary>
        /// 注册并连接指定工位
        /// </summary>
        public async Task<IWorkerClient> RegisterAndConnectAsync(string stationId, WorkerConnectMode mode)
        {
            if (_clients.TryGetValue(stationId, out var existingClient))
            {
                if (!existingClient.IsConnected)
                {
                    await existingClient.ConnectAsync();
                }
                return existingClient;
            }

            var client = WorkerClientFactory.CreateClient(stationId, mode);
            await client.ConnectAsync();

            _clients[stationId] = client;
            return client;
        }

        public IWorkerClient GetClient(string stationId)
        {
            _clients.TryGetValue(stationId, out var client);
            return client;
        }

        public IEnumerable<IWorkerClient> GetAllClients() => _clients.Values;

        /// <summary>
        /// 广播下发配方到所有连接的工位
        /// </summary>
        public async Task LoadRecipeToAllAsync(FlowProcessModel recipe)
        {
            foreach (var client in _clients.Values)
            {
                if (client.IsConnected)
                {
                    await client.LoadRecipeAsync(recipe);
                }
            }
        }

        public void Dispose()
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
            _clients.Clear();
        }
    }
}