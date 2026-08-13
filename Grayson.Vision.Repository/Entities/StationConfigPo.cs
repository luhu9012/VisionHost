using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Repository.Core;
using System.Collections.Generic;

namespace Grayson.Vision.Repository.Entities
{
    /// <summary>
    /// 产线工位配置持久化实体，内含 Contracts 层 StationConfigModel 完整结构
    /// </summary>
    public class StationConfigPo : BaseEntity
    {
        public string StationId { get; set; }
        public string StationCode { get; set; }
        public string StationName { get; set; }
        public string LineId { get; set; }
        public string LineName { get; set; }
        public bool IsEnabled { get; set; } = true;
        public int TimeoutMs { get; set; } = 3000;

        /// <summary>
        /// 直接包含 Contracts 层定义的完整配置模型
        /// </summary>
        public StationConfigModel Model { get; set; }

        /// <summary>
        /// 便于数据库按产线检索
        /// </summary>
        public List<string> DeviceKeys { get; set; } = new List<string>();
    }
}
