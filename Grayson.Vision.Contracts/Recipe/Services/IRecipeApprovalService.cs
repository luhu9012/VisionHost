//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 配方审批状态机服务契约。
//        简化版审批：不做电子签名（ElectronicSignature），仅记录操作人与时间戳，
//        满足基本 GxP 追溯要求。审批通过的配方可被管理员再次驳回进入可编辑状态。
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Permission;
using Grayson.Vision.Contracts.Recipe.Models;

namespace Grayson.Vision.Contracts.Recipe.Services
{
    /// <summary>
    /// 配方审批状态机服务：负责 Draft / PendingApproval / Approved / Rejected
    /// 之间的状态迁移校验、审批流水记录与持久化。
    /// </summary>
    public interface IRecipeApprovalService
    {
        /// <summary>
        /// 是否可以提交审批（Draft/Rejected → PendingApproval，需 Engineer+ 且元数据完整）。
        /// </summary>
        /// <returns>返回 null 表示允许；否则返回不可提交的原因文本。</returns>
        string CanSubmit(RecipeModel recipe, UserRole role);

        /// <summary>
        /// 是否可以审批通过（PendingApproval → Approved，仅 Administrator）。
        /// </summary>
        /// <returns>返回 null 表示允许；否则返回不可通过的原因文本。</returns>
        string CanApprove(RecipeModel recipe, UserRole role);

        /// <summary>
        /// 是否可以驳回（PendingApproval 或 Approved → Rejected，仅 Administrator）。
        /// 驳回后配方回退到可编辑状态，可修改后再次提交审批。
        /// </summary>
        /// <returns>返回 null 表示允许；否则返回不可驳回的原因文本。</returns>
        string CanReject(RecipeModel recipe, UserRole role);

        /// <summary>
        /// 提交审批：Draft/Rejected → PendingApproval，并记录审批流水与持久化。
        /// </summary>
        /// <returns>是否提交成功。</returns>
        bool SubmitForApproval(RecipeModel recipe, string operatorName, string comment = null);

        /// <summary>
        /// 审批通过：PendingApproval → Approved（可下发工位），并记录审批流水与持久化。
        /// </summary>
        /// <returns>是否审批成功。</returns>
        bool Approve(RecipeModel recipe, string operatorName, string comment = null);

        /// <summary>
        /// 驳回：PendingApproval/Approved → Rejected（回退可编辑、可再次提交），并记录审批流水与持久化。
        /// </summary>
        /// <returns>是否驳回成功。</returns>
        bool Reject(RecipeModel recipe, string operatorName, string reason = null);
    }
}
