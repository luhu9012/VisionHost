using System;

namespace Grayson.Vision.Contracts.Ipc.Models
{
    /// <summary>
    /// IPC 通信消息类型
    /// </summary>
    public enum IpcMessageType
    {
        Command,   // UI -> Worker 发出的控制指令
        Response,  // Worker -> UI 对指令的应答
        EventBroadcast // Worker -> UI 异步广播的事件 (如日志、帧渲染、状态变更)
    }

    /// <summary>
    /// 进程间通信统一数据包模型
    /// </summary>
    public class IpcMessage
    {
        /// <summary>
        /// 消息唯一标识 ID
        /// </summary>
        public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 目标工位 ID
        /// </summary>
        public string StationId { get; set; }

        /// <summary>
        /// 消息类型
        /// </summary>
        public IpcMessageType MessageType { get; set; }

        /// <summary>
        /// 指令名称或事件名称 (例如: "Start", "LoadRecipe", "OnStateChanged")
        /// </summary>
        public string Action { get; set; }

        /// <summary>
        /// 序列化后的 JSON 负载数据
        /// </summary>
        public string PayloadJson { get; set; }
    }
}