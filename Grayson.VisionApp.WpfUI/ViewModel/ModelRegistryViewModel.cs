// Grayson.Vision.WpfUI/ViewModel/ModelRegistryViewModel.cs
// 模型仓库列表页 VM（2026-09-09）：管理深度学习/测量可复用推理资产（ONNX/DLTool）元数据，
// 与任务模板中心的"深度学习推理 / 外观测量"族模板通过 ModelAssetCode 关联。
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.WpfUI.Service;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>模型列表行（含展示文本/文件在库状态）</summary>
    public class ModelAssetRowVm : ViewModelBase
    {
        public ModelAssetInfo Asset { get; set; }

        public string Code => Asset.AssetCode;
        public string Name => Asset.DisplayName;
        public string KindText => TaskKindCatalog.ModelKindDisplay(Asset.ModelKind);
        public string FrameworkText => TaskKindCatalog.FrameworkDisplay(Asset.Framework);
        public string InputSpec => Asset.InputSpecText;
        public string ClassSummary
        {
            get
            {
                if (Asset.ClassNames == null || Asset.ClassNames.Count == 0)
                    return Asset.ModelKind == DlModelKind.Classification ? "未填类别" : "—";
                if (Asset.ClassNames.Count <= 4) return string.Join(" / ", Asset.ClassNames);
                return string.Join(" / ", Asset.ClassNames.Take(4)) + $" …共{Asset.ClassNames.Count}类";
            }
        }
        public string Metrics => Asset.MetricsText;
        public string StatusText
        {
            get
            {
                switch (Asset.Status)
                {
                    case ModelAssetStatus.Ready: return "就绪";
                    case ModelAssetStatus.Verifying: return "验证中";
                    case ModelAssetStatus.Disabled: return "已停用";
                    default: return "未知";
                }
            }
        }
        public string FileMark => Asset.IsFileInLibrary ? "文件在库" : "⚠ 文件缺失";
        public string FileName => Asset.FileName;
        public string UpdatedText => Asset.UpdatedTime.ToString("MM-dd HH:mm");
    }

    public class ModelRegistryViewModel : ViewModelBase
    {
        private readonly ModelRegistryService _registry = new ModelRegistryService();

        public ObservableCollection<ModelAssetRowVm> Items { get; } = new ObservableCollection<ModelAssetRowVm>();

        private string _searchText = "";
        public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) ApplyFilter(); } }

        private ModelAssetRowVm _selected;
        public ModelAssetRowVm Selected { get => _selected; set { Set(ref _selected, value); OnPropertyChanged(nameof(HasSelection)); } }
        public bool HasSelection => Selected != null;

        private int _totalCount, _readyCount;
        public int TotalCount { get => _totalCount; set => Set(ref _totalCount, value); }
        public int ReadyCount { get => _readyCount; set => Set(ref _readyCount, value); }

        public ICommand NewCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand RefreshCommand { get; }

        private ObservableCollection<ModelAssetInfo> _all = new ObservableCollection<ModelAssetInfo>();

        public ModelRegistryViewModel()
        {
            NewCommand = new RelayCommand(_ => NewAsset());
            EditCommand = new RelayCommand(_ => EditAsset());
            DeleteCommand = new RelayCommand(_ => DeleteAsset());
            RefreshCommand = new RelayCommand(_ => Refresh());
        }

        public void Refresh()
        {
            _all = new ObservableCollection<ModelAssetInfo>(_registry.LoadAll());
            foreach (var m in _all)
            {
                var file = Path.Combine(_registry.ModelRootDir, m.AssetCode, m.FileName ?? "");
                m.IsFileInLibrary = !string.IsNullOrWhiteSpace(m.FileName) && File.Exists(file);
            }
            TotalCount = _all.Count;
            ReadyCount = _all.Count(m => m.Status == ModelAssetStatus.Ready);
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            Items.Clear();
            foreach (var m in _all)
            {
                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    var q = SearchText.Trim();
                    bool hit = (m.AssetCode ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                               || (m.DisplayName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                               || (m.Remark ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                }
                Items.Add(new ModelAssetRowVm { Asset = m });
            }
        }

        private ModelAssetInfo Current => Selected?.Asset;

        private void NewAsset()
        {
            var draft = new ModelAssetInfo { AssetCode = _registry.GenerateCode(DlModelKind.Detection) };
            var vm = new ModelAssetEditViewModel(draft, isNew: true);
            var win = new View.ModelAssetEditWindow { Owner = Application.Current.MainWindow };
            win.DataContext = vm;
            if (win.ShowDialog() == true) Refresh();
        }

        private void EditAsset()
        {
            var asset = Current;
            if (asset == null) { MessageBox.Show("请先在列表中选择一个模型资产。", "提示"); return; }
            var vm = new ModelAssetEditViewModel(asset, isNew: false);
            var win = new View.ModelAssetEditWindow { Owner = Application.Current.MainWindow };
            win.DataContext = vm;
            if (win.ShowDialog() == true) Refresh();
        }

        private void DeleteAsset()
        {
            var asset = Current;
            if (asset == null) { MessageBox.Show("请先选择要删除的模型资产。", "提示"); return; }
            if (MessageBox.Show($"确认删除模型资产【{asset.AssetCode} {asset.DisplayName}】？\n仅删除注册信息与清单记录，不删除 Config\\Models 下的模型文件本体。",
                    "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _registry.Delete(asset.AssetCode);
            Refresh();
        }
    }
}
