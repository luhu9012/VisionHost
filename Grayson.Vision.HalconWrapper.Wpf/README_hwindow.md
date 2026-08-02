# Grayson.Vision.HalconWrapper.Wpf

Halcon 图像的 WPF 显示封装层。本项目将 Halcon 的原生控件 `HSmartWindowControlWPF`、图像渲染、缩略图生成等 WPF 相关能力集中管理，使上层 WPF 应用（如 `Grayson.Vison.FlowEdit`）**无需直接引用 `halcondotnet`**。

## 1. 职责定位

```text
Grayson.Vision.Contracts (Imaging 契约，无 UI)
		↑
Grayson.Vision.HalconWrapper (纯 Halcon 算法，无 UI)
		↑
Grayson.Vision.HalconWrapper.Wpf (本层：WPF 显示封装)
		↑
Grayson.Vison.FlowEdit / Grayson.VisionApp.WpfUI
```

- 本层是唯一同时依赖 **WPF** 与 **halcondotnet** 的项目。
- 上层 WPF 项目只需要引用本层，即可获得 Halcon 图像显示能力。
- 契约接口定义在 `Grayson.Vision.Contracts.Imaging` 中，保持 Contracts 不依赖任何 UI 技术。

## 2. 目录结构

```text
Grayson.Vision.HalconWrapper.Wpf/
├── Properties/
│   └── AssemblyInfo.cs
├── Controls/
│   └── HalconImageDisplayHost.xaml       # 封装 HSmartWindowControlWPF 的显示宿主
├── Imaging/
│   ├── HalconImageRenderService.cs       # IImageRenderService 的 Halcon 实现
│   ├── HalconRenderImage.cs              # IRenderImage 的实现
│   └── WpfImageRenderContext.cs          # 含 BitmapSource 缩略图的 WPF 专用渲染上下文
└── Grayson.Vision.HalconWrapper.Wpf.csproj
```

## 3. 核心组件

### 3.1 IImageRenderService（契约位于 Contracts）

由 `HalconImageRenderService` 实现，提供：

| 方法 | 说明 |
|---|---|
| `WrapImage(object nativeImage)` | 将原生的 Halcon `HImage` 包装为 `IRenderImage` |
| `CreateThumbnail(IRenderImage image)` | 生成 `BitmapSource` 缩略图 |
| `GetPixelInfo(IRenderImage image, int x, int y)` | 获取指定像素的灰度/颜色信息 |
| `WrapOverlay(...)` | 将原生区域/XLD 包装为 `ImageOverlay` |
| `RenderToWindow(...)` | 在原生窗口（`HWindow`）中渲染图像与叠加图元 |
| `FitImageToWindow(...)` | 让窗口自适应完整图像 |

### 3.2 HalconImageDisplayHost

WPF 用户控件，内部承载 `HSmartWindowControlWPF`：

```xml
<halconHost:HalconImageDisplayHost DataContext="{Binding ImageDisplayVm}" />
```

- 初始化完成后持有 Halcon `HWindow` 句柄。
- 暴露 `IImageDisplayHost` 接口：
  - `Display(ImageRenderContext context)`：显示图像与叠加图元
  - `FitImage()`：自适应图像
  - `CursorPixelMoved` 事件：鼠标在图像上移动时触发，返回图像坐标

### 3.3 WpfImageRenderContext

继承自 Contracts 中的 `ImageRenderContext`，增加 WPF 缩略图属性：

```csharp
public class WpfImageRenderContext : ImageRenderContext
{
	public BitmapSource Thumbnail { get; set; }
}
```

上层 ViewModel（如 `ImageDisplayVm`）使用它来同时持有 Halcon 渲染图像与 WPF 缩略图。

## 4. 使用方式

### 4.1 XAML 中引用显示宿主

在需要使用 Halcon 显示的地方添加命名空间：

```xml
xmlns:halconHost="clr-namespace:Grayson.Vision.HalconWrapper.Wpf.Controls;assembly=Grayson.Vision.HalconWrapper.Wpf"
```

然后放置控件：

```xml
<halconHost:HalconImageDisplayHost DataContext="{Binding ImageDisplayVm}"/>
```

### 4.2 ViewModel 中创建渲染服务

```csharp
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;

public class ImageDisplayVm : ViewModelBase
{
	public ImageDisplayVm(ExecutionContext engineContext)
		: this(engineContext, new HalconImageRenderService())
	{
	}
}
```

### 4.3 订阅鼠标像素事件

```csharp
var host = new HalconImageDisplayHost();
host.CursorPixelMoved += (s, e) =>
{
	// e.X, e.Y 为图像坐标
	UpdateCursorPixelInfo(e.X, e.Y);
};
```

## 5. 依赖

- `Grayson.Vision.Contracts`
- `Grayson.Vision.HalconWrapper`
- `halcondotnet`
- WPF 程序集：`PresentationCore`、`PresentationFramework`、`WindowsBase`、`System.Xaml`、`System.Drawing`

## 6. 注意事项

- **Contracts 层不能依赖 WPF**。所有 `BitmapSource`、XAML、Dispatcher 相关类型必须放在本项目，或更上层的 WPF 项目中。
- 缩略图生成目前通过临时 BMP 文件 + `BitmapImage` 实现，后续可替换为内存流或 `WriteableBitmap` 以提升性能。
- 若需要在非 WPF 环境（如后台服务、控制台）使用 Halcon 算法，请直接引用 `Grayson.Vision.HalconWrapper`，不要引用本项目。
