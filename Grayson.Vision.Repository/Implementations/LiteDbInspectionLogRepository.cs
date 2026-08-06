// Implementations/LiteDbInspectionLogRepository.cs
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grayson.Vision.Repository.Implementations
{
    public class LiteDbInspectionLogRepository : LiteDbRepositoryBase<InspectionLogPo>, IInspectionLogRepository
    {
        protected override string CollectionName => "inspection_logs";

        public IEnumerable<InspectionLogPo> GetLogsByDate(string stationId, DateTime startTime, DateTime endTime)
        {
            return Find(x => x.StationId == stationId && x.InspectTime >= startTime && x.InspectTime <= endTime);
        }

        public (int TotalCount, int OkCount, int NgCount) GetYieldStats(string stationId, DateTime startTime, DateTime endTime)
        {
            var logs = GetLogsByDate(stationId, startTime, endTime).ToList();
            int total = logs.Count;
            int ok = logs.Count(x => x.IsOk);
            int ng = total - ok;
            return (total, ok, ng);
        }
    }
}