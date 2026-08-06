using Grayson.Vision.Contracts.Core;
using HslCommunication.Profinet.Omron;
using System;
using System.Threading.Tasks;


namespace Plugins.Protocol.Universal.Strategies
{
    public class OmronFinsStrategy : IPlcProtocolStrategy
    {
        private OmronFinsNet _omronNet;

        public bool IsConnected => _omronNet != null;

        public Result Connect(string ip, int port, string extraParams)
        {
            int connectPort = port > 0 ? port : 9600;
            _omronNet = new OmronFinsNet(ip, connectPort)
            {
                ConnectTimeOut = 3000
            };

            if (byte.TryParse(extraParams, out var sa1))
            {
                _omronNet.SA1 = sa1;
            }

            var connectRes = _omronNet.ConnectServer();
            return connectRes.IsSuccess
                ? Result.Ok()
                : Result.Fail($"欧姆龙 FINS 协议连接失败: {connectRes.Message}");
        }

        public Result Disconnect()
        {
            var res = _omronNet?.ConnectClose();
            _omronNet = null;
            return res?.IsSuccess == true ? Result.Ok() : Result.Fail(res?.Message ?? "断开失败");
        }

        public async Task<Result<T>> ReadAsync<T>(string address)
        {
            if (_omronNet == null) return Result<T>.Fail("欧姆龙 PLC 未连接");

            Type t = typeof(T);
            if (t == typeof(bool))
            {
                var read = await _omronNet.ReadBoolAsync(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }
            if (t == typeof(int))
            {
                var read = await _omronNet.ReadInt32Async(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }
            if (t == typeof(float))
            {
                var read = await _omronNet.ReadFloatAsync(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }

            return Result<T>.Fail($"暂不支持的数据类型: {t.Name}");
        }

        public async Task<Result> WriteAsync<T>(string address, T value)
        {
            if (_omronNet == null) return Result.Fail("欧姆龙 PLC 未连接");

            HslCommunication.OperateResult writeRes;
            switch (value)
            {
                case bool bVal: writeRes = await _omronNet.WriteAsync(address, bVal); break;
                case int iVal: writeRes = await _omronNet.WriteAsync(address, iVal); break;
                case float fVal: writeRes = await _omronNet.WriteAsync(address, fVal); break;
                default: return Result.Fail($"不支持写入类型: {typeof(T).Name}");
            }

            return writeRes.IsSuccess ? Result.Ok() : Result.Fail(writeRes.Message);
        }

        public async Task<Result<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            if (_omronNet == null) return Result<byte[]>.Fail("欧姆龙 PLC 未连接");
            var read = await _omronNet.ReadAsync(address, length);
            return read.IsSuccess ? Result<byte[]>.Ok(read.Content) : Result<byte[]>.Fail(read.Message);
        }

        public async Task<Result> WriteBytesAsync(string address, byte[] data)
        {
            if (_omronNet == null) return Result.Fail("欧姆龙 PLC 未连接");
            var write = await _omronNet.WriteAsync(address, data);
            return write.IsSuccess ? Result.Ok() : Result.Fail(write.Message);
        }

        public async Task<Result<string>> ReadStringAsync(string address, ushort length)
        {
            if (_omronNet == null) return Result<string>.Fail("欧姆龙 PLC 未连接");
            var read = await _omronNet.ReadStringAsync(address, length);
            return read.IsSuccess ? Result<string>.Ok(read.Content) : Result<string>.Fail(read.Message);
        }

        public async Task<Result> WriteStringAsync(string address, string value)
        {
            if (_omronNet == null) return Result.Fail("欧姆龙 PLC 未连接");
            var write = await _omronNet.WriteAsync(address, value);
            return write.IsSuccess ? Result.Ok() : Result.Fail(write.Message);
        }

        public void Dispose() => Disconnect();
    }
}
