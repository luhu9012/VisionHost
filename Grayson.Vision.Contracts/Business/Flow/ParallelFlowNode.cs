using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;

namespace Grayson.Vision.Contracts.Business.Flow
{
    /// <summary>并行执行节点，多相机同步抓拍使用</summary>
    public class ParallelFlowNode : FlowNodeBase
    {
        public List<FlowNodeBase> ParallelNodes { get; set; } = new List<FlowNodeBase>();

        public override Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            return Result.Ok();
        }
    }
}