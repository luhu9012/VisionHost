using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.ViewModel.HardwareConsole;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class HardwareConsoleViewModel : ViewModelBase
    {
        public CameraDebugViewModel CameraDebugVM { get; }
        public AxisControlViewModel AxisControlVM { get; }
        public IoMonitorViewModel IoMonitorVM { get; }
        public CommDebugViewModel CommDebugVM { get; }

        public HardwareConsoleViewModel()
        {
            // 组合子ViewModel，便于统一解耦与维护
            CameraDebugVM = new CameraDebugViewModel();
            AxisControlVM = new AxisControlViewModel();
            IoMonitorVM = new IoMonitorViewModel();
            CommDebugVM = new CommDebugViewModel();
        }

        /// <summary>
        /// 页面隐藏或导航离开时调用，停止所有调试面板的轮询/采集并释放事件。
        /// </summary>
        public void Cleanup()
        {
            CameraDebugVM?.Cleanup();
            AxisControlVM?.Cleanup();
            IoMonitorVM?.Cleanup();
        }
    }
}