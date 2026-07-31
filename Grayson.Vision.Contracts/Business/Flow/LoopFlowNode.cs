using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;

namespace Grayson.Vision.Contracts.Business.Flow
{
    /// <summary>循环执行节点，多用于拍照重试3次</summary>
    public class LoopFlowNode : FlowNodeBase
    {
        /// <summary>最大循环次数</summary>
        public int MaxLoopTimes { get; set; }

        /// <summary>跳出循环的条件表达式</summary>
        public string BreakExpr { get; set; }

        /// <summary>循环体内执行节点</summary>
        public List<FlowNodeBase> BodyNodes { get; set; } = new List<FlowNodeBase>();

        public override Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            return Result.Ok();
        }
    }
}