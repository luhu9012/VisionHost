using Grayson.Vision.Core.Client.Proxy; 
using Grayson.Vision.Contracts.Station.Interfaces;

namespace Grayson.Vision.Core.Client
{
    public enum WorkerConnectMode
    {
        /// <summary>
        /// 同进程嵌入式线程 (用于 FlowEdit 快速调试或单进程运行)
        /// </summary>
        Embedded,

        /// <summary>
        /// 跨进程 IPC 命名管道 (用于 Production 生产态/多进程运行)
        /// </summary>
        RemoteIpc
    }

    public static class WorkerClientFactory
    {
        /// <summary>
        /// 统一创建 Worker 客户端实例
        /// </summary>
        public static IWorkerClient CreateClient(string stationId, WorkerConnectMode mode)
        {
            switch (mode)
            {
                case WorkerConnectMode.Embedded:
                    return new EmbeddedWorkerClientProxy(stationId);

                case WorkerConnectMode.RemoteIpc:
                    return new RemoteWorkerClientProxy(stationId);

                default:
                    return new EmbeddedWorkerClientProxy(stationId);
            }
        }
    }
}