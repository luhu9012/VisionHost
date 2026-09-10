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
        /// <summary>
        /// 新建菜单动态生成（2026-09-04）：菜单项与右侧类型下拉同源于
        /// CalibrationTypeOptions，消除此前 XAML 硬编码导致的"新建侧缺吸放式、残留棋盘格"
        /// 分叉。开发中(TODO)项 IsAvailable=false → 灰显禁用，鼠标悬停显示说明。
        /// </summary>
        private void BtnNewProfile_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(DataContext is ViewModel.CalibrationManagerViewModel vm))
            {
                return;
            }
            var menu = btn.ContextMenu;
            if (menu == null)
            {
                return;
            }

            menu.Items.Clear();
            foreach (var opt in vm.CalibrationTypeOptions)
            {
                var item = new MenuItem
                {
                    Header = opt.DisplayName,
                    IsEnabled = opt.IsAvailable,
                    ToolTip = opt.Description,
                    Command = vm.NewProfileCommand,
                    CommandParameter = opt.Type
                };
                menu.Items.Add(item);
            }

            menu.PlacementTarget = btn;
            menu.IsOpen = true;
        }

        /// <summary>标题名称编辑失焦 → 持久化到仓库并刷新列表项</summary>
        private void ProfileNameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModel.CalibrationManagerViewModel vm)
            {
                vm.PersistProfileName();
            }
        }

        /// <summary>候选清单勾选变化 → VM 刷新统计与创建命令可用性</summary>
        private void CandidateCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModel.CalibrationManagerViewModel vm)
            {
                vm.NotifyCandidateCheckChanged();
            }
        }
    }
}
