# Plugins.Robot.Epson

Epson SCARA（4 轴）机械手插件：RC+ / SPEL+ / TCP 脚本三层通信，自动选路。麻将双吸嘴工位在用。

- 工程：`Plugins.Robot.Epson.csproj`；依赖仅 `Contracts`
- 电机旋转轴约定：第 4 轴 = U（旋转）；SCARA XY 参考点=法兰/U 轴中心线（偏心语义见仓库 ARCHITECTURE §4.7）

## 代码结构

| 文件 | 职责 |
|---|---|
| `EpsonPlugin.cs` | `IHardwarePlugin` 入口 + 设备工厂 |
| `EpsonRobot.cs` | 机械手行为实现：JOG/走位/IO/状态查询/错误码语义映射 |
| `RCAPINet7Adapter.cs` | RC+ API（控制器原生）通信适配 |
| `SpeLNetAdapter.cs` | SPEL+（Epson 语言）通信适配 |
| `EpsonTcpScriptAdapter.cs` | TCP 脚本通信（与真机 `mainTCP.prg` 配合）；`_ioLock` 一发一收 + TryEnter50ms |

## 真机协作铁律（踩坑沉淀）

- `mainTCP.prg` 编码 = **GBK/ANSI 单字节无 BOM + CRLF**（非 UTF-16）；改动用 `.workbuddy/patch_mainTCP_prg.py`（gbk 读写），改完须 RC+ 重编译重运行；
- SCARA 可达域 = 内外半径**环带**（非矩形）：本机实测内界 r≈232、外界 r≈418、Z 软限≈-145.5mm；
- 错误码：**4007**=超动作区域（外圈够不着）、**4001**=关节超脉冲（内圈收不拢/U 姿态）、**2997**=Z 超软限；
- 脚本钳制只做协议粗筛（±2000）+ OnErr 兜底，可达性唯一裁决 = RC+ 控制器；九点采集前先做可达性空走预检。

## 状态

- 可用；多相机复合（眼在手+固定相机）走位业务在研（见根 STATUS_TODO B2）。
