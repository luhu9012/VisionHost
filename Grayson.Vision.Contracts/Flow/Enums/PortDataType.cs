namespace Grayson.Vision.Contracts.Flow.Enums
{
    /// <summary>
    /// 数据端口强类型约束枚举（兼容现有 string DataType）。
    /// </summary>
    public enum PortDataType
    {
        /// <summary>未知/未指定</summary>
        Unknown = 0,

        /// <summary>图像</summary>
        Image = 1,

        /// <summary>double/float 等数值</summary>
        Double = 2,

        /// <summary>二维点</summary>
        Point2D = 3,

        /// <summary>三维点</summary>
        Point3D = 4,

        /// <summary>机器人位姿</summary>
        RobotPose = 5,

        /// <summary>字节数组</summary>
        ByteArray = 6,

        /// <summary>布尔</summary>
        Boolean = 7,

        /// <summary>字符串</summary>
        String = 8
    }
}
