using System.Windows.Controls;
namespace Grayson.Vision.WpfUI.View
{
    public partial class UserManageView : UserControl
    {
        public UserManageView() { InitializeComponent(); DataContext = new ViewModel.UserManageViewModel(); }
    }
}
