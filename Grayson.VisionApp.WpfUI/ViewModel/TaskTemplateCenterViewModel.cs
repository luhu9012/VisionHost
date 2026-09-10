// Grayson.Vision.WpfUI/ViewModel/TaskTemplateCenterViewModel.cs
// 任务模板中心（T 层）列表页 VM（2026-09-09 落地）：
//   任务模板 = 执行方案/配方/纯视觉链的"族级可复用封装"，列表按 类型族/运行载体/状态 筛选，
//   关键资产列按类型给出（引导→引擎+配方；DL→模型；测量→测量项数），绑定工位列由工位配置反查实时统计。
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.WpfUI.Service;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>模板列表行（含展示文本与徽章色，避免 XAML 写转换器）</summary>
    public class TaskTemplateRowVm : ViewModelBase
    {
        public TaskTemplateInfo Tpl { get; set; }

        public string KindBadge => $"{TaskKindCatalog.KindIcon(Tpl.Kind)} {TaskKindCatalog.KindDisplay(Tpl.Kind)}";
        public SolidColorBrush KindBrush
        {
            get
            {
                switch (Tpl.Kind)
                {
                    case TaskKind.PositioningGuidance: return new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
                    case TaskKind.DeepLearningInference: return new SolidColorBrush(Color.FromRgb(0x8F, 0x7B, 0xF2));
                    case TaskKind.AppearanceMeasurement: return new SolidColorBrush(Color.FromRgb(0x43, 0xB7, 0x81));
                    default: return new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));
                }
            }
        }

        public string Code => Tpl.TemplateCode;
        public string Name => Tpl.DisplayName;
        public string Summary => Tpl.Summary;
        public string DependencyText => TaskKindCatalog.DependencyDisplay(Tpl.DependencyMode);
        public string StatusText => TaskKindCatalog.StatusDisplay(Tpl.Status);

        /// <summary>关键资产列文案（按族给出主资产/链信息）</summary>
        public string AssetText
        {
            get
            {
                switch (Tpl.Kind)
                {
                    case TaskKind.PositioningGuidance:
                        var engine = string.IsNullOrWhiteSpace(Tpl.EngineKey) ? "未选执行方案"
                            : (StationProcessCatalog.Find(Tpl.EngineKey)?.Name ?? Tpl.EngineKey);
                        var recipe = string.IsNullOrWhiteSpace(Tpl.BoundRecipeId) ? "" : $" + 配方 {Tpl.BoundRecipeId}";
                        return engine + recipe;
                    case TaskKind.DeepLearningInference:
                        return string.IsNullOrWhiteSpace(Tpl.ModelAssetCode) ? "未关联模型"
                            : $"模型 {Tpl.ModelAssetCode}" + (Tpl.ConfidenceThreshold.HasValue ? $" @阈值 {Tpl.ConfidenceThreshold.Value:0.##}" : "");
                    case TaskKind.AppearanceMeasurement:
                        return Tpl.MeasurementSpecs == null || Tpl.MeasurementSpecs.Count == 0
                            ? "未配置测量项" : $"测量项 ×{Tpl.MeasurementSpecs.Count}";
                    default: return "";
                }
            }
        }

        public string ImageText
        {
            get
            {
                if (Tpl.ImageSource == null) return "—";
                switch (Tpl.ImageSource.Kind)
                {
                    case TaskImageSourceKind.CameraSource: return $"相机 · {Tpl.ImageSource.CameraSlotKey}";
                    case TaskImageSourceKind.LocalFolder: return Tpl.ImageSource.LocalFolderPath;
                    case TaskImageSourceKind.LocalFile: return Tpl.ImageSource.LocalFilePattern ?? "单图";
                    default: return "—";
                }
            }
        }

        /// <summary>绑定工位（“工位名(代码)”逗号拼接）</summary>
        public string BoundText => (Tpl.Bindings == null || Tpl.Bindings.Count == 0)
            ? "未绑定" : string.Join("、", Tpl.Bindings.Select(b => b.StationName + "(" + b.StationCode + ")"));
        public int BoundCount => Tpl.Bindings == null ? 0 : Tpl.Bindings.Count;
        public string Version => Tpl.Version;
        public string UpdatedText => Tpl.UpdatedTime.ToString("MM-dd HH:mm");
    }

    /// <summary>筛选下拉项（展示文本 + 值，null=全部）</summary>
    public class TaskKindFilterOption
    {
        public TaskKind? Value { get; set; }
        public string Text { get; set; }
        public override string ToString() { return Text; }
    }

    public class TaskDepFilterOption
    {
        public TaskDependencyMode? Value { get; set; }
        public string Text { get; set; }
        public override string ToString() { return Text; }
    }

    public class TaskTemplateCenterViewModel : ViewModelBase
    {
        private readonly TaskTemplateLibraryService _library = new TaskTemplateLibraryService();
        private readonly StationConfigService _stationConfig = new StationConfigService();

        public ObservableCollection<TaskTemplateRowVm> Rows { get; } = new ObservableCollection<TaskTemplateRowVm>();

        // —— 筛选下拉选项（固定四/三项）——
        public TaskKindFilterOption[] KindFilterOptions { get; } =
        {
            new TaskKindFilterOption { Value = null, Text = "全部类型" },
            new TaskKindFilterOption { Value = TaskKind.PositioningGuidance, Text = "🧭 引导定位" },
            new TaskKindFilterOption { Value = TaskKind.DeepLearningInference, Text = "🧠 深度学习推理" },
            new TaskKindFilterOption { Value = TaskKind.AppearanceMeasurement, Text = "📏 外观测量" }
        };

        public TaskDepFilterOption[] DepFilterOptions { get; } =
        {
            new TaskDepFilterOption { Value = null, Text = "全部载体" },
            new TaskDepFilterOption { Value = TaskDependencyMode.StationBound, Text = "工位联动" },
            new TaskDepFilterOption { Value = TaskDependencyMode.Standalone, Text = "独立纯软件" }
        };

        private TaskKindFilterOption _selectedKindOption;
        public TaskKindFilterOption SelectedKindOption
        {
            get => _selectedKindOption;
            set { if (Set(ref _selectedKindOption, value)) ApplyFilter(); }
        }

        private TaskDepFilterOption _selectedDepOption;
        public TaskDepFilterOption SelectedDepOption
        {
            get => _selectedDepOption;
            set { if (Set(ref _selectedDepOption, value)) ApplyFilter(); }
        }

        private string _searchText = "";
        public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) ApplyFilter(); } }

        private TaskTemplateRowVm _selected;
        public TaskTemplateRowVm Selected { get => _selected; set { Set(ref _selected, value); OnPropertyChanged(nameof(HasSelection)); } }
        public bool HasSelection => Selected != null;

        // —— 统计卡 ——
        private int _totalCount, _guidanceCount, _dlCount, _measureCount, _publishedCount, _boundStationCount;
        public int TotalCount { get => _totalCount; set => Set(ref _totalCount, value); }
        public int GuidanceCount { get => _guidanceCount; set => Set(ref _guidanceCount, value); }
        public int DlCount { get => _dlCount; set => Set(ref _dlCount, value); }
        public int MeasureCount { get => _measureCount; set => Set(ref _measureCount, value); }
        public int PublishedCount { get => _publishedCount; set => Set(ref _publishedCount, value); }
        public int BoundStationCount { get => _boundStationCount; set => Set(ref _boundStationCount, value); }

        public ICommand NewCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand DuplicateCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand RefreshCommand { get; }

        private ObservableCollection<TaskTemplateInfo> _all = new ObservableCollection<TaskTemplateInfo>();

        public TaskTemplateCenterViewModel()
        {
            SelectedKindOption = KindFilterOptions[0];
            SelectedDepOption = DepFilterOptions[0];
            NewCommand = new RelayCommand(_ => NewTemplate());
            EditCommand = new RelayCommand(_ => EditTemplate());
            DuplicateCommand = new RelayCommand(_ => DuplicateTemplate());
            DeleteCommand = new RelayCommand(_ => DeleteTemplate());
            RefreshCommand = new RelayCommand(_ => Refresh());
        }

        public void Refresh()
        {
            // 🌟 stage9-2 迭代：引导定位执行方案 → 任务模板 静态翻译（幂等）。
            // 旧"工位直接装配 ProcessKey"入口已废弃且无历史工位，无需迁移兼容；
            // 三个内置执行方案翻译为引导定位模板后，模板中心即为其唯一入口。
            try
            {
                TaskTemplateTranslatorService.EnsureTranslatedGuidanceTemplates();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TaskTemplateCenter] 引导模板翻译种子失败: {ex.Message}");
            }

            // 🌟 内嵌 DL 演示包自动落地（无按钮、不依赖外部示例目录）：
            // 资产已内嵌 Assets\DLDemo（模型+精选图），随构建输出；此处仅幂等拷贝到
            // Config\Models|Config\DemoData 并生成配方/模板。已存在则全跳过，开销极小。
            try
            {
                Service.DlDemoTaskFactory.EnsureDemoTasks();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TaskTemplateCenter] DL 演示包落地种子失败: {ex.Message}");
            }

            // 🌟 stage9-3 外观测量族闭环（2026-09-10）：内嵌测量素材自动落地，
            // 生成 TPL-AM seed + FitCircle 配方 + MeasurementSpecs 判据；幂等，已存在则跳过。
            try
            {
                Service.MeasurementDemoTaskFactory.EnsureMeasurementDemoTasks();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TaskTemplateCenter] 测量演示包落地种子失败: {ex.Message}");
            }

            _all = new ObservableCollection<TaskTemplateInfo>(_library.LoadAll());
            // 绑定工位反查（唯一真源=StationConfigModel.TaskTemplateCode；模板库 Bindings 仅展示缓存）
            var stations = _stationConfig.LoadAllLines()
                .SelectMany(l => l.Stations ?? new List<StationConfigModel>())
                .Where(s => !string.IsNullOrWhiteSpace(s.TaskTemplateCode))
                .ToList();
            foreach (var tpl in _all)
            {
                tpl.Bindings = stations
                    .Where(s => string.Equals(s.TaskTemplateCode, tpl.TemplateCode, StringComparison.OrdinalIgnoreCase))
                    .Select(s => new TaskStationBinding
                    {
                        StationId = s.StationId,
                        StationCode = s.StationCode,
                        StationName = s.StationName,
                        LineName = s.LineName,
                        BoundProcessKey = s.ProcessKey
                    })
                    .ToList();
            }

            TotalCount = _all.Count;
            GuidanceCount = _all.Count(t => t.Kind == TaskKind.PositioningGuidance);
            DlCount = _all.Count(t => t.Kind == TaskKind.DeepLearningInference);
            MeasureCount = _all.Count(t => t.Kind == TaskKind.AppearanceMeasurement);
            PublishedCount = _all.Count(t => t.Status == TaskTemplateStatus.Published);
            BoundStationCount = stations.Count;

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            Rows.Clear();
            var kind = SelectedKindOption?.Value;
            var dep = SelectedDepOption?.Value;
            foreach (var tpl in _all)
            {
                if (kind.HasValue && tpl.Kind != kind.Value) continue;
                if (dep.HasValue && tpl.DependencyMode != dep.Value) continue;
                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    var q = SearchText.Trim();
                    bool hit = (tpl.TemplateCode ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                               || (tpl.DisplayName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                               || (tpl.Summary ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                }
                Rows.Add(new TaskTemplateRowVm { Tpl = tpl });
            }
        }

        private TaskTemplateInfo CurrentTemplate => Selected?.Tpl;

        private void NewTemplate()
        {
            // stage9-2：新建任务 → 行业全量目录向导（仅三大已实现族可生成）
            var win = new View.NewTaskTemplateWindow { Owner = Application.Current.MainWindow };
            if (win.ShowDialog() == true) Refresh();
        }

        private void EditTemplate()
        {
            var tpl = CurrentTemplate;
            if (tpl == null) { MessageBox.Show("请先在列表中选择一个任务模板。", "提示"); return; }
            var vm = new TaskTemplateEditViewModel(tpl, isNew: false);
            var win = new View.TaskTemplateEditWindow { Owner = Application.Current.MainWindow };
            win.DataContext = vm;
            if (win.ShowDialog() == true) Refresh();
        }

        private void DuplicateTemplate()
        {
            var tpl = CurrentTemplate;
            if (tpl == null) { MessageBox.Show("请先选择要复制的任务模板。", "提示"); return; }
            var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<TaskTemplateInfo>(
                Newtonsoft.Json.JsonConvert.SerializeObject(tpl));
            copy.TemplateCode = _library.GenerateCode(tpl.Kind);
            copy.DisplayName = (tpl.DisplayName ?? "模板") + "（副本）";
            copy.Status = TaskTemplateStatus.Draft;
            copy.Bindings = new System.Collections.Generic.List<TaskStationBinding>();
            _library.Save(copy);
            Refresh();
        }

        private void DeleteTemplate()
        {
            var tpl = CurrentTemplate;
            if (tpl == null) { MessageBox.Show("请先选择要删除的任务模板。", "提示"); return; }
            bool hasBinding = tpl.Bindings != null && tpl.Bindings.Count > 0;
            string msg = hasBinding
                ? $"模板【{tpl.TemplateCode} {tpl.DisplayName}】仍绑定 {tpl.Bindings.Count} 个工位。\n确认删除？将清除这些工位上的任务模板绑定及其派生的运行引擎（模板为引擎唯一入口，解绑即卸载引擎）。"
                : $"确认删除模板【{tpl.TemplateCode} {tpl.DisplayName}】？";
            if (MessageBox.Show(msg, "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            if (hasBinding)
            {
                foreach (var b in tpl.Bindings.ToList())
                {
                    var line = _stationConfig.LoadAllLines().FirstOrDefault(l => l.Stations != null
                        && l.Stations.Any(s => s.StationId == b.StationId));
                    var st = line?.Stations?.FirstOrDefault(s => s.StationId == b.StationId);
                    if (st != null)
                    {
                        st.TaskTemplateCode = null;
                        st.TaskTemplateName = null;
                        st.TaskTemplateKindText = null;
                        st.ProcessKey = null;
                        st.ProcessConfigJson = null;
                        _stationConfig.SaveStation(st);
                    }
                }
            }
            _library.Delete(tpl.TemplateCode);
            Refresh();
        }
    }
}
