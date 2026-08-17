using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Recipe.Enums;

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

        /// <summary>
        /// 设备在配方中的角色：主设备 / 备用 / 校准 / 参考。
        /// </summary>
        public DeviceRole Role { get; set; } = DeviceRole.Primary;

        private string _mappedDeviceId;
        public string MappedDeviceId
        {
            get => _mappedDeviceId;
            set
            {
                if (Set(ref _mappedDeviceId, value))
                {
                    OnPropertyChanged(nameof(IsBoundToPhysical));
                }
            }
        }

        /// <summary>
        /// 是否已绑定到物理设备（用于 UI 映射状态指示）。
        /// </summary>
        public bool IsBoundToPhysical => !string.IsNullOrWhiteSpace(_mappedDeviceId);
    }
}