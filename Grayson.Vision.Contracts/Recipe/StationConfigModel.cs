using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Recipe
{
    /// <summary>工位运行状态枚举，用于重启恢复判断</summary>
    public enum StationRunStatus
    {
        Idle,
        Stopped,
        Running,
        Paused,
        Fault
    }

    /// <summary>全局工位配置，独立持久化，不受配方变更影响，软件重启恢复核心</summary>
    public class StationConfigModel
    {
        /// <summary>工位唯一ID</summary>
        public string StationId { get; set; }

        /// <summary>工位页面展示名称</summary>
        public string StationDisplayName { get; set; }

        /// <summary>该工位绑定的硬件Key列表</summary>
        public List<string> BindDeviceKeys { get; set; } = new List<string>();

        /// <summary>上次运行加载的配方ID，重启自动加载该配方</summary>
        public string LastActiveRecipeId { get; set; }

        /// <summary>软件正常退出时记录的工位运行状态</summary>
        public StationRunStatus LastRunStatus { get; set; }

        /// <summary>开机是否自动恢复上次运行状态，可手动关闭</summary>
        public bool AutoRestoreOnStartup { get; set; } = true;
    }
}