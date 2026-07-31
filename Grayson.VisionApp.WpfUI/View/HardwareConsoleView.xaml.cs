using System.Windows.Controls;
using Grayson.Vision.WpfUI.ViewModel;

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
    }
}