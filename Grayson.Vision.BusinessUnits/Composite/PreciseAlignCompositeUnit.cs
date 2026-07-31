using Grayson.Vision.BusinessUnits.Location;
using Grayson.Vision.Common.Extensions;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Flow;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using System.Collections.Generic;
using System.Windows.Controls;

namespace Grayson.Vision.BusinessUnits.Composite
{
    /// <summary>
    /// 复合单元：粗定位→ROI裁剪→精定位整套流程
    /// 多个工位重复使用，打包成单个节点简化画布
    /// 实现ICompositeBusinessUnit，内部可进入子流程编辑
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "PreciseAlignCompositeUnit",
        DisplayName = "粗精定位复合流程",
        UnitType = BusinessUnitType.Location,
        Category = "复合复用流程",
        Description = "封装粗定位、图像裁剪、精定位三步，可展开编辑内部子节点")]
    public class PreciseAlignCompositeUnit : ICompositeBusinessUnit
    {
        public List<FlowNodeBase> SubFlowNodes { get; set; } = new List<FlowNodeBase>();
        public bool Enable { get; set; } = true;

        public string ModuleId => "PreciseAlignCompositeUnit";
        public string ModuleName => "粗精定位复合流程";
        public BusinessUnitType UnitType => BusinessUnitType.Location;

        public IReadOnlyList<string> InputKeys => new List<string> { ContextDataKeys.Grab_SourceImage };
        public IReadOnlyList<string> OutputKeys => new List<string> { ContextDataKeys.Match_IsSuccess, ContextDataKeys.Match_WorkPose };

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 递归执行内部所有子节点，由引擎统一遍历
            foreach (var node in SubFlowNodes)
            {
                var nodeResult = node.Execute(context, runtime);
                if (!nodeResult.Success)
                    return nodeResult;
            }
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            // 子节点完整序列化存入配方
            return new Dictionary<string, object>()
            {
                {"SubNodes", SubFlowNodes},
                {"Enable", Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            SubFlowNodes = recipeDict.SafeGet("SubNodes", new List<FlowNodeBase>());
            Enable = recipeDict.SafeGet("Enable", true);
        }

        //public UserControl GetConfigPanel()
        //{
        //    // 复合单元面板提供入口：打开子流程编辑器
        //    return new PreciseAlignCompositePanel(this);
        //}

        public void OpenSubFlowEditor()
        {
            // 宿主唤起内嵌子流程画布，编辑SubFlowNodes节点树
        }
    }
}