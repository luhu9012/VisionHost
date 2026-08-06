using Grayson.Vision.Contracts.Infrastructure.Mvvm;

namespace Grayson.Vision.Contracts.Recipe.Models
{
    /// <summary>
    /// 配方逻辑设备映射模型
    /// 用于声明配方流程执行所依赖的逻辑设备节点与其规格要求
    /// </summary>
    public class RecipeDeviceMappingModel : ViewModelBase
    {
        public string LogicalDeviceId { get; set; }
        public string LogicalDeviceName { get; set; }
        public string LogicalDeviceType { get; set; }
        public string RequiredSpec { get; set; }

        private string _mappedDeviceId;
        public string MappedDeviceId
        {
            get => _mappedDeviceId;
            set => Set(ref _mappedDeviceId, value);
        }
    }
}