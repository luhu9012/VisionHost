//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: HalconImageDisplayHost 的 ROI 交互引擎（partial 分文件）。
//        —— 七种绘制工具：平行矩形 / 旋转矩形 / 圆形 / 椭圆 / 逐点多边形 / 手绘任意区域 / 线段；
//        —— 绘制语义（对齐 HALCON/HDevelop）：
//             矩形·圆形·线段：左键按下拖出 → 松手后继续跟手微调 → 右键完成创建；
//             椭圆·旋转矩形：左键拖出主轴(长度+角度) → 松手后移动调整副轴 → 右键完成；
//             多边形(逐点)：左键逐点添加顶点 → 移动实时预览 → 右键闭合完成；
//             手绘任意区域：按住左键沿路径连续描线（跟手采样） → 右键自动闭合创建（仿 draw_region）；
//           Esc 取消当前草绘/选中/工具；
//        —— 编辑：🖱 选择工具下点选 ROI → 拖动内部整体移动；拖动高亮手柄精确变形；
//           右键菜单（删除 / 移到最上层 / 改色）；Delete 删除选中；双击空白自适应窗口。
//        —— 视觉铁律：已提交 ROI 轮廓 / 草绘 / 手柄 / 框选橡皮筋 / ROI 参数信息 全部由
//           HALCON 窗口层绘制（DrawRoiEditorLayerToWindow，随 RepaintScene 重放，任何场景可见）。
//           WPF 覆盖层（OverlayLayer）已降级为纯事件层：实证 HSmartWindowControlWPF 视口上方的
//           WPF 覆盖层绘制不参与合成（不可见），且其可视树命中同样失效 → 点选/拖动/手柄命中
//           全部改为纯几何算法（图像坐标判定），不依赖任何 WPF 元素（见 HitTestRoiTag / RoiBodyHitTest）。
//        事件：RoiCommitted / RoiEdited / RoiRemoved / RoisCleared。交互引擎含 halcondotnet 绘制调用。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Wpf.Controls
{
    public partial class HalconImageDisplayHost
    {
        // ===================== ROI 集合 / 选中态 =====================
        private readonly List<RoiShape> _roiShapes = new List<RoiShape>();
        private RoiShape _selectedShape;
        private int _roiColorIndex;

        /// <summary>当前已提交的 ROI 集合（编辑器内存态，只读快照视图）</summary>
        public IReadOnlyList<RoiShape> Rois => _roiShapes.AsReadOnly();

        /// <summary>当前选中（可编辑）的 ROI，无则为 null</summary>
        public RoiShape SelectedRoi => _selectedShape;

        private static readonly string[] RoiPalette =
        {
            "yellow", "cyan", "orange", "red", "green", "magenta", "blue", "white"
        };

        /// <summary>命中容差（屏幕像素）：本体 / 手柄共用；换算到图像单位参与几何判定</summary>
        private const double RoiHitSlopPx = 8.0;
        private const double HandleHitSlopPx = 12.0;

        /// <summary>Pointer 模式拖拽交互子状态</summary>
        private enum PtrDragMode { None, Pan, MoveShape, ResizeShape }

        private PtrDragMode _ptrDrag = PtrDragMode.None;
        private RoiShape _dragShape;
        private string _handleKey;
        private Point _panLastView;
        private double _anchorImgRow, _anchorImgCol;
        private bool _gestureMutated;
        private DateTime _lastPanUtc2 = DateTime.MinValue;
        private DateTime _lastLeftDownUtc = DateTime.MinValue;
        private Point _lastLeftDownView = new Point(-1e6, -1e6);

        // 草绘状态（绘制工具内）
        private RoiShape _draft;
        private bool _draftPhase2;
        private readonly List<Point> _polyVerts = new List<Point>(); // X=col, Y=row（图像坐标）
        private double _mouseImgRow, _mouseImgCol;

        /// <summary>手绘任意区域：是否正在"按住左键连续描线"（Freehand 工具内）</summary>
        private bool _freehandDrawing;

        /// <summary>涂抹画笔：是否正在"按住左键涂抹"（Brush 工具内；松开收笔提交一笔）</summary>
        private bool _brushDrawing;

        /// <summary>场景重放进行中：ROI 刷新回调不再次触发全场景重放（防递归）</summary>
        private bool _inRoiSceneRepaint;
        /// <summary>HALCON 窗口层 ROI 视觉重画节流（拖动高频只按帧刷）</summary>
        private DateTime _lastRoiLayerPaintUtc = DateTime.MinValue;

        /// <summary>元素命中标签（body=形状本体，其余=手柄键名）</summary>
        private sealed class RoiElementTag
        {
            public RoiShape Roi;
            public string Key = "body";
            public RoiElementTag(RoiShape roi, string key) { Roi = roi; Key = key; }
        }

        /// <summary>编辑手柄（图像坐标 + 键名）：几何命中与窗口层视觉共用同一数据源</summary>
        private sealed class RoiHandlePoint
        {
            public string Key;
            public double Row, Col;
            public RoiHandlePoint(string key, double row, double col)
            {
                Key = key;
                Row = row;
                Col = col;
            }
        }

        // ===================== 公共操作 =====================

        /// <summary>清空全部已提交 ROI（触发 RoiRemoved + RoisCleared）</summary>
        public void ClearRois()
        {
            RunOnUiSync(() => ClearAllRois(notifyEvents: true));
        }

        /// <summary>
        /// 场景切换静默清空（2026-09-09 模板编辑器换取景/新建向导）：
        /// 不触发 RoiRemoved/RoisCleared —— 语义现场（掩膜笔画/特征/数值）由 VM 自持并自行裁决重建，
        /// 事件会误触发页面"清空掩膜/清空特征"逻辑（MaskEditVisible 时 RoisCleared 会清掉整组掩膜笔画）。
        /// </summary>
        public void ClearRoisSilently()
        {
            RunOnUiSync(() => ClearAllRois(notifyEvents: false));
        }

        /// <summary>
        /// 静默替换整组 ROI（2026-09-09 模板编辑"载入回注"）：从模板资产读回基底形状时
        /// 重建宿主 ROI 集合 → 可点选/拖动/手柄变形编辑（RoiEdited 实时回写）。
        /// 不触发 RoiRemoved/RoisCleared：页面数值/掩膜由 VM 持有，事件会误清现场。
        /// RoiShape 为纯数据（无 native 句柄），替换无需释放旧对象。
        /// </summary>
        public void ReplaceRois(IEnumerable<RoiShape> shapes)
        {
            RunOnUiSync(() =>
            {
                _roiShapes.Clear();
                if (shapes != null)
                {
                    foreach (var s in shapes)
                    {
                        if (s != null) _roiShapes.Add(s);
                    }
                }
                _selectedShape = null;
                _draft = null;
                _draftPhase2 = false;
                _polyVerts.Clear();
                _freehandDrawing = false;
                RefreshRoiVisuals();
                AfterRoiSetChanged();
            });
        }

        /// <summary>移除单条 ROI（触发 RoiRemoved）。返回是否移除成功</summary>
        public bool RemoveRoi(RoiShape roi)
        {
            if (roi == null) return false;
            if (!_roiShapes.Remove(roi)) return false;
            if (ReferenceEquals(_selectedShape, roi)) _selectedShape = null;
            RefreshRoiVisuals();
            RoiRemoved?.Invoke(this, new RoiShapeEventArgs(roi));
            AfterRoiSetChanged();
            return true;
        }

        /// <summary>
        /// 显示帧更换时是否自动清空 ROI 集合（默认 true，保持全系统行为）。
        /// 模板编辑器置 false（2026-09-09）：编辑器一帧到底——掩膜预览/学习域预览只是把显示帧换成
        /// 同取景的合成拷贝，若每帧都清 ROI，用户涂完掩膜后 ROI 就点选/拖动不了了（"编辑时 ROI
        /// 无法选中"根因之一）。换取景（新建向导重置）由页面显式 ClearRois() 兜底。
        /// </summary>
        public bool ResetRoisOnFrameChange { get; set; } = true;

        private void ClearAllRois(bool notifyEvents = true)
        {
            if (_roiShapes.Count == 0) return;
            var all = new List<RoiShape>(_roiShapes);
            _roiShapes.Clear();
            _selectedShape = null;
            _draft = null;
            _draftPhase2 = false;
            _polyVerts.Clear();
            _freehandDrawing = false;
            RefreshRoiVisuals();
            if (notifyEvents)
            {
                // 用户显式清空（🧹 / ClearRois）：通知页面同步清 VM 现场；
                // 显示帧切换触发的内部清空(notifyEvents=false)静默——掩膜/特征/数值由 VM 自持，不受影响
                foreach (var r in all)
                {
                    RoiRemoved?.Invoke(this, new RoiShapeEventArgs(r));
                }
                RoisCleared?.Invoke(this, EventArgs.Empty);
            }
            AfterRoiSetChanged();
        }

        /// <summary>集合变化后：刷新工具条可用态，并依 ROI 数量重算覆盖层武装状态</summary>
        private void AfterRoiSetChanged()
        {
            RefreshToolbarAvailability();
            SetActiveTool(_activeTool);
        }

        /// <summary>当前显示图像更换/清空时由主文件调用：默认作废旧 ROI（模板编辑器可关）</summary>
        private void OnDisplayFrameChanged()
        {
            if (ResetRoisOnFrameChange)
            {
                ClearAllRois(notifyEvents: false);
            }
        }

        // ===================== 事件入口（主文件 Overlay 事件壳转调） =====================

        /// <summary>ROI 绘制链路追踪：写入 VS 输出 + 运行目录 Data\roi_draw_trace.log（复测诊断用，异常隔离）</summary>
        private static void TraceRoi(string msg)
        {
            System.Diagnostics.Debug.WriteLine("[RoiTrace] " + msg);
            try
            {
                string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "roi_draw_trace.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {msg}\r\n");
            }
            catch { /* 追踪写失败不影响业务 */ }
        }

        private bool InteractiveMouseLeftDown(MouseButtonEventArgs e)
        {
            if (!OverlayShouldBeArmed() || _hWindow == null) return false;
            if (!HasDisplayImage())
            {
                TraceRoi($"绘制被拒：按下时无可用图像 (tool={_activeTool}, hw={_hWindow != null})");
                SetActiveTool(ViewTool.Pointer);
                return true;
            }
            Point vp = ClampToView(e.GetPosition(SmartWindow));
            TraceRoi($"Overlay 左键按下 view=({vp.X:F0},{vp.Y:F0}) tool={_activeTool} armed={OverlayLayer.IsHitTestVisible}");

            // 把键盘焦点收进视图区：Esc/Delete 等快捷键在宿主任意子控件获得焦点时都可靠生效
            TryFocusOverlay();

            // 双击检测（Pointer 模式）= 适应窗口
            if (_activeTool == ViewTool.Pointer || _activeTool == ViewTool.Hand)
            {
                DateTime nowUtc = DateTime.UtcNow;
                bool dbl = (nowUtc - _lastLeftDownUtc).TotalMilliseconds < 500
                           && Math.Abs(vp.X - _lastLeftDownView.X) < 5
                           && Math.Abs(vp.Y - _lastLeftDownView.Y) < 5;
                _lastLeftDownUtc = nowUtc;
                _lastLeftDownView = vp;
                if (dbl && _activeTool == ViewTool.Pointer)
                {
                    FitImage();
                    return true;
                }
            }
            else
            {
                _lastLeftDownUtc = DateTime.MinValue;
            }

            switch (_activeTool)
            {
                case ViewTool.ZoomRect:
                    StartZoomBox(vp);
                    return true;

                case ViewTool.Rect1:
                case ViewTool.Circle:
                case ViewTool.Ellipse:
                case ViewTool.Rect2:
                case ViewTool.Line:
                    BeginSketch(vp);
                    return true;

                case ViewTool.Polygon:
                    AddPolygonVertex(vp);
                    return true;

                case ViewTool.Freehand:
                    BeginFreehandStroke(vp);
                    return true;

                case ViewTool.Brush:
                    BeginBrushStroke(vp);
                    return true;

                default: // Pointer / Hand
                    return BeginPointerGesture(vp);
            }
        }

        private bool InteractiveMouseMove(MouseEventArgs e)
        {
            if (!OverlayShouldBeArmed() || _hWindow == null) return false;
            Point vp = ClampToView(e.GetPosition(SmartWindow));

            // 持续跟踪鼠标图像坐标（草绘预览 / 多边形橡皮筋需要）
            if (TryGetImagePointAt(vp, out double ir, out double ic))
            {
                _mouseImgRow = ir;
                _mouseImgCol = ic;
            }

            switch (_ptrDrag)
            {
                case PtrDragMode.Pan:
                    DoPan(vp);
                    return true;
                case PtrDragMode.MoveShape:
                    DoMoveShape(vp);
                    return true;
                case PtrDragMode.ResizeShape:
                    DoResizeShape(vp);
                    return true;
            }

            if (_activeTool == ViewTool.ZoomRect)
            {
                if (_dragActive)
                {
                    _dragEnd = vp;
                    RefreshRoiVisuals(); // 橡皮筋视觉在 HALCON 窗口层（DrawRoiEditorLayerToWindow）绘制
                }
                return true;
            }

            if (IsRoiDrawTool())
            {
                bool polyLike = _activeTool == ViewTool.Polygon || _activeTool == ViewTool.Freehand
                                || _activeTool == ViewTool.Brush;
                if (polyLike)
                {
                    bool sampled = false;
                    if (_activeTool == ViewTool.Freehand && _freehandDrawing)
                    {
                        sampled = AppendFreehandSample();
                    }
                    else if (_activeTool == ViewTool.Brush && _brushDrawing)
                    {
                        sampled = AppendBrushSample();
                    }
                    // 多边形：有顶点才刷新（橡皮筋预览）；手绘：仅在确实采到新点或逐点模式时刷新；
                    // 涂抹：采到新点即刷新，且按住期间每次移动都刷新（24ms 节流内）→ 圆盘带头实时跟手
                    if (sampled || _brushDrawing || (_activeTool == ViewTool.Polygon && _polyVerts.Count > 0))
                    {
                        RefreshRoiVisuals();
                        if (_activeTool == ViewTool.Brush && sampled)
                        {
                            RaiseBrushSketch(true); // 轨迹新增采样点：页面实时重算学习域版图
                        }
                    }
                }
                else if (_draft != null)
                {
                    UpdateSketchFollow();
                    RefreshRoiVisuals();
                }
                return true;
            }

            if (_activeTool == ViewTool.Pointer)
            {
                UpdateHoverCursor(vp);
            }
            return true;
        }

        private bool InteractiveMouseLeftUp(MouseButtonEventArgs e)
        {
            if (!OverlayShouldBeArmed() || _hWindow == null) return false;
            Point vp = ClampToView(e.GetPosition(SmartWindow));
            var drag = _ptrDrag;
            var finishedShape = _dragShape;
            _ptrDrag = PtrDragMode.None;
            _dragShape = null;
            _handleKey = null;
            ReleaseOverlayCapture();

            // 框选放大：松手完成
            if (_activeTool == ViewTool.ZoomRect)
            {
                bool active = _dragActive;
                _dragActive = false;
                if (active && Math.Abs(vp.X - _dragStart.X) >= 2 && Math.Abs(vp.Y - _dragStart.Y) >= 2)
                {
                    ZoomToRubber(_dragStart, vp);
                }
                return true;
            }

            // 绘制工具：松开左键 → 草绘进入"跟手待确认"（移动微调，右键完成）
            if (IsRoiDrawTool())
            {
                if (_activeTool == ViewTool.Polygon)
                {
                    RefreshRoiVisuals();
                    return true;
                }
                if (_activeTool == ViewTool.Freehand)
                {
                    // 手绘：松开暂停描线（已描轨迹保留，再按住可续描；右键闭合完成）
                    if (_freehandDrawing)
                    {
                        _freehandDrawing = false;
                        RefreshRoiVisuals();
                        SetSketchHint();
                    }
                    return true;
                }
                if (_activeTool == ViewTool.Brush)
                {
                    // 涂抹画笔：松开 = 收笔提交一笔（圆盘带区域；保留工具可连续涂抹；单点=半径圆）。
                    // 与 Freehand 差异：Freehand 松开暂停+右键闭合；涂抹"按下即刷、松开即收"，
                    // 每一笔独立提交（掩膜收笔语义=一笔一笔 ∪/∖）。
                    if (_brushDrawing)
                    {
                        _brushDrawing = false;
                        CommitBrushStroke();
                        RaiseBrushSketch(false); // 收笔：轨迹已清空并提交（页面清临时，落定版图）
                    }
                    return true;
                }
                if (_draft != null)
                {
                    if (_draft.Kind == RoiShapeKind.Ellipse || _draft.Kind == RoiShapeKind.Rectangle2)
                    {
                        _draftPhase2 = true;
                    }
                    RefreshRoiVisuals();
                    SetSketchHint();
                }
                return true;
            }

            // Pointer：移动/变形结束 → 发布 RoiEdited
            if (drag == PtrDragMode.MoveShape || drag == PtrDragMode.ResizeShape)
            {
                if (_gestureMutated && finishedShape != null)
                {
                    RoiEdited?.Invoke(this, new RoiShapeEventArgs(finishedShape));
                }
                _gestureMutated = false;
                RefreshRoiVisuals();
                return true;
            }

            // Pointer 单击：选择 / 取消选择
            if (_activeTool == ViewTool.Pointer && drag == PtrDragMode.None)
            {
                var tag = HitTestRoiTag(vp);
                if (tag != null && tag.Roi != null && tag.Key == "body")
                {
                    SelectRoi(tag.Roi, true);
                }
                else
                {
                    SelectRoi(null, true); // 点击空白取消选中：立即整帧去掉高亮/手柄/信息条
                }
            }
            return true;
        }

        private bool InteractiveMouseRightDown(MouseButtonEventArgs e)
        {
            if (!OverlayShouldBeArmed() || _hWindow == null) return false;
            Point vp = ClampToView(e.GetPosition(SmartWindow));

            // 绘制工具：右键 = 完成创建；无可完成草绘则退出工具
            if (IsRoiDrawTool() || _activeTool == ViewTool.ZoomRect)
            {
                if (_activeTool != ViewTool.ZoomRect)
                {
                    if (_activeTool == ViewTool.Brush)
                    {
                        // 涂抹画笔：右键 = 直接退出工具回 Pointer（放弃未收笔的涂抹轨迹，已收笔笔画不受影响）
                        _brushDrawing = false;
                        _polyVerts.Clear();
                        SetActiveTool(ViewTool.Pointer);
                        RaiseBrushSketch(false); // 放弃未收笔轨迹：页面撤销临时候选
                    }
                    else if (!CommitSketch())
                    {
                        SetActiveTool(ViewTool.Pointer);
                    }
                }
                else
                {
                    SetActiveTool(ViewTool.Pointer);
                }
                e.Handled = true;
                return true;
            }

            if (_activeTool == ViewTool.Pointer)
            {
                var tag = HitTestRoiTag(vp);
                if (tag != null && tag.Roi != null && tag.Key == "body")
                {
                    SelectRoi(tag.Roi, true);
                    ShowRoiContextMenu(tag.Roi);
                }
                e.Handled = true;
            }
            return true;
        }

        private bool InteractiveMouseWheel(MouseWheelEventArgs e)
        {
            if (!OverlayShouldBeArmed() || _hWindow == null) return false;
            if (_ptrDrag != PtrDragMode.None || _dragActive)
            {
                e.Handled = true;
                return true;
            }
            if (_activeTool == ViewTool.Polygon && _polyVerts.Count > 0) return true; // 定顶点时不缩放防误触
            double factor = e.Delta > 0 ? 1.0 / WheelZoomStep : WheelZoomStep;
            ZoomViewByFactorAt(ClampToView(e.GetPosition(SmartWindow)), factor);
            e.Handled = true;
            return true;
        }

        /// <summary>Esc：取消草绘 → 取消选中 → 退出绘制工具</summary>
        private bool InteractiveCancel()
        {
            if (_draft != null || _polyVerts.Count > 0)
            {
                bool wasBrush = _activeTool == ViewTool.Brush;
                _draft = null;
                _draftPhase2 = false;
                _polyVerts.Clear();
                _freehandDrawing = false;
                _brushDrawing = false;
                _ptrDrag = PtrDragMode.None;
                ReleaseOverlayCapture();
                RefreshRoiVisuals();
                if (wasBrush) RaiseBrushSketch(false); // Esc 放弃未收笔涂抹轨迹
                if (IsRoiDrawTool()) SetSketchHint();
                return true;
            }
            if (_selectedShape != null)
            {
                SelectRoi(null, false);
                return true;
            }
            if (IsRoiDrawTool() || _activeTool == ViewTool.ZoomRect)
            {
                SetActiveTool(ViewTool.Pointer);
                return true;
            }
            return false;
        }

        /// <summary>Delete：删除当前选中 ROI</summary>
        private bool InteractiveDelete()
        {
            if (_selectedShape == null) return false;
            RemoveRoi(_selectedShape);
            return true;
        }

        // ===================== 覆盖层武装判定 =====================

        private bool IsRoiDrawTool()
        {
            switch (_activeTool)
            {
                case ViewTool.Rect1:
                case ViewTool.Circle:
                case ViewTool.Ellipse:
                case ViewTool.Rect2:
                case ViewTool.Polygon:
                case ViewTool.Freehand:
                case ViewTool.Brush:
                case ViewTool.Line:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>覆盖层是否需要接管鼠标（绘制工具 || Pointer 且存在 ROI）</summary>
        private bool OverlayShouldBeArmed()
        {
            if (_activeTool == ViewTool.ZoomRect || IsRoiDrawTool()) return true;
            return _activeTool == ViewTool.Pointer && _roiShapes.Count > 0;
        }

        // ===================== 视口换算 =====================

        private bool TryGetViewScale(out double sx, out double sy, out double c1, out double r1)
        {
            sx = sy = c1 = r1 = 0;
            if (_hWindow == null || SmartWindow == null) return false;
            double w = SmartWindow.ActualWidth, h = SmartWindow.ActualHeight;
            if (w <= 0 || h <= 0) return false;
            try
            {
                _hWindow.GetPart(out HTuple pr1, out HTuple pc1, out HTuple pr2, out HTuple pc2);
                double pw = pc2.D - pc1.D, ph = pr2.D - pr1.D;
                if (pw <= 0 || ph <= 0) return false;
                sx = w / pw; sy = h / ph;
                c1 = pc1.D; r1 = pr1.D;
                return true;
            }
            catch { return false; }
        }

        private Point ImgToView(double row, double col, double sx, double sy, double c1, double r1)
            => new Point((col - c1) * sx, (row - r1) * sy);

        private void ClampCursorImage(ref double row, ref double col)
        {
            if (TryGetImageSize(out int iw, out int ih))
            {
                row = ClampValue(row, 0, ih - 1);
                col = ClampValue(col, 0, iw - 1);
            }
        }

        // ===================== 草绘（六种工具） =====================

        private void BeginSketch(Point vp)
        {
            if (!TryGetImagePointAt(vp, out double row, out double col)) return;
            ClampCursorImage(ref row, ref col);
            TryGetImageSize(out int iw, out int ih);
            TraceRoi($"绘制开始 {ToSketchKind(_activeTool)} 按下@(row {row:F1}, col {col:F1}) 图 {iw}x{ih} tool={_activeTool}");

            // ★ 种子化鼠标图像坐标缓存：避免"鼠标从未在图上移动过就按下"时，
            //   用陈旧/零值缓存驱动草绘首帧导致形状错位或退化到不可见
            _mouseImgRow = row;
            _mouseImgCol = col;

            _draft = new RoiShape(ToSketchKind(_activeTool));
            _anchorImgRow = row;
            _anchorImgCol = col;
            _draftPhase2 = false;

            switch (_draft.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    _draft.Row = row; _draft.Col = col;
                    _draft.Row2 = row; _draft.Col2 = col;
                    break;
                case RoiShapeKind.Line:
                    _draft.Row = row; _draft.Col = col;
                    _draft.Row2 = row; _draft.Col2 = col;
                    break;
                default: // 中心式：Circle/Ellipse/Rect2
                    _draft.Row = row; _draft.Col = col;
                    break;
            }
            CaptureOverlay();
            SetSketchHint();
            UpdateSketchFollow();
            RefreshRoiVisuals();
        }

        private static RoiShapeKind ToSketchKind(ViewTool tool)
        {
            switch (tool)
            {
                case ViewTool.Rect1: return RoiShapeKind.Rectangle1;
                case ViewTool.Circle: return RoiShapeKind.Circle;
                case ViewTool.Ellipse: return RoiShapeKind.Ellipse;
                case ViewTool.Rect2: return RoiShapeKind.Rectangle2;
                case ViewTool.Line: return RoiShapeKind.Line;
                default: return RoiShapeKind.Polygon;
            }
        }

        /// <summary>草绘跟手：由当前鼠标图像坐标刷新几何</summary>
        private void UpdateSketchFollow()
        {
            if (_draft == null) return;
            switch (_draft.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    _draft.Row = Math.Min(_anchorImgRow, _mouseImgRow);
                    _draft.Row2 = Math.Max(_anchorImgRow, _mouseImgRow);
                    _draft.Col = Math.Min(_anchorImgCol, _mouseImgCol);
                    _draft.Col2 = Math.Max(_anchorImgCol, _mouseImgCol);
                    break;

                case RoiShapeKind.Line:
                    _draft.Row2 = _mouseImgRow;
                    _draft.Col2 = _mouseImgCol;
                    break;

                case RoiShapeKind.Circle:
                    _draft.Radius1 = Math.Max(Dist(_draft.Row, _draft.Col, _mouseImgRow, _mouseImgCol), 0.5);
                    break;

                case RoiShapeKind.Ellipse:
                    if (!_draftPhase2)
                    {
                        double dc = _mouseImgCol - _draft.Col, dr = _mouseImgRow - _draft.Row;
                        double d = Math.Sqrt(dc * dc + dr * dr);
                        _draft.Phi = Math.Atan2(dr, dc);
                        _draft.Radius1 = Math.Max(d, 0.5);
                        _draft.Radius2 = Math.Max(d * 0.25, 0.5);
                    }
                    else
                    {
                        _draft.Radius2 = Math.Max(PerpDistance(_draft, _mouseImgRow, _mouseImgCol), 0.5);
                    }
                    break;

                case RoiShapeKind.Rectangle2:
                    if (!_draftPhase2)
                    {
                        double dc = _mouseImgCol - _draft.Col, dr = _mouseImgRow - _draft.Row;
                        double d = Math.Sqrt(dc * dc + dr * dr);
                        _draft.Phi = Math.Atan2(dr, dc);
                        _draft.Length1 = Math.Max(d, 0.5);
                        _draft.Length2 = Math.Max(d * 0.25, 0.5);
                    }
                    else
                    {
                        _draft.Length2 = Math.Max(PerpDistance(_draft, _mouseImgRow, _mouseImgCol), 0.5);
                    }
                    break;
            }
        }

        private static double Dist(double r1, double c1, double r2, double c2)
        {
            double dr = r2 - r1, dc = c2 - c1;
            return Math.Sqrt(dr * dr + dc * dc);
        }

        /// <summary>光标相对中心在"垂直主轴"方向的投影距离（椭圆/旋转矩形副轴）</summary>
        private double PerpDistance(RoiShape s, double row, double col)
        {
            double dr = row - s.Row, dc = col - s.Col;
            double vx = -Math.Sin(s.Phi), vy = Math.Cos(s.Phi);
            return Math.Abs(dc * vx + dr * vy);
        }

        private void AddPolygonVertex(Point vp)
        {
            if (!TryGetImagePointAt(vp, out double row, out double col)) return;
            ClampCursorImage(ref row, ref col);
            _polyVerts.Add(new Point(col, row));
            _mouseImgRow = row;
            _mouseImgCol = col;
            SetSketchHint();
            RefreshRoiVisuals();
        }

        // ===================== 手绘任意区域（仿 HALCON draw_region） =====================
        // 按住左键连续描线（Move 按间距采样加轨迹点），右键自动闭合创建。
        // 与逐点多边形的差异：多边形单击加顶点、追求直线边精确控制；手绘为连续自由曲线，
        // 轨迹点更密、更贴合任意形状轮廓（"控制得更精细"指沿鼠标路径逐点逼近任意轮廓）。

        /// <summary>手绘轨迹采样点数量上限（防失控海量点导致渲染卡顿）</summary>
        private const int FreehandMaxPoints = 6000;

        /// <summary>手绘：左键按下开始一段描线（每次按下从头开始新轨迹）</summary>
        private void BeginFreehandStroke(Point vp)
        {
            if (!TryGetImagePointAt(vp, out double row, out double col)) return;
            ClampCursorImage(ref row, ref col);
            _polyVerts.Clear();
            _polyVerts.Add(new Point(col, row));
            _mouseImgRow = row;
            _mouseImgCol = col;
            _freehandDrawing = true;
            TraceRoi($"手绘描线开始 @(row {row:F1}, col {col:F1})");
            SetSketchHint();
            RefreshRoiVisuals();
            CaptureOverlay();
        }

        /// <summary>手绘：鼠标移动时按屏幕间距采样追加轨迹点。返回是否新增了点</summary>
        private bool AppendFreehandSample()
        {
            if (!_freehandDrawing || _polyVerts.Count >= FreehandMaxPoints) return false;
            // 采样步长：屏幕约 2px → 图像距离（放大/缩小视图自适应，避免点海量或太疏）
            double step = 2.0;
            TryGetViewScale(out double sx, out double sy, out _, out _);
            double s = Math.Max(sx, sy);
            if (s > 1e-6) step = 2.0 / s;
            step = Math.Max(step, 0.4);

            double row = _mouseImgRow, col = _mouseImgCol;
            var last = _polyVerts[_polyVerts.Count - 1];
            double d = Math.Sqrt((col - last.X) * (col - last.X) + (row - last.Y) * (row - last.Y));
            if (d < step) return false;
            ClampCursorImage(ref row, ref col);
            _polyVerts.Add(new Point(col, row));
            return true;
        }

        // ===================== 涂抹画笔（掩膜画笔，P1 2026-09-09） =====================
        // 语义：按住=沿轨迹涂抹（圆盘带），松开=收笔提交一笔 RoiShapeKind.Brush。
        // 与 Freehand 差异：Freehand 松开暂停、右键闭合"封闭区域"；涂抹"按下即刷、松开即收"，
        // 一笔=沿轨迹以 BrushRadiusPx 为半径膨胀的圆盘带（区域化膨胀在引擎侧完成——
        // TemplateMaskRegionBuilder brush 分支，UI 只负责采轨迹与半径，所见即引擎所得）。
        // 收笔即发 RoiCommitted：掩膜编辑态由宿主收为掩膜笔画并自移除（与矩形/圆同链路）。
        // 2026-09-09 版图化：按住期间每个新采样点/状态转折发 BrushSketchChanged（携带轨迹快照），
        // 页面把未收笔轨迹实时并入学习域（∪ 版图实时扩 / ∖ 实时抠洞），灰化预览帧与整域轮廓跟手。

        /// <summary>涂抹草绘状态广播（新采样点/按下/收笔/取消）。Down=true=按住涂抹中(轨迹增长)；
        /// false=一笔收笔或放弃(Points 为空)。页面据此实时重建"学习域版图"。</summary>
        public event EventHandler<BrushSketchEventArgs> BrushSketchChanged;

        private void RaiseBrushSketch(bool down)
        {
            if (BrushSketchChanged == null) return;
            Point[] pts = down ? _polyVerts.ToArray() : Array.Empty<Point>();
            BrushSketchChanged?.Invoke(this, new BrushSketchEventArgs(pts, BrushRadiusPx, down));
        }

        /// <summary>涂抹：左键按下开始一笔（轨迹从按下点起）</summary>
        private void BeginBrushStroke(Point vp)
        {
            if (!TryGetImagePointAt(vp, out double row, out double col)) return;
            ClampCursorImage(ref row, ref col);
            _polyVerts.Clear();
            _polyVerts.Add(new Point(col, row));
            _mouseImgRow = row;
            _mouseImgCol = col;
            _brushDrawing = true;
            TraceRoi($"涂抹开始 @(row {row:F1}, col {col:F1}) 半径={BrushRadiusPx:F0}px");
            SetSketchHint();
            RefreshRoiVisuals();
            CaptureOverlay();
            RaiseBrushSketch(true); // 按下点即一笔起点：页面立即把版图并/抠到此处
        }

        /// <summary>涂抹：鼠标移动时按"半径比例步长"采样追加轨迹点（圆盘重叠成带、点数可控）。</summary>
        private bool AppendBrushSample()
        {
            if (!_brushDrawing || _polyVerts.Count >= FreehandMaxPoints) return false;
            // 步长 = 半径的 40%（≥0.5px）：相邻圆盘必有重叠 → 膨胀后连续成带
            double step = Math.Max(0.5, BrushRadiusPx * 0.4);
            double row = _mouseImgRow, col = _mouseImgCol;
            var last = _polyVerts[_polyVerts.Count - 1];
            double d = Math.Sqrt((col - last.X) * (col - last.X) + (row - last.Y) * (row - last.Y));
            if (d < step) return false;
            ClampCursorImage(ref row, ref col);
            _polyVerts.Add(new Point(col, row));
            return true;
        }

        /// <summary>
        /// 涂抹收笔：把轨迹提交为一笔 Brush ROI（Polygon 槽=轨迹点 + BrushRadius=半径）。
        /// 单点（点击未拖动）也提交：引擎侧退化为半径圆区域。提交后清轨迹、工具保留可连刷。
        /// </summary>
        private bool CommitBrushStroke()
        {
            if (_polyVerts.Count == 0) return false;
            var verts = _polyVerts.ToArray();
            _polyVerts.Clear();
            _brushDrawing = false;
            var shape = new RoiShape(RoiShapeKind.Brush)
            {
                Polygon = verts,
                BrushRadius = BrushRadiusPx
            };
            bool ok = FinalizeRoi(shape);
            TraceRoi(ok
                ? $"涂抹收笔成功 轨迹点={verts.Length} 半径={BrushRadiusPx:F0}px"
                : "涂抹收笔失败（FinalizeRoi 拒绝）");
            return ok;
        }

        /// <summary>右键完成创建（多边形/手绘区域闭合，其余形状定稿）</summary>
        private bool CommitSketch()
        {
            if (_activeTool == ViewTool.Polygon || _activeTool == ViewTool.Freehand)
            {
                if (_polyVerts.Count < 3)
                {
                    _polyVerts.Clear();
                    _freehandDrawing = false;
                    RefreshRoiVisuals();
                    return false;
                }
                var shape = new RoiShape(RoiShapeKind.Polygon)
                {
                    Polygon = _polyVerts.ToArray()
                };
                _polyVerts.Clear();
                _freehandDrawing = false;
                return FinalizeRoi(shape);
            }

            if (_draft == null) return false;
            if (!IsDraftUsable(_draft))
            {
                _draft = null;
                _draftPhase2 = false;
                RefreshRoiVisuals();
                return false;
            }
            var final = _draft;
            _draft = null;
            _draftPhase2 = false;
            return FinalizeRoi(final);
        }

        private bool IsDraftUsable(RoiShape s)
        {
            switch (s.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    return (s.Row2 - s.Row) >= 1 && (s.Col2 - s.Col) >= 1;
                case RoiShapeKind.Circle:
                    return s.Radius1 >= 1;
                case RoiShapeKind.Ellipse:
                    return s.Radius1 >= 1 && s.Radius2 >= 1;
                case RoiShapeKind.Rectangle2:
                    return s.Length1 >= 1 && s.Length2 >= 1;
                case RoiShapeKind.Line:
                    return Dist(s.Row, s.Col, s.Row2, s.Col2) >= 1;
                default:
                    return false;
            }
        }

        /// <summary>定稿：落颜色/图像尺寸、入集合、发事件；保持在当前绘制工具以便连续绘制</summary>
        private bool FinalizeRoi(RoiShape shape)
        {
            if (shape == null) return false;
            shape.ColorName = RoiPalette[_roiColorIndex % RoiPalette.Length];
            _roiColorIndex++;
            if (TryGetImageSize(out int iw, out int ih))
            {
                shape.ImageWidth = iw;
                shape.ImageHeight = ih;
            }
            _roiShapes.Add(shape);
            _selectedShape = null;
            RefreshRoiVisuals();
            RoiCommitted?.Invoke(this, new RoiShapeEventArgs(shape));
            AfterRoiSetChanged();
            SetSketchHint();
            TraceRoi($"ROI 提交成功 kind={shape.Kind} 数量={_roiShapes.Count} persist={PersistRoiInScene}");
            return true;
        }

        // ===================== Pointer 选择 / 编辑 =====================

        private bool BeginPointerGesture(Point vp)
        {
            if (_activeTool != ViewTool.Pointer || _roiShapes.Count == 0) return false;

            var tag = HitTestRoiTag(vp);
            if (tag != null && tag.Roi != null)
            {
                if (tag.Key != "body") // 手柄 → 变形
                {
                    _dragShape = tag.Roi;
                    _handleKey = tag.Key;
                    _ptrDrag = PtrDragMode.ResizeShape;
                    _gestureMutated = false;
                    CaptureOverlay();
                    return true;
                }
                // 形状本体 → 选中 + 整体移动
                SelectRoi(tag.Roi, true);
                _dragShape = tag.Roi;
                _ptrDrag = PtrDragMode.MoveShape;
                _gestureMutated = false;
                if (TryGetImagePointAt(vp, out double ir, out double ic))
                {
                    _anchorImgRow = ir;
                    _anchorImgCol = ic;
                }
                CaptureOverlay();
                return true;
            }

            // 空白：取消选中并平移
            SelectRoi(null, false);
            if (!IsPanEnabled) return true;
            _ptrDrag = PtrDragMode.Pan;
            _panLastView = vp;
            CaptureOverlay();
            return true;
        }

        private void DoMoveShape(Point vp)
        {
            if (_dragShape == null) return;
            if (!TryGetImagePointAt(vp, out double row, out double col)) return;
            double dr = row - _anchorImgRow;
            double dc = col - _anchorImgCol;
            _anchorImgRow = row;
            _anchorImgCol = col;
            if (Math.Abs(dr) < 1e-6 && Math.Abs(dc) < 1e-6) return;
            _gestureMutated = true;
            _dragShape.Translate(dr, dc);
            FitShapeIntoImage(_dragShape);
            RefreshRoiVisuals();
        }

        private void DoResizeShape(Point vp)
        {
            if (_dragShape == null || _handleKey == null) return;
            if (!TryGetImagePointAt(vp, out double row, out double col)) return;
            var s = _dragShape;
            _gestureMutated = true;
            double maxR = s.ImageHeight > 0 ? s.ImageHeight - 1 : double.MaxValue;
            double maxC = s.ImageWidth > 0 ? s.ImageWidth - 1 : double.MaxValue;
            double rr = ClampValue(row, 0, maxR);
            double cc = ClampValue(col, 0, maxC);

            switch (s.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    ResizeRect1Handle(s, rr, cc);
                    break;

                case RoiShapeKind.Circle:
                    if (_handleKey == "R")
                    {
                        s.Radius1 = Math.Max(Dist(s.Row, s.Col, rr, cc), 1);
                    }
                    else
                    {
                        s.Row = rr; s.Col = cc;
                    }
                    break;

                case RoiShapeKind.Ellipse:
                    if (_handleKey == "R1")
                    {
                        double dc = cc - s.Col, dr = rr - s.Row;
                        s.Phi = Math.Atan2(dr, dc);
                        s.Radius1 = Math.Max(Math.Sqrt(dc * dc + dr * dr), 1);
                    }
                    else if (_handleKey == "R2")
                    {
                        s.Radius2 = Math.Max(PerpDistance(s, rr, cc), 1);
                    }
                    else
                    {
                        s.Row = rr; s.Col = cc;
                    }
                    break;

                case RoiShapeKind.Rectangle2:
                    if (_handleKey == "L1")
                    {
                        double dc = cc - s.Col, dr = rr - s.Row;
                        s.Phi = Math.Atan2(dr, dc);
                        s.Length1 = Math.Max(Math.Sqrt(dc * dc + dr * dr), 1);
                    }
                    else if (_handleKey == "L2")
                    {
                        s.Length2 = Math.Max(PerpDistance(s, rr, cc), 1);
                    }
                    else
                    {
                        s.Row = rr; s.Col = cc;
                    }
                    break;

                case RoiShapeKind.Line:
                    if (_handleKey == "A")
                    {
                        s.Row = rr; s.Col = cc;
                    }
                    else
                    {
                        s.Row2 = rr; s.Col2 = cc;
                    }
                    break;

                case RoiShapeKind.Polygon:
                    if (_handleKey.StartsWith("V") && int.TryParse(_handleKey.Substring(1), out int idx)
                        && idx >= 0 && idx < s.Polygon.Length)
                    {
                        s.Polygon[idx] = new Point(cc, rr);
                    }
                    break;
            }
            RefreshRoiVisuals();
        }

        /// <summary>矩形四角手柄：分别调整对应角（对角保持不动）</summary>
        private void ResizeRect1Handle(RoiShape s, double row, double col)
        {
            switch (_handleKey)
            {
                case "TL":
                    s.Row = Math.Min(row, s.Row2 - 1);
                    s.Col = Math.Min(col, s.Col2 - 1);
                    break;
                case "TR":
                    s.Row = Math.Min(row, s.Row2 - 1);
                    s.Col2 = Math.Max(col, s.Col + 1);
                    break;
                case "BL":
                    s.Row2 = Math.Max(row, s.Row + 1);
                    s.Col = Math.Min(col, s.Col2 - 1);
                    break;
                case "BR":
                    s.Row2 = Math.Max(row, s.Row + 1);
                    s.Col2 = Math.Max(col, s.Col + 1);
                    break;
            }
        }

        /// <summary>整体平移后的形状拉回图像内（比图像大的形状只夹中心）</summary>
        private void FitShapeIntoImage(RoiShape s)
        {
            if (!TryGetImageSize(out int iw, out int ih)) return;
            double maxR = ih - 1, maxC = iw - 1;
            switch (s.Kind)
            {
                case RoiShapeKind.Rectangle1:
                case RoiShapeKind.Line:
                    ShiftBBoxIn(s, s.Row, s.Col, s.Row2, s.Col2, maxR, maxC);
                    break;
                case RoiShapeKind.Polygon:
                    if (s.Polygon == null) break;
                    {
                        double minC = double.MaxValue, minR = double.MaxValue;
                        double maxCv = double.MinValue, maxRv = double.MinValue;
                        foreach (var p in s.Polygon)
                        {
                            minC = Math.Min(minC, p.X); maxCv = Math.Max(maxCv, p.X);
                            minR = Math.Min(minR, p.Y); maxRv = Math.Max(maxRv, p.Y);
                        }
                        double w = maxCv - minC, h = maxRv - minR;
                        double dc = 0, dr = 0;
                        if (w <= maxC && maxCv > maxC) dc = maxC - maxCv;
                        else if (minC < 0) dc = -minC;
                        if (h <= maxR && maxRv > maxR) dr = maxR - maxRv;
                        else if (minR < 0) dr = -minR;
                        if (dc != 0 || dr != 0) s.Translate(dr, dc);
                    }
                    break;
                default:
                    s.Row = ClampValue(s.Row, 0, maxR);
                    s.Col = ClampValue(s.Col, 0, maxC);
                    break;
            }
        }

        private void ShiftBBoxIn(RoiShape s, double r1, double c1, double r2, double c2, double maxR, double maxC)
        {
            double w = c2 - c1, h = r2 - r1;
            double dc = 0, dr = 0;
            if (w <= maxC)
            {
                if (c2 > maxC) dc = maxC - c2;
                else if (c1 < 0) dc = -c1;
            }
            if (h <= maxR)
            {
                if (r2 > maxR) dr = maxR - r2;
                else if (r1 < 0) dr = -r1;
            }
            if (dc != 0 || dr != 0) s.Translate(dr, dc);
        }

        private void DoPan(Point vp)
        {
            if (!IsPanEnabled)
            {
                _ptrDrag = PtrDragMode.None;
                ReleaseOverlayCapture();
                return;
            }
            var now = DateTime.UtcNow;
            if ((now - _lastPanUtc2).TotalMilliseconds < 30) return;
            _lastPanUtc2 = now;
            double dx = vp.X - _panLastView.X, dy = vp.Y - _panLastView.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
            _panLastView = vp;
            if (PanWindowByPixels(dx, dy))
            {
                RepaintScene();
                RefreshRoiVisuals();
            }
        }

        // ===================== 框选放大（沿用既有逻辑） =====================

        private void StartZoomBox(Point vp)
        {
            _dragActive = true;
            _dragStart = vp;
            _dragEnd = vp;
            OverlayLayer.CaptureMouse();
        }

        // ===================== 命中测试 / 悬停光标（纯几何算法） =====================

        /// <summary>
        /// 点选命中：把视口点换算为图像坐标后做几何判定，不依赖任何 WPF 可视树元素。
        /// （实证：HSmartWindowControlWPF 视口上方的 WPF 覆盖层不参与合成，可视树命中同样失效——
        /// 曾经 RoiCanvas 内透明命中图形点选不到 ROI，即此因。）
        /// 优先级：选中形状的编辑手柄 &gt; 各形状本体（最上层优先）。
        /// </summary>
        private RoiElementTag HitTestRoiTag(Point viewPt)
        {
            if (_hWindow == null) return null;
            if (!TryGetImagePointAt(viewPt, out double row, out double col)) return null;

            // 1) 选中形状的编辑手柄（命中容差略大，便于点按 9px 方块）
            if (_selectedShape != null && !IsRoiDrawTool())
            {
                if (HitTestShapeHandles(_selectedShape, row, col) is string hKey)
                {
                    return new RoiElementTag(_selectedShape, hKey);
                }
            }

            // 2) 形状本体：从最上层（后画）往底层判定
            for (int i = _roiShapes.Count - 1; i >= 0; i--)
            {
                var roi = _roiShapes[i];
                if (RoiBodyHitTest(roi, row, col))
                {
                    return new RoiElementTag(roi, "body");
                }
            }
            return null;
        }

        /// <summary>像素命中容差 → 图像单位（行/列向）</summary>
        private void GetHitTolerances(out double tolRow, out double tolCol)
        {
            tolRow = tolCol = RoiHitSlopPx;
            TryGetViewScale(out double sx, out double sy, out _, out _);
            tolRow = RoiHitSlopPx / Math.Max(1e-6, sy);
            tolCol = RoiHitSlopPx / Math.Max(1e-6, sx);
        }

        /// <summary>点 (row,col) 是否命中某形状本体（含内部填充与边界线）</summary>
        private bool RoiBodyHitTest(RoiShape s, double row, double col)
        {
            GetHitTolerances(out double tolR, out double tolC);
            switch (s.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    return row >= s.Row - tolR && row <= s.Row2 + tolR &&
                           col >= s.Col - tolC && col <= s.Col2 + tolC;

                case RoiShapeKind.Line:
                    return PointToSegmentDist(row, col, s.Row, s.Col, s.Row2, s.Col2)
                           <= Math.Max(tolR, tolC);

                case RoiShapeKind.Circle:
                    return Dist(s.Row, s.Col, row, col) <= s.Radius1 + Math.Max(tolR, tolC);

                case RoiShapeKind.Rectangle2:
                {
                    // 主轴/副轴投影长度（点相对中心的 (行,列) 投影到旋转轴系）
                    double dr = row - s.Row, dc = col - s.Col;
                    double u = Math.Abs(dc * Math.Cos(s.Phi) + dr * Math.Sin(s.Phi));
                    double v = Math.Abs(-dc * Math.Sin(s.Phi) + dr * Math.Cos(s.Phi));
                    return u <= s.Length1 + tolC && v <= s.Length2 + tolR;
                }

                case RoiShapeKind.Ellipse:
                {
                    double dr = row - s.Row, dc = col - s.Col;
                    double u = dc * Math.Cos(s.Phi) + dr * Math.Sin(s.Phi);
                    double v = -dc * Math.Sin(s.Phi) + dr * Math.Cos(s.Phi);
                    double a = Math.Max(s.Radius1, 1e-6), b = Math.Max(s.Radius2, 1e-6);
                    double norm = Math.Sqrt((u * u) / (a * a) + (v * v) / (b * b));
                    double eps = Math.Max(tolC / a, tolR / b); // 边界带线性近似
                    return norm <= 1.0 + eps;
                }

                case RoiShapeKind.Polygon:
                    if (s.Polygon == null || s.Polygon.Length < 3) return false;
                    return PointInOrNearPolygon(s.Polygon, row, col, Math.Max(tolR, tolC));

                default:
                    return false;
            }
        }

        /// <summary>射线法判点在多边形内（含内）；或到任一条闭合边距离 ≤ tol（点在轮廓带上）</summary>
        private static bool PointInOrNearPolygon(Point[] poly, double row, double col, double tol)
        {
            int n = poly.Length;
            // 1) 闭合边带命中（容差内吸附线）
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (PointToSegmentDist(row, col, poly[i].Y, poly[i].X, poly[j].Y, poly[j].X) <= tol)
                {
                    return true;
                }
            }
            // 2) 射线法（X=col 向右，Y=row 向下）
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;
                if ((yi > row) != (yj > row))
                {
                    double xint = (xj - xi) * (row - yi) / (yj - yi) + xi;
                    if (col < xint) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>点到线段（图像坐标，row/col）的最短距离</summary>
        private static double PointToSegmentDist(double pr, double pc, double r1, double c1, double r2, double c2)
        {
            double dr = r2 - r1, dc = c2 - c1;
            double len2 = dr * dr + dc * dc;
            if (len2 < 1e-9) return Dist(pr, pc, r1, c1);
            double t = ((pr - r1) * dr + (pc - c1) * dc) / len2;
            t = Math.Max(0, Math.Min(1, t));
            return Dist(pr, pc, r1 + t * dr, c1 + t * dc);
        }

        private void UpdateHoverCursor(Point vp)
        {
            if (OverlayLayer == null) return;
            var tag = HitTestRoiTag(vp);
            OverlayLayer.Cursor = (tag == null || tag.Roi == null) ? Cursors.Arrow : Cursors.SizeAll;
        }

        // ===================== 编辑手柄（几何命中 + 窗口层视觉共用数据源） =====================

        /// <summary>命中选中形状的编辑手柄：命中返回手柄键名，未命中返回 null（矩形容差，与方块视觉一致）</summary>
        private string HitTestShapeHandles(RoiShape s, double row, double col)
        {
            TryGetViewScale(out double sx, out double sy, out _, out _);
            double tolR = HandleHitSlopPx / Math.Max(1e-6, sy);
            double tolC = HandleHitSlopPx / Math.Max(1e-6, sx);
            foreach (var hp in GetShapeHandles(s))
            {
                if (Math.Abs(hp.Row - row) <= tolR && Math.Abs(hp.Col - col) <= tolC)
                {
                    return hp.Key;
                }
            }
            return null;
        }

        /// <summary>生成形状的编辑手柄点列（图像坐标；中心柄键 C）。窗口层视觉与几何命中均取此数据</summary>
        private static List<RoiHandlePoint> GetShapeHandles(RoiShape s)
        {
            var list = new List<RoiHandlePoint>();
            switch (s.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    list.Add(new RoiHandlePoint("TL", s.Row, s.Col));
                    list.Add(new RoiHandlePoint("TR", s.Row, s.Col2));
                    list.Add(new RoiHandlePoint("BL", s.Row2, s.Col));
                    list.Add(new RoiHandlePoint("BR", s.Row2, s.Col2));
                    break;

                case RoiShapeKind.Circle:
                    list.Add(new RoiHandlePoint("R", s.Row, s.Col + s.Radius1));
                    break;

                case RoiShapeKind.Ellipse:
                    list.Add(new RoiHandlePoint("R1",
                        s.Row + Math.Sin(s.Phi) * s.Radius1, s.Col + Math.Cos(s.Phi) * s.Radius1));
                    list.Add(new RoiHandlePoint("R2",
                        s.Row + Math.Cos(s.Phi) * s.Radius2, s.Col - Math.Sin(s.Phi) * s.Radius2));
                    break;

                case RoiShapeKind.Rectangle2:
                    list.Add(new RoiHandlePoint("L1",
                        s.Row + Math.Sin(s.Phi) * s.Length1, s.Col + Math.Cos(s.Phi) * s.Length1));
                    list.Add(new RoiHandlePoint("L2",
                        s.Row + Math.Cos(s.Phi) * s.Length2, s.Col - Math.Sin(s.Phi) * s.Length2));
                    break;

                case RoiShapeKind.Polygon:
                    if (s.Polygon != null && s.Polygon.Length >= 1)
                    {
                        foreach (int i in PolygonHandleIndices(s.Polygon.Length))
                        {
                            list.Add(new RoiHandlePoint("V" + i, s.Polygon[i].Y, s.Polygon[i].X));
                        }
                    }
                    break;

                case RoiShapeKind.Line:
                    list.Add(new RoiHandlePoint("A", s.Row, s.Col));
                    list.Add(new RoiHandlePoint("B", s.Row2, s.Col2));
                    break;
            }

            // 中心手柄（圆/椭圆/旋转矩形）：拖动中心微调位置。
            // 平行矩形/多边形/线段本体内部拖动已足够，无需中心柄（避免冗余柄遮挡画面）
            if (s.Kind == RoiShapeKind.Circle || s.Kind == RoiShapeKind.Ellipse || s.Kind == RoiShapeKind.Rectangle2)
            {
                list.Add(new RoiHandlePoint("C", s.Row, s.Col));
            }
            return list;
        }

        /// <summary>多边形顶点手柄索引：顶点 ≤40 全量显示；更高密度（手绘区域数百上千点）
        /// 均匀抽取 ≤16 个代表性索引（含首/尾），避免画面被手柄方块淹没、命中/拖拽保持有效</summary>
        private static int[] PolygonHandleIndices(int count)
        {
            if (count <= 40)
            {
                var all = new int[count];
                for (int i = 0; i < count; i++) all[i] = i;
                return all;
            }
            const int maxHandles = 16;
            var res = new int[maxHandles + 1];
            double step = (count - 1) / (double)maxHandles;
            for (int k = 0; k <= maxHandles; k++)
            {
                res[k] = (int)Math.Round(k * step);
            }
            return res;
        }

        private void SelectRoi(RoiShape roi, bool refresh)
        {
            _selectedShape = roi;
            if (refresh) RefreshRoiVisuals();
        }

        // ===================== 右键菜单 =====================

        private void ShowRoiContextMenu(RoiShape roi)
        {
            if (roi == null) return;
            // 弹菜单前先释放覆盖层鼠标捕获——若仍捕获，Popup 内点击会被重定向到覆盖层，
            // MenuItem.Click 收不到（表现为"点了没反应"）
            ReleaseOverlayCapture();

            bool closedShape = roi.Kind != RoiShapeKind.Line; // 只有闭合形状能区域化填充

            var cm = new ContextMenu();
            cm.Placement = PlacementMode.MousePoint;
            cm.PlacementTarget = OverlayLayer;

            var miDelete = new MenuItem { Header = "❌ 删除" };
            miDelete.Click += (s, a) => RemoveRoi(roi);
            cm.Items.Add(miDelete);

            var miTop = new MenuItem { Header = "⏫ 移到最上层" };
            miTop.Click += (s, a) =>
            {
                _roiShapes.Remove(roi);
                _roiShapes.Add(roi);
                _selectedShape = roi;
                RefreshRoiVisuals();
            };
            cm.Items.Add(miTop);

            if (closedShape)
            {
                cm.Items.Add(new Separator());

                // 区域化显示开关（fill：填充色块 + 同对象描边）
                var miFill = new MenuItem
                {
                    Header = roi.Filled ? "■ 填充区域（点击取消）" : "□ 填充区域（fill）",
                    IsCheckable = true,
                    IsChecked = roi.Filled
                };
                miFill.Click += (s, a) =>
                {
                    roi.Filled = !roi.Filled;
                    if (roi.Filled && string.IsNullOrEmpty(roi.FillColorName))
                    {
                        roi.FillColorName = roi.ColorName; // 无显式填充色时沿用轮廓色
                    }
                    NotifyRoiStyled(roi);
                };
                cm.Items.Add(miFill);
            }

            cm.Items.Add(new Separator());

            // 轮廓颜色（默认始终可用）
            var miOutline = new MenuItem { Header = "✏️ 轮廓颜色" };
            foreach (var name in RoiPalette)
            {
                var item = new MenuItem
                {
                    Header = PaletteDisplayName(name),
                    Icon = PaletteColorSwatch(name),
                    IsChecked = string.Equals(roi.ColorName, name, StringComparison.OrdinalIgnoreCase)
                };
                string color = name;
                item.Click += (s, a) =>
                {
                    roi.ColorName = color;
                    NotifyRoiStyled(roi);
                };
                miOutline.Items.Add(item);
            }
            cm.Items.Add(miOutline);

            // 填充颜色（仅闭合形状；点击即自动开启区域填充并换填充色）
            if (closedShape)
            {
                var miFillColor = new MenuItem { Header = "▨ 填充颜色" };
                foreach (var name in RoiPalette)
                {
                    var item = new MenuItem
                    {
                        Header = PaletteDisplayName(name),
                        Icon = PaletteColorSwatch(name),
                        IsChecked = string.Equals(
                            string.IsNullOrEmpty(roi.FillColorName) ? roi.ColorName : roi.FillColorName,
                            name, StringComparison.OrdinalIgnoreCase)
                    };
                    string color = name;
                    item.Click += (s, a) =>
                    {
                        roi.FillColorName = color;
                        roi.Filled = true; // 选填充色即视为要区域化显示
                        NotifyRoiStyled(roi);
                    };
                    miFillColor.Items.Add(item);
                }
                cm.Items.Add(miFillColor);
            }

            cm.IsOpen = true;
        }

        /// <summary>颜色/填充样式变更的统一收口：重绘 + 发布 RoiEdited（宿主外部可实时联动）</summary>
        private void NotifyRoiStyled(RoiShape roi)
        {
            RefreshRoiVisuals();
            RoiEdited?.Invoke(this, new RoiShapeEventArgs(roi));
            TraceRoi($"ROI 样式变更 kind={roi.Kind} color={roi.ColorName} filled={roi.Filled} fill={roi.FillColorName ?? "-"}");
        }

        /// <summary>HALCON 颜色名的中文展示名（菜单可读性）</summary>
        private static string PaletteDisplayName(string name)
        {
            switch (name)
            {
                case "yellow": return "黄色 (yellow)";
                case "cyan": return "青色 (cyan)";
                case "orange": return "橙色 (orange)";
                case "red": return "红色 (red)";
                case "green": return "绿色 (green)";
                case "magenta": return "品红 (magenta)";
                case "blue": return "蓝色 (blue)";
                case "white": return "白色 (white)";
                default: return name;
            }
        }

        /// <summary>颜色菜单项左侧的色块图标（WPF Brush，仅做菜单预览）</summary>
        private static System.Windows.Shapes.Rectangle PaletteColorSwatch(string name)
        {
            System.Windows.Media.Color c = System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0x00); // yellow 兜底
            switch (name)
            {
                case "cyan": c = System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0xFF); break;
                case "orange": c = System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00); break;
                case "red": c = System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x00); break;
                case "green": c = System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0x00); break;
                case "magenta": c = System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0xFF); break;
                case "blue": c = System.Windows.Media.Color.FromRgb(0x00, 0x00, 0xFF); break;
                case "white": c = System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF); break;
            }
            var sw = new System.Windows.Shapes.Rectangle
            {
                Width = 13,
                Height = 13,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new System.Windows.Media.SolidColorBrush(c),
                Stroke = System.Windows.Media.Brushes.DarkGray,
                StrokeThickness = 1,
                Margin = new Thickness(2)
            };
            return sw;
        }

        // ===================== 捕获辅助 =====================

        private void CaptureOverlay()
        {
            if (OverlayLayer != null && !OverlayLayer.IsMouseCaptured)
            {
                OverlayLayer.CaptureMouse();
            }
        }

        /// <summary>把键盘焦点移到覆盖层（Focusable 已在 XAML 开启），确保 Esc/Delete 可靠到达宿主</summary>
        private void TryFocusOverlay()
        {
            if (OverlayLayer == null) return;
            try
            {
                if (!OverlayLayer.IsKeyboardFocusWithin)
                {
                    OverlayLayer.Focus();
                }
            }
            catch { /* 焦点系统异常忽略 */ }
        }

        private void ReleaseOverlayCapture()
        {
            if (OverlayLayer != null && OverlayLayer.IsMouseCaptured)
            {
                OverlayLayer.ReleaseMouseCapture();
            }
        }

        private void SetSketchHint()
        {
            if (DrawHint == null || DrawHintText == null) return;
            switch (_activeTool)
            {
                case ViewTool.Polygon:
                    DrawHintText.Text = _polyVerts.Count == 0
                        ? "🟢 左键逐个添加顶点 · 右键闭合完成创建 · Esc 取消"
                        : $"🟢 已 {_polyVerts.Count} 个顶点：左键继续添加 · 右键闭合完成 · Esc 取消";
                    break;
                case ViewTool.Freehand:
                    DrawHintText.Text = _freehandDrawing
                        ? $"✒️ 按住左键沿轮廓描线（已 {_polyVerts.Count} 点）· 松开暂停 · 右键闭合完成 · Esc 取消"
                        : _polyVerts.Count == 0
                            ? "✒️ 按住左键沿轮廓自由描线（跟随鼠标路径）· 右键自动闭合创建 · Esc 取消"
                            : $"✒️ 按住左键继续描线（已 {_polyVerts.Count} 点）· 松开暂停 · 右键闭合完成 · Esc 取消";
                    break;
                case ViewTool.Brush:
                    DrawHintText.Text = _brushDrawing
                        ? $"🖌 正在涂抹（{_polyVerts.Count} 轨迹点 · 半径 {BrushRadiusPx:F0}px）· 松开=收笔一笔 · 可连续涂 · 右键/Esc 退出"
                        : $"🖌 按住左键沿区域涂抹（画笔半径 {BrushRadiusPx:F0}px，可在掩膜面板调）· 松开=收笔一笔 · 右键/Esc 退出";
                    break;
                case ViewTool.Ellipse:
                case ViewTool.Rect2:
                    DrawHintText.Text = _draftPhase2
                        ? "↔ 移动鼠标调整副轴宽度 · 右键 完成 · Esc 取消"
                        : "↗ 按住左键拖出主轴（长度+角度）· 松开后移动调整宽度 · 右键 完成";
                    break;
                case ViewTool.Line:
                    DrawHintText.Text = "✏️ 左键拖出（或点击定位端点）· 右键 完成 · Esc 取消";
                    break;
                default:
                    DrawHintText.Text = "✏️ 左键按下拖出形状 · 松手后继续跟手微调 · 右键 完成 · Esc 取消";
                    break;
            }
        }

        // ===================== 视觉渲染 =====================

        /// <summary>
        /// ROI 视觉刷新入口：绘制/选中/拖动/编辑等状态变化后调用。
        /// 视觉（轮廓/草绘/手柄/参数信息）全在 HALCON 窗口层，由 RepaintScene 尾步
        /// DrawRoiEditorLayerToWindow 重放——这里只负责触发整帧重放：
        ///   一次性动作（提交/选中/删除/切工具）立即整帧；
        ///   连续手势（草绘/拖动变形/手绘描线/多边形逐点/框选）24ms 节流。
        /// 不再维护任何 WPF 命中图形——点选/拖动/手柄命中为纯几何算法（HitTestRoiTag）。
        /// </summary>
        private void RefreshRoiVisuals(bool withSceneRepaint = true)
        {
            if (!withSceneRepaint || _inRoiSceneRepaint || _hWindow == null) return;

            bool continuous = _ptrDrag != PtrDragMode.None || _dragActive || _draft != null || _freehandDrawing
                              || _brushDrawing
                              || ((_activeTool == ViewTool.Polygon || _activeTool == ViewTool.Freehand
                                   || _activeTool == ViewTool.Brush)
                                  && _polyVerts.Count > 0);
            if (!continuous)
            {
                RepaintScene();
                return;
            }
            var now = DateTime.UtcNow;
            if ((now - _lastRoiLayerPaintUtc).TotalMilliseconds >= 24)
            {
                _lastRoiLayerPaintUtc = now;
                RepaintScene();
            }
        }

        /// <summary>按 ROI 类型生成 图像坐标 → 屏幕坐标 的外轮廓点列</summary>
        private Point[] ComputeOutlinePoints(RoiShape roi, double sx, double sy, double c1, double r1)
        {
            switch (roi.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    return new[]
                    {
                        ImgToView(roi.Row, roi.Col, sx, sy, c1, r1),
                        ImgToView(roi.Row, roi.Col2, sx, sy, c1, r1),
                        ImgToView(roi.Row2, roi.Col2, sx, sy, c1, r1),
                        ImgToView(roi.Row2, roi.Col, sx, sy, c1, r1)
                    };

                case RoiShapeKind.Line:
                    return new[]
                    {
                        ImgToView(roi.Row, roi.Col, sx, sy, c1, r1),
                        ImgToView(roi.Row2, roi.Col2, sx, sy, c1, r1)
                    };

                case RoiShapeKind.Circle:
                    return SampleEllipse(roi.Row, roi.Col, roi.Radius1, roi.Radius1, 0, 48, sx, sy, c1, r1);

                case RoiShapeKind.Ellipse:
                    return SampleEllipse(roi.Row, roi.Col, roi.Radius1, roi.Radius2, roi.Phi, 72, sx, sy, c1, r1);

                case RoiShapeKind.Rectangle2:
                {
                    // 主轴 u=(cosφ*L1 列, sinφ*L1 行)，副轴 v=(-sinφ*L2 列, cosφ*L2 行)
                    double ux = Math.Cos(roi.Phi) * roi.Length1, uy = Math.Sin(roi.Phi) * roi.Length1;
                    double vx = -Math.Sin(roi.Phi) * roi.Length2, vy = Math.Cos(roi.Phi) * roi.Length2;
                    return new[]
                    {
                        ImgToView(roi.Row + uy + vy, roi.Col + ux + vx, sx, sy, c1, r1), // +u+v
                        ImgToView(roi.Row + uy - vy, roi.Col + ux - vx, sx, sy, c1, r1), // +u-v
                        ImgToView(roi.Row - uy - vy, roi.Col - ux - vx, sx, sy, c1, r1), // -u-v
                        ImgToView(roi.Row - uy + vy, roi.Col - ux + vx, sx, sy, c1, r1)  // -u+v
                    };
                }

                case RoiShapeKind.Polygon:
                    if (roi.Polygon == null || roi.Polygon.Length < 3) return null;
                    var poly = new Point[roi.Polygon.Length];
                    for (int i = 0; i < roi.Polygon.Length; i++)
                    {
                        poly[i] = ImgToView(roi.Polygon[i].Y, roi.Polygon[i].X, sx, sy, c1, r1);
                    }
                    return poly;

                default:
                    return null;
            }
        }

        /// <summary>椭圆/圆在图像空间采样为多边形点列（兼容不等比缩放）</summary>
        private Point[] SampleEllipse(double row, double col, double a, double b, double phi,
                                      int n, double sx, double sy, double c1, double r1)
        {
            if (a <= 0 || b <= 0) return null;
            var pts = new Point[n];
            double cosP = Math.Cos(phi), sinP = Math.Sin(phi);
            for (int i = 0; i < n; i++)
            {
                double t = i * 2.0 * Math.PI / n;
                double ct = Math.Cos(t), st = Math.Sin(t);
                double cc = a * ct, rr = b * st;
                double colI = col + cc * cosP - rr * sinP;
                double rowI = row + cc * sinP + rr * cosP;
                pts[i] = ImgToView(rowI, colI, sx, sy, c1, r1);
            }
            return pts;
        }

        // =====================================================================
        //  HALCON 窗口层 ROI 视觉（可见绘制的唯一通道）
        //  —— 实证：WPF 覆盖层内容叠在 HSmartWindowControlWPF 视口上方不显示，
        //     而 HALCON 窗口内绘制（底图 / context.Overlays 黄框 / 场景条目）全部可见。
        //     因此已提交轮廓 / 草绘 / 选中手柄 / 框选橡皮筋全部改由本层绘制，
        //     由 RepaintScene 在底图 + 场景叠加之后调用（清窗由调用方负责，此处只追加）。
        // =====================================================================

        /// <summary>把 ROI 编辑器当前视觉画进 HALCON 窗口层（跟随当前 part，图像像素坐标）</summary>
        private void DrawRoiEditorLayerToWindow()
        {
            var hw = _hWindow;
            if (hw == null) return;
            bool inDraw = IsRoiDrawTool();
            bool showHandles = _selectedShape != null && !inDraw;
            bool zoomRubber = _activeTool == ViewTool.ZoomRect && _dragActive;
            bool polySketch = (_activeTool == ViewTool.Polygon || _activeTool == ViewTool.Freehand
                               || _activeTool == ViewTool.Brush)
                              && _polyVerts.Count > 0;
            bool infoShown = HasRoiInfo();
            if (_roiShapes.Count == 0 && _draft == null && !showHandles && !zoomRubber && !polySketch && !infoShown)
            {
                return;
            }

            try
            {
                // 1) 已提交 ROI 轮廓（任何几何类型都可见；选中者加粗高亮）
                foreach (var roi in _roiShapes)
                {
                    bool sel = ReferenceEquals(roi, _selectedShape) && !inDraw;
                    DrawShapeOutlineHw(hw, roi, sel ? "yellow" : RoiHalconColor(roi.ColorName),
                                       sel ? 2.4 : 1.5);
                }

                // 2) 草绘 / 多边形·手绘·涂抹预览（绿色）
                if (polySketch)
                {
                    if (_activeTool == ViewTool.Brush)
                    {
                        DrawBrushSketchHw(hw); // 涂抹：圆盘带预览（粗折线+圆头，所见=引擎膨胀结果）
                    }
                    else
                    {
                        DrawPolygonSketchHw(hw);
                    }
                }
                else if (_draft != null && IsRoiDrawTool())
                {
                    DrawShapeOutlineHw(hw, _draft, "green", 1.8);
                    DrawDraftAnchorHw(hw);
                }

                // 3) 选中手柄（橙色方块，屏幕恒定 ~9px）
                if (showHandles)
                {
                    DrawSelectionHandlesHw(hw, _selectedShape);
                }

                // 4) 框选放大橡皮筋
                if (zoomRubber)
                {
                    DrawZoomRubberHw(hw);
                }

                // 5) ROI 参数信息条（HDevelop 风格：窗口左上角黑底黄字实时显示当前 ROI 几何参数）
                if (infoShown)
                {
                    DrawRoiInfoOverlayHw(hw);
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn("HalconHost", $"ROI 窗口层绘制失败: {ex.Message}");
            }
        }

        private static string RoiHalconColor(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < RoiPalette.Length; i++)
                {
                    if (RoiPalette[i] == name) return name;
                }
            }
            return "yellow";
        }

        /// <summary>按 ROI 几何在 HALCON 窗口内画轮廓线（region margin / XLD）</summary>
        private void DrawShapeOutlineHw(HWindow hw, RoiShape roi, string color, double width)
        {
            HObject obj = null;
            try
            {
                switch (roi.Kind)
                {
                    case RoiShapeKind.Rectangle1:
                        HOperatorSet.GenRectangle1(out obj, roi.Row, roi.Col, roi.Row2, roi.Col2);
                        break;
                    case RoiShapeKind.Rectangle2:
                        if (roi.Length1 > 0 && roi.Length2 > 0)
                        {
                            HOperatorSet.GenRectangle2(out obj, roi.Row, roi.Col, roi.Phi, roi.Length1, roi.Length2);
                        }
                        break;
                    case RoiShapeKind.Circle:
                        if (roi.Radius1 > 0)
                        {
                            HOperatorSet.GenCircle(out obj, roi.Row, roi.Col, roi.Radius1);
                        }
                        break;
                    case RoiShapeKind.Ellipse:
                        if (roi.Radius1 > 0 && roi.Radius2 > 0)
                        {
                            HOperatorSet.GenEllipse(out obj, roi.Row, roi.Col, roi.Phi, roi.Radius1, roi.Radius2);
                        }
                        break;
                    case RoiShapeKind.Polygon:
                        if (roi.Polygon != null && roi.Polygon.Length >= 3)
                        {
                            var rows = new double[roi.Polygon.Length];
                            var cols = new double[roi.Polygon.Length];
                            for (int i = 0; i < roi.Polygon.Length; i++)
                            {
                                rows[i] = roi.Polygon[i].Y;
                                cols[i] = roi.Polygon[i].X;
                            }
                            HOperatorSet.GenRegionPolygonFilled(out obj, rows, cols);
                        }
                        break;
                    case RoiShapeKind.Line:
                    {
                        double[] rr = { roi.Row, roi.Row2 }, cc = { roi.Col, roi.Col2 };
                        HOperatorSet.GenContourPolygonXld(out obj, rr, cc);
                        break;
                    }
                }
                if (obj == null) return;
                // 区域化显示（fill）：先以填充色实心铺一遍，再描边——同一对象两次 DispObj。
                // 注意：HSmartWindow 无 alpha 通道，fill 为不透明纯色（会盖住底图细节），
                // 由用户通过右键菜单「填充区域/填充颜色」显式开启。
                bool filledRegion = roi.Filled && roi.Kind != RoiShapeKind.Line;
                if (filledRegion)
                {
                    string fillColor = string.IsNullOrEmpty(roi.FillColorName) ? color : roi.FillColorName;
                    hw.SetColor(fillColor);
                    hw.SetDraw("fill");
                    hw.DispObj(obj);
                }
                hw.SetColor(color);
                hw.SetLineWidth(width);
                hw.SetDraw("margin");
                hw.DispObj(obj);
            }
            catch (Exception ex)
            {
                LogBus.Debug("HalconHost", $"ROI 轮廓绘制跳过: {ex.Message}");
            }
            finally
            {
                try { obj?.Dispose(); } catch { }
            }
        }

        /// <summary>草绘未拖出有效视口尺寸时，在轮廓中心画一个小圆点：任何缩放下"按下即见"</summary>
        private void DrawDraftAnchorHw(HWindow hw)
        {
            if (_draft == null) return;
            if (!TryGetViewScale(out double sx, out double sy, out double c1, out double r1)) return;
            var pts = ComputeOutlinePoints(_draft, sx, sy, c1, r1);
            if (pts == null || pts.Length == 0) return;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            if (maxX - minX >= 8 && maxY - minY >= 8) return; // 已拖开，正常画轮廓即可

            Point vc = new Point((minX + maxX) / 2, (minY + maxY) / 2);
            if (!TryGetImagePointAt(vc, out double irow, out double icol)) return;
            double rad = Math.Max(3.0, 4.0 / Math.Max(1e-3, Math.Max(sx, sy)));
            try
            {
                HOperatorSet.GenCircle(out HObject dot, irow, icol, rad);
                hw.SetColor("green");
                hw.SetDraw("fill");
                hw.DispObj(dot);
                dot.Dispose();
            }
            catch { }
        }

        /// <summary>多边形/手绘草绘预览：边线（末顶点→光标橡皮筋，仅进行中）+ 顶点方块，全在 HALCON 窗口层</summary>
        private void DrawPolygonSketchHw(HWindow hw)
        {
            if (_polyVerts.Count == 0) return;
            bool freehand = _activeTool == ViewTool.Freehand;
            bool connected = _activeTool == ViewTool.Polygon || _freehandDrawing; // 手绘暂停时不伸光标橡皮筋
            double sx = 1, sy = 1;
            TryGetViewScale(out sx, out sy, out _, out _);
            double halfC = Math.Max(2.5, 3.5 / Math.Max(1e-3, sx));
            double halfR = Math.Max(2.5, 3.5 / Math.Max(1e-3, sy));
            hw.SetColor("green");
            try
            {
                // 已定顶点 → （进行中）当前鼠标的折线
                var rows = new List<double>(_polyVerts.Count + 1);
                var cols = new List<double>(_polyVerts.Count + 1);
                for (int i = 0; i < _polyVerts.Count; i++)
                {
                    rows.Add(_polyVerts[i].Y);
                    cols.Add(_polyVerts[i].X);
                }
                if (connected)
                {
                    rows.Add(_mouseImgRow);
                    cols.Add(_mouseImgCol);
                }
                if (rows.Count >= 2)
                {
                    HOperatorSet.GenContourPolygonXld(out HObject c, rows.ToArray(), cols.ToArray());
                    hw.SetLineWidth(freehand ? 1.8 : 1.4);
                    hw.DispObj(c);
                    c.Dispose();
                }

                // 顶点方块：逐点多边形（点少可控）显示；手绘高密度轨迹画方块会淹没画面，不画
                if (_activeTool == ViewTool.Polygon && _polyVerts.Count <= 200)
                {
                    hw.SetDraw("fill");
                    for (int i = 0; i < _polyVerts.Count; i++)
                    {
                        HOperatorSet.GenRectangle2(out HObject q, _polyVerts[i].Y, _polyVerts[i].X, 0.0, halfC, halfR);
                        hw.DispObj(q);
                        q.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                LogBus.Debug("HalconHost", $"多边形草绘跳过: {ex.Message}");
            }
        }

        /// <summary>
        /// 涂抹画笔草绘预览：把轨迹画成"宽 2×BrushRadiusPx 的圆盘带"（图像半径×当前缩放=窗口像素线宽）
        /// + 首尾实心圆头。视觉与引擎侧膨胀（TemplateMaskRegionBuilder brush 分支）口径一致——
        /// 用户所见带形即引擎所得区域。绿色预览、收笔后由掩膜叠加层接替着色。
        /// </summary>
        private void DrawBrushSketchHw(HWindow hw)
        {
            if (_polyVerts.Count == 0) return;
            double sx = 1, sy = 1;
            TryGetViewScale(out sx, out sy, out _, out _);
            double scale = Math.Max(sx, sy);
            double imgRadius = Math.Max(BrushRadiusPx, 0.5);
            double pxWidth = Math.Max(1.0, imgRadius * 2.0 * scale); // 窗口像素线宽（不随 part 缩放）
            try
            {
                hw.SetColor("green");
                hw.SetDraw("margin");
                var rows = new List<double>(_polyVerts.Count + 1);
                var cols = new List<double>(_polyVerts.Count + 1);
                for (int i = 0; i < _polyVerts.Count; i++)
                {
                    rows.Add(_polyVerts[i].Y);
                    cols.Add(_polyVerts[i].X);
                }
                if (_brushDrawing)
                {
                    // 按住中：轨迹实时延伸到光标（圆盘带跟手）
                    rows.Add(_mouseImgRow);
                    cols.Add(_mouseImgCol);
                }
                if (rows.Count >= 2)
                {
                    HOperatorSet.GenContourPolygonXld(out HObject c, rows.ToArray(), cols.ToArray());
                    hw.SetLineWidth(pxWidth);
                    hw.DispObj(c);
                    c.Dispose();
                }
                // 首尾圆头（粗折线端点是方头，补两个实心圆成圆头带；单点=一个圆）
                hw.SetDraw("fill");
                HOperatorSet.GenCircle(out HObject head, _polyVerts[0].Y, _polyVerts[0].X, imgRadius);
                hw.DispObj(head);
                head.Dispose();
                double tailRow = _brushDrawing ? _mouseImgRow : _polyVerts[_polyVerts.Count - 1].Y;
                double tailCol = _brushDrawing ? _mouseImgCol : _polyVerts[_polyVerts.Count - 1].X;
                HOperatorSet.GenCircle(out HObject tail, tailRow, tailCol, imgRadius);
                hw.DispObj(tail);
                tail.Dispose();
            }
            catch (Exception ex)
            {
                LogBus.Debug("HalconHost", $"涂抹草绘跳过: {ex.Message}");
            }
        }

        /// <summary>选中形状的手柄方块（屏幕恒定 ~9px，橙）；手柄数据源 GetShapeHandles 同时供几何命中与视觉</summary>
        private void DrawSelectionHandlesHw(HWindow hw, RoiShape s)
        {
            double sx = 1, sy = 1;
            TryGetViewScale(out sx, out sy, out _, out _);
            double halfC = Math.Max(2.5, 4.5 / Math.Max(1e-3, sx));
            double halfR = Math.Max(2.5, 4.5 / Math.Max(1e-3, sy));
            var handles = GetShapeHandles(s);
            if (handles.Count == 0) return;
            try
            {
                hw.SetColor("orange");
                hw.SetDraw("fill");
                foreach (var hp in handles)
                {
                    HOperatorSet.GenRectangle2(out HObject q, hp.Row, hp.Col, 0.0, halfC, halfR);
                    hw.DispObj(q);
                    q.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogBus.Debug("HalconHost", $"选中手柄绘制跳过: {ex.Message}");
            }
        }

        /// <summary>框选放大：视口拖拽矩形画到 HALCON 窗口层（黄色，图像像素坐标）</summary>
        private void DrawZoomRubberHw(HWindow hw)
        {
            if (!_dragActive) return;
            if (!TryGetImagePointAt(_dragStart, out double r1, out double c1)) return;
            if (!TryGetImagePointAt(_dragEnd, out double r2, out double c2)) return;
            double a = Math.Min(r1, r2), b = Math.Max(r1, r2);
            double d = Math.Min(c1, c2), f = Math.Max(c1, c2);
            try
            {
                HOperatorSet.GenRectangle1(out HObject rr, a, d, b, f);
                hw.SetColor("yellow");
                hw.SetLineWidth(1.5);
                hw.SetDraw("margin");
                hw.DispObj(rr);
                rr.Dispose();
            }
            catch { }
        }

        // ===================== ROI 参数信息条（HDevelop 风格，HALCON 窗口层） =====================
        // 参考 HDevelop 绘制 ROI 时的实时参数反馈：绘制中/选中编辑时在窗口固定角落以
        // 黑底文本实时显示当前 ROI 的行列/尺寸/角度，随鼠标跟手刷新（每帧随 RepaintScene 重画）。

        /// <summary>当前是否存在需要展示参数信息的 ROI 上下文（草绘进行中 / 轨迹进行中 / Pointer 选中）</summary>
        private bool HasRoiInfo()
        {
            if (IsRoiDrawTool())
            {
                if (_draft != null) return true;
                return (_activeTool == ViewTool.Polygon || _activeTool == ViewTool.Freehand)
                       && _polyVerts.Count > 0;
            }
            return _activeTool == ViewTool.Pointer && _selectedShape != null;
        }

        /// <summary>当前参数信息文本行（首行标题，其余为数值；无展示上下文返回 null）</summary>
        private string[] CurrentRoiInfoLines()
        {
            if (_draft != null && IsRoiDrawTool())
            {
                return BuildRoiParamLines(_draft, "绘制中");
            }
            if ((_activeTool == ViewTool.Polygon || _activeTool == ViewTool.Freehand) && _polyVerts.Count > 0)
            {
                return BuildPolyTrajectoryInfoLines();
            }
            if (_activeTool == ViewTool.Pointer && _selectedShape != null)
            {
                return BuildRoiParamLines(_selectedShape, "选中");
            }
            return null;
        }

        private static string[] BuildRoiParamLines(RoiShape s, string state)
        {
            string kindName;
            switch (s.Kind)
            {
                case RoiShapeKind.Rectangle1: kindName = "平行矩形"; break;
                case RoiShapeKind.Rectangle2: kindName = "旋转矩形"; break;
                case RoiShapeKind.Circle: kindName = "圆形"; break;
                case RoiShapeKind.Ellipse: kindName = "椭圆"; break;
                case RoiShapeKind.Polygon: kindName = "区域"; break;
                case RoiShapeKind.Line: kindName = "线段"; break;
                default: kindName = s.Kind.ToString(); break;
            }

            var lines = new List<string> { $"ROI · {kindName} [{state}]" };
            switch (s.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    lines.Add($"Row {s.Row:F1} → {s.Row2:F1}   Col {s.Col:F1} → {s.Col2:F1}");
                    lines.Add($"宽 {s.Col2 - s.Col:F1} × 高 {s.Row2 - s.Row:F1} px");
                    break;

                case RoiShapeKind.Circle:
                    lines.Add($"中心  Row {s.Row:F1}   Col {s.Col:F1}");
                    lines.Add($"半径 {s.Radius1:F1} px");
                    break;

                case RoiShapeKind.Rectangle2:
                    lines.Add($"中心  Row {s.Row:F1}   Col {s.Col:F1}");
                    lines.Add($"半长 {s.Length1:F1} × 半宽 {s.Length2:F1}   角度 {s.Phi * 180 / Math.PI:F1}°");
                    break;

                case RoiShapeKind.Ellipse:
                    lines.Add($"中心  Row {s.Row:F1}   Col {s.Col:F1}");
                    lines.Add($"长轴 {s.Radius1:F1} × 短轴 {s.Radius2:F1}   角度 {s.Phi * 180 / Math.PI:F1}°");
                    break;

                case RoiShapeKind.Line:
                {
                    double len = Dist(s.Row, s.Col, s.Row2, s.Col2);
                    double ang = Math.Atan2(s.Row2 - s.Row, s.Col2 - s.Col) * 180 / Math.PI;
                    lines.Add($"A  Row {s.Row:F1} Col {s.Col:F1}   B  Row {s.Row2:F1} Col {s.Col2:F1}");
                    lines.Add($"长度 {len:F1} px   角度 {ang:F1}°");
                    break;
                }

                case RoiShapeKind.Polygon:
                    if (s.Polygon != null && s.Polygon.Length >= 1)
                    {
                        double minR = double.MaxValue, maxR = double.MinValue;
                        double minC = double.MaxValue, maxC = double.MinValue;
                        foreach (var p in s.Polygon)
                        {
                            minR = Math.Min(minR, p.Y); maxR = Math.Max(maxR, p.Y);
                            minC = Math.Min(minC, p.X); maxC = Math.Max(maxC, p.X);
                        }
                        lines.Add($"顶点 {s.Polygon.Length}   包围 W {maxC - minC:F1} × H {maxR - minR:F1} px");
                    }
                    break;
            }
            return lines.ToArray();
        }

        /// <summary>多边形/手绘轨迹进行中的信息（点数 + 当前已画范围 + 光标）</summary>
        private string[] BuildPolyTrajectoryInfoLines()
        {
            double minR = double.MaxValue, maxR = double.MinValue;
            double minC = double.MaxValue, maxC = double.MinValue;
            foreach (var p in _polyVerts)
            {
                minR = Math.Min(minR, p.Y); maxR = Math.Max(maxR, p.Y);
                minC = Math.Min(minC, p.X); maxC = Math.Max(maxC, p.X);
            }
            bool freehand = _activeTool == ViewTool.Freehand;
            return new[]
            {
                $"ROI · {(freehand ? "手绘区域" : "多边形区域")} [{(freehand ? "描线中" : "定点中")}]",
                $"点数 {_polyVerts.Count}   (Row {_mouseImgRow:F0}, Col {_mouseImgCol:F0})"
            };
        }

        /// <summary>把 ROI 参数信息画到 HALCON 窗口左上角（屏幕固定位置，黑底文本，跟手刷新）</summary>
        private void DrawRoiInfoOverlayHw(HWindow hw)
        {
            var lines = CurrentRoiInfoLines();
            if (lines == null || lines.Length == 0) return;
            if (!TryGetViewScale(out double sx, out double sy, out double c1, out double r1)) return;

            // 屏幕固定 8px 内边距、行距 17px → 换算为图像坐标（文本本身以屏幕像素渲染）
            double padRow = 8.0 / Math.Max(1e-6, sy);
            double padCol = 8.0 / Math.Max(1e-6, sx);
            double lineH = 17.0 / Math.Max(1e-6, sy);
            double row = r1 + padRow;
            double col = c1 + padCol;
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    HalconGlobalHelper.DispTextSafe(hw, lines[i], "image", row + i * lineH, col,
                                                    i == 0 ? "yellow" : "white");
                }
            }
            catch (Exception ex)
            {
                LogBus.Debug("HalconHost", $"ROI 参数信息条绘制跳过: {ex.Message}");
            }
        }
    }
}
