using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Recipe.Models
{
    /// <summary>
    /// 工艺参数集：与具体产品/工艺绑定。
    /// 蓝图节点中的参数 placeholder 会从此处取值；切换产品只切换工艺参数集，不修改画布。
    /// ⚠ 2026-09-05 对齐决策：删除孤儿标量 AiModelPath / CalibrationDataPath / ProductCategory / Version——
    ///   模型路径与标定矩阵路径的真身是各流程节点的参数（如 CalibrationApply.HomMatFilePath，
    ///   由标定中心发布自动改写），在参数集里再存一份即"编辑即幻觉"；产品类别/版本由配方顶层
    ///   元数据统一维护（单一真相）。参数集保留 Name 标识 + Parameters 字典（未来 placeholder 取值表）。
    /// </summary>
    public class ProcessParameterSet
    {
        /// <summary>参数集 ID</summary>
        public string ParameterSetId { get; set; }

        /// <summary>参数集名称，例如 "A产品_白色瓶盖"</summary>
        public string Name { get; set; }

        /// <summary>
        /// 工艺参数：Key = "节点ID_参数名" 或 "全局参数名"，Value = 参数值。
        /// 例如 {"ExposureNode_ExposureTime", 8000}, {"ThresholdNode_Threshold", 128}
        /// </summary>
        public Dictionary<string, object> Parameters { get; set; }
            = new Dictionary<string, object>();
    }
}
