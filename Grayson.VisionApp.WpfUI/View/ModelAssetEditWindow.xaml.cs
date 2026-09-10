//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: ModelAssetEditWindow.xaml.cs
// 说 明: 模型资产注册/编辑窗口
//===================================================================================
using Grayson.Vision.WpfUI.ViewModel;
using System.Windows;

namespace Grayson.Vision.WpfUI.View
{
    public partial class ModelAssetEditWindow : Window
    {
        public ModelAssetEditWindow()
        {
            InitializeComponent();
        }

        private void OnPickFile(object sender, RoutedEventArgs e)
        {
            if (DataContext is ModelAssetEditViewModel vm) vm.PickModelFile();
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (DataContext is ModelAssetEditViewModel vm && vm.Save())
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
