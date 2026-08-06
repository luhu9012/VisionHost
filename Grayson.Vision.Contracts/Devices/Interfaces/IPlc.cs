using Grayson.Vision.Contracts.Core;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>
    /// PLC / 工业控制器通用数据交互接口
    /// </summary>
    public interface IPlc : IDevice
    {
        #region 1. 核心泛型与异步读写（推荐主干方法）

        /// <summary>异步读取单点位数据（支持 bool, short, int, float, double 等）</summary>
        Task<Result<T>> ReadAsync<T>(string address);

        /// <summary>异步写入单点位数据</summary>
        Task<Result> WriteAsync<T>(string address, T value);

        #endregion

        #region 2. 批量与块数据交互（高性能场景）

        /// <summary>批量读取原始 Byte 数组（用于结构体解析或大块数据传输）</summary>
        Task<Result<byte[]>> ReadBytesAsync(string address, ushort length);

        /// <summary>写入原始 Byte 数组</summary>
        Task<Result> WriteBytesAsync(string address, byte[] data);

        /// <summary>读取 PLC 字符串（扫码/工单/配方）</summary>
        Task<Result<string>> ReadStringAsync(string address, ushort length);

        /// <summary>写入 PLC 字符串</summary>
        Task<Result> WriteStringAsync(string address, string value);

        #endregion

        #region 3. 基础同步快捷方法（向下兼容与简单脚本调用）

        Result<bool> ReadBit(string addr);
        Result WriteBit(string addr, bool val);

        Result<int> ReadInt(string addr);
        Result WriteInt(string addr, int val);

        Result<float> ReadFloat(string addr);
        Result WriteFloat(string addr, float val);

        #endregion
    }
}