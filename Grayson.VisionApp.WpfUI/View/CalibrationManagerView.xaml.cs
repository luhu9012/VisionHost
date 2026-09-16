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

            // 2026-09-11：复合工位（上/下相机多槽）在新建时就要能挑槽——
            // 档案定义 ≥2 个槽时给每种类型展开「按相机槽新建」子菜单（如 Cam_A 上固定 / Cam_C 下固定），
            // 否则维持原单层菜单（槽走档案建议值，建完仍可在右侧「相机槽」下拉改）。
            var slots = new System.Collections.Generic.List<ViewModel.CalibrationManagerViewModel.SlotChoice>();
            try { slots = vm.GetArchiveSlotOptions(); } catch { slots.Clear(); }

            foreach (var opt in vm.CalibrationTypeOptions)
            {
                var item = new MenuItem
                {
                    Header = opt.DisplayName,
                    IsEnabled = opt.IsAvailable,
                    ToolTip = opt.Description
                };

                if (slots != null && slots.Count >= 2)
                {
                    foreach (var sc in slots)
                    {
                        item.Items.Add(new MenuItem
                        {
                            Header = sc.Label,
                            IsEnabled = opt.IsAvailable,
                            ToolTip = "按相机槽 " + sc.SlotKey + " 新建【" + opt.DisplayName + "】",
                            Command = vm.NewProfileForSlotCommand,
                            CommandParameter = new ViewModel.CalibrationManagerViewModel.NewProfileRequest
                            {
                                Type = opt.Type,
                                SlotKey = sc.SlotKey
                            }
                        });
                    }
                }
                else
                {
                    item.Command = vm.NewProfileForSlotCommand;
                    item.CommandParameter = new ViewModel.CalibrationManagerViewModel.NewProfileRequest { Type = opt.Type };
                }

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
