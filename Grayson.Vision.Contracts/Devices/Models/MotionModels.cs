//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 运动控制设备模型
//===================================================================================

namespace Grayson.Vision.Contracts.Devices.Models
{
    /// <summary>
    /// 轴信息模型 - 代表运动控制卡上的单个轴
    /// </summary>
    public class AxisInfoModel
    {
        /// <summary>轴索引（从 0 开始）</summary>
        public int AxisIndex { get; set; }

        /// <summary>轴名称（如"X轴"、"Z轴"）</summary>
        public string AxisName { get; set; }
    }
}
