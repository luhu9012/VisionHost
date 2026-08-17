using Grayson.Vision.WpfUI.ViewModel;
using System.Windows.Controls;

namespace Grayson.Vision.WpfUI.View
{
    public partial class StationManageView : UserControl
    {
        private bool _isUpdatingSelection = false;

        public StationManageView()
        {
            InitializeComponent();

            // 显式指定 ViewModel，解决 DataContext 找不到的问题
            this.DataContext = new StationManageViewModel();

        }

        private void TreeView_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
        {
            // 防重入锁：如果正在处理选中变更，直接跳出，切断死循环
            if (_isUpdatingSelection) return;

            try
            {
                _isUpdatingSelection = true;

                if (DataContext is StationManageViewModel vm && e.NewValue != null)
                {
                    vm.OnSelectedNodeChanged(e.NewValue);
                }
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        }
    }
}