using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal
{
    public class UniversalProtocolPlugin : IHardwarePlugin
    {
        public string BrandName => "UniversalPLC";
        public DeviceCategory Category => DeviceCategory.PLC;
        public string Version => "1.0.0";

        public void Initialize()
        {
            // 如果使用 HslCommunication 正式授权，在此处写入授权码：
            // HslCommunication.Authorization.SetAuthorizationCode("YOUR_CODE");
        }

        public Result<List<DeviceInfo>> EnumerateDevices()
        {
            // 手动设备不支持局域网广播扫描，返回空列表
            return Result<List<DeviceInfo>>.Ok(new List<DeviceInfo>());
        }

        public IDevice CreateDevice(string deviceId)
        {
            return new UniversalPlcDevice(deviceId)
            {
                BrandName = BrandName,
                Category = Category
            };
        }

        public void Shutdown() { }
    }
}
