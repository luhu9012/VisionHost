using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;

namespace Grayson.Vision.Contracts.Business.Flow
{
    /// <summary>If Else条件分支控制节点</summary>
    public class ConditionFlowNode : FlowNodeBase
    {
        /// <summary>C#风格条件表达式，DynamicExpresso解析</summary>
        public string ConditionExpr { get; set; }

        /// <summary>条件成立执行的子节点列表</summary>
        public List<FlowNodeBase> TrueBranch { get; set; } = new List<FlowNodeBase>();

        /// <summary>条件不成立执行分支</summary>
        public List<FlowNodeBase> FalseBranch { get; set; } = new List<FlowNodeBase>();

        public override Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 执行逻辑宿主WorkflowExecutor内部实现，契约只定义结构
            return Result.Ok();
        }
    }
}