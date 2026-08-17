using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.CalibrationApply
{
    public partial class CalibrationApplyTemplateView : UserControl
    {
        public CalibrationApplyTemplateView()
        {
            InitializeComponent();
        }

        private void BrowseHomMat_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is CalibrationApplyParam param)
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Halcon Tuple 文件 (*.tup)|*.tup|所有文件 (*.*)|*.*",
                    Title = "选择标定矩阵文件"
                };

                if (dialog.ShowDialog() == true)
                {
                    param.HomMatFilePath = dialog.FileName;
                }
            }
        }
    }
}