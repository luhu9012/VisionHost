using System.Collections.Generic;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Core;


namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>
    /// 所有硬件驱动插件必须实现的工厂接口
    /// </summary>
    public interface IHardwarePlugin
    {
        /// <summary>插件唯一标识/品牌名称，如 "Hikvision", "Basler", "Siemens", "Gooogol"</summary>
        string BrandName { get; }

        /// <summary>插件支持的设备大类（相机、PLC、运动卡等）</summary>
        DeviceCategory Category { get; }

        /// <summary>插件版本号</summary>
        string Version { get; }
        /// <summary>
        /// 插件匹配优先级（数值越大越优先被匹配，如 SDK 插件为 100，通用兜底插件为 -100）
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// 判断该插件是否能够处理指定类别与品牌的设备
        /// </summary>
        bool Supports(DeviceCategory category, string brand);

        /// <summary>初始化插件环境（加载品牌 C/C++ 原生 SDK 等）</summary>
        void Initialize();

        /// <summary>
        /// 扫描/搜索当前系统连接的该品牌硬件设备
        /// </summary>
        Result<List<DeviceInfo>> EnumerateDevices();

        /// <summary>
        /// 根据扫描得到的 DeviceId 创建具体的硬件实例（返回 IDevice 抽象）
        /// </summary>
        IDevice CreateDevice(string deviceId);

        /// <summary>卸载插件，释放底层资源</summary>
        void Shutdown();
    }

    /// <summary>
    /// 统一设备扫描元数据
    /// </summary>
    public class DeviceInfo
    {
        public string DeviceId { get; set; }   // 硬件唯一标识 (如相机SN、PLC IP地址、串口号、卡号)
        public string ModelName { get; set; }  // 设备型号 (如 "MV-CA060-10GM", "S7-1200")
        public DeviceCategory Category { get; set; } // 设备类型
        public string BrandName { get; set; }  // 品牌名称

        public object ExtraInfo { get; set; }  // 额外信息 (如 IP、端口、通道号等)
    }
}