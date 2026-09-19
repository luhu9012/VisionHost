# Grayson.VisionApp.WpfUI — 主程序（WPF 上位机宿主）

> 校正：2026-09-07。本文替代旧版"P0-P5 改造进度"叙述（历史结论保留见 §6）。当前按"页面地图 + 工程纪律"组织。

## 1. 工程说明

- 目录含**两个 csproj**（老式工程，新增 .cs 须加 `<Compile Include>`）：
  - `Grayson.Vision.WpfUI.csproj`（进 slnx，**全插件版**：Basler/Epson/ZMC/Universal/ONNX/海康）→ 产物 `Grayson.Vision.WpfUI.exe`
  - `Grayson.VisionApp.WpfUI.csproj`（未进 slnx，轻量变体：海康 + 西门子 PLC 引用集）→ 产物同名
  - ⚠️ 治理 TODO A1：建议收敛为单一入口。
- 运行目录：`bin\Debug\`；原生依赖（halcon.dll、相机/机械手 SDK）须齐备（见根 README §5）。
- 规模：106 个 .cs（View 页面 40+）、.NET Framework 4.7.2。

## 2. 顶层代码结构

| 目录 | 职责 |
|---|---|
| `View\` | 页面 XAML（40+ 页面/窗口，见 §3 页面地图） |
| `ViewModel\` | 页面 VM（INavigationAware 缓存单例纪律见 §4） |
| `Model\` / `Common\` / `Selectors\` | 视图模型辅助、公共工具、DataTemplate 选择器 |
| `Service\` | 应用级服务（会话、导航、全局状态等） |
| `Resource\` | 样式/资源字典（卡片底刷键 `BackgroundCardBrush` 等） |
| `App.xaml.cs` / `MainWindow` / `ShellView` | 启动/主壳（登录 → 壳 → 各功能页） |

## 3. 页面地图（View 一级）

**总览/运行**：ShellView、SplashView、LoginView、LineOverviewView、StationMonitorView(+StationMonitorExtensions)、StationManageView、StationWizardWindow、WorkOrderDetailDialog、DataTraceView、AlarmView、SystemSettingView、UserManageView

**视觉工程**：TemplateManagerView、CalibrationManagerView、Calibration\*（CalibrationWizardWindow / CalibrationVerifierWindow / CalibrationToolOffsetWindow）、CameraTuneTool\*（成像与打光/垂直度装调双页签）、FlowEditViewWrapper（嵌节点编辑器）

**设备/通信**：DevicePoolView、HardwareAllocationView、AddDeviceDialog、ScanDeviceDialog、HardwareSelectWindow、HardwareConsoleView(+HardwareConsole：AxisControl/CameraDebug/CameraLiveWindow/CommDebug/IoMonitor/RobotDebug)、PluginManageView、MesBridgeView

**业务**：RecipeManageView

> 信息架构 v2：模板/标定独立页；工位工作台页只呈现 3 对象（模板-标定-流程）Tab。定位壳=ScopeStationCode + 主集/VisibleProfiles + BackToGlobalCommand。

## 4. 工程纪律（踩坑沉淀，改 UI 前必读）

1. **缓存单例页**必须 `INavigationAware`：OnNavigatedTo 全量重建（清订阅→重载→直接赋值 Selected+显式 RefreshAsync），防事件泄漏/脏数据；
2. **视口覆盖层**：WPF 覆盖层绘制不可见+命中失效 → 可见视觉全画 HALCON 窗口层；命中纯几何 HitTestRoiTag；右键菜单 lambda 闭包捕获（禁 DataContext/Tag），弹菜单前 ReleaseOverlayCapture；fill=SetDraw 两次；
3. RelayCommand 一律 `() =>` 无参 lambda；Worker 事件直连具名方法、经 Dispatcher 上屏；
4. 算子/采集/设备动作绝不跑 UI 线程（另见根 ARCHITECTURE §6）；
5. 模板/标定/配方改动入口统一收口到对应 Manager VM + 服务，页面不直接读写 JSON/矩阵文件。

## 5. 本工程内文档

- `View\Calibration\`：使用文档.md / 标定选型指南.md / 双工位滑台标定.md（操作手册）
- `MVVM讲解.md`：内部 MVVM 教程

## 6. 历史（旧版 README 结论存档）

2026-07 完成 UI P0-P4；2026-08 完成 P5（架构合规：配方持久化收口 Repository、设备池依赖注入、全局急停与状态栏、IWorkerClient 扩展 Pause/Resume/EStop、配方审批 UI、工位启停可视化）。2026-09 起按 v2 信息架构迭代（上表为准）。
