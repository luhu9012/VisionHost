using System.Windows;

namespace Grayson.Vison.FlowEdit
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑 (作为独立 Exe 运行时的启动壳)
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        // ⚠️ 注意：
        // 以前这里的 FlowCanvas_PreviewMouseWheel、FlowCanvas_MouseMove 
        // 以及所有拖拽、连线相关的事件代码，
        // 现在都应该且必须放在 FlowEditView.xaml.cs 中！
        // 这里不需要保留任何逻辑。
    }
}