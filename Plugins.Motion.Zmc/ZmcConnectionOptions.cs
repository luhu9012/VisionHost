namespace Plugins.Motion.Zmc
{
    public enum ZmcConnectionType
    {
        Ethernet,
        Serial,
        Pci
    }

    /// <summary>
    /// 正运动控制卡连接配置模型
    /// </summary>
    public class ZmcConnectionOptions
    {
        public ZmcConnectionType ConnectionType { get; set; } = ZmcConnectionType.Ethernet;

        // 以太网参数
        public string IpAddress { get; set; } = "192.168.0.11";
        public int Port { get; set; } = 8080;

        // 串口参数
        public string ComPort { get; set; } = "COM1";
        public int BaudRate { get; set; } = 115200;

        // PCI 参数
        public uint PciCardIndex { get; set; } = 0;
    }
}