using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Recipe.Enums;

namespace Grayson.Vision.Contracts.Recipe.Models
{
    /// <summary>
    /// 配方审批记录的详细信息。
    /// 满足 GxP / 21 CFR Part 11 等可追溯场景要求。
    /// </summary>
    public class RecipeApprovalInfo
    {
        /// <summary>提交人</summary>
        public string SubmittedBy { get; set; }

        /// <summary>提交时间</summary>
        public DateTime? SubmittedAt { get; set; }

        /// <summary>审批人</summary>
        public string ApprovedBy { get; set; }

        /// <summary>审批时间</summary>
        public DateTime? ApprovedAt { get; set; }

        /// <summary>最近一次审批动作时间（提交/批准/驳回均会更新）</summary>
        public DateTime? LastActionAt { get; set; }

        /// <summary>审批意见 / 变更说明</summary>
        public string ApprovalComment { get; set; }

        /// <summary>
        /// 驳回原因（仅 Rejected 状态时写入，便于返工）
        /// </summary>
        public string RejectionReason { get; set; }

        /// <summary>
        /// 电子签名记录，满足合规场景下的用户身份绑定。
        /// </summary>
        public string ElectronicSignature { get; set; }

        /// <summary>
        /// 审批历史流水，记录每一次提交/批准/驳回动作，用于审计。
        /// </summary>
        public List<RecipeApprovalHistoryEntry> History { get; set; } = new List<RecipeApprovalHistoryEntry>();
    }

    /// <summary>
    /// 配方审批流水中的一次动作记录。
    /// </summary>
    public class RecipeApprovalHistoryEntry
    {
        /// <summary>动作时间</summary>
        public DateTime ActionAt { get; set; }

        /// <summary>执行人</summary>
        public string ActionBy { get; set; }

        /// <summary>动作类型：Submitted/Approved/Rejected</summary>
        public RecipeApprovalStatus Action { get; set; }

        /// <summary>备注/意见/原因</summary>
        public string Comment { get; set; }

        /// <summary>电子签名</summary>
        public string ElectronicSignature { get; set; }
    }
}
