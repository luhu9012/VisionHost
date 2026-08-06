// Interfaces/IInspectionLogRepository.cs
using Grayson.Vision.Repository.Entities;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Repository.Interfaces
{
    public interface IInspectionLogRepository : IRepository<InspectionLogPo, string>
    {
        IEnumerable<InspectionLogPo> GetLogsByDate(string stationId, DateTime startTime, DateTime endTime);
        (int TotalCount, int OkCount, int NgCount) GetYieldStats(string stationId, DateTime startTime, DateTime endTime);
    }
}