using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.HalconWrapper.Core;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using HalconDotNet;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;

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
        /// ★ 日志诊断标签：标识"这是哪一个显示窗口"。
        /// 存在原因（2026-09-15 踩坑）：显示层日志只带节点名，而编辑器主视图 / 节点属性面板 /
        /// 工位监视页可以同时存在多个 HalconImageDisplayHost —— 同一时刻多条
        /// 「收到渲染请求，节点: [形状匹配]」「收到清空指令」交织在一起，
        /// **判不出是谁画了、谁把谁清掉了**（曾据此误判成"渲染没执行"）。
        /// 由宿主在创建适配器的地方赋值（如「编辑器主视图」「节点属性面板」），
        /// 只影响日志文本，不参与任何业务判定。
        /// </summary>
        public string LogTag
        {
            get => _logTag;
            set
            {
                _logTag = string.IsNullOrWhiteSpace(value) ? "显示窗口" : value;
                if (_boundVm != null) _boundVm.LogTag = _logTag;
            }
        }
        private string _logTag = "显示窗口";

        /// <summary>日志前缀用的标签（永不为空）。⚠ 不可命名 Tag —— 会隐藏 FrameworkElement.Tag</summary>
        private string LogPrefix => _logTag;

        /// <summary>
        /// 是否允许左键拖动平移（默认 true）。
        /// 模板管理等需要在图像上左键框选 ROI 的场景，可在拖拽期间置 false 临时禁用平移，
        /// 松开鼠标后恢复 true（防止 ROI 框选与拖动平移互相冲突）。
        /// </summary>
        public bool IsPanEnabled { get; set; } = true;

        /// <summary>
        /// 内部获取 Halcon 底层窗口句柄（仅限同程序集内部使用）。
        /// 注意：不能暴露为 public —— 该成员返回 halcondotnet 的 HWindow 类型，
        /// 一旦进入公共 API，XAML 编译器（ReflectionOnly 模式）解析本控件类型时
        /// 就必须加载 halcondotnet，进而触发其 .NET 2.0/3.5 依赖解析（本机缺失
        /// PresentationCore 3.0 等），导致 MC1000 构建错误。internal 成员不会被
        /// XAML 编译器解析，安全。
        /// </summary>
        internal HWindow GetDisplayWindow() => _hWindow;

        #region 🌟 外部工具激活 API（向导/掩膜画笔用；工具枚举不对外泄漏）
        // ViewTool 为私有枚举，WpfUI 等外部宿主无法直接 SetActiveTool——
        // 以下命名方法把"按名激活绘制工具"做成公共面（模板分步向导/掩膜画笔面板调用）。
        // 切换后工具条按钮状态/光标/提示条与手动点选完全一致（SetActiveTool 统一同步）。

        /// <summary>涂抹画笔半径（图像像素，默认 14）。涂抹笔画/草绘实时预览均按此半径膨胀。</summary>
        public double BrushRadiusPx { get; set; } = 14.0;

        /// <summary>激活涂抹画笔工具（按住=涂抹，松开=收笔提交一笔 Brush，可连续涂抹；右键/Esc 退出）</summary>
        public bool EnterBrushTool(double brushRadiusPx)
        {
            if (brushRadiusPx >= 1) BrushRadiusPx = brushRadiusPx;
            if (_hWindow == null) return false;
            SetActiveTool(ViewTool.Brush);
            return true;
        }

        /// <summary>激活指定形状绘制工具。shape 取值：Rectangle1/Rectangle2/Circle/Ellipse/Polygon/Freehand/Brush/Line；
        /// 未知值返回 false（工具不变）。空串 = 回到 Pointer（等同 EnterPointerTool）。</summary>
        public bool EnterShapeTool(string shapeName)
        {
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                EnterPointerTool();
                return true;
            }
            switch (shapeName.Trim().ToLowerInvariant())
            {
                case "rectangle1": return ActivateToolIfReady(ViewTool.Rect1);
                case "rectangle2": return ActivateToolIfReady(ViewTool.Rect2);
                case "circle": return ActivateToolIfReady(ViewTool.Circle);
                case "ellipse": return ActivateToolIfReady(ViewTool.Ellipse);
                case "polygon": return ActivateToolIfReady(ViewTool.Polygon);
                case "freehand": return ActivateToolIfReady(ViewTool.Freehand);
                case "brush": return ActivateToolIfReady(ViewTool.Brush);
                case "line": return ActivateToolIfReady(ViewTool.Line);
                default: return false;
            }
        }

        /// <summary>切回"选择/浏览"模式（Pointer：滚轮锚点缩放 + 空白拖动平移 + 点选 ROI 编辑）</summary>
        public void EnterPointerTool()
        {
            if (_activeTool == ViewTool.Pointer) return;
            SetActiveTool(ViewTool.Pointer);
        }

        private bool ActivateToolIfReady(ViewTool tool)
        {
            if (_hWindow == null) return false;
            SetActiveTool(tool);
            return true;
        }
        #endregion

        /// <summary>鼠标在图像上移动时触发事件，传递鼠标像素坐标</summary>
        public event EventHandler<CursorPixelEventArgs> CursorPixelMoved;

        #region 🌟 内置视图工具条 API（依赖属性 / ROI 提交事件）

        /// <summary>
        /// 是否显示顶部视图工具条（默认 true）。嵌入极简场景（小缩略预览）可置 false。
        /// </summary>
        public static readonly DependencyProperty ShowToolbarProperty =
            DependencyProperty.Register(
                nameof(ShowToolbar),
                typeof(bool),
                typeof(HalconImageDisplayHost),
                new PropertyMetadata(true, OnShowToolbarChanged));

        public bool ShowToolbar
        {
            get => (bool)GetValue(ShowToolbarProperty);
            set => SetValue(ShowToolbarProperty, value);
        }

        /// <summary>
        /// 顶部被工具条占用的高度（只读，供外部覆盖层避让）。
        /// 2026-09-09：校验台/对针窗口在宿主之上盖了一层"透明点选覆盖层"，它会把工具条一起盖住——
        /// 鼠标移到工具条上仍然触发点选、按钮也点不动（"鼠标指定就乱了"）。
        /// 外部覆盖层把这个值作为上边距，即可把工具条区域让出来（悬停与点击都恢复正常）。
        /// </summary>
        public static readonly DependencyPropertyKey TopReservedHeightPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(TopReservedHeight),
                typeof(double),
                typeof(HalconImageDisplayHost),
                new PropertyMetadata(0d));

        public static readonly DependencyProperty TopReservedHeightProperty =
            TopReservedHeightPropertyKey.DependencyProperty;

        public double TopReservedHeight => (double)GetValue(TopReservedHeightProperty);

        private void RefreshTopReservedHeight()
        {
            double h = 0d;
            if (Toolbar != null && Toolbar.Visibility == Visibility.Visible && ShowToolbar)
            {
                h = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : Toolbar.MinHeight;
            }
            if (Math.Abs(TopReservedHeight - h) > 0.5)
            {
                SetValue(TopReservedHeightPropertyKey, h);
            }
        }

        /// <summary>
        /// 是否由控件自己绘制"已提交 ROI"的常驻轮廓（跟随缩放平移）。
        /// 默认 true：ROI 由本控件覆盖层绘制，宿主只需消费事件。
        /// 若宿主自行叠加 ROI（如模板管理页走 context.Overlays 画黄框），置 false
        /// 避免双框；此时 ROI 本体不可见但可点选，选中后仍显示高亮与手柄。
        /// </summary>
        public static readonly DependencyProperty PersistRoiInSceneProperty =
            DependencyProperty.Register(
                nameof(PersistRoiInScene),
                typeof(bool),
                typeof(HalconImageDisplayHost),
                new PropertyMetadata(true));

        public bool PersistRoiInScene
        {
            get => (bool)GetValue(PersistRoiInSceneProperty);
            set => SetValue(PersistRoiInSceneProperty, value);
        }

        /// <summary>新增 ROI 定稿（右键完成创建）时触发，参数为创建好的 RoiShape（图像像素坐标）</summary>
        public event EventHandler<RoiShapeEventArgs> RoiCommitted;

        /// <summary>已提交 ROI 被编辑结束（拖动/变形/改色）时触发</summary>
        public event EventHandler<RoiShapeEventArgs> RoiEdited;

        /// <summary>已提交 ROI 被移除时触发</summary>
        public event EventHandler<RoiShapeEventArgs> RoiRemoved;

        /// <summary>全部 ROI 被清空时触发（移除事件已先行逐条发布）</summary>
        public event EventHandler RoisCleared;

        /// <summary>活动工具切换后触发（用户点选工具条/📐菜单，或程序 SetActiveTool）——供页面联动掩膜语义等。
        /// 参数 Tool = ViewTool 枚举名："Pointer"/"Hand"/"ZoomRect"/"Rect1"/"Rect2"/"Circle"/"Ellipse"/"Polygon"/"Freehand"/"Brush"/"Line"。</summary>
        public event EventHandler<ViewToolChangedEventArgs> ViewToolChanged;

        #endregion

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

        #region 自包含 UI：适应窗口命令 / 信息栏文本（不依赖外部 DataContext）

        /// <summary>
        /// "适应窗口"按钮命令：直接调用本控件的 FitImage()。
        /// 之所以不绑定外部 VM 的 FitImageCmd：本控件可能被嵌入任意 DataContext 宿主
        /// （如 FlowEdit 节点属性窗口预览区，DataContext 是 FlowNode），绑定外部 VM 属性
        /// 会报 Binding Error 40。控件自身命令在任何宿主下都自洽；
        /// VM 模式下 FitImage() 内部仍优先取 RenderContext / 已订阅 VM 的激活图像，行为等价。
        /// </summary>
        public ICommand FitImageCommand { get; }

        /// <summary>
        /// 左下角信息栏文本（依赖属性）。VM 宿主模式下由 DataContextChanged 订阅
        /// ImageDisplayVm.PropertyChanged 自动同步；非 VM 宿主（场景预览）下保持默认值，
        /// 也可由外部代码直接赋值。
        /// </summary>
        public static readonly DependencyProperty SelectedImageInfoProperty =
            DependencyProperty.Register(
                nameof(SelectedImageInfo),
                typeof(string),
                typeof(HalconImageDisplayHost),
                new PropertyMetadata(null));

        public string SelectedImageInfo
        {
            get => (string)GetValue(SelectedImageInfoProperty);
            set => SetValue(SelectedImageInfoProperty, value);
        }
        #endregion

        public HalconImageDisplayHost()
        {
            // "适应窗口"命令：控件自包含，不依赖外部 VM（详见 FitImageCommand 注释）。
            // ⚠ 必须先于 InitializeComponent() 赋值——XAML 中按钮绑定在 InitializeComponent
            // 时解析，晚赋会让按钮 Command 为 null 而不可点击。
            FitImageCommand = new RelayCommand(FitImage);

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

            // 🌟 关闭内置滚轮缩放（HZoomContent=Off）：内置缩放以窗口中心为锚点且步长偏大，
            // 目标会随缩放滑出视野，"想放大看 ROI 却越缩越看不清"。
            // 滚轮由本控件接管：以鼠标指针为锚点缩放（指针下的图像点缩放后仍在指针处），
            // 步长 15%，纵横比保持（见 SmartWindow_HMouseWheel）。
            SmartWindow.HZoomContent = HSmartWindowControlWPF.ZoomContent.Off;

            // 🌟 内置视图工具条初始化：默认"选择/浏览"模式、刷新按钮可用状态、支持 Esc 取消工具
            Toolbar.Visibility = ShowToolbar ? Visibility.Visible : Visibility.Collapsed;
            SetActiveTool(ViewTool.Pointer);
            RefreshToolbarAvailability();
            // 工具条实际高度出来后（布局完成/尺寸变化）刷新"顶部避让高度"，供外部点选覆盖层让位
            Toolbar.SizeChanged += (s, e) => RefreshTopReservedHeight();
            this.Loaded += (s, e) => RefreshTopReservedHeight();
            this.PreviewKeyDown += OnHostPreviewKeyDown;

            // 控件卸载时释放场景托管对象，防止 Halcon 句柄泄漏
            this.Unloaded += (s, e) => RunOnUiSync(ClearScene);
        }

        #region HDevelop 式场景绘制（走一步画一步，交互后整体重放）

        /// <summary>场景条目基类：Draw 自带完整绘制状态（颜色/线宽/模式），重放结果确定</summary>
        private abstract class SceneItem
        {
            public string Color;
            public int LineWidth = 1;
            /// <summary>是否来自 context.Overlays（SyncContextOverlays 重建时的移除标记）：
            /// 与 SceneAdd* 提交的条目区分开，替换叠加层时不误删节点绘制内容</summary>
            public bool IsContextOverlay;
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

        /// <summary>文本条目（image 坐标系，跟随缩放平移）。默认带背景框 + 黑阴影，保证叠加在任意底图（含亮色图）上均可读。</summary>
        private sealed class TextItem : SceneItem
        {
            public string Text;
            public double Row, Col;
            public override void Draw(HWindow window)
            {
                // 半透明黑底(box) + 文字描边：亮底图上白/黄字易糊，先用深色偏移 2px 打底再叠前景色，
                // 亮/暗底图都清晰。这里不再传 disp_text 的 'shadow'/'shadow_offset' 通用参数——
                // 这些参数名在不同 HALCON 版本支持不一（实测本机抛 #3286 Wrong generic parameter name，
                // 结果整段文本画不出来，只剩日志里一句「场景条目绘制失败」）。
                // 阴影改由两次绘制实现，DispTextSafe 内部再做参数降级兜底。
                HalconGlobalHelper.DispTextSafe(window, Text, "image", Row + 2, Col + 2, "black");
                HalconGlobalHelper.DispTextSafe(window, Text, "image", Row, Col, Color ?? "white");
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

        /// <summary>
        /// 回调条目：绘制内容由调用方自由书写（HDevelop 风格直通），重放时再执行一次闭包。
        /// 闭包捕获的对象生命周期归调用方——重放时对象若已释放，Draw 内抛异常由重放循环跳过。
        /// </summary>
        private sealed class DelegateItem : SceneItem
        {
            public Action<HWindow> Paint;
            public override void Draw(HWindow window) => Paint?.Invoke(window);
        }

        /// <summary>当前场景条目（按添加顺序累积）。仅 UI 线程访问。</summary>
        private readonly List<SceneItem> _scene = new List<SceneItem>();

        /// <summary>
        /// 对外标记层（P4 对针理论点 / 偏差标注 / 校验打点 / A 块残差可视化 共用）：
        /// 与 _scene 分离——底图换帧(SceneBegin)不自动清除，由调用方 ClearMarkers() 显式清；
        /// 用户缩放/平移触发的整体重放(RepaintScene)会随场景一起重画，跨交互保留。
        /// 仅 UI 线程访问。
        /// </summary>
        private readonly List<SceneItem> _markerLayer = new List<SceneItem>();

        /// <summary>场景底图（AddBorrowed 的图像，用于 Display 渲染任务的同帧判断）</summary>
        private HObject _sceneBaseImage;

        /// <summary>拖动过程中的场景重放节流时间戳（30ms 一次）</summary>
        private DateTime _lastSceneRepaintUtc = DateTime.MinValue;

        /// <summary>Ctrl+移动 像素探针的刷新节流（30ms 末次生效，避免 GetGrayval 高频）</summary>
        private DateTime _lastPixelProbeUtc = DateTime.MinValue;

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

        /// <summary>
        /// 提交「自由绘制回调」并立即上屏，同时登记进场景供交互后重放（HDevelop 风格直通）。
        /// 回调内容由算法层自由书写（HWindow 实例方法 / HOperatorSet 静态算子均可），
        /// 显示层只负责在 UI 线程执行与重放，不干预绘制内容。
        /// </summary>
        /// <param name="paint">绘制回调，参数即当前有效的 HWindow；重放时会被重复执行</param>
        internal void SceneAddAction(Action<HWindow> paint)
        {
            if (paint == null) return;
            RunOnUiSync(() => SafeAddAndDraw(new DelegateItem { Paint = paint }));
        }

        /// <summary>
        /// 在窗口上直接绘制一次（HDevelop 风格直通，不入场景）。
        /// 用户缩放/拖动窗口触发重放后本次绘制的内容不再出现——适合调试期的临时探针。
        /// 窗口不可用时静默跳过；回调抛异常只记日志，不向上传播。
        /// </summary>
        /// <param name="draw">绘制回调，参数即窗口当前有效的 HWindow（每次现取，不缓存）</param>
        internal void RunOnWindow(Action<HWindow> draw)
        {
            if (draw == null) return;
            RunOnUiSync(() =>
            {
                if (_hWindow == null) return;
                try
                {
                    draw(_hWindow);
                }
                catch (Exception ex)
                {
                    LogBus.Warn("HalconHost", $"窗口直绘回调失败（已跳过）: {ex.Message}");
                }
            });
        }

        #region 对外标记 API（P4 对针 / 校验打点 / 残差可视化 共用地基，2026-09-05）

        /// <summary>
        /// 追加十字标记（image 坐标系；缩放/平移后保留，换底图不自动清，需 ClearMarkers 显式清）。
        /// 线程安全：自动 marshal 到 UI 线程执行并立即上屏。
        /// </summary>
        /// <param name="row">图像行（=像素 Y）</param>
        /// <param name="col">图像列（=像素 X）</param>
        /// <param name="size">十字半尺寸(px)</param>
        /// <param name="color">颜色（"yellow"/"green"/"red"…）</param>
        /// <param name="label">可选文本标签（画在十字上方）</param>
        public void AddMarkerCross(double row, double col, double size, string color, string label = null)
        {
            RunOnUiSync(() =>
            {
                _markerLayer.Add(new CrossItem { Row = row, Col = col, Size = size, Color = color, LineWidth = 1 });
                if (_hWindow != null)
                {
                    try { _markerLayer[_markerLayer.Count - 1].Draw(_hWindow); }
                    catch (Exception ex) { LogBus.Warn("HalconHost", $"标记十字绘制失败（已跳过）: {ex.Message}"); }
                }
            });
            if (!string.IsNullOrWhiteSpace(label))
            {
                AddMarkerText(label, Math.Max(0, row - size - 6), col + size * 0.35, color);
            }
        }

        /// <summary>追加文本标记（image 坐标系，黑底可读；语义同 AddMarkerCross）</summary>
        public void AddMarkerText(string text, double row, double col, string color)
        {
            if (string.IsNullOrEmpty(text)) return;
            RunOnUiSync(() =>
            {
                _markerLayer.Add(new TextItem { Text = text, Row = row, Col = col, Color = color });
                if (_hWindow != null)
                {
                    try { _markerLayer[_markerLayer.Count - 1].Draw(_hWindow); }
                    catch (Exception ex) { LogBus.Warn("HalconHost", $"标记文本绘制失败（已跳过）: {ex.Message}"); }
                }
            });
        }

        /// <summary>清空全部对外标记并重绘当前画面（标记层不含 HObject，无需释放）</summary>
        public void ClearMarkers()
        {
            RunOnUiSync(() =>
            {
                _markerLayer.Clear();
                if (_hWindow == null) return;
                if (_scene.Count > 0)
                {
                    RepaintScene(); // 场景(含底图)+标记层全量重放
                }
                else
                {
                    try { _hWindow.ClearWindow(); } catch { }
                }
            });
        }

        #endregion

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
        /// 消除"清窗→逐条重画"过程中间态可见造成的闪烁。
        /// 容错：若场景底图已失效（被释放），尝试用 VM 当前激活图像兜底重绘，
        /// 避免匹配失败等场景下窗口只剩文字而底图黑屏。</summary>
        private void RepaintScene()
        {
            if (_hWindow == null || _scene.Count == 0) return;
            if (_inRoiSceneRepaint) return; // ROI 视觉刷新触发全场景重放时的防递归闸
            _inRoiSceneRepaint = true;
            bool baseImageDrawn = false;
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
                        try
                        {
                            item.Draw(_hWindow);
                            if (item is ObjectItem oi && oi.Color == null && oi.Obj != null)
                            {
                                baseImageDrawn = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            // ★ 2026-09-11：底图绘制失败 = 直接黑屏，绝不能再吞成 Debug。
                            //   排查"日志全正常但屏幕全黑"时，这条是唯一能指认真凶的线索
                            //   （典型原因：HImage 已被新帧 Dispose、句柄失效、Obj 已释放）。
                            if (item is ObjectItem boi && boi.Color == null)
                            {
                                LogBus.Warn("HalconHost", $"★底图 DispObj 失败（会黑屏）: {ex.Message}");
                            }
                            else
                            {
                                LogBus.Debug("HalconHost", $"场景条目绘制失败（已跳过）: {ex.Message}");
                            }
                        }
                    }

                    // 兜底：场景里没有有效底图时，用 VM 当前激活图像重绘，保证不黑屏
                    if (!baseImageDrawn)
                    {
                        var fallback = (_boundVm?.ActiveImageContext?.Image as HalconRenderImage)?.HImage;
                        if (fallback != null && fallback.IsInitialized())
                        {
                            try
                            {
                                _hWindow.DispObj(fallback);
                                baseImageDrawn = true;
                                LogBus.Warn("HalconHost", "★场景底图失效，已用 VM 当前激活图像兜底重绘。");
                            }
                            catch (Exception ex)
                            {
                                LogBus.Warn("HalconHost", $"兜底重绘图像失败: {ex.Message}");
                            }
                        }
                        else
                        {
                            LogBus.Error("HalconHost", "★无可用底图：场景底图与 VM 激活图像均为空/未初始化");
                        }
                    }

                    if (!baseImageDrawn)
                    {
                        LogBus.Error("HalconHost", "★本次渲染未能画出任何底图，窗口将是黑的");
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

                // 对外标记层重放：画在场景(含底图)之上、ROI 编辑器层之下。
                // 条目类(CrossItem/TextItem)不持有 HObject，清层无需释放。
                foreach (var m in _markerLayer)
                {
                    try
                    {
                        m.Draw(_hWindow);
                    }
                    catch (Exception ex)
                    {
                        LogBus.Debug("HalconHost", $"标记层条目绘制失败（已跳过）: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn("HalconHost", $"场景重放失败: {ex.Message}");
            }
            finally
            {
                _inRoiSceneRepaint = false;
            }

            // ROI 编辑器窗口层视觉：已提交轮廓 / 草绘 / 选中手柄 / 框选橡皮筋。
            // 与底图、VM context.Overlays 同走 HALCON 窗口 DispObj 通道（该通道实证可见；
            // WPF 覆盖层叠在 HSmartWindowControlWPF 视口上方不参与合成）。
            DrawRoiEditorLayerToWindow();
            // ROI WPF 命中层重建（scene=false：已处于场景重放内，禁止再触发全场景重放递归）
            RefreshRoiVisuals(false);
        }

        /// <summary>用户交互（缩放/平移结束、双击、尺寸变化）后重放场景</summary>
        private void RequestSceneRepaint()
        {
            RunOnUiSync(RepaintScene);
        }

        /// <summary>
        /// 把 context.Overlays 同步进场景：先移除上一轮的 context 叠加条目，再按当前 Overlays 重建。
        /// 只动 IsContextOverlay 标记的条目——底图与 SceneAdd* 提交的节点绘制内容不受影响
        /// （模板页的叠加层与生产链路的节点叠加层互不干扰）。
        /// NativeHandle（HObject）生命周期归 context（WpfImageRenderContext.Dispose 统一释放），
        /// 场景条目一律按借用处理（Owned=false）。仅 UI 线程调用（Display 内部）。
        /// </summary>
        private void SyncContextOverlays(ImageRenderContext context)
        {
            for (int i = _scene.Count - 1; i >= 0; i--)
            {
                if (_scene[i].IsContextOverlay)
                {
                    _scene.RemoveAt(i);
                }
            }

            var overlays = context?.Overlays;
            if (overlays == null) return;
            foreach (var overlay in overlays)
            {
                if (overlay == null) continue;
                SceneItem item = null;
                if (overlay.Kind == OverlayKind.Text)
                {
                    if (!string.IsNullOrEmpty(overlay.Text))
                    {
                        item = new TextItem
                        {
                            Text = overlay.Text,
                            Row = overlay.Row,
                            Col = overlay.Column,
                            Color = overlay.Color ?? "green"
                        };
                    }
                }
                else if (overlay.NativeHandle is HObject hObj && hObj.IsInitialized())
                {
                    item = new ObjectItem
                    {
                        Obj = hObj,
                        Color = overlay.Color ?? "green",
                        LineWidth = 2,
                        Owned = false
                    };
                }
                if (item != null)
                {
                    item.IsContextOverlay = true;
                    _scene.Add(item);
                }
            }
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
            // 滚轮缩放（本控件接管，内置缩放已用 HZoomContent=Off 关闭）：
            // 以鼠标指针为锚点——指针下的图像点在缩放后仍停留在指针位置（想看哪就滚哪），
            // 每格 15%，part 宽高同比缩放天然保持纵横比。
            double factor = e.Delta > 0 ? 1.0 / WheelZoomStep : WheelZoomStep; // 前滚放大 = part 变小
            ZoomViewByFactorAt(new Point(e.X, e.Y), factor);
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
            // 绘制类工具/带 ROI 的选择模式下交互由覆盖层接管（OverlayLayer 屏蔽了 SmartWindow 输入），
            // 因此本通道仅服务 Pointer(无 ROI)/Hand 两类纯浏览场景。
            // 外部（如模板管理 ROI 框选）可将 IsPanEnabled 置 false 临时禁用。
            if (!IsPanEnabled)
            {
                _panAnchorSet = false;
                return;
            }
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
                _boundVm.PropertyChanged -= OnBoundVmPropertyChanged;
                _boundVm.OnRequestRender -= HandleRequestRender;
                LogBus.Debug("HalconHost", "已解绑旧 ImageDisplayVm.OnRequestRender 事件");
                _boundVm = null;
            }

            // 2. 新ViewModel绑定，订阅渲染推送事件
            if (e.NewValue is ImageDisplayVm newVm)
            {
                _boundVm = newVm;
                _boundVm.LogTag = _logTag; // 让 ImageDisplayVm 的日志也带上"哪个窗口"
                _boundVm.PropertyChanged += OnBoundVmPropertyChanged;
                _boundVm.OnRequestRender += HandleRequestRender;
                //🌟 订阅自适应事件
                _boundVm.OnRequestFitImage += FitImage;
                LogBus.Info("HalconHost", "成功订阅 ImageDisplayVm.OnRequestRender 事件！");

                // 同步一次信息栏文本（若 VM 已有值）
                SelectedImageInfo = newVm.SelectedImageInfo;

                // 如果VM当前已有激活图像，控件窗口已就绪则立刻渲染
                if (newVm.ActiveImageContext != null)
                {
                    HandleRequestRender(newVm.ActiveImageContext);
                }
            }
        }

        /// <summary>
        /// VM 宿主模式下，把 ImageDisplayVm.SelectedImageInfo 的变化同步到控件自身依赖属性
        /// （信息栏 Text 绑定的是控件自身，不直接绑 VM —— 保证非 VM 宿主下不报 Binding 错误）。
        /// </summary>
        private void OnBoundVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ImageDisplayVm.SelectedImageInfo) && _boundVm != null)
            {
                string text = _boundVm.SelectedImageInfo;
                // ★ 2026-09-06 跨线程修复：ActiveImageContext 允许（且设计上必须）在相机回调线程
                //   直接赋值（见 CalibrationWizardViewModel.OnCameraFrameReceived 注释——采集时序依赖
                //   回调线程同步 Set()），而 ActiveImageContext setter 会联动改 SelectedImageInfo →
                //   此处若在抓帧线程直接把 SelectedImageInfo 镜像写进本控件 DP，WPF 会抛
                //   "调用线程无法访问此对象"（InvalidOperationException, WindowsBase）——表现为每会话
                //   首帧 [Basler] 图像回调异常，并打断回调线程后续 _frameArrivedEvent.Set()（首采延时）。
                //   修复：DP 写入一律 marshal 回 UI 线程（值低频变化，BeginInvoke 足够，无可见延迟）。
                if (Dispatcher.CheckAccess())
                {
                    SelectedImageInfo = text;
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(() => SelectedImageInfo = text));
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
                ? $"[{LogPrefix}] [OnRequestRender] 收到清空指令"
                : $"[{LogPrefix}] [OnRequestRender] 收到渲染请求，节点: [{context.NodeName}]");
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
                    LogBus.Info("HalconHost", $"[{host.LogPrefix}] [OnRenderContextChanged] 监听到 RenderContext 改变，节点名称: [{context.NodeName ?? "Unknown"}]，准备触发 Display(...)");
                    host.Display(context);
                }
                else
                {
                    LogBus.Warn("HalconHost", $"[{host.LogPrefix}] [OnRenderContextChanged] 接收到 null 或非 ImageRenderContext 对象，跳过渲染。");
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
                LogBus.Warn("HalconHost", $"[{LogPrefix}] [Display] 跳过渲染 - HWindow 已准备: {_hWindow != null}");
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
                        // ★ 场景是「底图 + 节点叠加层 + 上下文叠加层」的整体，ClearScene() 会一并丢弃，
                        //   而节点叠加层（模板匹配轮廓）**不会被任何后续帧重建** ⇒ 任何非用户复位的清空
                        //   都是永久性画面损失。这里按"丢了什么"留痕，把「效果闪一下就没」类问题
                        //   直接指到清空指令上（否则只看到底图，判不出叠加层是被谁抹的）。
                        int ctxOverlayCount = 0;
                        for (int i = 0; i < _scene.Count; i++)
                        {
                            if (_scene[i].IsContextOverlay) ctxOverlayCount++;
                        }
                        int baseCount = _sceneBaseImage != null ? 1 : 0;
                        int nodeItemCount = _scene.Count - ctxOverlayCount - baseCount;
                        if (_scene.Count > 0)
                        {
                            LogBus.Warn("HalconHost",
                                $"[{LogPrefix}] [Display] 收到清空 ⇒ 丢弃场景 {_scene.Count} 条（底图 {baseCount} + 节点叠加 {nodeItemCount} + 上下文叠加 {ctxOverlayCount}）。"
                                + (nodeItemCount > 0 ? "节点叠加层不会自动重建；若本次并非用户复位，说明有链路上层误发了清空指令。" : string.Empty));
                        }

                        _hWindow.ClearWindow();
                        ClearScene();
                        OnDisplayFrameChanged(); // 底图被移除：作废旧 ROI
                        _lastImageWidth = 0;
                        _lastImageHeight = 0;
                        LogBus.Debug("HalconHost", "[Display] 窗口与状态已成功清空。");
                        RefreshToolbarAvailability();
                        return;
                    }

                    // 🌟 场景协同（抓图流程时序：渲染任务在场景提交之后才执行）：
                    // - 渲染帧 ≠ 场景底图（新帧到达）：旧场景引用旧帧对象，作废清空；
                    // - 渲染帧 == 场景底图（同一帧补渲染）：场景已包含底图+叠加层，直接重放即可，
                    //   避免 RenderToWindow 先清窗再画底图带来的闪烁/黑屏风险（节点失败时底图可能已释放）。
                    var newImage = (context.Image as HalconRenderImage)?.HImage;
                    bool sameFrame = newImage != null && ReferenceEquals(newImage, _sceneBaseImage);
                    if (!sameFrame)
                    {
                        // ★ 这里会连节点叠加层一起清掉，是"叠加层一闪即没"的唯一现场，
                        //   必须留下痕迹（否则只看到底图，判不出是被换帧清掉的）。
                        if (_scene.Count > 0)
                        {
                            LogBus.Info("HalconHost",
                                $"[{LogPrefix}] [Display] 底图换帧 ⇒ 作废旧场景（原有 {_scene.Count} 条场景条目，含节点叠加层）。新节点: [{context.NodeName}]");
                        }
                        ClearScene();
                        OnDisplayFrameChanged(); // 底图更换：作废基于旧帧绘制的 ROI
                        // 🌟 新帧登记为场景底图（借用，不拥有）：生产链路的节点叠加图形
                        // （模板匹配轮廓/十字/文本）经 SceneAddObject 进入同一场景——
                        // 用户缩放/平移/窗口尺寸变化触发的 RepaintScene 重放"底图+叠加层"，
                        // 画面完整；底图本身仍由场景重放负责首绘。
                        // 借用引用的生命周期：换帧时 ClearScene 先移除；若图像已被显示层
                        // 释放（历史帧 Dispose），重放时单条 Draw 失败自动跳过。
                        if (newImage != null)
                        {
                            _sceneBaseImage = newImage;
                            _scene.Add(new ObjectItem { Obj = newImage, Color = null, Owned = false });
                        }
                    }

                    // 🌟 context.Overlays（ROI 框 / 匹配轮廓 / 分数标注）登记进场景：
                    // 此前叠加层只被 RenderToWindow 画一次，用户一缩放/平移触发重放就全部丢失。
                    // 现在与底图同场景重放，交互后依旧完整；文本走 image 坐标系（跟随缩放平移）。
                    SyncContextOverlays(context);

                    if (_scene.Count > 0)
                    {
                        // 首绘与重放统一走场景（flush_graphic 批量上屏，防闪烁）
                        RepaintScene();
                        LogBus.Debug("HalconHost", $"[Display] 场景重放完成 (尺寸: {context.Image.Width}x{context.Image.Height})");
                    }
                    else
                    {
                        // 场景为空（图像非 Halcon 渲染等罕见情形）退回直绘通道
                        _renderService.RenderToWindow(_hWindow, context.Image, context.Overlays);
                        LogBus.Debug("HalconHost", $"[Display] 图像已成功 DispObj 到 HWindow (尺寸: {context.Image.Width}x{context.Image.Height})");
                    }

                    // 仅当图像分辨率变化时，执行窗口自适应填充（性能优化，避免重复缩放）
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

                            // ★ 黑屏诊断（2026-09-11）：窗口自身的像素尺寸。
                            //   HWindow 若在布局完成前创建/未跟随容器拉伸，extents 会是 0 或 1，
                            //   DispObj 画进一个没有面积的窗口 —— 现象就是「日志全正常但屏幕全黑」。
                            //   同时打 SetPart 后的视口，确认没有把视口缩到一角。
                            try
                            {
                                HOperatorSet.GetWindowExtents(_hWindow, out HTuple wr, out HTuple wc, out HTuple ww, out HTuple wh);
                                LogBus.Info("HalconHost", $"[{LogPrefix}] [Display] 窗口尺寸={ww.I}x{wh.I} @({wr.I},{wc.I}) | 控件 Actual={ActualWidth:F0}x{ActualHeight:F0}");
                            }
                            catch (Exception exExt)
                            {
                                LogBus.Warn("HalconHost", "[Display] 读取窗口尺寸失败: " + exExt.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Error("HalconHost", $"渲染主图失败: {ex.Message}", ex);
                }

                // 渲染结果同步刷新工具条可用状态（有图才允许缩放/ROI/框选等操作）
                RefreshToolbarAvailability();
                // ROI 命中层与最新渲染对齐（同帧补渲/换帧/清空后）。scene=false：
                // 上方 Display 已走 RepaintScene（含窗口层 ROI 视觉），不必再触发一次全场景重放
                RefreshRoiVisuals(false);
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
                // part 变化后整体重放场景 + ROI 覆盖层对齐（双击适应/工具条 🖼️ 均走此通道）
                RequestSceneRepaint();
                LogBus.Debug("HalconHost", "手动触发了图像自适应窗口 (FitImage)。");
            }
        }

        /// <summary>
        /// 视口内坐标（相对 SmartWindow 视图区左上角，DIP）→ 图像坐标（HALCON row/col）。
        /// 供内置工具（ROI 框选/框选放大/缩放）及上层界面换算使用：把拖拽矩形两角换算为图像像素坐标。
        /// 基于当前窗口 part 映射，天然兼容缩放/平移后的画面。
        /// 注意：公共 API 只暴露 WPF/double 类型，不泄漏 halcondotnet（MC1000 约束）。
        /// 坐标基准是视图区（SmartWindow 所在单元格），不是整个控件（含顶部工具条）。
        /// </summary>
        public bool TryGetImagePointAt(Point viewPoint, out double row, out double col)
        {
            row = 0;
            col = 0;
            if (_hWindow == null || SmartWindow == null) return false;
            if (SmartWindow.ActualWidth <= 0 || SmartWindow.ActualHeight <= 0) return false;
            try
            {
                _hWindow.GetPart(out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
                double scaleX = (c2.D - c1.D) / SmartWindow.ActualWidth;  // 每窗口像素对应多少图像列
                double scaleY = (r2.D - r1.D) / SmartWindow.ActualHeight; // 每窗口像素对应多少图像行
                col = c1.D + viewPoint.X * scaleX;
                row = r1.D + viewPoint.Y * scaleY;
                return true;
            }
            catch (Exception ex)
            {
                LogBus.Warn("HalconHost", $"屏幕坐标转图像坐标失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 宿主控件坐标（相对本控件左上角，含顶部工具条）→ 图像坐标（HALCON row/col）。
        /// 2026-09-10 新增：外部窗口（对针/校验/点哪去哪）在宿主之上盖透明点选覆盖层，
        /// 用 e.GetPosition(ImageHost) 拿到的 Y 坐标含工具条高度，若直接喂 TryGetImagePointAt
        /// （其基准是"视图区/SmartWindow"，不含工具条）会把 row 多算一个工具条高度，
        /// 表现为十字标注落在点击点下方约 10mm。本方法先扣掉 TopReservedHeight（工具条实际高度）
        /// 再走同一套 part 映射，语义 = "宿主坐标 → 图像坐标"，供外部点选覆盖层专用。
        /// </summary>
        public bool TryGetImagePointAtHost(Point hostPoint, out double row, out double col)
        {
            row = 0;
            col = 0;
            // 扣掉顶部工具条占用的高度，把"宿主坐标"归一到"视口坐标"
            double topOffset = ShowToolbar ? TopReservedHeight : 0.0;
            var viewPoint = new Point(hostPoint.X, hostPoint.Y - topOffset);
            return TryGetImagePointAt(viewPoint, out row, out col);
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

            RefreshToolbarAvailability();
        }

        /// <summary>
        /// Ctrl+鼠标移动：读取鼠标下方图像像素坐标 → 更新 VM 的 SelectedImageInfo（左下角信息栏）
        /// 与 CursorPixelMoved 事件。30ms 节流（GetPixelInfo 含灰度/多通道采样，勿每 move 都跑）。
        /// 由覆盖层 PreviewMouseMove（视口内移动的唯一可靠入口）调用，
        /// 同时 SmartWindow/外层 Grid 的旧通道保留为兼容兜底（无覆盖层拦截时仍工作）。
        /// </summary>
        private void ProbeCursorPixel(System.Windows.Input.MouseEventArgs e)
        {
            if (SmartWindow == null || _hWindow == null) return;

            bool isCtrlPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)
                                 == System.Windows.Input.ModifierKeys.Control;
            if (!isCtrlPressed) return;

            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastPixelProbeUtc).TotalMilliseconds < 30) return;
                _lastPixelProbeUtc = now;

                var activeContext = RenderContext ?? _boundVm?.ActiveImageContext;
                if (activeContext?.Image == null) return;
                int imgWidth = activeContext.Image.Width;
                int imgHeight = activeContext.Image.Height;

                // WPF 坐标 + HWindow Part 逆映射（与滚轮缩放/ROI 同一套算法，不调用
                // GetMpositionSubPix——该算子在无鼠标按键时抛 HOperatorException）
                var mousePos = e.GetPosition(SmartWindow);
                if (SmartWindow.ActualWidth <= 0 || SmartWindow.ActualHeight <= 0) return;
                _hWindow.GetPart(out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
                double partWidth = c2.D - c1.D;
                double partHeight = r2.D - r1.D;
                int imgX = (int)Math.Floor(c1.D + (mousePos.X / SmartWindow.ActualWidth) * partWidth);
                int imgY = (int)Math.Floor(r1.D + (mousePos.Y / SmartWindow.ActualHeight) * partHeight);

                if (imgX >= 0 && imgX < imgWidth && imgY >= 0 && imgY < imgHeight)
                {
                    if (DataContext is ImageDisplayVm vm)
                    {
                        vm.UpdateCursorPixelInfo(imgX, imgY);
                    }
                    CursorPixelMoved?.Invoke(this, new CursorPixelEventArgs { X = imgX, Y = imgY });
                }
            }
            catch (Exception ex)
            {
                LogBus.Debug("HalconHost", $"像素探针微损: {ex.Message}");
            }
        }

        /// <summary>
        /// 窗口鼠标移动事件（HSmartWindow 自带通道）。
        /// 视口被交互覆盖层接管时该通道收不到事件，主体逻辑已收敛到 ProbeCursorPixel，
        /// 由 Overlay_MouseMove 统一驱动；本方法保留为无覆盖层/直连场景的兼容兜底。
        /// </summary>
        private void SmartWindow_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ProbeCursorPixel(e);
        }

        /// <summary>
        /// 监听外层 Grid 的 MouseMove（历史通道：绕过 HSmartWindowControlWPF 的 Win32 截断）。
        /// 主体逻辑已收敛到 ProbeCursorPixel——覆盖层 PreviewMouseMove 内已探针并可能 Handled，
        /// 冒泡到本层时通常不再触发；保留避免覆盖层未命中/未武装时漏掉探针。
        /// </summary>
        private void Grid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ProbeCursorPixel(e);
        }

        #region 🌟 内置视图工具条引擎（工具模式 / 缩放 / 清空 / 框选放大 / 绘制 ROI）

        /// <summary>视图交互工具模式</summary>
        private enum ViewTool
        {
            /// <summary>选择/浏览（默认）：滚轮锚点缩放 + 空白左键拖拽平移 + 双击适应；点选 ROI 可拖动/变形</summary>
            Pointer,
            /// <summary>拖动移动：左键拖拽平移画面（手型光标）</summary>
            Hand,
            /// <summary>框选放大：左键拖出矩形，松开放大到该区域（仿 HDevelop 放大镜）</summary>
            ZoomRect,
            /// <summary>绘制 平行矩形（axis-parallel）</summary>
            Rect1,
            /// <summary>绘制 旋转矩形（中心+角度+半长/半宽）</summary>
            Rect2,
            /// <summary>绘制 圆形</summary>
            Circle,
            /// <summary>绘制 椭圆（中心+角度+主轴/副轴）</summary>
            Ellipse,
            /// <summary>绘制 任意多边形区域（左键逐点加顶点，右键闭合完成）</summary>
            Polygon,
            /// <summary>绘制 任意区域（手绘，仿 HALCON draw_region：按住左键沿路径连续描线，右键闭合创建）</summary>
            Freehand,
            /// <summary>绘制 线段</summary>
            Line,
            /// <summary>涂抹画笔（掩膜画笔）：按住左键=沿轨迹涂抹（圆盘带），松开=收笔提交一笔。
            /// 每笔经 RoiCommitted 以 RoiShapeKind.Brush 提交（Polygon 槽=轨迹点，BrushRadius=半径）。
            /// 常驻工具可连续涂抹；右键/Esc 退出回 Pointer。画笔半径由 BrushRadiusPx 提供（外部可调）。</summary>
            Brush
        }

        private ViewTool _activeTool = ViewTool.Pointer;
        private const double WheelZoomStep = 1.15;
        private bool _dragActive;
        private Point _dragStart, _dragEnd;

        /// <summary>绘制类工具是否激活（覆盖层接管鼠标交互）</summary>
        private bool IsDrawToolActive =>
            _activeTool == ViewTool.ZoomRect || IsRoiDrawTool();

        private static void OnShowToolbarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as HalconImageDisplayHost)?.ApplyToolbarVisibility();
        }

        private void ApplyToolbarVisibility()
        {
            if (Toolbar == null) return;
            Toolbar.Visibility = ShowToolbar ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>工具栏工具单选触发：按 Tag 切换工具模式</summary>
        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag &&
                Enum.TryParse(tag, out ViewTool tool))
            {
                SetActiveTool(tool);
            }
        }

        /// <summary>切换工具模式：同步单选钮/覆盖层/光标/提示，并结束残留拖拽态</summary>
        private void SetActiveTool(ViewTool tool)
        {
            bool changed = _activeTool != tool;
            _activeTool = tool;

            // 结束拖拽态（框选橡皮筋/ROI 视觉由 HALCON 窗口层画，无 WPF 视觉元素可清理）
            _dragActive = false;
            _ptrDrag = PtrDragMode.None;
            _dragShape = null;
            _handleKey = null;
            ReleaseOverlayCapture();
            if (DrawHint != null) DrawHint.Visibility = Visibility.Collapsed;

            // 同步工具按钮状态：Pointer/Hand/框选放大/涂抹画笔 单选钮互斥；
            // 其余绘制形状没有独立单选钮，统一由「📐 绘制 ▾」下拉钮的激活高亮表达
            if (ToolSelect != null)
            {
                var rb = GetToolRadio(tool); // 浏览三工具 + 涂抹画笔返回按钮；其它绘制形状返回 null
                ToolSelect.IsChecked = rb == ToolSelect;
                ToolHand.IsChecked = rb == ToolHand;
                ToolZoomRect.IsChecked = rb == ToolZoomRect;
                if (ToolBrush != null) ToolBrush.IsChecked = rb == ToolBrush;
            }
            UpdateRoiPickerState();

            // 覆盖层接管条件：绘制工具，或 Pointer 下存在已提交 ROI
            bool armed = OverlayShouldBeArmed();
            if (OverlayLayer != null)
            {
                OverlayLayer.IsHitTestVisible = armed;
                OverlayLayer.Cursor = IsRoiDrawTool() ? Cursors.Cross : null;
            }
            if (SmartWindow != null)
            {
                SmartWindow.Cursor = tool == ViewTool.Hand ? Cursors.Hand : null;
            }

            // 提示条
            if (DrawHint != null)
            {
                if (IsDrawToolActive)
                {
                    DrawHint.Visibility = Visibility.Visible;
                    if (tool == ViewTool.ZoomRect)
                    {
                        DrawHintText.Text = "🔍 按住左键拖出矩形区域，松开后放大到该区域 · 右键 / Esc 取消";
                    }
                    else
                    {
                        SetSketchHint();
                    }
                }
                else
                {
                    DrawHint.Visibility = Visibility.Collapsed;
                }
            }

            RefreshRoiVisuals();

            // 广播工具切换（供模板页等外部订阅：Brush→自动开掩膜语义，Pointer/形状→收掩膜等）。
            // 仅真实切换时触发；SetActiveTool 内部将单选钮 IsChecked 复位引发的二次调用 changed=false，不会死循环。
            if (changed)
            {
                ViewToolChanged?.Invoke(this, new ViewToolChangedEventArgs(tool.ToString()));
            }
        }

        /// <summary>工具枚举 → 对应单选按钮（绘制类工具无独立按钮，返回 null）</summary>
        private RadioButton GetToolRadio(ViewTool tool)
        {
            switch (tool)
            {
                case ViewTool.Pointer: return ToolSelect;
                case ViewTool.Hand: return ToolHand;
                case ViewTool.ZoomRect: return ToolZoomRect;
                case ViewTool.Brush: return ToolBrush; // 涂抹画笔有一级工具按钮（2026-09-09 工具条化）
                default: return null; // 其余绘制形状统一由「📐 绘制 ▾」下拉表达
            }
        }

        // ---------------- 📐 绘制 ROI 形状下拉 ----------------

        /// <summary>「📐 绘制」按钮点击：弹出形状下拉（再次点击已激活时同样弹出，便于换形状/退出）</summary>
        private void ToolRoiPicker_Click(object sender, RoutedEventArgs e)
        {
            if (RoiShapeMenu == null) return;
            RoiShapeMenu.PlacementTarget = ToolRoiPicker;
            RoiShapeMenu.Placement = PlacementMode.Bottom;
            RoiShapeMenu.IsOpen = true;
            // ToggleButton 点击会翻转 IsChecked：立即纠正回“是否处于绘制工具”的真实状态
            UpdateRoiPickerState();
            e.Handled = true;
        }

        /// <summary>形状下拉菜单项点击：按 Tag 切换工具（形状或退出回选择）</summary>
        private void RoiShapeMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string tag &&
                Enum.TryParse(tag, out ViewTool tool))
            {
                SetActiveTool(tool);
            }
        }

        /// <summary>📐 绘制按钮 ToolTip：列出可绘制形状（涂抹画笔已升级为工具条一级按钮 🖌，不在此列）</summary>
        private const string RoiShapesTooltip = "矩形 / 旋转矩形 / 圆形 / 椭圆 / 多边形 / 手绘区域 / 线段";

        /// <summary>同步「📐 绘制」按钮的激活高亮与悬浮提示</summary>
        private void UpdateRoiPickerState()
        {
            if (ToolRoiPicker == null) return;
            // 涂抹画笔已提升为一级工具按钮（ToolBrush），激活时不再点亮 📐 下拉钮
            bool drawing = IsRoiDrawTool() && _activeTool != ViewTool.Brush;
            ToolRoiPicker.IsChecked = drawing;
            ToolRoiPicker.ToolTip = drawing
                ? $"📐 当前绘制：{ShapeDisplayName(_activeTool)}（左键绘制 · 右键完成 · Esc 退出；点击可换形状）"
                : $"📐 绘制 ROI：点击弹出形状菜单（{RoiShapesTooltip}）";
        }

        private static string ShapeDisplayName(ViewTool tool)
        {
            switch (tool)
            {
                case ViewTool.Rect1: return "平行矩形";
                case ViewTool.Rect2: return "旋转矩形";
                case ViewTool.Circle: return "圆形";
                case ViewTool.Ellipse: return "椭圆";
                case ViewTool.Polygon: return "多边形区域";
                case ViewTool.Freehand: return "手绘区域";
                case ViewTool.Brush: return "涂抹画笔";
                case ViewTool.Line: return "线段";
                default: return "";
            }
        }

        /// <summary>Esc 取消草绘/选中/退出绘制工具；Delete 删除选中 ROI</summary>
        private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (InteractiveCancel())
                {
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Delete)
            {
                if (InteractiveDelete())
                {
                    e.Handled = true;
                }
            }
        }

        // ---------------- 缩放（工具栏按钮 + 覆盖层滚轮共用一套锚点算法） ----------------

        /// <summary>
        /// 以视图内锚点缩放：锚点下的图像点缩放后仍停留锚点处。
        /// factor&lt;1 放大（part 变小），factor&gt;1 缩小。
        /// </summary>
        private void ZoomViewByFactorAt(Point anchor, double factor)
        {
            if (_hWindow == null || SmartWindow == null) return;
            if (SmartWindow.ActualWidth <= 0 || SmartWindow.ActualHeight <= 0) return;
            try
            {
                _hWindow.GetPart(out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
                double partW = c2.D - c1.D;
                double partH = r2.D - r1.D;
                if (partW <= 0 || partH <= 0) return;

                double nw = partW * factor;
                double nh = partH * factor;
                // 锚点（窗口比例位置）对应的图像坐标
                double col = c1.D + anchor.X * partW / SmartWindow.ActualWidth;
                double row = r1.D + anchor.Y * partH / SmartWindow.ActualHeight;
                // 缩放后锚点仍映射回原窗口比例位置
                double newC1 = col - (anchor.X / SmartWindow.ActualWidth) * nw;
                double newR1 = row - (anchor.Y / SmartWindow.ActualHeight) * nh;
                _hWindow.SetPart(newR1, newC1, newR1 + nh, newC1 + nw);
                RequestSceneRepaint();
            }
            catch (Exception ex)
            {
                LogBus.Warn("HalconHost", $"缩放失败: {ex.Message}");
            }
        }

        /// <summary>以窗口中心为锚缩放（工具栏 +/- 按钮）</summary>
        private void ZoomViewAtCenter(double factor)
        {
            RunOnUiSync(() =>
            {
                if (SmartWindow == null || SmartWindow.ActualWidth <= 0 || SmartWindow.ActualHeight <= 0) return;
                ZoomViewByFactorAt(new Point(SmartWindow.ActualWidth / 2, SmartWindow.ActualHeight / 2), factor);
            });
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
            => ZoomViewAtCenter(1.0 / WheelZoomStep);

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
            => ZoomViewAtCenter(WheelZoomStep);

        /// <summary>1:1 实际大小：保持当前视野中心不变，1 图像像素 ≈ 1 屏幕像素</summary>
        private void BtnActualSize_Click(object sender, RoutedEventArgs e)
        {
            RunOnUiSync(() =>
            {
                if (_hWindow == null || SmartWindow == null) return;
                if (SmartWindow.ActualWidth <= 0 || SmartWindow.ActualHeight <= 0) return;
                if (!TryGetImageSize(out int w, out int h)) return;
                try
                {
                    _hWindow.GetPart(out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
                    double centerRow = (r1.D + r2.D) / 2;
                    double centerCol = (c1.D + c2.D) / 2;
                    double winW = SmartWindow.ActualWidth;
                    double winH = SmartWindow.ActualHeight;
                    double maxR = Math.Max(0, h - winH);
                    double maxC = Math.Max(0, w - winW);
                    double newR1 = ClampValue(centerRow - winH / 2, 0, maxR);
                    double newC1 = ClampValue(centerCol - winW / 2, 0, maxC);
                    _hWindow.SetPart(newR1, newC1, newR1 + winH - 1, newC1 + winW - 1);
                    RequestSceneRepaint();
                }
                catch (Exception ex)
                {
                    LogBus.Warn("HalconHost", $"1:1 视图设置失败: {ex.Message}");
                }
            });
        }

        // ---------------- 清空标注 ----------------

        private void BtnClearAnnotations_Click(object sender, RoutedEventArgs e) => ClearAnnotations();

        /// <summary>
        /// 清空画面标注：移除场景中底图以外的所有条目（节点叠加图形 / 文字），
        /// 并清空内置 ROI 编辑器的已提交 ROI。保留底图继续显示。
        /// 拥有对象就地释放；context 叠加条目（IsContextOverlay）只移除不释放。
        /// </summary>
        public void ClearAnnotations()
        {
            RunOnUiSync(() =>
            {
                if (_hWindow == null) return;
                _dragActive = false;

                ObjectItem baseItem = null;
                for (int i = _scene.Count - 1; i >= 0; i--)
                {
                    var item = _scene[i];
                    bool isBase = item is ObjectItem bi && bi.Obj != null && bi.Color == null && !bi.Owned &&
                                  ReferenceEquals(bi.Obj, _sceneBaseImage);
                    if (isBase)
                    {
                        baseItem = (ObjectItem)item;
                        continue;
                    }
                    if (item is ObjectItem oi && oi.Owned && oi.Obj != null)
                    {
                        try { oi.Obj.Dispose(); } catch { }
                    }
                    _scene.RemoveAt(i);
                }

                _scene.Clear();
                if (baseItem != null)
                {
                    _scene.Add(baseItem);
                }
                else if (_sceneBaseImage != null && _sceneBaseImage.IsInitialized())
                {
                    _scene.Add(new ObjectItem { Obj = _sceneBaseImage, Color = null, Owned = false });
                }

                ClearAllRois(); // 清空 ROI 编辑器集合（发 RoiRemoved/RoisCleared）

                if (_scene.Count > 0)
                {
                    RepaintScene();
                }
                else
                {
                    try { _hWindow.ClearWindow(); } catch { }
                }
                RefreshToolbarAvailability();
                LogBus.Debug("HalconHost", "已清空画面标注（保留底图）。");
            });
        }

        // ---------------- 覆盖层交互事件壳（转发到 ROI 编辑引擎，见 HalconImageDisplayHost.RoiEditor.cs） ----------------

        private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (InteractiveMouseLeftDown(e)) e.Handled = true;
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            // Ctrl+鼠标移动像素探针：视口上的移动事件最终都被覆盖层 Preview 通道收走
            // （SmartWindow/外层 Grid 的 bubbling 通道被 Handled 屏蔽或路由不经过），
            // 因此探针必须在覆盖层内做，放在 ROI 交互之前（探针只读不改状态，互不干扰）
            ProbeCursorPixel(e);
            if (InteractiveMouseMove(e)) e.Handled = true;
        }

        private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (InteractiveMouseLeftUp(e)) e.Handled = true;
        }

        private void Overlay_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (InteractiveMouseRightDown(e)) e.Handled = true;
        }

        private void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (InteractiveMouseWheel(e)) e.Handled = true;
        }

        private Point ClampToView(Point p)
        {
            if (SmartWindow == null) return p;
            return new Point(
                Math.Max(0, Math.Min(p.X, SmartWindow.ActualWidth)),
                Math.Max(0, Math.Min(p.Y, SmartWindow.ActualHeight)));
        }

        /// <summary>把橡皮筋两角换算为图像坐标并收敛到图像范围</summary>
        private bool MapRubberToImage(Point p0, Point p1, out double r1, out double c1,
                                      out double r2, out double c2)
        {
            r1 = c1 = r2 = c2 = 0;
            if (!TryGetImagePointAt(p0, out double ar, out double ac)) return false;
            if (!TryGetImagePointAt(p1, out double br, out double bc)) return false;
            r1 = Math.Min(ar, br); c1 = Math.Min(ac, bc);
            r2 = Math.Max(ar, br); c2 = Math.Max(ac, bc);

            if (TryGetImageSize(out int w, out int h))
            {
                r1 = ClampValue(r1, 0, h - 1); c1 = ClampValue(c1, 0, w - 1);
                r2 = ClampValue(r2, 0, h - 1); c2 = ClampValue(c2, 0, w - 1);
            }
            return r2 > r1 && c2 > c1;
        }

        /// <summary>框选放大：把橡皮筋区域（保持窗口纵横比扩展）设为新的窗口 part</summary>
        private void ZoomToRubber(Point p0, Point p1)
        {
            if (_hWindow == null || SmartWindow == null ||
                SmartWindow.ActualWidth <= 0 || SmartWindow.ActualHeight <= 0)
            {
                SetActiveTool(ViewTool.Pointer);
                return;
            }
            if (!MapRubberToImage(p0, p1, out double r1, out double c1, out double r2, out double c2))
            {
                SetActiveTool(ViewTool.Pointer);
                return;
            }
            try
            {
                double dw = c2 - c1;
                double dh = r2 - r1;
                double aspect = SmartWindow.ActualWidth / SmartWindow.ActualHeight;
                double partW, partH;
                if (dw / dh > aspect)
                {
                    partW = dw;
                    partH = dw / aspect;
                }
                else
                {
                    partH = dh;
                    partW = dh * aspect;
                }
                double cx = (c1 + c2) / 2;
                double cy = (r1 + r2) / 2;
                partW = Math.Max(partW, 8);
                partH = Math.Max(partH, 8);
                _hWindow.SetPart(cy - partH / 2, cx - partW / 2, cy + partH / 2, cx + partW / 2);
                RequestSceneRepaint();
            }
            catch (Exception ex)
            {
                LogBus.Warn("HalconHost", $"框选放大失败: {ex.Message}");
            }
            SetActiveTool(ViewTool.Pointer); // 单次工具：完成后回到浏览
        }

        // ---------------- 状态辅助 ----------------

        /// <summary>当前是否有可交互底图（决定缩放/ROI/框选等工具是否可用）</summary>
        private bool HasDisplayImage()
        {
            if ((RenderContext ?? _boundVm?.ActiveImageContext)?.Image != null) return true;
            return _sceneBaseImage != null && _sceneBaseImage.IsInitialized();
        }

        /// <summary>获取当前显示的图像尺寸（优先 RenderContext/VM，其次场景底图）</summary>
        private bool TryGetImageSize(out int width, out int height)
        {
            width = 0;
            height = 0;
            var img = (RenderContext ?? _boundVm?.ActiveImageContext)?.Image;
            if (img != null)
            {
                width = img.Width;
                height = img.Height;
                return true;
            }
            if (_sceneBaseImage is HImage hi)
            {
                try
                {
                    hi.GetImageSize(out HTuple w, out HTuple h);
                    width = w.I;
                    height = h.I;
                    return true;
                }
                catch { /* 忽略，走默认 false */ }
            }
            return false;
        }

        /// <summary>按当前显示状态刷新工具条按钮可用性</summary>
        private void RefreshToolbarAvailability()
        {
            if (BtnZoomIn == null) return; // InitializeComponent 未完成
            bool hasImg = HasDisplayImage() && _hWindow != null;
            bool ready = _hWindow != null;
            BtnZoomIn.IsEnabled = hasImg;
            BtnZoomOut.IsEnabled = hasImg;
            BtnActualSize.IsEnabled = hasImg;
            BtnFit.IsEnabled = ready;
            ToolZoomRect.IsEnabled = hasImg;
            if (ToolRoiPicker != null)
            {
                ToolRoiPicker.IsEnabled = hasImg;
            }
            // 存在"底图以外标注"或 ROI 时才允许清空
            BtnClearAnnotations.IsEnabled = ready && (_scene.Count > 1 || _roiShapes.Count > 0);
        }

        private static double ClampValue(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        #endregion
    }
}