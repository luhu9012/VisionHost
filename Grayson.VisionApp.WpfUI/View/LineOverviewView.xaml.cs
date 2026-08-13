using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    public partial class LineOverviewView : UserControl
    {
        public LineOverviewView()
        {
            InitializeComponent();
            DataContext = new LineOverviewViewModel();
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
