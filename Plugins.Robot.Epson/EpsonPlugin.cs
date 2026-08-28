using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
// 解决 IDevice 命名空间冲突
using IContractDevice = Grayson.Vision.Contracts.Devices.IDevice;

namespace Plugins.Robot.Epson
{
    /// <summary>
    /// Epson 机器人插件工厂。
    ///
    /// 插件发现机制：程序集名 Plugins.Robot.Epson 含 "Plugin" 字样，
    /// 启动时被 DevicePluginManager 反射扫描加载；
    /// Supports(MotionCard, "Epson") 命中后由本工厂创建设备实例。
    ///
    /// 与相机插件不同：机器人不通过 USB/GigE 枚举发现，而是连接
    /// RC+ 控制器（IP 地址或本机 RC+ 仿真）。因此：
    /// - EnumerateDevices 返回常见控制器地址候选（本机 192.168.1.10 +
    ///   localhost），用户在设备管理界面选择实际使用的地址；
    /// - CreateDevice(deviceId) 中 deviceId 即控制器地址，EpsonRobot
    ///   在 Connect 时用它建立 RC+ 连接。
    /// </summary>
    public class EpsonPlugin : IHardwarePlugin
    {
        public string BrandName => "Epson";
        public DeviceCategory Category => DeviceCategory.MotionCard;
        public string Version => "1.0.0";

        /// <summary>品牌专用插件高优先级，优先于通用协议插件接管</summary>
        public int Priority => 100;

        public bool Supports(DeviceCategory category, string brand)
        {
            return category == DeviceCategory.MotionCard &&
                   string.Equals(brand, BrandName, StringComparison.OrdinalIgnoreCase);
        }

        public void Initialize() { }

        /// <summary>
        /// 枚举控制器候选地址。
        ///
        /// 真实网络中的控制器 IP 请按现场实际修改/新增——
        /// 也可以在设备管理界面手动录入任意 IP 作为 DeviceId，
        /// 本列表仅提供快捷选项。
        /// </summary>
        public Result<List<DeviceInfo>> EnumerateDevices()
        {
            try
            {
                var list = new List<DeviceInfo>
                {
                    // 常见默认：RC+ 控制器出厂常用网段
                    new DeviceInfo
                    {
                        DeviceId = "192.168.1.10",
                        ModelName = "Epson RC+ Controller",
                        Category = Category,
                        BrandName = BrandName
                    },
                    // 本机 RC+（开发调试：在装 RC+ 的电脑上运行）
                    new DeviceInfo
                    {
                        DeviceId = "localhost",
                        ModelName = "Epson RC+ (Local)",
                        Category = Category,
                        BrandName = BrandName
                    }
                };

                return Result<List<DeviceInfo>>.Ok(list);
            }
            catch (Exception ex)
            {
                return Result<List<DeviceInfo>>.Fail($"枚举 Epson 控制器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 按控制器地址创建设备实例。
        /// deviceId 为 RC+ 控制器 IP（如 192.168.1.10）或 "localhost"；
        /// 无论 UI 刚扫描创建，还是 LiteDB 离线恢复，均返回可用 IDevice。
        /// </summary>
        public IContractDevice CreateDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;

            var device = new EpsonRobot(deviceId)
            {
                BrandName = BrandName,
                Category = Category
            };

            // 默认初始参数（供属性面板展示/覆盖）
            device.SetParam("Speed", 100.0);            // 整体速度百分比（0~100）
            device.SetParam("Accel", 100.0);            // 整体加速度百分比
            device.SetParam("AxisCount", EpsonRobot.AxisCount); // 4 轴 SCARA

            return device;
        }

        public void Shutdown() { }
    }
}
