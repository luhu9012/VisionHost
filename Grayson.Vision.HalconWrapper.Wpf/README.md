# Grayson.Vision.HalconWrapper.Wpf — HALCON 图像的 WPF 显示/交互封装

> 校正：2026-09-07。把 HSmartWindowControlWPF 显示、渲染、缩略图、模板交互桥集中在 WPF 侧，让上层应用无需直接面对 halcondotnet。
> ⚠️ 旧版本文件所述"Worker-Host + IPC + Observer UI（跨进程图像传输）"已随多进程方案移除，现行=单进程线程级宿主 + Dispatcher 上屏。

## 1. 职责定位

```text
Contracts (Imaging 契约, 无 UI) ← HalconWrapper(纯算法, 无 UI) ← 本工程(WPF 显示封装) ← WpfUI / FlowEdit
```

规模：10 个 .cs。

## 2. 目录/文件地图

| 位置 | 职责 |
|---|---|
| `Controls\HalconImageDisplayHost(.xaml)` | 显示宿主控件：HWindow 生命周期、图像/ROI/标记渲染入口 |
| `Controls\HalconDisplayContextAdapter.cs` | 显示上下文适配（ROI 集/选中态/通道 Overlays 同色管理） |
| `Imaging\HalconImageRenderService.cs` / `WpfImageRenderContext.cs` | 渲染服务与 WPF 渲染上下文（上屏/缩放/质量） |
| `Services\TemplateCreationBridge.cs` | 模板学习/编辑桥（源图回读改 ROI 靠 SourceImagePath 机制） |
| `ViewModels\ImageDisplayVm.cs` | 显示 VM（订阅图像流 → 渲染） |

## 3. 交互层铁律（高价值沉淀）

1. 视口上 **WPF 覆盖层绘制不可见 + 命中失效** → 可见视觉全部画 HALCON 窗口层；命中用纯几何 `HitTestRoiTag`；`draw_*` 阻塞算子不可用；
2. 标记用 `AddMarkerCross/AddMarkerText`（独立层）；非矩形 ROI 提交 → `TryGetRoiAxisBounds` 折算轴对齐外接矩形；
3. Ctrl 探针：在 Overlay_MouseMove 先调 ProbeCursorPixel（30ms 节流）；
4. HImage 借用语义：**释放比 NativeHandle 早；AddOrUpdateImageContext 命中 existing==new 绝不 Dispose**；缩略图 try/catch 防 4294967295；算子绝不跑 UI 线程；
5. 模板页可见 ROI 框 = VM context.Overlays Region（两通道同色）。

## 4. 关联

- 交互翻译层设计：根目录 `视觉交互翻译层设计方案_v1_2026-09-05.md`
- 显示/命中细节与视觉工程约定：根 `ARCHITECTURE.md` §4.5 / §6
- `README_hwindow.md`（本目录另一份视角：HWindow 使用说明）
