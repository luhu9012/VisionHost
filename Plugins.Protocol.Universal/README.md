# Plugins.Protocol.Universal - 通用 PLC 协议插件

一个基于策略模式的通用 PLC 驱动插件，通过 `ConnectionString` 中的 `Protocol` 字段自动选择底层通信协议，向上统一暴露 `IPlc` / `IIoDevice` / `ICommunicationObservable` 能力。

---

## 📋 项目定位

- **插件名称**: `UniversalProtocol`
- **目标框架**: .NET Framework 4.7.2
- **核心能力**: 一个设备实例适配多种主流 PLC 协议（Siemens S7 / Modbus TCP / Mitsubishi MC / Omron FINS）。
- **在体系中的位置**: 作为 `IHardwarePlugin` 实现被 `DevicePoolManager` 扫描加载；当没有更具体的品牌插件命中时，该插件以 `Priority = -100` 兜底匹配。

---

## 🏗️ 架构设计

### 整体结构

```
Plugins.Protocol.Universal/
├── UniversalProtocolPlugin.cs   # IHardwarePlugin 插件入口
├── UniversalPlcDevice.cs        # IPlc + IIoDevice + ICommunicationObservable 统一设备
├── Strategies/
│   ├── IPlcProtocolStrategy.cs  # 协议策略接口
│   ├── SiemensS7Strategy.cs     # 西门子 S7 协议
│   ├── ModbusTcpStrategy.cs     # Modbus TCP 协议
│   ├── MitsubishiMcStrategy.cs  # 三菱 MC 协议（支持 ASCII/Binary）
│   └── OmronFinsStrategy.cs     # 欧姆龙 FINS 协议
└── packages.config              # NuGet 依赖
```

### 核心角色

| 类型 | 职责 |
|------|------|
| `UniversalProtocolPlugin` | 实现 `IHardwarePlugin`，`Supports` 始终返回 `true`，`CreateDevice` 生成 `UniversalPlcDevice`。 |
| `UniversalPlcDevice` | 解析连接字符串、选择并实例化协议策略、转发通信事件、提供数字量 IO 地址映射。 |
| `IPlcProtocolStrategy` | 协议无关的读写接口，包含连接、标量读写、字节数组读写、字符串读写。 |
| 各协议 Strategy | 基于 HslCommunication 封装西门子/Modbus/三菱/欧姆龙的连接与数据访问，并触发通信日志事件。 |

---

## ⚙️ 技术栈

- **框架**: .NET Framework 4.7.2
- **PLC 通信库**: [HslCommunication](https://github.com/dathlin/HslCommunication) 12.9.2
- **序列化**: Newtonsoft.Json 13.0.1
- **接口契约**: `Grayson.Vision.Contracts.Core` / `.Devices` / `.Communication` / `.Infrastructure.Logging`

---

## 🔌 支持的协议

| 协议 | 触发关键字 | 默认端口 | 额外参数说明 |
|------|-----------|---------|-------------|
| Siemens S7 | `siemenss7` / `siemens` | 102 | `Extra` 指定 PLC 型号：`S1200`/`S1500`/`S300`/`S400` 等；纯数字会自动补 `S` 前缀。 |
| Modbus TCP | `modbustcp` / `modbus` / `inovance` | 502 | `Extra` 指定站号（默认 1）。 |
| Mitsubishi MC | `mitsubishimc` / `mitsubishi` | 6000 | `Extra=ASCII` 启用 ASCII 模式，否则为 Binary。 |
| Omron FINS | `omronfins` / `omron` | 9600 | `Extra` 指定 SA1 字节（默认解析为 byte）。 |

> 关键字大小写不敏感。

---

## 📝 连接字符串格式

连接字符串使用 `Key=Value` 对，键值对之间以 `;` 或 `,` 分隔：

```text
Protocol=ModbusTcp;IP=192.168.1.10;Port=502;Extra=1
```

或

```text
Protocol=SiemensS7;IP=192.168.1.20;Port=102;Extra=S1500
```

### 必填与默认值

| 键 | 必填 | 默认值 | 说明 |
|---|------|--------|------|
| `Protocol` | 是 | `ModbusTcp` | 协议类型 |
| `IP` | 否 | `127.0.0.1` | PLC IP 地址 |
| `Port` | 否 | 各协议默认端口 | 自定义端口 |
| `Extra` | 否 | 空 | 协议特定参数（型号/站号/模式/SA1） |

连接字符串应放入设备配置参数的 `ConnectionString` 键中，由 `UniversalPlcDevice.Connect()` 解析。

---

## 🧮 数字量 IO 地址映射

`UniversalPlcDevice` 实现 `IIoDevice`，提供 `ReadDi` / `ReadDo` / `WriteDo`。

### 地址解析优先级

1. **显式映射表**（优先）：配置 `DiMapping` / `DoMapping` 参数。
2. **协议默认规则**：未配置映射时，根据当前协议策略自动生成。

### 显式映射配置

支持两种形式的配置值：

- `Dictionary<int, string>` 对象
- 字符串：`"0:I0.0;1:I0.1;2:Q0.0"`（分隔符支持 `;` 或 `,`，键值分隔支持 `:` 或 `=`）

### 默认地址规则

| 协议 | DI 默认格式 | DO 默认格式 | 示例 |
|------|------------|------------|------|
| Siemens S7 | `I{字节}.{位}` | `Q{字节}.{位}` | 通道 9 -> `I1.1` |
| Mitsubishi MC | `X{十六进制}` | `Y{十六进制}` | 通道 15 -> `XF` |
| Modbus TCP | `10001 + 通道` | `1 + 通道` | 通道 0 -> `10001` / `1` |
| Omron FINS | `0.{通道:D2}` | `1.{通道:D2}` | 通道 5 -> `0.05` / `1.05` |

---

## 📡 对外数据交互

### IPlc 同步接口

```csharp
Result<bool> ReadBit(string addr);
Result WriteBit(string addr, bool val);
Result<int> ReadInt(string addr);
Result WriteInt(string addr, int val);
Result<float> ReadFloat(string addr);
Result WriteFloat(string addr, float val);
```

### IPlc 异步与块数据接口

```csharp
Task<Result<T>> ReadAsync<T>(string address);
Task<Result> WriteAsync<T>(string address, T value);
Task<Result<byte[]>> ReadBytesAsync(string address, ushort length);
Task<Result> WriteBytesAsync(string address, byte[] data);
Task<Result<string>> ReadStringAsync(string address, ushort length);
Task<Result> WriteStringAsync(string address, string value);
```

### 通信事件

实现 `ICommunicationObservable`，每次收发都会触发 `MessageTransmitted` 事件：

```csharp
public event EventHandler<CommunicationMessage> MessageTransmitted;
```

事件携带方向（TX/RX）、内容、成功状态、备注，可用于 UI 日志窗、通信监控等。

---

## 🚀 使用示例

### 由 DevicePool 创建并连接

```csharp
var plugin = new UniversalProtocolPlugin();
var device = plugin.CreateDevice("PLC-001") as UniversalPlcDevice;

device.ConfigParams["ConnectionString"] =
	"Protocol=SiemensS7;IP=192.168.1.10;Port=102;Extra=S1500";

var connect = device.Connect();
if (!connect.Success)
{
	Console.WriteLine(connect.Message);
	return;
}

// 读取整数
var r = device.ReadInt("DB1.DBW0");
if (r.Success) Console.WriteLine(r.Content);

// 按需配置 DI/DO 映射（可选）
device.ConfigParams["DiMapping"] = "0:I0.0;1:I0.1";
```

### 订阅通信日志

```csharp
device.MessageTransmitted += (s, e) =>
{
	Console.WriteLine($"[{e.Direction}] {e.Content} {(e.IsSuccess ? "OK" : "FAIL")}");
};
```

---

## 📦 扩展方式

### 新增协议策略

1. 实现 `IPlcProtocolStrategy` 接口。
2. 在 `UniversalPlcDevice.Connect()` 的 `switch` 中增加新的协议关键字分支。
3. 在 `GetDefaultProtocolAddress()` 中为新协议补充 DI/DO 默认地址格式。

### 支持新的数据类型

当前各策略支持 `bool` / `int` / `float` 的读写。如需支持 `double` / `short` 等类型，在 `ReadAsync<T>` / `WriteAsync<T>` 中增加对应分支即可。

---

## ⚠️ 已知限制

1. `IsConnected` 在部分策略中以内部对象是否非空判断，未做网络保活检测；长期运行建议配合心跳或 `CheckStatus()` 主动探测。
2. `EnumerateDevices()` 返回空列表，当前不支持自动发现 PLC；设备需要通过配置手动创建。
3. 同步接口内部使用 `.GetAwaiter().GetResult()`，避免在 UI 线程大量调用，以防止阻塞。

---

## 📧 维护信息

- 项目作者: Grayson.VisionApp Team
- 问题反馈: 创建 Issue
- 文档更新: 2026-07-18
