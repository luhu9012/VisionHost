using Grayson.Vision.WpfUI.ViewModel;
using System.Windows.Controls;

namespace Grayson.Vision.WpfUI.View
{
    public partial class StationManageView : UserControl
    {
        private bool _isUpdatingSelection = false;
        private StationManageViewModel _vm;

        public StationManageView()
        {
            InitializeComponent();

            // 显式指定 ViewModel，解决 DataContext 找不到的问题
            this.DataContext = new StationManageViewModel();
            _vm = DataContext as StationManageViewModel;

            // 页面卸载时释放引擎参数/示教面板（退订 worker 事件防泄漏）
            Unloaded += (s, e) => _vm?.NotifyPageUnloaded();
        }

        /// <summary>
        /// 配方逻辑→物理映射下拉改动后即时重算装配进度（映射状态无需等保存才刷新）。
        /// </summary>
        private void MappingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm?.NotifyStationEdited();
        }

        /// <summary>
        /// 产品配方下拉改动：配方切换已由 StationModel.BoundRecipe setter 触发映射重建，
        /// 此处仅兜底刷新装配进度/站头摘要（含清空选择、配方库为空等边界）。
        /// </summary>
        private void RecipeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm?.NotifyStationEdited();
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
