using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using LiteDB;
using System.Collections.Generic;

namespace Grayson.Vision.Repository.Implementations
{
    public class LiteDbDeviceConfigRepository : LiteDbRepositoryBase<DeviceConfigPo>, IDeviceConfigRepository
    {
        protected override string CollectionName => "device_configs";

        public DeviceConfigPo GetByKey(string deviceKey)
        {
            using (var db = Grayson.Vision.Repository.Core.DbContext.GetDatabase())
            {
                var col = db.GetCollection<DeviceConfigPo>(CollectionName);
                return col.FindOne(x => x.DeviceKey == deviceKey);
            }
        }

        public DeviceConfigPo GetByDeviceId(string deviceId)
        {
            using (var db = Grayson.Vision.Repository.Core.DbContext.GetDatabase())
            {
                var col = db.GetCollection<DeviceConfigPo>(CollectionName);
                return col.FindOne(x => x.DeviceId == deviceId);
            }
        }

        public IEnumerable<DeviceConfigPo> GetAllEnabled()
        {
            using (var db = Grayson.Vision.Repository.Core.DbContext.GetDatabase())
            {
                var col = db.GetCollection<DeviceConfigPo>(CollectionName);
                return col.Find(x => x.IsEnabled);
            }
        }

        // 覆盖 Insert 方法以创建唯一索引 (优化查询)
        public override bool Insert(DeviceConfigPo entity)
        {
            using (var db = Grayson.Vision.Repository.Core.DbContext.GetDatabase())
            {
                var col = db.GetCollection<DeviceConfigPo>(CollectionName);
                col.EnsureIndex(x => x.DeviceKey, true); // DeviceKey 唯一索引
                col.EnsureIndex(x => x.DeviceId);
                return base.Insert(entity);
            }
        }
    }
}