using Grayson.Vision.Contracts.Communication;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HslCommunication.Core.Net;
using HslCommunication.Profinet.Melsec;
using System;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal.Strategies
{
    public class MitsubishiMcStrategy : IPlcProtocolStrategy, ICommunicationObservable
    {
        public event EventHandler<CommunicationMessage> MessageTransmitted;

        public string DeviceKey { get; set; } = "MitsubishiPLC";

        private void RaiseTransmitted(string direction, string content, bool isSuccess, string remark = "")
        {
            MessageTransmitted?.Invoke(this, new CommunicationMessage
            {
                DeviceKey = DeviceKey,
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

            if (connectRes.IsSuccess)
            {
                LogBus.Info("Communication", $"三菱 MC 协议连接成功 [{ip}:{connectPort}] (ASCII: {_isAscii})");
                return Result.Ok();
            }

            LogBus.Error("Communication", $"三菱 MC 协议连接失败 [{ip}:{connectPort}]: {connectRes.Message}");
            return Result.Fail($"三菱 MC 协议连接失败: {connectRes.Message}");
        }

        public Result Disconnect()
        {
            HslCommunication.OperateResult res = null;
            if (_isAscii)
            {
                res = _melsecAsciiNet?.ConnectClose();
                _melsecAsciiNet = null;
            }
            else
            {
                res = _melsecNet?.ConnectClose();
                _melsecNet = null;
            }

            LogBus.Info("Communication", "三菱 PLC 已断开连接");
            return res?.IsSuccess == true ? Result.Ok() : Result.Fail(res?.Message ?? "断开失败");
        }

        public async Task<Result<T>> ReadAsync<T>(string address)
        {
            if (!IsConnected)
            {
                LogBus.Warn("Communication", "三菱 PLC 未连接，放弃读取");
                return Result<T>.Fail("三菱 PLC 未连接");
            }

            Type t = typeof(T);
            RaiseTransmitted("TX", $"READ <{t.Name}> Address:{address}", true);

            HslCommunication.OperateResult<object> readObj = new HslCommunication.OperateResult<object>();

            if (t == typeof(bool))
            {
                var r = _isAscii ? await _melsecAsciiNet.ReadBoolAsync(address) : await _melsecNet.ReadBoolAsync(address);
                readObj.IsSuccess = r.IsSuccess; readObj.Message = r.Message; if (r.IsSuccess) readObj.Content = r.Content;
            }
            else if (t == typeof(int))
            {
                var r = _isAscii ? await _melsecAsciiNet.ReadInt32Async(address) : await _melsecNet.ReadInt32Async(address);
                readObj.IsSuccess = r.IsSuccess; readObj.Message = r.Message; if (r.IsSuccess) readObj.Content = r.Content;
            }
            else if (t == typeof(float))
            {
                var r = _isAscii ? await _melsecAsciiNet.ReadFloatAsync(address) : await _melsecNet.ReadFloatAsync(address);
                readObj.IsSuccess = r.IsSuccess; readObj.Message = r.Message; if (r.IsSuccess) readObj.Content = r.Content;
            }
            else
            {
                return Result<T>.Fail($"暂不支持的数据类型: {t.Name}");
            }

            if (readObj.IsSuccess)
            {
                RaiseTransmitted("RX", $"{readObj.Content}", true, "读取成功");
                return Result<T>.Ok((T)readObj.Content);
            }

            RaiseTransmitted("RX", $"ERROR: {readObj.Message}", false, "读取失败");
            LogBus.Error("Communication", $"三菱 PLC 读取点位 [{address}] 失败: {readObj.Message}");
            return Result<T>.Fail(readObj.Message);
        }

        public async Task<Result> WriteAsync<T>(string address, T value)
        {
            if (!IsConnected)
            {
                LogBus.Warn("Communication", "三菱 PLC 未连接，放弃写入");
                return Result.Fail("三菱 PLC 未连接");
            }

            RaiseTransmitted("TX", $"WRITE <{typeof(T).Name}> Address:{address} Value:{value}", true);

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

            if (writeRes.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {writeRes.Message}", false, "写入失败");
            LogBus.Error("Communication", $"三菱 PLC 写入点位 [{address}] 失败: {writeRes.Message}");
            return Result.Fail(writeRes.Message);
        }

        public async Task<Result<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            if (!IsConnected) return Result<byte[]>.Fail("三菱 PLC 未连接");

            RaiseTransmitted("TX", $"READ BYTES Address:{address} Len:{length}", true);
            var read = _isAscii ? await _melsecAsciiNet.ReadAsync(address, length) : await _melsecNet.ReadAsync(address, length);

            if (read.IsSuccess)
            {
                string hexStr = BitConverter.ToString(read.Content).Replace("-", " ");
                RaiseTransmitted("RX", hexStr, true, "读取成功");
                return Result<byte[]>.Ok(read.Content);
            }

            RaiseTransmitted("RX", $"ERROR: {read.Message}", false, "读取失败");
            LogBus.Error("Communication", $"三菱 PLC 读取字节 [{address}] 失败: {read.Message}");
            return Result<byte[]>.Fail(read.Message);
        }

        public async Task<Result> WriteBytesAsync(string address, byte[] data)
        {
            if (!IsConnected) return Result.Fail("三菱 PLC 未连接");

            string hexStr = BitConverter.ToString(data).Replace("-", " ");
            RaiseTransmitted("TX", $"WRITE BYTES Address:{address} Data:[{hexStr}]", true);

            var write = _isAscii ? await _melsecAsciiNet.WriteAsync(address, data) : await _melsecNet.WriteAsync(address, data);
            if (write.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {write.Message}", false, "写入失败");
            LogBus.Error("Communication", $"三菱 PLC 写入字节 [{address}] 失败: {write.Message}");
            return Result.Fail(write.Message);
        }

        public async Task<Result<string>> ReadStringAsync(string address, ushort length)
        {
            if (!IsConnected) return Result<string>.Fail("三菱 PLC 未连接");

            RaiseTransmitted("TX", $"READ STRING Address:{address} Len:{length}", true);
            var read = _isAscii ? await _melsecAsciiNet.ReadStringAsync(address, length) : await _melsecNet.ReadStringAsync(address, length);

            if (read.IsSuccess)
            {
                RaiseTransmitted("RX", read.Content, true, "读取成功");
                return Result<string>.Ok(read.Content);
            }

            RaiseTransmitted("RX", $"ERROR: {read.Message}", false, "读取失败");
            LogBus.Error("Communication", $"三菱 PLC 读取字符串 [{address}] 失败: {read.Message}");
            return Result<string>.Fail(read.Message);
        }

        public async Task<Result> WriteStringAsync(string address, string value)
        {
            if (!IsConnected) return Result.Fail("三菱 PLC 未连接");

            RaiseTransmitted("TX", $"WRITE STRING Address:{address} Value:{value}", true);
            var write = _isAscii ? await _melsecAsciiNet.WriteAsync(address, value) : await _melsecNet.WriteAsync(address, value);

            if (write.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {write.Message}", false, "写入失败");
            LogBus.Error("Communication", $"三菱 PLC 写入字符串 [{address}] 失败: {write.Message}");
            return Result.Fail(write.Message);
        }

        public void Dispose() => Disconnect();
    }
}