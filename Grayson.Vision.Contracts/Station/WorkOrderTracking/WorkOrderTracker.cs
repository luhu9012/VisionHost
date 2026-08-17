using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Station.WorkOrderTracking
{
    /// <summary>
    /// 工单追踪器抽象，用于 UI/上层读取最近工单历史而不依赖 Core 实现。
    /// </summary>
    public interface IWorkOrderTracker
    {
        IReadOnlyList<WorkOrderSnapshot> GetRecentWorkOrders(int count = 50);
    }

    /// <summary>
    /// 工单只读快照，供追溯/展示使用。
    /// </summary>
    public class WorkOrderSnapshot
    {
        public string WorkOrderId { get; set; }
        public string StationId { get; set; }
        public string BatchId { get; set; }
        public string RecipeName { get; set; }
        public string StatusText { get; set; }
        public bool? IsOk { get; set; }
        public double CycleTimeMs { get; set; }
        public string ImagePath { get; set; }
        public string ErrorMessage { get; set; }
        public System.DateTime CreatedAt { get; set; }

        /// <summary>
        /// 关键图像存储路径集合（如检测原始图、结果叠加图）。
        /// </summary>
        public System.Collections.Generic.List<string> ImagePaths { get; set; }
            = new System.Collections.Generic.List<string>();
    }
}
