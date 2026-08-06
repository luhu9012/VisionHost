using Grayson.Vision.Repository.Entities;
using System.Collections.Generic;

namespace Grayson.Vision.Repository.Interfaces
{
    public interface IDeviceConfigRepository : IRepository<DeviceConfigPo, string>
    {
        // 根据业务需求扩展的查询方法
        DeviceConfigPo GetByKey(string deviceKey);
        DeviceConfigPo GetByDeviceId(string deviceId);
        IEnumerable<DeviceConfigPo> GetAllEnabled();
    }
}