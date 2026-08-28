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
            // ⚠ 重要约定：本视图的 DataTemplate 会被 FlowEdit 的 NodePluginLoader 提取注册到
            // 应用全局资源，代码后台 this 实例与实际渲染的模板实例不是同一个（this.DataContext
            // 恒为 null，this 也不在视觉树中）。因此所有事件处理一律通过 sender 取实时
            // DataContext，切勿使用 this.DataContext（会静默失效，与 SwitchCaseTemplateView 同款坑）。
        }

        /// <summary>
        /// 模板根元素 DataContext 变化（面板打开 / 切换节点 / 参数实例更换）时自动扫描一次
        /// 标定文件存储目录，填充下拉候选。
        /// </summary>
        private void TemplateRoot_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            RefreshMatrixFiles(sender);
        }

        private void RefreshMatrixFiles_Click(object sender, RoutedEventArgs e)
        {
            RefreshMatrixFiles(sender);
        }

        private static void RefreshMatrixFiles(object sender)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CalibrationApplyParam param)
            {
                param.RefreshMatrixFiles();
            }
        }

        private void BrowseHomMat_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CalibrationApplyParam param)
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Halcon Tuple 文件 (*.tup)|*.tup|所有文件 (*.*)|*.*",
                    Title = "选择标定矩阵文件"
                };

                if (dialog.ShowDialog() == true)
                {
                    // setter 会把它补进候选列表，下拉框随即显示选中项
                    param.HomMatFilePath = dialog.FileName;
                }
            }
        }
    }
}
