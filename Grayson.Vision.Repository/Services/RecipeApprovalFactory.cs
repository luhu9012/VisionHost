using Grayson.Vision.Contracts.Recipe.Services;

namespace Grayson.Vision.Repository.Services
{
    /// <summary>
    /// 配方审批服务工厂。
    /// </summary>
    public static class RecipeApprovalFactory
    {
        public static IRecipeApprovalService CreateRecipeApprovalService() => new RecipeApprovalService();
    }
}
