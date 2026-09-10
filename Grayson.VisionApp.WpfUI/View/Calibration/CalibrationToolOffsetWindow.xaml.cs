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
            var viewPoint = e.GetPosition(ImageHost);
            double row, col;
            if (ImageHost.TryGetImagePointAt(viewPoint, out row, out col))
            {
                ToolOffsetVm.ApplyPickReal(row, col);
                e.Handled = true;
            }
        }

        private void DoneAndClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
