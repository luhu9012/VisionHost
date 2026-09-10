//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationWizardWindow.xaml.cs
// 说 明: 新建工位向导窗口（code-behind 极薄：按钮收尾 + DialogResult 协议）。
//        结果读取：DialogResult==true 后取 (DataContext as StationWizardViewModel)：
//          · ExitAsDraft=true  → ResultProfile 已是 Draft 并落盘；
//          · ExitAsDraft=false → ResultProfile 待父 VM 补 StationId 后落 Active + 建真实工位。
//===================================================================================
using System.Windows;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    public partial class StationWizardWindow : Window
    {
        private readonly StationWizardViewModel _vm;

        public StationWizardWindow(string lineId, string lineName, string suggestedCode, string suggestedName)
        {
            InitializeComponent();
            _vm = new StationWizardViewModel(lineId, lineName, suggestedCode, suggestedName);
            DataContext = _vm;
            Loaded += (s, e) => { if (!_vm.HasDrafts) _vm.SelectedDraft = null; };
        }

        /// <summary>
        /// 编辑模式构造：载入已有档案（Active）或父 VM 构造的"补档空壳"，收尾=保存档案修改。
        /// </summary>
        public StationWizardWindow(Contracts.Station.Models.StationProfile editProfile)
        {
            InitializeComponent();
            _vm = new StationWizardViewModel(editProfile);
            DataContext = _vm;
            Loaded += (s, e) => { if (!_vm.HasDrafts) _vm.SelectedDraft = null; };
        }

        /// <summary>设计器/兜底无参构造</summary>
        public StationWizardWindow() : this(null, null, string.Empty, string.Empty)
        {
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BtnSaveDraft_Click(object sender, RoutedEventArgs e)
        {
            _vm.ExitAsDraft = true;
            _vm.FinalizeProfile();
            if (!new StationProfileRepository().Save(_vm.ResultProfile))
            {
                MessageBox.Show("草稿保存失败（详见日志）。", "存草稿", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        }

        private void BtnLoadDraft_Click(object sender, RoutedEventArgs e)
        {
            if (!_vm.LoadSelectedDraft())
            {
                MessageBox.Show("请先在左侧下拉选择一条草稿。", "载入草稿", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnDeleteDraft_Click(object sender, RoutedEventArgs e)
        {
            _vm.DeleteSelectedDraft();
        }

        // ==================== ⑤ 相机槽（2026-09-05）====================
        private void BtnAddSlot_Click(object sender, RoutedEventArgs e)
        {
            _vm.AddCameraSlot();
        }

        private void BtnRemoveSlot_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is Contracts.Station.Models.VisionSlotInfo slot)
            {
                _vm.RemoveCameraSlot(slot);
            }
        }

        /// <summary>槽行任一字段变化 → 重算右侧推导预览</summary>
        private void SlotField_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _vm.NotifySlotEdited();
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            // 编辑模式：直接回写 Active 档案（代码/名称已锁定；问卷字段实时已写回 profile）
            if (_vm.IsEditMode)
            {
                _vm.FinalizeProfile();
                _vm.ResultProfile.Status = Contracts.Station.Models.StationProfileStatus.Active;
                if (!new StationProfileRepository().Save(_vm.ResultProfile))
                {
                    MessageBox.Show("档案保存失败（详见日志）。", "保存档案修改", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _vm.ExitAsDraft = false;
                DialogResult = true;
                return;
            }

            string err = _vm.ValidateForCreate();
            if (!string.IsNullOrEmpty(err))
            {
                MessageBox.Show(err, "创建工位", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _vm.ExitAsDraft = false;
            _vm.FinalizeProfile();
            DialogResult = true;
        }
    }
}
