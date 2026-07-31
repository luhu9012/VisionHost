using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Recipe
{
    /// <summary>配方、工位配置持久化读写接口，宿主用JSON/LiteDB实现</summary>
    public interface IRecipeProvider
    {
        /// <summary>保存全部工位全局配置（重启恢复用）</summary>
        Result SaveStationConfig(List<StationConfigModel> allStations);

        /// <summary>加载所有工位配置</summary>
        Result<List<StationConfigModel>> LoadAllStationConfigs();

        /// <summary>保存单条产品配方</summary>
        Result SaveRecipe(RecipeRootModel recipe);

        /// <summary>根据ID读取配方</summary>
        Result<RecipeRootModel> LoadRecipe(string recipeId);

        Result DeleteRecipe(string recipeId);

        /// <summary>获取全部配方ID清单</summary>
        Result<List<string>> GetAllRecipeIds();
    }
}