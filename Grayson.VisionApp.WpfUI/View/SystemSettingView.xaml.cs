using System.Windows.Controls;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    public partial class SystemSettingView : UserControl
    {
        public SystemSettingView()
        {
            InitializeComponent();
            DataContext = new SystemSettingViewModel();
        }
    }
}
