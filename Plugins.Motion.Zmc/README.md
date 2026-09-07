# Plugins.Motion.Zmc

正运动（ZMC）运动控制卡插件，实现 `IMotionCard`。

- 工程：`Plugins.Motion.Zmc.csproj`；依赖仅 `Contracts`
- 用于双滑台/轴控制（AxisControl 调试台、工位走位），旋转轴约定与机械手场景一致

## 代码结构

| 文件 | 职责 |
|---|---|
| `ZmcMotionCardPlugin.cs` | `IHardwarePlugin` 入口 + 工厂 |
| `ZmcMotionCard.cs` | `IMotionCard` 实现（轴使能/绝对相对运动/回零/IO） |
| `ZmcConnectionOptions.cs` | 连接参数（IP/超时等） |
| `Zmcaux.cs` | 与 zmcaux 库交互的辅助封装 |

## 注意

- 速度/安全相关参数解析失败时 `ResolveSafeSpeed` 兜底为 50（防零速/暴走）；
- 采图/联动前须 WaitAxesIdle + 100ms 稳定（九点采集同规则）。
