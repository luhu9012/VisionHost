using System.Windows.Controls;

namespace Grayson.Vision.WpfUI.View
{
    public partial class AlarmView : UserControl
    {
        public AlarmView()
        {
            InitializeComponent();
            DataContext = new ViewModel.AlarmViewModel();
        }
    }
}
