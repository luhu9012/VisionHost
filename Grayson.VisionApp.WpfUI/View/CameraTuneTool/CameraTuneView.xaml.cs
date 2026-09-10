using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    /// <summary>
    /// CameraTuneView.xaml 的交互逻辑
    /// 相机装调助手(工程工具):垂直度快检 + 三点对焦快调闭环。
    /// 每次导航全新实例;离开页面时 Cleanup(停流/退订/清显示)。
    /// </summary>
    public partial class CameraTuneView : UserControl
    {
        public CameraTuneView()
        {
            InitializeComponent();
            DataContext = new CameraTuneViewModel();
        }

        private CameraTuneViewModel ViewModel => DataContext as CameraTuneViewModel;

        /// <summary>页面呈现后补拉设备(设备池启动期可能尚未就绪,VM 会短时自动重试数次)</summary>
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel?.ReloadDevicesWhenReady();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel?.Cleanup();
            DataContext = null;
        }
    }
}
