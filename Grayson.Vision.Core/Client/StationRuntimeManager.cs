using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Contracts.Station.WorkOrderTracking;

namespace Grayson.Vision.Core.Client
{
    /// <summary>
    /// 全局多工位运行期总管 (UI 调用的直接门面)。
    /// 当前版本内部转发到 StationHostRuntime，保留原有 API 兼容性。
    /// </summary>
    public class StationRuntimeManager : IDisposable
    {
        /// <summary>
        /// 全局单例，便于没有依赖注入的 VM 直接访问。
        /// </summary>
        public static StationRuntimeManager GlobalInstance { get; set; }

        private readonly IStationHostRuntime _hostRuntime;

        public IStationHostRuntime HostRuntime => _hostRuntime;

        /// <summary>
        /// 使用默认的 StationHostRuntime 创建管理器。
        /// </summary>
        public StationRuntimeManager()
        {
            // 默认使用全局 StationHostRuntime，避免 UI 层持有多个设备池实例导致状态割裂。
            _hostRuntime = Station.StationHostRuntime.GlobalInstance;
            if (_hostRuntime == null)
            {
                _hostRuntime = new Station.StationHostRuntime();
            }
        }

        /// <summary>
        /// 注入已有的 StationHostRuntime（推荐：全局共享）。
        /// </summary>
        public StationRuntimeManager(IStationHostRuntime hostRuntime)
        {
            _hostRuntime = hostRuntime ?? throw new ArgumentNullException(nameof(hostRuntime));
        }

        /// <summary>
        /// 统一启动并连接工位 (当前仅支持嵌入式线程模式)
        /// </summary>
        public async Task<IWorkerClient> CreateAndConnectStationAsync(string stationId, WorkerConnectMode mode)
        {
            var client = _hostRuntime.GetStationClient(stationId);
            if (client != null) return client;

            await _hostRuntime.InitializeAsync();
            return await _hostRuntime.CreateEmbeddedStationAsync(stationId);
        }

        public IWorkerClient GetClient(string stationId) => _hostRuntime.GetStationClient(stationId);

        /// <summary>
        /// 获取指定工位的工单追踪器；若工位未创建或未实现则返回 null。
        /// </summary>
        public IWorkOrderTracker GetWorkOrderTracker(string stationId)
            => _hostRuntime.GetWorkOrderTracker(stationId);

        public void Dispose()
        {
            _hostRuntime?.Dispose();
        }
    }
}
