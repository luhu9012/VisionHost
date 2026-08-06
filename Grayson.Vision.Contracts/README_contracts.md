# Grayson.Vision.Contracts

本项目是 **Grayson.Vision.Host** 解决方案的契约层（Contracts Layer）。
作为整个视觉平台的最顶层依赖，它只包含抽象的接口、模型、枚举与基础工具，**不引用任何第三方 SDK**（包括 Halcon、相机 SDK、PLC SDK 等），以保证所有上层模块（Common、HalconWrapper、硬件插件、业务节点、UI）都能稳定依赖。

---

## 技术环境

- 目标框架：`.NET Framework 4.7.2`
- 开发环境：Visual Studio 2026 / C# 7.4
- 平台目标：推荐 `x64`（适配 Halcon 与各类硬件 SDK）
- 项目类型：类库（Class Library）

---

## 在解决方案中的位置

```
Grayson.Vision.Host.slnx
├─ Grayson.Vision.Contracts          <- 本层，无第三方依赖
├─ Grayson.Vision.Common
├─ Grayson.Vision.Core
├─ Grayson.Vision.HalconWrapper
├─ Grayson.Vision.HalconWrapper.Wpf
├─ Grayson.Vision.Nodes
├─ Grayson.Vision.WorkerHost
├─ Grayson.VisionApp.WpfUI
├─ Grayson.Vison.FlowEdit
├─ Plugins.Camera.Hikvision
└─ Plugins.PLC.Siemens
```

依赖方向：**只允许上层引用 Contracts，禁止 Contracts 反向依赖任何上层项目。**

---

## 当前实际目录结构

```
Grayson.Vision.Contracts
├─ Core                                // 基础公共类型与结构
│  ├─ Models
│  │  ├─ Result.cs                     // 通用执行结果模型
│  │  └─ Result<T>                     // 泛型执行结果模型（与 Result.cs 同文件）
│  └─ Geometry
│     └─ Pose3D.cs                     // 全局统一位姿结构体
│
├─ Station                             // 工位与 Worker 宿主契约
│  ├─ Interfaces
│  │  ├─ IStationWorkerHost.cs         // 工位 Worker 宿主契约
│  │  └─ IStationWorkerEvents.cs       // 工位事件暴露接口
│  ├─ Models
│  │  ├─ ImageRenderEventArgs.cs       // 图像渲染推送事件参数
│  │  └─ ChainCompletedEventArgs.cs    // 执行链完成事件参数
│  └─ Enums
│     ├─ StationState.cs               // 工位运行状态枚举
│     └─ WorkMode.cs                   // 工位运行模式（Debug / Production）
│
├─ Flow                                // 流程图与节点引擎契约
│  ├─ Nodes
│  │  ├─ FlowNodeBase.cs               // 所有流程节点基类
│  │  ├─ FlowNode.cs                   // 具体画布节点
│  │  ├─ CompositeFlowNode.cs          // 复合（Group）节点
│  │  └─ FlowModels.cs                 // 端口 / 连线 / 流程容器 / 工具箱元数据
│  ├─ Executants
│  │  ├─ INodeExecutor.cs              // 节点算子执行器接口
│  │  ├─ ExecutionChain.cs             // 节点拓扑执行链（含验证与排序）
│  │  └─ FlowExecutor.cs               // 流程执行引擎（Kahn 拓扑排序驱动）
│  ├─ Contexts
│  │  ├─ ExecutionContext.cs           // 流程执行全局数据管线
│  │  ├─ NodeExecutionContext.cs       // 单个节点执行上下文
│  │  └─ FrameCycleContext.cs          // 单次节拍/触发生命周期上下文
│  ├─ Attributes
│  │  ├─ NodeAttribute.cs              // 节点执行器元数据特性
│  │  ├─ NodePortAttribute.cs          // 节点端口声明特性
│  │  └─ NodeFieldMetaAttribute.cs     // 节点字段 UI 元数据（图标/颜色/描述）
│  ├─ Factories
│  │  └─ NodeFactory.cs                // 流程节点工厂（扫描 / 注册 / 创建）
│  ├─ Enums
│  │  └─ NodeFlowEnums.cs              // 节点分类 / 端口 / 节点类型枚举
│  └─ Helpers
│     └─ EnumExtensions.cs             // 枚举 Description / Attribute 扩展
│
├─ Recipe                              // 配方与流程模型
│  ├─ Models
│  │  └─ RecipeModel.cs                // 配方业务模型
│  └─ DTOs
│     ├─ RecipeDto.cs                  // 配方持久化数据传输对象
│     └─ RecipeConverter.cs            // 配方 DTO 与业务模型互转
│
├─ Devices                             // 硬件抽象层契约
│  ├─ Interfaces
│  │  ├─ IDevice.cs                    // 所有硬件顶层接口
│  │  ├─ ICamera.cs                    // 工业相机通用接口
│  │  ├─ IPlc.cs                       // PLC 通用读写接口
│  │  └─ IHardwarePlugin.cs            // 硬件插件工厂接口
│  └─ Enums
│     └─ DeviceState.cs                // 设备通用状态枚举
│
├─ Ipc                                 // 进程间通信消息契约（WorkerHost <-> UI）
│  ├─ Models
│  │  ├─ IpcMessage.cs                 // IPC 消息类型与统一封包
│  │  ├─ NodeErrorPayload.cs           // 节点执行异常序列化载荷
│  │  └─ IpcLogSink.cs                 // 跨进程日志接收模型
│  └─ Enums
│     └─ IpcMessageType.cs             // IPC 消息类型枚举
│
├─ Imaging                             // 跨进程 / UI 无关的图像与渲染抽象
│  ├─ Interfaces
│  │  ├─ IRenderImage.cs               // 与图像库无关的渲染图像句柄
│  │  ├─ IImageRenderService.cs        // 图像渲染服务契约
│  │  └─ IImageDisplayHost.cs          // 图像显示宿主契约
│  └─ Models
│     ├─ ImageRenderContext.cs         // 渲染上下文
│     └─ ImageOverlay.cs               // 图像叠加图元抽象
│
├─ Infrastructure                      // UI / 通用横切服务契约与基类
│  ├─ Mvvm
│  │  ├─ ViewModelBase.cs              // MVVM 基类
│  │  └─ RelayCommand.cs               // 命令实现
│  ├─ Logging
│  │  ├─ LogBus.cs                     // 全局日志总线
│  │  └─ FileLogSink.cs                // 文件日志落盘
│  ├─ Services
│  │  ├─ IDialogService.cs             // 通用对话框服务
│  │  ├─ IFileDialogService.cs         // 文件对话框服务
│  │  └─ IFlowLayoutService.cs         // 流程图自动布局服务
│  ├─ Permission
│  │  └─ UserRole.cs                   // 系统角色枚举
│  └─ Plugin
│     └─ INodePluginLoader.cs          // 节点插件加载器契约
│
└─ Properties
   └─ AssemblyInfo.cs
```

---

## 核心模块说明

### 0. Station —— 工位 Worker 宿主契约

为适应新版 **Worker-Host + IPC + Observer UI** 架构，Contracts 在 `Station/Interfaces` 下定义了工位 Worker 的统一接口与事件模型，供 `Grayson.Vision.Core` 的 `StationWorker`、控制台 `Grayson.Vision.WorkerHost` 以及 `Grayson.Vison.FlowEdit` 的嵌入式宿主共同实现或消费。

#### StationState（工位状态）

- `Idle`：初始空闲/配方就绪状态
- `Stopped`：处于停止/空闲状态
- `Running`：正在运行/准备响应触发
- `Paused`：已暂停
- `Faulted`：发生异常报错

#### IStationWorkerHost

工位 Worker 宿主契约，包含：

- `StationId`：当前工位唯一标识
- `State`：当前运行状态
- `LoadRecipeAsync(FlowProcessModel recipe)`：加载/更新配方（含流程图拓扑与节点参数）
- `StartAsync()` / `StopAsync()`：启动/停止工位
- `TriggerOnceAsync(string batchId = null)`：触发单次节拍运行
- `OnStateChanged` / `OnFrameRendered`：状态变更与渲染帧推送事件

#### IStationWorkerEvents

统一汇总的工位事件暴露接口，补充 `IStationWorkerHost` 未包含的节点级事件：

- `OnNodeExecuting` / `OnNodeExecuted`：节点开始/结束执行
- `OnExecutionError`：节点执行异常
- `OnExecutionCompleted`：执行链完成（含 `ChainExecutionResult`）
- `OnLogReceived`：日志接收

#### ImageRenderEventArgs

跨进程/跨线程图像渲染事件参数：

- `StationId`、`NodeId`、`NodeName`：定位信息
- `ImagePathOrBufferId`：图像缓存 ID 或共享内存路径（避免直接序列化大 Bitmap）
- `RenderData`：额外 ROI、检测框、测量数据

#### WorkMode

工位运行模式：

- `Debug`：调试/编排模式，支持单步/单次触发，开启全量日志与图像渲染
- `Production`：自动/产线模式，监听外部硬触发，极简渲染，追求极致性能

### 1. Core

- **`Result / Result<T>`**：全系统统一的执行结果封装，承载 `Success`、`ErrorCode`、`Message`、`Exception`。
- **`Pose3D`**：全局统一位姿结构体（X/Y/Z 平移，Rx/Ry/Rz 旋转），用于定位结果、机械手坐标、运动轴坐标等场景。

### 2. Devices

定义所有硬件设备的抽象契约，上层通过 `IDevice`/`ICamera`/`IPlc` 解耦访问，避免直接依赖厂商 SDK。

- **`IDevice`**：硬件通用生命周期（连接、断开、状态检查、参数读写）。
- **`IDevice.DeviceCategory`**：设备大类枚举，包含 `Camera`、`MotionCard`、`PLC`、`LightController`。
- **`ICamera`**：相机通用接口，含帧回调 `FrameReceived`、曝光/增益/触发/软触发/连续采集等。
- **`IPlc`**：PLC 通用点位读写（Bit/Int/Float）。
- **`IHardwarePlugin / DeviceInfo`**：硬件插件工厂契约及设备扫描元数据，供反射动态加载插件使用。

### 3. Flow —— 流程图与节点执行

当前 Contracts 的核心是 **数据流驱动的视觉流程图引擎**，相关契约位于 `Flow` 目录下。

- **`INodeExecutor`**（`Flow/Executants`）：所有节点算子的执行接口，实现类通过 `ExecuteAsync` 完成具体算法。
- **`FlowNodeBase / FlowNode / CompositeFlowNode`**（`Flow/Nodes`）：节点模型，包含位置、端口集合、参数模型、执行器绑定。
- **`NodePort / ConnectionModel / FlowProcessModel`**（`Flow/Nodes/FlowModels.cs`）：端口、连线、流程容器模型；支持数据端口类型、相对坐标、动态连线更新。
- **`ExecutionContext`**（`Flow/Contexts`）：全局数据管线，包含 `SharedVariables`、端口值缓存 `_portValueCache`、节点执行事件与日志接口。
- **`NodeExecutionContext`**（`Flow/Contexts`）：单个节点执行上下文，封装端口数据读写、硬件解析、`FrameCycleContext`、节点状态暂存。
- **`FrameCycleContext`**（`Flow/Contexts`）：单次节拍触发生命周期上下文（CycleId、TriggerTime、BatchId、TransientItems）。
- **`FlowExecutor`**（`Flow/Executants`）：流程执行引擎，基于 Kahn 拓扑排序构建执行链，支持连续运行、单步运行、停止、异常处理。
- **`NodeFactory`**（`Flow/Factories`）：静态节点工厂，扫描程序集注册 `INodeExecutor` 实现，生成工具箱元数据并创建画布节点。
- **`NodeAttribute / NodeFieldMetaAttribute / NodePortAttribute`**（`Flow/Attributes`）：节点元数据特性，用于工具箱分组、图标、颜色、端口声明与参数绑定。

### 4. Enums

- **`DeviceState`**（`Devices/Enums`）：`Disconnected / Connecting / Connected / Error`。
- **`WorkMode`**（`Station/Enums`）：`Debug / Production`，工位运行模式，由 `StationWorker` 在状态切换与事件输出策略中引用。
- **`StationState`**（`Station/Interfaces/IStationWorkerHost.cs`）：`Idle / Stopped / Running / Paused / Faulted`。
- **`NodeCategory / NodeType / PortType / PortCategory / PortPosition / ConnectorType`**（`Flow/Enums/NodeFlowEnums.cs`）：节点分类、具体节点类型、端口方向、类别、位置、连接语义枚举。

### 5. IPC —— 进程间通信契约

`Grayson.Vision.Contracts.IPC` 定义了 Worker 进程与 UI 观察者之间统一的 JSON 消息封包。

- **`IpcMessageType`**：
  - `Command`：UI → Worker 控制指令
  - `Response`：Worker → UI 指令应答
  - `EventBroadcast`：Worker → UI 异步事件广播
- **`IpcMessage`**：统一消息包，包含 `MessageId`、`StationId`、`MessageType`、`Action`、`PayloadJson`。

> 该协议当前由 `Grayson.Vision.WorkerHost.NamedPipeIpcServer` 通过命名管道实现，未来可无缝替换为 TCP / gRPC / WebSocket 适配器而无需修改消息模型。

### 6. Imaging

提供与具体图像库（Halcon）解耦的渲染契约。

> 新版架构下，`IRenderImage` / `IImageRenderService` 不仅要服务同进程 WPF 显示，还要能处理跨进程 Worker 推送的 `ImageRenderEventArgs`。建议通过 `ImagePathOrBufferId` 传递图像缓存 ID 或共享内存名称，而非直接序列化 HObject。

- **`IRenderImage`**：渲染图像句柄，内部持有原生图像对象。
- **`IImageRenderService`**：包装图像、查询像素、包装叠加图元、渲染到窗口、自适应窗口。
- **`IImageDisplayHost`**：图像显示宿主契约，由 WPF 控件实现。
- **`ImageRenderContext / ImageOverlay / OverlayKind`**：渲染上下文与叠加图元抽象。

### 7. Logging

- **`LogBus`**：全局静态日志总线，通过 `OnLogProduced` 事件发布 `LogEntry`。
- **`LogLevel / LogEntry`**：日志级别与日志实体。
- **`FileLogSink`**：异步文件日志落盘，按天分文件存储。

### 7. Services / Permission / Plugin / VM

- **`IDialogService / IFileDialogService / IFlowLayoutService`**：UI 相关服务的抽象契约，由 WPF/FlowEdit 实现。
- **`UserRole`**：操作员 / 工程师 / 管理员三级角色枚举。
- **`INodePluginLoader`**：节点插件加载器契约。
- **`ViewModelBase / RelayCommand`**：轻量 MVVM 基类与命令实现，供Contracts 层模型使用。

---

## 设计约束

1. **零第三方依赖**：Contracts 不引用 `HalconDotNet`、相机 SDK、PLC SDK、WPF 等任何第三方 dll。
2. **抽象优先**：所有硬件、渲染、对话框、流程布局、Worker 宿主、IPC 消息均通过接口或抽象模型描述。
3. **数据流驱动**：流程执行不再依赖传统 `Exec` 控制线，而是通过数据连线拓扑决定执行顺序。
4. **节点可扩展**：新算法节点只需实现 `INodeExecutor` 并标记 `[Node]`/`[NodePort]`，由 `NodeFactory` 自动扫描注册。
5. **Worker 宿主可替换**：同一 `IStationWorkerHost` 可以被 `Grayson.Vision.WorkerHost`（独立进程）或 `Grayson.Vison.FlowEdit`（嵌入式线程）实现/消费。
6. **IPC 协议中性**：`IpcMessage` 不依赖任何传输层，便于在命名管道、TCP、gRPC 之间切换。

---

## 维护提示

- 新增节点类型时，应同步扩展 `NodeType` 与 `NodeCategory` 枚举，并为节点字段添加 `[NodeFieldMeta]` 与 `[Description]`。
- 新增硬件类型时，优先扩展 `DeviceCategory` 并在 `Devices` 命名空间下新增对应接口。
- 若需调整流程引擎执行语义，应同步更新 `FlowExecutor` 与 `ExecutionContext` 的接口/事件设计。
- 新增 IPC 控制指令时，应同步更新 `IpcMessageContract` 相关说明、`Grayson.Vision.WorkerHost` 的 `HandleIpcCommand` 以及 UI 客户端的代理类。
- 新增 Worker 事件时，优先在 `IStationWorkerEvents` 扩展，再由 `StationWorker` 触发，`WorkerHost` 广播给 UI。
