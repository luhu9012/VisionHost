using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Enums;

namespace Grayson.Vision.Contracts.Flow.Attributes
{
    /// <summary>
    /// 声明节点默认拥有的端口（允许在一个 Executor 类上标记多个）
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
    public class NodePortAttribute : Attribute
    {
        public string PortName { get; }
        public PortType PortType { get; }
        public PortCategory Category { get; }
        public string DataType { get; }
        public string ColorHex { get; }

        public NodePortAttribute(
            string portName,
            PortType portType,
            PortCategory category = PortCategory.Data,
            string dataType = "object",
            string colorHex = "#007ACC")
        {
            PortName = portName;
            PortType = portType;
            Category = category;
            DataType = dataType;
            ColorHex = colorHex;
        }
    }
}
