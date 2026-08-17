using System.Windows;
using System.Windows.Controls;

namespace Grayson.Vision.WpfUI.View
{
    public partial class CalibrationManagerView : UserControl
    {
        public CalibrationManagerView()
        {
            InitializeComponent();
            DataContext = new ViewModel.CalibrationManagerViewModel();
        }
        private void BtnNewProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.IsOpen = true;
            }
        }
    }
}
