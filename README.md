# Grayson.Vision.Host — 机器视觉工位装配平台（上位机）

> 面向「视觉工位快速搭建 / 换型」的 C# + WPF 上位机平台：**流程可编排、设备可插拔、模板/标定/配方可配置、结果可追溯**。
> 形态：单仓库多工程，`.NET Framework 4.7.2` / WPF / x64；视觉内核 HALCON，流程引擎自研。
>
> 本文档 **2026-09-10 更新**（补能力域总览与状态三态）；架构细节以当前代码为准。旧版快照均归档于 [`Archive/`](Archive/说明.md)。

---

## 0. 能力域总览（本平台能干什么）

平台以「**1 个可配置底座 × N 个可插拔业务能力域**」组织——视觉任务按**任务族**沉淀为可配置模板，换产品只动配方 / 模板 / 标定档案、不重编译。
**覆盖工业视觉三类主流任务，并按「已实现 → 在研 → 规划」分阶段推进**（体现可扩展性，非单点功能堆砌）：

| 能力域 | 工业场景 | 状态 | 说明 |
|---|---|---|---|
| **A 视觉引导定位 · 取放** | 视觉引导机器人（VGR）取放料 | 🟢 已实现（引擎完整）· 真机调试中 | 成像→模板匹配→坐标换算→机械手联动取放；ETH / EIH 双布局 + 标定两级发布 |
| **B 深度学习质检** | 缺陷检测 / 分类 / 分割 | 🟢 已实现（端到端可跑） | HALCON DL 一站式（DLTool → .hdl → C# 集成）；内置分割/分类演示任务 |
| **C 外观尺寸测量** | 尺寸 / 几何量测量 | 🟢 已实现（纯软件可跑） | 卡尺+拟合测量链 → 像素当量换算 mm → 测量值±公差判 OK/NG |
| **D 多相机复合工位** | 贴装 / 组装「取-校-放」 | 🟡 在研 | 固定上相机 + 固定下相机 + 眼在手上相机；坐标系统一与视轴镜像处理 |
| **E 视觉任务模板中心** | 任务可配置化交付 | 🟢 已实现 | 三大任务族模板库 + 模型仓库；模板 = 选模板 + 绑设备 + 应用配方 |
| **F 扩展方向** | 3D 视觉 / 无监督异常检测 | ⚪ 规划 | 平台已预留节点与插件语义（Plugins.Inference.* / 3D 算子），按需扩展 |

> **开箱可跑的演示**：DL 质检（能力域 B）与外观测量（能力域 C）内置演示包（模型 + 精选图）已随仓库入库，clone 后打开「任务模板中心」即自动落地，**无外部依赖、无需真机**——见 §5。

---

## 1. 平台是什么

一句话：**把"一套视觉检测/引导设备的工程搭建"做成"配置 + 编排"，而不是"改代码"。**

覆盖三类现场角色：

| 角色 | 关心什么 | 平台怎么满足 |
|---|---|---|
| 现场工程师 | 换产品/换工位少改代码 | 模板、标定档案、配方、工位配置全部配置化；流程用节点图编排 |
| 上位机/视觉开发 | 新相机/新机械手/新逻辑怎么接入 | `Contracts` 接口契约 + `Plugins.*` 插件化，宿主零改动 |
| 设备/产线 | 稳定、可查、能对接 MES | 工位状态机、工单追溯、数据追溯、MesBridge、日志分级 |

已落地能力带：
- **视觉定位引导**：模板匹配（形状/相关性）+ 手眼标定 → 机器人/运动轴抓取引导；
- **测量与识别**：卡尺/边缘/亚像素测量、Blob/OCR 基础识别，AI（ONNX / DLTool）集成链路；
- **标定体系 v2**：物理量建模（H 手眼 / e 偏心 / t 对针 / s 像素当量…），向导化采集、残差体检、两级发布；
- **流程编排**：节点图（采集→预处理→匹配/测量→坐标换算→设备动作→数据回传），自研执行引擎；
- **设备插件**：相机（海康/巴斯勒）、SCARA（Epson）、运动控制卡（ZMC）、PLC（西门子/Modbus/通用）、推理（ONNX）；
- **配置持久化**：LiteDB（工位/设备/历史）+ JSON（配方/标定矩阵），支持旧 JSON 无损兼容；
- **追溯与通信**：工单追踪、日志分级、MES 桥接、触发信号统一入口。

---

## 2. 解决方案结构（工程清单）

解决方案文件：`Grayson.Vision.Host.slnx`。仓库共 **17 个产品工程**（slnx 收录 14 个；另有 3 个未收录，见下表注）；`.workbuddy/` 下另有 4 个一次性验证工程（不入库）。

| 层 | 工程 | 类型 | 职责一句话 | 规模(cs) |
|---|---|---|---|---|
| 契约底座 | **Grayson.Vision.Contracts** | 类库 | 接口/领域模型/DTO/枚举，全仓依赖最底层；不引 WPF/HALCON/硬件 SDK | 128 |
| 数据 | **Grayson.Vision.Repository** | 类库 | LiteDB 持久化：工位配置/配方存储/历史仓储 | 28 |
| 运行 | **Grayson.Vision.Core** | 类库 | StationWorker 运行宿主：工位状态机、设备池、调度、触发器、业务流程 Process | 35 |
| 视觉内核 | **Grayson.Vision.HalconWrapper** | 类库 | HALCON 业务化封装：模板/匹配/测量/标定/预处理，不依赖 WPF | 28 |
| 显示内核 | **Grayson.Vision.HalconWrapper.Wpf** | WPF 类库 | HALCON 图像 WPF 显示宿主/渲染服务/交互翻译层 | 10 |
| 节点 | **Grayson.Vision.Nodes** | 类库 | 流程节点实现 + 参数面板（10 大类节点），供编辑器与运行引擎共享 | 103 |
| 编辑器 | **Grayson.Vison.FlowEdit** | WinExe | 独立流程编辑器（拖节点/配参数/验流程），也可嵌入主程序 | 16 |
| 主程序 | Grayson.VisionApp.WpfUI 目录 | WinExe | 主界面壳：登录/总览/工位监控/模板/标定/配方/设备/系统等 40+ 页 | 106 |
| 相机 | **Plugins.Camera.Basler** | 插件 | 巴斯勒相机 `ICamera` 实现（Pylon） | 4 |
| 相机 | **Plugins.Camera.Hikvision** | 插件 | 海康相机 `ICamera` 实现（MvCamCtrl） | 4 |
| 机械手 | **Plugins.Robot.Epson** | 插件 | Epson SCARA：RC+/SPEL/TCP 脚本三层通信适配 | 7 |
| 运动 | **Plugins.Motion.Zmc** | 插件 | ZMC 运动控制卡 `IMotionCard` 实现 | 5 |
| PLC | **Plugins.PLC.Siemens** | 插件 | 西门子 S7 通信插件（未入 slnx） | 2 |
| PLC | **Plugins.PLC.Modbus** | 插件 | Modbus 占位工程（未入 slnx，暂无实现） | 2 |
| 通信 | **Plugins.Protocol.Universal** | 插件 | 通用协议解析/指令策略（网口/串口） | 8 |
| 推理 | **Plugins.Inference.OnnxRuntime** | 插件 | ONNX 推理 `IInferenceProvider`：YOLO/分类/异常检测 → Region/框 | 4 |

> 注：`Grayson.VisionApp.WpfUI` 目录内含 **两个 csproj**（`Grayson.Vision.WpfUI.csproj` 挂载全插件进 slnx；`Grayson.VisionApp.WpfUI.csproj` 为轻量变体，引用集不同，产物同名 `Grayson.Vision.WpfUI.exe`，未入 slnx）。建议后续收敛为一个入口。

依赖方向（已按 csproj 实引核对）：

```text
Plugins.* ─┐                                  ┌─> Grayson.Vision.HalconWrapper.Wpf
Contracts <-┼- Repository <- Core <-+         ├-> Grayson.Vision.Nodes  ─┐
(底座)      └- HalconWrapper <-+    |          │                          v
                              |    └-----------> Grayson.Vison.FlowEdit <-> Grayson.VisionApp.WpfUI(WPF 主程序)
                              └----------------> Grayson.Vision.Nodes ──> 同上
规则：Contracts 不依赖任何上层；Core/Repository/HalconWrapper 不引 WPF、不引具体硬件 SDK；只有主程序与编辑器允许组合一切。
```

---

## 3. 四个核心概念（30 秒扫盲）

1. **工位（Station）= 运行载体**：一条线/一台机上的一个视觉任务点。`StationConfigModel`（配置、触发源、流程键、运行参数）→ `StationWorker`（状态机 + 驱动流程）→ 具体业务 `Process`（如 VisionPickPlaceProcess、MahjongPickProcess）。
2. **配方（Recipe）= 随产品变的工艺**：流程节点图 + 工艺参数 + 逻辑设备需求。JSON 落盘（`Recipes\{Code}.json`），发布分**工位级**（共享标定矩阵）与**配方级**（矩阵快照进配方），不直接下发设备。
3. **模板/标定 = 视觉资产**：模板源图留档可回溯；标定按物理量建模（手眼 H、偏心 e、对针 t、像素当量 s），状态机 Draft→SampleComplete→Verified→Published|Expired，向导采集 + 残差门禁。
4. **节点（Node）= 最小执行单元**：10 大类（图像输入/预处理/识别/定位标定/测量2D/数学逻辑/设备IO/流程控制/数据存储/复合组），同一节点实现既被 FlowEdit 可视化编辑，也被运行时引擎调度执行。

---

## 4. 文档导航（先读哪几篇）

| 文档 | 内容 | 建议 |
|---|---|---|
| `README.md`（本篇） | 定位 / 工程清单 / 概念扫盲 / 快速开始 | 所有人先读 |
| `ARCHITECTURE.md` | 分层架构、依赖规则、关键机制（设备/工位/配方/节点/标定/显示） | 要改代码前读 |
| `STATUS_TODO.md` | 各工程/特性域完成度、已知缺口与隐患、下一步 TODO | 接需求前必读 |
| `DOCS_INDEX.md` | 仓库全部 md/html 文档清单 + 时效状态 + 阅读路径 | 找资料先查 |
| `architecture-overview.html` | 可视化架构图（分层/依赖/模块卡片） | 讲给别人听用 |

各工程内另有 README（`README_contracts.md` 等），随工程目录走；业务设计文档入口见 `DOCS_INDEX.md`。

---

## 5. 快速开始

```text
1) 打开 Grayson.Vision.Host.slnx（或直接编译入口 csproj）
2) 目标框架 .NET Framework 4.7.2；平台建议 x64
3) 外部原生依赖需放入 exe 运行目录：
   - halcondotnet / halcon.dll（HALCON Runtime）
   - 相机 SDK 原生库（Basler pylon / 海康 MVS）
   - 各插件驱动原生 dll 随插件 Content 拷贝
4) 运行目录 = Grayson.VisionApp.WpfUI\bin\Debug\（主程序 Grayson.Vision.WpfUI.exe）
5) 典型上机流程：建档工位 → 绑定/领用设备 → 采图调成像(模板/平场) → 标定(向导) → 编排配方 → 运行验证
```

> 本机构建/回归脚本（个人开发环境用）：`.workbuddy/` 下 `build_p2.py`、`build_recipe_publish.py`、`verify_p0_assert.py`、`verify_recipe_publish_assert.py`、`verify_halcon_operators.py`、`deploy_halcon_runtime.py`。MSBuild 用 32 位 `VS\18\Community\MSBuild\Current\Bin\MSBuild.exe`（amd64 版易崩）。

### 开箱演示（无需真机 / 无需外部素材）

`Assets\DLDemo`（DL 模型 + 精选图）与 `Assets\MeasureDemo`（测量素材）**已随仓库入库**，构建时复制到运行目录。首次打开主程序「**任务模板中心**」即自动落地演示任务（幂等）：

- **能力域 B**：MDL-SEG（药片分割）/ MDL-CLS（镁片分类）——`.hdl` 模型 + 本地图源，纯软件执行出结果；
- **能力域 C**：TPL-AM-001（六角螺母外圆直径）——FitCircle 测量 → 像素当量换算 mm → 测量值±公差判 OK/NG。

> 部署到任一工位 → 工位监视页【▶ 启动】→ 每周期取图执行 → 结果写 `Config\DemoData\{station}.csv`（含测量值 / 判定）。

---

## 6. 顶层目录导览

```text
Grayson.Vision.Host/
├─ Grayson.Vision.Contracts/      契约层（接口/模型/DTO，13 个领域目录）
├─ Grayson.Vision.Core/           工位运行宿主与业务 Process
├─ Grayson.Vision.Repository/     LiteDB 持久化
├─ Grayson.Vision.HalconWrapper/  HALCON 封装（无 WPF）
├─ Grayson.Vision.HalconWrapper.Wpf/  HALCON 显示宿主/交互层
├─ Grayson.Vision.Nodes/          流程节点实现
├─ Grayson.Vison.FlowEdit/        流程编辑器 exe
├─ Grayson.VisionApp.WpfUI/       主程序（View/ViewModel/Service/...）
├─ Plugins.Camera.Basler|Hikvision/   相机插件
├─ Plugins.Robot.Epson/           机械手插件
├─ Plugins.Motion.Zmc/            运控卡插件
├─ Plugins.PLC.Siemens|Modbus/    PLC 插件
├─ Plugins.Protocol.Universal/    通用通信插件
├─ Plugins.Inference.OnnxRuntime/ AI 推理插件
├─ DLLLib/                        第三方 dll 汇集（halcondotnet 等）
├─ packages/                      NuGet 本地包
├─ InterviewPrep/                 排查案例笔记（标定/模板质量诊断，已入库）
├─ resume/                        个人求职/面试材料（独立 git 仓库，根仓库已忽略）
└─ .workbuddy/                    开发工具与记忆（回归脚本/部署脚本/日志）
```

> 根目录散落的历史设计文档（2026-08 至 09-06）属**知识资产**，仍可参考，时效判定见 `DOCS_INDEX.md`，勿随手删除。

---

## 7. 状态速览（三态）

**🟢 已实现（有代码 + 有验证口径）**
- 主线能力：标定 v2、配方两级发布、流程编排、模板源图留档、设备插件、UI 工程台——**已落地并有回归断言保护**；
- 深度学习质检（能力域 B）：DLTool `.hdl` 自举加载 + 推理验证通过，纯软件任务引擎可端到端执行；
- 外观尺寸测量（能力域 C）：卡尺+拟合测量链 + 测量值±公差判据，已闭环可跑；
- 视觉任务模板中心（能力域 E）：三大任务族模板库 + 模型仓库，内置演示包 clone 即可跑。

**🟡 在研**
- 多相机复合工位（能力域 D）：固定上相机 + 固定下相机 + 眼在手相机的标定链路与业务模板；
- 引导定位真机端到端：ST_003 落点偏差归因与矩阵形状收敛（标定引擎完整，真机闭环联调中）。

**⚪ 规划**
- 3D 视觉（双目 / 结构光 / 点云算子）列为学习与扩展下一站；无监督异常检测冷启动流程。

> 完整完成度矩阵、已知缺口与隐患见 `STATUS_TODO.md`。
