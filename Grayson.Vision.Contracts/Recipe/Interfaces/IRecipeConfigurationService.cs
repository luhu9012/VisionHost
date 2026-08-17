using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Recipe.Models;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Recipe.Interfaces
{
    /// <summary>
    /// 配方配置服务契约（Core 层实现，UI 层委托调用）。
    /// 负责业务蓝图、工位运行配置、工艺参数集的加载与聚合。
    /// </summary>
    public interface IRecipeConfigurationService
    {
        Task<Result<RecipeModel>> LoadActiveRecipeAsync(string stationId);

        Task<Result> SaveRecipeAsync(RecipeModel recipe);

        Task<Result<ProcessParameterSet>> LoadProcessParameterSetAsync(string parameterSetId);

        Task<Result> SaveProcessParameterSetAsync(ProcessParameterSet parameterSet);
    }
}
