 # Grayson.Vision.Core

 `Grayson.Vision.Core` 是 **Grayson 视觉检测平台** 的工位级核心逻辑类库，承载配方执行、设备资源管理、PackML 状态机、工单追踪、调度与事件路由。本层严格遵循 **零 UI 污染** 原则：不引用任何 WPF、WinForms 或图像控件程序集，可被 `Grayson.VisionApp.WpfUI` 与 `Grayson.Vison.FlowEdit` 作为同进程嵌入式宿主复用。

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
 Grayson.Vision.Repository
	 ↑
 Grayson.Vision.Core  <- 本层（纯业务核心）
	 ↑
	 ├─ Grayson.VisionApp.WpfUI   (主程序 / 嵌入式线程宿主)
	 └─ Grayson.Vison.FlowEdit      (流程编辑器 / 嵌入式线程宿主)
 ```

 依赖方向：**只允许上层引用 `Grayson.Vision.Core`，禁止 Core 反向依赖任何宿主或 UI 项目。**

 ---

 ## 当前实际目录结构

 ```text
 Grayson.Vision.Core/
 ├── StationWorker.cs                  // 工位核心 Worker：遵循 PackML 状态机，管理配方与执行链
 ├── StationContext.cs                 // 工位级独立运行上下文：设备管理器、全局参数、数据管线
 ├── Client/
 │   ├── StationRuntimeManager.cs      // 全局多工位运行时总管 / UI 直接门面
 │   ├── WorkerClientManager.cs        // 多工位 IWorkerClient 缓存与广播管理
 │   ├── WorkerClientFactory.cs        // 客户端工厂（当前仅支持嵌入式）
 │   └── Proxy/
 │       └── EmbeddedWorkerClientProxy.cs   // 同进程嵌入式 Worker 代理
 ├── Devices/
 │   ├── DevicePool.cs                 // 全局设备池：插件加载、物理扫描、持久化、工位领用
 │   ├── DevicePoolService.cs          // Core 层轻量设备池服务（插件 + 内存池）
 │   ├── DevicePluginManager.cs        // 硬件驱动插件加载器
 │   ├── DeviceManager.cs              // 工位级设备管理器：注册、生命周期、独占/共享租赁
 │   └── DeviceLease.cs                // 设备租赁凭证默认实现
 ├── Station/
 │   ├── StationHostRuntime.cs         // 同进程多工位运行时根，持有全局设备池与 Worker 实例
 │   ├── WorkOrderTracker.cs           // 工单历史追踪器
 │   └── DefaultStationParameterService.cs  // 工位全局参数默认实现
 ├── Scheduling/
 │   └── SimpleTriggerScheduler.cs     // 简单触发调度器：单次/单步/调试连续循环，支持 Pause/Resume
 ├── Flow/
 │   ├── WorkOrderExecutionContext.cs  // 工单级执行上下文
 │   └── Validation/
 │       └── DefaultPortTypeValidator.cs    // 端口类型校验默认实现
 ├── Recipe/
 │   └── RecipeDeviceExtractor.cs      // 从配方中抽取所需逻辑设备键
 ├── Infrastructure/
 │   └── Alarm/
 │       └── AlarmBus.cs               // 告警总线
 └── Properties/
	 └── AssemblyInfo.cs
 ```

 > 说明：当前版本仅实现 **同进程嵌入式线程** 模式。`RemoteIpc` / 独立 `WorkerHost` 属于未来扩展点，现有代码中并未实现。

 ---

 ## 核心模块说明

 ### 1. StationWorker（工位核心）

 `StationWorker` 实现了 `IStationWorkerHost` 与 `IStationWorkerEvents` 契约，是工位状态、配方执行与事件发布的统一入口。

 主要职责：

 - **生命周期管理**：`LoadRecipeAsync`、`StartAsync`、`StopAsync`、`PauseAsync`、`ResumeAsync`、`TriggerOnceAsync`、`StepNodeAsync`、`EmergencyStopAsync`
 - **PackML 状态机**：`StationState`（Idle / Stopped / Running / Paused / Faulted / Resetting / ErrorLocked）
 - **复位分级**：
   - `WorkOrderResetAsync`：工单级复位，终止当前工单，不改变工位全局状态。
   - `SoftResetAsync`：工位软复位，清空队列/全局变量并重新 Open 设备，不重建硬件句柄。
   - `HardwareResetAsync`：硬件全复位，CloseAll 后重新 OpenAll，用于断连后恢复。
   - `EmergencyStopAsync`：急停，进入 `ErrorLocked`，拒绝新的触发/单步。
 - **工位运行模式**：`WorkMode`（Debug / Production）
   - `Debug`：支持单步/单次触发、开启调试连续循环与全量日志。
   - `Production`：由外部触发驱动，强调性能与稳定性。
 - **执行链管理**：通过 `ExecutionChain.BuildAndValidate()` 从 `FlowProcessModel` 构建拓扑执行链。
 - **事件路由**：`OnStateChanged`、`OnNodeExecuting`、`OnNodeExecuted`、`OnFrameRendered`、`OnExecutionCompleted`、`OnExecutionError`。
 - **图像渲染推送**：节点执行完成后自动从 `OutputPorts` 中提取 `Image` 类型数据，向 UI 广播 `ImageRenderEventArgs`。
 - **可观测性**：暴露 `StationMetrics`（工位级指标聚合）与 `WorkOrderTracker`（最近工单追踪）。

 ### 2. StationContext（工位上下文）

 `StationContext` 为每个工位维护独立的运行环境，隔离不同工位之间的硬件状态与数据管线。

 主要成员：

 - `StationId`：工位唯一标识。
 - `GlobalEngineContext`：工位级全局共享数据管线（`ExecutionContext`）。业务节点只应读取全局参数；写入需授权。
 - `DeviceManager`：工位级设备管理器（`IDeviceManager`），负责逻辑设备注册、生命周期、独占/共享租赁。
 - `StationParameters`：工位全局参数服务（`IStationParameterService`），标定矩阵、工位参数等只读参数，写入需要 `operatorToken`。
 - `RegisterDevice(string logicalName, IDevice deviceInstance)`：注册/绑定逻辑硬件设备。
 - `ResolveDevice(string logicalName)`：兼容旧委托签名，返回已注册/已租赁设备的物理实例；新节点建议通过 `IDeviceLease` 使用设备。

 > 建议：配方中只记录逻辑设备名（`DeviceKey`），由 `StationHostRuntime` 在工位初始化时从 `DevicePool` 领用并映射为真实硬件实例，实现同一配方在多工位间复用。

 ### 3. StationHostRuntime（同进程多工位运行时根）

 `StationHostRuntime` 是单进程内的核心入口，统一持有全局 `DevicePool` 与所有 `StationWorker` 实例。

 主要职责：

 - `InitializeAsync()`：加载插件、恢复已配置设备池。
 - `RegisterSafetyInterlock(ISafetyInterlockService)`：注入安全联锁服务，未注入时保持非联锁运行。
 - `CreateEmbeddedStationAsync(stationId)`：创建嵌入式工位并返回 `IWorkerClient`。
 - `CreateStationWithRecipeAsync(...)`：创建工位并绑定设备映射、加载 `RecipeModel`、设置运行模式。
 - `GetStationClient` / `GetStationWorker` / `GetStationIds`：查询已创建的工位。
 - `RemoveStation(stationId)`：移除工位，归还该工位领用的全部设备。
 - `Dispose()`：统一释放所有 Worker、Client 与设备池。

 ### 4. 设备管理（Devices 命名空间）

 #### 4.1 DevicePool（全局设备池）

 - 进程级单例，供 `StationHostRuntime` 持有。
 - 负责插件加载、物理设备扫描、设备实例化、持久化、CRUD、工位领用管理。
 - 通过 `LeaseDeviceToStation` / `ReturnDeviceFromStation` 实现设备在多工位间的安全领用与归还。

 #### 4.2 DeviceManager（工位级设备管理器）

 - 每个 `StationContext` 拥有一个 `DeviceManager`。
 - 维护本工位的逻辑设备 → 物理设备映射。
 - 支持 `Exclusive`（独占）与 `SharedReadOnly`（共享只读）两种租赁模式。
 - 统一设备状态机转换与事件上报。

 #### 4.3 DeviceLease（租赁凭证）

 - `IDeviceLease` 的默认实现，记录 `StationId`、`WorkOrderId`、`LogicalDeviceKey`、`AccessMode` 等信息。
 - 通过 `Release()` / `Dispose()` 归还设备并恢复状态。

 #### 4.4 DevicePluginManager（插件加载器）

 - 扫描包含 `Plugin` 名称的 DLL，自动加载实现 `IHardwarePlugin` 的类型。
 - 按优先级（`Priority`）与支持的品类/品牌（`Supports`）解析最佳插件。

 ### 5. Scheduling（调度器）

 #### 5.1 SimpleTriggerScheduler

 - `IWorkflowScheduler` 的默认实现，保持现有“单次触发 + 调试连续循环”的行为。
 - `LoadExecutionChainAsync`：加载执行链并创建 `FlowExecutor`。
 - `StartAsync` / `StopAsync`：启动/停止调度器；`Debug` 模式下会启动连续循环。
 - `TriggerOnceAsync`：单次工单触发，自动创建 `WorkOrderExecutionContext`。
 - `StepNodeAsync`：单步执行指定节点，用于节点属性弹窗调试。

 ### 6. Client（工位访问抽象层）

 #### 6.1 StationRuntimeManager

 面向 UI 调用者的顶层门面，内部转发到 `StationHostRuntime`。

 - `CreateAndConnectStationAsync(stationId, mode)`：初始化并创建嵌入式工位（当前仅支持 `Embedded`）。
 - `GetClient(stationId)`：获取已连接的 `IWorkerClient`。
 - `Dispose()`：释放底层 `StationHostRuntime`。

 #### 6.2 WorkerClientFactory

 - 当前版本固定返回 `EmbeddedWorkerClientProxy`。
 - `WorkerConnectMode` 目前仅定义 `Embedded`，保留 `mode` 参数以兼容未来多进程扩展。

 #### 6.3 EmbeddedWorkerClientProxy

 - 在调用方进程内直接持有 `StationWorker`（推荐由 `StationHostRuntime` 传入）。
 - 将 `StationWorker` 的所有业务事件原样转发为 `IWorkerClient` 事件。

 #### 6.4 WorkerClientManager

 - 维护 `stationId → IWorkerClient` 的并发字典。
 - `RegisterAndConnectAsync`：注册并连接工位。
 - `LoadRecipeToAllAsync(recipe)`：向所有已连接工位统一下发配方。

 ### 7. 其他辅助模块

 - `WorkOrderTracker`：保存最近 N 个工单，支持按状态查询。
 - `RecipeDeviceExtractor`：从 `RecipeModel` 与节点参数中提取 `RequiredDeviceKeys`。
 - `DefaultStationParameterService`：运行时内存字典 + `operatorToken` 写入校验。
 - `AlarmBus`：告警分发总线。
 - `DefaultPortTypeValidator`：流程端口类型默认校验。

 ---

 ## 工位状态流转

 ```text
 Stopped ──LoadRecipeAsync──> Idle ──StartAsync──> Running
   ↑                                              │
   │                     StopAsync/Canceled       │ TriggerOnceAsync 完成
   └──────────────────────────────────────────────┤
				  SoftReset / HardwareReset       │
						   │                      │
						   ▼                      │
					   Resetting ────────────────┤
												  │
										   ErrorLocked
											 ▲
											 │ EmergencyStopAsync
											 │
										  Faulted
											 ▲
											 │ 执行失败 / LoadRecipeAsync 校验失败
 ```

 - `LoadRecipeAsync` 失败时进入 `Faulted`，但保留空链/部分链，确保 UI 仍能启动并显示错误。
 - 节点执行失败时进入 `Faulted`。
 - 连续运行取消时，根据 `ChainExecutionResult` 与执行链状态切回 `Idle` 或 `Stopped`。
 - `ErrorLocked` 状态下拒绝新的 `TriggerOnceAsync` 与 `StepNodeAsync`，需通过复位流程解锁。

 ---

 ## 线程安全

 - `StationWorker` 的调度器内部使用 `SemaphoreSlim(1, 1)` 作为执行锁，保证 `TriggerOnceAsync`、`StepNodeAsync` 与连续运行循环不会并发执行。
 - `StationContext` 通过 `IDeviceManager` 管理设备，底层使用 `ConcurrentDictionary` 与租赁锁保证并发安全。
 - `StationHostRuntime` 使用 `ConcurrentDictionary` 缓存 Worker 与 Client，支持同进程内多工位并发访问。
 - `DevicePool` 使用 `ConcurrentDictionary` 与租约映射保证设备领用/归还的线程安全。

 ---

 ## 依赖

 - `Grayson.Vision.Contracts`（接口、模型、契约）
 - `Grayson.Vision.Repository`（设备配置等持久化实体/仓储；当前 `DevicePool` 使用）
 - `Newtonsoft.Json`（JSON 序列化）
 - `System` / `System.Core` / `System.IO.Pipes` / `System.Diagnostics`（进程与管道通信预留）

 ---

 ## 使用方式

 ### 推荐方式：通过 StationHostRuntime 管理多工位

 ```csharp
 var hostRuntime = new StationHostRuntime();
 await hostRuntime.InitializeAsync();

 var client = await hostRuntime.CreateStationWithRecipeAsync(
	 "Station_01",
	 recipeModel,
	 deviceMappings,
	 WorkMode.Production);

 client.OnStateChanged += (s, state) => Console.WriteLine($"State: {state}");
 client.OnFrameRendered += (s, e) => renderService.Render(e.RenderData);

 await client.StartAsync();
 await client.TriggerOnceAsync(batchId);
 ```

 ### 作为嵌入式线程运行（FlowEdit 调试）

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
 3. **工位隔离**：每个 `StationWorker` 拥有独立的 `StationContext`，不同工位间不共享硬件句柄与数据缓存。
 4. **设备租赁优先**：节点不再直接持有设备实例，应通过 `IDeviceLease` 申请与释放设备。
 5. **同进程模型**：当前版本所有 StationWorker 运行在宿主进程内，通过跨线程事件推送状态；独立 WorkerHost / IPC 为后续扩展预留。

 ---

 ## 维护提示

 - 新增硬件类型时，优先在 `Contracts` 层扩展 `IDevice` 子接口，然后通过 `DevicePluginManager` 加载对应插件。
 - 调整执行链语义时，同步更新 `FlowExecutor`（Contracts）与 `StationWorker` / `SimpleTriggerScheduler` 中的状态跳转逻辑。
 - 若需要在 `Core` 中扩展日志落盘、性能计数等功能，应保持不依赖 UI 的原则。
 - 修改 `StationContext` 全局参数写入权限时，同步更新 `DefaultStationParameterService.CanWrite` 与相关节点调用方。
