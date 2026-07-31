using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Business.Models;
namespace Grayson.Vision.Contracts.Business.Attributes
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
        /// 节点在 UI 工具箱和画布上显示的默认名称（如 "🔍 模板匹配"）
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// 节点功能描述，用于属性面板提示或 Tooltip
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// 节点绑定的参数模型 Type（如 typeof(TemplateMatchParam)）
        /// 用于属性面板动态加载配置项以及序列化配方
        /// </summary>
        public Type ParameterType { get; }

        /// <summary>
        /// 节点图标路径或 Material/FontAwesome 矢量图标 Key（可选，用于 UI 渲染）
        /// </summary>
        public string Icon { get; set; }

        public NodeAttribute(
            NodeType type,
            NodeCategory category,
            string displayName,
            string description = "",
            Type parameterType = null,
            string icon= "")
        {
            Type = type;
            Category = category;
            DisplayName = displayName;
            Description = description;
            ParameterType = parameterType;
            Icon = icon;
        }
    }
}
