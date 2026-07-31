# Grayson.VisionApp.WpfUI - 工业视觉上位机界面架构文档

> **改造进度：✅ P0 + P1 已完成（2026-07-18）　　✅ P2 已完成（Worker 运行链路与 UI 接入完成）　　✅ P3 已完成（插件治理能力已落地）　　✅ P4 已完成（文档基线、独立治理页、硬件标准化与可观测性接入）**

---

## 🔄 架构改造阶段进展

| 阶段 | 状态 | 当前结论 |
|---|---|---|
| **P0** | ✅ 已完成 | 角色化视图拆分与 Shell 二级菜单已完成 |
| **P1** | ✅ 已完成 | 多产线模型与插件接口迁移到 Core 已完成 |
| **P2** | ✅ 已完成 | Core Worker 契约、插件迁移、配方索引与 UI 接入链路已贯通（Host/Worker/Outbox/运行态） |
| **P3** | ✅ 已完成 | 视觉工程台已接入插件治理最小入口（刷新/启用/禁用/回滚） |
| **P4** | ✅ 已完成 | 已新增独立 `PluginGovernanceView`、接入硬件适配器工厂与设备状态总线桥接，并补充运行时轻量指标展示 |

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
- ✅ 页面框架：登录、Shell 导航、九大业务页面结构齐全。
- ✅ P2 接入：`MonitorViewModel` / `VisionViewModel` 已通过 `StationWorkerRuntimeService` 接入 Worker Host。
- ✅ P4-2：独立插件治理页 `PluginGovernanceView` 已接入菜单导航，支持扫描/启用/禁用/回滚/审计日志。
- ✅ 运行链路：可执行 Start/Stop、发布节拍信号、接收工位状态摘要。
- ⚠️ 真实设备与外部系统：当前仍以演示/Mock 数据为主，MES/数据库/真实相机链路待 P4 后续阶段深化。

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
├── Common/                     # 公共基础设施
│   ├── ViewModelBase.cs        # ViewModel基类
│   ├── RelayCommand.cs         # 命令实现
│   ├── Enums.cs                # 枚举定义（权限/报警等级/设备状态）
│   ├── Converters.cs           # 值转换器集合
│   └── GlobalData.cs           # 全局单例数据上下文
│
├── Service/                    # UI层服务
│   ├── INavigationService.cs   # 导航服务接口
│   ├── NavigationService.cs    # 导航服务实现
│   ├── IDialogService.cs       # 对话框服务接口
│   ├── IAuthenticationService.cs # 认证服务（Mock实现）
│   └── StationWorkerRuntimeService.cs # P2 运行时接入服务（Host/Worker 统一入口）
│
├── Resource/                   # 资源文件
│   ├── Colors.xaml             # 工业风格配色方案
│   └── Styles/
│       └── GlobalStyles.xaml   # 全局样式定义
│
├── View/                       # 界面文件
│   ├── LoginView.xaml          # 登录界面
│   ├── ShellView.xaml          # 主框架窗口
│   ├── MonitorView.xaml        # ✅ 生产监控大屏
│   ├── VisionView.xaml         # ✅ 视觉检测模块
│   ├── PluginGovernanceView.xaml # ✅ 插件治理独立入口（P4）
│   ├── RecipeView.xaml         # ✅ 配方管理模块（完整实现）
│   ├── HardwareConfigView.xaml # ✅ 硬件配置模块（完整实现）
│   ├── AlarmView.xaml          # ✅ 报警日志模块（完整实现）
│   ├── UserManageView.xaml     # ✅ 用户管理模块（完整实现）
│   ├── StatisticsView.xaml     # ✅ 数据统计模块（完整实现）
│   └── SettingsView.xaml       # ✅ 系统设置模块（完整实现）
│
├── ViewModel/                  # 视图模型
│   ├── LoginViewModel.cs
│   ├── ShellViewModel.cs
│   ├── MonitorViewModel.cs     # ✅ 包含工位状态/生产数据
│   ├── VisionViewModel.cs      # ✅ 视觉检测控制
│   ├── PluginGovernanceViewModel.cs # ✅ 插件治理页面逻辑（P4）
│   ├── RecipeViewModel.cs      # ✅ 配方CRUD + 参数管理
│   ├── HardwareConfigViewModel.cs # ✅ 多硬件管理（相机/运动卡/PLC/机器人）
│   ├── AlarmViewModel.cs       # ✅ 报警列表 + 确认 + 导出
│   ├── UserManageViewModel.cs  # ✅ 用户增删改查 + 权限分配
│   ├── StatisticsViewModel.cs  # ✅ 产量趋势 + 良率分析 + 缺陷分布
│   └── SettingsViewModel.cs    # ✅ 路径/网络/通用/日志四类设置
│
├── App.xaml                    # 应用启动配置
├── App.xaml.cs                 # 启动逻辑（登录->主窗口切换）
└── packages.config             # NuGet包配置
```

## 🎯 核心模块说明

### 1️⃣ 登录模块（LoginView）
- 用户认证（Mock数据：admin/admin123，engineer/eng123，operator/op123）
- 权限等级验证
- 记住密码功能占位

### 2️⃣ 主框架（ShellView）
- 左侧菜单导航（8个模块菜单）
- 自定义标题栏（最小化/最大化/关闭）
- 实时显示：当前用户/系统状态/报警数量
- 权限控制：根据用户角色显示可访问模块

### 3️⃣ 生产监控（MonitorView） - ✅ 完整实现
**功能：**
- 实时生产数据：总数/OK数/NG数/良率
- 4工位状态监控：状态/检测结果/节拍时间
- 启动/停止/复位控制
- 当前配方显示

**Mock数据：** 每2秒自动更新模拟生产数据

### 4️⃣ 视觉检测（VisionView） - ✅ 三栏重构完成
**功能：**
- 顶部全局工具栏：模式切换（生产/调试）、配方加载、分屏切换、运行控制（启动/停止/单步）
- 中部三栏标准工控布局：
  - 左栏：多相机容器（1~N路列表、每路触发/全屏、主画面显示占位）
  - 中栏：视觉流程编辑器（步骤启用、顺序上移下移、算子调试入口）
  - 右栏：生产态结果与参数只读面板（不混入调试编辑控件）
- 调试能力独立面板：算法参数调试、硬件快调、标定工具、插件清单、配方分配（工位/相机/流程/阈值集）
- 权限隔离：仅工程师/管理员可进入调试模式并打开调试面板
- 可演示交互：内置 Mock 运行器（自动节拍、步骤状态流转、OK/NG计数、良率、日志滚动）
- 顶部模式/配方/分屏下拉已修复显示，演示时可直接切换并联动刷新
- ComboBox / TabItem / DataGrid 已统一为深色主题，修复亮色背景导致的可读性问题

- 底部联动区：实时日志、产线状态、当前配方、在线相机、OK/NG计数

**说明：** 当前为可运行骨架，已预留对接 Core 层服务与 HalconDotNet 的入口。

### 5️⃣ 配方管理（RecipeView） - ✅ 完整实现
**功能：**
- 配方列表：名称/型号/使用次数/修改时间
- 配方操作：新建/复制/删除/加载到系统/保存
- 参数管理：按组分类（相机/光源/定位/尺寸/缺陷/判定）
- 参数编辑：21+个典型工业视觉参数
- 导入/导出功能占位

**Mock数据：** 4个示例配方（电池片/手机屏幕/PCB）

### 6️⃣ 硬件配置（HardwareConfigView） - ✅ 完整实现
**功能：**
- 分类管理：工业相机/运动控制卡/PLC/工业机器人
- 设备操作：连接/断开/测试
- 参数配置：根据设备类型动态加载配置项
- 测试结果显示
- 批量连接/扫描设备占位

**Mock数据：**
- 4台相机（海康/大华/Basler）
- 2台运动卡（正运动/固高）
- 2台PLC（西门子/三菱）
- 2台机器人（ABB/发那科）

### 7️⃣ 报警日志（AlarmView） - ✅ 完整实现
**功能：**
- 实时报警列表：时间/等级/来源/信息/状态
- 报警统计：总数/严重/错误/警告 分类统计
- 报警操作：单个确认/全部确认/清除历史/导出
- 报警等级：Info/Warning/Error/Critical（颜色区分）
- 自动刷新：每15秒模拟新报警

**Mock数据：** 5条历史报警 + 自动生成新报警

### 8️⃣ 用户管理（UserManageView） - ✅ 完整实现
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

## 🔌 与Core层对接点

### 需要对接的接口:
1. **相机驱动** (`Core.HardDriver.ICamera`)
   - 连接/断开/采图/参数设置

2. **运动控制卡** (`Core.HardDriver.IMotionController`)
   - 连接/轴控制/位置读取

3. **工位运行主机** (`Core.StationWorker.IStationWorkerHost`)
   - 按 `LineId + StationId` 启动/暂停/恢复/停止 Worker
   - 发布工位信号并获取运行快照

4. **工位上下文与数据服务** (`Core.StationWorker.IStationContext` 及相关接口)
   - 配方读取：`IRecipeService`
   - 本地落地：`IDataStore` / `IImageArchiveService`
   - 异步同步：`ISyncService`（Outbox）

5. **生产数据** (`Core.Common.ProductionContext`)
   - 实时数据绑定到GlobalData

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
- 默认登录账号:
  - **管理员**: admin / admin123
  - **工程师**: engineer / eng123
  - **操作员**: operator / op123

### 3. 权限说明
| 模块 | 操作员 | 工程师 | 管理员 |
|------|--------|--------|--------|
| 生产监控 | ✅ | ✅ | ✅ |
| 视觉检测 | ✅ | ✅ | ✅ |
| 报警日志 | ✅ | ✅ | ✅ |
| 配方管理 | ❌ | ✅ | ✅ |
| 硬件配置 | ❌ | ✅ | ✅ |
| 数据统计 | ❌ | ✅ | ✅ |
| 用户管理 | ❌ | ❌ | ✅ |
| 系统设置 | ❌ | ✅ | ✅ |

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
   - 所有数据均为Mock，需对接Core层实际数据

2. **数据统计图表**
   - 当前使用DataGrid表格展示，建议后续接入LiveCharts可视化图表

3. **路径浏览**
   - 系统设置中的文件夹浏览功能需对接FolderBrowserDialog

## 📧 联系方式

- 项目作者: Grayson.VisionApp Team
- 问题反馈: 创建Issue
- 文档更新: 2026-07-18

---

## ✅ 实现进度总结

| 模块 | 状态 | 说明 |
|------|------|------|
| 登录模块 | ✅ 完成 | Mock认证，三级权限 |
| 主框架 | ✅ 完成 | 导航 + 权限控制 |
| 生产监控 | ✅ 完成 | 实时数据 + Mock自动刷新 |
| 视觉检测 | ✅ 三栏骨架完成 | 多相机+流程面板+Tab参数区，待对接真实硬件/算法插件 |
| 配方管理 | ✅ 完成 | 21+参数 + CRUD |
| 硬件配置 | ✅ 完成 | 多品牌设备管理 |
| 报警日志 | ✅ 完成 | 自动刷新 + 确认导出 |
| 用户管理 | ✅ 完成 | 增删改查 + 权限分配 |
| 数据统计 | ✅ 完成 | 产量/良率/缺陷分析 |
| 系统设置 | ✅ 完成 | 路径/网络/通用/日志 |

所有模块均采用MVVM模式，代码结构清晰，易于扩展！🎉



## 🆕 VisionView 2.0 改造记录（2026-07）

### 本次已落地
- 三栏主布局（左画面 / 中流程 / 右生产结果）
- 顶部全局工具栏（模式、配方、分屏、运行控制）
- 多相机容器（相机列表 + 单路触发 + 全屏入口）
- 流程编辑器（启用/禁用、上移下移、调试入口）
- 调试能力独立弹层面板（参数调试 / 硬件快调 / 标定工具 / 插件清单）
- 调试权限隔离（仅工程师、管理员）
- 底部实时日志与产线状态联动显示
- 右侧亮色背景问题修复：统一使用深色主题样式（ComboBox/DataGrid）

### 已预留接口位置（待后续接入）
- HalconDotNet 实时图像窗口
- Core层相机/PLC/机械手服务
- 配方服务联动（切换配方自动加载流程与阈值）
- 算法插件动态加载（九点标定/定位/测量/缺陷/OCR/胶路无需改主界面）



