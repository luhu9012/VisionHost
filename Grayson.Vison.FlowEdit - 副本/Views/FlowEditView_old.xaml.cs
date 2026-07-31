using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.ViewModels;
using Grayson.Vison.FlowEdit.ViewModels;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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
        private ConnectorType _connectingSourceType;

        private Point _panStart;
        private bool _isPanning;

        private Point _dragStartPoint;

        public FlowEditView()
        {
            InitializeComponent();
            // 监听 DataContext 变更并订阅 OnNodeExecuting 事件
            this.DataContextChanged += FlowEditView_DataContextChanged;
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
            // 1. 处理双击动作
            if (e.ClickCount == 2)
            {
                if (sender is FrameworkElement element && element.DataContext is UnitMeta meta)
                {
                    if (DataContext is FlowVm vm)
                    {
                        // 在画布中心坐标生成节点（按需调整生成位置）
                        vm.AddNodeFromTemplate(meta, new Point2D(300, 200));
                        e.Handled = true;
                        return;
                    }
                }
            }

            // 2. 记录按下鼠标的起始位置（为拖拽做准备）
            _dragStartPoint = e.GetPosition(null);
        }

        // 2. 鼠标移动时检测并触发拖拽（不需要先选中！）
        private void ToolItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPos = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPos;

                // 超过最小拖拽距离时启动 DragDrop
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is FrameworkElement element && element.DataContext is UnitMeta meta)
                    {
                        // 修正：直接传递 UnitMeta 实例，与 Canvas_Drop 的类型保持一致
                        DataObject dragData = new DataObject(meta);
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

            // 根据拖拽源端口的颜色设置临时线的颜色
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
                // 获得在 ContentGrid (画布真实坐标系) 上的准确点
                Point mousePos = e.GetPosition(ContentGrid);

                // 绘制连线起点（源节点右侧端口：PosX + 节点宽度160）
                double startY = _connectingSourceNode.PosY + _connectingSourcePort.RelativeY;
                Point startPoint = new Point(_connectingSourceNode.PosX + 160, startY);

                TempFigure.StartPoint = startPoint;
                TempBezier.Point1 = new Point(startPoint.X + 50, startPoint.Y);
                TempBezier.Point2 = new Point(mousePos.X - 50, mousePos.Y);
                TempBezier.Point3 = mousePos;

                // 检测 35px 范围内是否有可连接的 InputPort
                var (targetNode, targetPort) = FindNearbyInputPort(mousePos, 35.0);

                if (targetNode != null && targetPort != null)
                {
                    // 改变指针为十字架
                    Mouse.OverrideCursor = Cursors.Cross;
                }
                else
                {
                    Mouse.OverrideCursor = null;
                }
            }
        }

        /// <summary>
        /// ⚠️ 关键修正：使用 PreviewMouseLeftButtonUp 强行优先捕获鼠标抬起
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
                        // 不能连到自己身上
                        if (node == _connectingSourceNode) continue;

                        if (node.InputPorts != null)
                        {
                            foreach (var port in node.InputPorts)
                            {
                                // 🌟 【关键修复】：如果起点是 Data 端口，目标必须也是 Data 端口！
                                // 避免数据线被旁边的 Exec 端口抢先吸附
                                if (port.Category != _connectingSourcePort.Category)
                                    continue;

                                // 计算输入端口在 ContentGrid 中的绝对物理坐标
                                Point portPos = new Point(node.PosX, node.PosY + port.RelativeY);

                                // 计算鼠标与端口的欧氏距离
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

    }
}