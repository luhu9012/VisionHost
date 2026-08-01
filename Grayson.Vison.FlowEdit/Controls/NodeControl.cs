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
            MinHeight = 65;
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

            double nodeWidth = this.Width > 0 ? this.Width : 160.0;
            double minNodeHeight = 70.0;

            var inDataPorts = node.InputPorts.Where(p => p.Category == PortCategory.Data).ToList();
            var outDataPorts = node.OutputPorts.Where(p => p.Category == PortCategory.Data).ToList();

            var topPorts = inDataPorts.Take(3).ToList();
            var leftPorts = inDataPorts.Skip(3).ToList();

            var bottomPorts = outDataPorts.Take(3).ToList();
            var rightPorts = outDataPorts.Skip(3).ToList();

            for (int i = 0; i < topPorts.Count; i++)
            {
                var port = topPorts[i];
                port.Position = PortPosition.Top;
                port.RelativeX = (nodeWidth / (topPorts.Count + 1)) * (i + 1);
                port.RelativeY = 0;
            }

            for (int i = 0; i < bottomPorts.Count; i++)
            {
                var port = bottomPorts[i];
                port.Position = PortPosition.Bottom;
                port.RelativeX = (nodeWidth / (bottomPorts.Count + 1)) * (i + 1);
                port.RelativeY = minNodeHeight;
            }

            double sideRowHeight = 22.0;
            double sideStartTop = 25.0;
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
            double calculatedHeight = Math.Max(minNodeHeight, sideStartTop + (maxSideRows * sideRowHeight) + 15.0);

            node.Height = calculatedHeight;

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

            // 1. 节点背景 Border (改用统一的 CategoryMetaConverter)
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding("Node.Category")
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Converter = new CategoryMetaConverter(),
                ConverterParameter = "Brush"
            });

            var borderBrushBind = new MultiBinding { Converter = new NodeBorderConv() };
            borderBrushBind.Bindings.Add(new Binding("Node") { RelativeSource = RelativeSource.TemplatedParent });
            borderBrushBind.Bindings.Add(new Binding("DataContext.SelectedNode")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(UserControl), 1)
            });
            border.SetBinding(Border.BorderBrushProperty, borderBrushBind);

            border.SetValue(Border.BorderThicknessProperty, new Thickness(2.5));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

            var glowEffectBind = new Binding("Node.IsRunning")
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Converter = new RunningGlowEffectConv()
            };
            border.SetBinding(Border.EffectProperty, glowEffectBind);

            // 2. 节点内部标题与信息布局
            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.MarginProperty, new Thickness(10, 6, 10, 6));

            var txtName = new FrameworkElementFactory(typeof(TextBlock));
            var nameBind = new Binding("Node.DisplayName") { RelativeSource = RelativeSource.TemplatedParent };
            txtName.SetBinding(TextBlock.TextProperty, nameBind);
            txtName.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            txtName.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            txtName.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

            var txtKind = new FrameworkElementFactory(typeof(TextBlock));
            txtKind.SetBinding(TextBlock.TextProperty, new Binding("Node.Type") { RelativeSource = RelativeSource.TemplatedParent });
            txtKind.SetValue(TextBlock.FontSizeProperty, 10d);
            txtKind.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200)));

            stack.AppendChild(txtName);
            stack.AppendChild(txtKind);
            border.AppendChild(stack);
            grid.AppendChild(border);

            // 3. 执行状态提示徽章 Badge
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

            // 4. 端口渲染
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
                    if (Node is CompositeFlowNode compositeNode)
                    {
                        vm.DrillDownCompositeNode(compositeNode);
                    }
                    else
                    {
                        ShowNodePropertyDialog(Node);
                    }

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

        private void ShowNodePropertyDialog(FlowNodeBase node)
        {
            var win = new Grayson.Vison.FlowEdit.Views.NodePropertyWindow
            {
                DataContext = node,
                Owner = Window.GetWindow(this)
            };
            win.ShowDialog();
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