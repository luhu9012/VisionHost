using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Validation;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Core.Flow.Validation
{
    /// <summary>
    /// 默认端口类型校验器：相同类型兼容，Unknown 兼容任意类型（兼容旧数据）。
    /// </summary>
    public class DefaultPortTypeValidator : IPortTypeValidator
    {
        private static readonly Dictionary<string, PortDataType> TypeMap
            = new Dictionary<string, PortDataType>(StringComparer.OrdinalIgnoreCase)
            {
                ["image"] = PortDataType.Image,
                ["himage"] = PortDataType.Image,
                ["halconimage"] = PortDataType.Image,
                ["bitmap"] = PortDataType.Image,
                ["double"] = PortDataType.Double,
                ["float"] = PortDataType.Double,
                ["single"] = PortDataType.Double,
                ["int"] = PortDataType.Double,
                ["point2d"] = PortDataType.Point2D,
                ["point"] = PortDataType.Point2D,
                ["point3d"] = PortDataType.Point3D,
                ["robotpose"] = PortDataType.RobotPose,
                ["pose"] = PortDataType.RobotPose,
                ["bytearray"] = PortDataType.ByteArray,
                ["byte[]"] = PortDataType.ByteArray,
                ["bool"] = PortDataType.Boolean,
                ["boolean"] = PortDataType.Boolean,
                ["string"] = PortDataType.String,
                ["object"] = PortDataType.Unknown
            };

        public bool IsCompatible(PortDataType sourceType, PortDataType targetType)
        {
            if (sourceType == PortDataType.Unknown || targetType == PortDataType.Unknown)
                return true;

            return sourceType == targetType;
        }

        public PortDataType Parse(string dataType, PortDataType defaultType = PortDataType.Unknown)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return defaultType;
            if (TypeMap.TryGetValue(dataType.Trim(), out var parsed)) return parsed;

            if (Enum.TryParse<PortDataType>(dataType.Trim(), true, out var enumValue))
                return enumValue;

            return defaultType;
        }
    }
}
