using Grayson.Vision.Contracts.Core;
using HslCommunication.Profinet.Siemens;
using System;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal.Strategies
{
    public class SiemensS7Strategy : IPlcProtocolStrategy
    {
        private SiemensS7Net _s7Net;

        // 修复 CS1061: HslCommunication 判断连接状态
        public bool IsConnected => _s7Net != null;

        public Result Connect(string ip, int port, string extraParams)
        {
            // 1. 默认设置为 S1200
            SiemensPLCS type = SiemensPLCS.S1200;

            // 2. 增强解析逻辑：忽略大小写、支持带 S 或不带 S (如 "1200" / "S1200" / "S1500" / "200Smart")
            if (!string.IsNullOrWhiteSpace(extraParams))
            {
                string param = extraParams.Trim();

                // 兼容传入 "1200", "1500", "300", "400" 的格式
                if (!param.StartsWith("S", StringComparison.OrdinalIgnoreCase) && char.IsDigit(param[0]))
                {
                    param = "S" + param;
                }

                if (Enum.TryParse<SiemensPLCS>(param, true, out var parsedType))
                {
                    type = parsedType;
                }
            }

            // 3. 根据解析出的类型实例化 S7 通信对象
            _s7Net = new SiemensS7Net(type, ip)
            {
                Port = port > 0 ? port : 102,
                ConnectTimeOut = 3000
            };

            var connectRes = _s7Net.ConnectServer();
            return connectRes.IsSuccess
                ? Result.Ok()
                : Result.Fail($"西门子 PLC [{type}] 连接失败: {connectRes.Message}");
        }

        public Result Disconnect()
        {
            var res = _s7Net?.ConnectClose();
            _s7Net = null;
            return res?.IsSuccess == true ? Result.Ok() : Result.Fail(res?.Message ?? "断开失败");
        }

        public async Task<Result<T>> ReadAsync<T>(string address)
        {
            if (_s7Net == null) return Result<T>.Fail("PLC 未连接");

            Type t = typeof(T);
            if (t == typeof(bool))
            {
                var read = await _s7Net.ReadBoolAsync(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }
            if (t == typeof(int))
            {
                var read = await _s7Net.ReadInt32Async(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }
            if (t == typeof(float))
            {
                var read = await _s7Net.ReadFloatAsync(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }

            return Result<T>.Fail($"暂不支持的数据类型: {t.Name}");
        }

        public async Task<Result> WriteAsync<T>(string address, T value)
        {
            if (_s7Net == null) return Result.Fail("PLC 未连接");

            HslCommunication.OperateResult writeRes;
            switch (value)
            {
                case bool bVal: writeRes = await _s7Net.WriteAsync(address, bVal); break;
                case int iVal: writeRes = await _s7Net.WriteAsync(address, iVal); break;
                case float fVal: writeRes = await _s7Net.WriteAsync(address, fVal); break;
                default: return Result.Fail($"不支持写入类型: {typeof(T).Name}");
            }

            return writeRes.IsSuccess ? Result.Ok() : Result.Fail(writeRes.Message);
        }

        public async Task<Result<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            if (_s7Net == null) return Result<byte[]>.Fail("PLC 未连接");
            var read = await _s7Net.ReadAsync(address, length);
            return read.IsSuccess ? Result<byte[]>.Ok(read.Content) : Result<byte[]>.Fail(read.Message);
        }

        public async Task<Result> WriteBytesAsync(string address, byte[] data)
        {
            if (_s7Net == null) return Result.Fail("PLC 未连接");
            var write = await _s7Net.WriteAsync(address, data);
            return write.IsSuccess ? Result.Ok() : Result.Fail(write.Message);
        }

        public async Task<Result<string>> ReadStringAsync(string address, ushort length)
        {
            if (_s7Net == null) return Result<string>.Fail("PLC 未连接");
            var read = await _s7Net.ReadStringAsync(address, length);
            return read.IsSuccess ? Result<string>.Ok(read.Content) : Result<string>.Fail(read.Message);
        }

        public async Task<Result> WriteStringAsync(string address, string value)
        {
            if (_s7Net == null) return Result.Fail("PLC 未连接");
            var write = await _s7Net.WriteAsync(address, value);
            return write.IsSuccess ? Result.Ok() : Result.Fail(write.Message);
        }

        public void Dispose() => Disconnect();

    }
}
