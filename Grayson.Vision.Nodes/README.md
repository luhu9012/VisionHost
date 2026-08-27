# Grayson.Vision.Nodes

仓库：节点实现库（.NET Framework 4.7.2）。每个节点包含三部分：
- Param：参数模型（继承 ParamBase，支持数据校验与 ICommand 交互）
- Executor：运行时逻辑（继承 NodeExecutorBase<TParam>，通过 NodeExecutionContext 读写端口、访问设备服务）
- TemplateView：WPF 参数面板（XAML + code-behind）

目标：为宿主（FlowEdit / VisionApp.WpfUI / WorkerHost）提供可复用的视觉处理节点，保持执行层无 UI 依赖、可跨进程运行。

目录结构（关键节点与说明）：

Grayson.Vision.Nodes/
├── All/                          # 按业务分组的节点实现（每组为一个子目录）
│   ├── CalibrationLocation/      # 标定与位置校正、模板匹配等
│   │   ├── ApplyFixture/         # 应用标定矩阵到 ROI（将基准坐标映射到当前图像）
│   │   ├── CreateFixture/        # 通过匹配结果建立基准矩阵（ΔX/ΔY/Δθ）
│   │   ├── NccMatch/             # 灰度/NCC 模板匹配
│   │   └── ShapeMatch/           # 基于形状的模板匹配（Halcon create_shape_model）
│   ├── DataStorage/              # 图像与测量数据持久化、MES 上报
│   │   ├── MesReport/
│   │   ├── SaveData/
│   │   └── SaveImage/
│   ├── DeviceIO/                 # 外部设备控制（PLC、光源等）
│   │   ├── LightControl/
│   │   └── PlcReadWrite/
│   ├── FlowControl/              # 流程控制（条件、分支、循环、延时、等待等）
│   │   ├── ConditionIf/
│   │   ├── Delay/
│   │   ├── ForLoop/
│   │   ├── Merge/
│   │   ├── SwitchCase/
│   │   ├── TerminateFlow/
│   │   ├── WaitSignal/
│   │   └── WhileLoop/
│   ├── Identification/           # 识别与读码（OCR、Barcode、DL 推理、颜色识别）
│   │   ├── ColorIdentify/
│   │   ├── DlInference/
│   │   ├── ReadBarcode/
│   │   └── ReadOCR/
│   ├── ImageInput/               # 图像输入（相机采集、读取文件、通道操作）
│   │   ├── AcquireImage/
│   │   ├── ImageChannel/
│   │   └── ReadImageFile/
│   └── ImagePreprocess/          # 预处理（仿射变换、滤波、阈值、ROI 操作）
│       ├── AffineImage/
│       ├── ImageFilter/
│       ├── ImageThreshold/
│       └── ROIOperation/
├── Common/                       # 公共基类（NodeExecutorBase、ParamBase）
├── Converters/                   # XAML 值转换器
├── Themes/                       # 共享样式（Generic.xaml）
├── Properties/
└── Grayson.Vision.Nodes.csproj

每个已实现节点目录一般包含：
- {Node}Param.cs
- {Node}Executor.cs
- {Node}TemplateView.xaml
- {Node}TemplateView.xaml.cs

功能简要说明（按分组）：

- ImageInput：负责图像来源，包括实际相机采集（AcquireImage）、从磁盘读取（ReadImageFile）、通道分离/合并（ImageChannel）。
- ImagePreprocess：图像去噪、滤波、阈值分割、仿射变换、ROI 裁切与掩膜生成，为后续算法清洗图像。 
- CalibrationLocation：定位与标定，提供基准匹配（ShapeMatch、NccMatch）及位置修正（CreateFixture/ApplyFixture），用于后续 ROI 跟随与坐标转换。
- Identification：条码/字符识别与深度学习推理（YOLO/分类/分割），输出文本与检测框或分割结果。
- FlowControl：流程编排节点（If/Switch/Loop/Delay/Wait/Terminate），控制执行顺序与分支逻辑。
- DeviceIO：与外设通信（PLC 读写、光源控制等），用于闭环控制与触发。
- DataStorage：图像与测量结果存盘、上报 MES 或写入 CSV/数据库，支持按 OK/NG 分类保存。

实现细节与约定
- 节点执行器必须保持无 UI 依赖：所有与画面交互通过 ICommand/事件告知宿主，由宿主在主视图上执行绘制/交互。
- 参数模型（Param）应继承 ParamBase，使用 Set(...) 触发 PropertyChanged，并实现 IDataErrorInfo 以支持 XAML 验证。
- 所有 Executor 使用 NodeExecutionContext 进行端口读写（context.GetInputValue / SetOutputValue）并通过 context.ResolveDevice 或服务容器访问硬件。
- 复杂节点建议在 TemplateView 中使用 TabControl 分页组织参数，避免一个长滚动面板。

实时预览（节点属性面板内嵌视图窗口，2026-08-22 新增）
- 图像处理类节点可在属性编辑时实时看到图像变化，完整设计见根目录《节点属性面板实时预览_设计.md》。
- 节点侧接入只需两步：
  1. Param 覆写 `public override bool SupportsPreview => true;`（声明"需要预览区"，弹窗切换两列布局）；
  2. Executor 内用基类便捷属性 `Preview?.`（即 `context.Preview`）场景式绘制——`Preview?.BeginScene()` → `Preview?.AddBorrowed(输入底图)` → `Preview?.Add(...)`。
- 样板：`All/ImagePreprocess/ImageThreshold/ImageThresholdExecutor.cs`（阈值分割画绿色区域 + 参数标注）。
- 所有权约定：`Add` 提交即所有权转移（显示层托管释放）；同时作为端口输出继续被下游消费的对象必须提交副本——用 `NodePreviewHelper.CopyForDisplay(res.Data)`（HalconWrapper，object 判型）。底图走 `AddBorrowed`（借用，生命周期归端口缓存）。矩形框可用 `NodePreviewHelper.CreateRectangle(r1,c1,r2,c2)`。
- 生产运行 `Preview` 恒为 null，所有调用判空跳过，节点行为零变化；节点源码依旧零 `using HalconDotNet`。
- **注意**：Nodes 调用 HalconWrapper 方法时，返回类型必须是 `Result<object>` 而非 `Result<HObject>`——后者会暴露 HObject 类型导致 CS0012。图像加载用 `ImageBasicTool.LoadImage`（返回 `Result<object>`），而非 `ReadImageFile`（返回 `Result<HObject>`）。
- 已接入预览的节点（9 个）：ReadImageFile、ImageThreshold、ImageFilter、AffineImage、ROIOperation、ShapeMatch、NccMatch、ColorIdentify、ReadBarcode、ReadOCR。

如何新增节点（快速步骤）
1. 在 All/{Category}/{YourNode}/ 创建四个文件（Param/Executor/TemplateView/TemplateView.xaml.cs）。
2. 在 Executor 上添加 [Node(...)] 与 [NodePort(...)] 特性，声明节点类型与端口。
3. 在 Themes/Generic.xaml 中添加或使用现有 DataTemplate/样式，使宿主能自动识别并渲染参数面板。
4. 编译并在 FlowEdit 中验证节点可用性（NodePluginLoader 会扫描并加载节点 DLL）。

常见注意事项
- 禁止在 Executor 中直接访问 Application.Current、Window 或 Halcon 渲染控件。
- 输出图像请用轻量句柄或序列化友好的引用，避免长时间持有大型 HObject 导致内存问题。
- 保持 NodeCategory 与 Contracts 中定义一致，必要时同步 Contracts 的枚举。

如需我：
1) 我可以自动对比 All/ 目录与 README 列表，生成差异报告（缺失/新增节点）。
2) 我可以把本 README 导出为英文版或生成节点 API 参考表。
