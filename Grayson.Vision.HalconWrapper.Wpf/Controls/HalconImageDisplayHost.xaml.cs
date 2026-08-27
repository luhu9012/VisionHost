using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Grayson.Vision.HalconWrapper.Wpf.Controls
{
    /// <summary>
    /// Halcon图像渲染宿主控件
    /// 封装Halcon HWindow窗口，实现图像渲染、自适应缩放、鼠标坐标采集、VM渲染事件订阅
    /// 实现IImageDisplayHost统一图像显示宿主接口，供上层流程画布绑定使用
    /// </summary>
    public partial class HalconImageDisplayHost : UserControl, IImageDisplayHost
    {
        /// <summary>Halcon底层窗口句柄，用于绘制图像、绘制叠加轮廓</summary>
        private HWindow _hWindow;

        /// <summary>图像渲染服务，封装Halcon图像绘制、自适应填充逻辑</summary>
        private readonly IImageRenderService _renderService;

        /// <summary>缓存当前绑定的图像ViewModel，用于事件解绑防止内存泄漏</summary>
        private ImageDisplayVm _boundVm;

        /// <summary>窗口是否初始化完成（拥有有效Halcon句柄）</summary>
        public bool IsReady => _hWindow != null;

        /// <summary>
        /// 内部获取 Halcon 底层窗口句柄（仅限同程序集内部使用）。
        /// 注意：不能暴露为 public —— 该成员返回 halcondotnet 的 HWindow 类型，
        /// 一旦进入公共 API，XAML 编译器（ReflectionOnly 模式）解析本控件类型时
        /// 就必须加载 halcondotnet，进而触发其 .NET 2.0/3.5 依赖解析（本机缺失
        /// PresentationCore 3.0 等），导致 MC1000 构建错误。internal 成员不会被
        /// XAML 编译器解析，安全。
        /// </summary>
        internal HWindow GetDisplayWindow() => _hWindow;

        /// <summary>鼠标在图像上移动时触发事件，传递鼠标像素坐标</summary>
        public event EventHandler<CursorPixelEventArgs> CursorPixelMoved;

        #region 依赖属性：RenderContext 绑定图像渲染上下文
        /// <summary>
        /// 可绑定依赖属性：图像渲染上下文（包含图像、绘制叠加层、节点信息）
        /// 支持XAML双向绑定，切换图像时自动触发渲染
        /// </summary>
        public static readonly DependencyProperty RenderContextProperty =
            DependencyProperty.Register(
                nameof(RenderContext),
                typeof(ImageRenderContext),
                typeof(HalconImageDisplayHost),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnRenderContextChanged // 上下文变更回调
                ));

        /// <summary>当前绑定的图像渲染上下文</summary>
        public ImageRenderContext RenderContext
        {
            get => (ImageRenderContext)GetValue(RenderContextProperty);
            set => SetValue(RenderContextProperty, value);
        }
        #endregion

        public HalconImageDisplayHost()
        {
            InitializeComponent();
            // 实例化Halcon图像渲染工具服务
            _renderService = new HalconImageRenderService();

            // 监听控件DataContext切换，自动订阅/解绑VM渲染事件，避免内存泄漏
            this.DataContextChanged += HalconImageDisplayHost_DataContextChanged;

            // 🌟 订阅 SmartWindow 缩放/平移/尺寸变化事件：
            // HSmartWindowControlWPF 交互（滚轮缩放 / 拖动平移 / 双击适配 / 窗口尺寸变化）
            // 过程中不重绘任何已显示对象（窗口内容会被清掉），由本控件在事件后整体
            // 重放场景（底图 + 叠加层），保证画面始终完整。
            SmartWindow.HMouseWheel += SmartWindow_HMouseWheel;
            SmartWindow.HMouseUp += SmartWindow_HMouseUp;
            SmartWindow.HMouseMove += SmartWindow_HMouseMove;
            SmartWindow.HMouseDoubleClick += SmartWindow_HMouseDoubleClick;
            SmartWindow.SizeChanged += SmartWindow_SizeChanged;

            // 🌟 关闭 SmartWindow 内置拖动平移（HMoveContent=true 时其内部 HShiftWindowContents
            // 会清窗并只重绘控件自己管理的内容，与场景重放互相打架 → 拖动闪屏）。
            // 平移由本控件接管：HMouseMove 拖动时平移窗口 part + 整体重放场景（见 SmartWindow_HMouseMove）。
            SmartWindow.HMoveContent = false;

            // 控件卸载时释放场景托管对象，防止 Halcon 句柄泄漏
            this.Unloaded += (s, e) => RunOnUiSync(ClearScene);
        }

        #region HDevelop 式场景绘制（走一步画一步，交互后整体重放）

        /// <summary>场景条目基类：Draw 自带完整绘制状态（颜色/线宽/模式），重放结果确定</summary>
        private abstract class SceneItem
        {
            public string Color;
            public int LineWidth = 1;
            /// <summary>把本条目绘制到窗口（窗口 part 决定映射，无需关心缩放平移）</summary>
            public abstract void Draw(HWindow window);
        }

        /// <summary>对象条目：region / XLD / 图像。Owned=true 时由显示层负责释放</summary>
        private sealed class ObjectItem : SceneItem
        {
            public HObject Obj;
            public bool Owned;
            public override void Draw(HWindow window)
            {
                if (Obj == null || !Obj.IsInitialized()) return;
                if (Color != null)
                {
                    window.SetColor(Color);
                    window.SetLineWidth(LineWidth);
                    window.SetDraw("margin");
                }
                else
                {
                    window.SetDraw("fill");
                }
                HOperatorSet.DispObj(Obj, window);
            }
        }

        /// <summary>文本条目（image 坐标系，跟随缩放平移）</summary>
        private sealed class TextItem : SceneItem
        {
            public string Text;
            public double Row, Col;
            public override void Draw(HWindow window)
            {
                HOperatorSet.DispText(window, Text, "image", Row, Col, Color ?? "white", "box", "false");
            }
        }

        /// <summary>十字标记条目（特征点中心）</summary>
        private sealed class CrossItem : SceneItem
        {
            public double Row, Col, Size;
            public override void Draw(HWindow window)
            {
                window.SetColor(Color ?? "yellow");
                window.SetLineWidth(LineWidth);
                HOperatorSet.DispCross(window, Row, Col, Size, 0.785398);
            }
        }

        /// <summary>圆条目（拟合圆等）</summary>
        private sealed class CircleItem : SceneItem
        {
            public double Row, Col, Radius;
            public override void Draw(HWindow window)
            {
                window.SetColor(Color ?? "red");
                window.SetLineWidth(LineWidth);
                HOperatorSet.DispCircle(window, Row, Col, Radius);
            }
        }

        /// <summary>当前场景条目（按添加顺序累积）。仅 UI 线程访问。</summary>
        private readonly List<SceneItem> _scene = new List<SceneItem>();

        /// <summary>场景底图（AddBorrowed 的图像，用于 Display 渲染任务的同帧判断）</summary>
        private HObject _sceneBaseImage;

        /// <summary>拖动过程中的场景重放节流时间戳（30ms 一次）</summary>
        private DateTime _lastSceneRepaintUtc = DateTime.MinValue;

        /// <summary>拖动平移状态：本轮拖动是否已记录锚点（仅 UI 线程访问）</summary>
        private bool _panAnchorSet;
        /// <summary>拖动平移锚点（窗口像素坐标）</summary>
        private double _panAnchorX, _panAnchorY;

        /// <summary>开始新画面：清空场景（释放托管对象）并清空窗口</summary>
        internal void SceneBegin()
        {
            RunOnUiSync(() =>
            {
                ClearScene();
                if (_hWindow != null)
                {
                    try { _hWindow.ClearWindow(); } catch { }
                }
            });
        }

        /// <summary>提交对象（托管）并立即上屏。窗口不可用时对象就地释放，调用方无需关心</summary>
        internal void SceneAddObject(HObject obj, string color, int lineWidth)
        {
            if (obj == null) return;
            RunOnUiSync(() =>
            {
                if (!SafeAddAndDraw(new ObjectItem { Obj = obj, Color = color, LineWidth = lineWidth, Owned = true }))
                {
                    try { obj.Dispose(); } catch { }
                }
            });
        }

        /// <summary>提交借用对象（底图，不负责释放）并立即上屏，同时登记为场景底图</summary>
        internal void SceneAddBorrowed(HObject obj)
        {
            if (obj == null) return;
            RunOnUiSync(() =>
            {
                _sceneBaseImage = obj;
                SafeAddAndDraw(new ObjectItem { Obj = obj, Color = null, Owned = false });
            });
        }

        /// <summary>提交文本（image 坐标系）并立即上屏</summary>
        internal void SceneAddText(string text, double row, double col, string color)
        {
            if (string.IsNullOrEmpty(text)) return;
            RunOnUiSync(() => SafeAddAndDraw(new TextItem { Text = text, Row = row, Col = col, Color = color }));
        }

        /// <summary>提交十字标记并立即上屏</summary>
        internal void SceneAddCross(double row, double col, double size, string color)
        {
            RunOnUiSync(() => SafeAddAndDraw(new CrossItem { Row = row, Col = col, Size = size, Color = color }));
        }

        /// <summary>提交圆并立即上屏</summary>
        internal void SceneAddCircle(double row, double col, double radius, string color)
        {
            RunOnUiSync(() => SafeAddAndDraw(new CircleItem { Row = row, Col = col, Radius = radius, Color = color }));
        }

        /// <summary>追加条目并立即绘制（不清窗，叠加式）。返回 false 仅表示窗口不可用</summary>
        private bool SafeAddAndDraw(SceneItem item)
        {
            _scene.Add(item);
            if (_hWindow == null) return false;
            try { item.Draw(_hWindow); }
            catch (Exception ex)
            {
                // 单条绘制失败只记日志，条目仍留在场景里（交互重放时再尝试）
                LogBus.Warn("HalconHost", $"场景条目绘制失败（已跳过）: {ex.Message}");
            }
            return true;
        }

        /// <summary>清空场景并释放其中托管对象（借用对象不动）。仅 UI 线程调用</summary>
        private void ClearScene()
        {
            foreach (var item in _scene)
            {
                if (item is ObjectItem oi && oi.Owned && oi.Obj != null)
                {
                    try { oi.Obj.Dispose(); } catch { }
                }
            }
            _scene.Clear();
            _sceneBaseImage = null;
        }

        /// <summary>整体重放场景：清窗后按添加顺序逐条重画（窗口 part 已更新）。
        /// 批量刷新：先关闭 flush_graphic 逐算子上屏，全部画完再统一刷新一次，
        /// 消除"清窗→逐条重画"过程中间态可见造成的闪烁。</summary>
        private void RepaintScene()
        {
            if (_hWindow == null || _scene.Count == 0) return;
            try
            {
                bool flushDisabled = false;
                try
                {
                    HOperatorSet.SetSystem("flush_graphic", "false");
                    flushDisabled = true;
                }
                catch { /* 个别环境不支持该参数时降级为直接绘制 */ }

                try
                {
                    _hWindow.ClearWindow();
                    foreach (var item in _scene)
                    {
                        try { item.Draw(_hWindow); }
                        catch { /* 底图可能已被新帧替换释放：单条失败跳过，不影响其余条目 */ }
                    }
                }
                finally
                {
                    if (flushDisabled)
                    {
                        // 恢复的同时把缓冲的全部绘制一次性刷上屏
                        try { HOperatorSet.SetSystem("flush_graphic", "true"); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn("HalconHost", $"场景重放失败: {ex.Message}");
            }
        }

        /// <summary>用户交互（缩放/平移结束、双击、尺寸变化）后重放场景</summary>
        private void RequestSceneRepaint()
        {
            RunOnUiSync(RepaintScene);
        }

        /// <summary>把动作调度到 UI 线程同步执行（已在 UI 线程则直接执行，保证调用方顺序）</summary>
        private void RunOnUiSync(Action action)
        {
            if (action == null) return;
            var dispatcher = Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action, System.Windows.Threading.DispatcherPriority.Render);
            }
        }

        #endregion

        private void SmartWindow_HMouseWheel(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
        {
            RequestSceneRepaint();
        }

        private void SmartWindow_HMouseUp(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
        {
            _panAnchorSet = false; // 拖动结束，重置平移锚点
            RequestSceneRepaint();
        }

        private void SmartWindow_HMouseDoubleClick(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
        {
            // 双击适配（HDoubleClickToFitContent）会调整窗口 part，重放场景
            RequestSceneRepaint();
        }

        private void SmartWindow_HMouseMove(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
        {
            // 平移由本控件接管（SmartWindow.HMoveContent 已关闭，避免其内部清窗重绘与场景重放打架闪屏）：
            // 左键按住拖动 = 按窗口像素位移平移窗口 part，再整体重放场景，画面跟随鼠标平滑移动。
            if (System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                _panAnchorSet = false;
                return;
            }

            if (!_panAnchorSet)
            {
                // 按下后的第一次移动：只记录锚点不平移
                _panAnchorX = e.X;
                _panAnchorY = e.Y;
                _panAnchorSet = true;
                return;
            }

            // 30ms 节流；节流期间累积的位移留到下一次一起平移（锚点仅在真正平移后前移）
            var now = DateTime.UtcNow;
            if ((now - _lastSceneRepaintUtc).TotalMilliseconds < 30) return;
            _lastSceneRepaintUtc = now;

            double dx = e.X - _panAnchorX;
            double dy = e.Y - _panAnchorY;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
            _panAnchorX = e.X;
            _panAnchorY = e.Y;

            if (PanWindowByPixels(dx, dy))
            {
                RepaintScene();
            }
        }

        /// <summary>
        /// 按窗口像素位移平移窗口 part（拖动平移核心）。
        /// 鼠标向右拖 dx 像素 = 画面跟随右移 = 显示图像更左侧内容 → part 列区间左移。
        /// 只平移不缩放（part 尺寸不变），天然保持纵横比。
        /// </summary>
        private bool PanWindowByPixels(double dxPixels, double dyPixels)
        {
            if (_hWindow == null || SmartWindow == null) return false;
            if (SmartWindow.ActualWidth <= 0 || SmartWindow.ActualHeight <= 0) return false;
            try
            {
                _hWindow.GetPart(out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
                double scaleX = (c2.D - c1.D) / SmartWindow.ActualWidth;  // 每窗口像素对应多少图像列
                double scaleY = (r2.D - r1.D) / SmartWindow.ActualHeight; // 每窗口像素对应多少图像行
                double dCol = -dxPixels * scaleX;
                double dRow = -dyPixels * scaleY;
                _hWindow.SetPart(r1.D + dRow, c1.D + dCol, r2.D + dRow, c2.D + dCol);
                return true;
            }
            catch (Exception ex)
            {
                LogBus.Warn("HalconHost", $"拖动平移窗口 part 失败: {ex.Message}");
                return false;
            }
        }

        private void SmartWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RequestSceneRepaint();
        }

        /// <summary>
        /// DataContext变更回调：切换绑定的ImageDisplayVm时处理事件订阅
        /// 核心：先解绑旧VM渲染事件，再绑定新VM的渲染推送事件
        /// </summary>
        private void HalconImageDisplayHost_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // 1. 解绑上一个ViewModel的渲染事件，防止多次订阅、内存泄漏
            if (_boundVm != null)
            {
                _boundVm.OnRequestRender -= HandleRequestRender;
                LogBus.Debug("HalconHost", "已解绑旧 ImageDisplayVm.OnRequestRender 事件");
                _boundVm = null;
            }

            // 2. 新ViewModel绑定，订阅渲染推送事件
            if (e.NewValue is ImageDisplayVm newVm)
            {
                _boundVm = newVm;
                _boundVm.OnRequestRender += HandleRequestRender;
                //🌟 订阅自适应事件
                _boundVm.OnRequestFitImage += FitImage;
                LogBus.Info("HalconHost", "成功订阅 ImageDisplayVm.OnRequestRender 事件！");

                // 如果VM当前已有激活图像，控件窗口已就绪则立刻渲染
                if (newVm.ActiveImageContext != null)
                {
                    HandleRequestRender(newVm.ActiveImageContext);
                }
            }
        }

        /// <summary>
        /// 响应ViewModel主动发起的渲染请求
        /// VM切换图像、运行节点推送图像时会触发该回调
        /// </summary>
        /// <param name="context">待渲染图像上下文，null代表清空窗口</param>
        private void HandleRequestRender(ImageRenderContext context)
        {
            LogBus.Info("HalconHost", context == null
                ? "[OnRequestRender] 收到清空指令"
                : $"[OnRequestRender] 收到渲染请求，节点: [{context.NodeName}]");
            // 统一调用渲染方法
            Display(context);
        }

        /// <summary>
        /// RenderContext依赖属性变更静态回调
        /// XAML绑定的图像上下文切换时自动执行渲染
        /// </summary>
        private static void OnRenderContextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HalconImageDisplayHost host)
            {
                if (e.NewValue is ImageRenderContext context)
                {
                    LogBus.Info("HalconHost", $"[OnRenderContextChanged] 监听到 RenderContext 改变，节点名称: [{context.NodeName ?? "Unknown"}]，准备触发 Display(...)");
                    host.Display(context);
                }
                else
                {
                    LogBus.Warn("HalconHost", "[OnRenderContextChanged] 接收到 null 或非 ImageRenderContext 对象，跳过渲染。");
                }
            }
        }

        /// <summary>上一次渲染图像宽度，用于判断是否需要重新自适应窗口</summary>
        private int _lastImageWidth = 0;
        /// <summary>上一次渲染图像高度，用于判断是否需要重新自适应窗口</summary>
        private int _lastImageHeight = 0;

        /// <summary>
        /// 核心图像渲染方法：绘制图像+叠加轮廓，空上下文则清空窗口
        /// 全部调度至WPF渲染优先级线程执行，防止跨线程报错
        /// </summary>
        /// <param name="context">图像渲染上下文</param>
        public void Display(ImageRenderContext context)
        {
            // Halcon窗口未初始化，直接放弃渲染
            if (_hWindow == null)
            {
                LogBus.Warn("HalconHost", $"[Display] 跳过渲染 - HWindow 已准备: {_hWindow != null}");
                return;
            }

            // 切换至渲染线程执行绘制逻辑
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // 上下文为空 / 无图像：清空窗口与场景，重置尺寸缓存
                    if (context?.Image == null)
                    {
                        _hWindow.ClearWindow();
                        ClearScene();
                        _lastImageWidth = 0;
                        _lastImageHeight = 0;
                        LogBus.Debug("HalconHost", "[Display] 窗口与状态已成功清空。");
                        return;
                    }

                    // 🌟 场景协同（抓图流程时序：渲染任务在场景提交之后才执行）：
                    // - 渲染帧 ≠ 场景底图（新帧到达）：旧场景引用旧帧对象，作废清空；
                    // - 渲染帧 == 场景底图（同一帧补渲染）：渲染完底图后重放场景，把叠加层补回来。
                    var newImage = (context.Image as HalconRenderImage)?.HImage;
                    bool sameFrame = newImage != null && ReferenceEquals(newImage, _sceneBaseImage);
                    if (!sameFrame)
                    {
                        ClearScene();
                    }

                    // 1. 渲染原图 + 叠加绘图（ROI、检测框、文字等Overlay）
                    _renderService.RenderToWindow(_hWindow, context.Image, context.Overlays);
                    LogBus.Debug("HalconHost", $"[Display] 图像已成功 DispObj 到 HWindow (尺寸: {context.Image.Width}x{context.Image.Height})");

                    // 2. 同帧补渲染：渲染任务画了底图会盖住场景叠加层，重放补回
                    if (sameFrame && _scene.Count > 0)
                    {
                        RepaintScene();
                    }

                    // 2. 仅当图像分辨率变化时，执行窗口自适应填充（性能优化，避免重复缩放）
                    if (context.Image != null)
                    {
                        if (_lastImageWidth != context.Image.Width || _lastImageHeight != context.Image.Height)
                        {
                            _renderService.FitImageToWindow(_hWindow, context.Image);
                            SmartWindow?.SetFullImagePart();

                            // 更新尺寸缓存
                            _lastImageWidth = context.Image.Width;
                            _lastImageHeight = context.Image.Height;
                            LogBus.Info("HalconHost", $"[Display] 视口区域根据新图像尺寸 [{context.Image.Width}x{context.Image.Height}] 完成自适应调整。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Error("HalconHost", $"渲染主图失败: {ex.Message}", ex);
                }
            }, System.Windows.Threading.DispatcherPriority.Render); // 指定渲染优先级，UI刷新更顺滑
        }

        /// <summary>
        /// 手动触发图像自适应窗口铺满
        /// 供工具栏"适配图像"按钮调用
        /// </summary>
        public void FitImage()
        {
            // 优先取绑定的RenderContext，无则取ViewModel激活图像
            var activeContext = RenderContext ?? _boundVm?.ActiveImageContext;
            if (_hWindow != null && activeContext?.Image != null)
            {
                _renderService.FitImageToWindow(_hWindow, activeContext.Image);
                SmartWindow?.SetFullImagePart();
                LogBus.Debug("HalconHost", "手动触发了图像自适应窗口 (FitImage)。");
            }
        }

        /// <summary>
        /// Halcon底层窗口初始化完成回调
        /// 获取HWindow句柄，设置全局绘图样式，补发待渲染图像
        /// </summary>
        private void SmartWindow_HInitWindow(object sender, EventArgs e)
        {
            // 捕获Halcon原生窗口句柄
            _hWindow = SmartWindow.HalconWindow;
            // 绘图模式：图像边缘留白
            _hWindow.SetDraw("margin");
            // 默认线条宽度2像素
            _hWindow.SetLineWidth(2);

            LogBus.Info("HalconHost", "SmartWindow 句柄 HInitWindow 初始化完成！");

            // 窗口就绪后，补发当前待渲染图像
            var currentContext = RenderContext ?? _boundVm?.ActiveImageContext;
            if (currentContext != null)
            {
                LogBus.Info("HalconHost", "窗口准备完毕，开始补发渲染当前 Context...");
                Display(currentContext);
            }
            else
            {
                LogBus.Debug("HalconHost", "窗口准备完毕，当前无待渲染的 RenderContext。");
                // 窗口重建（控件重新加载）而场景仍有条目时直接重放，画面不丢
                if (_scene.Count > 0)
                {
                    RepaintScene();
                }
            }
        }

        /// <summary>
        /// 窗口鼠标移动事件
        /// 捕获鼠标坐标，向上抛出事件，同步更新ViewModel像素信息
        /// </summary>
        private void SmartWindow_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (SmartWindow == null || _hWindow == null) return;

            // 1. 判断是否按下了 Ctrl 键
            bool isCtrlPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;

            if (!isCtrlPressed) return;

            try
            {
                // 2. 获取当前图像上下文与尺寸
                var activeContext = RenderContext ?? _boundVm?.ActiveImageContext;
                if (activeContext?.Image == null) return;

                int imgWidth = activeContext.Image.Width;
                int imgHeight = activeContext.Image.Height;

                // 3. 🌟 关键点：优先尝试 Halcon 原生获取，若失败/不灵敏则用 Part 矩阵反算
                double row = 0, col = 0;
                bool getPosSuccess = false;

                try
                {
                    // 尝试直接获取（注意：Halcon 内部 Handle 在无 mouse-button 时可能抛异常）
                    _hWindow.GetMpositionSubPix(out row, out col, out _);
                    getPosSuccess = true;
                }
                catch
                {
                    // 如果 Halcon 抛异常，降级到 WPF 坐标 + HWindow Part 逆映射算法
                    var mousePos = e.GetPosition(SmartWindow);
                    if (SmartWindow.ActualWidth > 0 && SmartWindow.ActualHeight > 0)
                    {
                        // 获取当前窗口显示的图像 Part 区域 (row1, col1, row2, col2)
                        _hWindow.GetPart(out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);

                        double partWidth = c2.D - c1.D;
                        double partHeight = r2.D - r1.D;

                        // 算坐标比例 (WPF控件坐标 -> 图像Part真实坐标)
                        col = c1.D + (mousePos.X / SmartWindow.ActualWidth) * partWidth;
                        row = r1.D + (mousePos.Y / SmartWindow.ActualHeight) * partHeight;
                        getPosSuccess = true;
                    }
                }

                if (getPosSuccess)
                {
                    // 取整获取像素阵列的列(X)和行(Y)
                    int imgX = (int)Math.Floor(col);
                    int imgY = (int)Math.Floor(row);

                    // 4. 越界检查：基于图片 0,0 到 Width,Height 判定
                    if (imgX >= 0 && imgX < imgWidth && imgY >= 0 && imgY < imgHeight)
                    {
                        // 更新 ViewModel 中的 SelectedImageInfo 文本
                        if (DataContext is ImageDisplayVm vm)
                        {
                            vm.UpdateCursorPixelInfo(imgX, imgY);
                        }

                        CursorPixelMoved?.Invoke(this, new CursorPixelEventArgs { X = imgX, Y = imgY });
                    }
                }
            }
            catch (Exception ex)
            {
                LogBus.Debug("HalconHost", $"MouseMove 坐标转换微损: {ex.Message}");
            }
        }
        /// <summary>
        /// 监听外层 Grid 的 MouseMove，彻底绕过 HSmartWindowControlWPF 的 Win32 消息截断问题
        /// </summary>
        private void Grid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (SmartWindow == null || _hWindow == null) return; 

    // 1. 判断是否按下了 Ctrl 键
    bool isCtrlPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;

            if (!isCtrlPressed) return;

            try
            {
                // 2. 获取当前图像上下文
                var activeContext = RenderContext ?? _boundVm?.ActiveImageContext;
        if (activeContext?.Image == null) return; 

        int imgWidth = activeContext.Image.Width;
                int imgHeight = activeContext.Image.Height;

                // 3. 获取鼠标在 SmartWindow 控件上的实时 WPF 像素坐标
                var mousePos = e.GetPosition(SmartWindow);

                if (SmartWindow.ActualWidth > 0 && SmartWindow.ActualHeight > 0)
                {
                    // 4. 获取当前 Halcon 窗口显示的图像 Part 区域 (row1, col1, row2, col2)
                    _hWindow.GetPart(out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);

                    double partWidth = c2.D - c1.D;
                    double partHeight = r2.D - r1.D;

                    // 5. 将 WPF 相对坐标精准映射为图片像素坐标 (Col = X, Row = Y)
                    double col = c1.D + (mousePos.X / SmartWindow.ActualWidth) * partWidth;
                    double row = r1.D + (mousePos.Y / SmartWindow.ActualHeight) * partHeight;

                    int imgX = (int)Math.Floor(col);
                    int imgY = (int)Math.Floor(row);

                    // 6. 越界检查：只有在图像真实的 [0,0] ~ [Width, Height] 范围内才更新
                    if (imgX >= 0 && imgX < imgWidth && imgY >= 0 && imgY < imgHeight)
                    {
                        if (DataContext is ImageDisplayVm vm)
                        {
                            vm.UpdateCursorPixelInfo(imgX, imgY); 
                }

                        CursorPixelMoved?.Invoke(this, new CursorPixelEventArgs { X = imgX, Y = imgY }); 
            }
                }
            }
            catch (Exception ex)
            {
                // 忽略视口未准备好时的异常
            }
        }
    }
}