using Grayson.Vision.Contracts.Communication;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HslCommunication.Profinet.Omron;
using System;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal.Strategies
{
    public class OmronFinsStrategy : IPlcProtocolStrategy, ICommunicationObservable
    {
        public event EventHandler<CommunicationMessage> MessageTransmitted;

        public string DeviceKey { get; set; } = "OmronPLC";

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
            if (connectRes.IsSuccess)
            {
                LogBus.Info("Communication", $"欧姆龙 FINS 协议连接成功 [{ip}:{connectPort}] SA1:{_omronNet.SA1}");
                return Result.Ok();
            }

            LogBus.Error("Communication", $"欧姆龙 FINS 协议连接失败 [{ip}:{connectPort}]: {connectRes.Message}");
            return Result.Fail($"欧姆龙 FINS 协议连接失败: {connectRes.Message}");
        }

        public Result Disconnect()
        {
            var res = _omronNet?.ConnectClose();
            _omronNet = null;
            LogBus.Info("Communication", "欧姆龙 PLC 已断开连接");
            return res?.IsSuccess == true ? Result.Ok() : Result.Fail(res?.Message ?? "断开失败");
        }

        public async Task<Result<T>> ReadAsync<T>(string address)
        {
            if (_omronNet == null)
            {
                LogBus.Warn("Communication", "欧姆龙 PLC 未连接，放弃读取");
                return Result<T>.Fail("欧姆龙 PLC 未连接");
            }

            Type t = typeof(T);
            RaiseTransmitted("TX", $"READ <{t.Name}> Address:{address}", true);

            HslCommunication.OperateResult<object> readObj = new HslCommunication.OperateResult<object>();

            if (t == typeof(bool))
            {
                var r = await _omronNet.ReadBoolAsync(address);
                readObj.IsSuccess = r.IsSuccess; readObj.Message = r.Message; if (r.IsSuccess) readObj.Content = r.Content;
            }
            else if (t == typeof(int))
            {
                var r = await _omronNet.ReadInt32Async(address);
                readObj.IsSuccess = r.IsSuccess; readObj.Message = r.Message; if (r.IsSuccess) readObj.Content = r.Content;
            }
            else if (t == typeof(float))
            {
                var r = await _omronNet.ReadFloatAsync(address);
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
            LogBus.Error("Communication", $"欧姆龙 PLC 读取点位 [{address}] 失败: {readObj.Message}");
            return Result<T>.Fail(readObj.Message);
        }

        public async Task<Result> WriteAsync<T>(string address, T value)
        {
            if (_omronNet == null)
            {
                LogBus.Warn("Communication", "欧姆龙 PLC 未连接，放弃写入");
                return Result.Fail("欧姆龙 PLC 未连接");
            }

            RaiseTransmitted("TX", $"WRITE <{typeof(T).Name}> Address:{address} Value:{value}", true);

            HslCommunication.OperateResult writeRes;
            switch (value)
            {
                case bool bVal: writeRes = await _omronNet.WriteAsync(address, bVal); break;
                case int iVal: writeRes = await _omronNet.WriteAsync(address, iVal); break;
                case float fVal: writeRes = await _omronNet.WriteAsync(address, fVal); break;
                default: return Result.Fail($"不支持写入类型: {typeof(T).Name}");
            }

            if (writeRes.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {writeRes.Message}", false, "写入失败");
            LogBus.Error("Communication", $"欧姆龙 PLC 写入点位 [{address}] 失败: {writeRes.Message}");
            return Result.Fail(writeRes.Message);
        }

        public async Task<Result<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            if (_omronNet == null) return Result<byte[]>.Fail("欧姆龙 PLC 未连接");

            RaiseTransmitted("TX", $"READ BYTES Address:{address} Len:{length}", true);
            var read = await _omronNet.ReadAsync(address, length);

            if (read.IsSuccess)
            {
                string hexStr = BitConverter.ToString(read.Content).Replace("-", " ");
                RaiseTransmitted("RX", hexStr, true, "读取成功");
                return Result<byte[]>.Ok(read.Content);
            }

            RaiseTransmitted("RX", $"ERROR: {read.Message}", false, "读取失败");
            LogBus.Error("Communication", $"欧姆龙 PLC 读取字节 [{address}] 失败: {read.Message}");
            return Result<byte[]>.Fail(read.Message);
        }

        public async Task<Result> WriteBytesAsync(string address, byte[] data)
        {
            if (_omronNet == null) return Result.Fail("欧姆龙 PLC 未连接");

            string hexStr = BitConverter.ToString(data).Replace("-", " ");
            RaiseTransmitted("TX", $"WRITE BYTES Address:{address} Data:[{hexStr}]", true);

            var write = await _omronNet.WriteAsync(address, data);
            if (write.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {write.Message}", false, "写入失败");
            LogBus.Error("Communication", $"欧姆龙 PLC 写入字节 [{address}] 失败: {write.Message}");
            return Result.Fail(write.Message);
        }

        public async Task<Result<string>> ReadStringAsync(string address, ushort length)
        {
            if (_omronNet == null) return Result<string>.Fail("欧姆龙 PLC 未连接");

            RaiseTransmitted("TX", $"READ STRING Address:{address} Len:{length}", true);
            var read = await _omronNet.ReadStringAsync(address, length);

            if (read.IsSuccess)
            {
                RaiseTransmitted("RX", read.Content, true, "读取成功");
                return Result<string>.Ok(read.Content);
            }

            RaiseTransmitted("RX", $"ERROR: {read.Message}", false, "读取失败");
            LogBus.Error("Communication", $"欧姆龙 PLC 读取字符串 [{address}] 失败: {read.Message}");
            return Result<string>.Fail(read.Message);
        }

        public async Task<Result> WriteStringAsync(string address, string value)
        {
            if (_omronNet == null) return Result.Fail("欧姆龙 PLC 未连接");

            RaiseTransmitted("TX", $"WRITE STRING Address:{address} Value:{value}", true);
            var write = await _omronNet.WriteAsync(address, value);

            if (write.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {write.Message}", false, "写入失败");
            LogBus.Error("Communication", $"欧姆龙 PLC 写入字符串 [{address}] 失败: {write.Message}");
            return Result.Fail(write.Message);
        }

        public void Dispose() => Disconnect();
    }
}