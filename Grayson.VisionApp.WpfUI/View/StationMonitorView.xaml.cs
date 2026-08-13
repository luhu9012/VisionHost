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
            DataContext = new StationMonitorViewModel();
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