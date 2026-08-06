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
    }
}