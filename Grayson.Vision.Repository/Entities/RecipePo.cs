// Entities/RecipePo.cs
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Repository.Core;

namespace Grayson.Vision.Repository.Entities
{
    public class RecipePo : BaseEntity
    {
        public string RecipeCode { get; set; }
        public string RecipeName { get; set; }
        public string ProductCategory { get; set; }
        public string Version { get; set; }
        public bool IsActive { get; set; }
        public string Description { get; set; }

        /// <summary>
        /// 直接包含 Contracts 层定义的 RecipeModel 完整结构
        /// </summary>
        public RecipeModel Model { get; set; }
    }

    public class CalibrationProfilePo : BaseEntity
    {
        public string ProfileName { get; set; }
        public CalibrationQuantity CalibrationQuantity { get; set; }
        public string BoundStationCode { get; set; }
        public string BoundDeviceId { get; set; }
        public bool IsCalibrated { get; set; }
        public CalibrationProfile Model { get; set; }
    }
}
