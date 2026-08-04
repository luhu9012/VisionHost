# Grayson.Vision.HalconWrapper.Wpf

Halcon 图像的 **WPF 显示封装层**。本项目将 Halcon 的原生控件 `HSmartWindowControlWPF`、图像渲染、缩略图生成等 WPF 相关能力集中管理，使上层 WPF 应用（如 `Grayson.Vison.FlowEdit`、`Grayson.VisionApp.WpfUI`）**无需直接引用 `halcondotnet`**。

在新版 **Worker-Host + IPC + Observer UI** 架构下，本层还需要支持跨进程/跨线程图像传输：Worker 进程推送 `ImageRenderEventArgs`，UI 进程通过 `IImageRenderService` 将图像句柄或共享内存数据渲染到 `HalconImageDisplayHost`。

---

## 1. 职责定位

```text
Grayson.Vision.Contracts (Imaging 契约，无 UI)
	↑
Grayson.Vision.HalconWrapper (纯 Halcon 算法，无 UI)
	↑
Grayson.Vision.HalconWrapper.Wpf (本层：WPF 显示封装)
	↑
Grayson.Vison.FlowEdit / Grayson.VisionApp.WpfUI
		⇄ NamedPipe
Grayson.Vision.WorkerHost
```

- 本层是唯一同时依赖 **WPF** 与 **halcondotnet** 的项目。
- 上层 WPF 项目只需要引用本层，即可获得 Halcon 图像显示能力。
- 契约接口定义在 `Grayson.Vision.Contracts.Imaging` 中，保持 Contracts 不依赖任何 UI 技术。
- 在新架构下，渲染的数据源既可以是同进程 Halcon 对象，也可以是跨进程传递的缓存 ID / 共享内存名 / 图像字节数组。

---

## 2. 目录结构

```text
Grayson.Vision.HalconWrapper.Wpf/
├── Properties/
│   └── AssemblyInfo.cs
├── Controls/
│   ├── HalconImageDisplayHost.xaml       # 封装 HSmartWindowControlWPF 的显示宿主
│   └── HalconImageDisplayHost.xaml.cs    # 主机事件处理与 IImageDisplayHost 实现
├── Imaging/
│   ├── HalconImageRenderService.cs       # IImageRenderService 的 Halcon 实现
│   ├── HalconRenderImage.cs              # IRenderImage 的实现
│   └── WpfImageRenderContext.cs          # 含 BitmapSource 缩略图的 WPF 专用渲染上下文
├── ViewModels/
│   └── ImageDisplayVm.cs                 # 图像显示 ViewModel（封装 HalconImageDisplayHost 的可绑定状态）
└── Grayson.Vision.HalconWrapper.Wpf.csproj
```

---

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

**跨进程图像处理建议：**

- `ImageRenderEventArgs.RenderData` 可能是 `HImage`（同进程）、图像缓存 ID、共享内存名称或字节数组。
- 建议优先在 Worker 端将 HObject 序列化为图像缓存 ID 或共享内存名称，避免通过 NamedPipe 传输完整 Bitmap。
- UI 端根据 `ImagePathOrBufferId` 从图像缓存服务或共享内存获取数据，再调用 `WrapImage` 渲染。

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

---

## 4. 使用方式

### 4.1 XAML 中引用显示宿主

在需要使用 Halcon 显示的地方添加命名空间：

```xml
xmlns:halconHost="clr-namespace:Grayson.Vision.HalconWrapper.Wpf.Controls;assembly=Grayson.Vision.HalconWrapper.Wpf"
```

然后放置控件：

```xml
<halconImageDisplayHost:HalconImageDisplayHost DataContext="{Binding ImageDisplayVm}"/>
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

### 4.4 跨进程图像渲染示例

```csharp
// 在 IPC 客户端事件回调中
private void OnFrameRendered(object sender, ImageRenderEventArgs e)
{
	// 1. 从缓存/共享内存获取图像对象（避免直接反序列化大 Bitmap）
	object nativeImage = _imageCache.Get(e.ImagePathOrBufferId);

	// 2. 包装为渲染上下文
	var renderImage = _renderService.WrapImage(nativeImage);
	var context = _renderService.CreateRenderContext(renderImage, e.RenderData);

	// 3. 通过 Dispatcher 刷新控件
	Dispatcher.Invoke(() => _displayHost.Display(context));
}
```

---

## 5. 依赖

- `Grayson.Vision.Contracts`
- `Grayson.Vision.HalconWrapper`
- `halcondotnet`
- WPF 程序集：`PresentationCore`、`PresentationFramework`、`WindowsBase`、`System.Xaml`、`System.Drawing`

---

## 6. 设计约束与注意事项

1. **Contracts 层不能依赖 WPF**。所有 `BitmapSource`、XAML、Dispatcher 相关类型必须放在本项目，或更上层的 WPF 项目中。
2. **WorkerHost / Core 不能引用本项目**。`Grayson.Vision.Core` 与 `Grayson.Vision.WorkerHost` 只能输出 `ImageRenderEventArgs`，不能创建 WPF 控件或缩略图。
3. **缩略图性能**：缩略图生成目前可通过临时 BMP 文件 + `BitmapImage` 实现，后续可替换为内存流或 `WriteableBitmap` 以提升性能。
4. **图像传输建议**：跨进程场景优先使用缓存 ID/共享内存/指针交换，避免在 NamedPipe 中传输完整 HObject 或超大 Bitmap。
5. **若需要在非 WPF 环境（如后台服务、控制台）使用 Halcon 算法**，请直接引用 `Grayson.Vision.HalconWrapper`，不要引用本项目。

---

## 7. 维护提示

- 新增可视化能力（如 ROI 绘制、测量结果显示）时，优先扩展 `ImageOverlay` 与 `IImageRenderService` 接口，再分别实现 Halcon 版本与 WPF 版本。
- 调整 `WpfImageRenderContext` 时，必须保证 Contracts 层的 `ImageRenderContext` 仍保持无 UI 依赖。
- 在 IPC 模式下，UI 端渲染失败时应友好提示“图像缓冲区失效”，并尝试从 Worker 端重新请求一帧。
