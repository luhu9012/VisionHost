using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>
    /// 统一数字量 IO 控制设备接口
    /// </summary>
    public interface IIoDevice : IDevice
    {
        /// <summary>读取通用数字量输入 (DI) 状态</summary>
        Result<bool> ReadDi(int channelIndex);

        /// <summary>读取通用数字量输出 (DO) 状态</summary>
        Result<bool> ReadDo(int channelIndex);

        /// <summary>控制/写入数字量输出 (DO) 状态</summary>
        Result WriteDo(int channelIndex, bool state);
    }
}