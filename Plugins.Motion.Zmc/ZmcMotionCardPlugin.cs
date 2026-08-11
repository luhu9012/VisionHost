using cszmcaux;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace Plugins.Motion.Zmc
{
    /// <summary>
    /// 正运动 (ZMC) 硬件驱动插件工厂实现
    /// </summary>
    public class ZmcHardwarePlugin : IHardwarePlugin
    {
        public string BrandName => "ZMC";

        // 1. 修正设备类别为 MotionCard
        public DeviceCategory Category => DeviceCategory.MotionCard;

        public string Version => "1.0.0";
        public int Priority => 100;

        public bool Supports(DeviceCategory category, string brand)
        {
            return category == DeviceCategory.MotionCard &&
                   string.Equals(brand, BrandName, StringComparison.OrdinalIgnoreCase);
        }

        public void Initialize()
        {
            // SDK 环境初始化
        }

        /// <summary>
        /// 扫描系统当前连接的正运动控制卡
        /// </summary>
        public Result<List<DeviceInfo>> EnumerateDevices()
        {
            var devices = new List<DeviceInfo>();

            try
            {
                // 2. 匹配 ZAux_SearchEthlist(StringBuilder ipaddrlist, uint addrbufflength, uint uims) 签名
                uint bufferSize = 1024;
                uint timeoutMs = 2000;
                StringBuilder ipBuffer = new StringBuilder((int)bufferSize);

                int ret = zmcaux.ZAux_SearchEthlist(ipBuffer, bufferSize, timeoutMs);
                if (ret == 0)
                {
                    string ethData = ipBuffer.ToString().Trim('\0', ' ');
                    if (!string.IsNullOrWhiteSpace(ethData))
                    {
                        // 搜索到的 IP 格式通常以 '\0'、空格或逗号隔开
                        string[] ips = ethData.Split(new[] { ' ', ',', '\r', '\n', '\t', '\0' }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (var ip in ips)
                        {
                            string cleanIp = ip.Trim();
                            if (string.IsNullOrEmpty(cleanIp)) continue;

                            devices.Add(new DeviceInfo
                            {
                                DeviceId = cleanIp,
                                ModelName = "ZMC Ethernet Motion Controller",
                                Category = DeviceCategory.MotionCard,
                                BrandName = BrandName,
                                ExtraInfo = new ZmcConnectionOptions
                                {
                                    ConnectionType = ZmcConnectionType.Ethernet,
                                    IpAddress = cleanIp,
                                    Port = 8080
                                }
                            });
                        }
                    }
                }

                // 若未自动搜索到网口卡，生成默认回退连接项
                if (devices.Count == 0)
                {
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = "192.168.3.11",
                        ModelName = "ZMC Eth Controller (Default)",
                        Category = DeviceCategory.MotionCard,
                        BrandName = BrandName,
                        ExtraInfo = new ZmcConnectionOptions
                        {
                            ConnectionType = ZmcConnectionType.Ethernet,
                            IpAddress = "192.168.0.11",
                            Port = 8080
                        }
                    });

                    devices.Add(new DeviceInfo
                    {
                        DeviceId = "COM1",
                        ModelName = "ZMC Serial Controller",
                        Category = DeviceCategory.MotionCard,
                        BrandName = BrandName,
                        ExtraInfo = new ZmcConnectionOptions
                        {
                            ConnectionType = ZmcConnectionType.Serial,
                            ComPort = "COM1",
                            BaudRate = 115200
                        }
                    });
                }

                return Result<List<DeviceInfo>>.Ok(devices);
            }
            catch (Exception ex)
            {
                return Result<List<DeviceInfo>>.Fail($"扫描正运动设备异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据 DeviceId 创建具体的运动卡实例
        /// </summary>
        public IDevice CreateDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new ArgumentException("设备 ID 不能为空", nameof(deviceId));

            var options = new ZmcConnectionOptions();

            if (deviceId.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                options.ConnectionType = ZmcConnectionType.Serial;
                options.ComPort = deviceId;
                options.BaudRate = 115200;
            }
            else if (deviceId.StartsWith("PCI", StringComparison.OrdinalIgnoreCase))
            {
                options.ConnectionType = ZmcConnectionType.Pci;
                options.PciCardIndex = 0;
            }
            else
            {
                options.ConnectionType = ZmcConnectionType.Ethernet;
                options.IpAddress = deviceId;
                options.Port = 8080;
            }

            // 对应 ZmcMotionCard 的双参构造函数
            return new ZmcMotionCard(deviceId, options);
        }

        public void Shutdown()
        {
        }
    }
}