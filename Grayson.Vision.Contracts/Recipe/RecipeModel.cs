using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Business.Models;

namespace Grayson.Vision.Contracts.Recipe.Models
{
    /// <summary>
    /// 工厂/工位级配方契约模型
    /// </summary>
    public class RecipeModel
    {
        #region 1. 基础元数据 (Meta Info)
        /// <summary>
        /// 配方唯一标识 GUID
        /// </summary>
        public string RecipeId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 配方编号/代号 (如: REC-20260803-001)
        /// </summary>
        public string RecipeCode { get; set; }

        /// <summary>
        /// 配方名称 (如: 瓶盖缺陷检测配方_A)
        /// </summary>
        public string RecipeName { get; set; }

        /// <summary>
        /// 产品类别/型号 (如: Product_350ml_Bottle)
        /// </summary>
        public string ProductCategory { get; set; }

        /// <summary>
        /// 配方版本号 (语义化版本，如: 1.0.2)
        /// </summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// 是否作为工位当前默认/激活配方
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// 备注与版本变更说明
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 创建人 / 修改人
        /// </summary>
        public string Author { get; set; }

        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime LastModifiedTime { get; set; } = DateTime.Now;
        #endregion

        #region 2. 核心业务流数据 (Workflow / Execution Data)
        /// <summary>
        /// 主工作流程定义（包含节点拓扑、连接关系与参数）
        /// </summary>
        public FlowProcessModel MainProcess { get; set; }

        /// <summary>
        /// 可选：子流程/并行流程字典 (Key: ProcessId/Name)
        /// </summary>
        public Dictionary<string, FlowProcessModel> SubProcesses { get; set; }
            = new Dictionary<string, FlowProcessModel>();
        #endregion
    }
}