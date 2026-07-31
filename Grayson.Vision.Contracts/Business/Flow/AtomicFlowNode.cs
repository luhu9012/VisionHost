using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;

namespace Grayson.Vision.Contracts.Business.Flow
{
    /// <summary>承载IBusinessUnit的普通执行节点</summary>
    public class AtomicFlowNode : FlowNodeBase
    {
        /// <summary>绑定的业务单元ModuleId</summary>
        public string ModuleId { get; set; }

        /// <summary>运行时实例化后的单元对象</summary>
        public IBusinessUnit BindUnit { get; set; }

        public override Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 节点关闭、单元不存在、单元自身关闭，直接跳过
            if (!Enable || BindUnit == null || !BindUnit.Enable)
                return Result.Ok();
            return BindUnit.Execute(context, runtime);
        }
    }
}