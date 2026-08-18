using Grayson.Vision.Repository.Entities;

namespace Grayson.Vision.Repository.Interfaces
{
    public interface IRecipeRepository : IRepository<RecipePo, string>
    {
        RecipePo GetByName(string recipeName);
        RecipePo GetActiveRecipe(string productCategory);
        bool SetActive(string id);
    }

    public interface ICalibrationProfileRepository : IRepository<CalibrationProfilePo, string>
    {
        CalibrationProfilePo GetByName(string profileName);
        CalibrationProfilePo GetByStationCode(string stationCode);
    }
}
