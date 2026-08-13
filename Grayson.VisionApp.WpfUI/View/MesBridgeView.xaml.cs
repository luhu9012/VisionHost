using System.Windows.Controls;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    public partial class MesBridgeView : UserControl
    {
        public MesBridgeView()
        {
            InitializeComponent();
            DataContext = new MesBridgeViewModel();
        }
    }
}
