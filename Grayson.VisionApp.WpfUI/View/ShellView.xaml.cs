//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: ShellView.xaml.cs
// 创 建: 2026-07-18
// 说 明: 主框架窗口代码后置
//===================================================================================

using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.View
{
    /// <summary>
    /// ShellView.xaml 的交互逻辑
    /// </summary>
    public partial class ShellView : Window
    {
        public ShellView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 标题栏拖动
        /// </summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 双击标题栏最大化/还原
                WindowState = WindowState == WindowState.Maximized 
                    ? WindowState.Normal 
                    : WindowState.Maximized;
            }
            else
            {
                // 单击拖动
                DragMove();
            }
        }
    }
}
