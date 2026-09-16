//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationToolOffsetWindow.xaml.cs
// 说 明: P4 对针补偿窗口 code-behind。注入宿主引用给 VM(标记绘制)；
//        覆盖层点选 → TryGetImagePointAt → VM.ApplyPickReal。
//===================================================================================
using System.Windows;
using System.Windows.Input;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    public partial class CalibrationToolOffsetWindow : Window
    {
        public CalibrationToolOffsetViewModel ToolOffsetVm { get; }

        // B 点拖拽状态（覆盖层坐标换算用）：命中理论点 B 后进入拖拽态，移动实时预览 δ，松开提交
        private bool _draggingB;
        private bool _mouseCaptured;

        public CalibrationToolOffsetWindow(CalibrationProfile profile)
        {
            InitializeComponent();
            ToolOffsetVm = new CalibrationToolOffsetViewModel(profile);
            DataContext = ToolOffsetVm;
            ToolOffsetVm.AttachDisplayHost(ImageHost);
            Title = $"对针补偿 — {profile?.Name}";
            // 关闭收尾：停自开流/恢复触发模式/退订帧事件/释放句柄（设备连接保留——池共享）
            Closing += (s, e) => ToolOffsetVm.Cleanup();
        }

        private void Overlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ImageHost == null) return;
            // 宿主坐标（含工具条）→ 图像坐标：TryGetImagePointAtHost 内部扣掉工具条高度，
            // 避免十字标注落在点击点下方（工具条高度被误算进 row）。
            var viewPoint = e.GetPosition(ImageHost);
            if (!ImageHost.TryGetImagePointAtHost(viewPoint, out double row, out double col)) return;

            // 命中理论点 B → 启动拖拽（预览态）；否则普通点选
            if (ToolOffsetVm.IsHitTheoryPoint(row, col))
            {
                _draggingB = true;
                _mouseCaptured = true;
                ToolOffsetVm.BeginDragB();
                if (sender is System.Windows.UIElement el)
                {
                    el.CaptureMouse();
                }
                e.Handled = true;
                return;
            }

            ToolOffsetVm.ApplyPickReal(row, col);
            e.Handled = true;
        }

        private void Overlay_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingB || ImageHost == null) return;
            var viewPoint = e.GetPosition(ImageHost);
            if (ImageHost.TryGetImagePointAtHost(viewPoint, out double row, out double col))
            {
                ToolOffsetVm.PreviewPickReal(row, col);
                e.Handled = true;
            }
        }

        private void Overlay_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_draggingB || ImageHost == null) return;
            _draggingB = false;
            if (sender is System.Windows.UIElement el && _mouseCaptured)
            {
                el.ReleaseMouseCapture();
                _mouseCaptured = false;
            }
            var viewPoint = e.GetPosition(ImageHost);
            if (ImageHost.TryGetImagePointAtHost(viewPoint, out double row, out double col))
            {
                ToolOffsetVm.EndDragB(row, col);
                e.Handled = true;
            }
        }

        private void DoneAndClose_Click(object sender, RoutedEventArgs e)
        {
            // 非模态打开：不能设 DialogResult（会抛 InvalidOperationException），直接 Close。
            // ToolOffset 已由 VM 在「✅ 写入」时写进 profile；关闭后由 OpenToolOffset 的 Closed 事件落库。
            Close();
        }
    }
}
