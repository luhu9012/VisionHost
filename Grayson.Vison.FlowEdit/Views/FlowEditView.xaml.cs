using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vison.FlowEdit.ViewModels;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;



namespace Grayson.Vison.FlowEdit.Views
{

    /// <summary>
    /// FlowEditView.xaml 的交互逻辑（包含连线拖拽与画布平移/缩放）
    /// </summary>
    public partial class FlowEditView : UserControl
    {
        private FlowNodeBase _connectingSourceNode;
        private NodePort _connectingSourcePort;

        private Point _panStart;
        private bool _isPanning;

        private Point _dragStartPoint;

        public FlowEditView()
        {
            InitializeComponent();
            // 监听 DataContext 变更并订阅 OnNodeExecuting 事件
            this.DataContextChanged += FlowEditView_DataContextChanged;


            // ?? 全局鼠标按下监听：点击抽屉外部时自动收起
            this.PreviewMouseDown += FlowEditView_PreviewMouseDown;
        }
        /// <summary>
        /// 自动将 ScrollViewer 平移，使当前正在执行的节点平滑居中
        /// </summary>
        private void ScrollToNode(FlowNodeBase node)
        {
            if (node == null) return;

            // 计算节点在 ContentGrid 中的缩放后物理坐标
            double scale = CanvasScale.ScaleX;
            double targetX = (node.PosX + 80) * scale;  // 节点中心 X (按宽度160计算)
            double targetY = (node.PosY + 40) * scale;  // 节点中心 Y (按高度80计算)

            // 视图窗口尺寸
            double viewportWidth = FlowScrollViewer.ViewportWidth;
            double viewportHeight = FlowScrollViewer.ViewportHeight;

            // 计算让节点处于画布视野正中央所需的 Offset
            double offsetX = targetX - (viewportWidth / 2);
            double offsetY = targetY - (viewportHeight / 2);

            // 滚动平移
            FlowScrollViewer.ScrollToHorizontalOffset(Math.Max(0, offsetX));
            FlowScrollViewer.ScrollToVerticalOffset(Math.Max(0, offsetY));
        }

        private void FlowEditView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is FlowVm oldVm)
            {
                oldVm.OnNodeExecuting -= ScrollToNode;
            }
            if (e.NewValue is FlowVm newVm)
            {
                newVm.OnNodeExecuting += ScrollToNode;
            }
        }
        #region 解决工具箱拖拽与双击不生效
        // 1. 记录按下鼠标的起始位置
        private void ToolItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (sender is FrameworkElement element && element.DataContext is UnitMeta meta)
                {
                    if (DataContext is FlowVm vm)
                    {
                        vm.AddNodeFromTemplate(meta, new Point2D(300, 200));
                        CloseDrawer(); // ?? 双击完成后自动收起抽屉
                        e.Handled = true;
                        return;
                    }
                }
            }
            _dragStartPoint = e.GetPosition(null);
        }

        // 2. 鼠标移动时检测并触发拖拽（不需要先选中！）
        private void ToolItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPos = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is FrameworkElement element && element.DataContext is UnitMeta meta)
                    {
                        DataObject dragData = new DataObject(meta);
                        CloseDrawer(); // ?? 拖拽拖出抽屉的瞬间自动收起抽屉，方便放置到画布
                        DragDrop.DoDragDrop(element, dragData, DragDropEffects.Copy);
                    }
                }
            }
        }


        #endregion

        #region 1. 拖拽组件到画布生成节点
        private void ListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBox listBox && listBox.SelectedItem is UnitMeta meta)
            {
                DragDrop.DoDragDrop(listBox, meta, DragDropEffects.Copy);
            }
        }

        private void Canvas_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(UnitMeta)) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Canvas_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(UnitMeta)) is UnitMeta meta)
            {
                if (sender is Canvas canvas && DataContext is FlowVm vm)
                {
                    Point dropPos = e.GetPosition(canvas);
                    vm.AddNodeFromMeta(meta, new Point2D(dropPos.X, dropPos.Y));
                }
            }
        }
        #endregion

        #region 2. 画布缩放与平移 (Pan & Zoom)
        /// <summary>
        /// 滚轮仅缩放节点和连线（背景保持不变）
        /// </summary>
        private void FlowCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoom = e.Delta > 0 ? 1.1 : 0.9;
            double newScale = CanvasScale.ScaleX * zoom;

            // 限制缩放比例在 0.4 到 2.5 之间
            if (newScale >= 0.4 && newScale <= 2.5)
            {
                CanvasScale.ScaleX = newScale;
                CanvasScale.ScaleY = newScale;
            }

            e.Handled = true; // 阻止外层 ScrollViewer 默认滚动
        }

        /// <summary>
        /// 鼠标右键按住拖拽画布平移
        /// </summary>
        private void FlowCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            CloseDrawer(); // ?? 拖拽平移画布时自动收起
            if (e.OriginalSource is Canvas || e.OriginalSource is System.Windows.Shapes.Path)
            {
                _isPanning = true;
                _panStart = e.GetPosition(FlowScrollViewer);
                FlowCanvas.CaptureMouse();
            }
        }

        private void FlowCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                FlowCanvas.ReleaseMouseCapture();
            }
        }

        /// <summary>
        /// 鼠标左键点击画布空白处取消选中
        /// </summary>
        private void FlowCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CloseDrawer(); // ?? 点击画布取消选择时顺手收起抽屉
            if (e.OriginalSource is Canvas || e.OriginalSource is System.Windows.Shapes.Path)
            {
                if (DataContext is FlowVm vm)
                {
                    vm.SelectedNode = null;
                }
            }
        }
        #endregion
        #region 3. 拖拽建立连线交互

        /// <summary>
        /// 开始连线（由 NodeControl 上的 OutputPort 点击触发）
        /// </summary>
        public void StartConnecting(FlowNodeBase sourceNode, NodePort sourcePort)
        {
            _connectingSourceNode = sourceNode;
            _connectingSourcePort = sourcePort;

            // ?? 强制清除虚线，保证拖拽时展示实线
            TempPath.StrokeDashArray = null;
            TempPath.StrokeThickness = 2;

            if (!string.IsNullOrEmpty(sourcePort.ColorHex))
            {
                try
                {
                    TempPath.Stroke = (Brush)new BrushConverter().ConvertFromString(sourcePort.ColorHex);
                }
                catch { TempPath.Stroke = Brushes.Orange; }
            }

            TempPath.Visibility = Visibility.Visible;
            FlowCanvas.CaptureMouse();
        }

        /// <summary>
        /// ?? 动态绘制临时连线（鼠标拖拽过程）
        /// </summary>
        private void FlowCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            // A. 右键平移画布逻辑
            if (_isPanning)
            {
                Point currentPos = e.GetPosition(FlowScrollViewer);
                Vector delta = _panStart - currentPos;
                _panStart = currentPos;

                FlowScrollViewer.ScrollToHorizontalOffset(FlowScrollViewer.HorizontalOffset + delta.X);
                FlowScrollViewer.ScrollToVerticalOffset(FlowScrollViewer.VerticalOffset + delta.Y);
                return;
            }

            // B. 动态绘制临时贝塞尔连线逻辑
            if (_connectingSourceNode != null && _connectingSourcePort != null)
            {
                Point mousePos = e.GetPosition(ContentGrid);

                // ?? 获取起点的精准绝对坐标
                Point startPoint = GetPortCenterAbsolutePosition(_connectingSourceNode, _connectingSourcePort);

                TempFigure.StartPoint = startPoint;

                // 根据端口方位计算控点，保证拉出天然的曲线弧度
                switch (_connectingSourcePort.Position)
                {
                    case PortPosition.Top:
                        TempBezier.Point1 = new Point(startPoint.X, startPoint.Y - 50);
                        TempBezier.Point2 = new Point(mousePos.X, mousePos.Y + 50);
                        break;
                    case PortPosition.Bottom:
                        TempBezier.Point1 = new Point(startPoint.X, startPoint.Y + 50);
                        TempBezier.Point2 = new Point(mousePos.X, mousePos.Y - 50);
                        break;
                    case PortPosition.Left:
                        TempBezier.Point1 = new Point(startPoint.X - 50, startPoint.Y);
                        TempBezier.Point2 = new Point(mousePos.X + 50, mousePos.Y);
                        break;
                    case PortPosition.Right:
                    default:
                        TempBezier.Point1 = new Point(startPoint.X + 50, startPoint.Y);
                        TempBezier.Point2 = new Point(mousePos.X - 50, mousePos.Y);
                        break;
                }

                TempBezier.Point3 = mousePos;

                // 吸附检测
                var (targetNode, targetPort) = FindNearbyInputPort(mousePos, 35.0);
                Mouse.OverrideCursor = (targetNode != null && targetPort != null) ? Cursors.Cross : null;
            }
        }

        /// <summary>
        /// ?? 准确计算端口端点在 ContentGrid 画布中的圆心坐标
        /// </summary>
        private Point GetPortCenterAbsolutePosition(FlowNodeBase node, NodePort port)
        {
            if (node == null || port == null) return new Point(0, 0);

            // Node.PosX/PosY + Port.RelativeX/RelativeY 完美契合 8px 端点在 (-4 offset) 后的圆心绝对坐标
            return new Point(
                node.PosX + port.RelativeX,
                node.PosY + port.RelativeY
            );
        }
        /// <summary>
        /// ?? 关键修正：使用 PreviewMouseLeftButtonUp 强行优先捕获鼠标抬起
        /// </summary>
        private void FlowCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_connectingSourceNode != null)
            {
                Point mousePos = e.GetPosition(ContentGrid);

                // 统一检测半径为 35.0（与 MouseMove 保持完完全全一致！）
                var (targetNode, targetPort) = FindNearbyInputPort(mousePos, 35.0);

                if (targetNode != null && targetPort != null)
                {
                    // 成功连接！
                    EndConnecting(targetNode, targetPort);
                }
                else
                {
                    // 无论落在哪里，只要没吸附成功，一律取消连线，释放鼠标捕获！
                    ResetConnecting();
                }

                e.Handled = true; // 阻止事件传递
            }
        }

        /// <summary>
        /// 右键点击打断连线
        /// </summary>
        private void FlowCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_connectingSourceNode != null)
            {
                ResetConnecting();
                e.Handled = true;
            }
        }

        public void EndConnecting(FlowNodeBase targetNode, NodePort targetPort)
        {
            if (_connectingSourceNode != null && targetNode != null && _connectingSourcePort != null && targetPort != null)
            {
                if (DataContext is FlowVm vm)
                {
                    vm.AddConnection(_connectingSourceNode, _connectingSourcePort, targetNode, targetPort);
                }
            }
            ResetConnecting();
        }

        private void ResetConnecting()
        {
            _connectingSourceNode = null;
            _connectingSourcePort = null;
            TempPath.Visibility = Visibility.Collapsed;

            // 还原全局鼠标样式与捕获
            Mouse.OverrideCursor = null;
            if (FlowCanvas.IsMouseCaptured)
            {
                FlowCanvas.ReleaseMouseCapture();
            }
        }

        /// <summary>
        /// 【数学几何算法】：从 ViewModel 的当前流程中，遍历查找距离当前鼠标 Point 最近且在 maxDistance 半径内的 InputPort
        /// </summary>
        private (FlowNodeBase Node, NodePort Port) FindNearbyInputPort(Point mousePoint, double maxDistance)
        {
            if (DataContext is FlowVm vm)
            {
                var nodes = vm.CurrentProcess?.Nodes;
                if (nodes != null && _connectingSourcePort != null)
                {
                    foreach (var node in nodes)
                    {
                        if (node == _connectingSourceNode) continue;

                        if (node.InputPorts != null)
                        {
                            foreach (var port in node.InputPorts)
                            {
                                // ? 修正后：获取精准端点圆心坐标
                                Point portPos = GetPortCenterAbsolutePosition(node, port);

                                double dist = Math.Sqrt(Math.Pow(mousePoint.X - portPos.X, 2) + Math.Pow(mousePoint.Y - portPos.Y, 2));

                                if (dist <= maxDistance)
                                {
                                    return (node, port);
                                }
                            }
                        }
                    }
                }
            }
            return (null, null);
        }
        #endregion

        #region 日志窗口复制
        /// <summary>
        /// 复制选中行：只能从ListBox取选中项，遍历不可避免，数据量一般不大无性能压力
        /// </summary>
        private void LogListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LogListBox?.SelectedItem == null || !(DataContext is FlowVm vm))
                return;

            string line = LogListBox.SelectedItem.ToString();
            if (string.IsNullOrWhiteSpace(line))
                return;

            var nodeMatch = Regex.Match(line, @"节点 \[(?<name>[^\]]+)\]");
            if (!nodeMatch.Success)
                return;

            string nodeName = nodeMatch.Groups["name"].Value;
            if (!vm.TryFocusNodeByDisplayName(nodeName))
            {
                LogBus.Warn("FlowEditView", $"日志定位失败：当前流程未找到节点 [{nodeName}]。");
            }
        }

        private void CopySelectedLogRows(object sender, RoutedEventArgs e)
        {
            if (LogListBox == null || LogListBox.SelectedItems.Count == 0)
                return;

            List<string> lines = new List<string>();
            foreach (object item in LogListBox.SelectedItems)
            {
                lines.Add(item?.ToString() ?? string.Empty);
            }
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }

        /// <summary>
        /// 复制全部：直接拿VM数据源，跳过UI控件，零UI遍历，性能拉满
        /// </summary>
        private void CopyAllLogRows(object sender, RoutedEventArgs e)
        {
            FlowVm vm = DataContext as FlowVm;
            if (vm == null || vm.ExecutionLogs == null)
                return;

            string allText = string.Join(Environment.NewLine, vm.ExecutionLogs);
            Clipboard.SetText(allText);
        }
        #endregion
        /// <summary>
        /// 安全收起抽屉
        /// </summary>
        private void CloseDrawer()
        {
            if (DrawerPanel.Visibility != Visibility.Collapsed)
            {
                DrawerPanel.Visibility = Visibility.Collapsed;
                _activeGroup = null;
            }
        }

        /// <summary>
        /// 当鼠标点击在抽屉和左侧图标栏外部时，自动收起抽屉
        /// </summary>
        private void FlowEditView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (DrawerPanel.Visibility == Visibility.Visible)
            {
                // 获取当前鼠标点击相对抽屉和图标栏的位置
                Point posInDrawer = e.GetPosition(DrawerPanel);
                Point posInNav = e.GetPosition(ToolBoxCategories);

                bool hitDrawer = (posInDrawer.X >= 0 && posInDrawer.X <= DrawerPanel.ActualWidth &&
                                  posInDrawer.Y >= 0 && posInDrawer.Y <= DrawerPanel.ActualHeight);

                bool hitNav = (posInNav.X >= 0 && posInNav.X <= ToolBoxCategories.ActualWidth &&
                               posInNav.Y >= 0 && posInNav.Y <= ToolBoxCategories.ActualHeight);

                // 如果既没点在抽屉里，也没点在左侧图标上，直接关闭抽屉
                if (!hitDrawer && !hitNav)
                {
                    CloseDrawer();
                }
            }
        }
        /// <summary>
        /// 点击左侧图标：切换/展开二级 Drawer 面板
        /// </summary>
        /// 
        private CollectionViewGroup _activeGroup = null;
        private void CategoryItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is CollectionViewGroup group)
            {
                // 如果重复点击同一个分类，则收起抽屉
                if (_activeGroup == group && DrawerPanel.Visibility == Visibility.Visible)
                {
                    DrawerPanel.Visibility = Visibility.Collapsed;
                    _activeGroup = null;
                }
                else
                {
                    // 切换分类并展开抽屉
                    _activeGroup = group;
                    TxtCurrentCategoryTitle.Text = group.Name?.ToString();
                    DrawerItemsControl.ItemsSource = group.Items;
                    DrawerPanel.Visibility = Visibility.Visible;
                }
            }
        }

        /// <summary>
        /// 点击关闭按钮收起抽屉
        /// </summary>
        private void CloseDrawer_Click(object sender, RoutedEventArgs e)
        {
            DrawerPanel.Visibility = Visibility.Collapsed;
            _activeGroup = null;
        }

        public void InvalidateCanvas()
        {
            // 强制刷新 ContentGrid (包含连线 Path 和 NodeControl 容器)
            ContentGrid?.InvalidateArrange();
            ContentGrid?.InvalidateVisual();

            // 强制刷新 FlowCanvas 视觉重绘与擦除
            FlowCanvas?.InvalidateArrange();
            FlowCanvas?.InvalidateVisual();
        }

    }
}