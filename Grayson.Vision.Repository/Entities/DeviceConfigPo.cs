using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Repository.Core;

namespace Grayson.Vision.Repository.Entities
{
    /// <summary>
    /// 已注册设备的持久化配置实体 (PO/DO)
    /// </summary>
    public class DeviceConfigPo : BaseEntity
    {
        /// <summary>
        /// 用户定义的逻辑唯一名称 (如 Cam_Top_01, Plc_Line_01)
        /// 作为业务层访问设备的唯一标识，用于配方绑定，不随硬件SN变化
        /// </summary>
        public string DeviceKey { get; set; }

        /// <summary>
        /// 物理硬件ID (如相机SN、PLC IP地址、运动卡卡号)
        /// </summary>
        public string DeviceId { get; set; }

        /// <summary>
        /// 硬件品牌名称 (如 Hikvision, Basler, Siemens, Gooogol)
        /// 用于反查对应插件
        /// </summary>
        public string BrandName { get; set; }

        /// <summary>
        /// 设备大类 (Camera, PLC, MotionCard)
        /// </summary>
        public DeviceCategory Category { get; set; }

        /// <summary>
        /// 是否启用 (保留属性，用于软删除或临时禁用)
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 设备的扩展连接参数 (JSON字符串存储，如 IP=192.168.1.10;Port=502)
        /// TODO: 后续深化设备参数保存时使用
        /// </summary>
        public string ConnectionString { get; set; }
    }
}