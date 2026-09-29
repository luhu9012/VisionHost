using System;
using System.Windows;
using System.Windows.Input;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    /// <summary>
    /// L3【真机指哪打哪】验证台（范式2）。
    ///
    /// 与链校验台（L1 门禁可视 + L2 数学校验）并列：那两个只看"落盘数字自洽"，
    /// 本窗用真机走一遍完整闭环（相机看到点 → 链求值 → 逆解 → 到位 → 复测），
    /// 在世界域量偏差，给出"能不能投产"的放行/拦截结论。
    ///
    /// 本文件只负责一件事：把用户在图像上的点击换算成【标定坐标系的像素 (u,v)】。
    /// 换算必须考虑 Image 的 Stretch=Uniform 实际缩放与留白，否则点选像素会系统性偏移，
    /// 而点选偏差会 1:1 进入世界偏差（像素当量），把"标定问题"和"点不准"混为一谈。
    /// </summary>
    public partial class ChainLiveVerifyWindow : Window
    {
        private readonly ChainLiveVerifyViewModel _vm;

        public ChainLiveVerifyWindow(string stationCode)
        {
            InitializeComponent();
            _vm = new ChainLiveVerifyViewModel(stationCode);
            DataContext = _vm;

            // ★2026-09-29 P0-3 兜底：正常跑完 ExecuteAll 时 VM 已落盘留痕（防重跳过）；
            //   但操作员可能跑到一半直接关窗——那也是一次真实验证，必须留痕，不能凭空消失。
            Closed += (s, e) =>
            {
                try
                {
                    if (!_vm.HistorySaved && _vm.Report != null && _vm.Report.Points.Count > 0)
                        _vm.TrySaveHistory();
                }
                catch { /* 关窗兜底绝不反向阻塞关闭流程 */ }
            };
        }

        private void PickImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var img = PickImage;
            var src = img.Source;
            if (src == null || img.ActualWidth <= 0 || img.ActualHeight <= 0)
            {
                return;
            }

            // Stretch=Uniform：先算实际显示区域（等比缩放 + 居中留白），再反算源像素。
            double srcW = src.Width, srcH = src.Height;
            double boxW = img.ActualWidth, boxH = img.ActualHeight;
            double scale = Math.Min(boxW / srcW, boxH / srcH);
            double dispW = srcW * scale, dispH = srcH * scale;
            double offX = (boxW - dispW) / 2.0, offY = (boxH - dispH) / 2.0;

            var pt = e.GetPosition(img);
            double px = (pt.X - offX) / scale;
            double py = (pt.Y - offY) / scale;

            // 点击落在留白区（图像之外）则忽略，避免记入离谱像素。
            if (px < 0 || py < 0 || px > srcW || py > srcH) return;

            // 标定坐标系约定：u = 列(col) = 图像 x；v = 行(row) = 图像 y。
            _vm.SetPickedPixel(px, py);
        }
    }
}
