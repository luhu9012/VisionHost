using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 一次工单的执行结果数据：OK/NG、缺陷分类、测量值、图像路径等。
    /// </summary>
    public class WorkOrderResultData
    {
        /// <summary>的总体结果，true 表示 OK，false 表示 NG。</summary>
        public bool IsOk { get; set; }

        /// <summary>缺陷分类/NG 原因代码，例如 "SCRATCH", "MISSING_PART"。</summary>
        public List<string> DefectCodes { get; set; } = new List<string>();

        /// <summary>测量项结果集合，例如 {"Length": 12.34, "Diameter": 8.5}。</summary>
        public Dictionary<string, double> Measurements { get; set; } = new Dictionary<string, double>();

        /// <summary>关键图像存储路径（如检测原始图、结果叠加图）。</summary>
        public List<string> ImagePaths { get; set; } = new List<string>();

        /// <summary>本次工单执行耗时（毫秒）；未设置时 UI 可用 WorkOrder.Elapsed 近似。</summary>
        public double CycleTimeMs { get; set; }

        /// <summary>错误/NG 说明。</summary>
        public string ErrorMessage { get; set; }

        /// <summary>结果说明/操作员备注。</summary>
        public string ResultComment { get; set; }
    }
}
