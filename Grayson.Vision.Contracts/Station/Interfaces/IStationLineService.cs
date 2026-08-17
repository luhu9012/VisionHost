using Grayson.Vision.Contracts.Station.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Station.Interfaces
{
    /// <summary>
    /// 产线工位编排服务契约（Core 层实现）。
    /// 管理多个站点的运行状态、结果汇总与跨工位触发。
    /// </summary>
    public interface IStationLineService
    {
        /// <summary>注册一个工位 WorkerHost</summary>
        void RegisterStation(IStationWorkerHost host);

        /// <summary>获取所有工位状态快照</summary>
        IReadOnlyDictionary<string, StationState> GetStationStates();

        /// <summary>向指定工位触发一次执行</summary>
        Task TriggerStationAsync(string stationId, string batchId = null);

        /// <summary>停止所有工位</summary>
        Task StopAllAsync();
    }
}
