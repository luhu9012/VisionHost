# HalconWrapper.Wpf / README_hwindow — HWindow 显示宿主使用说明

> 校正：2026-09-07。与同目录 `README.md`（项目总览）互补，本文偏"怎么用显示宿主 / 常见坑"。

## 1. 快速上手

```csharp
// 1) XAML 放 HalconImageDisplayHost（或经 VM 绑定）
// 2) 订阅图像流：ImageDisplayVm.Subscribe(source) — Worker 侧推送图像事件
// 3) 上屏：HalconImageRenderService(上下文适配器).Render(...)
// 4) ROI/标记：走 VM context.Overlays Region + AddMarkerCross/Text（独立层）
```

- 断线/释放：图像源替换时按 HImage 借用语义释放（见下）；宿主 Disposed 时清订阅，防泄漏。

## 2. 常见坑速查

| 现象 | 原因与对策 |
|---|---|
| WPF 层画的东西看不见/点不中 | 覆盖层不可用 → 画 HALCON 窗口层 + 纯几何命中（HitTestRoiTag） |
| draw_* 卡死 | 阻塞算子不可用 → 自绘标记 |
| ROI 框错位/双份 | 非矩形提交 → 先 TryGetRoiAxisBounds 折算外接矩形；两通道 Overlays 保持同色 |
| 缩略图 4294967295 崩溃 | HImage 已释放还取像素 → try/catch + 借用语义管理 |
| 图像卡顿/掉帧 | 渲染/算子别挤 UI 线程；30ms 节流探针 |

## 3. 关联

- 项目总览：`README.md`；设计文档：根 `视觉交互翻译层设计方案_v1_2026-09-05.md`
- 视觉工程铁律：根 `ARCHITECTURE.md` §4.4-§4.5、§6
