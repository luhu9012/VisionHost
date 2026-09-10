//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: ModelRegistryView.xaml.cs
// 说 明: 模型仓库列表页（数据上下文由页面工厂装配）
//===================================================================================
using System.Windows.Controls;

namespace Grayson.Vision.WpfUI.View
{
    public partial class ModelRegistryView : UserControl
    {
        public ModelRegistryView()
        {
            InitializeComponent();
            DataContext = new ViewModel.ModelRegistryViewModel();
            ((ViewModel.ModelRegistryViewModel)DataContext).Refresh();
        }
    }
}
