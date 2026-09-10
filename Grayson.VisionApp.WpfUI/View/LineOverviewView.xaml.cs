using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    public partial class LineOverviewView : UserControl, INavigationAware
    {
        public LineOverviewView()
        {
            InitializeComponent();
            DataContext = new LineOverviewViewModel();
        }

        /// <summary>
        /// 导航激活钩子（页面注册为缓存单例，跨导航复用同一实例）：
        /// 转发给 VM 触发「按配置全量重建」，增删/修改工位后回到本页立即刷新，无需重启。
        /// </summary>
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

        private void CardBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 左键按下时仍允许双击事件冒泡
        }

        private void CardBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is StationStatusCardModel card)
            {
                if (DataContext is LineOverviewViewModel vm)
                {
                    vm.SelectedCard = card;
                }
            }
        }
    }
}
