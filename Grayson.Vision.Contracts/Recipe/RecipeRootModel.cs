using System.Collections.Generic;
using Grayson.Vision.Contracts.Business.Flow;

namespace Grayson.Vision.Contracts.Recipe
{
    /// <summary>整套产品配方实体：包含流程拓扑+所有节点参数</summary>
    public class RecipeRootModel
    {
        /// <summary>配方唯一ID</summary>
        public string RecipeId { get; set; }

        /// <summary>适配产品型号</summary>
        public string ProductModel { get; set; }

        /// <summary>配方版本号，用于迭代管理</summary>
        public string Version { get; set; }

        /// <summary>创建时间戳</summary>
        public long CreateTime { get; set; }

        /// <summary>完整工位流程节点树</summary>
        public List<FlowNodeBase> WholeFlowNodes { get; set; } = new List<FlowNodeBase>();

        /// <summary>键：NodeId，值：当前节点所有单元参数字典</summary>
        public Dictionary<string, Dictionary<string, object>> NodeRecipeDict { get; set; } = new Dictionary<string, Dictionary<string, object>>();
    }
}