using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HalconDotNet;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Imaging;

namespace VisualCalibTool.Controls
{
    /// <summary>
    /// 标定专用 HALCON 显示控件。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 设计要点（都是踩过坑换来的，改之前先想清楚）
    /// ══════════════════════════════════════════════════════════════════
    /// ① <b>公共面里不出现任何 halcondotnet 类型</b>：外部一律通过 <c>byte[]</c> 送图、
    ///    通过 <see cref="ICalibOverlayTarget"/> 画叠加。这样 XAML 编译器（ReflectionOnly）
    ///    不会因为公共 API 去加载 halcondotnet 而报 MC1000，视图模型也能离线单测。
    /// ② <b>叠加必须画进 HALCON 窗口层</b>：叠在这个控件上方的 WPF 元素不参与合成（看不见）。
    /// ③ <b>内置交互全部关掉</b>（HZoomContent=Off / HMoveContent=false），缩放平移由本控件
    ///    按 part 自行实现 —— 内置缩放会清窗重绘，和自写 part 一起用就是"缩放打架"。
    /// ④ 放大/缩小都以 <b>鼠标指针为锚点</b>：指针下的那个图像点在缩放后仍停在指针位置。
    /// </summary>
    public partial class HalconView : UserControl, ICalibOverlayTarget, ICalibImageSurface, IRoiPickSurface
    {
        // ── 交互参数 ──
        private const double WheelZoomStep = 1.15;      // 每格滚轮的缩放比例
        private const double MinPartSpan = 16.0;        // part 最小跨度（像素）→ 限制最大放大倍数
        private const double MaxPartSpanFactor = 8.0;   // part 最大跨度 = 图像长边 × 该系数
        private const double MinRoiSpanPx = 3.0;        // 小于这个跨度的"框"视为误点，不提交

        private HWindow _hWindow;
        private HImage _image;

        private readonly List<OverlayEntry> _overlay = new List<OverlayEntry>();
        private string _currentColor = "green";
        private int _currentLineWidth = 2;

        // 当前 part（图像坐标，row/col）
        private double _partR1, _partC1, _partR2, _partC2;

        private bool _dragging;
        private double _dragLastX;
        private double _dragLastY;

        // ── 框选模式（模板示教"框选"那一步）──
        // ★ 橡皮筋矩形独立于 _overlay 列表：叠加层归视图模型管（ResetOverlay 会清），
        //   框选是控件自己的交互痕迹，混进去就会被业务绘制一把抹掉。
        private bool _roiPickMode;
        private bool _roiDrawing;
        private double _roiStartR, _roiStartC;
        private double _roiLiveR1, _roiLiveC1, _roiLiveR2, _roiLiveC2;
        private bool _roiLiveActive;

        /// <summary>用户拖完一个框（松开左键）。图像坐标，已归一化（r1&lt;r2、c1&lt;c2）。</summary>
        public event Action<double, double, double, double> RoiPicked;

        public HalconView()
        {
            // ★ 独立启动时 App 的静态构造已经挂过一次；被宿主嵌入时没人挂 —— 这里兜底（幂等）。
            //   必须在 InitializeComponent 之前：XAML 里的 HSmartWindowControlWPF 一实例化
            //   就可能去加载原生库，那时再挂就晚了。
            HalconRuntime.EnsureNativeLibrary();
            InitializeComponent();
            Unloaded += HalconView_Unloaded;
        }

        /// <summary>光标移动（图像坐标 row, col）。"指哪打哪"依赖它。</summary>
        public event Action<double, double> CursorPixelMoved;

        /// <summary>
        /// HALCON 原生库不可用（缺 <c>halcon.dll</c>）时触发一次，参数是给人看的原因与处置办法。
        /// ★ 订阅方应当<b>提示</b>而不是让它冒出去 —— 这个异常原本会从 Loaded 里把整个程序带走。
        /// </summary>
        public event Action<string> HalconUnavailable;

        /// <summary>无图像时画在窗口中央的提示（必须画在 HALCON 层，见设计要点 ②）。</summary>
        public string EmptyHintText = "无图像";

        /// <summary>是否在底图左下角显示图像尺寸与缩放倍率。</summary>
        public bool ShowImageInfo = true;

        #region 公开面（零 halcondotnet 类型）

        /// <summary>窗口是否已就绪。</summary>
        public bool IsReady
        {
            get
            {
                if (_hWindow == null)
                {
                    return false;
                }

                try
                {
                    return _hWindow.IsInitialized();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public bool HasImage
        {
            get { return _image != null; }
        }

        /// <summary>叠加绘制入口（视图模型只认这个接口）。</summary>
        public ICalibOverlayTarget Overlay
        {
            get { return this; }
        }

        /// <summary>
        /// <see cref="ICalibImageSurface"/> 的实现：显示一帧 8 位灰度裸帧。
        /// 视图模型走的就是这个入口，它拿到的是 <c>byte[]</c>，全程不出现 halcondotnet 类型。
        /// </summary>
        public void ShowFrame(byte[] rawGray, int width, int height, bool fit)
        {
            if (rawGray == null)
            {
                ClearImage();
                return;
            }

            if (!EnsureHalconForImage())
            {
                return;
            }

            SetImageFromRawGray(rawGray, width, height, fit);
        }

        /// <summary>送一帧 8 位灰度裸帧并显示。</summary>
        public void SetImageFromRawGray(byte[] raw, int width, int height, bool fit = true)
        {
            if (!EnsureHalconForImage())
            {
                return;
            }

            SetImageInternal(HalconFrameSource.FromRawGray(raw, width, height), fit);
        }

        /// <summary>HALCON 原生库是否可用（缺库时本控件只提示、不抛异常）。</summary>
        public bool IsHalconAvailable
        {
            get { return HalconRuntime.IsAvailable; }
        }

        /// <summary>一行状态：可用时给路径，不可用时给原因。</summary>
        public string HalconStatusText
        {
            get { return HalconRuntime.StatusText; }
        }

        private bool _halconMissingReported;

        /// <summary>
        /// 建 HImage 之前统一过一道：原生库不在就<b>降级</b>，绝不把 DllNotFoundException 抛出去。
        /// 现场要的是"照着做就能修好"的一句话，不是一个英文堆栈。
        /// </summary>
        private bool EnsureHalconForImage()
        {
            if (HalconRuntime.IsAvailable)
            {
                return true;
            }

            if (!_halconMissingReported)
            {
                _halconMissingReported = true;
                Action<string> h = HalconUnavailable;
                if (h != null)
                {
                    h(HalconRuntime.FailureReason);
                }
            }

            return false;
        }

        /// <summary>送一帧三通道交错 BGR 裸帧并显示。</summary>
        public void SetImageFromRawBgr(byte[] raw, int width, int height, bool fit = true)
        {
            SetImageInternal(HalconFrameSource.FromRawBgr(raw, width, height), fit);
        }

        /// <summary>
        /// 从磁盘读图并显示（离线排查 / 会话回放）。
        /// </summary>
        public void SetImageFromFile(string path, bool fit = true)
        {
            SetImageInternal(HalconFrameSource.ReadImage(path), fit);
        }

        /// <summary>清空图像（保留窗口）。</summary>
        public void ClearImage()
        {
            var old = _image;
            _image = null;
            _overlay.Clear();
            if (old != null)
            {
                try
                {
                    old.Dispose();
                }
                catch (Exception)
                {
                }
            }

            Replay();
        }

        /// <summary>把当前帧存盘（留档 / 出报告用）。</summary>
        public bool TrySaveCurrentFrame(string path)
        {
            return _image != null && HalconFrameSource.TrySaveImage(_image, path);
        }

        /// <summary>把当前帧编码成 PNG 字节。</summary>
        public byte[] TryEncodeCurrentFramePng()
        {
            return _image == null ? null : HalconFrameSource.TryEncodePng(_image);
        }

        /// <summary>当前 part（图像坐标），供诊断与"像素↔世界"换算显示。</summary>
        public void GetVisiblePart(out double row1, out double col1, out double row2, out double col2)
        {
            row1 = _partR1;
            col1 = _partC1;
            row2 = _partR2;
            col2 = _partC2;
        }

        /// <summary>适配整图（含留白保持纵横比）。</summary>
        public void FitImage()
        {
            FitImageCore();
            Replay();
        }

        /// <summary>
        /// 进入 / 退出框选模式（<see cref="IRoiPickSurface"/>）。
        /// 进入后左键拖拽 = 画框（平移让位；缩放仍走滚轮），光标变十字。
        /// 退出时清掉框选痕迹，平移恢复。
        /// </summary>
        public void SetRoiPickMode(bool active)
        {
            _roiPickMode = active;
            _roiDrawing = false;
            if (!active)
            {
                _roiLiveActive = false;
            }

            // ★ 光标是"现在能干什么"最直接的提示：进了框选模式却还是箭头，
            //   用户无从知道"可以拖框了"。
            Cursor = active ? Cursors.Cross : null;
            Replay();
        }

        #endregion

        #region 框选交互（IRoiPickSurface 内部）

        private void RoiMouseDown(double winX, double winY)
        {
            double row;
            double col;
            WindowToImage(winX, winY, out row, out col);
            _roiDrawing = true;
            _roiStartR = row;
            _roiStartC = col;
            _roiLiveR1 = row;
            _roiLiveC1 = col;
            _roiLiveR2 = row;
            _roiLiveC2 = col;
            _roiLiveActive = true;
        }

        private void RoiMouseMove(double winX, double winY)
        {
            if (!_roiDrawing)
            {
                return;
            }

            double row;
            double col;
            WindowToImage(winX, winY, out row, out col);
            _roiLiveR1 = Math.Min(_roiStartR, row);
            _roiLiveC1 = Math.Min(_roiStartC, col);
            _roiLiveR2 = Math.Max(_roiStartR, row);
            _roiLiveC2 = Math.Max(_roiStartC, col);
            Replay();
        }

        private void RoiMouseUp()
        {
            if (!_roiDrawing)
            {
                return;
            }

            _roiDrawing = false;

            // ★ 太小的框 = 误点：不留痕迹、不触发事件（训练那边 6×6 起步，3×3 以下没有意义）
            if (_roiLiveR2 - _roiLiveR1 < MinRoiSpanPx || _roiLiveC2 - _roiLiveC1 < MinRoiSpanPx)
            {
                _roiLiveActive = false;
                Replay();
                return;
            }

            Replay();

            Action<double, double, double, double> handler = RoiPicked;
            if (handler != null)
            {
                handler(_roiLiveR1, _roiLiveC1, _roiLiveR2, _roiLiveC2);
            }
        }

        /// <summary>把当前橡皮筋框画出来（在叠加层之后，保证压在业务绘制上面）。</summary>
        private void PaintLiveRoi()
        {
            if (!_roiLiveActive)
            {
                return;
            }

            try
            {
                _hWindow.SetColor("orange");
                _hWindow.SetLineWidth(2);
                _hWindow.DispRectangle1(_roiLiveR1, _roiLiveC1, _roiLiveR2, _roiLiveC2);
                _hWindow.DispText(
                    string.Format(CultureInfo.InvariantCulture, " {0:F0}x{1:F0}",
                        _roiLiveC2 - _roiLiveC1, _roiLiveR2 - _roiLiveR1),
                    "image", (int)Math.Round(_roiLiveR1), (int)Math.Round(_roiLiveC2),
                    "orange", new HTuple("box"), new HTuple("false"));
            }
            catch (HalconException)
            {
            }
        }

        #endregion

        #region 内部图像接管

        /// <summary>
        /// 接管一张 HALCON 图像的所有权（旧图会被释放）。
        /// ★ 只允许内部/同程序集调用：一旦这个签名变 public，halcondotnet 就进了公共 API（见设计要点 ①）。
        /// </summary>
        internal void SetImageInternal(HImage image, bool fit)
        {
            var old = _image;
            _image = image;
            _overlay.Clear();

            if (old != null && !ReferenceEquals(old, image))
            {
                try
                {
                    old.Dispose();
                }
                catch (Exception)
                {
                }
            }

            if (fit)
            {
                FitImageCore();
            }

            Replay();
        }

        #endregion

        #region HALCON 窗口生命周期

        private void SmartWindow_HInitWindow(object sender, EventArgs e)
        {
            _hWindow = SmartWindow.HalconWindow;

            // ★ 设计要点 ③：内置交互全部关掉，缩放/平移由本控件接管
            SmartWindow.HMoveContent = false;
            SmartWindow.HZoomContent = HSmartWindowControlWPF.ZoomContent.Off;

            try
            {
                _hWindow.SetDraw("margin");
                _hWindow.SetLineWidth(_currentLineWidth);
            }
            catch (HalconException)
            {
            }

            if (_image != null)
            {
                FitImageCore();
            }

            Replay();
        }

        private void HalconView_Unloaded(object sender, RoutedEventArgs e)
        {
            // 控件卸载时释放图像句柄；窗口句柄由 HSmartWindowControlWPF 自己管
            var img = _image;
            _image = null;
            if (img != null)
            {
                try
                {
                    img.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private void SmartWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 尺寸变化：按新纵横比重新适配，再重放场景（否则画面会被拉伸或留黑边不对）
            FitImageCore();
            Replay();
        }

        #endregion

        #region 缩放 / 平移（自实现 part）

        private bool TryGetImageSize(out double width, out double height)
        {
            width = 0.0;
            height = 0.0;

            if (_image == null)
            {
                return false;
            }

            int w;
            int h;
            if (!HalconFrameSource.TryGetSize(_image, out w, out h))
            {
                return false;
            }

            width = w;
            height = h;
            return true;
        }

        private void FitImageCore()
        {
            double iw;
            double ih;
            if (!TryGetImageSize(out iw, out ih))
            {
                return;
            }

            double cw = Math.Max(1.0, RootGrid.ActualWidth);
            double ch = Math.Max(1.0, RootGrid.ActualHeight);

            // 取较大的比例，保证整图都能装进画布（letterbox）
            double scale = Math.Max(iw / cw, ih / ch);
            double spanW = cw * scale;
            double spanH = ch * scale;

            double cy = ih / 2.0;
            double cx = iw / 2.0;

            ApplyPartCore(cy - spanH / 2.0, cx - spanW / 2.0, cy + spanH / 2.0, cx + spanW / 2.0);
        }

        private void ApplyPartCore(double r1, double c1, double r2, double c2)
        {
            if (r2 <= r1 || c2 <= c1)
            {
                return;
            }

            double iw;
            double ih;
            TryGetImageSize(out iw, out ih);
            double maxSpan = Math.Max(iw, ih) * MaxPartSpanFactor;

            double rSpan = r2 - r1;
            double cSpan = c2 - c1;
            if (rSpan < MinPartSpan || cSpan < MinPartSpan || rSpan > maxSpan || cSpan > maxSpan)
            {
                return;   // 超出缩放上下限时忽略本次操作
            }

            _partR1 = r1;
            _partC1 = c1;
            _partR2 = r2;
            _partC2 = c2;
        }

        private void ZoomAt(double factor, double winXDip, double winYDip)
        {
            if (_partR2 <= _partR1 || _partC2 <= _partC1)
            {
                FitImageCore();
            }

            double cw = Math.Max(1.0, RootGrid.ActualWidth);
            double ch = Math.Max(1.0, RootGrid.ActualHeight);
            double fx = Clamp01(winXDip / cw);
            double fy = Clamp01(winYDip / ch);

            double rSpan = (_partR2 - _partR1) * factor;
            double cSpan = (_partC2 - _partC1) * factor;

            // 指针下的图像点保持不动
            double rPivot = _partR1 + fy * (_partR2 - _partR1);
            double cPivot = _partC1 + fx * (_partC2 - _partC1);

            ApplyPartCore(
                rPivot - fy * rSpan, cPivot - fx * cSpan,
                rPivot + (1.0 - fy) * rSpan, cPivot + (1.0 - fx) * cSpan);

            Replay();
        }

        private void PanByDip(double dxDip, double dyDip)
        {
            double cw = Math.Max(1.0, RootGrid.ActualWidth);
            double ch = Math.Max(1.0, RootGrid.ActualHeight);

            double dCol = dxDip / cw * (_partC2 - _partC1);
            double dRow = dyDip / ch * (_partR2 - _partR1);

            ApplyPartCore(_partR1 - dRow, _partC1 - dCol, _partR2 - dRow, _partC2 - dCol);
            Replay();
        }

        private static double Clamp01(double v)
        {
            if (v < 0.0)
            {
                return 0.0;
            }

            return v > 1.0 ? 1.0 : v;
        }

        /// <summary>窗口坐标（DIP，相对控件左上角）→ 图像坐标（row, col）。</summary>
        private void WindowToImage(double winXDip, double winYDip, out double row, out double col)
        {
            double cw = Math.Max(1.0, RootGrid.ActualWidth);
            double ch = Math.Max(1.0, RootGrid.ActualHeight);

            col = _partC1 + Clamp01(winXDip / cw) * (_partC2 - _partC1);
            row = _partR1 + Clamp01(winYDip / ch) * (_partR2 - _partR1);
        }

        private void SmartWindow_HMouseWheel(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
        {
            // 前滚放大 = part 变小
            double factor = e.Delta > 0 ? 1.0 / WheelZoomStep : WheelZoomStep;
            ZoomAt(factor, e.X, e.Y);
        }

        private void SmartWindow_HMouseDown(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed)
            {
                // ★ 框选模式优先：左键拖拽 = 画框，平移让位（缩放仍走滚轮）。
                //   两种手势抢同一个键又不分主次，就是"拖一下既动了图又出了框"的打架来源。
                if (_roiPickMode)
                {
                    RoiMouseDown(e.X, e.Y);
                    return;
                }

                _dragging = true;
                _dragLastX = e.X;
                _dragLastY = e.Y;
            }
        }

        private void SmartWindow_HMouseMove(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
        {
            // 光标像素读数（"指哪打哪"）
            double row;
            double col;
            WindowToImage(e.X, e.Y, out row, out col);

            var handler = CursorPixelMoved;
            if (handler != null)
            {
                handler(row, col);
            }

            if (_roiDrawing)
            {
                RoiMouseMove(e.X, e.Y);
                return;
            }

            if (_dragging && Mouse.LeftButton == MouseButtonState.Pressed)
            {
                PanByDip(e.X - _dragLastX, e.Y - _dragLastY);
                _dragLastX = e.X;
                _dragLastY = e.Y;
            }
        }

        private void SmartWindow_HMouseUp(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
        {
            if (_roiDrawing)
            {
                RoiMouseUp();
                return;
            }

            _dragging = false;
        }

        #endregion

        #region 场景重放

        /// <summary>重放：清窗 → 设 part → 底图 → 叠加。缩放/平移/尺寸变化后都要走一次。</summary>
        private void Replay()
        {
            if (!IsReady)
            {
                return;
            }

            try
            {
                _hWindow.ClearWindow();

                if (_image != null && _partR2 > _partR1 && _partC2 > _partC1)
                {
                    _hWindow.SetPart(
                        (int)Math.Floor(_partR1), (int)Math.Floor(_partC1),
                        (int)Math.Ceiling(_partR2), (int)Math.Ceiling(_partC2));

                    _hWindow.DispObj(_image);

                    if (ShowImageInfo)
                    {
                        PaintImageInfo();
                    }
                }
                else
                {
                    PaintEmptyHint();
                }

                PaintOverlay();
                PaintLiveRoi();
            }
            catch (HalconException)
            {
                // 窗口重建/释放的竞态：忽略，下一次重放会补上
            }
        }

        private void PaintEmptyHint()
        {
            double iw;
            double ih;
            if (!TryGetImageSize(out iw, out ih))
            {
                return;
            }

            double row = (_partR1 + _partR2) / 2.0;
            double col = (_partC1 + _partC2) / 2.0;

            try
            {
                _hWindow.SetColor("gray");
                _hWindow.DispText(
                    EmptyHintText, "image",
                    (int)Math.Round(row), (int)Math.Round(col),
                    "gray", new HTuple("box"), new HTuple("false"));
            }
            catch (HalconException)
            {
            }
        }

        private void PaintImageInfo()
        {
            double iw;
            double ih;
            if (!TryGetImageSize(out iw, out ih))
            {
                return;
            }

            double zoom = iw / Math.Max(1e-9, _partC2 - _partC1);
            string info = string.Format(
                CultureInfo.InvariantCulture,
                "{0}x{1}  zoom {2:F2}x",
                (int)iw, (int)ih, zoom);

            try
            {
                _hWindow.SetColor("green");
                _hWindow.DispText(
                    info, "window", 4, 4, "green",
                    new HTuple("box"), new HTuple("true"));
            }
            catch (HalconException)
            {
            }
        }

        #endregion

        #region ICalibOverlayTarget

        public void ResetOverlay()
        {
            _overlay.Clear();
            Replay();
        }

        public void SetColor(string color)
        {
            if (!string.IsNullOrEmpty(color))
            {
                _currentColor = color;
            }
        }

        public void SetLineWidth(int width)
        {
            _currentLineWidth = width < 1 ? 1 : width;
        }

        public void DrawCross(double row, double col, double size, double angleDeg = 0.0)
        {
            Add(new OverlayEntry { Kind = OverlayKind.Cross, A = row, B = col, C = size, E = angleDeg });
        }

        public void DrawCircle(double row, double col, double radius)
        {
            Add(new OverlayEntry { Kind = OverlayKind.Circle, A = row, B = col, C = radius });
        }

        public void DrawLine(double row1, double col1, double row2, double col2)
        {
            Add(new OverlayEntry { Kind = OverlayKind.Line, A = row1, B = col1, C = row2, D = col2 });
        }

        public void DrawArrow(double row1, double col1, double row2, double col2, double size = 8.0)
        {
            Add(new OverlayEntry { Kind = OverlayKind.Arrow, A = row1, B = col1, C = row2, D = col2, E = size });
        }

        public void DrawRectangle(double row1, double col1, double row2, double col2)
        {
            Add(new OverlayEntry { Kind = OverlayKind.Rectangle, A = row1, B = col1, C = row2, D = col2 });
        }

        public void DrawText(double row, double col, string text)
        {
            Add(new OverlayEntry { Kind = OverlayKind.Text, A = row, B = col, Text = text });
        }

        public void DrawPolyline(double[] rows, double[] cols, bool closed = false)
        {
            if (rows == null || cols == null || rows.Length < 2 || rows.Length != cols.Length)
            {
                return;
            }

            Add(new OverlayEntry { Kind = OverlayKind.Polyline, Rows = rows, Cols = cols, Closed = closed });
        }

        public void Redraw()
        {
            Replay();
        }

        private void Add(OverlayEntry entry)
        {
            entry.Color = _currentColor;
            entry.LineWidth = _currentLineWidth;
            _overlay.Add(entry);

            if (IsReady)
            {
                try
                {
                    PaintEntry(entry);
                }
                catch (HalconException)
                {
                }
            }
        }

        private void PaintOverlay()
        {
            for (int i = 0; i < _overlay.Count; i++)
            {
                try
                {
                    PaintEntry(_overlay[i]);
                }
                catch (HalconException)
                {
                    // 单条画不出来不影响其余
                }
            }
        }

        private void PaintEntry(OverlayEntry e)
        {
            _hWindow.SetColor(e.Color);
            _hWindow.SetLineWidth(e.LineWidth);

            switch (e.Kind)
            {
                case OverlayKind.Cross:
                    _hWindow.DispCross(e.A, e.B, e.C, e.E);
                    break;

                case OverlayKind.Circle:
                    _hWindow.DispCircle(e.A, e.B, e.C);
                    break;

                case OverlayKind.Line:
                    _hWindow.DispLine(e.A, e.B, e.C, e.D);
                    break;

                case OverlayKind.Arrow:
                    _hWindow.DispArrow(e.A, e.B, e.C, e.D, e.E);
                    break;

                case OverlayKind.Rectangle:
                    _hWindow.DispRectangle1(e.A, e.B, e.C, e.D);
                    break;

                case OverlayKind.Text:
                    _hWindow.DispText(
                        e.Text ?? string.Empty, "image",
                        (int)Math.Round(e.A), (int)Math.Round(e.B),
                        e.Color, new HTuple("box"), new HTuple("false"));
                    break;

                case OverlayKind.Polyline:
                    PaintPolyline(e);
                    break;
            }
        }

        private void PaintPolyline(OverlayEntry e)
        {
            HObject contour = null;
            try
            {
                double[] rows = e.Rows;
                double[] cols = e.Cols;

                if (e.Closed && rows.Length >= 3)
                {
                    // 闭合多边形：把首点补到末尾（HALCON 的多边形轮廓本身不闭合）
                    var r2 = new double[rows.Length + 1];
                    var c2 = new double[cols.Length + 1];
                    Array.Copy(rows, r2, rows.Length);
                    Array.Copy(cols, c2, cols.Length);
                    r2[rows.Length] = rows[0];
                    c2[cols.Length] = cols[0];
                    rows = r2;
                    cols = c2;
                }

                // ★ 不存在 GenRectangle1ContourXld；多边形轮廓一律走 GenContourPolygonXld
                HOperatorSet.GenContourPolygonXld(
                    out contour,
                    new HTuple(rows),
                    new HTuple(cols));

                _hWindow.DispObj(contour);
            }
            finally
            {
                if (contour != null)
                {
                    try
                    {
                        contour.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        #endregion

        private enum OverlayKind
        {
            Cross,
            Circle,
            Line,
            Arrow,
            Rectangle,
            Text,
            Polyline
        }

        /// <summary>叠加条目：存"画什么"而不是"画完的样子"，缩放/平移后可以原样重放。</summary>
        private sealed class OverlayEntry
        {
            public OverlayKind Kind;
            public string Color;
            public int LineWidth;

            /// <summary>通用数值（含义随 Kind，见各 Draw* 方法）。</summary>
            public double A;
            public double B;
            public double C;
            public double D;
            public double E;

            public string Text;
            public double[] Rows;
            public double[] Cols;
            public bool Closed;
        }
    }
}
