# Plugins.PLC.Siemens

西门子 S7 PLC 插件，实现 `IPlc`。

- 工程：`Plugins.PLC.Siemens.csproj`；依赖仅 `Contracts`
- 被 `Grayson.VisionApp.WpfUI.csproj`（轻量变体，未进 slnx）引用；全插件版主程序经设备池装配同类接口，UI 不感知厂商

## 代码结构

| 文件 | 职责 |
|---|---|
| `SiemensPlcPlugin.cs` | `IHardwarePlugin` 入口 + `IPlc` 实现（连接/读写/断线重连） |

## 状态

- 基础可用；读写点位表与触发信号联调随双滑台业务流推进。
- 同类参考：`Plugins.Protocol.Universal`（通用协议）、`Plugins.PLC.Modbus`（占位未实现）。
