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
        public string BrandName => "UniversalProtocol";
        // 标记为通用驱动，不绑定单一类型
        public DeviceCategory Category => DeviceCategory.Generic;
        public string Version => "1.0.0";
        // 最低优先级：用于无匹配插件时的全局兜底
        public int Priority => -100;

        public bool Supports(DeviceCategory category, string brand)
        {
            // 兜底逻辑：支持所有通过 ConnectionString 配置的手动设备
            return true;
        }

        public void Initialize() { }

        public Result<List<DeviceInfo>> EnumerateDevices()
        {
            return Result<List<DeviceInfo>>.Ok(new List<DeviceInfo>());
        }

        public IDevice CreateDevice(string deviceId)
        {
            // 内部根据传入参数或 Protocol 标记创建对应的物理协议设备对象
            return new UniversalPlcDevice(deviceId)
            {
                BrandName = BrandName,
                Category = Category
            };
        }

        public void Shutdown() { }
    }
}
