# Grayson.Vision.Nodes

流程节点实现库。每个节点由 **执行器（Executor）**、**参数模型（Param）**、**参数面板（TemplateView）** 三部分组成，可被 `Grayson.Vison.FlowEdit` 或 `Grayson.VisionApp.WpfUI` 加载并在 `Grayson.Vision.Core.StationWorker` 中执行。

在新版 **Worker-Host + IPC + Observer UI** 架构下，节点执行器仍然运行在：

- `Grayson.Vision.Core.StationWorker` 的本地线程中（`FlowEdit` 调试模式）
- `Grayson.Vision.WorkerHost` 独立进程中（产线运行模式）

因此节点实现必须保持 **无 UI 依赖、无进程假设**，只通过 `NodeExecutionContext` 读写端口、解析硬件、输出日志与图像。

---

## 1. 职责定位与 UI 架构交互规范

```text
Grayson.Vision.Contracts (节点接口与模型)
	↑
Grayson.Vision.Core (StationWorker 驱动节点执行)
	↑
Grayson.Vision.Nodes (本层：具体节点实现 + 参数面板)
	↑
Grayson.Vison.FlowEdit / Grayson.VisionApp.WpfUI (宿主平台)
```

- 本层引用 `Grayson.Vision.Contracts`。
- 由于参数面板使用 WPF XAML，因此也依赖 WPF 程序集（`PresentationCore`、`PresentationFramework`、`WindowsBase`、`System.Xaml`）。
- 节点执行器 **不直接操作硬件 SDK**，而是通过 `NodeExecutionContext` 读取输入端口、设置输出端口、写入日志。
- 节点执行器 **禁止引用任何 UI 控件**：不能创建 `HWindow`、不能弹出 MessageBox、不能访问 `Application.Current.Dispatcher`。

### 1.1 宿主主界面与属性弹窗 (`NodePropertyWindow`) 的职责分工

为了避免窗口句柄冲突与图像画布冗余，平台采用了“沙盒配置 + 宿主共享画笔”的设计：

1. **主界面右侧 (`FlowEditView` 右侧区)**：
	- **全局图像监视器（Main Viewer）**：全流程运行或单步运行时的唯一图像展示区。
	- 负责响应节点发起的交互指令（如：在主图上绘制 ROI 矩形/圆形/掩膜）。

2. **节点属性弹窗 (`NodePropertyWindow`)**：
	- **轻量化调参沙盒**：双击节点打开，尺寸控制在 `Width=520, Height=620` 左右。
	- **不包含**独立的 Halcon 图像控件；对于视觉匹配、测量等复杂节点，通过在自身的 `TemplateView` 中加入 `TabControl` 实现多页参数与交互控制。

---

## 2. 目录结构

```text
Grayson.Vision.Nodes/
├── All/                              # 所有节点分类
│   ├── DeviceIO/                     # 设备 IO 类节点
│   │   ├── AcquireImage/             # 相机采集
│   │   │   ├── AcquireImageParam.cs
│   │   │   ├── AcquireImageExecutor.cs
│   │   │   ├── AcquireImageTemplateView.xaml
│   │   │   └── AcquireImageTemplateView.xaml.cs
│   │   ├── AxisMove/
│   │   ├── DigitalOutput/
│   │   ├── LightControl/
│   │   └── PlcReadWrite/
│   ├── Logic/                        # 逻辑控制类节点
│   │   ├── ConditionIf/
│   │   ├── Delay/
│   │   ├── ForLoop/
│   │   ├── Merge/
│   │   ├── SwitchCase/
│   │   └── WaitSignal/
│   ├── Vision/                       # 视觉算法类节点（待扩展）
│   ├── ImagePreprocess/              # 图像预处理节点（待扩展）
│   ├── CalibrationLocation/          # 标定定位节点（待扩展）
│   ├── Measurement2D/                # 几何测量节点（待扩展）
│   ├── Identification/               # 识别读码节点（待扩展）
│   ├── MathLogic/                    # 逻辑运算节点（待扩展）
│   ├── FlowControl/                  # 流程控制节点（待扩展）
│   ├── CompositeGroup/               # 子流程节点（待扩展）
│   └── DataStorage/                  # 数据 MES 节点（待扩展）
├── Common/                           # 公共基类
│   ├── NodeExecutorBase.cs           # 强类型 Executor 抽象基类
│   └── ParamBase.cs                  # 参数模型基类（含 IDataErrorInfo 校验）
├── Themes/
│   └── Generic.xaml                  # 节点参数面板统一样式 & DataTemplate 全局映射
├── Properties/
└── Grayson.Vision.Nodes.csproj
```

---

## 3. 节点三件套开发规范

每个节点目录下按 `All/{Category}/{NodeName}/` 包含四个标准文件：

| 文件 | 命名约定 | 作用 |
|---|---|---|
| `XXXParam.cs` | `AcquireImageParam` | 节点配置参数模型，继承 `ParamBase`，支持属性变更通知、数据校验及交互 Command |
| `XXXExecutor.cs` | `AcquireImageExecutor` | 节点运行逻辑，继承 `NodeExecutorBase<TParam>`，实现 `ExecuteCoreAsync` |
| `XXXTemplateView.xaml` | `AcquireImageTemplateView` | 节点参数配置面板，内含 `DataTemplate` |
| `XXXTemplateView.xaml.cs` | `AcquireImageTemplateView.xaml.cs` | 代码后置文件（通常保持为空） |

### 3.1 参数模型（Param）

所有参数模型继承 `ParamBase`：

```csharp
using Grayson.Vision.Nodes.Common;
using System.Windows.Input;

namespace Grayson.Vision.Nodes.DeviceIO.AcquireImage
{
	public class AcquireImageParam : ParamBase
	{
		private string _cameraAlias = "CAM_01";
		public string CameraAlias
		{
			get => _cameraAlias;
			set => Set(ref _cameraAlias, value);
		}

		public override string this[string columnName]
		{
			get
			{
				if (columnName == nameof(CameraAlias) && string.IsNullOrWhiteSpace(CameraAlias))
					return "相机标识不能为空";
				return null;
			}
		}
	}
}
```

`ParamBase` 提供了：

- `ViewModelBase` 的属性变更通知能力（`Set<T>` 方法）
- `IDataErrorInfo` 接口支持，便于 XAML 绑定验证

### 3.2 参数 UI 模板（TemplateView）设计规范

根据节点复杂度，XAML 布局遵循以下规范：

#### 规范 A：简单节点（延时、简单 IO、条件判断）

使用单页 Vertical `StackPanel` 或 `Grid` 布局即可：

```xml
<UserControl x:Class="Grayson.Vision.Nodes.DeviceIO.AcquireImage.AcquireImageTemplateView"
			 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
			 xmlns:local="clr-namespace:Grayson.Vision.Nodes.DeviceIO.AcquireImage"
			 mc:Ignorable="d" d:DesignHeight="400" d:DesignWidth="300" Background="#1E1E1E">
	<UserControl.Resources>
		<ResourceDictionary>
			<ResourceDictionary.MergedDictionaries>
				<ResourceDictionary Source="pack://application:,,,/Grayson.Vision.Nodes;component/Themes/Generic.xaml"/>
			</ResourceDictionary.MergedDictionaries>

			<DataTemplate DataType="{x:Type local:AcquireImageParam}">
				<StackPanel Margin="8">
					<TextBlock Text="相机标识:" Style="{StaticResource NodeParamHeaderLabel}"/>
					<TextBox Text="{Binding CameraAlias, UpdateSourceTrigger=PropertyChanged}" Style="{StaticResource NodeParamInputStyle}"/>
				</StackPanel>
			</DataTemplate>
		</ResourceDictionary>
	</UserControl.Resources>

	<Grid d:DataContext="{d:DesignInstance Type=local:AcquireImageParam, IsDesignTimeCreatable=True}">
		<ContentControl Content="{Binding}"/>
	</Grid>
</UserControl>
```

#### 规范 B：复杂节点（模板匹配、几何测量、缺陷检测等）

**必须使用 `TabControl` 组织不同维度的配置**，禁止在一个长滚动页面里堆砌所有参数：

```xml
<TabControl Background="#252526" Foreground="White" BorderThickness="0">
	<TabItem Header="🎯 模板训练">
		<!-- 训练参数、绘制 ROI Command -->
	</TabItem>
	<TabItem Header="⚙️ 运行参数">
		<!-- 匹配分数、重叠率、极性 -->
	</TabItem>
	<TabItem Header="📊 结果显示">
		<!-- 结果显示配置 -->
	</TabItem>
</TabControl>
```

### 3.3 执行器（Executor）

所有执行器继承 `NodeExecutorBase<TParam>`：

```csharp
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Nodes.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.DeviceIO.AcquireImage
{
	[Node(NodeType.AcquireImage, NodeCategory.DeviceIO, typeof(AcquireImageParam))]
	[NodePort("Image", PortType.Out, PortCategory.Data, dataType: "Image", colorHex: "#9B59B6")]
	public class AcquireImageExecutor : NodeExecutorBase<AcquireImageParam>
	{
		public const string PORT_OUT_IMAGE = "Image";

		protected override async Task ExecuteCoreAsync(
			FlowNodeBase node,
			AcquireImageParam param,
			NodeExecutionContext context,
			CancellationToken token)
		{
			context.Log($"[相机采集] 开始处理... (相机: {param.CameraAlias})");

			// 1. 从上下文解析硬件（逻辑名称 -> 真实 ICamera）
			var camera = context.ResolveDevice(param.CameraAlias) as ICamera;
			if (camera == null)
			{
				context.Log($"[相机采集] 未找到相机: {param.CameraAlias}");
				return;
			}

			// 2. 采图
			var image = await camera.SoftTriggerAsync();

			// 3. 输出到端口
			context.SetOutputValue(node, PORT_OUT_IMAGE, image);
			context.Log($"[相机采集] 采图成功，输出句柄到端口 {PORT_OUT_IMAGE}");
		}
	}
}
```

`NodeExecutorBase<TParam>` 会自动完成：

- `node` 和 `context` 的空值检查
- `node.ParameterModel` 到 `TParam` 的安全强转
- 子类只需实现 `ExecuteCoreAsync`

---

## 4. 如何新增一个节点

1. 在 `Grayson.Vision.Nodes/All/{Category}/{YourNodeName}/` 下新建四个文件：
	- `YourNodeParam.cs`：继承 `ParamBase`
	- `YourNodeExecutor.cs`：继承 `NodeExecutorBase<YourNodeParam>`
	- `YourNodeTemplateView.xaml`：参数配置面板（简单节点用 Vertical StackPanel，复杂节点使用 `TabControl`）
	- `YourNodeTemplateView.xaml.cs`：代码后置（保持为空）
2. 在 `Themes/Generic.xaml` 中配置隐式 `DataTemplate` 类型映射。
3. 在 `YourNodeExecutor` 上添加 `[Node]` 和 `[NodePort]` 特性。
4. 若涉及新分类/新类型，在 `Grayson.Vision.Contracts.Business.Enums.NodeCategory` / `NodeType` 中扩展。
5. 编译后，`Grayson.Vison.FlowEdit` 的 `NodePluginLoader` 会自动扫描 `Grayson.Vision.Nodes*.dll` 并加载所有节点。

---

## 5. 开发注意事项

1. **禁用冗余画布**：节点 UI 面板（`TemplateView`）内**切勿嵌套独立的 Halcon 渲染控件**。绘制 ROI、显示缩略图必须通过 Command/事件通知主界面右侧的主 ImageDisplay。
2. **数据绑定安全**：所有参数模型必须继承 `ParamBase` 并在 Setter 中调用 `Set(...)` 触发变更，保证属性弹窗修改参数时，流程配置能实时同步与保存。
3. **端口类型匹配**：节点输出端口的数据类型名称（如 `Image`、`Boolean`、`Double`）需要与下游节点期望的类型匹配，才能在 FlowEdit 中建立有效连接。
4. **分类一致性**：`NodeCategory` 使用 `Grayson.Vision.Contracts.Business.Enums.NodeCategory` 中定义的分类（如 `DeviceIO`、`FlowControl`、`ImageInput` 等），新增节点时请与 Contracts 保持一致。
5. **跨进程运行时**：节点执行器可能在 `Grayson.Vision.WorkerHost` 独立进程中运行，禁止访问 UI 线程、禁止直接序列化 UI 对象、禁止依赖 `Application.Current`。
6. **图像输出建议**：图像数据应通过 `context.SetOutputValue(node, "Image", renderImage)` 输出，由 `StationWorker` 自动提取并推送 `ImageRenderEventArgs`。避免节点内直接持有过大 HObject 实例过长时间。

---

## 6. 依赖

- `Grayson.Vision.Contracts`
- WPF 程序集：`PresentationCore`、`PresentationFramework`、`WindowsBase`、`System.Xaml`
- `Newtonsoft.Json`（通常由 Contracts 传递依赖）

---

## 7. 已实现的节点

| 分类 | 节点 |
|---|---|
| **DeviceIO（设备 IO）** | `AcquireImage`（相机采集）、`AxisMove`（轴运动）、`DigitalOutput`（数字量输出）、`LightControl`（光源控制）、`PlcReadWrite`（PLC 读写） |
| **Logic（逻辑控制）** | `ConditionIf`（条件判断）、`Delay`（延时）、`ForLoop`（循环）、`Merge`（合并）、`SwitchCase`（分支）、`WaitSignal`（等待信号） |
| **其他分类** | `Vision`、`ImagePreprocess`、`CalibrationLocation`、`Measurement2D`、`Identification`、`MathLogic`、`FlowControl`、`CompositeGroup`、`DataStorage` 已预留目录，待按业务需求扩展 |

---

## 8. 维护提示

- 新增复杂节点时，优先复用 `Themes/Generic.xaml` 中已定义的 `NodeParamHeaderLabel`、`NodeParamInputStyle`、`NodeParamButtonStyle` 等样式，保持视觉一致性。
- 节点 Command 建议通过 `ICommand` 绑定触发，不要在 `Param` 中直接调用 `Window.ShowDialog()`。
- 若节点需要与主界面图像区交互，请通过事件或 Messenger/ServiceBus 解耦，而不是直接引用 `FlowEditView`。
