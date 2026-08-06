using Grayson.Vision.Contracts.Core;
using System;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal.Strategies
{
    /// <summary>
    /// PLC 通信用底层协议策略接口
    /// </summary>
    public interface IPlcProtocolStrategy : IDisposable
    {
        Result Connect(string ip, int port, string extraParams);
        Result Disconnect();
        bool IsConnected { get; }

        Task<Result<T>> ReadAsync<T>(string address);
        Task<Result> WriteAsync<T>(string address, T value);

        Task<Result<byte[]>> ReadBytesAsync(string address, ushort length);
        Task<Result> WriteBytesAsync(string address, byte[] data);

        Task<Result<string>> ReadStringAsync(string address, ushort length);
        Task<Result> WriteStringAsync(string address, string value);
    }
}
