using System;
using Grayson.Vision.Contracts.Recipe.Enums;

namespace Grayson.Vision.Contracts.Recipe.Traceability
{
    /// <summary>
    /// 配方在某一时刻的不可变追溯快照。
    /// 用于工单绑定、审计日志、版本回滚基准。
    /// </summary>
    public class RecipeTraceabilityEntry
    {
        /// <summary>配方唯一标识</summary>
        public string RecipeId { get; set; }

        /// <summary>配方编号</summary>
        public string RecipeCode { get; set; }

        /// <summary>配方名称</summary>
        public string RecipeName { get; set; }

        /// <summary>配方版本</summary>
        public string Version { get; set; }

        /// <summary>作者/创建人</summary>
        public string Author { get; set; }

        /// <summary>审批状态</summary>
        public RecipeApprovalStatus ApprovalStatus { get; set; }

        /// <summary>审批人</summary>
        public string ApprovedBy { get; set; }

        /// <summary>审批时间</summary>
        public DateTime? ApprovedAt { get; set; }

        /// <summary>快照创建时间</summary>
        public DateTime CapturedAt { get; set; }
    }
}
