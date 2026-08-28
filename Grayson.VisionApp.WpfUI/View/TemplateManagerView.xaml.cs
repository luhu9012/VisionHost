//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TemplateManagerView.xaml.cs
// 说 明: 模板管理视图 code-behind —— ROI 拖拽框选交互。
//        左键在图像上拖拽 → 画黄色矩形 → 松开后经 HalconImageDisplayHost.
//        TryGetImagePointAt 换算为图像像素坐标写回 VM（兼容缩放/平移后的画面）。
//        框选期间临时禁用宿主控件左键平移（IsPanEnabled=false），松开恢复。
//===================================================================================
using Grayson.Vision.WpfUI.ViewModel;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.View
{
    public partial class TemplateManagerView : UserControl
    {
        private bool _isDraggingRoi;
        private Point _roiStart;

        public TemplateManagerView()
        {
            InitializeComponent();
            Loaded += (s, e) => (DataContext as TemplateManagerViewModel)?.OnViewLoaded();
        }

        private void ImageArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as TemplateManagerViewModel;
            if (vm == null || !vm.HasImage) return;

            // 进入 ROI 框选：临时禁用宿主左键平移，防止拖拽时画面跟着跑
            _isDraggingRoi = true;
            ImageHost.IsPanEnabled = false;
            _roiStart = e.GetPosition(ImageHost);

            RoiRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(RoiRect, _roiStart.X);
            Canvas.SetTop(RoiRect, _roiStart.Y);
            RoiRect.Width = 0;
            RoiRect.Height = 0;
        }

        private void ImageArea_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingRoi) return;

            var p = e.GetPosition(ImageHost);
            double x = Math.Min(_roiStart.X, p.X);
            double y = Math.Min(_roiStart.Y, p.Y);
            Canvas.SetLeft(RoiRect, x);
            Canvas.SetTop(RoiRect, y);
            RoiRect.Width = Math.Abs(p.X - _roiStart.X);
            RoiRect.Height = Math.Abs(p.Y - _roiStart.Y);
        }

        private void ImageArea_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingRoi) return;
            _isDraggingRoi = false;
            ImageHost.IsPanEnabled = true;

            var vm = DataContext as TemplateManagerViewModel;
            if (vm != null && vm.HasImage)
            {
                var p = e.GetPosition(ImageHost);
                if (ImageHost.TryGetImagePointAt(_roiStart, out double r1, out double c1) &&
                    ImageHost.TryGetImagePointAt(p, out double r2, out double c2))
                {
                    // 归一化：左上角 (r1,c1) / 右下角 (r2,c2)
                    vm.RoiRow1 = Math.Min(r1, r2);
                    vm.RoiCol1 = Math.Min(c1, c2);
                    vm.RoiRow2 = Math.Max(r1, r2);
                    vm.RoiCol2 = Math.Max(c1, c2);
                    vm.NotifyRoiSelected();
                }
            }
            RoiRect.Visibility = Visibility.Collapsed;
        }

        private void BtnClearRoi_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as TemplateManagerViewModel)?.ClearRoi();
        }
    }
}
