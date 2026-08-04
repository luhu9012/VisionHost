# Grayson.Vision.WorkerHost

`Grayson.Vision.WorkerHost` 是 **Grayson 视觉检测平台** 的独立工位 Worker 进程宿主。以控制台可执行程序（WinExe/Exe）形式运行，负责将 `Grayson.Vision.Core` 中的 `StationWorker` 挂载到独立进程中，并通过命名管道（Named Pipe）与 `Grayson.VisionApp.WpfUI` 或其他控制台进程进行双向通信。

---

## 技术环境

- 目标框架：`.NET Framework 4.7.2`
- 开发环境：Visual Studio 2026 / C# 7.4
- 平台目标：推荐 `x64`
- 项目类型：控制台可执行程序（Console / WinExe，具体输出类型按项目配置）

---

## 在解决方案中的位置

```text
Grayson.Vision.Contracts
	↑
Grayson.Vision.Core
	↑
Grayson.Vision.WorkerHost  <- 本层（独立进程宿主 + IPC 服务端）
	⇄ NamedPipe
Grayson.VisionApp.WpfUI / Grayson.Vison.FlowEdit（IPC 客户端 / UI 观察者）
```

---

## 当前实际目录结构

```text
Grayson.Vision.WorkerHost/
├── Program.cs                // 控制台入口：解析命令行 -> 初始化工厂 -> 挂载 Worker -> 启动 IPC 服务
├── NamedPipeIpcServer.cs     // 基于 NamedPipeServerStream 的 IPC 服务端实现
└── Properties/
	└── AssemblyInfo.cs
```

---

## 核心模块说明

### 1. Program（进程入口）

控制台入口类，完成以下初始化流程：

1. 解析命令行参数 `--stationId=Station_01`，默认 `Station_01`
2. 扫描并注册 `NodeFactory`（加载所有可执行节点类型）
3. 创建 `StationWorker`
4. 创建 `NamedPipeIpcServer` 并订阅 Worker 事件
5. 启动 IPC 监听，等待 UI 客户端连接与指令
6. 按 `Q` 键安全退出

支持的 IPC 控制指令：

| 指令 | 作用 |
|---|---|
| `LoadRecipe` | 反序列化 `FlowProcessModel` 并调用 `worker.LoadRecipeAsync(recipe)` |
| `Start` | 调用 `worker.StartAsync()` |
| `Stop` | 调用 `worker.StopAsync()` |
| `TriggerOnce` | 触发单次节拍运行，负载为可选 `batchId` |
| `StepNode` | 单步调试单个节点，负载为 `FlowNodeBase` JSON |

Worker 业务事件广播：

- `OnStateChanged` → 向所有客户端广播 `OnStateChanged`
- `OnFrameRendered` → 向所有客户端广播 `OnFrameRendered`
- `OnNodeExecuting` / `OnNodeExecuted` → 可扩展为 `OnNodeEvents`
- `OnExecutionCompleted` / `OnExecutionError` → 可扩展为执行链结果事件

### 2. NamedPipeIpcServer（命名管道 IPC 服务端）

基于 `NamedPipeServerStream` 实现，使用 newline-delimited JSON 作为通信协议。

主要特性：

- 每个工位使用独立管道名：`Grayson_Vision_Pipe_{StationId}`
- 支持异步连接：`ListenAsync(CancellationToken)`
- 消息模型：`Grayson.Vision.Contracts.IPC.IpcMessage`
  - `MessageType = Command`：接收 UI → Worker 的控制指令
  - `MessageType = EventBroadcast`：向已连接客户端广播 Worker 事件
- 断线自动重连：异常后等待 1 秒再次进入监听状态
- 线程安全写管道：`BroadcastEvent<T>` 使用 `lock(_pipeServer)` 保护写出

> 注意：当前版本为单机多进程方案。如需远程/跨机部署，可保留 `IpcMessage` 不变，将底层换成 TCP Host / gRPC / WebSocket 适配器。

---

## 启动方式

### 从命令行启动

```powershell
Grayson.Vision.WorkerHost.exe --stationId=Station_01
```

### UI 端启动

`WpfUI` 可通过 `Process.Start` 拉起 WorkerHost 进程；也可预先将 WorkerHost 注册为后台守护服务：

```csharp
Process.Start(new ProcessStartInfo
{
	FileName = "Grayson.Vision.WorkerHost.exe",
	Arguments = $"--stationId={stationId}",
	UseShellExecute = false
});
```

### 作为后台服务运行（推荐产线部署）

可结合 `sc.exe` 或 Topshelf 将 WorkerHost 注册为 Windows Service，实现开机自启动、崩溃重启、多工位横向扩展。

---

## 依赖

- `Grayson.Vision.Contracts`
- `Grayson.Vision.Core`
- `Grayson.Vision.Nodes`（通过插件加载触发，确保节点程序集在扫描路径中）
- `Newtonsoft.Json`

---

## 设计约束

1. **无 UI 引用**：WorkerHost 不引用任何 WPF / WinForms 程序集，避免占用 UI 线程资源。
2. **节点程序集必须可被扫描**：启动时会调用 `NodeFactory.Initialize()`，请将实际节点 DLL（如 `Grayson.Vision.Nodes`、`Plugins.Camera.Hikvision`、`Plugins.PLC.Siemens`）放在 WorkerHost 可加载路径（同目录）中。
3. **图像数据建议走共享内存**：IPC 管道传输图像时，避免序列化超大 Bitmap；推荐在 `ImageRenderEventArgs.ImagePathOrBufferId` 中传递缓存 ID 或共享内存名称，客户端从 `HalconImageRenderService` 渲染。
4. **异常不致命**：每个 IPC 命令独立 try-catch，避免单条指令异常导致整个 Worker 进程退出。

---

## 维护提示

- 新增 IPC 控制指令时，同步修改 `NamedPipeIpcServer` 的 `HandleIpcCommand` switch 与 `Grayson.VisionApp.WpfUI` 中对应的客户端发送逻辑。
- 若 WorkerHost 启动后提示找不到节点，检查项目引用/构建输出中是否包含对应节点 DLL。
- 本地调试时可直接启动 `WorkerHost` + `WpfUI`，观察控制台输出与 UI 事件是否一致。
