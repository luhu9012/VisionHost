 using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Contracts.Business.Enums;
using System.Collections.Generic;
//using System.Windows.Controls;

namespace Grayson.Vision.Contracts.Business
{
    /// <summary>
    /// 所有原子视觉、PLC、运动、数据单元统一顶层接口
    /// 所有可拖拽流程节点的业务逻辑都实现此接口
    /// 支持执行、参数序列化、独立配置面板
    /// </summary>
    public interface IBusinessUnit
    {
        /// <summary>唯一模块ID，和特性标记保持一致</summary>
        string ModuleId { get; }

        /// <summary>界面展示名称</summary>
        string ModuleName { get; }

        /// <summary>单元业务分类</summary>
        BusinessUnitType UnitType { get; }

        /// <summary>当前节点是否启用，可在画布临时禁用不走逻辑</summary>
        bool Enable { get; set; }

        /// <summary>本单元需要读取的上下文Key列表，编辑器做依赖校验</summary>
        IReadOnlyList<string> InputKeys { get; }

        /// <summary>本单元执行完成写入的上下文Key列表</summary>
        IReadOnlyList<string> OutputKeys { get; }

        /// <summary>
        /// 单元核心执行逻辑
        /// </summary>
        /// <param name="context">单次流程上下文</param>
        /// <param name="runtime">运行时服务，用来拿硬件、打日志、推UI</param>
        Result Execute(VisionContext context, IWorkflowRuntime runtime);

        /// <summary>把单元当前所有配置参数导出字典，存入配方</summary>
        Dictionary<string, object> SaveRecipe();

        /// <summary>从配方字典加载参数</summary>
        void LoadRecipe(Dictionary<string, object> recipeDict);

        /// <summary>返回WPF配置面板，双击节点弹出参数配置</summary>
        //UserControl GetConfigPanel();
    }
}