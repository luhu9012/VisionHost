using Grayson.Vision.Core.Client.Proxy;
using Grayson.Vision.Contracts.Station.Interfaces;

namespace Grayson.Vision.Core.Client
{
    /// <summary>
    /// Worker 连接模式。
    /// 当前版本仅支持同进程嵌入式线程模式；多进程扩展点在架构上预留，但不在本版本中实现。
    /// </summary>
    public enum WorkerConnectMode
    {
        /// <summary>
        /// 同进程嵌入式线程 (用于 FlowEdit 快速调试或单进程运行)
        /// </summary>
        Embedded
    }

    public static class WorkerClientFactory
    {
        /// <summary>
        /// 统一创建 Worker 客户端实例。
        /// 当前版本固定返回同进程嵌入式代理，保留 mode 参数以兼容未来多进程扩展。
        /// </summary>
        public static IWorkerClient CreateClient(string stationId, WorkerConnectMode mode)
        {
            return new EmbeddedWorkerClientProxy(stationId);
        }
    }
}