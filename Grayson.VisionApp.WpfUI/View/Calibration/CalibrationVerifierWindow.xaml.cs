//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationVerifierWindow.xaml.cs
// 说 明: P3 标定校验台窗口 code-behind。
//        ★2026-09-12 精简：校验台唯一功能 = 点选像素特征 → 低速移动 → 吸嘴贴合到物理工件
//        （测试标定结果的消费与应用）。移除「残差体检」「分步几何校验」诊断 Tab 及其图上标记逻辑，
//        只保留点选事件 → 宿主 TryGetImagePointAt 视口坐标→图像坐标(row/col) → VM.ApplyPick。
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
            Closing += (s, e) =>
            {
                VerifierVm.Cleanup();
                if (VerifierVm.VerificationPoints.Any(p => p.IsVerdicted) && !VerifierVm.HasPendingRecord)
                {
                    VerifierVm.SaveRecordCommand.Execute(null);
                }
            };
        }

        /// <summary>覆盖层点选：视口坐标 → 图像坐标（TryGetImagePointAt 由宿主换算）。</summary>
        private void Overlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ImageHost == null || VerifierVm == null || !VerifierVm.PickEnabled) return;
            // 宿主坐标（含工具条）→ 图像坐标：TryGetImagePointAtHost 内部扣掉工具条高度
            var viewPoint = e.GetPosition(ImageHost);
            double row, col;
            if (ImageHost.TryGetImagePointAtHost(viewPoint, out row, out col))
            {
                VerifierVm.ApplyPick(row, col);
                e.Handled = true;
            }
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
