using Grayson.Vision.Contracts.Recipe.Models;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Recipe.Services
{
    public interface IRecipeStorageService
    {
        List<RecipeModel> GetAllRecipes();
        RecipeModel LoadRecipe(string recipeIdOrPath);
        bool SaveRecipe(RecipeModel recipe);
        bool DeleteRecipe(string recipeId);
        /// <summary>
        /// 自动扫描并清理磁盘上的损坏文件、无法解析的非法 JSON 以及孤儿冗余文件
        /// </summary>
        /// <returns>清理掉的脏数据文件路径列表</returns>
        List<string> CleanupGarbageRecipes();
    }
}