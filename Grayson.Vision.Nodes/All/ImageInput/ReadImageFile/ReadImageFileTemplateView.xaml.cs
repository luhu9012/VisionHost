using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Grayson.Vision.Nodes.All.ImageInput.ReadImageFile
{
    public partial class ReadImageFileTemplateView : UserControl
    {
        public ReadImageFileTemplateView()
        {
            InitializeComponent();
        }

        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ReadImageFileParam param)
            {
                var dialog = new OpenFileDialog
                {
                    Title = "请选择视觉检测图片",
                    Filter = "图像文件 (*.png;*.jpg;*.bmp;*.tif)|*.png;*.jpg;*.bmp;*.tif|所有文件 (*.*)|*.*",
                    Multiselect = false
                };

                if (dialog.ShowDialog() == true)
                {
                    param.FilePath = dialog.FileName;
                }
            }
        }

        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ReadImageFileParam param)
            {
                var dialog = new OpenFileDialog
                {
                    Title = "请选择批处理文件夹中的任意一张图片",
                    Filter = "图像文件 (*.png;*.jpg;*.bmp;*.tif)|*.png;*.jpg;*.bmp;*.tif|所有文件 (*.*)|*.*",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() == true)
                {
                    // 获取该文件所在的目录路径
                    param.FolderPath = Path.GetDirectoryName(dialog.FileName);
                }
            }
        }
    }
}