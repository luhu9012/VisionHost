using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    /// <summary>
    /// 链向导窗口 v2（工位驱动）。交互约定：
    ///   · 每台链上相机一个 Tab（模板共享同一组 handler，从 sender.DataContext 定位 section）；
    ///   · 左键点击图像 = 把点击处像素回填到点对表格【选中行】（Stretch=None，按 ActualWidth 比例换算，DIP 无关）；
    ///   · 下相机（IsDownCorrect）图像右键 = 写入差分基准像素 DeltaRefPixel（R_cdown，必测）；
    ///   · 主工具指派 = 工具表「设为主工具」（重推导+已填数据按 Id 迁移）；
    ///   · 拟合/校验/保存逻辑全部在 ChainWizardViewModel（形状硬拦 + G0~G4 门禁 + fail-closed 落盘）。
    /// </summary>
    public partial class ChainWizardWindow : Window
    {
        private readonly ChainWizardViewModel _vm;

        public ChainWizardWindow(string stationCode = null)
        {
            InitializeComponent();
            _vm = new ChainWizardViewModel(string.IsNullOrWhiteSpace(stationCode) ? "ST_002" : stationCode);
            DataContext = _vm;
        }

        //---------------------------------------------------------------------
        // 图像载入与像素拾取（模板共享：sender.DataContext = ChainCameraSectionViewModel）
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

        private string PickImageFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择标定采集图像",
                Filter = "图像|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|全部文件|*.*",
            };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
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

        private ChainCameraSectionViewModel SectionOf(object sender)
        {
            var fe = sender as FrameworkElement;
            return fe == null ? null : fe.DataContext as ChainCameraSectionViewModel;
        }

        //---------------------------------------------------------------------
        // 步骤清单 ↔ 操作区联动（2026-09-28）：点某一步 → 右侧切到该步区域
        //---------------------------------------------------------------------

        private void StepRow_Click(object sender, MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var row = fe == null ? null : fe.DataContext as ChainStepRow;
            if (row != null) _vm.SelectStep(row);
        }

        private void LoadImage_Click(object sender, RoutedEventArgs e)
        {
            var sec = SectionOf(sender);
            if (sec == null) return;
            string path = PickImageFile();
            if (path != null) sec.Image = LoadBitmap(path);
        }

        /// <summary>连接设备池中的当前相机（T10）</summary>
        private void ConnectCamera_Click(object sender, RoutedEventArgs e)
        {
            _vm.ConnectCamera();
        }

        /// <summary>单帧取图（软触发）：把相机当前帧送入该相机 Section 的图像区（T10）</summary>
        private void SnapFromCamera_Click(object sender, RoutedEventArgs e)
        {
            var sec = SectionOf(sender);
            FrameEventArgs frame = _vm.CaptureOnce();
            if (frame == null) return;
            var bmp = ChainWizardViewModel.FrameToBitmap(frame);
            if (bmp == null)
            {
                MessageBox.Show(this,
                    "帧格式暂不支持显示（PixelFormat=" + (frame.PixelFormat ?? "?") + "）。\n" +
                    "当前仅支持 Mono8 / RGB8；请改用『载入图像…』离线选图。",
                    "链向导", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (sec != null) sec.Image = bmp;
        }

        private void RefreshCameras_Click(object sender, RoutedEventArgs e)
        {
            _vm.LoadCameraDevices();
        }

        private void CameraImage_Click(object sender, MouseButtonEventArgs e)
        {
            var img = sender as Image;
            var sec = SectionOf(sender);
            if (img == null || sec == null) return;
            double col, row;
            ToPixel(img, e.GetPosition(img), out col, out row);
            sec.SetPixelFromClick(col, row);
        }

        private void CameraImage_RightClick(object sender, MouseButtonEventArgs e)
        {
            var img = sender as Image;
            var sec = SectionOf(sender);
            if (img == null || sec == null) return;
            if (!sec.IsDownCorrect)
            {
                MessageBox.Show(this, "该相机无下相机纠偏边（DownCameraCorrect），不需要 DeltaRefPixel。",
                    "链向导", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            double col, row;
            ToPixel(img, e.GetPosition(img), out col, out row);
            sec.SetDeltaRefFromClick(col, row);
            _vm.RefreshSteps();
        }

        //---------------------------------------------------------------------
        // 点对编辑 / 拟合 / 主工具裁决 / 校验 / 保存（全部转发 VM）
        //---------------------------------------------------------------------

        private void AddPoint_Click(object sender, RoutedEventArgs e)
        {
            var sec = SectionOf(sender);
            if (sec != null) sec.AddPoint();
        }

        private void RemovePoint_Click(object sender, RoutedEventArgs e)
        {
            var sec = SectionOf(sender);
            if (sec != null) sec.RemovePoint(sec.SelectedRow);
        }

        private void FitSection_Click(object sender, RoutedEventArgs e)
        {
            var sec = SectionOf(sender);
            if (sec == null) return;
            sec.Fit();
            _vm.RefreshSteps();
            // 完成即推进：拟合过门 ⇒ 自动带到下一个待办步（行业向导惯例）
            if (sec.FitOk && !sec.ShapeGateFailed) _vm.AdvanceToNextPendingStep();
        }

        private void SwitchMaster_Click(object sender, RoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var row = fe == null ? null : fe.DataContext as ChainToolRowViewModel;
            if (row == null) return;
            if (row.IsMaster)
            {
                MessageBox.Show(this, row.ToolId + " 已是主工具。", "链向导",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this,
                    "把主工具切换为 " + row.ToolId + "？\n链骨架将按新裁决重新推导（已填采集数据按 Id 迁移，形状变化时未匹配项丢弃）。",
                    "链向导·人工裁决", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _vm.SwitchMaster(row.ToolId);
            }
        }

        private void Validate_Click(object sender, RoutedEventArgs e) { _vm.Validate(); }
        private void Save_Click(object sender, RoutedEventArgs e) { _vm.Save(); }
        private void RefreshSteps_Click(object sender, RoutedEventArgs e) { _vm.RefreshSteps(); }

        //---------------------------------------------------------------------
        // 轴操控（T18）：连接 / 点动 / 回读 / 回填
        //   布局：右区独立一栏，先"走到位"再"取图点选"，最后把位姿回填进点对表。
        //---------------------------------------------------------------------

        private void RefreshMotion_Click(object sender, RoutedEventArgs e) { _vm.LoadMotionDevices(); }

        private void ConnectMotion_Click(object sender, RoutedEventArgs e) { _vm.ConnectMotion(); }

        private void JogXp_Click(object sender, RoutedEventArgs e) { _vm.Jog(_vm.AxisX, +1); }
        private void JogXm_Click(object sender, RoutedEventArgs e) { _vm.Jog(_vm.AxisX, -1); }
        private void JogYp_Click(object sender, RoutedEventArgs e) { _vm.Jog(_vm.AxisY, +1); }
        private void JogYm_Click(object sender, RoutedEventArgs e) { _vm.Jog(_vm.AxisY, -1); }

        private void ReadPose_Click(object sender, RoutedEventArgs e) { _vm.ReadPose(); }

        //---------------------------------------------------------------------
        // 九点网格采集（2026-09-30 补）：设基准 → 生成目标 → 逐点走位取图
        //   走位只做逐点触发，不做全自动循环（真机上须全程可见、可急停）。
        //---------------------------------------------------------------------

        private void SetGridBase_Click(object sender, RoutedEventArgs e) { _vm.SetGridBaseFromPose(); }

        private void BuildGrid_Click(object sender, RoutedEventArgs e) { _vm.BuildGridTargets(); }

        private void MoveNextPoint_Click(object sender, RoutedEventArgs e)
        {
            _vm.MoveToNextPoint();
            _vm.RefreshSteps();
        }

        /// <summary>把刚回读的位姿写入当前相机【选中点行】的 WorldX/WorldY</summary>
        private void FillPose_Click(object sender, RoutedEventArgs e)
        {
            string msg = _vm.FillPoseIntoSelectedRow(_vm.SelectedSection);
            _vm.RefreshSteps();
            MessageBox.Show(this, msg, "链向导·回填位姿", MessageBoxButton.OK,
                msg.StartsWith("⚠") ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        //---------------------------------------------------------------------
        // 工具 TCP 采集（T14 pivoting）：中区面板——扎点表/求解/写入
        //---------------------------------------------------------------------

        private void AddPivotPoint_Click(object sender, RoutedEventArgs e) { _vm.AddPivotPoint(); }

        private void RemovePivotPoint_Click(object sender, RoutedEventArgs e)
        {
            _vm.RemovePivotPoint(_vm.SelectedPivotRow);
        }

        private void FillPivotPose_Click(object sender, RoutedEventArgs e)
        {
            _vm.FillPivotIntoSelectedRow();
        }

        private void SolvePivoting_Click(object sender, RoutedEventArgs e)
        {
            _vm.SolvePivoting();
            _vm.RefreshSteps();
        }

        private void ApplyPivot_Click(object sender, RoutedEventArgs e)
        {
            string msg = _vm.ApplyPivotToTool();
            _vm.RefreshSteps();
            MessageBox.Show(this, msg, "链向导·写入工具偏移", MessageBoxButton.OK,
                msg.StartsWith("⚠") ? MessageBoxImage.Warning : MessageBoxImage.Information);
            // 写入成功即完成本步 → 推进
            if (!msg.StartsWith("⚠")) _vm.AdvanceToNextPendingStep();
        }
    }
}
