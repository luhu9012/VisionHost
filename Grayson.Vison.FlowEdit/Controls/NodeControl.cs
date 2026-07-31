using Grayson.Vison.FlowEdit.Converters;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.ViewModels;
using Grayson.Vison.FlowEdit.ViewModels;
using Grayson.Vison.FlowEdit.Views;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Grayson.Vison.FlowEdit.Controls
{
    public class NodeControl : ContentControl
    {
        private Point _dragStartPoint;
        private Point _nodeStartPos;
        private bool _isDragging;

        public FlowNodeBase Node
        {
            get => GetValue(NodeProperty) as FlowNodeBase;
            set => SetValue(NodeProperty, value);
        }

        public static readonly DependencyProperty NodeProperty = DependencyProperty.Register(
            "Node",
            typeof(FlowNodeBase),
            typeof(NodeControl),
            new PropertyMetadata(null, OnNodePropertyChanged));

        public NodeControl()
        {
            Width = 160;
            MinHeight = 65; // 🌟 取消固定 Height = 70，改用 MinHeight 支持根据端口数量自适应高度
            Template = GetNodeTemplate();
            // 在 NodeControl 构造函数或初始化中绑定 Node.Height
            this.SetBinding(FrameworkElement.HeightProperty, new Binding("Node.Height")
            {
                RelativeSource = RelativeSource.Self,
                Mode = BindingMode.TwoWay
            });
        }

        /// <summary>
        /// 当 Node 实例发生变化时，触发端口自动排版布局
        /// </summary>
        private static void OnNodePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is NodeControl control && e.NewValue is FlowNodeBase newNode)
            {
                control.AutoLayoutNodePorts(newNode);
            }
        }

        /// <summary>
        /// 🌟 工业级自动排版算法：自动计算控制流(Exec)与数据流(Data)端点的 RelativeY 坐标并撑开节点高度
        /// </summary>
        public void AutoLayoutNodePorts(FlowNodeBase node)
        {
            if (node == null) return;

            double nodeWidth = this.Width > 0 ? this.Width : 160.0;
            double minNodeHeight = 70.0;

            // ----------------------------------------------------
            // 1. 过滤掉 Exec 端口，仅保留 Data 端口
            // ----------------------------------------------------
            var inDataPorts = node.InputPorts.Where(p => p.Category == PortCategory.Data).ToList();
            var outDataPorts = node.OutputPorts.Where(p => p.Category == PortCategory.Data).ToList();

            // 如果你有扩展端口的区分属性（比如 IsExtension），在此区分；
            // 默认示例：前 3 个为主要输入/输出（上下排），其余为扩展（左右排）
            var topPorts = inDataPorts.Take(3).ToList();
            var leftPorts = inDataPorts.Skip(3).ToList();

            var bottomPorts = outDataPorts.Take(3).ToList();
            var rightPorts = outDataPorts.Skip(3).ToList();

            // ----------------------------------------------------
            // 2. 顶端端口排版 (Top - 输入)
            // ----------------------------------------------------
            for (int i = 0; i < topPorts.Count; i++)
            {
                var port = topPorts[i];
                port.Position = PortPosition.Top;
                // 均匀横向分布在顶边
                port.RelativeX = (nodeWidth / (topPorts.Count + 1)) * (i + 1);
                port.RelativeY = 0; // 顶边缘
            }

            // ----------------------------------------------------
            // 3. 底端端口排版 (Bottom - 输出)
            // ----------------------------------------------------
            for (int i = 0; i < bottomPorts.Count; i++)
            {
                var port = bottomPorts[i];
                port.Position = PortPosition.Bottom;
                // 均匀横向分布底边
                port.RelativeX = (nodeWidth / (bottomPorts.Count + 1)) * (i + 1);
                port.RelativeY = minNodeHeight; // 初始底边缘，会在下面随高度调整
            }

            // ----------------------------------------------------
            // 4. 左侧扩展端口排版 (Left)
            // ----------------------------------------------------
            double sideRowHeight = 22.0;
            double sideStartTop = 25.0; // 避开顶部 Header
            for (int i = 0; i < leftPorts.Count; i++)
            {
                var port = leftPorts[i];
                port.Position = PortPosition.Left;
                port.RelativeX = 0;
                port.RelativeY = sideStartTop + (i * sideRowHeight);
            }

            // ----------------------------------------------------
            // 5. 右侧扩展端口排版 (Right)
            // ----------------------------------------------------
            for (int i = 0; i < rightPorts.Count; i++)
            {
                var port = rightPorts[i];
                port.Position = PortPosition.Right;
                port.RelativeX = nodeWidth;
                port.RelativeY = sideStartTop + (i * sideRowHeight);
            }

            // ----------------------------------------------------
            // 6. 动态计算节点高度并校正 Bottom 端口 Y 坐标
            // ----------------------------------------------------
            int maxSideRows = Math.Max(leftPorts.Count, rightPorts.Count);
            double calculatedHeight = Math.Max(minNodeHeight, sideStartTop + (maxSideRows * sideRowHeight) + 15.0);

            node.Height = calculatedHeight;

            // 更新底部端口的相对 Y 坐标（对应节点实际高度）
            foreach (var port in bottomPorts)
            {
                port.RelativeY = calculatedHeight;
            }
        }

        private FlowEditView GetParentFlowEditView()
        {
            DependencyObject parent = VisualTreeHelper.GetParent(this);
            while (parent != null && !(parent is FlowEditView))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            return parent as FlowEditView;
        }

        private ControlTemplate GetNodeTemplate()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));

            // ==========================================
            // 1. 节点背景 Border (主卡片样式)
            // ==========================================
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding("Node.Category") { RelativeSource = RelativeSource.TemplatedParent, Converter = new Converters.NodeColorConv() });

            // 选中边框与运行状态边框高亮绑定
            var borderBrushBind = new MultiBinding { Converter = new Converters.NodeBorderConv() };
            borderBrushBind.Bindings.Add(new Binding("Node") { RelativeSource = RelativeSource.TemplatedParent });
            borderBrushBind.Bindings.Add(new Binding("DataContext.SelectedNode")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(UserControl), 1)
            });
            border.SetBinding(Border.BorderBrushProperty, borderBrushBind);

            border.SetValue(Border.BorderThicknessProperty, new Thickness(2.5));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

            // 绑定发光特效 (DropShadowEffect) - 当 Node.IsRunning 为 True 时生效
            var glowEffectBind = new Binding("Node.IsRunning")
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Converter = new RunningGlowEffectConv()
            };
            border.SetBinding(Border.EffectProperty, glowEffectBind);

            // ==========================================
            // 2. 节点内部标题与信息布局
            // ==========================================
            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.MarginProperty, new Thickness(10, 6, 10, 6));

            // 节点名称 DisplayName
            var txtName = new FrameworkElementFactory(typeof(TextBlock));
            var nameBind = new Binding("Node.DisplayName") { RelativeSource = RelativeSource.TemplatedParent };
            txtName.SetBinding(TextBlock.TextProperty, nameBind);
            txtName.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            txtName.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            txtName.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

            // 节点类型标识 (Type)
            var txtKind = new FrameworkElementFactory(typeof(TextBlock));
            txtKind.SetBinding(TextBlock.TextProperty, new Binding("Node.Type") { RelativeSource = RelativeSource.TemplatedParent });
            txtKind.SetValue(TextBlock.FontSizeProperty, 10d);
            txtKind.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200)));

            stack.AppendChild(txtName);
            stack.AppendChild(txtKind);
            border.AppendChild(stack);
            grid.AppendChild(border);

            // ==========================================
            // 3. 执行状态提示徽章 Badge (右上角 "▶ 执行中...")
            // ==========================================
            var badgeBorder = new FrameworkElementFactory(typeof(Border));
            badgeBorder.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            badgeBorder.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Top);
            badgeBorder.SetValue(Border.MarginProperty, new Thickness(0, -10, -5, 0));
            badgeBorder.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
            badgeBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            badgeBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#27AE60")));
            badgeBorder.SetValue(Panel.ZIndexProperty, 99);

            badgeBorder.SetBinding(UIElement.VisibilityProperty, new Binding("Node.IsRunning")
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Converter = new BooleanToVisibilityConverter()
            });

            var badgeText = new FrameworkElementFactory(typeof(TextBlock));
            badgeText.SetValue(TextBlock.TextProperty, "▶ 执行中...");
            badgeText.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            badgeText.SetValue(TextBlock.FontSizeProperty, 9d);
            badgeText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);

            badgeBorder.AppendChild(badgeText);
            grid.AppendChild(badgeBorder);

            // ==========================================
            // 2. 统一渲染所有数据端口 (根据 RelativeX, RelativeY 定位)
            // ==========================================
            var allPortsItemsControl = new FrameworkElementFactory(typeof(ItemsControl));

            // 🌟 直接绑定到 FlowNodeBase 中的 AllPorts 集合
            allPortsItemsControl.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Node.AllPorts") { RelativeSource = RelativeSource.TemplatedParent });

            var canvasFactory = new FrameworkElementFactory(typeof(Canvas));
            allPortsItemsControl.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(canvasFactory));

            var portContainerStyle = new Style(typeof(ContentPresenter));
            // 居中偏移量: 端口尺寸为 8px，偏移 -4px 实现以 (RelativeX, RelativeY) 为原点居中
            portContainerStyle.Setters.Add(new Setter(Canvas.LeftProperty, new Binding("RelativeX") { Converter = new OffsetYConverter(-4) }));
            portContainerStyle.Setters.Add(new Setter(Canvas.TopProperty, new Binding("RelativeY") { Converter = new OffsetYConverter(-4) }));
            allPortsItemsControl.SetValue(ItemsControl.ItemContainerStyleProperty, portContainerStyle);

            var portTemplate = new DataTemplate(typeof(NodePort));
            var anchor = new FrameworkElementFactory(typeof(Border));

            anchor.SetValue(Border.WidthProperty, 8.0);
            anchor.SetValue(Border.HeightProperty, 8.0);
            anchor.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); // 圆形端点
            anchor.SetBinding(Border.BackgroundProperty, new Binding("ColorHex") { Converter = new HexToBrushConv() });
            anchor.SetValue(Border.BorderBrushProperty, Brushes.White);
            anchor.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
            anchor.SetValue(Border.CursorProperty, Cursors.Cross);
            anchor.SetBinding(Border.ToolTipProperty, new Binding("PortName"));

            // 控制 Exec 端口隐藏/过滤
            anchor.SetBinding(UIElement.VisibilityProperty, new Binding("Category") { Converter = new ExecPortHiddenConv() });

            // 交互事件绑定...
            anchor.AddHandler(Border.MouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) => {
                if (s is FrameworkElement el && el.DataContext is NodePort port)
                {
                    GetParentFlowEditView()?.StartConnecting(Node, port);
                    e.Handled = true;
                }
            }));

            anchor.AddHandler(Border.MouseLeftButtonUpEvent, new MouseButtonEventHandler((s, e) => {
                if (s is FrameworkElement el && el.DataContext is NodePort port)
                {
                    GetParentFlowEditView()?.EndConnecting(Node, port);
                    e.Handled = true;
                }
            }));

            portTemplate.VisualTree = anchor;
            allPortsItemsControl.SetValue(ItemsControl.ItemTemplateProperty, portTemplate);
            grid.AppendChild(allPortsItemsControl);

            return new ControlTemplate { VisualTree = grid };
        }

        /// <summary>
        /// 节点按下事件：负责选中节点、双击响应（复合节点下挖/普通节点弹框）与支持拖拽节点整体
        /// </summary>
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            // 🌟 防误触核心逻辑：如果点击的是端口端点 (NodePort)，跳过节点拖拽逻辑，交给端口连线处理
            if (e.OriginalSource is FrameworkElement elem && elem.DataContext is NodePort)
            {
                return;
            }

            base.OnMouseLeftButtonDown(e);

            if (GetParentFlowEditView()?.DataContext is FlowVm vm)
            {
                vm.SelectedNode = Node;

                // 🌟 核心修改：处理节点双击响应事件 (ClickCount == 2)
                if (e.ClickCount == 2 && Node != null)
                {
                    // 1. 如果是复合节点 (CompositeFlowNode)，保持原样下挖
                    if (Node is CompositeFlowNode compositeNode)
                    {
                        vm.DrillDownCompositeNode(compositeNode);
                    }
                    // 2. 如果是普通节点，弹窗显示节点参数模型与 XAML 属性设置
                    else
                    {
                        ShowNodePropertyDialog(Node);
                    }

                    e.Handled = true; // 阻止事件向上冒泡，防止触发拖拽
                    return;
                }
            }

            // 开始拖拽节点整体位置
            _isDragging = true;
            _dragStartPoint = e.GetPosition(Window.GetWindow(this));
            _nodeStartPos = new Point(Node.PosX, Node.PosY);
            CaptureMouse();
            e.Handled = true;
        }

        /// <summary>
        /// 弹出普通节点的属性与参数设置窗口
        /// </summary>
        private void ShowNodePropertyDialog(FlowNodeBase node)
        {
            var win = new Grayson.Vison.FlowEdit.Views.NodePropertyWindow
            {
                DataContext = node, // 将节点的 Model / ParameterModel 绑定至弹窗
                Owner = Window.GetWindow(this) // 设置当前 Window 为宿主窗口，保证弹窗居中
            };
            win.ShowDialog();
        }

        /// <summary>
        /// 节点移动事件：实时更新 Node.PosX 和 Node.PosY
        /// </summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDragging && Node != null)
            {
                Point currentPoint = e.GetPosition(Window.GetWindow(this));
                Vector diff = currentPoint - _dragStartPoint;
                Node.PosX = _nodeStartPos.X + diff.X;
                Node.PosY = _nodeStartPos.Y + diff.Y;
            }
        }

        /// <summary>
        /// 鼠标抬起结束节点拖拽
        /// </summary>
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_isDragging)
            {
                _isDragging = false;
                ReleaseMouseCapture();
            }
        }
    }

    #region 转换器工具类

    /// <summary>
    /// 当节点执行时，生成发光/阴影特效
    /// </summary>
    public class RunningGlowEffectConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isRunning && isRunning)
            {
                return new DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString("#00FF66"),
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.95
                };
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// 端口中心点 Y 轴坐标偏移转换器
    /// </summary>
    public class OffsetYConverter : IValueConverter
    {
        private readonly double _offset;
        public OffsetYConverter(double offset) { _offset = offset; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double y) return y + _offset;
            if (value is int iy) return iy + _offset;
            return _offset;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// 16进制颜色字符串转 WPF Brush
    /// </summary>
    public class HexToBrushConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    return new SolidColorBrush(color);
                }
                catch { }
            }
            return Brushes.DodgerBlue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// 控制端点用纯圆 (CornerRadius=5)，数据端点用小方块 (CornerRadius=1)
    /// </summary>
    public class PortShapeConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PortCategory cat && cat == PortCategory.Exec)
            {
                return new CornerRadius(5); // 纯圆形
            }
            return new CornerRadius(1);     // 小方块
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// 控制端点 10px，数据端点更小一点 (8px)
    /// </summary>
    public class PortSizeConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PortCategory cat && cat == PortCategory.Exec)
            {
                return 10.0; // 控制端点尺寸大一点
            }
            return 8.0;  // 数据端点尺寸小一点
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// 根据端口类别 (Category) 与全局开关 (ShowDataPorts) 决定端口端点是否显示
    /// </summary>
    public class PortVisibilityConv : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is PortCategory category && values[1] is bool showDataPorts)
            {
                if (category == PortCategory.Exec)
                {
                    return Visibility.Visible;
                }

                return showDataPorts ? Visibility.Visible : Visibility.Collapsed;
            }

            return Visibility.Visible;
        }

        public object[] ConvertBack(object values, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ExecPortHiddenConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PortCategory cat && cat == PortCategory.Exec)
            {
                return Visibility.Collapsed; // 完全隐藏 Exec 端口
            }
            return Visibility.Visible;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
    #endregion
}