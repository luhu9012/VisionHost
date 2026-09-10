//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TaskTemplateEditWindow.xaml.cs
// 说 明: 任务模板编辑窗口（保存/取消由按钮 Click 驱动，避免 VM 依赖 WPF Window）
//===================================================================================
using Grayson.Vision.WpfUI.ViewModel;
using System.Windows;

namespace Grayson.Vision.WpfUI.View
{
    public partial class TaskTemplateEditWindow : Window
    {
        public TaskTemplateEditWindow()
        {
            InitializeComponent();
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (DataContext is TaskTemplateEditViewModel vm && vm.Save())
            {
                DialogResult = true;
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
