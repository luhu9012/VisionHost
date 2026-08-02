using Grayson.Vision.Contracts.Business.Engine;
using Grayson.Vision.Contracts.Business.Engine.Execution; 
using Grayson.Vision.Contracts.Business.Models;
using System;

namespace Grayson.Vision.Contracts.Business.Events
{
    /// <summary>
    /// 节点基础执行事件参数
    /// </summary>
    public class NodeEventArgs : EventArgs
    {
        public string StationId { get; set; }
        public FlowNodeBase Node { get; set; }

        public NodeEventArgs(string stationId, FlowNodeBase node)
        {
            StationId = stationId;
            Node = node;
        }
    }

    /// <summary>
    /// 统一汇总的工位事件暴露接口
    /// </summary>
    public interface IStationWorkerEvents
    {
        event EventHandler<StationState> OnStateChanged;
        event EventHandler<ImageRenderEventArgs> OnFrameRendered;
        event EventHandler<NodeEventArgs> OnNodeExecuting;
        event EventHandler<NodeEventArgs> OnNodeExecuted;
        event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError; 
        event EventHandler<string> OnLogReceived;
    }
}