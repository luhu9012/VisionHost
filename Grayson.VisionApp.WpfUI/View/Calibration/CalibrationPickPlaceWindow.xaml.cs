//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationPickPlaceWindow.xaml.cs
// 说 明: 「点哪去哪」轻量校验窗口 code-behind。注入宿主引用给 VM(标记绘制)；
//        覆盖层点选 → TryGetImagePointAt → VM.ApplyPick；订阅 VM.MarkersInvalidated
//        重画图上标记（绿=目标吸点，青=机械位反投影像素）。
//===================================================================================
using System.Windows;
using System.Windows.Input;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    public partial class CalibrationPickPlaceWindow : Window
    {
        public CalibrationPickPlaceViewModel PickPlaceVm { get; }

        public CalibrationPickPlaceWindow(CalibrationProfile profile)
        {
            InitializeComponent();
            PickPlaceVm = new CalibrationPickPlaceViewModel(profile);
            DataContext = PickPlaceVm;
            Title = $"点哪去哪校验 — {profile?.Name}";
            // 标记重画：VM 产出 Markers 集合 → 宿主绘制
            PickPlaceVm.MarkersInvalidated += (s, e) => RedrawMarkers();
            // 关闭收尾：停自开流/恢复触发模式/退订帧事件/释放句柄（设备连接保留——池共享）
            Closing += (s, e) => PickPlaceVm.Cleanup();
        }

        private void Overlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ImageHost == null) return;
            // 宿主坐标（含工具条）→ 图像坐标：TryGetImagePointAtHost 内部扣掉工具条高度
            var viewPoint = e.GetPosition(ImageHost);
            double row, col;
            if (ImageHost.TryGetImagePointAtHost(viewPoint, out row, out col))
            {
                PickPlaceVm.ApplyPick(row, col);
                e.Handled = true;
            }
        }

        /// <summary>按 VM 的 Markers 集合重画图上标记</summary>
        private void RedrawMarkers()
        {
            if (ImageHost == null || PickPlaceVm == null) return;
            ImageHost.ClearMarkers();
            foreach (var m in PickPlaceVm.Markers)
            {
                ImageHost.AddMarkerCross(m.Row, m.Col, m.Size, m.Color, m.Label);
            }
        }

        private void DoneAndClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
