using System;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 一次工单的质量统计信息，用于 SPC / OEE / 良率报表。
    /// </summary>
    public class WorkOrderQualityData
    {
        /// <summary>检测区域/ROI 数量</summary>
        public int InspectionCount { get; set; }

        /// <summary>缺陷数量</summary>
        public int DefectCount { get; set; }

        /// <summary>置信度/评分（如 AI 模型输出）</summary>
        public double ConfidenceScore { get; set; }

        /// <summary>处理耗时（毫秒）</summary>
        public double ProcessingTimeMs { get; set; }

        /// <summary>采集耗时（毫秒）</summary>
        public double AcquisitionTimeMs { get; set; }

        /// <summary>记录生成时间</summary>
        public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    }
}
