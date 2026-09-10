//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TemplateManagerView.xaml.cs
// 说 明: 模板管理视图 code-behind。
//        ROI 交互已上收进 HalconImageDisplayHost 内置工具条：
//        ▭ 矩形 / ⭘ 圆 / ⬭ 椭圆 / ⬠ 多边形 等绘制（左键拖出 → 右键完成）→ RoiCommitted 写回 VM。
//        v2 ROI 语义（2026-09-09）：学习框=用户所画的形状本身（圆/旋转矩形/多边形…），
//        RoiRow1..Col2 仅作内部窗口/裁剪/回显坐标；AABB 折算在宿主内保留原形状供拖动/缩放，
//        掩膜/预览/创建链全部按"形状∩ROI"起算（TemplateMaskRegionBuilder.BuildLearnRegion）。
//        编辑载入时 EditorRoiReady → ReplaceRois 把基底形状回注宿主（可点选/拖动/手柄编辑）。
//        宿主设 PersistRoiInScene=false：ROI 视觉效果统一走 VM 叠加层黄框，避免双框。
//        2026-09-09 工具条化：🖌 涂抹提升为宿主工具条一级按钮；本页订阅 ViewToolChanged，
//        收到 Brush 即自动进入掩膜编辑语义（学习域预览 + 收笔），滑杆半径实时同步宿主。
//===================================================================================
using Grayson.Vision.HalconWrapper.Wpf.Controls;
using Grayson.Vision.WpfUI.ViewModel;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.View
{
    public partial class TemplateManagerView : UserControl
    {
        // ==================== 📏 卡尺图上拖绘/编辑会话（批3 2026-09-09）====================
        // 宿主 ROI 形状 ↔ 卡尺行映射：进入拖绘模式时把【基底 + 全部卡尺行】回注宿主（基底黄/卡尺青），
        // 拖绘新笔（〰线/⭘圆/▭▣旋转矩形）收笔入行后整组重同步；宿主拖动/删除形状经 RoiEdited/RoiRemoved 回写/删行。
        private readonly Dictionary<RoiShape, TemplateManagerViewModel.CaliperRowItem> _caliperShapeRows =
            new Dictionary<RoiShape, TemplateManagerViewModel.CaliperRowItem>();
        private bool _caliperSessionActive;

        public TemplateManagerView()
        {
            InitializeComponent();
            // 页面缓存单例（App.xaml.cs 页面工厂）：VM 与 View 同生，编辑现场跨导航保活
            DataContext = new TemplateManagerViewModel();
            Loaded += OnViewLoaded;
            Unloaded += OnViewUnloaded;
        }

        private void OnViewLoaded(object sender, RoutedEventArgs e)
        {
            (DataContext as TemplateManagerViewModel)?.OnViewLoaded();
            // 编辑器一帧到底：掩膜预览/学习域预览只是换显示帧（同取景），宿主默认"换帧即清 ROI"会让
            // 用户涂完掩膜后基底 ROI 消失、点选不到（"编辑时 ROI 无法选中"根因）。
            // 真正换取景（新源图/新建向导）由 VM EditorSceneReset → ClearRoisSilently 显式兜底。
            ImageHost.ResetRoisOnFrameChange = false;
            // 订阅内置 ROI 工具事件（切换 DataContext 前后均同一控件，事件常驻安全）
            ImageHost.RoiCommitted += ImageHost_RoiCommitted;
            ImageHost.RoiEdited += ImageHost_RoiEdited;
            ImageHost.RoiRemoved += ImageHost_RoiRemoved;
            ImageHost.RoisCleared += ImageHost_RoisCleared;
            ImageHost.ViewToolChanged += ImageHost_ViewToolChanged; // 🖌 工具条涂抹一键直达掩膜
            ImageHost.BrushSketchChanged += ImageHost_BrushSketchChanged; // 涂抹轨迹实时 → 版图跟手
            if (DataContext is TemplateManagerViewModel vm)
            {
                vm.EditorRoiReady += Vm_EditorRoiReady; // 编辑载入 → 基底形状回注宿主
                vm.EditorSceneReset += Vm_EditorSceneReset; // 换源图取景/新建向导 → 静默清宿主 ROI 集合
                vm.PropertyChanged += Vm_PropertyChanged;   // 批3：卡尺拖绘模式开关 → 回注/退场
            }
        }

        private void OnViewUnloaded(object sender, RoutedEventArgs e)
        {
            ImageHost.RoiCommitted -= ImageHost_RoiCommitted;
            ImageHost.RoiEdited -= ImageHost_RoiEdited;
            ImageHost.RoiRemoved -= ImageHost_RoiRemoved;
            ImageHost.RoisCleared -= ImageHost_RoisCleared;
            ImageHost.ViewToolChanged -= ImageHost_ViewToolChanged;
            ImageHost.BrushSketchChanged -= ImageHost_BrushSketchChanged;
            if (DataContext is TemplateManagerViewModel vm)
            {
                vm.EditorRoiReady -= Vm_EditorRoiReady;
                vm.EditorSceneReset -= Vm_EditorSceneReset;
                vm.PropertyChanged -= Vm_PropertyChanged;
            }
        }

        /// <summary>📏 卡尺拖绘模式开关联动（VM 里其它编辑模式/清场也会置 false）：进入回注宿主形状、退出收走形状</summary>
        private void Vm_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm == null) return;
            if (!string.Equals(e.PropertyName, nameof(TemplateManagerViewModel.CaliperDrawMode),
                    StringComparison.Ordinal)) return;
            if (vm.CaliperDrawMode)
            {
                EnterCaliperDrawSession(vm);
            }
            else if (_caliperSessionActive)
            {
                ExitCaliperDrawSession(vm);
            }
        }

        /// <summary>进入拖绘会话：宿主集合 = [基底(黄) + 全部卡尺行(青)]，行形状可点选拖动/手柄微调</summary>
        private void EnterCaliperDrawSession(TemplateManagerViewModel vm)
        {
            _caliperSessionActive = true;
            SyncCaliperShapesToHost(vm);
        }

        /// <summary>整组重同步（进入会话/拖绘新增后）：按当前行集合重建宿主形状与映射</summary>
        private void SyncCaliperShapesToHost(TemplateManagerViewModel vm)
        {
            _caliperShapeRows.Clear();
            var rois = new List<RoiShape>();
            var baseRoi = vm?.BuildHostBaseRoi();
            if (baseRoi != null)
            {
                baseRoi.ColorName = "yellow";
                rois.Add(baseRoi);
            }
            if (vm != null && vm.CaliperRows != null)
            {
                foreach (var row in vm.CaliperRows)
                {
                    var s = vm.BuildCaliperDrawRoi(row.Cal);
                    if (s == null) continue;
                    s.ColorName = "cyan";
                    rois.Add(s);
                    _caliperShapeRows[s] = row;
                }
            }
            ImageHost.ReplaceRois(rois); // 静默替换：不触发 RoiRemoved/RoisCleared（语义现场由 VM 自持）
        }

        /// <summary>退出拖绘会话：宿主只留基底（从 VM 状态重建），卡尺回到 VM 紫带静态预览</summary>
        private void ExitCaliperDrawSession(TemplateManagerViewModel vm)
        {
            _caliperSessionActive = false;
            _caliperShapeRows.Clear();
            var baseRoi = vm?.BuildHostBaseRoi();
            if (baseRoi != null)
            {
                baseRoi.ColorName = "yellow";
                ImageHost.ReplaceRois(new[] { baseRoi });
            }
            else
            {
                ImageHost.ReplaceRois(Array.Empty<RoiShape>());
            }
        }

        /// <summary>拖绘模式下宿主一笔提交：〰线/⭘圆 → 卡尺行；▭▣旋转矩形 → 四边卡尺行（批3）</summary>
        private void CommitCaliperDraw(RoiShape shape, TemplateManagerViewModel vm)
        {
            if (shape == null || vm == null) return;
            bool accepted = false;
            if (shape.Kind == RoiShapeKind.Line || shape.Kind == RoiShapeKind.Circle)
            {
                var row = vm.AddCaliperFromDrawn(shape);
                accepted = row != null;
            }
            else if (shape.Kind == RoiShapeKind.Rectangle2)
            {
                var rows = vm.AddRect2CalipersFromDrawn(shape);
                accepted = rows != null && rows.Count > 0;
            }
            else
            {
                vm.SetStatusText("📏 卡尺拖绘模式只收 〰线 / ⭘圆 / ▭▣旋转矩形；学习框等闭合形状请先退出拖绘再画（勾掉「图上拖绘/编辑卡尺」）");
            }
            // 无论成败，本次笔迹都不留在宿主做基底（行已收走/被拒）；成功后整组重同步（四边行也变可拖动形状）
            ImageHost.RemoveRoi(shape); // 拖绘态 RoiRemoved 守卫：非映射形状静默忽略，不会误动基底
            if (accepted)
            {
                SyncCaliperShapesToHost(vm);
            }
        }

        /// <summary>🖌 涂抹草绘广播（宿主按住采样/收笔/取消）→ VM 实时并入学习域版图（∪ 扩 / ∖ 抠洞）</summary>
        private void ImageHost_BrushSketchChanged(object sender, BrushSketchEventArgs e)
        {
            (DataContext as TemplateManagerViewModel)?.NotifyBrushSketch(e.Points, e.Radius, e.Down);
        }

        /// <summary>模板编辑载入完成 → 把基底 ROI（用户所画圆/旋转矩形等或退化矩形）回注宿主，
        /// 使 ROI 可点选/拖动/手柄编辑（宿主换帧清空后由 Background 排队保证时序）。</summary>
        private void Vm_EditorRoiReady(object sender, EventArgs e)
        {
            if (!(DataContext is TemplateManagerViewModel vm)) return;
            var hostRoi = vm.BuildHostBaseRoi();
            if (hostRoi != null)
            {
                ImageHost.ReplaceRois(new[] { hostRoi });
            }
        }

        /// <summary>换源图取景/新建向导 → 静默清宿主 ROI 集合（旧形状按旧图像素锚定，不留到新场景悬浮；
        /// 不触发 RoisCleared → 不会误清掩膜/特征语义现场）。基底形状本体由 VM 持有，编辑载入会重新回注。</summary>
        private void Vm_EditorSceneReset(object sender, EventArgs e)
        {
            ImageHost.ClearRoisSilently();
        }

        /// <summary>
        /// 视图工具条工具切换联动（2026-09-09 工具条化）：
        /// · 切到 🖌 涂抹画笔 = 掩膜语义一键直达 —— 自动开启【掩膜编辑中】（学习域预览 + 后续笔画收为掩膜），
        ///   并把掩膜面板滑杆半径同步给宿主（激活中的画笔所见即所得）。
        /// · 切到其它工具（Pointer/形状/…）不改变掩膜/特征模式：掩膜态下仍可用 📐▾ 矩形/圆画精确掩膜，
        ///   退出掩膜模式仍由「掩膜编辑中」勾选框控制（刻意不自动退，避免"画一个矩形排除区"被拆成两步）。
        /// </summary>
        private void ImageHost_ViewToolChanged(object sender, ViewToolChangedEventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm == null) return;
            if (string.Equals(e.Tool, "Brush", StringComparison.OrdinalIgnoreCase))
            {
                if (!vm.HasImage)
                {
                    vm.SetStatusText("请先载入源图（步骤 1），🖌 涂抹画笔才能作用于画面。");
                    return;
                }
                ImageHost.BrushRadiusPx = vm.MaskBrushRadius; // 与滑杆一致（工具条直点不经按钮）
                if (!vm.MaskEditVisible)
                {
                    vm.MaskEditVisible = true; // 自动进掩膜语义（与特征标注互斥由 VM 处理）
                }
                vm.SetStatusText($"🖌 涂抹已激活（半径 {vm.MaskBrushRadius:0}px，可拖滑杆实时调）：按住左键在图上刷——" +
                                 (vm.MaskAddMode ? "保留∪=涂哪学哪" : "排除∖=抠除干扰") +
                                 "；松开收笔一笔，可连涂 / 切 📐▾ 画精确掩膜，取消勾选「掩膜编辑中」结束");
            }
        }

        /// <summary>
        /// ROI 提交分发（三种语义互斥）：
        /// · 掩膜编辑中 → 每笔提交=一笔"学习掩膜"（保留∪/排除∖）；
        /// · 特征标注中 → 每笔提交=一个特征（⭕点取中心 / ▨面保留形状，P2）；
        /// · 默认 → 闭合有面积形状作为模板基底 ROI 写回 VM（学习框=所画形状；线段拒绝）。
        /// 掩膜/特征收笔后从宿主移除该形状（宿主只保留基底 ROI；视觉由 VM 叠加层描边）。
        /// </summary>
        private void ImageHost_RoiCommitted(object sender, RoiShapeEventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm == null) return;
            if (vm.MaskEditVisible && vm.HasImage)
            {
                if (vm.AddMaskStroke(e.Shape))
                {
                    ImageHost.RemoveRoi(e.Shape); // 已收为掩膜，宿主内不再保留（避免与基底 ROI 混淆）
                }
                return;
            }
            if (vm.FeatureEditVisible && vm.HasImage)
            {
                if (vm.AddFeatureStroke(e.Shape))
                {
                    ImageHost.RemoveRoi(e.Shape); // 已收为特征，宿主内不再保留
                }
                return;
            }
            // 🔷 搜索框框选模式：下一笔闭合形状=搜索框（仅 ▭ 平行矩形，收笔回填+退出）
            if (vm.SearchRoiDrawMode && vm.HasImage)
            {
                CommitSearchRoi(e.Shape, vm);
                return;
            }
            // 📏 卡尺拖绘模式：下一笔 〰线/⭘圆/▭▣旋转矩形 = 卡尺行（收笔即入行，见 CommitCaliperDraw）
            if (vm.CaliperDrawMode && vm.HasImage)
            {
                CommitCaliperDraw(e.Shape, vm);
                return;
            }
            ApplyRoiToVm(e.Shape, committed: true);
        }

        /// <summary>搜索框收笔：仅接受 ▭ 平行矩形 → 回填 VM 搜索框数值（橙色叠加）→ 移出宿主集合。
        /// 移除发生在退出框选模式【之前】，保证 RoiRemoved 事件命中"框选模式守卫"（不会误清基底 ROI）。</summary>
        private void CommitSearchRoi(RoiShape shape, TemplateManagerViewModel vm)
        {
            if (shape == null) return;
            if (shape.Kind != RoiShapeKind.Rectangle1)
            {
                vm.SetStatusText("搜索框请用 📐▾ 的 ▭ 平行矩形绘制（圆/椭圆/多边形不支持作为搜索区，学习框才支持任意形状）");
                return; // 保持框选模式，让用户重画
            }
            double r1 = Math.Min(shape.Row, shape.Row2);
            double c1 = Math.Min(shape.Col, shape.Col2);
            double r2 = Math.Max(shape.Row, shape.Row2);
            double c2 = Math.Max(shape.Col, shape.Col2);
            bool ok = vm.ApplySearchRoiBounds(r1, c1, r2, c2);
            ImageHost.RemoveRoi(shape); // SearchRoiDrawMode 仍为 true → RoiRemoved 守卫跳过基底同步
            if (ok)
            {
                vm.SearchRoiDrawMode = false; // 收笔即退出（setter 会给"已退出"文案，随后覆盖）
                vm.SetStatusText($"🔍 搜索框已框选: R({r1:0.0},{c1:0.0})~({r2:0.0},{c2:0.0})（橙框=匹配搜索范围；再框一次或手填数值可调整）");
            }
        }

        /// <summary>已提交 ROI 被拖动/变形/改色后同步 VM（黄框实时跟随，形状几何原样保留）。
        /// 📏 卡尺拖绘态：命中的青色形状=某卡尺行 → 按新几何回写行（锚点转相对偏移），不误写基底 ROI。</summary>
        private void ImageHost_RoiEdited(object sender, RoiShapeEventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm == null) return;
            if (vm.CaliperDrawMode && e.Shape != null && _caliperShapeRows.TryGetValue(e.Shape, out var row))
            {
                vm.UpdateCaliperFromHostRoi(row, e.Shape);
                return;
            }
            ApplyRoiToVm(e.Shape, committed: false);
        }

        /// <summary>宿主单条 ROI 被删除（右键菜单/Delete）：基底没了则连掩膜/特征一并清；
        /// 仍有其它形状则以最上层形状重新折算基底。掩膜/特征态的"收笔自移除"不参与基底同步。
        /// 📏 卡尺拖绘态：删青色形状=删对应卡尺行；基底被删且宿主已空 → 同步清 VM（拖绘现场随 ClearRoi 退出）。</summary>
        private void ImageHost_RoiRemoved(object sender, RoiShapeEventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            // 掩膜/特征/搜索框框选的"收笔自移除"不参与基底同步（守卫在模式退出前命中，见 CommitSearchRoi）
            if (vm == null || vm.MaskEditVisible || vm.FeatureEditVisible || vm.SearchRoiDrawMode) return;
            if (vm.CaliperDrawMode)
            {
                if (e.Shape != null && _caliperShapeRows.TryGetValue(e.Shape, out var row))
                {
                    _caliperShapeRows.Remove(e.Shape);
                    vm.RemoveCaliperRow(row); // 宿主形状已移除，行也删（拖绘态紫带让位，无需重绘）
                }
                else if (ImageHost.Rois.Count == 0)
                {
                    vm.ClearRoi(); // 基底被删净：数值/形状/掩膜/卡尺一并清（ClearCaliperAssets 内会退出拖绘态）
                }
                return;
            }
            if (ImageHost.Rois.Count == 0)
            {
                vm.ClearRoi();
            }
            else
            {
                var top = ImageHost.Rois[ImageHost.Rois.Count - 1]; // 集合顺序=绘制顺序，末位=最上层
                ApplyRoiToVm(top, committed: false);
            }
        }

        /// <summary>
        /// 模板基底 ROI 写回（v2 语义）：任意**闭合有面积**形状作为学习框写入 VM ——
        /// 数值槽 RoiRow1..Col2 存其外接矩形（内部窗口/回显坐标），形状本体由 vm.SetRoiShape 原样保存
        /// （学习域=形状∩窗口，掩膜/创建链同口径）；线段/涂抹无面积被拒并提示。committed=true 给反馈。
        /// </summary>
        private void ApplyRoiToVm(RoiShape shape, bool committed)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm == null || !vm.HasImage || shape == null) return;
            if (!TryGetRoiAxisBounds(shape, out double r1, out double c1, out double r2, out double c2))
            {
                if (committed && shape.Kind == RoiShapeKind.Line)
                {
                    vm.SetStatusText("线段无面积，不能作为模板学习框；请用 ▭ 矩形 / ⭘ 圆 / ⬭ 椭圆 / ⬠ 多边形 等闭合形状框选目标");
                }
                else if (committed && shape.Kind == RoiShapeKind.Brush)
                {
                    vm.SetStatusText("🖌 涂抹只在【掩膜编辑中】收为掩膜笔画；点工具条 🖌 即自动开启掩膜编辑，或勾选「掩膜编辑中」后再刷");
                }
                return;
            }
            vm.RoiRow1 = r1;
            vm.RoiCol1 = c1;
            vm.RoiRow2 = r2;
            vm.RoiCol2 = c2;
            vm.SetRoiShape(shape); // v2：保存"用户所画形状"为学习框（非矩形不再只留外接矩形）
            vm.NotifyRoiSelected(shape.ColorName); // 黄框跟随宿主当前轮廓色（改色联动）
            if (committed && shape.Kind != RoiShapeKind.Rectangle1)
            {
                vm.SetStatusText($"ROI 已框选: Row {r1:0.0} ~ {r2:0.0}, Col {c1:0.0} ~ {c2:0.0}（{ShapeKindName(shape.Kind)}：学习框=所画形状，外接矩形仅作窗口）");
            }
        }

        /// <summary>RoiShape → 轴对齐外接矩形（图像像素坐标，行向下为正）。返回 false=无面积形状/无效。</summary>
        private static bool TryGetRoiAxisBounds(RoiShape s, out double r1, out double c1, out double r2, out double c2)
        {
            r1 = c1 = r2 = c2 = 0;
            if (s == null) return false;
            switch (s.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    r1 = Math.Min(s.Row, s.Row2);
                    c1 = Math.Min(s.Col, s.Col2);
                    r2 = Math.Max(s.Row, s.Row2);
                    c2 = Math.Max(s.Col, s.Col2);
                    break;
                case RoiShapeKind.Circle: // Row/Col=圆心, Radius1=半径
                    r1 = s.Row - s.Radius1;
                    c1 = s.Col - s.Radius1;
                    r2 = s.Row + s.Radius1;
                    c2 = s.Col + s.Radius1;
                    break;
                case RoiShapeKind.Ellipse:   // Radius1/Radius2=主轴(沿Phi)/副轴半长
                case RoiShapeKind.Rectangle2: // Length1/Length2=半长(沿Phi)/半宽
                {
                    double a = s.Kind == RoiShapeKind.Ellipse ? s.Radius1 : s.Length1;
                    double b = s.Kind == RoiShapeKind.Ellipse ? s.Radius2 : s.Length2;
                    double sa = Math.Abs(Math.Sin(s.Phi));
                    double ca = Math.Abs(Math.Cos(s.Phi));
                    double hRow = sa * a + ca * b; // 行向投影半宽
                    double hCol = ca * a + sa * b; // 列向投影半宽
                    r1 = s.Row - hRow;
                    c1 = s.Col - hCol;
                    r2 = s.Row + hRow;
                    c2 = s.Col + hCol;
                    break;
                }
                case RoiShapeKind.Polygon: // 顶点 X=col, Y=row
                    if (s.Polygon == null || s.Polygon.Length == 0) return false;
                    r1 = r2 = s.Polygon[0].Y;
                    c1 = c2 = s.Polygon[0].X;
                    for (int i = 1; i < s.Polygon.Length; i++)
                    {
                        r1 = Math.Min(r1, s.Polygon[i].Y);
                        c1 = Math.Min(c1, s.Polygon[i].X);
                        r2 = Math.Max(r2, s.Polygon[i].Y);
                        c2 = Math.Max(c2, s.Polygon[i].X);
                    }
                    break;
                default:
                    return false; // Line 无面积
            }
            // 图像边界收敛 + 最小尺寸校验（宿主 FinalizeRoi 已填 ImageWidth/Height）
            if (s.ImageHeight > 0)
            {
                r1 = Math.Max(0, r1);
                r2 = Math.Min(s.ImageHeight, r2);
            }
            if (s.ImageWidth > 0)
            {
                c1 = Math.Max(0, c1);
                c2 = Math.Min(s.ImageWidth, c2);
            }
            return r2 - r1 >= 1 && c2 - c1 >= 1;
        }

        /// <summary>形状中文名（状态栏折算反馈用）</summary>
        private static string ShapeKindName(RoiShapeKind kind)
        {
            switch (kind)
            {
                case RoiShapeKind.Circle: return "圆形";
                case RoiShapeKind.Ellipse: return "椭圆";
                case RoiShapeKind.Rectangle2: return "旋转矩形";
                case RoiShapeKind.Polygon: return "多边形";
                default: return kind.ToString();
            }
        }

        /// <summary>宿主 ROI 集合被清空（工具条 🧹）：掩膜编辑中视为"清空掩膜"、特征标注中视为"清空特征"
        /// （基底 ROI 由 VM 保留，两态收笔不常驻宿主）；卡尺拖绘中=清空卡尺与基底（ClearRoi 全清语义，
        /// 内部会退出拖绘态并触发形状退场）；否则为"清除基底 ROI"（数值/黄框/掩膜/特征一并清空）</summary>
        private void ImageHost_RoisCleared(object sender, EventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm?.MaskEditVisible == true)
            {
                vm.ClearMaskStrokes(true);
            }
            else if (vm?.FeatureEditVisible == true)
            {
                vm.ClearFeatureStrokes(true);
            }
            else if (vm?.CaliperDrawMode == true)
            {
                _caliperShapeRows.Clear();
                vm.ClearRoi();
            }
            else
            {
                vm?.ClearRoi();
            }
        }

        private void BtnClearRoi_Click(object sender, RoutedEventArgs e)
        {
            // 本页按钮与工具条 🧹 语义一致：都清 VM 数值与宿主 ROI 集合
            (DataContext as TemplateManagerViewModel)?.ClearRoi();
            ImageHost.ClearRois();
        }

        // ==================== 🔍 搜索框（模板资产"在哪找"，2026-09-09） ====================

        /// <summary>「🖱 框选」勾选：进入搜索框框选模式并把视图工具切到 ▭ 矩形（所见即所得，下一笔即搜索框）</summary>
        private void ChkSearchRoiDraw_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm == null) return;
            var chk = sender as CheckBox;
            if (chk?.IsChecked == true)
            {
                if (!vm.HasImage)
                {
                    vm.SetStatusText("请先载入源图，才能框选搜索框");
                    chk.IsChecked = false;
                    return;
                }
                ImageHost.EnterShapeTool("Rectangle1"); // 工具条切 ▭：左键拖出→右键完成=搜索框
            }
        }

        /// <summary>「⛶ 整图」：搜索框一键恢复整图范围（不限制搜索）</summary>
        private void BtnSearchRoiFull_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as TemplateManagerViewModel)?.SearchRoiSetToFull();
        }

        /// <summary>「✖ 清除」：禁用并清空搜索框（回归不限制搜索）</summary>
        private void BtnSearchRoiClear_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as TemplateManagerViewModel)?.SearchRoiClear();
        }

        /// <summary>关闭体检报告卡</summary>
        private void BtnCloseQualityReport_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as TemplateManagerViewModel)?.HideQualityReport();
        }

        // ==================== 📐 测量卡尺 + 基准点（P1 2026-09-09） ====================

        /// <summary>「✨ 生成建议卡尺」：按工件形状+当前学习框自动布卡尺（矩形四边/圆形整环），紫色测量带即刻上屏</summary>
        private void BtnSuggestCalipers_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm == null) return;
            if (!vm.HasImage)
            {
                vm.SetStatusText("请先载入源图并框选学习框（步骤 1~2），才能按形状生成卡尺");
                return;
            }
            vm.GenerateCaliperSuggest();
        }

        /// <summary>「🧹 清空卡尺」：全部卡尺行删除（显式基准点保留）。
        /// 拖绘态下宿主青色形状同步退场（基底保留，模式继续可用——清完可重画）。</summary>
        private void BtnClearCalipers_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm == null) return;
            vm.ClearCalipersAll();
            if (vm.CaliperDrawMode)
            {
                // 清空后整组重同步：宿主只剩基底（映射随行集合重建，天然清掉已删形状）
                SyncCaliperShapesToHost(vm);
                vm.SetStatusText("已清空全部卡尺行：可继续在图上拖绘新卡尺，或勾掉开关退出");
            }
        }

        /// <summary>「↺ 复位 0,0」：基准点回到自动（创建时锚点=学习域中心/ROI 中心）</summary>
        private void BtnDatumAuto_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as TemplateManagerViewModel)?.DatumResetToAuto();
        }

        /// <summary>行内「✖」：删除单条卡尺（Button 的 DataContext=行模型）。
        /// 拖绘态下该行对应的宿主青色形状一并移除（RemoveRoi → RoiRemoved → 删行，此处只删宿主形状）。</summary>
        private void BtnRemoveCaliper_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is TemplateManagerViewModel vm)) return;
            if (!((sender as FrameworkElement)?.DataContext is TemplateManagerViewModel.CaliperRowItem row)) return;
            if (vm.CaliperDrawMode)
            {
                // 找该行对应的宿主形状 → RemoveRoi（事件内删行+清映射）；找不到（未回注）直接删行
                RoiShape hit = null;
                foreach (var kv in _caliperShapeRows)
                {
                    if (ReferenceEquals(kv.Value, row)) { hit = kv.Key; break; }
                }
                if (hit != null)
                {
                    ImageHost.RemoveRoi(hit);
                }
                else
                {
                    vm.RemoveCaliperRow(row);
                }
                return;
            }
            vm.RemoveCaliperRow(row);
        }

        /// <summary>「📏 图上拖绘/编辑卡尺」开关：勾选进入拖绘会话（行回注宿主 + 新笔即卡尺）；
        /// 取消退出（形状退场回紫带预览）。具体会话由 VM 属性变更联动（Vm_PropertyChanged），此处仅做前置校验。</summary>
        private void ChkCaliperDraw_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm == null) return;
            var chk = sender as CheckBox;
            if (chk != null && chk.IsChecked == true)
            {
                if (!vm.HasImage || !vm.HasRoiWindow)
                {
                    vm.SetStatusText(vm.HasImage
                        ? "请先框选学习框（ROI）再进入卡尺拖绘：卡尺偏移以锚点（显式基准点或学习框中心）为原点"
                        : "请先载入源图并框选学习框（ROI），才能进入卡尺拖绘/编辑");
                    chk.IsChecked = false; // 回弹（binding 会把 VM 拖绘模式同步关掉）
                    return;
                }
                vm.SetStatusText("📏 卡尺拖绘/编辑已开启：视图工具条选 〰线/⭘圆/▭▣旋转矩形 在图上拖出即卡尺（左键拖出→右键完成）；青色形状点选可拖动/拉手柄微调；行内 ✖ / Delete 删除；勾掉开关退出");
            }
        }

        // ==================== 属性抽屉 收起/展开（2026-09-09 右抽屉布局） ====================

        /// <summary>抽屉收起/展开：收起 → DrawerPanel 折叠、DrawerCol 列宽归 0（图像区吃满剩余）；
        /// 同时主区头部「⚙ 展开面板」按钮现身（否则收起按钮随抽屉一起消失，抽屉就再也打不开了）；
        /// 展开 → 恢复（DrawerCol=Auto 由 DrawerPanel 定宽撑开），展开按钮隐退。
        /// 抽屉内宽度由 DrawerPanel.Width 记忆，GridSplitter 拖宽后同样由 DrawerPanel 实际宽度决定。</summary>
        private bool _drawerVisible = true;
        private void BtnToggleDrawer_Click(object sender, RoutedEventArgs e)
        {
            _drawerVisible = !_drawerVisible;
            DrawerPanel.Visibility = _drawerVisible ? Visibility.Visible : Visibility.Collapsed;
            DrawerCol.Width = _drawerVisible
                ? new GridLength(1, GridUnitType.Auto)
                : new GridLength(0);
            if (BtnExpandDrawer != null)
            {
                // 收起状态抽屉内按钮不可达 → 主区头部常驻展开入口；展开后隐藏避免双按钮
                BtnExpandDrawer.Visibility = _drawerVisible ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        // ==================== P1 分步引导（2026-09-09） ====================

        /// <summary>步骤条点击：跳转到对应引导步骤（0 选源图 ~ 4 参数与创建）。</summary>
        private void StepBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string tag && int.TryParse(tag, out int step))
            {
                var vm = DataContext as TemplateManagerViewModel;
                if (vm == null) return;
                vm.WizardStep = step;
            }
        }

        /// <summary>掩膜画笔滑杆（MaskBrushRadius）变化：实时同步宿主 BrushRadiusPx ——
        /// 工具条 🖌 激活中拖动滑杆，下一笔/草绘预览即刻按新半径（所见即所得，无需重新激活）。</summary>
        private void MaskBrushSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (e.NewValue >= 1 && ImageHost != null)
            {
                ImageHost.BrushRadiusPx = e.NewValue;
            }
        }
    }
}
