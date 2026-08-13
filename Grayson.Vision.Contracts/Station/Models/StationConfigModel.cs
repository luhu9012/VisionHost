using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Recipe.Models;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 工位对外配置模型：包含配方、硬件映射、IO/运行参数。
    /// 用于 UI 配置保存后下发给 StationRuntimeManager。
    /// </summary>
    public class StationConfigModel
    {
        public string StationId { get; set; }
        public string StationCode { get; set; }
        public string StationName { get; set; }
        public string LineId { get; set; }
        public string LineName { get; set; }
        public bool IsEnabled { get; set; } = true;
        public int TimeoutMs { get; set; } = 3000;

        /// <summary>
        /// 当前绑定配方的完整模型
        /// </summary>
        public RecipeModel BoundRecipe { get; set; }

        /// <summary>
        /// 配方逻辑设备 -> 物理设备 DeviceKey 的映射
        /// </summary>
        public List<DeviceMappingModel> DeviceMappings { get; set; } = new List<DeviceMappingModel>();

        /// <summary>
        /// 运行期参数（可扩展：如触发模式、调试/生产模式等）
        /// </summary>
        public Dictionary<string, string> RuntimeParams { get; set; } = new Dictionary<string, string>();

        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime UpdatedTime { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 单个逻辑设备到物理设备的映射
    /// </summary>
    public class DeviceMappingModel
    {
        public string LogicalDeviceId { get; set; }
        public string LogicalDeviceName { get; set; }
        public string LogicalDeviceType { get; set; }
        public string RequiredSpec { get; set; }
        public string MappedDeviceKey { get; set; }
        public string MappedDeviceName { get; set; }
    }

    /// <summary>
    /// 产线模型：轻量领域对象，用于 UI 层产线拓扑总览。
    /// </summary>
    public class LineConfigModel
    {
        public string LineId { get; set; }
        public string LineName { get; set; }
        public List<StationConfigModel> Stations { get; set; } = new List<StationConfigModel>();
    }

    /// <summary>
    /// 工位实时信息（由 UI 从 IWorkerClient 聚合刷新，不持久化）
    /// </summary>
    public class StationRuntimeInfo
    {
        public string StationId { get; set; }
        public bool IsConnected { get; set; }
        public string State { get; set; } = "Stopped";
        public string CurrentRecipeName { get; set; }
        public int TotalCount { get; set; }
        public int OkCount { get; set; }
        public int NgCount { get; set; }
        public double YieldRate { get; set; }
        public string LastResult { get; set; }
        public string LastMessage { get; set; }
        public DateTime LastUpdateTime { get; set; } = DateTime.Now;
    }
}
