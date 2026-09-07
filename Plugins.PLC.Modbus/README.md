# Plugins.PLC.Modbus

⚠️ **占位工程（未实现）**——当前仅 `Class1.cs` 骨架，无 `ICamera/IPlc/IHardwarePlugin` 实质实现。

- 状态：TODO B7（实现或下线，二选一）；
- 若需 Modbus 通信：短期可评估走 `Plugins.Protocol.Universal`（通用协议，指令策略化）承载，避免重复造轮子；
- 若正式接入：照 `Plugins.PLC.Siemens` 的范式实现 `IHardwarePlugin` + `IPlc`，并加入 slnx 与主程序引用。
