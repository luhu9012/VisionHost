using System;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 设备领用/归还事件参数。
    /// </summary>
    public class DeviceLeaseEventArgs : EventArgs
    {
        public string DeviceKey { get; }
        public string StationId { get; }
        public bool IsLeased { get; }

        public DeviceLeaseEventArgs(string deviceKey, string stationId, bool isLeased)
        {
            DeviceKey = deviceKey;
            StationId = stationId;
            IsLeased = isLeased;
        }
    }
}
