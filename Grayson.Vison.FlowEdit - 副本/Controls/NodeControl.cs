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

            double headerHeight = 28.0; // 顶部 Header/控制端点占用的相对偏移
            double itemRowHeight = 20.0; // 数据端口逐行间隔

            // ----------------------------------------------------
            // 1. 排版输入端口 (InputPorts)
            // ----------------------------------------------------
            var inExecs = node.InputPorts.Where(p => p.Category == PortCategory.Exec).ToList();
            var inDatas = node.InputPorts.Where(p => p.Category == PortCategory.Data).ToList();

            // 控制端点统一在顶部 (Y = 14)
            foreach (var execPort in inExecs)
            {
                execPort.RelativeY = 14.0;
            }

            // 数据端点从 Header 下方开始，自上而下逐行递增
            for (int i = 0; i < inDatas.Count; i++)
            {
                inDatas[i].RelativeY = headerHeight + 12.0 + (i * itemRowHeight);
            }

            // ----------------------------------------------------
            // 2. 排版输出端口 (OutputPorts)
            // ----------------------------------------------------
            var outExecs = node.OutputPorts.Where(p => p.Category == PortCategory.Exec).ToList();
            var outDatas = node.OutputPorts.Where(p => p.Category == PortCategory.Data).ToList();

            // 输出控制端点（如 OK, NG, Pass）在顶部纵向或平行微调
            for (int i = 0; i < outExecs.Count; i++)
            {
                outExecs[i].RelativeY = 14.0 + (i * 16.0);
            }

            // 输出数据端点按行下移
            for (int i = 0; i < outDatas.Count; i++)
            {
                outDatas[i].RelativeY = headerHeight + 12.0 + (i * itemRowHeight);
            }

            // ----------------------------------------------------
            // 3. 动态调整节点整体的高度，防止卡死重叠
            // ----------------------------------------------------
            int maxDataRows = Math.Max(inDatas.Count, outDatas.Count);
            int maxExecRows = Math.Max(inExecs.Count, outExecs.Count);

            double calculatedHeight = headerHeight + (maxDataRows * itemRowHeight) + 16.0;
            if (maxExecRows > 1)
            {
                calculatedHeight = Math.Max(calculatedHeight, 30.0 + (maxExecRows * 16.0));
            }

            // 刷新 ViewModel 上的高度
            node.Height = Math.Max(65.0, calculatedHeight);
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
            // 4. 动态绘制【输入端口集合】(左侧 Canvas 排版)
            // ==========================================
            var inputItemsControl = new FrameworkElementFactory(typeof(ItemsControl));
            inputItemsControl.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Node.InputPorts") { RelativeSource = RelativeSource.TemplatedParent });
            inputItemsControl.SetValue(Grid.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            inputItemsControl.SetValue(Grid.VerticalAlignmentProperty, VerticalAlignment.Stretch);

            var canvasFactoryIn = new FrameworkElementFactory(typeof(Canvas));
            canvasFactoryIn.SetValue(Canvas.WidthProperty, 0d);
            inputItemsControl.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(canvasFactoryIn));

            var inContainerStyle = new Style(typeof(ContentPresenter));
            inContainerStyle.Setters.Add(new Setter(Canvas.LeftProperty, -5d)); // 精确对齐左边缘
            inContainerStyle.Setters.Add(new Setter(Canvas.TopProperty, new Binding("RelativeY") { Converter = new OffsetYConverter(-5) }));
            inputItemsControl.SetValue(ItemsControl.ItemContainerStyleProperty, inContainerStyle);

            var inPortTemplate = new DataTemplate(typeof(NodePort));
            var inAnchor = new FrameworkElementFactory(typeof(Border));

            // 根据 PortCategory 动态设置形状与尺寸
            inAnchor.SetBinding(Border.WidthProperty, new Binding("Category") { Converter = new PortSizeConv() });    // Exec:10, Data:8
            inAnchor.SetBinding(Border.HeightProperty, new Binding("Category") { Converter = new PortSizeConv() });
            inAnchor.SetBinding(Border.CornerRadiusProperty, new Binding("Category") { Converter = new PortShapeConv() }); // Exec:5(纯圆), Data:1(小方块)

            inAnchor.SetBinding(Border.BackgroundProperty, new Binding("ColorHex") { Converter = new HexToBrushConv() });
            inAnchor.SetValue(Border.BorderBrushProperty, Brushes.White);
            inAnchor.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
            inAnchor.SetValue(Border.CursorProperty, Cursors.Cross);

            // 悬停提示 Tooltip (带端口类型提示)
            var inToolTipBind = new Binding("PortName");
            inAnchor.SetBinding(Border.ToolTipProperty, inToolTipBind);

            // 鼠标抬起结束连线
            inAnchor.AddHandler(Border.MouseLeftButtonUpEvent, new MouseButtonEventHandler((s, e) => {
                if (s is FrameworkElement el && el.DataContext is NodePort port)
                {
                    GetParentFlowEditView()?.EndConnecting(Node, port);
                    e.Handled = true; // 阻止冒泡，避免触发节点的点击事件
                }
            }));

            // 绑定 Visibility (多路绑定：当前端口 Category + View 层的 ShowDataPorts)
            var inVisMultiBinding = new MultiBinding { Converter = new PortVisibilityConv() };
            inVisMultiBinding.Bindings.Add(new Binding("Category"));
            inVisMultiBinding.Bindings.Add(new Binding("DataContext.ShowDataPorts")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(FlowEditView), 1)
            });
            inAnchor.SetBinding(Border.VisibilityProperty, inVisMultiBinding);

            inPortTemplate.VisualTree = inAnchor;
            inputItemsControl.SetValue(ItemsControl.ItemTemplateProperty, inPortTemplate);
            grid.AppendChild(inputItemsControl);

            // ==========================================
            // 5. 动态绘制【输出端口集合】(右侧 Canvas 排版)
            // ==========================================
            var outputItemsControl = new FrameworkElementFactory(typeof(ItemsControl));
            outputItemsControl.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Node.OutputPorts") { RelativeSource = RelativeSource.TemplatedParent });
            outputItemsControl.SetValue(Grid.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            outputItemsControl.SetValue(Grid.VerticalAlignmentProperty, VerticalAlignment.Stretch);

            var canvasFactoryOut = new FrameworkElementFactory(typeof(Canvas));
            canvasFactoryOut.SetValue(Canvas.WidthProperty, 0d);
            outputItemsControl.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(canvasFactoryOut));

            var outContainerStyle = new Style(typeof(ContentPresenter));
            outContainerStyle.Setters.Add(new Setter(Canvas.LeftProperty, -5d));
            outContainerStyle.Setters.Add(new Setter(Canvas.TopProperty, new Binding("RelativeY") { Converter = new OffsetYConverter(-5) }));
            outputItemsControl.SetValue(ItemsControl.ItemContainerStyleProperty, outContainerStyle);

            var outPortTemplate = new DataTemplate(typeof(NodePort));
            var outAnchor = new FrameworkElementFactory(typeof(Border));

            // 根据 PortCategory 动态设置形状与尺寸
            outAnchor.SetBinding(Border.WidthProperty, new Binding("Category") { Converter = new PortSizeConv() });   // Exec:10, Data:8
            outAnchor.SetBinding(Border.HeightProperty, new Binding("Category") { Converter = new PortSizeConv() });
            outAnchor.SetBinding(Border.CornerRadiusProperty, new Binding("Category") { Converter = new PortShapeConv() }); // Exec:5(纯圆), Data:1(小方块)

            outAnchor.SetBinding(Border.BackgroundProperty, new Binding("ColorHex") { Converter = new HexToBrushConv() });
            outAnchor.SetValue(Border.BorderBrushProperty, Brushes.White);
            outAnchor.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
            outAnchor.SetValue(Border.CursorProperty, Cursors.Cross);

            outAnchor.SetBinding(Border.ToolTipProperty, new Binding("PortName"));

            // 鼠标按下开始拉出连线
            outAnchor.AddHandler(Border.MouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) => {
                if (s is FrameworkElement el && el.DataContext is NodePort port)
                {
                    GetParentFlowEditView()?.StartConnecting(Node, port);
                    e.Handled = true; // 🌟 关键：吃掉事件，防止点端点拉线时同时触发节点的卡片拖拽
                }
            }));

            // 绑定 Visibility 控制是否显示数据端点
            var outVisMultiBinding = new MultiBinding { Converter = new PortVisibilityConv() };
            outVisMultiBinding.Bindings.Add(new Binding("Category"));
            outVisMultiBinding.Bindings.Add(new Binding("DataContext.ShowDataPorts")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(FlowEditView), 1)
            });
            outAnchor.SetBinding(Border.VisibilityProperty, outVisMultiBinding);

            outPortTemplate.VisualTree = outAnchor;
            outputItemsControl.SetValue(ItemsControl.ItemTemplateProperty, outPortTemplate);
            grid.AppendChild(outputItemsControl);

            return new ControlTemplate { VisualTree = grid };
        }

        /// <summary>
        /// 节点按下事件：负责选中节点与支持拖拽节点整体
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

                // 双击下钻组合节点 (CompositeFlowNode)
                if (e.ClickCount == 2 && Node is CompositeFlowNode compositeNode)
                {
                    vm.DrillDownCompositeNode(compositeNode);
                    e.Handled = true; // 阻止冒泡
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
            // values[0]: PortCategory (Exec / Data)
            // values[1]: bool (ShowDataPorts 开关状态)
            if (values.Length >= 2 && values[0] is PortCategory category && values[1] is bool showDataPorts)
            {
                // 控制流端点 (Exec) 始终保持可见
                if (category == PortCategory.Exec)
                {
                    return Visibility.Visible;
                }

                // 数据流端点 (Data) 由全局开关控制
                return showDataPorts ? Visibility.Visible : Visibility.Collapsed;
            }

            return Visibility.Visible;
        }

        public object[] ConvertBack(object values, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    #endregion
}