using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;

namespace Grayson.Vision.Contracts.Flow.Attributes
{/// <summary>
 /// 标注在节点执行器（INodeExecutor）或节点 ViewModel 上的元数据特性
 /// 用于工具箱自动扫描注册、节点工厂动态创建以及属性面板匹配
 /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public class NodeAttribute : Attribute
    {
        /// <summary>
        /// 节点类型枚举（23种全量节点之一）
        /// </summary>
        public NodeType Type { get; }

        /// <summary>
        /// 节点所属业务分类
        /// </summary>
        public NodeCategory Category { get; }


        /// <summary>
        /// 节点绑定的参数模型 Type（如 typeof(TemplateMatchParam)）
        /// 用于属性面板动态加载配置项以及序列化配方
        /// </summary>
        public Type ParameterType { get; }



        public NodeAttribute(
            NodeType type,
            NodeCategory category,
            Type parameterType = null
            )
        {
            Type = type;
            Category = category;
            ParameterType = parameterType;
        }
    }
}
