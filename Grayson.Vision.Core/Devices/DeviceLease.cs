using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Threading;

namespace Grayson.Vision.Core.Devices
{
    /// <summary>
    /// 设备租赁凭证的默认实现。
    /// </summary>
    public sealed class DeviceLease : IDeviceLease
    {
        private readonly Action<DeviceLease> _onRelease;
        private int _released;

        public string LeaseId { get; }
        public string StationId { get; }
        public string WorkOrderId { get; }
        public string LogicalDeviceKey { get; }
        public DeviceAccessMode AccessMode { get; }
        public DateTime LeasedAt { get; }
        public bool IsValid => _released == 0;
        public IDevice Device { get; }

        public DeviceLease(string leaseId, string stationId, string workOrderId,
            string logicalDeviceKey, DeviceAccessMode accessMode, IDevice device,
            Action<DeviceLease> onRelease)
        {
            LeaseId = leaseId ?? Guid.NewGuid().ToString("N");
            StationId = stationId;
            WorkOrderId = workOrderId;
            LogicalDeviceKey = logicalDeviceKey;
            AccessMode = accessMode;
            Device = device ?? throw new ArgumentNullException(nameof(device));
            _onRelease = onRelease ?? throw new ArgumentNullException(nameof(onRelease));
            LeasedAt = DateTime.UtcNow;
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _onRelease?.Invoke(this);
            }
        }

        public void Dispose()
        {
            Release();
        }
    }
}
