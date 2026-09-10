//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TaskTemplateCenterView.xaml.cs
// 说 明: 任务模板中心列表页（数据上下文由页面工厂在 App.xaml.cs 中装配）
//===================================================================================
using System.Windows.Controls;

namespace Grayson.Vision.WpfUI.View
{
    public partial class TaskTemplateCenterView : UserControl
    {
        public TaskTemplateCenterView()
        {
            InitializeComponent();
            DataContext = new ViewModel.TaskTemplateCenterViewModel();
            ((ViewModel.TaskTemplateCenterViewModel)DataContext).Refresh();
        }
    }
}
