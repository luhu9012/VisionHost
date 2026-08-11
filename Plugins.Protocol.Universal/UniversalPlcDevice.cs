using Grayson.Vision.Contracts.Communication;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Plugins.Protocol.Universal.Strategies;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Plugins.Protocol.Universal
{
    public class UniversalPlcDevice : IPlc, ICommunicationObservable, IIoDevice
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string DeviceKey { get; set; }
        public string BrandName { get; set; } = "UniversalPLC";
        public DeviceCategory Category { get; set; } = DeviceCategory.PLC;

        public event EventHandler<DeviceState> StateChanged;
        // 2. 声明事件
        public event EventHandler<CommunicationMessage> MessageTransmitted;
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
        #region IIoDevice 数字量 IO 接口实现

        /// <summary>
        /// 读取数字量输入 (DI)
        /// channelIndex: 支持映射配置或默认转换 (如 "0" -> "IX0.0" / "X0" / "10001")
        /// </summary>
        public Result<bool> ReadDi(int channelIndex)
        {
            if (State != DeviceState.Connected || _protocolStrategy == null)
            {
                return Result<bool>.Fail("设备未连接，无法读取 DI");
            }

            string address = ResolveIoAddress("DI", channelIndex);
            if (string.IsNullOrEmpty(address))
            {
                return Result<bool>.Fail($"未找到 DI 通道 [{channelIndex}] 对应的 PLC 地址映射");
            }

            return ReadBit(address);
        }

        /// <summary>
        /// 读取数字量输出 (DO) 状态
        /// </summary>
        public Result<bool> ReadDo(int channelIndex)
        {
            if (State != DeviceState.Connected || _protocolStrategy == null)
            {
                return Result<bool>.Fail("设备未连接，无法读取 DO");
            }

            string address = ResolveIoAddress("DO", channelIndex);
            if (string.IsNullOrEmpty(address))
            {
                return Result<bool>.Fail($"未找到 DO 通道 [{channelIndex}] 对应的 PLC 地址映射");
            }

            return ReadBit(address);
        }

        /// <summary>
        /// 写入数字量输出 (DO)
        /// </summary>
        public Result WriteDo(int channelIndex, bool state)
        {
            if (State != DeviceState.Connected || _protocolStrategy == null)
            {
                return Result.Fail("设备未连接，无法写入 DO");
            }

            string address = ResolveIoAddress("DO", channelIndex);
            if (string.IsNullOrEmpty(address))
            {
                return Result.Fail($"未找到 DO 通道 [{channelIndex}] 对应的 PLC 地址映射");
            }

            return WriteBit(address, state);
        }

        /// <summary>
        /// 解析通道索引为实际 PLC 地址
        /// 优先从 ConfigParams 的 "DiMapping" / "DoMapping" 字典获取，若无则使用默认规则生成
        /// </summary>
        private string ResolveIoAddress(string ioType, int channelIndex)
        {
            string configKey = ioType.Equals("DI", StringComparison.OrdinalIgnoreCase) ? "DiMapping" : "DoMapping";

            // 1. 优先查配置的显式映射表 (例如 Dictionary<int, string> 或 "0:I0.0;1:I0.1" 格式字符串)
            if (ConfigParams.TryGetValue(configKey, out var mappingObj))
            {
                if (mappingObj is Dictionary<int, string> mapDict && mapDict.TryGetValue(channelIndex, out var customAddr))
                {
                    return customAddr;
                }
                if (mappingObj is string mapStr)
                {
                    var parsed = ParseMappingString(mapStr);
                    if (parsed.TryGetValue(channelIndex, out var strAddr)) return strAddr;
                }
            }

            // 2. 无显式配置时，根据当前协议策略提供默认地址规整规则
            return GetDefaultProtocolAddress(ioType, channelIndex);
        }

        /// <summary>
        /// 根据底层协议自动推导默认的点位格式
        /// </summary>
        private string GetDefaultProtocolAddress(string ioType, int channelIndex)
        {
            if (_protocolStrategy is SiemensS7Strategy)
            {
                // 西门子默认: DI -> I0.x, DO -> Q0.x
                string prefix = ioType == "DI" ? "I" : "Q";
                int byteAddr = channelIndex / 8;
                int bitAddr = channelIndex % 8;
                return $"{prefix}{byteAddr}.{bitAddr}";
            }
            if (_protocolStrategy is MitsubishiMcStrategy)
            {
                // 三菱默认: DI -> X0, X1..., DO -> Y0, Y1... (十六进制)
                string prefix = ioType == "DI" ? "X" : "Y";
                return $"{prefix}{channelIndex:X}";
            }
            if (_protocolStrategy is ModbusTcpStrategy)
            {
                // Modbus 默认: DI -> 10001+, DO -> 00001+
                int baseAddr = ioType == "DI" ? 10001 : 1;
                return (baseAddr + channelIndex).ToString();
            }
            if (_protocolStrategy is OmronFinsStrategy)
            {
                // 欧姆龙默认: DI -> CIO 0.x, DO -> CIO 1.x
                int word = ioType == "DI" ? 0 : 1;
                return $"{word}.{channelIndex:D2}";
            }

            return channelIndex.ToString();
        }

        private Dictionary<int, string> ParseMappingString(string raw)
        {
            var dict = new Dictionary<int, string>();
            if (string.IsNullOrWhiteSpace(raw)) return dict;

            var pairs = raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var kv = pair.Split(new[] { ':', '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (kv.Length == 2 && int.TryParse(kv[0].Trim(), out int ch))
                {
                    dict[ch] = kv[1].Trim();
                }
            }
            return dict;
        }

        #endregion
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

            string protocol = GetValueOrDefault(connParams, "Protocol", "ModbusTcp");
            string ip = GetValueOrDefault(connParams, "IP", "127.0.0.1");
            int port = int.TryParse(GetValueOrDefault(connParams, "Port", "0"), out var p) ? p : 0;
            string extra = GetValueOrDefault(connParams, "Extra", "");

            // 清理旧策略及事件绑定
            if (_protocolStrategy != null)
            {
                if (_protocolStrategy is ICommunicationObservable oldObservable)
                {
                    oldObservable.MessageTransmitted -= OnStrategyMessageTransmitted;
                }
                _protocolStrategy.Dispose();
            }

            switch (protocol.ToLowerInvariant())
            {
                case "siemenss7":
                case "siemens":
                    _protocolStrategy = new SiemensS7Strategy();
                    break;

                case "modbustcp":
                case "modbus":
                case "inovance":
                    _protocolStrategy = new ModbusTcpStrategy();
                    break;

                case "mitsubishimc":
                case "mitsubishi":
                    _protocolStrategy = new MitsubishiMcStrategy();
                    break;

                case "omronfins":
                case "omron":
                    _protocolStrategy = new OmronFinsStrategy();
                    break;

                default:
                    return Result.Fail($"暂不支持的协议类型: {protocol}");
            }

            // 3. 订阅底层策略的 MessageTransmitted 事件并转发
            if (_protocolStrategy is ICommunicationObservable newObservable)
            {
                newObservable.MessageTransmitted += OnStrategyMessageTransmitted;
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
        private void OnStrategyMessageTransmitted(object sender, CommunicationMessage e)
        {
            // 将策略层的日志事件透传给 UniversalPlcDevice 的订阅者 (ViewModel)
            MessageTransmitted?.Invoke(this, e);
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