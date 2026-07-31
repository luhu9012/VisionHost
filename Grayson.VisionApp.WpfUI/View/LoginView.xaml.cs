//===================================================================================
// Copyright (c) 2026 Grayson.VisionApp. All rights reserved.
// 文件名: LoginView.xaml.cs
// 创 建: 2026-07-18
// 说 明: 登录界面代码后置
//===================================================================================

using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.VisionApp.WpfUI.View
{
    /// <summary>
    /// LoginView.xaml 的交互逻辑
    /// </summary>
    public partial class LoginView : UserControl
    {
        public LoginView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// PasswordBox 的密码变更事件 (因为 Password 属性不是依赖属性,无法绑定)
        /// </summary>
        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is LoginViewModel viewModel)
            {
                viewModel.Password = ((PasswordBox)sender).Password;
            }
        }
    }
}
