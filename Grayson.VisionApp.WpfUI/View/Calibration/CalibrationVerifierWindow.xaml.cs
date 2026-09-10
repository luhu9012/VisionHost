//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationVerifierWindow.xaml.cs
// 说 明: P3 标定校验台窗口 code-behind。
//        B 块：点选事件(覆盖层)→ 宿主 TryGetImagePointAt 视口坐标→图像坐标(row/col) → VM.ApplyPick；
//        A 块：残差体检按钮 → VM.RunResidualAudit 计算 → 按 ResidualRows 在左图叠加
//        标记（黄十字=记录像素点 P{n}；机械真值反投影与记录像素差>2px 加青十字=残差向量）。
//        覆盖层点选仅在"在线打点"Tab 激活时收（体检 Tab 下忽略，防误点）。
//===================================================================================
using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    public partial class CalibrationVerifierWindow : Window
    {
        public CalibrationVerifierViewModel VerifierVm { get; }

        public CalibrationVerifierWindow(CalibrationProfile profile)
        {
            InitializeComponent();
            VerifierVm = new CalibrationVerifierViewModel(profile);
            DataContext = VerifierVm;
            Title = $"标定校验台 — {profile?.Name}";
            // 关闭兜底：先收尾相机取流/触发模式/事件订阅；再处理记录
            // （存在已判定验证点但未点保存 → 自动生成记录，中心 Closed 事件统一落库，
            //   2026-09-06 非模态化后由标定中心 Closed 提交链 CommitVerifierOnClosed 执行）
            // 分步几何校验区标记：VM 产出 GeometryMarkers → 宿主绘制（黄=采样像素 青=真值反投影 绿=预测位 洋红=实际点选）
            VerifierVm.MarkersInvalidated += (s, e) => RedrawGeometryMarkers();
            Closing += (s, e) =>
            {
                VerifierVm.Cleanup();
                if (VerifierVm.VerificationPoints.Any(p => p.IsVerdicted) && !VerifierVm.HasPendingRecord)
                {
                    VerifierVm.SaveRecordCommand.Execute(null);
                }
            };
        }

        /// <summary>覆盖层点选：视口坐标 → 图像坐标（TryGetImagePointAt 由宿主换算）。
        /// 仅在线打点 Tab(0) 激活时收点；残差体检 Tab 下忽略。</summary>
        private void Overlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ImageHost == null || VerifierVm == null || !VerifierVm.PickEnabled) return;
            var viewPoint = e.GetPosition(ImageHost);
            double row, col;
            if (ImageHost.TryGetImagePointAt(viewPoint, out row, out col))
            {
                VerifierVm.ApplyPick(row, col);
                e.Handled = true;
            }
        }

        /// <summary>切换右栏页签：按页签重画对应的图上标记（残差体检 / 分步几何校验），
        /// 并在切到「H+e+t 全量」时刷新全链路中间量显示。</summary>
        private void RightTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (VerifierVm == null || ImageHost == null) return;
            if (VerifierVm.ActiveTabIndex == 1)
            {
                if (VerifierVm.ResidualRows.Count > 0) DrawResidualMarkers();
            }
            else if (VerifierVm.ActiveTabIndex == 2 || VerifierVm.ActiveTabIndex == 3)
            {
                RedrawGeometryMarkers();
                if (VerifierVm.ActiveTabIndex == 3) VerifierVm.RefreshFullChain();
            }
        }

        /// <summary>按 VM 的 GeometryMarkers 集合重画分步校验标记</summary>
        private void RedrawGeometryMarkers()
        {
            if (ImageHost == null || VerifierVm == null) return;
            ImageHost.ClearMarkers();
            foreach (var m in VerifierVm.GeometryMarkers)
            {
                ImageHost.AddMarkerCross(m.Row, m.Col, m.Size, m.Color, m.Label);
            }
        }

        /// <summary>A 块：计算残差并叠加图上标记（黄十字=记录像素点；反投影差>2px 加青十字）</summary>
        private void BtnRunResidual_Click(object sender, RoutedEventArgs e)
        {
            int n = VerifierVm.RunResidualAudit();
            if (n <= 0 || VerifierVm.ResidualRows.Count == 0) return;
            DrawResidualMarkers();
        }

        /// <summary>按 ResidualRows 画残差标记（黄十字=记录像素点；反投影差>2px 加青十字）</summary>
        private void DrawResidualMarkers()
        {
            ImageHost.ClearMarkers();
            int drawn = 0;
            foreach (var row in VerifierVm.ResidualRows)
            {
                // 记录像素点（col=PixelX, row=PixelY）：黄十字 + 序号
                ImageHost.AddMarkerCross(row.PixelY, row.PixelX, 15, "yellow", $"P{row.Order}");
                drawn++;
                // 机械真值反投影点：与记录像素差>2px 才画（≈0 说明贴合，避免同屏叠十字噪声）
                if (!double.IsNaN(row.BackX) && !double.IsNaN(row.BackY))
                {
                    double dRow = row.BackY - row.PixelY;
                    double dCol = row.BackX - row.PixelX;
                    if (Math.Sqrt(dRow * dRow + dCol * dCol) > 2.0)
                    {
                        ImageHost.AddMarkerCross(row.BackY, row.BackX, 11, "cyan", null);
                    }
                }
            }
            VerifierVm.AppendLog($"残差体检图上标记完成: 黄十字 {drawn} 点(记录位) + 青色反投影(偏差>2px 点)。" +
                                 "十字与标定同取景时叠在实物特征上；当前无定格图时画在空白视口仅作示意。");
        }

        /// <summary>完成并保存：生成记录后关闭（落库由标定中心在 Closed 事件提交链统一执行；
        /// 非模态窗口不可设置 DialogResult——直接 Close，语义等同原"确定关闭"）</summary>
        private void SaveAndClose_Click(object sender, RoutedEventArgs e)
        {
            if (VerifierVm.HasPendingRecord || VerifierVm.VerificationPoints.Any(p => p.IsVerdicted))
            {
                VerifierVm.SaveRecordCommand.Execute(null);
            }
            Close();
        }
    }
}
