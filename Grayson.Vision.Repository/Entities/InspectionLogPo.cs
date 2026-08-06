// Entities/InspectionLogPo.cs
using Grayson.Vision.Repository.Core;
using System;

namespace Grayson.Vision.Repository.Entities
{
    /// <summary>
    /// 节拍检测与统计数据表
    /// </summary>
    public class InspectionLogPo : BaseEntity
    {
        public string StationId { get; set; }
        public string BatchId { get; set; }
        public string RecipeName { get; set; }
        public bool IsOk { get; set; }                // 最终检测结果 (OK / NG)
        public double CycleTimeMs { get; set; }       // 耗时毫秒
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string ImagePath { get; set; }         // 存盘图片物理路径
        public DateTime InspectTime { get; set; } = DateTime.Now;
    }
}