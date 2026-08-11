using Grayson.Vision.Contracts.Communication;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HslCommunication.Profinet.Siemens;
using System;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal.Strategies
{
    public class SiemensS7Strategy : IPlcProtocolStrategy, ICommunicationObservable
    {
        public event EventHandler<CommunicationMessage> MessageTransmitted;

        public string DeviceKey { get; set; } = "SiemensPLC";

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

        private SiemensS7Net _s7Net;

        public bool IsConnected => _s7Net != null;

        public Result Connect(string ip, int port, string extraParams)
        {
            SiemensPLCS type = SiemensPLCS.S1200;

            if (!string.IsNullOrWhiteSpace(extraParams))
            {
                string param = extraParams.Trim();
                if (!param.StartsWith("S", StringComparison.OrdinalIgnoreCase) && char.IsDigit(param[0]))
                {
                    param = "S" + param;
                }

                if (Enum.TryParse<SiemensPLCS>(param, true, out var parsedType))
                {
                    type = parsedType;
                }
            }

            int connectPort = port > 0 ? port : 102;
            _s7Net = new SiemensS7Net(type, ip)
            {
                Port = connectPort,
                ConnectTimeOut = 3000
            };

            var connectRes = _s7Net.ConnectServer();
            if (connectRes.IsSuccess)
            {
                LogBus.Info("Communication", $"西门子 S7 协议连接成功 [{ip}:{connectPort}] 机型:{type}");
                return Result.Ok();
            }

            LogBus.Error("Communication", $"西门子 PLC [{type}] 连接失败 [{ip}:{connectPort}]: {connectRes.Message}");
            return Result.Fail($"西门子 PLC [{type}] 连接失败: {connectRes.Message}");
        }

        public Result Disconnect()
        {
            var res = _s7Net?.ConnectClose();
            _s7Net = null;
            LogBus.Info("Communication", "西门子 PLC 已断开连接");
            return res?.IsSuccess == true ? Result.Ok() : Result.Fail(res?.Message ?? "断开失败");
        }

        public async Task<Result<T>> ReadAsync<T>(string address)
        {
            if (_s7Net == null)
            {
                LogBus.Warn("Communication", "西门子 PLC 未连接，放弃读取");
                return Result<T>.Fail("PLC 未连接");
            }

            Type t = typeof(T);
            RaiseTransmitted("TX", $"READ <{t.Name}> Address:{address}", true);

            HslCommunication.OperateResult<object> readObj = new HslCommunication.OperateResult<object>();

            if (t == typeof(bool))
            {
                var r = await _s7Net.ReadBoolAsync(address);
                readObj.IsSuccess = r.IsSuccess; readObj.Message = r.Message; if (r.IsSuccess) readObj.Content = r.Content;
            }
            else if (t == typeof(int))
            {
                var r = await _s7Net.ReadInt32Async(address);
                readObj.IsSuccess = r.IsSuccess; readObj.Message = r.Message; if (r.IsSuccess) readObj.Content = r.Content;
            }
            else if (t == typeof(float))
            {
                var r = await _s7Net.ReadFloatAsync(address);
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
            LogBus.Error("Communication", $"西门子 PLC 读取点位 [{address}] 失败: {readObj.Message}");
            return Result<T>.Fail(readObj.Message);
        }

        public async Task<Result> WriteAsync<T>(string address, T value)
        {
            if (_s7Net == null)
            {
                LogBus.Warn("Communication", "西门子 PLC 未连接，放弃写入");
                return Result.Fail("PLC 未连接");
            }

            RaiseTransmitted("TX", $"WRITE <{typeof(T).Name}> Address:{address} Value:{value}", true);

            HslCommunication.OperateResult writeRes;
            switch (value)
            {
                case bool bVal: writeRes = await _s7Net.WriteAsync(address, bVal); break;
                case int iVal: writeRes = await _s7Net.WriteAsync(address, iVal); break;
                case float fVal: writeRes = await _s7Net.WriteAsync(address, fVal); break;
                default: return Result.Fail($"不支持写入类型: {typeof(T).Name}");
            }

            if (writeRes.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {writeRes.Message}", false, "写入失败");
            LogBus.Error("Communication", $"西门子 PLC 写入点位 [{address}] 失败: {writeRes.Message}");
            return Result.Fail(writeRes.Message);
        }

        public async Task<Result<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            if (_s7Net == null) return Result<byte[]>.Fail("PLC 未连接");

            RaiseTransmitted("TX", $"READ BYTES Address:{address} Len:{length}", true);
            var read = await _s7Net.ReadAsync(address, length);

            if (read.IsSuccess)
            {
                string hexStr = BitConverter.ToString(read.Content).Replace("-", " ");
                RaiseTransmitted("RX", hexStr, true, "读取成功");
                return Result<byte[]>.Ok(read.Content);
            }

            RaiseTransmitted("RX", $"ERROR: {read.Message}", false, "读取失败");
            LogBus.Error("Communication", $"西门子 PLC 读取字节 [{address}] 失败: {read.Message}");
            return Result<byte[]>.Fail(read.Message);
        }

        public async Task<Result> WriteBytesAsync(string address, byte[] data)
        {
            if (_s7Net == null) return Result.Fail("PLC 未连接");

            string hexStr = BitConverter.ToString(data).Replace("-", " ");
            RaiseTransmitted("TX", $"WRITE BYTES Address:{address} Data:[{hexStr}]", true);

            var write = await _s7Net.WriteAsync(address, data);
            if (write.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {write.Message}", false, "写入失败");
            LogBus.Error("Communication", $"西门子 PLC 写入字节 [{address}] 失败: {write.Message}");
            return Result.Fail(write.Message);
        }

        public async Task<Result<string>> ReadStringAsync(string address, ushort length)
        {
            if (_s7Net == null) return Result<string>.Fail("PLC 未连接");

            RaiseTransmitted("TX", $"READ STRING Address:{address} Len:{length}", true);
            var read = await _s7Net.ReadStringAsync(address, length);

            if (read.IsSuccess)
            {
                RaiseTransmitted("RX", read.Content, true, "读取成功");
                return Result<string>.Ok(read.Content);
            }

            RaiseTransmitted("RX", $"ERROR: {read.Message}", false, "读取失败");
            LogBus.Error("Communication", $"西门子 PLC 读取字符串 [{address}] 失败: {read.Message}");
            return Result<string>.Fail(read.Message);
        }

        public async Task<Result> WriteStringAsync(string address, string value)
        {
            if (_s7Net == null) return Result.Fail("PLC 未连接");

            RaiseTransmitted("TX", $"WRITE STRING Address:{address} Value:{value}", true);

            // 改用通用 WriteAsync 方法写字符串
            var write = await _s7Net.WriteAsync(address, value);

            if (write.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {write.Message}", false, "写入失败");
            LogBus.Error("Communication", $"西门子 PLC 写入字符串 [{address}] 失败: {write.Message}");
            return Result.Fail(write.Message);
        }

        public void Dispose() => Disconnect();
    }
}