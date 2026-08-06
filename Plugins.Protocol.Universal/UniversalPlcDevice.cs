using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Plugins.Protocol.Universal.Strategies;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal
{
    public class UniversalPlcDevice : IPlc
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string DeviceKey { get; set; }
        public string BrandName { get; set; } = "UniversalPLC";
        public DeviceCategory Category { get; set; } = DeviceCategory.PLC;

        public event EventHandler<DeviceState> StateChanged;
        private DeviceState _state = DeviceState.Disconnected;
        public DeviceState State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _state = value;
                    StateChanged?.Invoke(this, _state);
                }
            }
        }

        public Dictionary<string, object> ConfigParams { get; set; } = new Dictionary<string, object>();

        private IPlcProtocolStrategy _protocolStrategy;

        public UniversalPlcDevice(string deviceId)
        {
            DeviceId = deviceId;
        }

        public Result Connect()
        {
            if (!ConfigParams.TryGetValue("ConnectionString", out var connObj) || string.IsNullOrWhiteSpace(connObj?.ToString()))
            {
                return Result.Fail("连接失败：ConnectionString 未提供！");
            }

            var connParams = ParseConnectionString(connObj.ToString());

            // 替代 GetValueOrDefault 的兼容写规，解决 CS1061
            string protocol = GetValueOrDefault(connParams, "Protocol", "ModbusTcp");
            string ip = GetValueOrDefault(connParams, "IP", "127.0.0.1");
            int port = int.TryParse(GetValueOrDefault(connParams, "Port", "0"), out var p) ? p : 0;
            string extra = GetValueOrDefault(connParams, "Extra", "");

            _protocolStrategy?.Dispose();
            switch (protocol.ToLowerInvariant())
            {
                case "siemenss7":
                case "siemens":
                    _protocolStrategy = new SiemensS7Strategy();
                    break;

                case "modbustcp":
                case "modbus":
                case "inovance": // 汇川走标准 Modbus TCP
                    _protocolStrategy = new ModbusTcpStrategy();
                    break;

                case "mitsubishimc":
                case "mitsubishi": // 三菱 MC 协议策略
                    _protocolStrategy = new MitsubishiMcStrategy();
                    break;

                case "omronfins":
                case "omron": // 欧姆龙 FINS 协议策略
                    _protocolStrategy = new OmronFinsStrategy();
                    break;

                default:
                    return Result.Fail($"暂不支持的协议类型: {protocol}");
            }

            var res = _protocolStrategy.Connect(ip, port, extra);
            if (res.Success)
            {
                State = DeviceState.Connected;
                return Result.Ok();
            }

            State = DeviceState.Disconnected;
            return res;
        }

        public Result Disconnect()
        {
            _protocolStrategy?.Disconnect();
            State = DeviceState.Disconnected;
            return Result.Ok();
        }

        public Result CheckStatus()
        {
            if (_protocolStrategy == null || !_protocolStrategy.IsConnected)
            {
                State = DeviceState.Disconnected;
                return Result.Fail("设备已断开连接");
            }
            State = DeviceState.Connected;
            return Result.Ok();
        }

        public Result SetParam(string key, object value)
        {
            ConfigParams[key] = value;
            return Result.Ok();
        }

        public Result<object> GetParam(string key)
        {
            return ConfigParams.TryGetValue(key, out var val)
                ? Result<object>.Ok(val)
                : Result<object>.Fail($"未找到参数: {key}");
        }

        #region IPlc 异步与块数据交互成员实现 (消除 CS0535)

        public Task<Result<T>> ReadAsync<T>(string address) => _protocolStrategy?.ReadAsync<T>(address) ?? Task.FromResult(Result<T>.Fail("协议策略未初始化"));

        public Task<Result> WriteAsync<T>(string address, T value) => _protocolStrategy?.WriteAsync(address, value) ?? Task.FromResult(Result.Fail("协议策略未初始化"));

        public Task<Result<byte[]>> ReadBytesAsync(string address, ushort length) => _protocolStrategy?.ReadBytesAsync(address, length) ?? Task.FromResult(Result<byte[]>.Fail("协议策略未初始化"));

        public Task<Result> WriteBytesAsync(string address, byte[] data) => _protocolStrategy?.WriteBytesAsync(address, data) ?? Task.FromResult(Result.Fail("协议策略未初始化"));

        public Task<Result<string>> ReadStringAsync(string address, ushort length) => _protocolStrategy?.ReadStringAsync(address, length) ?? Task.FromResult(Result<string>.Fail("协议策略未初始化"));

        public Task<Result> WriteStringAsync(string address, string value) => _protocolStrategy?.WriteStringAsync(address, value) ?? Task.FromResult(Result.Fail("协议策略未初始化"));

        #endregion

        #region IPlc 基础同步方法实现 (消除 CS0535)

        public Result<bool> ReadBit(string addr)
        {
            var task = ReadAsync<bool>(addr);
            return task.GetAwaiter().GetResult();
        }

        public Result WriteBit(string addr, bool val) => WriteAsync(addr, val).GetAwaiter().GetResult();

        public Result<int> ReadInt(string addr)
        {
            var task = ReadAsync<int>(addr);
            return task.GetAwaiter().GetResult();
        }

        public Result WriteInt(string addr, int val) => WriteAsync(addr, val).GetAwaiter().GetResult();

        public Result<float> ReadFloat(string addr)
        {
            var task = ReadAsync<float>(addr);
            return task.GetAwaiter().GetResult();
        }

        public Result WriteFloat(string addr, float val) => WriteAsync(addr, val).GetAwaiter().GetResult();

        #endregion

        public void Dispose()
        {
            Disconnect();
            _protocolStrategy?.Dispose();
        }

        private Dictionary<string, string> ParseConnectionString(string connStr)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pairs = connStr.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var kv = pair.Split('=');
                if (kv.Length == 2) dict[kv[0].Trim()] = kv[1].Trim();
            }
            return dict;
        }

        private static string GetValueOrDefault(Dictionary<string, string> dict, string key, string defaultValue)
        {
            return dict.TryGetValue(key, out var value) ? value : defaultValue;
        }
    }
}