# Grayson.Vision.FlowEdit

`Grayson.Vision.FlowEdit` 是工业视觉上位机专用的 **可视化流程编排器**。用户可通过拖拽、连线定义工位自动化逻辑，涵盖设备 IO、逻辑控制、图像处理、PLC 通讯等场景。它既可以作为 **独立 WPF 程序** 运行调试，也可以作为 **用户控件** 嵌入到 `Grayson.VisionApp.WpfUI` 主程序中。

在新版 **Worker-Host + IPC + Observer UI** 架构下，FlowEdit 同时承担两种角色：

- **嵌入式线程宿主**：在单进程编辑/调试时直接驱动 `Grayson.Vision.Core.StationWorker`，无需序列化开销。
- **观察者 UI**：通过 `Grayson.Vision.Contracts.IPC` 协议连接远端 `Grayson.Vision.WorkerHost` 进程，作为多工位生产线上的可视化监控客户端。

---

## 1. 项目定位

```text
Grayson.Vision.Contracts (契约与模型)
	↑
Grayson.Vision.Core (工位核心：StationWorker / StationContext)
	↑
Grayson.Vision.Nodes (节点实现)
	↑
Grayson.Vision.HalconWrapper.Wpf (Halcon WPF 显示)
	↑
Grayson.Vison.FlowEdit (本层：流程编辑器 / 调试宿主 / 观察者 UI)
	↑
Grayson.VisionApp.WpfUI (主程序，可选嵌入)
		⇄ NamedPipe
Grayson.Vision.WorkerHost (独立进程 Worker 宿主)
```

- 本层是 **应用层/编排层**，负责把 Contracts 中的流程模型、Nodes 中的节点、HalconWrapper.Wpf 中的图像显示整合成一个可交互编辑器。
- 不向底层暴露 UI 细节，通过 ViewModel 与 Services 组织业务。
- 在新架构下，FlowEdit 既可直接使用 `StationWorker` 进行本地单步调试（跨线程事件），也可通过 IPC 客户端代理观察远端工位（命名管道）。

---

## 2. 目录结构

```text
Grayson.Vison.FlowEdit/
├── App.xaml / App.xaml.cs              # 应用入口
├── MainWindow.xaml / .cs               # 独立运行时的调试宿主窗口
├── Views/
│   ├── FlowEditView.xaml / .xaml.cs    # 主画布（工具箱、节点、连线、图像区、日志）
│   └── NodePropertyWindow.xaml / .cs   # 节点属性弹窗
├── ViewModels/
│   ├── FlowVm.cs                       # 主流程 ViewModel
├── Controls/
│   └── NodeControl.cs                  # 可视化节点控件（拖拽、端口渲染）
├── Converters/
│   └── ValueConverters.cs              # XAML 转换器（贝塞尔曲线、节点颜色等）
├── Services/
│   ├── WpfDialogService.cs             # IDialogService / IFileDialogService 的 WPF 实现
│   ├── NodePluginLoader.cs             # 插件加载，实现 INodePluginLoader
│   ├── LayoutService.cs                # 自动布局，实现 IFlowLayoutService
│   └── RecipeManager.cs                # 配方 JSON 导入/导出/扫描
└── Grayson.Vision.FlowEdit.csproj
```

> 注意：本项目不再包含 `ImageDisplayControl.xaml` 和 `HalconRenderContext.cs`，Halcon 显示已迁移到 `Grayson.Vision.HalconWrapper.Wpf`。

---

## 3. 架构与通信

FlowEdit 采用 **MVVM + 事件 + 契约接口** 的方式组织代码，View 与 ViewModel 之间通过数据绑定和事件松耦合。为适应新架构，FlowEdit 支持两种运行模式。

### 3.1 两种 Worker 宿主模式

#### 模式 A：嵌入式线程宿主（Embedded Thread Host）

用于本地编辑与单步调试：

```text
FlowEdit (UI 线程)
	↓ 创建 StationWorker（后台线程 / Task）
Grayson.Vision.Core.StationWorker
	↑ 事件回调通过 Dispatcher.Invoke 回到 UI 线程
FlowEdit ImageDisplayVm / FlowVm
```

- 直接实例化 `StationWorker`。
- 通过 C# 事件订阅 `OnStateChanged`、`OnNodeExecuting`、`OnNodeExecuted`、`OnFrameRendered`。
- UI 更新切回 `Dispatcher` 线程，无需 JSON 序列化，开销最低。

#### 模式 B：观察者 UI（Observer UI over IPC）

用于连接生产环境独立 Worker 进程：

```text
FlowEdit / WpfUI (IPC 客户端)
	⇄ NamedPipe (Grayson_Vision_Pipe_{StationId})
Grayson.Vision.WorkerHost (独立进程)
	↓ 操作 StationWorker
Grayson.Vision.Core
```

- UI 通过 `IpcMessage` 向 WorkerHost 发送 `LoadRecipe`、`Start`、`Stop`、`TriggerOnce`、`StepNode` 等命令。
- WorkerHost 将 `StationWorker` 事件反序列化为 `IpcMessage` 广播给 UI。
- UI 客户端解析事件后刷新状态、日志、图像。

### 3.2 分层职责

| 层 | 主要职责 |
|---|---|
| **View（Views/Controls）** | 用户交互：拖拽节点、绘制连线、画布平移缩放、双击打开属性面板、滚动到当前执行节点 |
| **ViewModel（ViewModels）** | 业务逻辑：节点/连线增删、工具箱初始化、流程运行控制、配方导入导出、日志收集、IPC 客户端封装 |
| **Services** | 横向能力：对话框、文件选择、插件加载、自动布局、配方持久化 |
| **Contracts** | 数据模型与接口：`FlowProcessModel`、`FlowNodeBase`、`INodeExecutor`、`IImageRenderService`、`IpcMessage` 等 |

### 3.3 View ↔ ViewModel 通信

FlowEditView.xaml 的 `DataContext` 是 `FlowVm`：

```xml
<UserControl.DataContext>
	<vm:FlowVm />
</UserControl.DataContext>
```

**数据绑定示例：**

| UI 元素 | 绑定路径 | 说明 |
|---|---|---|
| 工具箱列表 | `ToolBoxGrouped` | 显示分类节点 |
| 画布节点 | `CurrentProcess.Nodes` | `ItemsControl.ItemTemplate` 使用 `NodeControl` |
| 画布连线 | `CurrentProcess.Connections` | 贝塞尔曲线绑定端口坐标 |
| 选中节点 | `SelectedNode` | 控制 Inspector / 属性面板 |
| 运行日志 | `ExecutionLogs` | 底部日志列表 |
| 共享数据 | `WatchData` | `SharedData` 变量监控 |
| 图像显示 | `ImageDisplayVm` | 右侧图像区与缩略图列表 |

### 3.4 画布交互事件流

1. **新增节点**
   - 用户在工具箱双击或拖拽节点。
   - `FlowEditView.xaml.cs` 捕获鼠标事件，调用 `FlowVm.AddNodeFromMeta(meta, position)`。
   - `FlowVm` 通过 `NodeFactory.CreateFromMeta` 生成节点并加入 `CurrentProcess.Nodes`。

2. **连接节点**
   - 用户在 `NodeControl` 的输出端口按下鼠标左键 → `NodeControl` 调用 `FlowEditView.StartConnecting(sourceNode, sourcePort)`。
   - 鼠标移动时，`FlowEditView` 绘制临时贝塞尔曲线并吸附附近输入端口。
   - 鼠标释放时，调用 `FlowVm.AddConnection(...)` 创建 `ConnectionModel`。

3. **平移/缩放画布**
   - 滚轮缩放：修改 `CanvasScale.ScaleX/Y`。
   - 右键拖拽：修改 `FlowScrollViewer` 的 `Offset`。

4. **节点选中/属性面板**
   - `NodeControl` 处理鼠标按下/双击事件。
   - 单击：设置 `FlowVm.SelectedNode`。
   - 双击：若是 `CompositeFlowNode` 则下钻；否则弹出 `NodePropertyWindow`。

### 3.5 流程执行与图像显示通信

#### 本地调试模式

```text
FlowVm.ExecuteAsync()
	↓
StationWorker.TriggerOnceAsync() / StepNodeAsync()
	↓
FlowExecutor / ExecutionChain 逐个调用 INodeExecutor.ExecuteAsync(...)
	↓
节点通过 context.SetOutputValue(...) 输出数据
	↓
StationWorker.Engine_OnNodeExecuted
	↓
ImageDisplayVm 订阅事件，从 OutputPorts 查找 Image 数据
	↓
IImageRenderService 包装为 WpfImageRenderContext
	↓
OnRequestRender?.Invoke(context) 通知 HalconImageDisplayHost 渲染
```

#### 独立进程模式

```text
FlowVm 通过 IpcClient 发送 TriggerOnce
	↓
WorkerHost.HandleIpcCommand
	↓
StationWorker.TriggerOnceAsync()
	↓
执行结果通过 OnFrameRendered 广播
	↓
WorkerHost.NamedPipeIpcServer.BroadcastEvent
	↓
FlowEdit IpcClient 收到事件，反序列化 ImageRenderEventArgs
	↓
ImageDisplayVm 根据 ImagePathOrBufferId 获取图像并渲染
```

关键事件：

| 事件 | 触发源 | 订阅者 | 作用 |
|---|---|---|---|
| `StationWorker.OnNodeExecuting` | 流程执行器 | `FlowEditView` | 运行时自动滚动到正在执行的节点 |
| `StationWorker.OnNodeExecuted` | 流程执行器 | `ImageDisplayVm` | 节点执行完成后提取图像数据 |
| `ExecutionContext.OnLogProduced` | 流程执行器 | `FlowVm` | 收集运行日志 |
| `ImageDisplayVm.OnRequestRender` | `ImageDisplayVm` | `HalconImageDisplayHost` | 请求刷新 Halcon 窗口 |
| `HalconImageDisplayHost.CursorPixelMoved` | Halcon 控件 | `ImageDisplayVm` | 鼠标移动时查询像素信息 |

---

## 4. 核心模块

### 4.1 FlowVm

`FlowVm` 是编辑器主 ViewModel，主要职责：

- **节点管理**：`AddNodeFromMeta`、`DeleteSelectedNode`、`ClearCanvas`
- **连线管理**：`AddConnection`、`DeleteConnection`
- **工具箱**：`InitFullToolBox`，从 `NodeFactory.GenerateToolboxMetas()` 加载节点元数据
- **流程运行**：`RunContinuousCmd`、`StepRunCmd`、`StopRunCmd`、`PauseRunCmd`
- **导航**：`Breadcrumbs`、`NavigateToProcessCmd`，支持 `CompositeFlowNode` 下钻
- **配方**：`SaveRecipeCmd`、`ImportRecipeCmd`、`ExportRecipeCmd`、`SaveCurrentPipelineAsRecipeCommand`
- **自动布局**：`AutoLayoutCmd`，调用 `LayoutService`
- **Worker 切换**：维护当前使用的 `IStationWorkerHost`（本地 StationWorker 或 IPC 客户端代理）

### 4.2 ImageDisplayVm （已经移动到 Grayson.Vision.HalconWrapper.Wpf）

`ImageDisplayVm` 负责图像显示与缩略图管理：

- 订阅 `StationWorker.OnNodeExecuted` / IPC 客户端的 `OnFrameRendered`。
- 从节点输出端口或 `ImageRenderEventArgs.RenderData` 中提取图像数据。
- 使用 `IImageRenderService.WrapImage` 包装图像，生成 `WpfImageRenderContext` 和缩略图。
- 维护 `ImageHistoryList`，支持上一张/下一张/点击切换。
- `UpdateCursorPixelInfo` 根据鼠标坐标查询像素信息。

### 4.3 NodeControl

`NodeControl` 是节点可视化控件：

- 继承自 `ContentControl`。
- 内部通过代码动态构建 `ControlTemplate`（节点背景、标题、执行状态、端口）。
- 端口命中测试与布局由 `AutoLayoutNodePorts` 完成。
- 端口鼠标事件委托给 `FlowEditView` 处理连线。

### 4.4 RecipeManager

`RecipeManager` 负责配方的持久化：

- 扫描 `Recipes/` 目录，将 `.json` 文件注入工具箱作为 `CompositeFlowNode` 模板。
- `SavePipelineAsRecipe`：将当前流程保存为 JSON 模板。
- `ExportRecipe` / `ImportRecipe`：导出/导入完整配方，依赖 `IFileDialogService`。

### 4.5 Services

| 服务 | 实现 | 作用 |
|---|---|---|
| `IDialogService` | `WpfDialogService` | 消息弹窗、确认框、输入框、等待框 |
| `IFileDialogService` | `WpfDialogService` | 保存/打开文件对话框 |
| `INodePluginLoader` | `NodePluginLoader` | 扫描插件 DLL，注册节点和 XAML 模板 |
| `IFlowLayoutService` | `LayoutService` | DAG 拓扑自动布局 |

---

## 5. 插件加载机制

在 `FlowVm` 构造函数中：

```csharp
string pluginDir = AppDomain.CurrentDomain.BaseDirectory;
new NodePluginLoader().LoadPlugins(pluginDir, AddLog);
```

`NodePluginLoader` 会：

1. 扫描 `Grayson.Vision.Nodes*.dll`。
2. 通过反射查找实现 `INodeExecutor` 并标记 `[Node]` 的类型。
3. 调用 `NodeFactory.RegisterExecutorType(type)` 注册节点元数据。
4. 解析 DLL 内嵌 BAML 资源，将 `*TemplatePage.xaml` / `*Template.xaml` 的 `DataTemplate` 注入 `Application.Current.Resources`。

开发新节点时，只需在 `Grayson.Vision.Nodes` 中添加三件套（`Param`、`Executor`、`TemplateView.xaml`），FlowEdit 启动时会自动识别。

---

## 6. 依赖

- `Grayson.Vision.Contracts`
- `Grayson.Vision.Core`（本地调试模式）
- `Grayson.Vision.Nodes`
- `Grayson.Vision.HalconWrapper.Wpf`
- WPF 程序集：`PresentationCore`、`PresentationFramework`、`WindowsBase`、`System.Xaml`
- `Newtonsoft.Json`

> 本项目**不再直接引用 `halcondotnet`**，所有 Halcon 显示能力通过 `Grayson.Vision.HalconWrapper.Wpf` 中转。

---

## 7. 两种模式的切换建议

| 场景 | 推荐模式 | 说明 |
|---|---|---|
| 本地编辑/单步调试 | 嵌入式线程宿主 | 直接实例化 `StationWorker`，事件到 Dispatcher，无 IPC 开销 |
| 连接产线已有工位 | 观察者 UI（NamedPipe） | 连接 `Grayson.Vision.WorkerHost` 进程，不占用图像处理 CPU |
| 4 工位大屏监控 | 观察者 UI（NamedPipe × 4） | 每个工位一个 IPC 客户端，每个 Worker 一个独立进程 |

---

## 8. 未来扩展建议

- **节点参数模板进一步拆分**：当前 `Grayson.Vision.Nodes` 中的 `TemplatePage.xaml` 已作为内嵌资源加载，后续可按分类拆分到独立资源字典。
- **撤销/重做**：在 `FlowVm` 中维护命令栈，封装节点/连线增删操作。
- **运行时状态持久化**：将断点、当前执行索引等状态加入 `ExecutionContext`。
- **多工位支持**：`RootProcess` 下可包含多个子流程，每个工位对应一个 `FlowProcessModel` 和一个 `IStationWorkerHost` 实例。
- **IPC 客户端封装**：可在 `FlowEdit` 中增加 `RemoteWorkerClientProxy`，统一处理 NamedPipe 连接、事件分发与 Command 发送。
