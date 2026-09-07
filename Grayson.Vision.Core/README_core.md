# Grayson.Vision.Core — 运行层（工位宿主 / 业务 Process / 设备池）

> 校正：2026-09-07。承载"工位怎么跑起来"：状态机、流程驱动、设备资源、触发、调度与业务 Process。
> **纪律：零 UI 污染**——不引 WPF/WinForms/HALCON/具体硬件 SDK，可被 WpfUI 与 FlowEdit 同进程嵌入复用。

## 1. 目录结构

| 目录/文件 | 职责 |
|---|---|
| `StationWorker.cs` / `StationContext.cs` | 工位运行时宿主：常驻线程、状态机、事件路由（Worker 事件直连具名方法） |
| `Station\StationHostRuntime.cs` | 宿主运行时注册表（多工位托管；通过接口暴露给 UI） |
| `Station\DefaultStationParameterService.cs` | 工位参数默认值服务 |
| `Station\WorkOrderTracker.cs` | 工单追踪（追溯用，UI 经接口读取） |
| `Processes\` | 业务流程族：`VisionPickPlaceProcess(+Config)`、`MahjongPickProcess(+Config)`、`MahjongDualNozzleProcess(+Config)`、`StationProcessBase`、`StationProcessFactory`（按流程键产 Process）、`ProcessConfigOverlay` |
| `Devices\DevicePool.cs` | 设备池：领用/释放/健康/热插 |
| `Triggers\` | 触发源统一入口（软触发/信号/手动） |
| `Scheduling\` | 调度（执行节拍/串并行） |
| `Flow\` | 流程装配（把配方节点图实例化为可执行链） |
| `Recipe\` | 配方装配到工位的服务 |
| `Client\` | Worker 客户端抽象（UI 侧控制代理的契约在 Contracts） |
| `Infrastructure\` | 运行基础设施 |

## 2. 核心运行链路

```text
StationConfigService(读 LiteDB ProcessConfigJson)
  └▶ StationWorker ─▶ StationProcessFactory ─▶ 具体 Process(业务参数来自 *Config)
       └▶ Triggers 收到触发 ─▶ 采集/视觉/走位/回传(节点或内建流程)
       └▶ 事件上抛 UI(Dispatcher), 日志/指标/工单写入
```

配置持久化铁律：**已建档工位以 LiteDB `ProcessConfigJson` 为准，改 .cs 默认值零效果**——参数一律界面写回再保存（`StationConfigService.LoadAllLines → SaveStation → worker.DesiredProcessConfigJson + 热挂`）。

## 3. 扩展指南：加一个业务 Process

1. 在 `Contracts\Station`（或业务区）定义配置模型字段；
2. 建 `Processes\XxxProcess.cs`（继承 `StationProcessBase`）+ `XxxConfig.cs`；
3. `StationProcessFactory` 注册流程键 ↔ Process 映射；
4. 需要跨进程热挂则实现 Worker 配置热更（参照现有 Mahjong 系列）；
5. 跑回归断言 + 双 bin 构建（见根 STATUS_TODO §4）。

## 4. 关键公式/语义（勿凭记忆改）

- EyeInHand 镜像、偏心、回转中心目标公式见根 `ARCHITECTURE.md` §4.7（代码注释与设计文档同源）；
- VisionPickPlace 相机来源由配方 AcquireImage 决定（眼在手/固定上相机）；下相机检知归正为预留语义；
- Z 高度一致性 ±5mm（HomMat 是拍照高度 2D 仿射）。

## 5. 关联文档

- 根 `ARCHITECTURE.md` §4.2 / §4.7；`STATUS_TODO.md`（Process 域状态）
- `定位抓取业务_配方编排与快速落地_方案_2026-09-05.md`；`MahjongDualNozzle工位业务_补齐清单与Epson模拟器接入.md`（历史背景）
