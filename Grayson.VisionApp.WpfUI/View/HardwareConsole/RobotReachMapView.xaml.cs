using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Model;
using Grayson.Vision.WpfUI.ViewModel.HardwareConsole;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Grayson.Vision.WpfUI.View.HardwareConsole
{
    /// <summary>
    /// 可达域与九点预演画布（2026-09-11 新增）。
    ///
    /// 【分层】PlotRoot 下四层，重绘代价从低到高分开，保证交互流畅：
    ///   _domain —— 背景网格 / 可达域填充 / 内外边界 / margin 安全线
    ///   _green  —— 可行基准位栅格位图（贵，仅参数或数据变化时重建）
    ///   _grid   —— 九点网格 + 基准位 + 每点判定（拖动基准位时只重绘这一层）
    ///   _live   —— 当前机器位置（250ms 轮询跟随，只更新这一层）
    ///
    /// 【为什么不自己算几何】所有判定都走 ReachMapGeometry / ViewModel，
    /// 本文件只做「模型 → 图元」的翻译，不含任何可达性逻辑。
    /// </summary>
    public partial class RobotReachMapView : UserControl
    {
        private RobotDebugViewModel _vm;
        private bool _layersReady;

        private readonly Canvas _domain = new Canvas();
        private readonly Canvas _green = new Canvas();
        private readonly Canvas _grid = new Canvas();
        private readonly Canvas _live = new Canvas();

        // 世界坐标 → 屏幕坐标变换（每次重绘按当前取景范围重算）
        private double _scale = 1;
        private double _padX, _padY;
        private double _bxMin = -450, _bxMax = 450, _byMin = -450, _byMax = 450;

        private bool _dragging;

        public RobotReachMapView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += (s, e) => { EnsureLayers(); RebuildAll(); };
        }

        // ==================================================================
        // 与 ViewModel 的连接
        // ==================================================================

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null) _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm = e.NewValue as RobotDebugViewModel;
            if (_vm != null) _vm.PropertyChanged += Vm_PropertyChanged;
            RebuildAll();
        }

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                // ---- 高频：只更新当前点那一层 ----
                case nameof(RobotDebugViewModel.PosX):
                case nameof(RobotDebugViewModel.PosY):
                    UpdateLive();
                    break;

                // ---- 拖动路径：绿区与可达域都没变，只重画九点 ----
                case nameof(RobotDebugViewModel.ReachBaseX):
                case nameof(RobotDebugViewModel.ReachBaseY):
                case nameof(RobotDebugViewModel.ReachVerdictText):
                    DrawGridLayer();
                    break;

                // ---- 数据或参数变化：全量重建（含绿区栅格）----
                case nameof(RobotDebugViewModel.ReachMap):
                case nameof(RobotDebugViewModel.ReachFeasible):
                case nameof(RobotDebugViewModel.ShowReachDomain):
                case nameof(RobotDebugViewModel.ShowMarginBand):
                case nameof(RobotDebugViewModel.ShowReachGrid):
                case nameof(RobotDebugViewModel.ShowFeasibleZone):
                    RebuildAll();
                    break;
            }
        }

        // ==================================================================
        // 分层初始化
        // ==================================================================

        private void EnsureLayers()
        {
            if (_layersReady) return;
            _layersReady = true;

            // 层叠顺序 = 添加顺序（后加的在上）
            _domain.IsHitTestVisible = false;
            _green.IsHitTestVisible = false;
            _grid.IsHitTestVisible = false;
            _live.IsHitTestVisible = false;

            PlotRoot.Children.Add(_domain);
            PlotRoot.Children.Add(_green);
            PlotRoot.Children.Add(_grid);
            PlotRoot.Children.Add(_live);

            SyncLayerSize();
            PlotRoot.SizeChanged += (s, e) => { SyncLayerSize(); RebuildAll(); };
        }

        private void SyncLayerSize()
        {
            double w = Math.Max(0, PlotRoot.ActualWidth);
            double h = Math.Max(0, PlotRoot.ActualHeight);
            _domain.Width = w; _domain.Height = h;
            _green.Width = w; _green.Height = h;
            _grid.Width = w; _grid.Height = h;
            _live.Width = w; _live.Height = h;
        }

        // ==================================================================
        // 坐标变换
        // ==================================================================

        private void RecalcTransform()
        {
            if (_vm != null && !_vm.ReachXMax.Equals(_vm.ReachXMin))
            {
                _bxMin = _vm.ReachXMin; _bxMax = _vm.ReachXMax;
                _byMin = _vm.ReachYMin; _byMax = _vm.ReachYMax;
            }
            else
            {
                _bxMin = -450; _bxMax = 450; _byMin = -450; _byMax = 450;
            }

            double w = Math.Max(1, PlotRoot.ActualWidth);
            double h = Math.Max(1, PlotRoot.ActualHeight);
            double spanX = Math.Max(1e-6, _bxMax - _bxMin);
            double spanY = Math.Max(1e-6, _byMax - _byMin);

            _scale = Math.Min(w / spanX, h / spanY);
            _padX = (w - spanX * _scale) / 2.0;
            _padY = (h - spanY * _scale) / 2.0;
        }

        private Point W2S(double wx, double wy)
            => new Point(_padX + (wx - _bxMin) * _scale, _padY + (_byMax - wy) * _scale);

        private void S2W(Point p, out double wx, out double wy)
        {
            wx = _bxMin + (p.X - _padX) / _scale;
            wy = _byMax - (p.Y - _padY) / _scale;
        }

        // ==================================================================
        // 全量重绘
        // ==================================================================

        private void RebuildAll()
        {
            if (!_layersReady) return;

            RecalcTransform();
            DrawDomainLayer();
            DrawGreenLayer();
            DrawGridLayer();
            UpdateLive();
            UpdateCaption();

            EmptyHint.Visibility = (_vm?.ReachMap == null) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCaption()
        {
            DataCaption.Text = _vm?.ReachMap == null
                ? string.Empty
                : _vm.ReachMap.Describe() + (_vm.ReachFeasible == null
                    ? string.Empty
                    : $"  绿区 {_vm.ReachFeasible.Count} 像素");
        }

        // ==================================================================
        // 第 1 层：背景网格 + 可达域 + margin 安全线
        // ==================================================================

        private void DrawDomainLayer()
        {
            _domain.Children.Clear();
            double w = PlotRoot.ActualWidth, h = PlotRoot.ActualHeight;
            if (w < 2 || h < 2) return;

            DrawBackgroundGrid(w, h);

            var map = _vm?.ReachMap;
            if (map == null || map.IsEmpty) return;

            var outerPts = BoundaryPoints(map, true, 0);
            var innerPts = BoundaryPoints(map, false, 0);

            if (_vm.ShowReachDomain)
            {
                // 环形填充：EvenOdd 规则下内环自动成为"洞"，无需反向绕序
                var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
                if (outerPts.Count >= 3) geo.Children.Add(MakePoly(outerPts));
                if (innerPts.Count >= 3) geo.Children.Add(MakePoly(innerPts));
                if (geo.Children.Count > 0)
                {
                    _domain.Children.Add(new Path
                    {
                        Data = geo,
                        Fill = new SolidColorBrush(Color.FromArgb(0x2E, 0x4F, 0xC3, 0xF7)),
                    });
                }

                if (outerPts.Count >= 3)
                    _domain.Children.Add(StrokePoly(outerPts, Color.FromRgb(0x4F, 0xC3, 0xF7), 1.8));
                if (innerPts.Count >= 3)
                    _domain.Children.Add(StrokePoly(innerPts, Color.FromRgb(0xE0, 0xA0, 0x30), 1.8));
            }

            // margin 安全线：内边界向外让 margin、外边界向内让 margin —— 两条线之间才是"九点可落脚"的带宽
            if (_vm.ShowMarginBand)
            {
                double mg = ParseDouble(_vm.ReachMarginMm, 10);
                var safeOuter = BoundaryPoints(map, true, -mg);
                var safeInner = BoundaryPoints(map, false, +mg);
                if (safeOuter.Count >= 3)
                    _domain.Children.Add(StrokePoly(safeOuter, Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 1.0, true));
                if (safeInner.Count >= 3)
                    _domain.Children.Add(StrokePoly(safeInner, Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 1.0, true));
            }

            // 基座原点
            var o = W2S(0, 0);
            _domain.Children.Add(MakeLine(o.X - 9, o.Y, o.X + 9, o.Y, Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), 1.2));
            _domain.Children.Add(MakeLine(o.X, o.Y - 9, o.X, o.Y + 9, Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), 1.2));
            var ot = MakeText("基座", 10, Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
            Canvas.SetLeft(ot, o.X + 8);
            Canvas.SetTop(ot, o.Y + 4);
            _domain.Children.Add(ot);
        }

        private void DrawBackgroundGrid(double w, double h)
        {
            // 50mm 一格；远小于 50mm 的视野会自动加粗到 100mm，避免糊成一片
            double span = Math.Max(_bxMax - _bxMin, _byMax - _byMin);
            double stepMm = 50;
            while (stepMm * _scale < 28) stepMm *= 2;

            var brush = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
            double x0 = Math.Ceiling(_bxMin / stepMm) * stepMm;
            for (double x = x0; x <= _bxMax; x += stepMm)
            {
                var p = W2S(x, 0);
                _domain.Children.Add(MakeLine(p.X, 0, p.X, h, brush.Color, 1));
            }
            double y0 = Math.Ceiling(_byMin / stepMm) * stepMm;
            for (double y = y0; y <= _byMax; y += stepMm)
            {
                var p = W2S(0, y);
                _domain.Children.Add(MakeLine(0, p.Y, w, p.Y, brush.Color, 1));
            }
        }

        /// <summary>
        /// 生成边界多边形顶点。marginDelta &gt; 0 表示内边界向外扩、marginDelta &lt; 0 表示外边界向内缩。
        /// 实测不可达（null）的方向被剔除 —— 画面上会少一段，配合警示文案提示存在缺口。
        /// </summary>
        private List<Point> BoundaryPoints(ReachMapData map, bool outer, double marginDelta)
        {
            var pts = new List<Point>();
            if (map == null) return pts;

            foreach (var d in map.Directions)
            {
                double? rr = outer ? d.ROut : d.RIn;
                if (!rr.HasValue) continue;                 // 该方向整体不可达
                double r = rr.Value + marginDelta;
                if (r <= 0) continue;
                double a = d.Deg * Math.PI / 180.0;
                pts.Add(W2S(r * Math.Cos(a), r * Math.Sin(a)));
            }
            return pts;
        }

        // ==================================================================
        // 第 2 层：可行基准位绿区
        // ==================================================================

        private void DrawGreenLayer()
        {
            _green.Children.Clear();
            var fb = _vm?.ReachFeasible;
            if (fb == null || fb.Bits == null || !_vm.ShowFeasibleZone) return;
            if (fb.Count == 0)
            {
                var t = MakeText("该步长/margin 下不存在可行基准位", 11.5, Color.FromRgb(0xE5, 0x39, 0x35));
                Canvas.SetLeft(t, 10);
                Canvas.SetTop(t, 10);
                _green.Children.Add(t);
                return;
            }

            int w = fb.W, h = fb.H;
            var px = new int[w * h];
            const int greenArgb = unchecked((int)0x6600E676);
            for (int i = 0; i < px.Length; i++) px[i] = fb.Bits[i] ? greenArgb : 0;

            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);

            var p0 = W2S(fb.XMin, fb.YMax);
            var img = new Image
            {
                Source = wb,
                Width = (fb.XMax - fb.XMin) * _scale,
                Height = (fb.YMax - fb.YMin) * _scale,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
            Canvas.SetLeft(img, p0.X);
            Canvas.SetTop(img, p0.Y);
            _green.Children.Add(img);

            // 最稳基准位：可行域最大内接圆圆心（离所有边界最远）
            if (fb.BestCenter.HasValue)
            {
                var c = W2S(fb.BestCenter.Value.X, fb.BestCenter.Value.Y);
                double rad = Math.Max(4, fb.BestClearanceMm * _scale);
                var ring = new Ellipse
                {
                    Width = rad * 2,
                    Height = rad * 2,
                    Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xD5, 0x4F)),
                    StrokeThickness = 1.2,
                    StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 }),
                };
                Canvas.SetLeft(ring, c.X - rad);
                Canvas.SetTop(ring, c.Y - rad);
                _green.Children.Add(ring);
            }
        }

        // ==================================================================
        // 第 3 层：九点网格 + 基准位
        // ==================================================================

        private void DrawGridLayer()
        {
            _grid.Children.Clear();
            if (_vm?.ReachMap == null || !_vm.ShowReachGrid) return;
            if (PlotRoot.ActualWidth < 2) return;

            double step = ParseDouble(_vm.ReachStepMm, 10);
            double margin = ParseDouble(_vm.ReachMarginMm, 10);
            double bx = ParseDouble(_vm.ReachBaseX, 0);
            double by = ParseDouble(_vm.ReachBaseY, 0);
            bool eih = _vm.ReachEyeModeIndex == 0;

            var pts = ReachMapGeometry.BuildGrid(bx, by, step, step,
                                                 _vm.ReachInvertX, _vm.ReachInvertY, eih);

            // ---- 网格连线（3 横 3 竖；Index 1~9 固定对应 3×3）----
            var lineBrush = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
            int[][] links =
            {
                // 横（每行三连）
                new[] { 0, 1 }, new[] { 1, 2 }, new[] { 3, 4 }, new[] { 4, 5 }, new[] { 6, 7 }, new[] { 7, 8 },
                // 竖（每列三连）
                new[] { 0, 3 }, new[] { 3, 6 }, new[] { 1, 4 }, new[] { 4, 7 }, new[] { 2, 5 }, new[] { 5, 8 },
            };
            foreach (var lk in links)
            {
                var a = W2S(pts[lk[0]].X, pts[lk[0]].Y);
                var b = W2S(pts[lk[1]].X, pts[lk[1]].Y);
                _grid.Children.Add(MakeLine(a.X, a.Y, b.X, b.Y, lineBrush, 1, true));
            }

            // ---- 九点 + 判定着色 ----
            var order = _vm.ReachTraverseOrder;   // 访问次序（Index 数组）
            for (int i = 0; i < 9; i++)
            {
                var v = ReachMapGeometry.Evaluate(_vm.ReachMap, pts[i].X, pts[i].Y, margin);
                var sp = W2S(pts[i].X, pts[i].Y);

                Color fill;
                switch (v.State)
                {
                    case ReachPointState.Safe: fill = Color.FromRgb(0x00, 0xE6, 0x76); break;
                    case ReachPointState.NearInner: fill = Color.FromRgb(0xFF, 0x98, 0x00); break;
                    case ReachPointState.NearOuter: fill = Color.FromRgb(0xE5, 0x39, 0x35); break;
                    case ReachPointState.Unsafe: fill = Color.FromRgb(0x7F, 0x1D, 0x1D); break;
                    default: fill = Color.FromRgb(0x9E, 0x9E, 0x9E); break;
                }

                bool isCenter = (i == 4);
                double dia = isCenter ? 15 : 12;
                var dot = new Ellipse
                {
                    Width = dia,
                    Height = dia,
                    Fill = new SolidColorBrush(fill),
                    Stroke = new SolidColorBrush(isCenter ? Colors.White : Color.FromArgb(0xCC, 0x00, 0x00, 0x00)),
                    StrokeThickness = isCenter ? 2 : 1,
                    ToolTip = $"第 {i + 1} 点 {pts[i]}\n{v.Text}\n走位次序: 第 {SeqOf(order, i + 1)} 个",
                };
                Canvas.SetLeft(dot, sp.X - dia / 2);
                Canvas.SetTop(dot, sp.Y - dia / 2);
                _grid.Children.Add(dot);

                // 点号（放在圆心）
                var num = MakeText((i + 1).ToString(), isCenter ? 10.5 : 9.5, Colors.White);
                num.FontWeight = FontWeights.Bold;
                num.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(num, sp.X - num.DesiredSize.Width / 2);
                Canvas.SetTop(num, sp.Y - num.DesiredSize.Height / 2);
                _grid.Children.Add(num);

                // 走位次序角标
                var seq = MakeText("#" + SeqOf(order, i + 1), 9, Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF));
                Canvas.SetLeft(seq, sp.X + dia / 2 + 1);
                Canvas.SetTop(seq, sp.Y + 2);
                _grid.Children.Add(seq);
            }

            // ---- 基准位标记（琥珀色）----
            var bsp = W2S(bx, by);
            var cross = new Ellipse
            {
                Width = 20, Height = 20,
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
                StrokeThickness = 1.6,
                ToolTip = $"基准位 ({bx:F2}, {by:F2}) —— 在画布上按住拖动可移动",
            };
            Canvas.SetLeft(cross, bsp.X - 10);
            Canvas.SetTop(cross, bsp.Y - 10);
            _grid.Children.Add(cross);
            _grid.Children.Add(MakeLine(bsp.X - 14, bsp.Y, bsp.X + 14, bsp.Y, Color.FromRgb(0xFF, 0xC1, 0x07), 1.2));
            _grid.Children.Add(MakeLine(bsp.X, bsp.Y - 14, bsp.X, bsp.Y + 14, Color.FromRgb(0xFF, 0xC1, 0x07), 1.2));
        }

        /// <summary>Index → 走位次序（第几个被访问）。数组顺序即访问顺序。</summary>
        private static int SeqOf(int[] order, int index)
        {
            if (order == null) return index;
            for (int i = 0; i < order.Length; i++) if (order[i] == index) return i + 1;
            return index;
        }

        // ==================================================================
        // 第 4 层：当前机器位置
        // ==================================================================

        private void UpdateLive()
        {
            _live.Children.Clear();
            if (_vm == null || PlotRoot.ActualWidth < 2) return;

            var p = W2S(_vm.PosX, _vm.PosY);
            var red = Color.FromRgb(0xFF, 0x3B, 0x30);
            _live.Children.Add(MakeLine(p.X - 11, p.Y, p.X + 11, p.Y, red, 1.6));
            _live.Children.Add(MakeLine(p.X, p.Y - 11, p.X, p.Y + 11, red, 1.6));

            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(red),
                StrokeThickness = 2,
            };
            Canvas.SetLeft(dot, p.X - 4);
            Canvas.SetTop(dot, p.Y - 4);
            _live.Children.Add(dot);

            var t = MakeText($"当前 ({_vm.PosX:F1}, {_vm.PosY:F1})", 10.5, Colors.White);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tx = p.X + 9;
            if (tx + t.DesiredSize.Width > PlotRoot.ActualWidth - 4)
                tx = p.X - 9 - t.DesiredSize.Width;
            Canvas.SetLeft(t, tx);
            Canvas.SetTop(t, p.Y - 18);
            _live.Children.Add(t);
        }

        // ==================================================================
        // 交互：拖动基准位
        // ==================================================================

        private void PlotRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_vm?.ReachMap == null) return;
            _dragging = true;
            PlotRoot.CaptureMouse();
            MoveBaseTo(e.GetPosition(PlotRoot));
        }

        private void PlotRoot_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            MoveBaseTo(e.GetPosition(PlotRoot));
        }

        private void PlotRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            PlotRoot.ReleaseMouseCapture();
        }

        private void PlotRoot_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_vm == null) return;
            _vm.ReachBaseX = _vm.PosX.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            _vm.ReachBaseY = _vm.PosY.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            e.Handled = true;
        }

        private void MoveBaseTo(Point screen)
        {
            double wx, wy;
            S2W(screen, out wx, out wy);
            _vm.ReachBaseX = wx.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            _vm.ReachBaseY = wy.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ==================================================================
        // 图元工厂
        // ==================================================================

        private static Line MakeLine(double x1, double y1, double x2, double y2, Color color, double thickness, bool dashed = false)
        {
            var ln = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = thickness,
                IsHitTestVisible = false,
            };
            if (dashed) ln.StrokeDashArray = new DoubleCollection(new[] { 3.0, 3.0 });
            return ln;
        }

        private static TextBlock MakeText(string text, double size, Color color)
            => new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = new SolidColorBrush(color),
                IsHitTestVisible = false,
            };

        private static Path StrokePoly(IList<Point> pts, Color color, double thickness, bool dashed = false)
        {
            var fig = new PathFigure { IsClosed = true, IsFilled = false, StartPoint = pts[0] };
            var seg = new PolyLineSegment();
            for (int i = 1; i < pts.Count; i++) seg.Points.Add(pts[i]);
            fig.Segments.Add(seg);
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var path = new Path
            {
                Data = geo,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = thickness,
                IsHitTestVisible = false,
            };
            if (dashed) path.StrokeDashArray = new DoubleCollection(new[] { 5.0, 4.0 });
            return path;
        }

        private static PathGeometry MakePoly(IList<Point> pts)
        {
            var fig = new PathFigure { IsClosed = true, IsFilled = true, StartPoint = pts[0] };
            var seg = new PolyLineSegment();
            for (int i = 1; i < pts.Count; i++) seg.Points.Add(pts[i]);
            fig.Segments.Add(seg);
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return geo;
        }

        /// <summary>容错解析：输入框里可能夹着全角字符、负号或半截输入，失败时回退到默认值</summary>
        private static double ParseDouble(string s, double fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            string t = s.Trim()
                        .Replace('。', '.').Replace('，', ',')
                        .Replace('．', '.').Replace('－', '-');
            double v;
            return double.TryParse(t, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
    }
}
