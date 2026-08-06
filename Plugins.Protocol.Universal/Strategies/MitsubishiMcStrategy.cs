using Grayson.Vision.Contracts.Communication;
using Grayson.Vision.Contracts.Core;
using HslCommunication.Core.Net;
using HslCommunication.Profinet.Melsec;
using System;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal.Strategies
{
    public class MitsubishiMcStrategy : IPlcProtocolStrategy, ICommunicationObservable
    {
        public event EventHandler<CommunicationMessage> MessageTransmitted;

        private void RaiseTransmitted(string direction, string content, bool isSuccess, string remark = "")
        {
            MessageTransmitted?.Invoke(this, new CommunicationMessage
            {
                DeviceKey = "MitsubishiPLC",
                Direction = direction,
                Content = content,
                IsSuccess = isSuccess,
                Remark = remark
            });
        }
        private MelsecMcNet _melsecNet;
        private MelsecMcAsciiNet _melsecAsciiNet;
        private bool _isAscii = false;

        public bool IsConnected => _melsecNet != null || _melsecAsciiNet != null;

        public Result Connect(string ip, int port, string extraParams)
        {
            int connectPort = port > 0 ? port : 6000;
            _isAscii = string.Equals(extraParams, "ASCII", StringComparison.OrdinalIgnoreCase);

            HslCommunication.OperateResult connectRes;

            if (_isAscii)
            {
                _melsecAsciiNet = new MelsecMcAsciiNet(ip, connectPort) { ConnectTimeOut = 3000 };
                connectRes = _melsecAsciiNet.ConnectServer();
            }
            else
            {
                _melsecNet = new MelsecMcNet(ip, connectPort) { ConnectTimeOut = 3000 };
                connectRes = _melsecNet.ConnectServer();
            }

            return connectRes.IsSuccess
                ? Result.Ok()
                : Result.Fail($"三菱 MC 协议连接失败: {connectRes.Message}");
        }

        public Result Disconnect()
        {
            if (_isAscii)
            {
                var res = _melsecAsciiNet?.ConnectClose();
                _melsecAsciiNet = null;
                return res?.IsSuccess == true ? Result.Ok() : Result.Fail(res?.Message ?? "断开失败");
            }
            else
            {
                var res = _melsecNet?.ConnectClose();
                _melsecNet = null;
                return res?.IsSuccess == true ? Result.Ok() : Result.Fail(res?.Message ?? "断开失败");
            }
        }

        public async Task<Result<T>> ReadAsync<T>(string address)
        {
            if (!IsConnected) return Result<T>.Fail("三菱 PLC 未连接");


            Type t = typeof(T);
            if (t == typeof(bool))
            {
                var read = _isAscii ? await _melsecAsciiNet.ReadBoolAsync(address) : await _melsecNet.ReadBoolAsync(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }
            if (t == typeof(int))
            {
                var read = _isAscii ? await _melsecAsciiNet.ReadInt32Async(address) : await _melsecNet.ReadInt32Async(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }
            if (t == typeof(float))
            {
                var read = _isAscii ? await _melsecAsciiNet.ReadFloatAsync(address) : await _melsecNet.ReadFloatAsync(address);
                return read.IsSuccess ? Result<T>.Ok((T)(object)read.Content) : Result<T>.Fail(read.Message);
            }

            return Result<T>.Fail($"暂不支持的数据类型: {t.Name}");
        }

        public async Task<Result> WriteAsync<T>(string address, T value)
        {
            if (!IsConnected) return Result.Fail("三菱 PLC 未连接");

            HslCommunication.OperateResult writeRes;
            switch (value)
            {
                case bool bVal:
                    writeRes = _isAscii ? await _melsecAsciiNet.WriteAsync(address, bVal) : await _melsecNet.WriteAsync(address, bVal);
                    break;
                case int iVal:
                    writeRes = _isAscii ? await _melsecAsciiNet.WriteAsync(address, iVal) : await _melsecNet.WriteAsync(address, iVal);
                    break;
                case float fVal:
                    writeRes = _isAscii ? await _melsecAsciiNet.WriteAsync(address, fVal) : await _melsecNet.WriteAsync(address, fVal);
                    break;
                default:
                    return Result.Fail($"不支持写入类型: {typeof(T).Name}");
            }

            return writeRes.IsSuccess ? Result.Ok() : Result.Fail(writeRes.Message);
        }

        public async Task<Result<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            if (!IsConnected) return Result<byte[]>.Fail("三菱 PLC 未连接");
            // 记录发送 (TX)
            RaiseTransmitted("TX", $"READ {address} Len:{length}", true);

            var read = _isAscii ? await _melsecAsciiNet.ReadAsync(address, length) : await _melsecNet.ReadAsync(address, length);

            // 记录接收 (RX)
            if (read.IsSuccess)
            {
                string hexStr = BitConverter.ToString(read.Content).Replace("-", " ");
                RaiseTransmitted("RX", hexStr, true, "读取成功");
                return Result<byte[]>.Ok(read.Content);
            }
            else
            {
                RaiseTransmitted("RX", $"ERROR: {read.Message}", false, "读取失败");
                return Result<byte[]>.Fail(read.Message);
            }
        }

        public async Task<Result> WriteBytesAsync(string address, byte[] data)
        {
            if (!IsConnected) return Result.Fail("三菱 PLC 未连接");
            var write = _isAscii ? await _melsecAsciiNet.WriteAsync(address, data) : await _melsecNet.WriteAsync(address, data);
            return write.IsSuccess ? Result.Ok() : Result.Fail(write.Message);
        }

        public async Task<Result<string>> ReadStringAsync(string address, ushort length)
        {
            if (!IsConnected) return Result<string>.Fail("三菱 PLC 未连接");
            var read = _isAscii ? await _melsecAsciiNet.ReadStringAsync(address, length) : await _melsecNet.ReadStringAsync(address, length);
            return read.IsSuccess ? Result<string>.Ok(read.Content) : Result<string>.Fail(read.Message);
        }

        public async Task<Result> WriteStringAsync(string address, string value)
        {
            if (!IsConnected) return Result.Fail("三菱 PLC 未连接");

            // HslCommunication 中写入字符串统一使用 WriteAsync 方法
            var write = _isAscii
                ? await _melsecAsciiNet.WriteAsync(address, value)
                : await _melsecNet.WriteAsync(address, value);

            return write.IsSuccess ? Result.Ok() : Result.Fail(write.Message);
        }

        public void Dispose() => Disconnect();
    }
}
