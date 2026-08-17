using Grayson.Vision.Contracts.Station.Enums;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Recipe.Models
{
    /// <summary>
    /// 工位运行配置：调度器、超时默认值、IO 映射、报警阈值等。
    /// 切换工艺参数集时不需要改动此配置。
    /// </summary>
    public class StationRuntimeConfiguration
    {
        /// <summary>配置版本</summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>默认调度器类型：SimpleTrigger / Pipeline / EventDriven</summary>
        public string SchedulerType { get; set; } = "SimpleTrigger";

        /// <summary>同一工单最大执行超时（毫秒）</summary>
        public int DefaultWorkOrderTimeoutMs { get; set; } = 30000;

        /// <summary>是否允许工位在节点失败后自动软复位</summary>
        public bool AutoSoftResetOnFailure { get; set; } = false;

        /// <summary>IO 信号映射：信号逻辑名 -> 物理通道</summary>
        public Dictionary<string, string> IoMappings { get; set; }
            = new Dictionary<string, string>();

        /// <summary>告警阈值：例如连续 NG 次数、节点耗时上限等</summary>
        public Dictionary<string, double> AlarmThresholds { get; set; }
            = new Dictionary<string, double>();

        /// <summary>默认工作模式</summary>
        public WorkMode DefaultWorkMode { get; set; } = WorkMode.Production;

        /// <summary>
        /// 安全联锁配置：光栅、安全门、急停、双手按钮等。
        /// </summary>
        public SafetyInterlockConfiguration Safety { get; set; }
            = new SafetyInterlockConfiguration();

        /// <summary>
        /// 最小节拍时间（毫秒），防止设备过速运行或PLC周期抖动导致的误触发。
        /// </summary>
        public int MinimumCycleTimeMs { get; set; } = 50;

        /// <summary>
        /// 当前界面/报警语言代码，如 "zh-CN" / "en-US"。
        /// </summary>
        public string LanguageCode { get; set; } = "zh-CN";
    }
}
