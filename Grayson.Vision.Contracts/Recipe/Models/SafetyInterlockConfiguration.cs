namespace Grayson.Vision.Contracts.Recipe.Models
{
    /// <summary>
    /// 工位安全联锁配置：用于定义光栅、安全门、急停、双手按钮等输入信号的 IO 映射。
    /// 不依赖具体硬件 SDK，仅描述逻辑通道与安全策略。
    /// </summary>
    public class SafetyInterlockConfiguration
    {
        /// <summary>
        /// 是否启用安全联锁。
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// 急停输入信号逻辑通道名。
        /// </summary>
        public string EmergencyStopInputChannel { get; set; }

        /// <summary>
        /// 安全门/防护罩输入信号逻辑通道名。
        /// </summary>
        public string SafetyDoorInputChannel { get; set; }

        /// <summary>
        /// 安全光栅输入信号逻辑通道名。
        /// </summary>
        public string LightCurtainInputChannel { get; set; }

        /// <summary>
        /// 双手按钮同步输入逻辑通道名（用逗号分隔多个通道）。
        /// </summary>
        public string TwoHandControlChannels { get; set; }

        /// <summary>
        /// 安全门关闭后是否需要复位确认才能恢复运行。
        /// </summary>
        public bool RequireResetAfterDoorOpen { get; set; } = true;

        /// <summary>
        /// 光幕遮挡后是否允许继续当前工单（软 curtain）还是立即急停（硬 curtain）。
        /// </summary>
        public bool LightCurtainImmediateEStop { get; set; } = true;
    }
}
