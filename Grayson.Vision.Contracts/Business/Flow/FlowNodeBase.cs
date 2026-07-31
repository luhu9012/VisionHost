using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;

namespace Grayson.Vision.Contracts.Business.Flow
{
    /// <summary>所有流程节点父类，分为业务节点、控制节点</summary>
    public abstract class FlowNodeBase
    {
        /// <summary>节点全局唯一ID，配方序列化标识节点</summary>
        public string NodeId { get; set; }

        /// <summary>节点是否启用，关闭则跳过执行</summary>
        public bool Enable { get; set; }

        /// <summary>递归执行当前节点逻辑</summary>
        public abstract Result Execute(VisionContext context, IWorkflowRuntime runtime);
    }
}