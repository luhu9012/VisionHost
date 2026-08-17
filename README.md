# Grayson.Vision.Host 解决方案说明

本仓库是 **Grayson 视觉检测平台** 的主机端解决方案，基于 **.NET Framework 4.7.2** 构建，采用 WPF 进行界面开发，核心围绕 **可拖拽流程节点编辑器**、**工业相机**、**PLC**、**Halcon 图像处理**、**设备资源管理**、**工单追踪**、**工位 PackML 状态机** 等能力展开。

> 设计约束：
> - `Grayson.Vision.Contracts` 与 `Grayson.Vision.Core` 不引用任何 WPF / Halcon / 具体硬件 SDK。
> - `Grayson.VisionApp.WpfUI` 只承担 UI 交互与显示门面（Facade），不持有设备池、调度策略、工位状态机等核心职责。
> - 设备生命周期、资源租赁、调度器、配方配置、工单追踪、指标告警等能力逐步集中到 `Core`。

---

## 1. 目标框架与运行环境

- **目标框架**：`.NET Framework 4.7.2`
- **解决方案文件**：`Grayson.Vision.Host.slnx`
- **主要 UI 技术**：WPF（Windows Presentation Foundation）
- **平台目标**：推荐 `x64`
- **外部依赖/中间件**：
  - `halcondotnet`（Halcon 机器视觉库）
  - `MvCamCtrl.Net`（海康工业相机 SDK）
  - `Newtonsoft.Json`（JSON 序列化）
  - `DynamicExpresso.Core`（动态表达式求值）

---

## 2. 项目一览

| 项目名称 | 输出类型 | 主要用途 | 目标框架 |
|---|---|---|---|
| `Grayson.Vision.Contracts` | 类库 (Library) | 核心接口、领域模型、流程引擎抽象、设备抽象、通用服务契约、Worker 宿主契约 | .NET Framework 4.7.2 |
| `Grayson.Vision.Core` | 类库 (Library) | 工位核心：`StationHostRuntime`、`StationWorker`、`StationContext`、设备池/租赁、调度器、状态机、事件路由 | .NET Framework 4.7.2 |
| `Grayson.Vision.HalconWrapper` | 类库 (Library) | 对 Halcon 图像处理 API 的业务化封装，提供标定、匹配、测量、滤波等工具 | .NET Framework 4.7.2 |
| `Grayson.Vision.HalconWrapper.Wpf` | WPF 类库 (Library) | Halcon 图像的 WPF 显示封装，封装 `HSmartWindowControlWPF`、渲染服务、缩略图 | .NET Framework 4.7.2 |
| `Grayson.Vision.Nodes` | 类库 (Library) | 流程节点实现：DeviceIO / Logic 等分类节点，以及对应参数面板 XAML | .NET Framework 4.7.2 |
| `Grayson.Vison.FlowEdit` | WPF 程序 (WinExe) | 独立的视觉流程编辑器，支持拖拽节点、图像显示、节点属性配置、配方管理；同时可作为嵌入式线程调试宿主 | .NET Framework 4.7.2 |
| `Grayson.VisionApp.WpfUI` | WPF 程序 (WinExe) | 主程序宿主界面（登录、主框架、设备池、流程编辑、报警、配方、用户管理等） | .NET Framework 4.7.2 |
| `Grayson.Vision.Repository` | 类库 (Library) | 基于 LiteDB 的数据持久化层，负责配方、工位配置、历史记录等 PO 与仓储 | .NET Framework 4.7.2 |
| `Plugins.Camera.Hikvision` | 类库 (Library) | 海康工业相机插件，实现 `ICamera` 接口 | .NET Framework 4.7.2 |
| `Plugins.Motion.Zmc` | 类库 (Library) | ZMC 运动控制卡插件，实现 `IMotion` 接口 | .NET Framework 4.7.2 |
| `Plugins.Protocol.Universal` | 类库 (Library) | 通用通信协议插件 | .NET Framework 4.7.2 |

> 说明：项目名称中 `Grayson.Vison.FlowEdit` 保留了仓库中的原始拼写。

---

## 3. 分层架构与职责分离

本方案采用倒置依赖的分层结构，越底层的项目越稳定，越上层的项目越接近具体 UI 与硬件：

```text
┌─────────────────────────────────────────────────────────────────────┐
│  应用层 (WinExe)                                                    │
│  Grayson.VisionApp.WpfUI / Grayson.Vison.FlowEdit                   │
├─────────────────────────────────────────────────────────────────────┤
│  工位核心逻辑层                                                     │
│  Grayson.Vision.Core                                                │
│  (StationHostRuntime / StationWorker / StationContext)              │
├─────────────────────────────────────────────────────────────────────┤
│  插件与节点层                                                       │
│  Grayson.Vision.Nodes / Plugins.Camera.Hikvision /                  │
│  Plugins.Motion.Zmc / Plugins.Protocol.Universal                    │
├─────────────────────────────────────────────────────────────────────┤
│  基础设施层                                                         │
│  Grayson.Vision.HalconWrapper.Wpf (WPF 显示)                        │
│  Grayson.Vision.HalconWrapper (纯 Halcon 算法)                      │
│  Grayson.Vision.Repository (数据持久化)                             │
├─────────────────────────────────────────────────────────────────────┤
│  契约层                                                             │
│  Grayson.Vision.Contracts (接口、模型、枚举、通用服务契约)            │
└─────────────────────────────────────────────────────────────────────┘
```

### 3.1 核心运行架构

当前版本采用 **严格分层 + 单进程内多线程 Worker 宿主** 模式运行 Station 工作流。`StationHostRuntime` 是全局同进程运行入口：

```text
应用层 / 编排 UI
	Grayson.VisionApp.WpfUI / Grayson.Vison.FlowEdit
			│ 编辑配方 / 发送指令 / 订阅状态     ▲ 推送图像帧 / 节点执行状态 / 日志 / 指标 / 告警
			│（禁止直接操作节点、设备 SDK）      │
			└────────── 跨线程事件 / Dispatcher ──┘
			│                                    │
StationHost 运行时层
	Grayson.Vision.Core.Station.StationHostRuntime
			│ 全局设备池 (DevicePool)、多工位 Worker 缓存、统一生命周期
			▼
WorkStation 工位层
	Grayson.Vision.Core.StationWorker
			│ PackML 顶层状态机（Idle / Stopped / Running / Paused / Faulted / Resetting / ErrorLocked）
			│ 调度器托管、生命周期、安全联锁、复位分级
			▼
Scheduler 调度层
	Grayson.Vision.Contracts.Station.Interfaces.IWorkflowScheduler
	Grayson.Vision.Core.Scheduling.SimpleTriggerScheduler
			│ 工单级执行、WorkOrder 生命周期、设备租赁申请、单步/连续触发
			▼
Blueprint / Node 层
	Grayson.Vision.Contracts.Flow.Nodes / INodeExecutor
			│ 只读写 WorkOrder 上下文，通过注入的设备代理调用硬件
			▼
Device 设备层
	Grayson.Vision.Core.Devices.DevicePool / DeviceManager
	Grayson.Vision.Contracts.Devices.IDevice / IDeviceLease / IDeviceManager
			│ 硬件 SDK、状态机、重连、资源租赁、Mock / 仿真
```

- **StationHostRuntime**：进程级单例（推荐），统一持有 `IDevicePool`、多工位 `StationWorker` 与 `IWorkerClient` 缓存。UI 通过它创建/销毁工位，避免每处各自实例化设备池。
- **工位（Station）**：拥有唯一的 `StationId`；`StationWorker` 是顶层状态机，不直接参与节点执行细节。
- **配方（Recipe）**：`RecipeModel` 拆分如下：
  - 业务流（`MainProcess` / `SubProcesses`）：画布编排的业务逻辑；
  - 工位运行配置（`StationRuntimeConfiguration`）：调度器、超时、IO 映射、告警阈值；
  - 工艺参数集（`ProcessParameterSet`）：与产品绑定的阈值、曝光、AI 模型、标定结果。
- **设备资源管理**：`IDeviceManager`/`DeviceManager` 负责设备注册、Open / Close、独占/共享租赁；`IDeviceLease` 是工单持有设备句柄的凭证；节点不再直接持有设备实例。全局设备池 `DevicePool` 负责插件加载、物理扫描、持久化与工位领用。
- **工单追踪**：`WorkOrder` 强类型记录 `Created → Running → Completed_OK / Completed_NG / Abort_*` 生命周期；`WorkOrderTracker` 保存最近工单历史。
- **可观测性**：`LogBus` 支持 Category 过滤与工单级日志；`StationMetrics` 聚合 OK/NG、节点耗时、设备重连、队列长度；`AlarmBus` 分发 Information/Warning/Fault 告警。
- **单进程运行**：当前版本所有 `StationWorker` 运行在宿主进程内（`WpfUI` 或 `FlowEdit`），通过跨线程事件与 `Dispatcher` 将状态/图像/日志推送到 UI，无 IPC 序列化开销，适合单机视觉检测场景。
- **嵌入式调试**：`Grayson.Vison.FlowEdit` 可直接创建 `StationWorker` 或经由 `StationHostRuntime` 进行单步 / 连续调试。

---

## 4. 主要入口与使用方式

### 4.1 推荐入口：StationHostRuntime

在主程序中，建议全局共享一个 `StationHostRuntime`：

```csharp
var host = new StationHostRuntime();
await host.InitializeAsync(); // 加载插件、恢复设备池

// 创建工位并绑定配方与设备映射
var client = await host.CreateStationWithRecipeAsync(
	"Station_01",
	recipe,
	deviceMappings,
	WorkMode.Production
);

client.OnStateChanged += (s, e) => { /* 更新 UI 状态 */ };
client.OnFrameRendered += (s, e) => Dispatcher.Invoke(() => imageVm.Render(e));

await client.StartAsync();
```

### 4.2 UI 门面：StationRuntimeManager / WorkerClientManager

```csharp
// 简化的 UI 门面，内部转发到 StationHostRuntime
var manager = new StationRuntimeManager(host);
var client = await manager.CreateAndConnectStationAsync("Station_01", WorkerConnectMode.Embedded);

// 多工位缓存与广播
var multiManager = new WorkerClientManager();
await multiManager.RegisterAndConnectAsync("Station_01", WorkerConnectMode.Embedded);
await multiManager.LoadRecipeToAllAsync(recipe.MainProcess);
```

### 4.3 底层调试：直接使用 StationWorker

```csharp
var worker = new StationWorker("DebugStation_01");
worker.OnFrameRendered += (s, e) => Dispatcher.Invoke(() => imageVm.Render(e));
await worker.LoadRecipeAsync(recipe.MainProcess);
await worker.StartAsync();
```

---

## 5. 项目职责速查

### 5.1 Grayson.Vision.Contracts

**契约层，不依赖 WPF、Halcon 等具体技术。** 所有上层项目都依赖它。主要包含：

- **视图模型基类**：`ViewModelBase`、`RelayCommand<T>`
- **通用模型与结果**：`Result<T>`、`Pose3D`
- **工位 Worker 契约**：
  - `IStationWorkerHost`：工位宿主统一接口（`LoadRecipeAsync`、`StartAsync`、`StopAsync`、`TriggerOnceAsync`）
  - `IStationWorkerEvents`：状态/节点/执行链/日志事件集合
  - `IStationHostRuntime`：进程级运行时根接口
  - `IWorkflowScheduler`：调度器抽象
  - `StationState`、`WorkMode`、`ImageRenderEventArgs`、`ChainCompletedEventArgs`
- **业务节点接口与模型**：
  - `INodeExecutor`、节点特性 `NodeAttribute` / `NodePortAttribute`
  - 流程节点模型：`FlowNode`、`FlowNodeBase`、`FlowModels`、`CompositeFlowNode`
  - 执行上下文：`NodeExecutionContext`、`ExecutionContext`、`FrameCycleContext`
  - 流程执行器：`FlowExecutor`
  - 节点工厂：`NodeFactory`
- **设备抽象接口**：`IDevice`、`ICamera`、`IPlc`、`IHardwarePlugin`、`IDeviceManager`、`IDevicePool`、`IDeviceLease`
- **通用服务契约**：`IDialogService`、`IFileDialogService`、`IFlowLayoutService`
- **插件加载契约**：`INodePluginLoader`
- **图像渲染契约（无 UI）**：`IRenderImage`、`ImageRenderContext`、`ImageOverlay`、`IImageRenderService`、`IImageDisplayHost`
- **权限**：`UserRole`

### 5.2 Grayson.Vision.Core

**工位核心逻辑层，零 UI 污染。** 只引用 `Contracts`，承载全局同进程宿主、设备管理、调度、工单追踪、状态机、事件路由。完整说明请参阅 [`Grayson.Vision.Core/README_core.md`](Grayson.Vision.Core/README_core.md)。

主要类型：

- `StationHostRuntime`：全局同进程运行时根，持有 `IDevicePool`、所有工位 `StationWorker` 与 `IWorkerClient`。
- `StationWorker`：实现 `IStationWorkerHost` 与 `IStationWorkerEvents`。
  - 管理 `StationState`：Idle / Stopped / Running / Paused / Faulted / **Resetting** / **ErrorLocked**。
  - 加载配方 → 构建 `ExecutionChain`；通过 `IWorkflowScheduler` 驱动执行，自己只负责顶层状态机与事件转发。
  - 提供 **四级复位/停止**：`WorkOrderResetAsync`（工单级）、`SoftResetAsync`（工位软复位）、`HardwareResetAsync`（硬件全复位）、`EmergencyStopAsync`（急停 → ErrorLocked）。
  - 暴露 `StationMetrics`、`WorkOrderTracker`。
- `StationContext`：工位独立运行上下文。
  - `IDeviceManager DeviceManager`：统一设备注册、生命周期、独占/共享租赁。
  - `IStationParameterService StationParameters`：工位全局参数（标定矩阵、工位配置），写入需要 `operatorToken`。
  - `ExecutionContext GlobalEngineContext`：工位全局共享数据总线，节点可通过它读取全局参数，业务节点禁止随意写入。
- `Devices/DevicePool.cs`、`Devices/DeviceManager.cs`：全局设备池与工位级设备租赁。
- `Scheduling/SimpleTriggerScheduler.cs`：单次触发 + Debug 连续循环的默认调度器。
- `Station/WorkOrderTracker.cs`：最近工单历史。
- `Client/StationRuntimeManager.cs`、`Client/WorkerClientManager.cs`、`Client/EmbeddedWorkerClientProxy.cs`：UI 入口与多工位广播。

### 5.3 Grayson.Vision.HalconWrapper / .Wpf

- `HalconWrapper`：纯算法层，不引用 WPF，封装 Halcon 标定、Blob、匹配、测量、滤波等。
- `HalconWrapper.Wpf`：WPF 渲染层，封装 `HSmartWindowControlWPF`、图像显示服务、缩略图转换。

### 5.4 Grayson.Vision.Nodes

流程节点实现，按分类组织（如 DeviceIO、Logic、Halcon 等）。每个节点通常包含：

- 节点执行器（`INodeExecutor` 实现）
- 参数模型（可序列化）
- 参数面板 XAML / ViewModel（与节点插件配套）

### 5.5 Grayson.Vison.FlowEdit

- 独立的视觉流程编辑器 WinExe。
- 拖拽节点画布、属性面板、图像显示、配方保存/加载。
- 可作为嵌入式线程宿主直接创建 `StationWorker` 或 `StationHostRuntime` 进行调试。

### 5.6 Grayson.VisionApp.WpfUI

- 主程序宿主 WinExe。
- 登录、主框架、设备池管理、流程编辑、报警、配方、用户管理等。
- 通过 `StationRuntimeManager` 或 `WorkerClientManager` 访问工位，禁止直接操作 `StationWorker` 或设备 SDK。

### 5.7 Grayson.Vision.Repository

- 基于 LiteDB 的数据持久化层。
- 配方、工位配置、设备配置、历史记录等 PO 与仓储实现。

### 5.8 插件项目

| 项目 | 说明 |
|---|---|
| `Plugins.Camera.Hikvision` | 海康工业相机插件，实现 `ICamera`。 |
| `Plugins.Motion.Zmc` | ZMC 运动控制卡插件，实现 `IMotion`。 |
| `Plugins.Protocol.Universal` | 通用通信协议插件（串口 / TCP / UDP）。 |

---

## 6. 工位状态机

`StationWorker` 采用 PackML 风格状态机：

```text
Stopped ──LoadRecipeAsync──> Idle ──StartAsync──> Running
  ↑                                              │      │
  │                     StopAsync / Canceled     │      │ TriggerOnceAsync 完成
  └──────────────────────────────────────────────┘      │   （单次运行回 Idle）
														  │
												 Faulted  │  ErrorLocked
														 Fault
														  │
												 任何错误   │  EmergencyStopAsync
														  │
												 Soft/Hardware
												 Reset ──>  Resetting ──> Idle
```

- `Resetting`：复位中，执行 `SoftResetAsync` 或 `HardwareResetAsync` 期间进入。
- `ErrorLocked`：急停锁定，需人工干预后才能重新初始化。
- `LoadRecipeAsync` 失败时进入 `Faulted`，但保留空链/部分链，确保 UI 仍能启动并显示错误。
- 节点执行失败或连续运行取消时，根据 `ChainExecutionResult` 切回 `Faulted` / `Stopped` / `Idle`。

---

## 7. 设计约束

1. **零 UI 污染**：`Grayson.Vision.Contracts` 与 `Grayson.Vision.Core` 不引用 `PresentationFramework`、`PresentationCore`、`WindowsBase` 等任何 UI 程序集。
2. **倒置依赖**：上层项目（WPF UI、插件）引用下层项目（Contracts、Core），Core 不反向依赖任何宿主或 UI 项目。
3. **设备租赁**：节点不应直接持有设备实例；执行期间通过 `IDeviceLease` 申请与释放设备。
4. **单进程宿主**：当前版本所有工位运行在宿主进程内，通过事件与 `Dispatcher` 与 UI 交互；独立的跨进程 WorkerHost 仅作为未来扩展点保留。
5. **工位隔离**：每个 `StationWorker` 拥有独立的 `StationContext`，不同工位间不共享硬件句柄与数据缓存。

---

## 8. Quick Start

```bash
# 1. 克隆后用 Visual Studio 2026 打开 slnx
Grayson.Vision.Host.slnx

# 2. 还原 NuGet 包
# 3. 编译并启动 Grayson.VisionApp.WpfUI（或 Grayson.Vison.FlowEdit）
```

如需在 `FlowEdit` 中快速调试流程：

```csharp
var host = new StationHostRuntime();
await host.InitializeAsync();
var client = await host.CreateEmbeddedStationAsync("Debug_01");
await client.LoadRecipeAsync(recipe.MainProcess);
await client.StartAsync(); // Debug 模式下会自动进入连续触发循环
```

---

## 9. 相关文档

- [`Grayson.Vision.Core/README_core.md`](Grayson.Vision.Core/README_core.md) — 工位核心详细说明
- [`Grayson.Vision.Nodes/README.md`](Grayson.Vision.Nodes/README.md) — 节点开发说明
- [`Grayson.Vison.FlowEdit/README.md`](Grayson.Vison.FlowEdit/README.md) — 流程编辑器说明
