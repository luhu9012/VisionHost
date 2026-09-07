# Grayson.Vision.FlowEdit — 可视化流程编排器

> 校正：2026-09-07。⚠️ 旧版文档所述"Worker-Host + IPC + 远端监控客户端"已随多进程方案移除；现行=单进程内驱动 `Core.StationWorker`（嵌入式线程宿主），可独立运行亦可嵌入主程序。
> 注意工程名拼写：**Grayson.Vison.FlowEdit**（目录/产物名沿用，勿改）。

## 1. 定位

```text
独立 WinExe：拖节点 → 连线 → 配参数(节点属性面板实时预览) → 运行调试
嵌入形态：WpfUI 的 FlowEditViewWrapper 页把本编辑器嵌进主程序，联动配方/工位
```

规模：16 个 .cs；依赖 Contracts/Core/Nodes/Repository/HalconWrapper(+Wpf)/OnnxRuntime（运行时组合全部视觉与推理能力）。

## 2. 代码结构

| 目录/文件 | 职责 |
|---|---|
| `Views\FlowEditView(.xaml)` | 编辑器画布（拖拽/连线/缩放） |
| `Views\NodePropertyWindow` | 节点属性面板（参数实时预览） |
| `ViewModels\` | 图 VM/节点 VM/端口连线 |
| `Controls\` / `Converters\` | 画布控件/连线转换器 |
| `RecipeManager\` | 配方管理（编辑态配方 ↔ JSON） |
| `Helpers\` / `Services\` | 图保存加载、执行服务 |
| `MainWindow` | 独立运行壳 |

## 3. 与主程序/配方的联动（stage7 之后现状）

- FlowEdit 内对节点图/配方的编辑经共享契约落 `RecipeStorageService`（JSON），主程序 RecipeManage 读同一存储；
- 编辑器 ↔ 工位联动：按工位 Scope 加载/保存配方；采集/设备节点运行依赖 Core 侧设备池（嵌入主程序时才具备完整设备上下文）。

## 4. 新增节点接入编辑器

1. 在 `Nodes\All\{分类}\` 建 Param/Executor/TemplateView 三件套（+csproj `<Compile Include>`）；
2. 节点元数据（显示名/分类/端口）来自节点描述契约 → 编辑器自动出现在工具箱；
3. 参数面板走共享样式（Nodes\Themes），实时预览事件由 NodePreviewHelper 抛出。

## 5. 关联

- 节点体系：`Grayson.Vision.Nodes\README.md`（权威）
- 设计：`节点属性面板实时预览_设计.md`（实现随 FlowEdit 演进）
- 状态：`STATUS_TODO.md`（编辑器/配方域）
