using Grayson.Vision.Contracts.Communication;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HslCommunication;
using HslCommunication.ModBus;
using System;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal.Strategies
{
    public class ModbusTcpStrategy : IPlcProtocolStrategy, ICommunicationObservable
    {
        public event EventHandler<CommunicationMessage> MessageTransmitted;

        public string DeviceKey { get; set; } = "ModbusTcpDevice";

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

        private ModbusTcpNet _modbusNet;

        public bool IsConnected => _modbusNet != null;

        public Result Connect(string ip, int port, string extraParams)
        {
            byte station = 1;
            if (byte.TryParse(extraParams, out var parsedStation)) station = parsedStation;

            int connectPort = port > 0 ? port : 502;
            _modbusNet = new ModbusTcpNet(ip, connectPort, station);
            var connectRes = _modbusNet.ConnectServer();

            if (connectRes.IsSuccess)
            {
                LogBus.Info("Communication", $"Modbus TCP 连接成功 [{ip}:{connectPort}] 站号:{station}");
                return Result.Ok();
            }

            LogBus.Error("Communication", $"Modbus TCP 连接失败 [{ip}:{connectPort}]: {connectRes.Message}");
            return Result.Fail($"Modbus TCP 连接失败: {connectRes.Message}");
        }

        public Result Disconnect()
        {
            var res = _modbusNet?.ConnectClose();
            _modbusNet = null;
            LogBus.Info("Communication", "Modbus TCP 设备已断开连接");
            return res?.IsSuccess == true ? Result.Ok() : Result.Fail(res?.Message ?? "断开失败");
        }

        public async Task<Result<T>> ReadAsync<T>(string address)
        {
            if (_modbusNet == null)
            {
                LogBus.Warn("Communication", "Modbus 设备未连接，放弃读取");
                return Result<T>.Fail("Modbus 设备未连接");
            }

            Type t = typeof(T);
            RaiseTransmitted("TX", $"READ <{t.Name}> Address:{address}", true);

            HslCommunication.OperateResult<object> readObj = new HslCommunication.OperateResult<object>();

            if (t == typeof(bool))
            {
                var r = await _modbusNet.ReadBoolAsync(address);
                readObj.IsSuccess = r.IsSuccess; readObj.Message = r.Message; if (r.IsSuccess) readObj.Content = r.Content;
            }
            else if (t == typeof(int))
            {
                var r = await _modbusNet.ReadInt32Async(address);
                readObj.IsSuccess = r.IsSuccess; readObj.Message = r.Message; if (r.IsSuccess) readObj.Content = r.Content;
            }
            else if (t == typeof(float))
            {
                var r = await _modbusNet.ReadFloatAsync(address);
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
            LogBus.Error("Communication", $"Modbus 读取点位 [{address}] 失败: {readObj.Message}");
            return Result<T>.Fail(readObj.Message);
        }

        public async Task<Result> WriteAsync<T>(string address, T value)
        {
            if (_modbusNet == null)
            {
                LogBus.Warn("Communication", "Modbus 设备未连接，放弃写入");
                return Result.Fail("Modbus 设备未连接");
            }

            RaiseTransmitted("TX", $"WRITE <{typeof(T).Name}> Address:{address} Value:{value}", true);

            HslCommunication.OperateResult writeRes;
            if (value is bool bVal) writeRes = await _modbusNet.WriteAsync(address, bVal);
            else if (value is int iVal) writeRes = await _modbusNet.WriteAsync(address, iVal);
            else if (value is float fVal) writeRes = await _modbusNet.WriteAsync(address, fVal);
            else return Result.Fail($"不支持写入类型: {typeof(T).Name}");

            if (writeRes.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {writeRes.Message}", false, "写入失败");
            LogBus.Error("Communication", $"Modbus 写入点位 [{address}] 失败: {writeRes.Message}");
            return Result.Fail(writeRes.Message);
        }

        public async Task<Result<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            if (_modbusNet == null) return Result<byte[]>.Fail("Modbus 设备未连接");

            RaiseTransmitted("TX", $"READ BYTES Address:{address} Len:{length}", true);
            var read = await _modbusNet.ReadAsync(address, length);

            if (read.IsSuccess)
            {
                string hexStr = BitConverter.ToString(read.Content).Replace("-", " ");
                RaiseTransmitted("RX", hexStr, true, "读取成功");
                return Result<byte[]>.Ok(read.Content);
            }

            RaiseTransmitted("RX", $"ERROR: {read.Message}", false, "读取失败");
            LogBus.Error("Communication", $"Modbus 读取字节 [{address}] 失败: {read.Message}");
            return Result<byte[]>.Fail(read.Message);
        }

        public async Task<Result> WriteBytesAsync(string address, byte[] data)
        {
            if (_modbusNet == null) return Result.Fail("Modbus 设备未连接");

            string hexStr = BitConverter.ToString(data).Replace("-", " ");
            RaiseTransmitted("TX", $"WRITE BYTES Address:{address} Data:[{hexStr}]", true);

            var write = await _modbusNet.WriteAsync(address, data);
            if (write.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {write.Message}", false, "写入失败");
            LogBus.Error("Communication", $"Modbus 写入字节 [{address}] 失败: {write.Message}");
            return Result.Fail(write.Message);
        }

        public async Task<Result<string>> ReadStringAsync(string address, ushort length)
        {
            if (_modbusNet == null) return Result<string>.Fail("Modbus 设备未连接");

            RaiseTransmitted("TX", $"READ STRING Address:{address} Len:{length}", true);
            var read = await _modbusNet.ReadStringAsync(address, length);

            if (read.IsSuccess)
            {
                RaiseTransmitted("RX", read.Content, true, "读取成功");
                return Result<string>.Ok(read.Content);
            }

            RaiseTransmitted("RX", $"ERROR: {read.Message}", false, "读取失败");
            LogBus.Error("Communication", $"Modbus 读取字符串 [{address}] 失败: {read.Message}");
            return Result<string>.Fail(read.Message);
        }

        public async Task<Result> WriteStringAsync(string address, string value)
        {
            if (_modbusNet == null) return Result.Fail("Modbus 设备未连接");

            RaiseTransmitted("TX", $"WRITE STRING Address:{address} Value:{value}", true);
            var write = await _modbusNet.WriteAsync(address, value);

            if (write.IsSuccess)
            {
                RaiseTransmitted("RX", "OK", true, "写入成功");
                return Result.Ok();
            }

            RaiseTransmitted("RX", $"ERROR: {write.Message}", false, "写入失败");
            LogBus.Error("Communication", $"Modbus 写入字符串 [{address}] 失败: {write.Message}");
            return Result.Fail(write.Message);
        }

        public void Dispose() => Disconnect();
    }
}