# Grayson.VisionApp.WpfUI - 工业视觉上位机界面架构文档

> **改造进度**：✅ P0-P4 已完成（2026-07）　　✅ P5 已完成（2026-08）：架构合规性治理，包含配方持久化收口、设备池依赖注入、全局急停与状态栏增强。　　🔧 P5+ 补全（当前）：修复配方设备绑定状态显示、新增配方审批 UI、StationMonitor 暂停/恢复、DataTrace 实时工单、StationManage 启用状态可视化。

## P5 重点变更

- **配方持久化由 Repository 接管**：`RecipeManageViewModel` 不再直接 `File.ReadAllText/WriteAllText`，统一使用 `IRecipeStorageService`（默认 JSON 文件实现，保留 `Recipes` 目录兼容性）。
- **设备池依赖注入**：`DevicePoolViewModel` 与 `StationConfigService` 改为构造注入 `IDevicePool`，不再硬编码 `DevicePoolManager.Instance`（为兼容保留回退路径）。
- **消除 Core 类型强转**：`StationManageViewModel` 中 `App.StationHostRuntime as StationHostRuntime` 改为 `as IStationHostRuntime`，支持未来 IPC/远端 Worker 宿主切换。
- **全局急停与状态栏增强**：`ShellView.xaml` 标题栏新增“🚨 急停”按钮；状态栏新增当前用户角色与程序版本显示；`GlobalData` 提供 `CurrentUserRoleText` / `IsAuthenticated` / `ShowCriticalAlarm`。
- **工位控制命令补齐**：`IWorkerClient` 与 `EmbeddedWorkerClientProxy` 增加 `PauseAsync` / `ResumeAsync` / `EmergencyStopAsync`，`StationMonitorViewModel` 已绑定 Pause/Resume 按钮；`StationManageView` 可启用/禁用单个工位并在拓扑树显示启用状态。
- **配方审批流 UI**：`RecipeManageView` 显示审批状态、审批人/时间，并提供提交/通过/驳回按钮；仅审批通过的配方才允许下发到生产工位。
- **实时工单追溯**：`DataTraceView` 新增“⚡ 实时工单”按钮，从 `IStationHostRuntime.GetWorkOrderTracker(stationId)` 读取工位最近工单并展示。
- **角色门控急停**：`ShellView` 全局急停按钮使用 `RoleSatisfiesConverter` 仅对 Operator 及以上角色可见。

---

## 🔄 架构改造阶段进展

| 阶段 | 状态 | 当前结论 |
|---|---|---|
| **P0** | ✅ 已完成 | 角色化视图拆分与 Shell 二级菜单已完成 |
| **P1** | ✅ 已完成 | 多产线模型与插件接口迁移到 Core 已完成 |
| **P2** | ✅ 已完成 | Core Worker 契约、插件迁移、配方索引与 UI 接入链路已贯通（Host/Worker/Outbox/运行态） |
| **P3** | ✅ 已完成 | 视觉工程台已接入插件治理最小入口（刷新/启用/禁用/回滚） |
| **P4** | ✅ 已完成 | 已新增硬件设备池二级菜单（物理设备实例 / 轴·IO·相机调试台 / 驱动插件管理）、设备状态总线桥接、报警 Banner，并补充运行时轻量指标展示 |

## 审查入口（迭代中）

- 实施计划：[`../docs/P2P3-Implementation-Plan.md`](../docs/P2P3-Implementation-Plan.md)
- 架构运行手册：[`../docs/Architecture-Runbook.md`](../docs/Architecture-Runbook.md)
- 操作 SOP：[`../docs/Operation-SOP.md`](../docs/Operation-SOP.md)
- 当前阶段：P4 已完成，当前 UI 已具备“独立插件治理入口、硬件接入标准化、设备状态统一传播、轻量可观测性展示”。

## 🧭 管理范围蓝图（WpfUI）

### 本 README 覆盖边界
- 覆盖 `Grayson.VisionApp.WpfUI` 项目内的 UI 架构、模块能力、与 Core 的对接契约。
- 不覆盖 Core 内部实现细节（Worker 队列、Outbox 存储实现、插件加载器内部机制）。

### 当前完成度基线（以代码为准）
- ✅ 页面框架：登录、Shell 导航、11+ 业务页面已注册（以 `App.xaml.cs` 中的 `RegisterPages` 为准）。
- ✅ 菜单结构：Shell 二级菜单分为“运行操作区 / 核心工程配置 / 硬件设备池 / 数据与运维”四组，按角色控制可见性。
- ✅ P2 接入：`StationWorkerRuntimeService` 已提供 Host/Worker 统一入口。
- ✅ P4 硬件池：左侧新增“设备与驱动”分组，含物理设备实例、轴/IO/相机调试台、驱动插件管理。
- ✅ 运行链路：可执行 Start/Stop/Pause/Resume、发布节拍信号、接收工位状态摘要；全局急停由 `ShellViewModel` 统一调用所有工位 `EmergencyStopAsync`。
- ✅ 配方审批：配方提交→审批通过→工位下发链路已在 UI 中可运行。
- ✅ 工单实时追溯：`DataTraceView` 支持从运行态 Worker 的 `WorkOrderTracker` 读取最近工单。
- ⚠️ 真实设备与外部系统：当前仍以演示/Mock 数据为主，MES/数据库/真实相机链路待后续阶段深化。

### 关键运行约束
- UI 线程仅负责展示与命令下发，不承载阻塞式业务逻辑。
- 工位执行统一走 `IStationWorkerHost`，避免在 ViewModel 内直接拼装底层依赖。
- 与 Core 对接遵循“本地优先落地 + 异步同步”，不阻塞工位节拍。

### 紧要信息（开发前必看）
- 角色权限仍是强约束：调试功能仅工程师/管理员可用。
- 当前蓝图与进度以本文件 + Core README + 根 README 三者一致为准。
- 如实现与文档冲突，先修文档基线再继续开发。

---

## 📋 项目概述

这是一个专业的**工业视觉上位机 WPF UI 层**，采用MVVM模式设计，模块化清晰，易于扩展和适配新产线。

## 🏗️ 架构特点

### 设计理念
- ✅ **MVVM模式** - View/ViewModel严格分离
- ✅ **插件化模块** - 每个功能模块独立，可热插拔
- ✅ **导航框架** - 主框架 + 模块导航
- ✅ **权限控制** - 基于角色的界面访问控制（操作员/工程师/管理员）
- ✅ **实时数据总线** - GlobalData单例共享跨模块数据

### 技术栈
- 框架: .NET Framework 4.7.2
- UI: WPF + XAML
- MVVM: 自定义 RelayCommand + ViewModelBase
- 样式: 工业深色主题（可扩展HandyControl）

## 📂 目录结构

```
Grayson.VisionApp.WpfUI/
├── App.config                  # 应用程序配置
├── App.xaml                    # 应用启动配置
├── App.xaml.cs                 # 启动逻辑：日志、设备池、认证、页面注册
├── packages.config             # NuGet 包配置
├── MainWindow.xaml             # 保留入口（当前由 LoginView + ShellView 承载）
├── MainWindow.xaml.cs
│
├── Common/                     # 公共基础设施
│   ├── ViewModelBase.cs        # ViewModel 基类
│   ├── RelayCommand.cs         # 命令实现
│   ├── Enums.cs                # 枚举定义（权限/报警等级/设备状态）
│   ├── Converters.cs           # 值转换器集合
│   └── GlobalData.cs           # 全局单例数据上下文
│
├── Model/                      # UI 数据模型与 Mock 工厂
│   ├── CommonModel.cs          # 页面/菜单等通用模型
│   └── MockDataFactory.cs      # Mock 数据生成工厂
│
├── Properties/                 # 项目属性文件
│   ├── AssemblyInfo.cs
│   ├── Resources.resx
│   ├── Resources.Designer.cs
│   ├── Settings.settings
│   └── Settings.Designer.cs
│
├── Resource/                   # 资源文件
│   └── Styles/
│       ├── Colors.xaml         # 工业风格配色方案
│       └── GlobalStyles.xaml   # 全局样式定义
│
├── Service/                    # UI 层服务
│   ├── INavigationService.cs   # 导航服务接口
│   ├── NavigationService.cs    # 导航服务实现
│   ├── INavigationAware.cs     # 页面导航感知接口
│   ├── IDialogService.cs       # 对话框服务接口
│   ├── WpfDialogService.cs     # 对话框服务实现
│   ├── IAuthenticationService.cs # 认证服务接口
│   ├── LiteDbAuthenticationService.cs # 持久化认证实现
│   ├── DevicePoolManager.cs    # 设备池管理器
│   ├── DevicePoolManager.CoreBridge.cs # Core 层设备池桥接
│   ├── StationConfigService.cs # 产线/工位配置与设备映射服务
│   └── MockMesBridgeService.cs # MES 对接模拟服务
│
├── View/                       # 界面文件
│   ├── LoginView.xaml          # ✅ 登录界面
│   ├── ShellView.xaml          # ✅ 主框架窗口
│   │
│   ├── LineOverviewView.xaml   # ✅ 产线拓扑总览
│   ├── StationMonitorView.xaml # ✅ 单工位监控
│   │
│   ├── StationManageView.xaml  # ✅ 产线工位与映射配置
│   ├── RecipeManageView.xaml   # ✅ 配方管理模块
│   ├── FlowEditView.xaml       # ✅ 视觉流程编辑器
│   │
│   ├── DevicePoolView.xaml     # ✅ 物理设备实例
│   ├── HardwareConsoleView.xaml # ✅ 轴/IO/相机调试台
│   │   └── HardwareConsole/         # 硬件调试子视图
│   │       ├── AxisControlView.xaml       # 轴控制
│   │       ├── CameraDebugView.xaml       # 相机调测
│   │       ├── CommDebugView.xaml         # 通信调试
│   │       └── IoMonitorView.xaml         # IO 监视
│   ├── PluginManageView.xaml   # ✅ 驱动插件管理
│   │
│   ├── DataTraceView.xaml      # ✅ 本地追溯与防错
│   ├── MesBridgeView.xaml      # ✅ MES 对接状态
│   ├── SystemSettingView.xaml  # ✅ 系统与存储设置
│   │
│   ├── AlarmView.xaml          # ✅ 报警日志模块
│   ├── UserManageView.xaml     # ✅ 用户管理模块
│   │
│   └── Dialogs & Windows/      # 弹窗与辅助窗口
│       ├── AddDeviceDialog.xaml
│       ├── ScanDeviceDialog.xaml
│       └── HardwareSelectWindow.xaml
│
└── ViewModel/                  # 视图模型
    ├── LoginViewModel.cs
    ├── ShellViewModel.cs       # ✅ 分栏菜单、导航同步、菜单收起、报警横幅
    │
    ├── LineOverviewViewModel.cs
    ├── StationMonitorViewModel.cs
    │
    ├── StationManageViewModel.cs # ✅ 产线/工位/设备映射 CRUD
    ├── RecipeManageViewModel.cs  # ✅ 配方 CRUD + 参数管理
    ├── FlowEditViewModel.cs
    │
    ├── DevicePoolViewModel.cs
    ├── HardwareConsoleViewModel.cs
    │   └── HardWareConsole/         # 硬件调试子 ViewModel
    │       ├── AxisControlViewModel.cs
    │       ├── CameraDebugViewModel.cs
    │       ├── CommDebugViewModel.cs
    │       ├── HardwareModels.cs
    │       └── IoMonitorViewModel.cs
    ├── PluginManageViewModel.cs
    │
    ├── DataTraceViewModel.cs
    ├── MesBridgeViewModel.cs
    ├── SystemSettingViewModel.cs
    │
    ├── AlarmViewModel.cs       # ✅ 报警列表 + 确认 + 导出
    ├── UserManageViewModel.cs  # ✅ 用户增删改查 + 权限分配
    │
    └── Dialogs & Windows/      # 弹窗与辅助窗口 ViewModel
        ├── AddDeviceDialogViewModel.cs
        ├── ScanDeviceDialogViewModel.cs
        └── HardwareSelectViewModel.cs
```

## 🎯 核心模块说明

### 1️⃣ 登录模块（LoginView）
- 用户认证由 `App.xaml.cs` 注入 `LiteDbAuthenticationService`（`IAuthenticationService` 的持久化实现），`IAuthenticationService.cs` 中同时保留 `MockAuthenticationService` 供快速演示。
- 权限等级验证：操作员 / 工程师 / 管理员。
- 登录成功后关闭登录窗、加载 `ShellView` 并注册全部业务页面。

### 2️⃣ 主框架（ShellView / ShellViewModel）
- 左侧二级菜单导航，按四组分栏：运行操作区、核心工程配置、硬件设备池、数据与运维。
- 自定义标题栏（最小化/最大化/关闭）+ 菜单收起/展开（宽度 230↔60），标题栏右侧新增全局“🚨 急停”按钮。
- 实时显示：当前用户、系统状态、未处理报警数；存在严重报警时弹出全局 Banner。
- 权限控制：`RequiredRole` 与 `CurrentUserRole` 比较，`UserRole` 小的用户无法看到高权限菜单项；急停按钮对 Operator 及以上角色可见。
- 菜单选中状态与 `NavigationService.PageChanged` 双向同步，并自动展开父级菜单。

### 3️⃣ 生产看板
- **产线拓扑总览（LineOverview）**: 产线级运行状态与 KPI 总览（对接中）。
- **单工位监控（StationMonitor）**: 按 `LineId + StationId` 展示实时数据（总数/OK/NG/良率/节拍），提供 Start / Pause / Resume / Stop / Trigger Once / Reset 控制。

### 4️⃣ 报警与诊断（AlarmView） - ✅ 完整实现
**功能：**
- 实时报警列表：时间/等级/来源/信息/状态
- 报警统计：总数/严重/错误/警告 分类统计
- 报警操作：单个确认/全部确认/清除历史/导出
- 报警等级：Info/Warning/Error/Critical（颜色区分）
- 自动刷新：每15秒模拟新报警

**数据流：** `GlobalData.AlarmCount` / `HasAlarm` / `HasCriticalAlarm` / `CriticalAlarmMessage` 驱动标题栏徽章与全局 Banner。

**Mock数据：** 5条历史报警 + 自动生成新报警

### 5️⃣ 核心工程配置
- **产线工位与映射（StationManage）**: 产线、工位、设备映射关系配置；支持启用/禁用工位并在拓扑树实时显示启用状态。
- **配方管理（RecipeManage）**: 配方 CRUD、参数分组、设备映射绑定状态、提交/审批/驳回流程，审批通过后方可下发到生产工位。
- **视觉流程编辑器（FlowEdit）**: 视觉检测流程步骤编排与调试入口。

### 6️⃣ 硬件设备池（P4 新增）
- **物理设备实例（DevicePool）**: 已实例化的 `IDevice` 集合管理、参数配置、连接/断开状态控制。
- **轴/IO/相机调试台（HardwareConsole）**: 单硬件在线调测：实时图像抓拍、轴点动/回零、IO 点位监视与强制切换。
- **驱动插件管理（PluginManage）**: 驱动 DLL 扫描装载、已安装驱动列表、版本与元数据查看。

### 7️⃣ 用户管理（UserManageView） - ✅ 完整实现
**功能：**
- 用户列表：用户名/显示名/角色/状态/最后登录时间
- 用户操作：新增/编辑/删除/重置密码/启用/禁用
- 权限分配：操作员/工程师/管理员三级角色
- 搜索筛选：关键字搜索 + 角色筛选
- 统计卡片：用户总数/管理员/工程师/操作员分类统计
- 操作日志：记录所有用户管理操作

**Mock数据：** 6个预置账号（含已禁用账号示例）

### 9️⃣ 数据统计（StatisticsView） - ✅ 完整实现
**功能：**
- 汇总统计卡片：总产量/OK数/NG数/综合良率/节拍时间/报警次数
- 时段切换：今日/本周/本月/本季度快捷查询
- 逐小时产量表格：时段/总量/OK/NG/良率
- 班组产量对比：A/B/C班生产数据
- 缺陷类型分布：按缺陷类型统计占比（含进度条可视化）
- 30秒自动刷新实时数据

**Mock数据：** 今日逐小时产量 + 缺陷分布（划痕/气泡/缺损/污点/色差）

### 🔟 系统设置（SettingsView） - ✅ 完整实现
**功能（4个Tab分组）：**

**📁 路径设置：**
- 图像保存目录/配方文件目录（支持浏览选择）
- 图像格式（BMP/PNG/JPEG/TIFF）
- 磁盘占用上限
- 仅保存NG图像/同时保存OK图像开关

**🌐 网络设置：**
- MES服务器 IP + 端口配置
- MES数据上传开关
- PLC IP + 端口配置
- 通信超时设置
- 网络连接测试（含延迟显示）

**🖥️ 通用设置：**
- 界面主题（深色/浅色）
- 界面语言（中文/英文）
- 屏保激活时间
- 开机自启动/声音报警/NG弹窗开关
- 软件版本信息

**📋 日志设置：**
- 日志保存目录
- 最低日志级别（Debug/Info/Warning/Error）
- 日志保留天数
- 单文件大小上限
- 滚动日志开关
- 日志级别说明

## 🎨 UI设计风格

### 配色方案（工业深色主题）
```
主色调: 深蓝灰 #1E3A5F（工业感）
强调色: 科技蓝 #00A8FF
背景色: 深黑 #1A1A1A / #2D2D30 / #3E3E42
状态色:
  - 成功: #00C853（绿）
  - 警告: #FFA726（橙）
  - 错误: #FF3D00（红）
  - 信息: #0091EA（蓝）
```

### 组件样式
- **按钮**: 圆角4px，鼠标悬停高亮
- **卡片**: 阴影效果，边框1px
- **表格**: 斑马纹，行高35-50px
- **状态指示**: 圆点/颜色标签

## 🔌 与 Core/Core 层对接点

### 已接入的运行时服务
1. **导航服务** (`Grayson.Vision.WpfUI.Service.INavigationService` / `NavigationService`)
   - 按 `PageType` 注册/跳转页面，支持 `GoBack` 与 `PageChanged` 事件。
   - 所有业务页面在 `App.xaml.cs.RegisterPages()` 中懒加载注册。

2. **认证服务** (`Grayson.Vision.WpfUI.Service.IAuthenticationService`)
   - 运行时注入 `LiteDbAuthenticationService`（持久化账号）。
   - 源码中保留 `MockAuthenticationService` 供离线演示。

3. **全局状态总线** (`Grayson.Vision.WpfUI.Common.GlobalData`)
   - 跨模块共享用户、生产、系统状态与报警；提供 `RaiseCriticalAlarm` / `DismissCriticalAlarm` 等事件。

### 需要对接的接口
4. **相机驱动** (`Core.HardDriver.ICamera`)
   - 连接/断开/采图/参数设置

5. **运动控制卡** (`Core.HardDriver.IMotionController`)
   - 连接/轴控制/位置读取

6. **工位运行主机** (`Core.StationWorker.IStationWorkerHost`)
   - 按 `LineId + StationId` 启动/暂停/恢复/停止 Worker
   - 发布工位信号并获取运行快照

7. **工位上下文与数据服务** (`Core.StationWorker.IStationContext` 及相关接口)
   - 配方读取：`IRecipeService`
   - 本地落地：`IDataStore` / `IImageArchiveService`
   - 异步同步：`ISyncService`（Outbox）

8. **生产数据** (`Core.Common.ProductionContext`)
   - 实时数据绑定到 GlobalData

## 📦 依赖包（packages.config）

```xml
- HandyControl (3.4.0)              # UI框架（可选）
- CommunityToolkit.Mvvm (8.2.0)    # MVVM工具（可选）
- LiveCharts.Wpf (0.9.7)           # 图表库（统计模块）
- MaterialDesignThemes (4.9.0)     # 图标资源（可选）
```

**注意：** 由于VS打开项目无法编辑csproj，需要手动通过以下方式安装：
1. 在VS中右键项目 -> "管理NuGet包"
2. 根据packages.config手动搜索安装
3. 或者关闭VS，编辑csproj后重新打开

## 🚀 快速启动

### 1. 安装依赖
- 还原NuGet包（如上所述）
- 添加Core项目引用: `Grayson.VisionApp.Core.dll`

### 2. 运行程序
- 启动项目: `Grayson.VisionApp.WpfUI`
- 默认登录账号（若使用持久化账号库，首次需初始化；Mock 账号如下）:
  - **管理员**: admin / admin123
  - **工程师**: engineer / eng123
  - **操作员**: operator / op123

### 3. 权限说明
| 菜单/模块 | 操作员 | 工程师 | 管理员 |
|------|--------|--------|--------|
| 产线拓扑总览 | ✅ | ✅ | ✅ |
| 单工位监控 | ✅ | ✅ | ✅ |
| 报警与诊断 | ✅ | ✅ | ✅ |
| 产线工位与映射 | ❌ | ✅ | ✅ |
| 配方管理 | ❌ | ✅ | ✅ |
| 视觉流程编辑器 | ❌ | ✅ | ✅ |
| 物理设备实例 | ❌ | ✅ | ✅ |
| 轴/IO/相机调试台 | ❌ | ✅ | ✅ |
| 驱动插件管理 | ❌ | ✅ | ✅ |
| 本地追溯与防错 | 查询/实时工单 | 查询/实时工单 | 查询/实时工单 |
| 用户管理 | ❌ | ❌ | ✅ |

## 🔧 后续扩展建议

### 高优先级:
1. **视觉检测模块深度对接**
   - 对接HalconDotNet HWindowControl替换当前占位画布
   - 对接Core层相机/PLC/机械手服务，实现真实参数下发
   - 流程步骤改为算法插件动态加载（MEF/反射）
   - ROI绘制、历史图回看、结果叠加标注

2. **数据统计模块增强**
   - 集成LiveCharts图表：产量趋势/良率曲线/缺陷分布
   - 报表导出：Excel/PDF
   - 历史数据查询

3. **系统设置增强**
   - 文件夹浏览对话框（System.Windows.Forms.FolderBrowserDialog）
   - 配置文件持久化（INI/JSON/XML）
   - 主题实时切换

### 中优先级:
4. **配方管理增强**
   - 参数编辑对话框（弹窗）
   - 配方导入导出（JSON/XML）
   - 配方对比功能

5. **硬件配置增强**
   - 自动扫描网络设备
   - 批量连接/断开
   - 设备健康度监控

### 低优先级:
6. **界面优化**
   - 动画效果（页面切换/数据更新）
   - 多语言支持
   - 自定义主题编辑器

7. **性能优化**
   - 虚拟化长列表
   - 图像显示性能优化
   - 内存占用优化

## 📝 代码规范

### 命名规范:
- View文件: `xxxView.xaml`
- ViewModel文件: `xxxViewModel.cs`
- 数据模型: `xxxModel`
- 命令: `xxxCommand`
- 属性: PascalCase
- 私有字段: `_camelCase`

### XAML规范:
- 使用StaticResource引用样式
- 避免代码后置，优先使用Command绑定
- 复杂UI抽取UserControl复用

### 注释规范:
- 类/方法使用XML注释 `/// <summary>`
- TODO标记: `// TODO: 说明`
- 对接点标记: `// TODO: 对接Core层的xxx`

## 🐛 已知问题

1. **Mock数据**
   - 报警、产线拓扑等当前仍使用 Mock 数据，需对接 Core 层实际数据；单工位监控与实时工单已接入 `IStationHostRuntime`。

2. **未注册页面**
   - `PageType` 中已定义 `LineOverview`、`StationMonitor`、`DataTrace`、`MesBridge`、`SystemSetting` 等页面，但尚未在 `App.xaml.cs.RegisterPages()` 中注册 View 工厂，需在对应页面完成后补齐。

3. **数据统计图表**
   - 当前使用 DataGrid 表格展示，建议后续接入 LiveCharts 可视化图表。

4. **路径浏览**
   - 系统设置中的文件夹浏览功能需对接 FolderBrowserDialog。

## 📧 联系方式

- 项目作者: Grayson.VisionApp Team
- 问题反馈: 创建Issue
- 文档更新: 2026-08-22

---

## ✅ 实现进度总结

| 模块 | 状态 | 说明 |
|------|------|------|
| 登录模块 | ✅ 完成 | 持久化认证 + Mock 实现并存，三级权限 |
| 主框架 | ✅ 完成 | 四组二级菜单 + 导航同步 + 收起展开 + 报警 Banner |
| 报警与诊断 | ✅ 完成 | 列表/统计/确认/导出 + GlobalData 驱动 Banner |
| 生产看板 | 🔄 对接中 | 产线拓扑总览 / 单工位监控页面已枚举，待注册 View 并对接 Core |
| 核心工程配置 | ✅ 页面结构完成 | 产线工位/配方/视觉流程编辑器已在导航注册 |
| 硬件设备池 | ✅ 菜单与入口完成 | 物理设备实例 / 调试台 / 驱动插件管理已在导航注册 |
| 用户管理 | ✅ 完成 | 增删改查 + 权限分配 |
| 数据统计 / 系统设置 | 🔄 对接中 | View 已存在，需随 Core 数据服务完善 |

所有已注册模块均采用 MVVM 模式，代码结构清晰，易于扩展！🎉



## 🆕 Shell 4.0 改造记录（2026-07）

### 本次已落地
- 四组分栏二级菜单：运行操作区 / 核心工程配置 / 硬件设备池 / 数据与运维。
- 菜单收起/展开：宽度 230↔60，支持 ToolTip 提示。
- 导航与菜单双向同步：`NavigationService.PageChanged` 自动更新选中项并展开父级。
- 全局严重报警 Banner：通过 `GlobalData.HasCriticalAlarm` / `CriticalAlarmMessage` 跨页面浮现。
- 标题栏实时状态：用户名、系统状态、未处理报警数。

### 已预留接口位置（待后续接入）
- 产线拓扑总览、单工位监控页面注册与 Core 数据绑定。
- 本地追溯与防错、MES 桥接、系统设置页面注册。
- HalconDotNet 实时图像窗口与算法插件动态加载。



