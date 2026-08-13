using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Repository.Core;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using LiteDB;

namespace Grayson.Vision.Repository.Implementations
{
    public class LiteDbStationRepository : LiteDbRepositoryBase<StationConfigPo>, IStationRepository
    {
        protected override string CollectionName => "station_configs";

        public bool SaveLine(LineConfigModel line)
        {
            if (line == null || string.IsNullOrEmpty(line.LineId)) return false;

            using (var db = DbContext.GetDatabase())
            {
                var col = db.GetCollection<StationConfigPo>(CollectionName);
                var existing = col.Find(x => x.LineId == line.LineId).ToList();

                // 删除已被移除的工位
                var incomingIds = line.Stations?.Select(s => s.StationId).Where(id => !string.IsNullOrEmpty(id)).ToList()
                    ?? new List<string>();
                foreach (var po in existing)
                {
                    if (!incomingIds.Contains(po.StationId))
                    {
                        col.Delete(po.Id);
                    }
                }

                if (line.Stations != null)
                {
                    foreach (var station in line.Stations)
                    {
                        station.LineId = line.LineId;
                        station.LineName = line.LineName;
                        SaveStationInternal(station, col);
                    }
                }
            }

            return true;
        }

        public bool SaveStation(StationConfigModel station)
        {
            if (station == null || string.IsNullOrEmpty(station.StationId)) return false;

            using (var db = DbContext.GetDatabase())
            {
                var col = db.GetCollection<StationConfigPo>(CollectionName);
                return SaveStationInternal(station, col);
            }
        }

        private bool SaveStationInternal(StationConfigModel station, ILiteCollection<StationConfigPo> col)
        {
            if (station == null || string.IsNullOrEmpty(station.StationId)) return false;

            station.UpdatedTime = DateTime.Now;

            var existing = col.Find(x => x.StationId == station.StationId).FirstOrDefault();
            if (existing != null)
            {
                existing.StationCode = station.StationCode;
                existing.StationName = station.StationName;
                existing.LineId = station.LineId;
                existing.LineName = station.LineName;
                existing.IsEnabled = station.IsEnabled;
                existing.TimeoutMs = station.TimeoutMs;
                existing.Model = station;
                existing.DeviceKeys = station.DeviceMappings?.Select(d => d.MappedDeviceKey).Where(k => !string.IsNullOrEmpty(k)).ToList()
                    ?? new List<string>();
                existing.UpdatedTime = DateTime.Now;
                return col.Update(existing);
            }

            var po = new StationConfigPo
            {
                StationId = station.StationId,
                StationCode = station.StationCode,
                StationName = station.StationName,
                LineId = station.LineId,
                LineName = station.LineName,
                IsEnabled = station.IsEnabled,
                TimeoutMs = station.TimeoutMs,
                Model = station,
                DeviceKeys = station.DeviceMappings?.Select(d => d.MappedDeviceKey).Where(k => !string.IsNullOrEmpty(k)).ToList()
                    ?? new List<string>()
            };

            if (string.IsNullOrEmpty(po.Id))
            {
                po.Id = Guid.NewGuid().ToString("N");
            }
            po.CreatedTime = DateTime.Now;
            po.UpdatedTime = DateTime.Now;

            return col.Insert(po) != null;
        }

        public List<LineConfigModel> GetAllLines()
        {
            using (var db = DbContext.GetDatabase())
            {
                var col = db.GetCollection<StationConfigPo>(CollectionName);
                var all = col.FindAll().ToList();

                return all
                    .Where(x => x.Model != null)
                    .GroupBy(x => x.LineId ?? string.Empty)
                    .Select(g => new LineConfigModel
                    {
                        LineId = g.Key,
                        LineName = g.FirstOrDefault(x => !string.IsNullOrEmpty(x.LineName))?.LineName ?? g.Key,
                        Stations = g.OrderBy(x => x.StationCode).Select(x => x.Model).ToList()
                    })
                    .ToList();
            }
        }

        public LineConfigModel GetLineById(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)) return null;

            using (var db = DbContext.GetDatabase())
            {
                var col = db.GetCollection<StationConfigPo>(CollectionName);
                var all = col.Find(x => x.LineId == lineId).ToList();
                if (!all.Any()) return null;

                return new LineConfigModel
                {
                    LineId = lineId,
                    LineName = all.FirstOrDefault(x => !string.IsNullOrEmpty(x.LineName))?.LineName ?? lineId,
                    Stations = all.Where(x => x.Model != null).Select(x => x.Model).ToList()
                };
            }
        }

        public StationConfigModel GetStationById(string stationId)
        {
            if (string.IsNullOrEmpty(stationId)) return null;

            using (var db = DbContext.GetDatabase())
            {
                var col = db.GetCollection<StationConfigPo>(CollectionName);
                var po = col.Find(x => x.StationId == stationId).FirstOrDefault();
                return po?.Model;
            }
        }

        public bool DeleteLine(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)) return false;

            using (var db = DbContext.GetDatabase())
            {
                var col = db.GetCollection<StationConfigPo>(CollectionName);
                var ids = col.Find(x => x.LineId == lineId).Select(x => x.Id).ToList();
                foreach (var id in ids)
                {
                    col.Delete(id);
                }
            }

            return true;
        }

        public bool DeleteStation(string stationId)
        {
            if (string.IsNullOrEmpty(stationId)) return false;

            using (var db = DbContext.GetDatabase())
            {
                var col = db.GetCollection<StationConfigPo>(CollectionName);
                var po = col.Find(x => x.StationId == stationId).FirstOrDefault();
                if (po == null) return false;
                return col.Delete(po.Id);
            }
        }
    }
}
