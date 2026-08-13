using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>
    /// 光源控制器通用接口（串口/网口调光）
    /// </summary>
    public interface ILightController : IDevice
    {
        /// <summary>
        /// 设置指定通道的亮度 (0 - 255)
        /// </summary>
        /// <param name="channel">通道号 (从 1 或 0 开始，视硬件而定)</param>
        /// <param name="intensity">亮度强度 (0 - 255)</param>
        Result SetIntensity(int channel, int intensity);

        /// <summary>
        /// 获取指定通道的当前亮度
        /// </summary>
        Result<int> GetIntensity(int channel);

        /// <summary>
        /// 打开/关闭指定通道
        /// </summary>
        Result TurnChannel(int channel, bool turnOn);

        /// <summary>
        /// 打开/关闭所有通道
        /// </summary>
        Result TurnAll(bool turnOn);
    }
}