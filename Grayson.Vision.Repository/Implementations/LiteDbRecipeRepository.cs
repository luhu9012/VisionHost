// Implementations/LiteDbRecipeRepository.cs
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using System.Linq;

namespace Grayson.Vision.Repository.Implementations
{
    public class LiteDbRecipeRepository : LiteDbRepositoryBase<RecipePo>, IRecipeRepository
    {
        protected override string CollectionName => "recipes";

        public RecipePo GetByName(string recipeName)
        {
            return Find(x => x.RecipeName == recipeName).FirstOrDefault();
        }

        public RecipePo GetActiveRecipe(string productCategory)
        {
            return Find(x => x.ProductCategory == productCategory && x.IsActive).FirstOrDefault();
        }

        public bool SetActive(string id)
        {
            var recipe = GetById(id);
            if (recipe == null) return false;

            // 将同一类别的其他配方置为不激活
            var sameCategory = Find(x => x.ProductCategory == recipe.ProductCategory);
            foreach (var r in sameCategory)
            {
                if (r.IsActive)
                {
                    r.IsActive = false;
                    Update(r);
                }
            }

            recipe.IsActive = true;
            return Update(recipe);
        }
    }
}