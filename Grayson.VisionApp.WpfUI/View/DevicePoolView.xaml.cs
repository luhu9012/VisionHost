using Grayson.Vision.WpfUI.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Grayson.Vision.WpfUI.View
{
    /// <summary>
    /// DevicePoolView.xaml 的交互逻辑
    /// </summary>
    public partial class DevicePoolView : UserControl
    {
        public DevicePoolView()
        {
            InitializeComponent();
            this.DataContext = new DevicePoolViewModel();
            // 打开界面时，自动扫描物理硬件
            ((DevicePoolViewModel)this.DataContext).OnScanPhysicalHardware(true);
        }
    }
}
