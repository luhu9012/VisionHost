using Grayson.Vision.Contracts.Core;
using HslCommunication;
using HslCommunication.ModBus;
using System;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal.Strategies
{
    public class ModbusTcpStrategy : IPlcProtocolStrategy
    {
        private ModbusTcpNet _modbusNet;

        // 修复 CS1061: HslCommunication 判断连接状态
        public bool IsConnected => _modbusNet != null;

        public Result Connect(string ip, int port, string extraParams)
        {
            byte station = 1;
            if (byte.TryParse(extraParams, out var parsedStation)) station = parsedStation;

            _modbusNet = new ModbusTcpNet(ip, port > 0 ? port : 502, station);
            var connectRes = _modbusNet.ConnectServer();
            return connectRes.IsSuccess
                ? Result.Ok()
                : Result.Fail($"Modbus TCP 连接失败: {connectRes.Message}");
        }

        public Result Disconnect()
        {
            var res = _modbusNet?.ConnectClose();
            _modbusNet = null;
            return res?.IsSuccess == true ? Result.Ok() : Result.Fail(res?.Message ?? "断开失败");
        }

        public async Task<Result<T>> ReadAsync<T>(string address)
        {
            if (_modbusNet == null) return Result<T>.Fail("Modbus 设备未连接");

            Type t = typeof(T);
            if (t == typeof(bool))
            {
                var read = await _modbusNet.ReadBoolAsync(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }
            if (t == typeof(int))
            {
                var read = await _modbusNet.ReadInt32Async(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }
            if (t == typeof(float))
            {
                var read = await _modbusNet.ReadFloatAsync(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }

            return Result<T>.Fail($"暂不支持的数据类型: {t.Name}");
        }

        public async Task<Result> WriteAsync<T>(string address, T value)
        {
            if (_modbusNet == null) return Result.Fail("Modbus 设备未连接");

            HslCommunication.OperateResult writeRes;
            if (value is bool bVal) writeRes = await _modbusNet.WriteAsync(address, bVal);
            else if (value is int iVal) writeRes = await _modbusNet.WriteAsync(address, iVal);
            else if (value is float fVal) writeRes = await _modbusNet.WriteAsync(address, fVal);
            else return Result.Fail($"不支持写入类型: {typeof(T).Name}");

            return writeRes.IsSuccess ? Result.Ok() : Result.Fail(writeRes.Message);
        }

        public async Task<Result<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            if (_modbusNet == null) return Result<byte[]>.Fail("Modbus 设备未连接");
            var read = await _modbusNet.ReadAsync(address, length);
            return read.IsSuccess ? Result<byte[]>.Ok(read.Content) : Result<byte[]>.Fail(read.Message);
        }

        public async Task<Result> WriteBytesAsync(string address, byte[] data)
        {
            if (_modbusNet == null) return Result.Fail("Modbus 设备未连接");
            var write = await _modbusNet.WriteAsync(address, data);
            return write.IsSuccess ? Result.Ok() : Result.Fail(write.Message);
        }

        public async Task<Result<string>> ReadStringAsync(string address, ushort length)
        {
            if (_modbusNet == null) return Result<string>.Fail("Modbus 设备未连接");
            var read = await _modbusNet.ReadStringAsync(address, length);
            return read.IsSuccess ? Result<string>.Ok(read.Content) : Result<string>.Fail(read.Message);
        }

        public async Task<Result> WriteStringAsync(string address, string value)
        {
            if (_modbusNet == null) return Result.Fail("Modbus 设备未连接");
            var write = await _modbusNet.WriteAsync(address, value);
            return write.IsSuccess ? Result.Ok() : Result.Fail(write.Message);
        }

        public void Dispose() => Disconnect();
    }
}
