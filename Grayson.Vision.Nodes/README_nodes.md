# Grayson.Vision.Nodes — 扩展说明（已同步）

此文档为历史/扩展说明。主要、最新的项目说明与目录结构已合并并发布在 README.md（同目录下）。请以 README.md 为权威来源。

若需要离线或增补参考，下面为该模块的关键要点（快速索引）：

- 目的：描述节点实现约定、目录结构、参数传递与 UI 交互规范，便于视觉算法工程师与框架开发者按约定扩展节点。
- 关键目录：
  - All/：按业务分组的节点实现（每个节点目录包含 Param / Executor / TemplateView / TemplateView.xaml.cs）。
  - Common/：公共基类（NodeExecutorBase、ParamBase 等）。
  - Themes/：参数面板共享样式（Generic.xaml）。

- 核心原则：
  1. 节点执行器必须无 UI 依赖，所有图像/硬件交互通过 NodeExecutionContext 或设备服务总线完成。
  2. 参数模型继承 ParamBase，通过双向绑定与 UI 实时同步。
  3. 复杂节点的参数面板使用 TabControl 分页组织，简洁节点使用垂直 StackPanel。

- 快速操作建议：
  1. 新增节点：在 All/{Category}/{YourNode}/ 下创建 YourNodeParam.cs、YourNodeExecutor.cs、YourNodeTemplateView.xaml、YourNodeTemplateView.xaml.cs；在 Executor 上添加 [Node]/[NodePort] 特性。
  2. 参数交互：在 Param 中通过 ICommand 暴露交互命令（如 DrawRoiCmd），由宿主主视图绑定并响应绘制命令。
  3. 样式复用：优先复用 Themes/Generic.xaml 中的样式资源以保证界面一致性。

- 我可以为你自动执行的操作（任选其一）：
  1. 将 All/ 目录与 README.md 中列出的目录做自动比对并生成差异报告（列出缺失/新增节点目录）。
  2. 将参数传递（参数流）文档整理为英文版或生成 API 风格的快速参考表。

---

更多细节请参见 README.md。