namespace Grayson.Vision.Contracts.Recipe.Enums
{
    /// <summary>
    /// 配方审批与发布状态
    /// </summary>
    public enum RecipeApprovalStatus
    {
        /// <summary>草稿：仅可编辑，不可下发到生产工位</summary>
        Draft,

        /// <summary>待审批：已提交，等待有权限人员审批</summary>
        PendingApproval,

        /// <summary>已审批：可用于生产下发</summary>
        Approved,

        /// <summary>已驳回：审批不通过，退回修改</summary>
        Rejected,

        /// <summary>已冻结：因产品迭代或质量问题暂停使用</summary>
        Frozen,

        /// <summary>已归档：历史版本，仅可查看/复制</summary>
        Archived,

        /// <summary>已废弃：不再使用</summary>
        Deprecated
    }
}
