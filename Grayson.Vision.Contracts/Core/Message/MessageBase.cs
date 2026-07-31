namespace Grayson.Vision.Contracts.Core.Message
{
    /// <summary>所有总线消息父类</summary>
    public abstract class MessageBase
    {
        /// <summary>消息发送方标识（工位ID/单元名称）</summary>
        public string Sender { get; set; }
        /// <summary>消息发生时间戳</summary>
        public long Timestamp { get; set; }
    }

    /// <summary>硬件设备状态变更消息</summary>
    public class DeviceStateChangedMessage : MessageBase
    {
        public string DeviceKey { get; set; }
        public Business.Enums.DeviceState NewState { get; set; }
    }

    /// <summary>单条视觉流程执行完成消息</summary>
    public class WorkflowFinishMessage : MessageBase
    {
        public string StationId { get; set; }
        public bool IsPass { get; set; }
    }
}