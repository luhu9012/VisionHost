using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.WpfUI.ViewModel;
using Grayson.Vision.WpfUI.ViewModel.HardwareConsole;

namespace Grayson.Vision.WpfUI.View
{
    /// <summary>
    /// HardwareConsoleView.xaml 的交互逻辑
    /// </summary>
    public partial class HardwareConsoleView : UserControl
    {
        public HardwareConsoleView()
        {
            InitializeComponent();

            // 演示用数据上下文
            DataContext = new HardwareConsoleViewModel();
        }

        private HardwareConsoleViewModel ViewModel => DataContext as HardwareConsoleViewModel;

        /// <summary>
        /// Tab 切换时：仅当前激活的子面板保持轮询/采集，非激活面板停止后台活动。
        /// </summary>
        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel == null) return;

            var tabControl = sender as TabControl;
            var selectedHeader = (tabControl?.SelectedItem as TabItem)?.Header?.ToString();

            ViewModel.CameraDebugVM.IsActive = selectedHeader?.Contains("相机") == true;
            ViewModel.AxisControlVM.IsActive = selectedHeader?.Contains("轴") == true;
            ViewModel.IoMonitorVM.IsActive = selectedHeader?.Contains("IO") == true;
        }

        /// <summary>
        /// 页面卸载时停止所有后台轮询/采集，避免内存泄漏与设备占用冲突。
        /// </summary>
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel?.Cleanup();
        }
    }
}