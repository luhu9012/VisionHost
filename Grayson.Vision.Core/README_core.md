 # Grayson.Vision.Core

`Grayson.Vision.Core` 是 **Grayson 视觉检测平台** 的工位级核心逻辑类库，承载配方执行、硬件设备映射、状态机与事件分发的纯业务核心。

本层严格遵循 **零 UI 污染** 原则：不引用任何 WPF、WinForms 或图像控件程序集，可被 `Grayson.Vision.WorkerHost`（独立进程宿主）或 `Grayson.Vision.FlowEdit`（嵌入式线程宿主）复用。

---

## 技术环境

- 目标框架：`.NET Framework 4.7.2`
- 开发环境：Visual Studio 2026 / C# 7.4
- 平台目标：推荐 `x64`
- 项目类型：类库（Class Library）

---

## 在解决方案中的位置

```text
Grayson.Vision.Contracts
	↑
Grayson.Vision.Core  <- 本层（纯业务核心）
	↑
	├─ Grayson.Vision.WorkerHost   (独立进程宿主)
	└─ Grayson.Vision.FlowEdit      (嵌入式线程宿主 / 观察者客户端)
```

依赖方向：**只允许上层引用 `Grayson.Vision.Core`，禁止 Core 反向依赖任何宿主或 UI 项目。**

---

## 当前实际目录结构

```text
Grayson.Vision.Core/
├── StationWorker.cs                  // 工位核心 Worker：遵循 PackML 状态机，管理配方与执行链
├── StationContext.cs                 // 工位级独立运行上下文：硬件设备池、数据管线
├── Client/
│   ├── StationRuntimeManager.cs      // 全局多工位运行时总管 / UI 直接门面
│   ├── WorkerClientManager.cs        // 多工位 IWorkerClient 缓存与广播管理
│   ├── StationProcessManager.cs      // 外部 WorkerHost.exe 生命周期管理
│   ├── WorkerClientFactory.cs        // 嵌入式/远程 IPC 客户端工厂
│   └── Proxy/
│       ├── EmbeddedWorkerClientProxy.cs   // 同进程嵌入式 Worker 代理
│       └── RemoteWorkerClientProxy.cs     // 跨进程命名管道 Worker 代理
└── Properties/
	└── AssemblyInfo.cs
```

> 说明：`Core` 在保留纯业务核心的同时，新增 `Client` 命名空间，统一封装“嵌入式线程”与“远程 IPC 进程”两种工位访问方式。上层 UI（`WpfUI` / `FlowEdit`）只需面向 `IWorkerClient` 编程，无需关心内部是线程还是进程。

---

## 核心模块说明

### 1. StationWorker（工位核心）

`StationWorker` 实现了 `IStationWorkerHost` 与 `IStationWorkerEvents` 契约，是工位状态、配方执行与事件发布的统一入口。

主要职责：

- **生命周期管理**：`LoadRecipeAsync`、`StartAsync`、`StopAsync`、`TriggerOnceAsync`、`StepNodeAsync`
- **PackML 状态机**：`StationState`（Idle / Stopped / Running / Paused / Faulted）
- **工位运行模式**：`WorkMode`（Debug / Production）
  - `Debug`：支持单步/单次触发、开启全量日志与图像渲染
  - `Production`：监听外部硬触发，极简渲染，追求性能
- **执行链管理**：通过 `ExecutionChain.BuildAndValidate()` 从 `FlowProcessModel` 构建拓扑执行链
- **事件路由**：`OnStateChanged`、`OnNodeExecuting`、`OnNodeExecuted`、`OnFrameRendered`、`OnExecutionCompleted`、`OnExecutionError`
- **图像渲染推送**：节点执行完成后自动从 `OutputPorts` 中提取 `Image` 类型数据，向 UI 广播 `ImageRenderEventArgs`

### 2. StationContext（工位上下文）

`StationContext` 为每个工位维护独立的运行环境，隔离不同工位之间的硬件状态与数据管线。

主要成员：

- `StationId`：工位唯一标识
- `GlobalEngineContext`：工位级全局共享数据管线（`ExecutionContext`）
- `_hardwareDeviceMap`：逻辑设备名称到物理设备实例的映射字典
- `RegisterDevice(string logicalName, object deviceInstance)`：注册/绑定逻辑硬件设备
- `ResolveDevice(string logicalName)`：节点执行时通过 `NodeExecutionContext` 解析实际硬件

> 建议：配方中只记录逻辑设备名（`DeviceKey`），由 `StationContext` 在工位初始化时动态映射为真实硬件实例，实现同一配方在多工位间复用。

### 3. Client（工位访问抽象层）

`Client` 命名空间为上层 UI 提供统一的工位调用入口，屏蔽“同进程线程”与“跨进程 WorkerHost”两种运行模式差异。

#### 3.1 StationRuntimeManager（全局运行时总管）

面向 UI 调用者的顶层门面，负责一键拉起并连接工位：

- `CreateAndConnectStationAsync(stationId, mode)`：根据 `WorkerConnectMode` 创建连接
  - `Embedded`：直接实例化 `EmbeddedWorkerClientProxy`（同进程后台线程）
  - `RemoteIpc`：通过 `StationProcessManager` 启动 `WorkerHost.exe` 后再建立命名管道连接
- 内部缓存所有已连接 `IWorkerClient`，避免重复创建
- `Dispose()` 时统一释放客户端并关闭外部进程

#### 3.2 WorkerClientFactory（客户端工厂）

根据 `WorkerConnectMode` 创建对应 `IWorkerClient` 实例：

- `Embedded` → `EmbeddedWorkerClientProxy`
- `RemoteIpc` → `RemoteWorkerClientProxy`

#### 3.3 EmbeddedWorkerClientProxy（嵌入式代理）

- 在调用方进程内直接实例化 `StationWorker`
- 默认以 `WorkMode.Debug` 运行，适合 `FlowEdit` 快速调试、单工位单进程场景
- 将 `StationWorker` 的所有业务事件原样转发为 `IWorkerClient` 事件

#### 3.4 RemoteWorkerClientProxy（远程 IPC 代理）

- 通过 `NamedPipeClientStream` 连接独立 `WorkerHost` 进程
- 使用 `Newtonsoft.Json` 序列化 `IpcMessage`，完成命令下发与事件广播
- 接收的远程事件包括：`OnStateChanged`、`OnFrameRendered`、`OnNodeExecuting`、`OnNodeExecuted`、`OnExecutionError`、`OnExecutionCompleted`

#### 3.5 StationProcessManager（外部进程管理器）

- 负责启动/停止/清理外部 `Grayson.Vision.WorkerHost.exe`
- 启动参数：`--stationId={stationId}`
- `Dispose()` 时终止所有仍存活的 WorkerHost 进程

#### 3.6 WorkerClientManager（多工位缓存与广播）

- 维护 `stationId → IWorkerClient` 的并发字典
- 提供 `LoadRecipeToAllAsync(recipe)` 向所有已连接工位统一下发配方
- 供 `WpfUI` / `FlowEdit` 在多工位场景下复用

---

## 工位状态流转

```text
Stopped ──LoadRecipeAsync──> Idle ──StartAsync──> Running
  ↑                                              │      │
  │                     StopAsync/Canceled       │      │ TriggerOnceAsync 完成
  └──────────────────────────────────────────────┘      │ （单次运行回 Idle）
														  │
														   Faulted
```

- `LoadRecipeAsync` 失败时进入 `Faulted`，但保留空链/部分链，确保 UI 仍能启动并显示错误。
- 节点执行失败或连续运行取消时，根据 `ChainExecutionResult` 切回 `Faulted` / `Stopped` / `Idle`。

---

## 线程安全

- `StationWorker` 使用 `SemaphoreSlim(1, 1)` 作为执行锁，保证 `TriggerOnceAsync`、`StepNodeAsync`、连续运行循环不会并发执行。
- `StationContext` 使用 `ConcurrentDictionary<string, object>` 管理硬件映射，支持多线程安全访问。

---

## 依赖

- `Grayson.Vision.Contracts`
- `Newtonsoft.Json`（`RemoteWorkerClientProxy` 的 IPC 命令/事件序列化）
- `System` / `System.Core` / `System.IO.Pipes` / `System.Diagnostics`（Client 层进程与管道通信）

---

## 使用方式

### 作为独立进程运行

通常由 `Grayson.Vision.WorkerHost` 控制台程序承载：

```csharp
var worker = new StationWorker("Station_01");
await worker.LoadRecipeAsync(recipe);
await worker.StartAsync();
await worker.TriggerOnceAsync(batchId);
```

### 作为嵌入式线程运行

在 `Grayson.Vison.FlowEdit` 中可直接在同进程内实例化并运行：

```csharp
var worker = new StationWorker("DebugStation_01");
worker.OnFrameRendered += (s, e) => Dispatcher.Invoke(() => imageVm.Render(e));
await worker.LoadRecipeAsync(recipe);
await worker.StartAsync();
```

---

## 设计约束

1. **零 UI 污染**：`Grayson.Vision.Core` 不引用 `PresentationFramework`、`PresentationCore`、`WindowsBase` 等任何 UI 程序集。
2. **事件类型保持弱耦合**：向 UI 推送的 `RenderData` 为 `object` 类型，实际类型由上层渲染服务（如 `HalconImageRenderService`）自行解释与转换。
3. **不处理通信细节**：命名管道、TCP、共享内存等通信机制由 `Grayson.Vision.WorkerHost` 或专门的 IPC 适配层负责，Core 只负责业务事件发布。
4. **工位隔离**：每个 `StationWorker` 拥有独立的 `StationContext`，不同工位间不共享硬件句柄与数据缓存。

---

## 维护提示

- 新增硬件类型时，优先在 `Contracts` 层扩展 `IDevice` 子接口，然后在 `StationContext.RegisterDevice/ResolveDevice` 中试用。
- 调整执行链语义时，同步更新 `FlowExecutor`（Contracts）与 `StationWorker` 中的状态跳转逻辑。
- 若需要在 `Core` 中扩展日志落盘、性能计数等功能，应保持不依赖 UI 的原则。
