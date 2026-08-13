using Grayson.Vision.Contracts.Station.Models;
using System.Collections.Generic;

namespace Grayson.Vision.Repository.Interfaces
{
    /// <summary>
    /// 产线与工位配置仓储接口
    /// </summary>
    public interface IStationRepository
    {
        bool SaveLine(LineConfigModel line);
        bool SaveStation(StationConfigModel station);
        List<LineConfigModel> GetAllLines();
        LineConfigModel GetLineById(string lineId);
        StationConfigModel GetStationById(string stationId);
        bool DeleteLine(string lineId);
        bool DeleteStation(string stationId);
    }
}
