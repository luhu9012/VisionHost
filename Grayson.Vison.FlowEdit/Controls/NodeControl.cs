using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.ViewModels;
using Grayson.Vison.FlowEdit.Converters;
using Grayson.Vison.FlowEdit.ViewModels;
using Grayson.Vison.FlowEdit.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
            Width = 135;        // 🌟 VisionMaster 经典胶囊宽度
            MinHeight = 32;     // 🌟 经典胶囊高度
            Template = GetNodeTemplate();

            this.SetBinding(FrameworkElement.HeightProperty, new Binding("Node.Height")
            {
                RelativeSource = RelativeSource.Self,
                Mode = BindingMode.TwoWay
            });
        }

        private static void OnNodePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is NodeControl control && e.NewValue is FlowNodeBase newNode)
            {
                control.AutoLayoutNodePorts(newNode);
            }
        }

        public void AutoLayoutNodePorts(FlowNodeBase node)
        {
            if (node == null) return;

            double nodeWidth = this.Width > 0 ? this.Width : 135.0;
            double minNodeHeight = 32.0;

            var inDataPorts = node.InputPorts.Where(p => p.Category == PortCategory.Data).ToList();
            var outDataPorts = node.OutputPorts.Where(p => p.Category == PortCategory.Data).ToList();

            var topPorts = inDataPorts.Take(3).ToList();
            var leftPorts = inDataPorts.Skip(3).ToList();

            var bottomPorts = outDataPorts.Take(3).ToList();
            var rightPorts = outDataPorts.Skip(3).ToList();

            // 1. 顶端流程与数据入口 (Top-In)
            for (int i = 0; i < topPorts.Count; i++)
            {
                var port = topPorts[i];
                port.Position = PortPosition.Top;
                port.RelativeX = (nodeWidth / (topPorts.Count + 1)) * (i + 1);
                port.RelativeY = 0;
            }

            // 2. 侧边扩展端口（若有）
            double sideRowHeight = 14.0;
            double sideStartTop = 8.0;
            for (int i = 0; i < leftPorts.Count; i++)
            {
                var port = leftPorts[i];
                port.Position = PortPosition.Left;
                port.RelativeX = 0;
                port.RelativeY = sideStartTop + (i * sideRowHeight);
            }

            for (int i = 0; i < rightPorts.Count; i++)
            {
                var port = rightPorts[i];
                port.Position = PortPosition.Right;
                port.RelativeX = nodeWidth;
                port.RelativeY = sideStartTop + (i * sideRowHeight);
            }

            int maxSideRows = Math.Max(leftPorts.Count, rightPorts.Count);
            double calculatedHeight = Math.Max(minNodeHeight, sideStartTop + (maxSideRows * sideRowHeight) + 8.0);

            node.Height = calculatedHeight;

            // 3. 底端流程与数据出口 (Bottom-Out)
            for (int i = 0; i < bottomPorts.Count; i++)
            {
                var port = bottomPorts[i];
                port.Position = PortPosition.Bottom;
                port.RelativeX = (nodeWidth / (bottomPorts.Count + 1)) * (i + 1);
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

        /// <summary>
        /// 动态生成画布节点的控件模板（完全代码构建，替代XAML Template）
        /// 整体层级结构：根Grid
        /// ├─ 底层Border：节点底色、圆角、选中/报错边框、运行发光特效
        /// │   └─ StackPanel：节点名称+类型两行文字
        /// ├─ 右上角徽章Border：节点正在运行时才显示「执行中」角标
        /// └─ ItemsControl(Canvas面板)：渲染所有输入/输出端口小圆点，支持拖拽连线
        /// </summary>
        private ControlTemplate GetNodeTemplate()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));

            // 1. 外层主卡片 Border (VM 经典深色胶囊形状)
            var border = new FrameworkElementFactory(typeof(Border));

            // 🌟 动态背景色绑定 (正常分类色 vs 报错深红色)
            var bgMultiBind = new MultiBinding { Converter = new NodeBackgroundConv() };
            bgMultiBind.Bindings.Add(new Binding("Node.Category") { RelativeSource = RelativeSource.TemplatedParent });
            bgMultiBind.Bindings.Add(new Binding("Node.HasError") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BackgroundProperty, bgMultiBind);

            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(2.0)); // 适当加厚边框感

            // 动态边框绑定 (选中/报错/高亮)
            var borderBrushBind = new MultiBinding { Converter = new NodeBorderConv() };
            borderBrushBind.Bindings.Add(new Binding("Node") { RelativeSource = RelativeSource.TemplatedParent });
            borderBrushBind.Bindings.Add(new Binding("Node.HasError") { RelativeSource = RelativeSource.TemplatedParent });
            borderBrushBind.Bindings.Add(new Binding("DataContext.SelectedNode")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(UserControl), 1)
            });
            border.SetBinding(Border.BorderBrushProperty, borderBrushBind);

            // 🌟 动态 Effect 绑定 (运行绿光 vs 报错红光)
            var glowEffectBind = new MultiBinding { Converter = new NodeGlowEffectConv() };
            glowEffectBind.Bindings.Add(new Binding("Node.IsRunning") { RelativeSource = RelativeSource.TemplatedParent });
            glowEffectBind.Bindings.Add(new Binding("Node.HasError") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.EffectProperty, glowEffectBind);

            // 2. 节点内部内容区 [左侧节点Icon | 中间名称 | 右侧状态指示点]
            var contentGrid = new FrameworkElementFactory(typeof(Grid));
            contentGrid.SetValue(Grid.MarginProperty, new Thickness(4, 2, 8, 2));

            var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col1.SetValue(ColumnDefinition.WidthProperty, new GridLength(26));
            var col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col2.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var col3 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col3.SetValue(ColumnDefinition.WidthProperty, new GridLength(8));

            contentGrid.AppendChild(col1);
            contentGrid.AppendChild(col2);
            contentGrid.AppendChild(col3);

            // 左侧胶囊圆圈图标
            var iconBorder = new FrameworkElementFactory(typeof(Border));
            iconBorder.SetValue(Grid.ColumnProperty, 0);
            iconBorder.SetValue(Border.WidthProperty, 24.0);
            iconBorder.SetValue(Border.HeightProperty, 24.0);
            iconBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            iconBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")));
            iconBorder.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            iconBorder.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);

            var iconTxt = new FrameworkElementFactory(typeof(TextBlock));
            iconTxt.SetBinding(TextBlock.TextProperty, new Binding("Node")
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Converter = new NodeIconConverter(),
                FallbackValue = "📷"
            });
            iconTxt.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI Emoji"));
            iconTxt.SetValue(TextBlock.FontSizeProperty, 12d);
            iconTxt.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            iconTxt.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            iconTxt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

            iconBorder.AppendChild(iconTxt);

            // 中间单行节点名称
            var txtName = new FrameworkElementFactory(typeof(TextBlock));
            txtName.SetValue(Grid.ColumnProperty, 1);
            txtName.SetBinding(TextBlock.TextProperty, new Binding("Node.DisplayName") { RelativeSource = RelativeSource.TemplatedParent });
            txtName.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            txtName.SetValue(TextBlock.FontSizeProperty, 11.5d);
            txtName.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F1F1")));
            txtName.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            txtName.SetValue(TextBlock.MarginProperty, new Thickness(6, 0, 4, 0));
            txtName.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

            // 🌟 右侧状态指示灯：在 运行中 (IsRunning) 或 报错 (HasError) 时均显示，颜色根据状态切换
            var statusDot = new FrameworkElementFactory(typeof(Ellipse));
            statusDot.SetValue(Grid.ColumnProperty, 2);
            statusDot.SetValue(Ellipse.WidthProperty, 6.0);
            statusDot.SetValue(Ellipse.HeightProperty, 6.0);
            statusDot.SetValue(Ellipse.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            statusDot.SetValue(Ellipse.VerticalAlignmentProperty, VerticalAlignment.Center);

            // 根据 HasError 动态决定点灯颜色（报错变红，运行变绿）
            statusDot.SetBinding(Shape.FillProperty, new Binding("Node.HasError")
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Converter = new NodeStatusDotBrushConv()
            });

            // 只有在 运行中 或 报错 时才显示状态点
            var dotVisMultiBind = new MultiBinding
            {
                Converter = new BaseMultiConverterLambda((values) =>
                {
                    bool isRunning = values.Length > 0 && values[0] is bool r && r;
                    bool hasError = values.Length > 1 && values[1] is bool e && e;
                    return (isRunning || hasError) ? Visibility.Visible : Visibility.Collapsed;
                })
            };
            dotVisMultiBind.Bindings.Add(new Binding("Node.IsRunning") { RelativeSource = RelativeSource.TemplatedParent });
            dotVisMultiBind.Bindings.Add(new Binding("Node.HasError") { RelativeSource = RelativeSource.TemplatedParent });
            statusDot.SetBinding(UIElement.VisibilityProperty, dotVisMultiBind);

            contentGrid.AppendChild(iconBorder);
            contentGrid.AppendChild(txtName);
            contentGrid.AppendChild(statusDot);

            border.AppendChild(contentGrid);
            grid.AppendChild(border);

            // 3. 连线端口渲染 (Top/Bottom 挂载点)
            var allPortsItemsControl = new FrameworkElementFactory(typeof(ItemsControl));
            allPortsItemsControl.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Node.AllPorts") { RelativeSource = RelativeSource.TemplatedParent });

            var canvasFactory = new FrameworkElementFactory(typeof(Canvas));
            allPortsItemsControl.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(canvasFactory));

            var portContainerStyle = new Style(typeof(ContentPresenter));
            portContainerStyle.Setters.Add(new Setter(Canvas.LeftProperty, new Binding("RelativeX") { Converter = new OffsetYConverter(-4) }));
            portContainerStyle.Setters.Add(new Setter(Canvas.TopProperty, new Binding("RelativeY") { Converter = new OffsetYConverter(-4) }));
            allPortsItemsControl.SetValue(ItemsControl.ItemContainerStyleProperty, portContainerStyle);

            var portTemplate = new DataTemplate(typeof(NodePort));
            var anchor = new FrameworkElementFactory(typeof(Border));

            anchor.SetValue(Border.WidthProperty, 8.0);
            anchor.SetValue(Border.HeightProperty, 8.0);
            anchor.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            anchor.SetBinding(Border.BackgroundProperty, new Binding("ColorHex") { Converter = new HexToBrushConv() });
            anchor.SetValue(Border.BorderBrushProperty, Brushes.White);
            anchor.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
            anchor.SetValue(Border.CursorProperty, Cursors.Cross);
            anchor.SetBinding(Border.ToolTipProperty, new Binding("PortName"));

            anchor.SetBinding(UIElement.VisibilityProperty, new Binding("Category") { Converter = new ExecPortHiddenConv() });

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
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement elem && elem.DataContext is NodePort)
            {
                return;
            }

            base.OnMouseLeftButtonDown(e);

            if (GetParentFlowEditView()?.DataContext is FlowVm vm)
            {
                vm.SelectedNode = Node;

                if (e.ClickCount == 2 && Node != null)
                {
                    // 只转发，业务全部交给VM处理
                    vm.OnNodeDoubleClicked(Node);
                    e.Handled = true;
                    return;
                }
            }

            _isDragging = true;
            _dragStartPoint = e.GetPosition(Window.GetWindow(this));
            _nodeStartPos = new Point(Node.PosX, Node.PosY);
            CaptureMouse();
            e.Handled = true;
        }

      

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
}