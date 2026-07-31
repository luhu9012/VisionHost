using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Devices;

namespace Grayson.Vision.Contracts.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class DevicePluginAttribute : Attribute
    {
        public string DeviceId { get; }
        public string DeviceName { get; }
        public DeviceCategory Category { get; }
        public string BrandName { get; }

        public DevicePluginAttribute(string deviceId, string deviceName, DeviceCategory category, string brandName)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
            Category = category;
            BrandName = brandName;
        }
    }
}
