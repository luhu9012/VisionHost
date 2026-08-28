// Grayson.Vision.WpfUI/View/StationMonitorView.xaml.cs
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.WpfUI.ViewModel;
using System.Windows.Controls;

namespace Grayson.Vision.WpfUI.View
{
    public partial class StationMonitorView : UserControl, INavigationAware
    {
        public StationMonitorView()
        {
            InitializeComponent();
            var vm = new StationMonitorViewModel();
            DataContext = vm;

            // 🌟 把本视图的 Halcon 显示控件注册为节点实时预览上下文：
            // 生产执行链里节点提交的叠加图形（模板匹配贴合轮廓/十字/文本）
            // 经此通路实时画到工位监视窗口（此前仅 FlowEdit 属性面板预览可见）
            vm.AttachDisplayHost(ImageHost);
        }

        public void OnNavigatedTo(object parameter)
        {
            if (DataContext is INavigationAware vmNav)
            {
                vmNav.OnNavigatedTo(parameter);
            }
        }

        public void OnNavigatedFrom()
        {
            if (DataContext is INavigationAware vmNav)
            {
                vmNav.OnNavigatedFrom();
            }
        }
    }
}