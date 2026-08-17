using Grayson.Vision.Contracts.Recipe.Services;

namespace Grayson.Vision.Repository.Services
{
    public static class RecipeStorageFactory
    {
        public static IRecipeStorageService CreateRecipeStorageService() => new RecipeStorageService();
    }
}
