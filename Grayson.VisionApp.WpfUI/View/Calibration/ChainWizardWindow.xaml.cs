using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    /// <summary>
    /// 链向导窗口（范式2，R3）。交互约定：
    ///   · 左键点击图像 = 把点击处像素回填到点对表格【选中行】（Stretch=None，按 ActualWidth 比例换算，DIP 无关）；
    ///   · 下相机图像右键 = 写入差分基准像素 DeltaRefPixel（吸嘴 U 轴图像投影 R_cdown）；
    ///   · 拟合/校验/保存逻辑全部在 ChainWizardViewModel（形状硬拦 + G0~G4 门禁 + fail-closed 落盘）。
    /// </summary>
    public partial class ChainWizardWindow : Window
    {
        private readonly ChainWizardViewModel _vm = new ChainWizardViewModel();

        public ChainWizardWindow(string stationCode = null)
        {
            InitializeComponent();
            if (!string.IsNullOrWhiteSpace(stationCode)) _vm.StationCode = stationCode;
            DataContext = _vm;
        }

        //---------------------------------------------------------------------
        // 图像载入与像素拾取
        //---------------------------------------------------------------------

        private static BitmapImage LoadBitmap(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 释放文件句柄（现场要反复重拍）
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private void LoadPickImage_Click(object sender, RoutedEventArgs e)
        {
            string path = PickImageFile();
            if (path != null) PickImage.Source = LoadBitmap(path);
        }

        private void LoadDownImage_Click(object sender, RoutedEventArgs e)
        {
            string path = PickImageFile();
            if (path != null) DownImage.Source = LoadBitmap(path);
        }

        private static string PickImageFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择标定采集图像",
                Filter = "图像|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|全部文件|*.*",
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        /// <summary>点击处 → 原图像素（按显示区/位图宽高比换算，与位图 DPI 无关）</summary>
        private static void ToPixel(Image img, Point p, out double col, out double row)
        {
            var bmp = img.Source as BitmapSource;
            if (bmp == null)
            {
                col = row = 0;
                return;
            }
            double sx = img.ActualWidth > 0 ? bmp.PixelWidth / img.ActualWidth : 1.0;
            double sy = img.ActualHeight > 0 ? bmp.PixelHeight / img.ActualHeight : 1.0;
            col = p.X * sx;
            row = p.Y * sy;
        }

        private void PickImage_Click(object sender, MouseButtonEventArgs e)
        {
            double col, row;
            ToPixel(PickImage, e.GetPosition(PickImage), out col, out row);
            _vm.PickCamera.SetPixelFromClick(col, row);
        }

        private void DownImage_Click(object sender, MouseButtonEventArgs e)
        {
            double col, row;
            ToPixel(DownImage, e.GetPosition(DownImage), out col, out row);
            _vm.DownCamera.SetPixelFromClick(col, row);
        }

        private void DownImage_RightClick(object sender, MouseButtonEventArgs e)
        {
            double col, row;
            ToPixel(DownImage, e.GetPosition(DownImage), out col, out row);
            _vm.DownCamera.SetDeltaRefFromClick(col, row);
        }

        //---------------------------------------------------------------------
        // 点对编辑 / 拟合 / 校验 / 保存（全部转发 VM）
        //---------------------------------------------------------------------

        private void AddPickPoint_Click(object sender, RoutedEventArgs e) { _vm.PickCamera.AddPoint(); }
        private void RemovePickPoint_Click(object sender, RoutedEventArgs e) { _vm.PickCamera.RemovePoint(_vm.PickCamera.SelectedRow); }
        private void FitPick_Click(object sender, RoutedEventArgs e) { _vm.PickCamera.Fit(); }
        private void AddDownPoint_Click(object sender, RoutedEventArgs e) { _vm.DownCamera.AddPoint(); }
        private void RemoveDownPoint_Click(object sender, RoutedEventArgs e) { _vm.DownCamera.RemovePoint(_vm.DownCamera.SelectedRow); }
        private void FitDown_Click(object sender, RoutedEventArgs e) { _vm.DownCamera.Fit(); }
        private void Validate_Click(object sender, RoutedEventArgs e) { _vm.Validate(); }
        private void Save_Click(object sender, RoutedEventArgs e) { _vm.Save(); }
    }
}
