using Grayson.Vision.WpfUI.ViewModel.HardwareConsole;
using System;
using System.Windows;

namespace Grayson.Vision.WpfUI.View.HardwareConsole
{
    /// <summary>
    /// CameraLiveWindow.xaml 的交互逻辑。
    /// 相机实时画面弹窗：供轴控等调试页面在运动过程中动态观察相机视角。
    /// 非模态显示（Show 而非 ShowDialog），打开后仍可回到主界面操作轴运动。
    /// </summary>
    public partial class CameraLiveWindow : Window
    {
        private readonly CameraLiveWindowViewModel _viewModel;

        public CameraLiveWindow()
        {
            InitializeComponent();

            _viewModel = new CameraLiveWindowViewModel();
            DataContext = _viewModel;

            Loaded += CameraLiveWindow_Loaded;
            Closing += CameraLiveWindow_Closing;
        }

        private void CameraLiveWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 窗口就绪后再启动取流：确保 HalconImageDisplayHost 的 HWindow 已初始化
            _viewModel.StartLive();
        }

        private void CameraLiveWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _viewModel.Cleanup();
        }
    }
}
