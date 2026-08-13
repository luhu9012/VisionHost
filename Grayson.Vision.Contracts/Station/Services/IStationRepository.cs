using System.Collections.Generic;
using Grayson.Vision.Contracts.Station.Models;

namespace Grayson.Vision.Contracts.Station.Services
{
    /// <summary>
    /// 产线与工位配置仓储接口
    /// </summary>
    public interface IStationRepository
    {
        /// <summary>
        /// 保存或更新产线（包含下属工位）
        /// </summary>
        bool SaveLine(LineConfigModel line);

        /// <summary>
        /// 保存或更新单个工位
        /// </summary>
        bool SaveStation(StationConfigModel station);

        /// <summary>
        /// 获取全部产线及其工位
        /// </summary>
        List<LineConfigModel> GetAllLines();

        /// <summary>
        /// 按产线ID获取
        /// </summary>
        LineConfigModel GetLineById(string lineId);

        /// <summary>
        /// 按工位ID获取
        /// </summary>
        StationConfigModel GetStationById(string stationId);

        /// <summary>
        /// 删除产线（同时删除下属工位）
        /// </summary>
        bool DeleteLine(string lineId);

        /// <summary>
        /// 删除工位
        /// </summary>
        bool DeleteStation(string stationId);
    }
}
