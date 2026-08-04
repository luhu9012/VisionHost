using Grayson.Vision.Contracts.Flow.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Flow.Nodes
{
    /// <summary>
    /// 工作流节点模型:（具体画布节点）：继承自 FlowNodeBase，专服务于画布渲染、属性绑定与流程执行
    /// </summary>
    public class FlowNode : FlowNodeBase
    {
        public FlowNode() { }
        public FlowNode(NodeType type, string displayName, NodeCategory category, Point2D position, string description = "", object parameterModel = null)
        {
            Type = type;
            DisplayName = displayName;
            Category = category;
            Description = description;
            PosX = position.X;
            PosY = position.Y;
            ParameterModel = parameterModel;
        }
    }
}
