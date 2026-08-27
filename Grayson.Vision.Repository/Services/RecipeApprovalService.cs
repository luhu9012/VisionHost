using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Infrastructure.Permission;
using Grayson.Vision.Contracts.Recipe.Enums;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Recipe.Services;

namespace Grayson.Vision.Repository.Services
{
    /// <summary>
    /// 配方审批状态机服务实现（简化版）：
    /// - 不做电子签名，仅记录操作人与时间戳；
    /// - 状态迁移校验集中在服务内，UI 不再自行维护审批状态机；
    /// - 已审批（Approved）配方可被管理员再次驳回（→ Rejected），回退到可编辑状态后重新提交。
    /// </summary>
    public class RecipeApprovalService : IRecipeApprovalService
    {
        private readonly IRecipeStorageService _recipeStorage;

        public RecipeApprovalService(IRecipeStorageService recipeStorage = null)
        {
            _recipeStorage = recipeStorage ?? RecipeStorageFactory.CreateRecipeStorageService();
        }

        public string CanSubmit(RecipeModel recipe, UserRole role)
        {
            if (recipe == null) return "未选择配方";
            if (role < UserRole.Engineer) return "工程师及以上角色才能提交审批";
            if (!recipe.HasMinimumMetadata)
                return "请完善配方名称、编号、产品类别和版本号后再提交审批";
            if (recipe.ApprovalStatus != RecipeApprovalStatus.Draft &&
                recipe.ApprovalStatus != RecipeApprovalStatus.Rejected)
                return $"当前状态[{recipe.ApprovalStatus}]不可提交审批";
            return null;
        }

        public string CanApprove(RecipeModel recipe, UserRole role)
        {
            if (recipe == null) return "未选择配方";
            if (role != UserRole.Administrator) return "仅管理员可审批通过";
            if (recipe.ApprovalStatus != RecipeApprovalStatus.PendingApproval)
                return "仅待审批状态的配方可通过审批";
            return null;
        }

        public string CanReject(RecipeModel recipe, UserRole role)
        {
            if (recipe == null) return "未选择配方";
            if (role != UserRole.Administrator) return "仅管理员可驳回配方";
            if (recipe.ApprovalStatus != RecipeApprovalStatus.PendingApproval &&
                recipe.ApprovalStatus != RecipeApprovalStatus.Approved)
                return "仅待审批或已审批状态的配方可被驳回";
            return null;
        }

        public bool SubmitForApproval(RecipeModel recipe, string operatorName, string comment = null)
        {
            if (recipe == null) return false;

            var info = EnsureApprovalInfo(recipe);
            recipe.ApprovalStatus = RecipeApprovalStatus.PendingApproval;
            info.SubmittedBy = operatorName ?? "未知用户";
            info.SubmittedAt = DateTime.Now;
            info.LastActionAt = DateTime.Now;
            if (!string.IsNullOrEmpty(comment)) info.ApprovalComment = comment;
            AddHistory(recipe, RecipeApprovalStatus.PendingApproval, operatorName, comment);
            return _recipeStorage.SaveRecipe(recipe);
        }

        public bool Approve(RecipeModel recipe, string operatorName, string comment = null)
        {
            if (recipe == null) return false;

            var info = EnsureApprovalInfo(recipe);
            recipe.ApprovalStatus = RecipeApprovalStatus.Approved;
            info.ApprovedBy = operatorName ?? "未知用户";
            info.ApprovedAt = DateTime.Now;
            info.LastActionAt = DateTime.Now;
            info.RejectionReason = null;
            recipe.EffectiveFrom = DateTime.Now;   // 审批通过即刻生效
            recipe.IsLocked = false;               // 保持可被驳回（驳回后回退可编辑）
            AddHistory(recipe, RecipeApprovalStatus.Approved, operatorName, comment);
            return _recipeStorage.SaveRecipe(recipe);
        }

        public bool Reject(RecipeModel recipe, string operatorName, string reason = null)
        {
            if (recipe == null) return false;

            var info = EnsureApprovalInfo(recipe);
            recipe.ApprovalStatus = RecipeApprovalStatus.Rejected;
            info.ApprovedBy = null;
            info.ApprovedAt = null;
            info.LastActionAt = DateTime.Now;
            info.RejectionReason = reason;
            recipe.EffectiveFrom = null;
            recipe.IsLocked = false;               // 解锁，允许编辑后重新提交
            AddHistory(recipe, RecipeApprovalStatus.Rejected, operatorName, reason);
            return _recipeStorage.SaveRecipe(recipe);
        }

        private static RecipeApprovalInfo EnsureApprovalInfo(RecipeModel recipe)
        {
            if (recipe.ApprovalInfo == null)
                recipe.ApprovalInfo = new RecipeApprovalInfo();
            if (recipe.ApprovalInfo.History == null)
                recipe.ApprovalInfo.History = new List<RecipeApprovalHistoryEntry>();
            return recipe.ApprovalInfo;
        }

        private static void AddHistory(RecipeModel recipe, RecipeApprovalStatus action, string operatorName, string comment)
        {
            var info = EnsureApprovalInfo(recipe);
            info.History.Add(new RecipeApprovalHistoryEntry
            {
                ActionAt = DateTime.Now,
                ActionBy = operatorName ?? "未知用户",
                Action = action,
                Comment = comment
            });
        }
    }
}
