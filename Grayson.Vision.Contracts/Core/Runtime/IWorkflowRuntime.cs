using Grayson.Vision.Contracts.Core.Message;
using Grayson.Vision.Contracts.Devices;
using System;
using System.Reflection;

namespace Grayson.Vision.Contracts.Core.Runtime
{
    /// <summary>
    /// 业务单元运行时服务门面
    /// 所有IBusinessUnit禁止直接引用宿主、硬件实例、日志类
    /// 全部通过此接口获取硬件、打印日志、推送UI消息，实现彻底解耦
    /// 每个流程执行器持有独立Runtime实例
    /// </summary>
    public interface IWorkflowRuntime
    {
        /// <summary>根据配置的DeviceKey获取硬件设备</summary>
        Result<IDevice> GetDevice(string deviceKey);

        /// <summary>全局消息总线</summary>
        IMessageBus MessageBus { get; }

        #region 分级日志输出
        void LogTrace(string msg, string moduleId=null);
        void LogInfo(string msg,string moduleId= null);
        void LogWarn(string msg,string moduleId= null);
        void LogError(string msg, Exception ex = null, string moduleId = null);

        #endregion

        /// <summary>推送UI事件，单元只发消息，不操作任何WPF控件</summary>
        void PublishUiEvent(string eventKey, object payload);
    }
}