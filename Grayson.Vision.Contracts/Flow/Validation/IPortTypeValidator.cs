using Grayson.Vision.Contracts.Flow.Enums;

namespace Grayson.Vision.Contracts.Flow.Validation
{
    /// <summary>
    /// 端口类型兼容性校验器
    /// </summary>
    public interface IPortTypeValidator
    {
        /// <summary>
        /// 检查源端口类型是否能连接/灌入目标端口类型。
        /// </summary>
        bool IsCompatible(PortDataType sourceType, PortDataType targetType);

        /// <summary>
        /// 将 string DataType 解析为枚举。
        /// </summary>
        PortDataType Parse(string dataType, PortDataType defaultType = PortDataType.Unknown);
    }
}
