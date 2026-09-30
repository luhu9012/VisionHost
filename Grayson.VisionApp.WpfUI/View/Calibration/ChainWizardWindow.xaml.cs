using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.HalconWrapper.Wpf.Controls;
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

        public ChainWizardWindow(string stationCode = null, string focusCard = null)
        {
            InitializeComponent();
            _vm = new ChainWizardViewModel(string.IsNullOrWhiteSpace(stationCode) ? "ST_002" : stationCode, focusCard);
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

        //---------------------------------------------------------------------
        // Halcon 视窗定位（2026-09-30）
        //   ★ 同一时刻只有一个 Tab 内容被实例化（TabControl 只有一个 ContentPresenter），
        //     所以"从窗口向下找第一个 HalconImageDisplayHost"就是当前可见的那个视窗。
        //     点选换算与识别标记都打在它上面。
        //---------------------------------------------------------------------

        private static T FindDescendant<T>(DependencyObject d) where T : DependencyObject
        {
            if (d == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                var t = c as T;
                if (t != null) return t;
                var r = FindDescendant<T>(c);
                if (r != null) return r;
            }
            return null;
        }

        private HalconImageDisplayHost FindRealizedHost()
        {
            return FindDescendant<HalconImageDisplayHost>(this);
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

        /// <summary>
        /// ★2026-09-30 修复（Bug「载入图像…」点击无反应）：
        /// 采集工具栏在 TabControl 之外，其 DataContext 是窗口级 ChainWizardViewModel，
        /// 不是 ChainCameraSectionViewModel ⇒ SectionOf(sender) 恒为 null ⇒ 老代码直接 return，
        /// 按钮看起来"点了没反应"。此处统一回退到当前选中的相机 Tab。
        /// </summary>
        private ChainCameraSectionViewModel ResolveSection(object sender)
        {
            return SectionOf(sender) ?? _vm.SelectedSection;
        }

        private void LoadImage_Click(object sender, RoutedEventArgs e)
        {
            var sec = ResolveSection(sender);
            if (sec == null)
            {
                MessageBox.Show(this, "当前没有可用的相机 Tab（本工位档案未派生相机段）。",
                    "链向导", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string path = PickImageFile();
            if (path == null) return;
            _vm.LoadImageIntoSection(sec, path);
        }

        /// <summary>连接设备池中的当前相机（T10）</summary>
        private void ConnectCamera_Click(object sender, RoutedEventArgs e)
        {
            _vm.ConnectCamera();
        }

        /// <summary>单帧取图（软触发）：把相机当前帧送入该相机 Section 的图像区（T10）</summary>
        private void SnapFromCamera_Click(object sender, RoutedEventArgs e)
        {
            var sec = ResolveSection(sender);
            if (sec == null)
            {
                MessageBox.Show(this, "当前没有可用的相机 Tab（本工位档案未派生相机段）。",
                    "链向导", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            FrameEventArgs frame = _vm.CaptureOnce();
            if (frame == null)
            {
                MessageBox.Show(this, "取图失败：" + _vm.CameraLog, "链向导",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _vm.ShowFrameInSection(sec, frame);
        }

        private void RefreshCameras_Click(object sender, RoutedEventArgs e)
        {
            _vm.LoadCameraDevices();
        }

        //---------------------------------------------------------------------
        // 图像点选（覆盖层）：宿主坐标 → 图像坐标
        //   TryGetImagePointAtHost 内部扣掉工具栏高度（ShowToolbar=True 时），
        //   避免"点十字落在点击点下方"。点在工具栏/留白区返回 false，忽略。
        //---------------------------------------------------------------------

        private void CameraOverlay_Pick(object sender, MouseButtonEventArgs e)
        {
            var host = FindRealizedHost();
            var sec = ResolveSection(sender);
            if (host == null || sec == null) return;
            double row, col;
            if (!host.TryGetImagePointAtHost(e.GetPosition(host), out row, out col)) return;
            // ★旋转中心步：同一次点选语义不同——要落到【旋转采样行】，不是相机点对表
            if (_vm.IsRotationStepSelected)
                _vm.SetRotationPixel(_vm.SelectedRotationRow, col, row);
            else
                sec.SetPixelFromClick(col, row);
            _vm.RefreshSteps();
        }

        private void CameraOverlay_RightPick(object sender, MouseButtonEventArgs e)
        {
            var sec = ResolveSection(sender);
            if (sec == null) return;
            if (!sec.IsDownCorrect)
            {
                MessageBox.Show(this, "该相机无下相机纠偏边（DownCameraCorrect），不需要 DeltaRefPixel。",
                    "链向导", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var host = FindRealizedHost();
            if (host == null) return;
            double row, col;
            if (!host.TryGetImagePointAtHost(e.GetPosition(host), out row, out col)) return;
            sec.SetDeltaRefFromClick(col, row);
            _vm.RefreshSteps();
        }

        //---------------------------------------------------------------------
        // 特征 / 模板识别（2026-09-30 补）：识别 → 视窗叠加标记 → 回填点对表
        //   算法在 HalconWrapper 的 CalibrationService（圆/十字/模板三路，与生产同源）；
        //   本层只负责把结果画到视窗 + 提示。
        //---------------------------------------------------------------------

        /// <summary>识别当前段图像上的特征点，并在视窗上打十字 + 分数标签</summary>
        private void Recognize_Click(object sender, RoutedEventArgs e)
        {
            var sec = ResolveSection(sender);
            var host = FindRealizedHost();
            if (host != null) host.ClearMarkers();

            string msg = _vm.RecognizeFeature(sec);
            bool ok = sec != null && sec.HasRecognized;
            if (ok && host != null)
            {
                string label = string.Format(CultureInfo.InvariantCulture,
                    "u={0:F2} v={1:F2} · {2:F0}/100", sec.RecognizedCol, sec.RecognizedRow, sec.MatchScore);
                // AddMarkerCross(row=像素Y, col=像素X, 半尺寸, 颜色, 标签)；标记层在底图之上、随场景重放
                host.AddMarkerCross(sec.RecognizedRow, sec.RecognizedCol, 60,
                    sec.MatchScore >= 70 ? "green" : "yellow", label);
            }
            MessageBox.Show(this, msg, "链向导·特征识别", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        /// <summary>把识别到的像素回填点对表当前选中行（替代肉眼点选）</summary>
        private void ApplyRecognized_Click(object sender, RoutedEventArgs e)
        {
            var sec = ResolveSection(sender);
            string msg = _vm.ApplyRecognizedToSelectedRow(sec);
            _vm.RefreshSteps();
            MessageBox.Show(this, msg, "链向导·回填识别像素", MessageBoxButton.OK,
                msg.StartsWith("✓") ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void ReloadTemplates_Click(object sender, RoutedEventArgs e)
        {
            var sec = ResolveSection(sender);
            if (sec == null) return;
            sec.ReloadTemplates();
            MessageBox.Show(this, "模板库已刷新：" + sec.AvailableTemplates.Count + " 个模板。",
                "链向导", MessageBoxButton.OK, MessageBoxImage.Information);
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
        // ★2026-09-30 补：Z / U 点动（此前只有 X/Y）
        private void JogZp_Click(object sender, RoutedEventArgs e) { _vm.Jog(_vm.AxisZ, +1); }
        private void JogZm_Click(object sender, RoutedEventArgs e) { _vm.Jog(_vm.AxisZ, -1); }
        private void JogUp_Click(object sender, RoutedEventArgs e) { _vm.Jog(_vm.AxisU, +1); }
        private void JogUm_Click(object sender, RoutedEventArgs e) { _vm.Jog(_vm.AxisU, -1); }

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

        // ★2026-09-30 补（#4）：一键把法兰【相对】转到下一个采样角（0→90→180→270）
        private void PivotNextAngle_Click(object sender, RoutedEventArgs e)
        {
            _vm.PivotStepNextAngle();
        }

        //---------------------------------------------------------------------
        // 旋转中心 e 采集（#1 / #6, 2026-09-30）：EIH 绕 U 转、相机看同一固定特征
        //   识别与图上点选两条入口都汇到 VM.SetRotationPixel —— 单一落点，避免两套口径打架。
        //---------------------------------------------------------------------

        private void AddRotationPoint_Click(object sender, RoutedEventArgs e) { _vm.AddRotationPoint(); }

        private void RemoveRotationPoint_Click(object sender, RoutedEventArgs e)
        {
            _vm.RemoveRotationPoint(_vm.SelectedRotationRow);
        }

        /// <summary>识别当前相机图像特征 → 回填选中采样行的像素（EIH 间接对针免人工点选）</summary>
        private void RecognizeForRotation_Click(object sender, RoutedEventArgs e)
        {
            var host = FindRealizedHost();
            if (host != null) host.ClearMarkers();
            _vm.RecognizeForRotation();
            var sec = _vm.SelectedSection;
            if (sec != null && sec.HasRecognized && host != null)
            {
                string label = string.Format(CultureInfo.InvariantCulture,
                    "u={0:F2} v={1:F2} · {2:F0}/100", sec.RecognizedCol, sec.RecognizedRow, sec.MatchScore);
                host.AddMarkerCross(sec.RecognizedRow, sec.RecognizedCol, 60,
                    sec.MatchScore >= 70 ? "green" : "yellow", label);
            }
        }

        private void SolveRotationCircle_Click(object sender, RoutedEventArgs e) { _vm.SolveRotationCircle(); }

        private void ApplyRotation_Click(object sender, RoutedEventArgs e)
        {
            string msg = _vm.ApplyRotationToTool();
            _vm.RefreshSteps();
            MessageBox.Show(this, msg, "链向导·写入旋转中心", MessageBoxButton.OK,
                msg.StartsWith("⚠") ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
    }
}
