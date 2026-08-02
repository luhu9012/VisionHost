基于你提供的最新源码架构（`All/{Category}/{NodeName}/` 目录结构、`NodeExecutorBase<TParam>` 抽象基类、`ParamBase` 参数验证、`Themes/Generic.xaml` 样式库），结合我们之前确认的 UI 架构分工（**主界面右侧为唯一图像渲染区 + 双击弹窗轻量化调参 + 复杂节点内置 TabControl + 弹窗/主界面画笔联动**），我为你对最新的 `README.md` 进行了完整升级与规范规范化。

你可以直接使用以下内容更新覆盖 `README.md`：

---

# Grayson.Vision.Nodes

流程节点实现库。每个节点由 **执行器（Executor）**、**参数模型（Param）**、**参数面板（TemplateView）** 三部分组成，可被 `Grayson.Vison.FlowEdit` 或 `Grayson.VisionApp.WpfUI` 加载并执行。

## 1. 职责定位与 UI 架构交互规范

```text
Grayson.Vision.Contracts (节点接口与模型)
		↑
Grayson.Vision.Nodes (本层：具体节点实现 + 参数面板)
		↑
Grayson.Vison.FlowEdit / Grayson.VisionApp.WpfUI (宿主平台)

```

* 本层引用 `Grayson.Vision.Contracts`。
* 由于参数面板使用 WPF XAML，因此也依赖 WPF 程序集（`PresentationCore`、`PresentationFramework`、`WindowsBase`、`System.Xaml`）。
* 节点执行器 **不直接操作硬件 SDK**，而是通过 `NodeExecutionContext` 读取输入端口、设置输出端口、写入日志。

### 1.1 宿主主界面与属性弹窗 (`NodePropertyWindow`) 的职责分工

为了避免窗口句柄冲突与图像画布冗余，平台采用了“沙盒配置 + 宿主共享画笔”的设计：

1. **主界面右侧 (`FlowEditView` 右侧区)**：
* **全局图像监视器（Main Viewer）**：全流程运行或单步运行时的唯一图像展示区。


* 负责响应节点发起的交互指令（如：在主图上绘制 ROI 矩形/圆形/掩膜）。


2. **节点属性弹窗 (`NodePropertyWindow`)**：
* **轻量化调参沙盒**：双击节点打开，尺寸控制在 `Width=520, Height=620` 左右。
* **不包含**独立的 Halcon 图像控件；对于视觉匹配、测量等复杂节点，通过在自身的 `TemplateView` 中加入 `TabControl` 实现多页参数与交互控制。



---

## 2. 目录结构

```text
Grayson.Vision.Nodes/
├── All/                              # 所有节点分类
│   ├── ImageInput/                     # 设备 IO 类节点 (如相机采集、PLC读写)
│   │   └── AcquireImage/             # 相机采集示例
│   │       ├── AcquireImageParam.cs
│   │       ├── AcquireImageExecutor.cs
│   │       ├── AcquireImageTemplateView.xaml
│   │       └── AcquireImageTemplateView.xaml.cs
│   └── Vision/                       # 视觉算法类节点 (如模板匹配、几何测量)
│       └── ShapeMatch/               # 复杂节点示例
│           ├── ShapeMatchParam.cs
│           ├── ShapeMatchExecutor.cs
│           ├── ShapeMatchTemplateView.xaml
│           └── ShapeMatchTemplateView.xaml.cs
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
| --- | --- | --- |
| `XXXParam.cs` | `AcquireImageParam` | 节点配置参数模型，继承 `ParamBase`，支持属性变更通知、数据校验及交互 Command |
| `XXXExecutor.cs` | `AcquireImageExecutor` | 节点运行逻辑，继承 `NodeExecutorBase<TParam>`，实现 `ExecuteCoreAsync` |
| `XXXTemplateView.xaml` | `AcquireImageTemplateView` | 节点参数配置面板，内含 `DataTemplate` |
| `XXXTemplateView.xaml.cs` | `AcquireImageTemplateView.xaml.cs` | 代码后置文件（通常保持为空） |

### 3.1 参数模型（Param）

所有参数模型继承 `ParamBase`：

```csharp
using Grayson.Vision.Nodes.Common;
using System.Windows.Input;

namespace Grayson.Vision.Nodes.Vision.ShapeMatch
{
    public class ShapeMatchParam : ParamBase
    {
        private double _minScore = 0.7;
        public double MinScore
        {
            get => _minScore;
            set => Set(ref _minScore, value);
        }

        // 交互 Command (例如：通知主界面右侧画图区激活绘制)
        public ICommand DrawRoiCmd { get; }
        public ICommand TrainModelCmd { get; }

        public ShapeMatchParam()
        {
            // Command 初始化...
        }

        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(MinScore) && (MinScore < 0 || MinScore > 1.0))
                    return "最小匹配得分必须在 0.0 到 1.0 之间";
                return null;
            }
        }
    }
}

```

`ParamBase` 提供了：

* `ViewModelBase` 的属性变更通知能力（`Set<T>` 方法）
* `IDataErrorInfo` 接口支持，便于 XAML 绑定验证

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

**必须使用 `TabControl**` 组织不同维度的配置，禁止在一个长滚动页面里堆砌所有参数：

```xml
<UserControl x:Class="Grayson.Vision.Nodes.Vision.ShapeMatch.ShapeMatchTemplateView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:local="clr-namespace:Grayson.Vision.Nodes.Vision.ShapeMatch"
             mc:Ignorable="d" d:DesignHeight="400" d:DesignWidth="300" Background="#1E1E1E">
    <UserControl.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="pack://application:,,,/Grayson.Vision.Nodes;component/Themes/Generic.xaml"/>
            </ResourceDictionary.MergedDictionaries>

            <DataTemplate DataType="{x:Type local:ShapeMatchParam}">
                <TabControl Background="#252526" Foreground="White" BorderThickness="0">
                    <!-- Tab 1: ROI 绘制与模板训练 -->
                    <TabItem Header="🎯 模板训练">
                        <StackPanel Margin="8">
                            <TextBlock Text="1. 区域设置 (联动主界面右侧画布):" Style="{StaticResource NodeParamHeaderLabel}"/>
                            <UniformGrid Columns="2" Margin="0 0 0 8">
                                <Button Content="✏️ 画矩形 ROI" Command="{Binding DrawRoiCmd}" Style="{StaticResource NodeParamButtonStyle}"/>
                                <Button Content="⭕ 画圆形 ROI" Command="{Binding DrawCircleRoiCmd}" Style="{StaticResource NodeParamButtonStyle}" Margin="4 0 0 0"/>
                            </UniformGrid>

                            <TextBlock Text="2. 训练学习:" Style="{StaticResource NodeParamHeaderLabel}"/>
                            <Button Content="⚡ 训练/学习当前模板" Command="{Binding TrainModelCmd}" Style="{StaticResource NodeParamPrimaryButtonStyle}"/>
                        </StackPanel>
                    </TabItem>

                    <!-- Tab 2: 查找参数 -->
                    <TabItem Header="⚙️ 查找参数">
                        <StackPanel Margin="8">
                            <TextBlock Text="最小匹配得分:" Style="{StaticResource NodeParamHeaderLabel}"/>
                            <TextBox Text="{Binding MinScore, UpdateSourceTrigger=PropertyChanged}" Style="{StaticResource NodeParamInputStyle}"/>
                        </StackPanel>
                    </TabItem>
                </TabControl>
            </DataTemplate>
        </ResourceDictionary>
    </UserControl.Resources>

    <Grid d:DataContext="{d:DesignInstance Type=local:ShapeMatchParam, IsDesignTimeCreatable=True}">
        <ContentControl Content="{Binding}"/>
    </Grid>
</UserControl>

```

### 3.3 全局隐式 DataTemplate 映射 (`Themes/Generic.xaml`)

为了确保属性弹窗在调用 `<ContentControl Content="{Binding ParameterModel}"/>` 时能够根据 `ParameterModel` 的类型自动匹配并渲染对应的 UI，需在 `Themes/Generic.xaml` 中汇总或引入模板：

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:acq="clr-namespace:Grayson.Vision.Nodes.DeviceIO.AcquireImage"
                    xmlns:match="clr-namespace:Grayson.Vision.Nodes.Vision.ShapeMatch">

    <!-- 通用文本/输入框/按钮样式 -->
    <Style x:Key="NodeParamHeaderLabel" TargetType="TextBlock">
        <Setter Property="Foreground" Value="#AAA"/>
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="Margin" Value="0 4 0 4"/>
    </Style>

    <!-- 1. 相机采集参数模板映射 -->
    <DataTemplate DataType="{x:Type acq:AcquireImageParam}">
        <acq:AcquireImageTemplateView/>
    </DataTemplate>

    <!-- 2. 形状匹配参数模板映射 -->
    <DataTemplate DataType="{x:Type match:ShapeMatchParam}">
        <match:ShapeMatchTemplateView/>
    </DataTemplate>

</ResourceDictionary>

```

### 3.4 执行器（Executor）

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

		protected override async Task ExecuteCoreAsync(FlowNodeBase node, AcquireImageParam param, NodeExecutionContext context, CancellationToken token)
		{
			// 1. 记录日志
			context.Log($"[相机采集] 开始处理... (相机: {param.CameraAlias})");

			// 2. 采图逻辑
			await Task.Delay(100, token);
			string mockImage = $"HImage_Handle_{param.CameraAlias}_{System.DateTime.Now:HHmmss.fff}";

			// 3. 输出到端口
			context.SetOutputValue(node, PORT_OUT_IMAGE, mockImage);
			context.Log($"[相机采集] 采图成功，输出句柄: {mockImage}");
		}
	}
}

```

`NodeExecutorBase<TParam>` 会自动完成：

* `node` 和 `context` 的空值检查
* `node.ParameterModel` 到 `TParam` 的安全强转
* 子类只需实现 `ExecuteCoreAsync`

---

## 4. 如何新增一个节点

1. 在 `Grayson.Vision.Nodes/All/{Category}/{YourNodeName}/` 下新建四个文件：
* `YourNodeParam.cs`：继承 `ParamBase`
* `YourNodeExecutor.cs`：继承 `NodeExecutorBase<YourNodeParam>`
* `YourNodeTemplateView.xaml`：参数配置面板（简单节点用 Vertical StackPanel，复杂节点使用 `TabControl`）
* `YourNodeTemplateView.xaml.cs`：代码后置（保持为空）


2. 在 `Themes/Generic.xaml` 中配置隐式 `DataTemplate` 类型映射。
3. 在 `YourNodeExecutor` 上添加 `[Node]` 和 `[NodePort]` 特性。
4. 编译后，`Grayson.Vison.FlowEdit` 的 `NodePluginLoader` 会自动扫描 `Grayson.Vision.Nodes*.dll` 并加载所有节点。

---

## 5. 开发注意事项

1. **禁用冗余画布**：节点 UI 面板（`TemplateView`）内**切勿嵌套独立的 Halcon 渲染控件**。绘制 ROI、显示缩略图必须通过 Command/事件通知主界面右侧的主 ImageDisplay。


2. **数据绑定安全**：所有参数模型必须继承 `ParamBase` 并在 Setter 中调用 `Set(...)` 触发变更，保证属性弹窗修改参数时，流程配置能实时同步与保存。
3. **端口类型匹配**：节点输出端口的数据类型名称（如 `Image`、`Boolean`、`Double`）需要与下游节点期望的类型匹配，才能在 FlowEdit 中建立有效连接。
4. **分类一致性**：`NodeCategory` 使用 `Grayson.Vision.Contracts.Business.Enums.NodeCategory` 中定义的分类（如 `DeviceIO`、`FlowControl`、`ImageInput` 等），新增节点时请与 Contracts 保持一致。



# TODO 实现所有节点：
```
/// <summary>
    /// 工业 2D 视觉平台的 10 大核心业务分类（参考 VisionMaster 架构，暗黑主题高辨识度系）
    /// </summary>
    public enum NodeCategory
    {
        // 1. 青绿/湖绿系 (Teal Cyan)
        [NodeFieldMeta("📷", "#00A896", "图像采集", "图像采集与输入")]
        ImageInput,

        // 2. 蓝绿/薄荷系 (Mint Green-Blue)
        [NodeFieldMeta("🎨", "#02C39A", "图像增强", "图像预处理与增强")]
        ImagePreprocess,

        // 3. 科技蓝系 (Tech Blue)
        [NodeFieldMeta("🎯", "#0077B6", "标定定位", "标定与位置跟随")]
        CalibrationLocation,

        // 4. 暖琥珀/金黄系 (Warm Amber)
        [NodeFieldMeta("📏", "#D97706", "几何测量", "2D 几何测量与检测")]
        Measurement2D,

        // 5. 亮橙/暖铜系 (Copper Orange)
        [NodeFieldMeta("🔍", "#EA580C", "识别读码", "识别与读码")]
        Identification,

        // 6. 电光紫系 (Electric Purple)
        [NodeFieldMeta("🧮", "#7C3AED", "逻辑运算", "逻辑与算术运算")]
        MathLogic,

        // 7. 洋红/品红系 (Magenta Violet)
        [NodeFieldMeta("🔀", "#C026D3", "流程控制", "流程控制与条件分支")]
        FlowControl,

        // 8. 柔粉紫系 (Soft Rose Orchid)
        [NodeFieldMeta("📦", "#DB2777", "子流程", "Group 子流程")]
        CompositeGroup,

        // 9. 蓝灰/钢蓝系 (Slate Steel Blue)
        [NodeFieldMeta("🔌", "#2563EB", "设备 IO", "设备通信与硬件控制")]
        DeviceIO,

        // 10. 炭晶/冷钛系 (Cold Titanium)
        [NodeFieldMeta("💾", "#475569", "数据 MES", "数据存储与系统交互")]
        DataStorage
    }

    /// <summary>
    /// 工业 2D 视觉节点全量类型枚举 (派生自父分类同色系渐变)
    /// </summary>
    public enum NodeType
    {
        // ==========================================
        // 1. 📷 图像采集与输入 (ImageInput - #00A896 青绿系)
        // ==========================================
        [NodeFieldMeta("📸", "#00A896", "相机采集", "相机采集 (触发/连续)")]
        AcquireImage,       // 海康/大恒等 SDK 触发采集[cite: 4]

        [NodeFieldMeta("🖼️", "#028090", "图像读取", "本地图像/序列读取")]
        ReadImageFile,      // 从磁盘读取单张/离线图片序列[cite: 4]

        [NodeFieldMeta("🎛️", "#05668D", "通道拆分", "RGB/HSV 通道拆分合成")]
        ImageChannel,       // RGB/HSV 拆分或多灰度图合成[cite: 4]

        // ==========================================
        // 2. 🎨 图像预处理与增强 (ImagePreprocess - #02C39A 蓝绿系)
        // ==========================================
        [NodeFieldMeta("🧹", "#02C39A", "图像滤波", "图像滤波 (平滑/去噪/形态学)")]
        ImageFilter,        // 高斯/中值/形态学膨胀腐蚀[cite: 4]

        [NodeFieldMeta("🌓", "#00A884", "阈值分割", "阈值分割 (固定/自适应/Otsu)")]
        ImageThreshold,     // 固定/动态/Otsu 阈值二值化[cite: 4]

        [NodeFieldMeta("✂️", "#008E73", "ROI 提取", "ROI 提取与 Mask 掩膜")]
        ROIMask,            // 裁剪/生成 Mask 掩膜区域[cite: 4]

        [NodeFieldMeta("✨", "#007562", "图像增强", "图像增强 (对比度/直方图)")]
        ImageEnhance,       // 直方图均衡化/对比度拉伸/图像相减[cite: 4]

        // ==========================================
        // 3. 🎯 标定与位置跟随 (CalibrationLocation - #0077B6 科技蓝系)
        // ==========================================
        [NodeFieldMeta("📐", "#0077B6", "手眼标定", "九点 / 手眼标定")]
        Calib2D,            // 像素转毫米、平移旋转标定矩阵计算[cite: 4]

        [NodeFieldMeta("🧩", "#0096C7", "形状匹配", "基于形状/边缘模板匹配")]
        ShapeMatch,         // 基于 Halcon Shape-Based 查找定位[cite: 4]

        [NodeFieldMeta("🏁", "#03045E", "灰度匹配", "基于灰度/NCC 模板匹配")]
        NccMatch,           // 基于灰度纹理匹配[cite: 4]

        [NodeFieldMeta("⚓", "#023E8A", "位置修正", "位置修正 (基准/参照系跟随)")]
        Fixturing,          // 提取旋转平移矩阵，用于后续 ROI 位置跟随[cite: 4]

        // ==========================================
        // 4. 📏 2D 几何测量与检测 (Measurement2D - #D97706 暖琥珀系)
        // ==========================================
        [NodeFieldMeta("🔎", "#D97706", "卡尺测量", "卡尺找边 / 找线 / 找圆")]
        CaliperMeasure,     // 单/多卡尺检测边缘、线段、圆弧[cite: 4]

        [NodeFieldMeta("📐", "#B45309", "几何测量", "几何距离与角度测量")]
        GeometryMeasure,    // 点到点、点到线距离、两线夹角测量[cite: 4]

        [NodeFieldMeta("⚪", "#92400E", "Blob 分析", "Blob 连通域斑点分析")]
        BlobAnalysis,       // 连通域面积、周长、圆度、质心提取[cite: 4]

        [NodeFieldMeta("⚠️", "#F59E0B", "缺陷检测", "表面缺陷与瑕疵检测")]
        DefectDetect,       // 黄金模板差分、划痕/脏污检测[cite: 4]

        // ==========================================
        // 5. 🔍 识别与读码 (Identification - #EA580C 亮橙系)
        // ==========================================
        [NodeFieldMeta("🏁", "#EA580C", "条码识别", "一维码 / 二维码识别")]
        ReadBarcode,        // 一维码 (Code128 等) / 二维码 (QR/DM) 识别[cite: 4]

        [NodeFieldMeta("🔤", "#C2410C", "字符识别", "字符识别 (OCR / OCV)")]
        ReadOCR,            // 文本字符识别与打印质量验证[cite: 4]

        [NodeFieldMeta("🤖", "#9A3412", "深度学习", "深度学习 AI 推理")]
        DlInference,        // YOLO/Halcon DL 分类与目标检测[cite: 4]

        // ==========================================
        // 6. 🧮 逻辑与算术运算 (MathLogic - #7C3AED 电光紫系)
        // ==========================================
        [NodeFieldMeta("🗺️", "#7C3AED", "坐标转换", "坐标矩阵转换 (像素->机械手)")]
        OffsetMath,         // 像素坐标转机器人/机械手物理坐标[cite: 4]

        [NodeFieldMeta("🧮", "#6D28D9", "公式计算", "表达式 / 动态公式计算")]
        ScriptMath,         // 利用 DynamicExpresso 进行复杂数学计算[cite: 4]

        [NodeFieldMeta("🔀", "#5B21B6", "变量映射", "变量类型映射与转换")]
        VarMapper,          // 变量类型转换与数据拼装[cite: 4]

        [NodeFieldMeta("📝", "#8B5CF6", "字符串格式", "字符串格式化与报文拼装")]
        StringFormat,       // 动态拼接 PLC 报文或 MES 字符串[cite: 4]

        // ==========================================
        // 7. 🔀 流程控制与条件分支 (FlowControl - #C026D3 洋红系)
        // ==========================================
        [NodeFieldMeta("🔱", "#C026D3", "条件判断", "条件分支 (If / Else)")]
        ConditionIf,        // 根据测量结果判 OK/NG 走向不同分支[cite: 4]

        [NodeFieldMeta("🔀", "#A21CAF", "多路分支", "多路条件分支 (Switch)")]
        SwitchCase,         // 根据物料类型/型号跳转对应逻辑[cite: 4]

        [NodeFieldMeta("🔄", "#86198F", "循环控制", "循环控制 (For / While)")]
        ForLoop,            // 多工位重复检测或批量计算[cite: 4]

        [NodeFieldMeta("🔀", "#701A75", "分支汇聚", "数据与控制流合并 (Merge)")]
        Merge,              // 多条分支并行后收拢交汇点[cite: 4]

        [NodeFieldMeta("⏳", "#D946EF", "延时等待", "延时等待 (Delay)")]
        Delay,              // 硬件到位等待或延时触发[cite: 4]

        [NodeFieldMeta("🚥", "#E879F9", "等待信号", "等待外部状态/触发信号")]
        WaitSignal,         // 阻塞等待 PLC/IO 触发信号[cite: 4]

        // ==========================================
        // 8. 📦 Group 子流程 (CompositeGroup - #DB2777 柔粉紫系)
        // ==========================================
        [NodeFieldMeta("📦", "#DB2777", "Group子流程", "Group 子流程 / 模块封装")]
        CompositeFlow,      // 类似 VisionMaster 的 Group 组，实现折叠与复用[cite: 4]

        [NodeFieldMeta("🛡️", "#BE185D", "异常捕获", "异常捕获与保护 (TryCatch)")]
        TryCatch,           // 算法报错防卡死保护[cite: 4]

        [NodeFieldMeta("🛑", "#9D174D", "终止流程", "流程强制终止 / 跳出")]
        TerminateFlow,      // 触发严重错误时强制终止当前运行[cite: 4]

        // ==========================================
        // 9. 🔌 设备通信与硬件控制 (DeviceIO - #2563EB 蓝灰/钢蓝系)
        // ==========================================
        [NodeFieldMeta("📟", "#2563EB", "PLC 读写", "PLC 寄存器读写 (Siemens/Modbus)")]
        PlcReadWrite,       // Siemens/Modbus 寄存器位/字读写[cite: 4]

        [NodeFieldMeta("🚨", "#1D4ED8", "数字 IO", "数字量开关控制 (Digital Output)")]
        DigitalOutput,      // 板卡/PLC DO 信号输出（剔除气缸等）[cite: 4]

        [NodeFieldMeta("⚙️", "#1E40AF", "运动轴控制", "运动轴 / 伺服定位控制")]
        AxisMove,           // 控制伺服/步进电机到位[cite: 4]

        [NodeFieldMeta("💡", "#3B82F6", "光源控制", "光源控制器 (串口/网口调光)")]
        LightControl,       // 串口/网口动态调节光源亮度[cite: 4]

        // ==========================================
        // 10. 💾 数据存储与系统交互 (DataStorage - #475569 炭晶系)
        // ==========================================
        [NodeFieldMeta("💾", "#475569", "图像存盘", "图像异步分类存盘 (OK/NG)")]
        SaveImage,          // OK/NG 图片异步分类保存[cite: 4]

        [NodeFieldMeta("📊", "#334155", "数据写盘", "测量数据追加存盘 (CSV/DB)")]
        SaveData,           // 测量结果追加写入本地文件或数据库[cite: 4]

        [NodeFieldMeta("🌐", "#64748B", "MES 上报", "MES 系统对接 (WebAPI/MQTT)")]
        MesReport           // WebAPI/HTTP/MQTT 对接 MES 上报数据[cite: 4]
    }
```