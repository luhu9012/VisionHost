using Grayson.Vision.WpfUI.ViewModel.HardwareConsole;
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

namespace Grayson.Vision.WpfUI.View.HardwareConsole
{
    /// <summary>
    /// IoMonitorView.xaml 的交互逻辑
    /// </summary>
    public partial class IoMonitorView : UserControl
    {
        public IoMonitorView()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                if (DataContext is IoMonitorViewModel vm)
                {
                    vm.IsActive = true; // 切入该 Tab，启动后台 IO 状态轮询
                }
            };

            Unloaded += (s, e) =>
            {
                if (DataContext is IoMonitorViewModel vm)
                {
                    vm.IsActive = false; // 切出该 Tab，立即停止轮询，节省串口/网口总线资源
                }
            };
        }
    }
}
