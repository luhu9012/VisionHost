using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Recipe.Models
{
    /// <summary>
    /// 工艺参数集：与具体产品/工艺绑定。
    /// 蓝图节点中的参数 placeholder 会从此处取值；切换产品只切换工艺参数集，不修改画布。
    /// </summary>
    public class ProcessParameterSet
    {
        /// <summary>参数集 ID</summary>
        public string ParameterSetId { get; set; }

        /// <summary>参数集名称，例如 "A产品_白色瓶盖"</summary>
        public string Name { get; set; }

        /// <summary>适用产品类别</summary>
        public string ProductCategory { get; set; }

        /// <summary>版本号</summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// 工艺参数：Key = "节点ID_参数名" 或 "全局参数名"，Value = 参数值。
        /// 例如 {"ExposureNode_ExposureTime", 8000}, {"ThresholdNode_Threshold", 128}
        /// </summary>
        public Dictionary<string, object> Parameters { get; set; }
            = new Dictionary<string, object>();

        /// <summary>AI 模型文件路径（如果工艺需要）</summary>
        public string AiModelPath { get; set; }

        /// <summary>标定文件/结果路径</summary>
        public string CalibrationDataPath { get; set; }
    }
}
