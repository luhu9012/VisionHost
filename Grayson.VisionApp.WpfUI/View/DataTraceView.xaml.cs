using System.Windows.Controls;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    public partial class DataTraceView : UserControl
    {
        public DataTraceView()
        {
            InitializeComponent();
            DataContext = new DataTraceViewModel();
        }
    }
}
