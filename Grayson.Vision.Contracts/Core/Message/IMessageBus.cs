using System;

namespace Grayson.Vision.Contracts.Core.Message
{
    /// <summary>进程内消息总线，模块/插件/宿主解耦通信</summary>
    public interface IMessageBus
    {
        /// <summary>发布一条消息</summary>
        void Publish<T>(T msg) where T : MessageBase;

        /// <summary>订阅指定类型消息</summary>
        void Subscribe<T>(Action<T> callback) where T : MessageBase;

        /// <summary>取消订阅</summary>
        void Unsubscribe<T>(Action<T> callback) where T : MessageBase;
    }
}