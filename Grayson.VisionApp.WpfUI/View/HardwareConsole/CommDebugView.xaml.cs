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
    /// CommDebugView.xaml 的交互逻辑
    /// </summary>
    public partial class CommDebugView : UserControl
    {
        public CommDebugView()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                if (DataContext is CommDebugViewModel vm)
                {
                    vm.IsActive = true; // 进入通信调试界面（含独立窗口），开启日志监听
                }
            };

            Unloaded += (s, e) =>
            {
                if (DataContext is CommDebugViewModel vm && !vm.IsDetached)
                {
                    // 独立窗口模式（IsDetached=true）下主 Tab 切走不停订阅——独立窗口仍要刷新
                    vm.IsActive = false;
                }
            };
        }
    }
}
