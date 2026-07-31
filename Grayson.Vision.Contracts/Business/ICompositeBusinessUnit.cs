using Grayson.Vision.Contracts.Business.Flow;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Business
{
    /// <summary>
    /// 复合业务单元：内部嵌套完整子工作流
    /// 对外依旧是IBusinessUnit，实现组合模式，兼顾灵活与复用
    /// 适用于粗定位+ROI+精定位这种多工位重复固定组合
    /// </summary>
    public interface ICompositeBusinessUnit : IBusinessUnit
    {
        /// <summary>内部嵌套子流程节点树</summary>
        List<FlowNodeBase> SubFlowNodes { get; set; }

        /// <summary>打开子流程独立编辑器画布</summary>
        void OpenSubFlowEditor();
    }
}