// Grayson.Vision.WpfUI/ViewModel/TaskTemplateEditViewModel.cs
// 任务模板编辑对话框 VM（2026-09-09）：
//   四个分区：① 标识与类型族 ② 运行载体与图像源 ③ 任务链/资产（按类型族动态：引导→执行方案+配方+模板+标定+示教；
//   深度学习→模型资产+阈值；外观测量→测量项表） ④ 输出契约 + 通用资产引用 + 工位部署（勾选即写 StationConfigModel.TaskTemplate*）。
//   安全：打开时深拷贝，确定才落盘（JSON 库 + 工位绑定），取消不影响原对象。
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.WpfUI.Service;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>ComboBox 显示项（值 + 中文文本）</summary>
    public class LabeledOption
    {
        public object Value { get; set; }
        public string Text { get; set; }
        public override string ToString() { return Text; }
    }

    /// <summary>工位部署行</summary>
    public class StationBindVm : ViewModelBase
    {
        public StationConfigModel Station { get; set; }
        public string LineName { get; set; }
        public string Display => $"{Station.StationName}({Station.StationCode})";
        public string ProcessText => string.IsNullOrWhiteSpace(Station.ProcessKey) ? "未挂执行方案" : $"ProcessKey={Station.ProcessKey}";
        /// <summary>当前是否绑定正在编辑的模板</summary>
        public bool CurrentlyBoundToThis { get; set; }
        public string BoundOtherText { get; set; }

        private bool _isBound;
        public bool IsBound
        {
            get => _isBound;
            set => Set(ref _isBound, value);
        }
    }

    public class TaskTemplateEditViewModel : ViewModelBase
    {
        private readonly TaskTemplateLibraryService _library = new TaskTemplateLibraryService();
        private readonly StationConfigService _stationConfig = new StationConfigService();

        /// <summary>编辑工作副本（打开时深拷贝；确定才回写）</summary>
        public TaskTemplateInfo Tpl { get; }

        public string WindowTitle { get; }

        private readonly bool _isNew;

        // —— 类型族 ——
        public LabeledOption[] KindOptions { get; } =
        {
            new LabeledOption { Value = TaskKind.PositioningGuidance, Text = "🧭 引导定位（相机+标定+执行方案，工位联动）" },
            new LabeledOption { Value = TaskKind.DeepLearningInference, Text = "🧠 深度学习推理（本地图像源+模型，独立纯软件）" },
            new LabeledOption { Value = TaskKind.AppearanceMeasurement, Text = "📏 外观测量（本地图像源+测量算子，独立纯软件）" }
        };

        private LabeledOption _selectedKindOption;
        public LabeledOption SelectedKindOption
        {
            get => _selectedKindOption;
            set
            {
                if (Set(ref _selectedKindOption, value) && value != null)
                {
                    Tpl.Kind = (TaskKind)value.Value;
                    NotifyKindChanged();
                    // 新建态下切换类型族 → 自动按新族生成代码（编辑态保持既有代码不动）
                    if (_isNew) RegenerateCode();
                }
            }
        }

        public LabeledOption[] DependencyOptions { get; } =
        {
            new LabeledOption { Value = TaskDependencyMode.StationBound, Text = "工位联动（需相机/运动/IO，由工位监视【启动】驱动）" },
            new LabeledOption { Value = TaskDependencyMode.Standalone, Text = "独立纯软件（仅本地图像源+资产即可运行，无硬件依赖）" }
        };

        private LabeledOption _selectedDepOption;
        public LabeledOption SelectedDepOption
        {
            get => _selectedDepOption;
            set
            {
                if (Set(ref _selectedDepOption, value) && value != null)
                    Tpl.DependencyMode = (TaskDependencyMode)value.Value;
            }
        }

        public LabeledOption[] StatusOptions { get; } =
        {
            new LabeledOption { Value = TaskTemplateStatus.Draft, Text = "草稿" },
            new LabeledOption { Value = TaskTemplateStatus.Published, Text = "已发布" },
            new LabeledOption { Value = TaskTemplateStatus.Retired, Text = "已停用" }
        };

        private LabeledOption _selectedStatusOption;
        public LabeledOption SelectedStatusOption
        {
            get => _selectedStatusOption;
            set
            {
                if (Set(ref _selectedStatusOption, value) && value != null)
                    Tpl.Status = (TaskTemplateStatus)value.Value;
            }
        }

        // —— 图像源 ——
        public LabeledOption[] ImageSourceOptions { get; } =
        {
            new LabeledOption { Value = TaskImageSourceKind.CameraSource, Text = "相机源（占用工位相机槽）" },
            new LabeledOption { Value = TaskImageSourceKind.LocalFolder, Text = "本地文件夹（批量/离线/独立运行）" },
            new LabeledOption { Value = TaskImageSourceKind.LocalFile, Text = "本地单张图片（试运行/验证）" }
        };

        private LabeledOption _selectedImageSourceOption;
        public LabeledOption SelectedImageSourceOption
        {
            get => _selectedImageSourceOption;
            set
            {
                if (Set(ref _selectedImageSourceOption, value) && value != null)
                {
                    Tpl.ImageSource.Kind = (TaskImageSourceKind)value.Value;
                    OnPropertyChanged(nameof(IsCameraSource));
                    OnPropertyChanged(nameof(IsLocalSource));
                }
            }
        }

        // —— 引导定位：执行方案 ——
        public EngineOption[] EngineOptions => StationProcessCatalog.GetOptions();

        // —— 深度学习：模型 ——
        public List<ModelAssetInfo> ModelOptions => new ModelRegistryService().LoadAll();

        // —— 外观测量 ——
        public ObservableCollection<MeasurementSpecItem> MeasurementSpecs { get; } = new ObservableCollection<MeasurementSpecItem>();

        // —— 通用资产引用 ——
        public LabeledOption[] AssetKindOptions { get; } =
        {
            new LabeledOption { Value = TaskAssetKind.ShapeTemplate, Text = "形状模板" },
            new LabeledOption { Value = TaskAssetKind.CalibrationProfile, Text = "标定档案" },
            new LabeledOption { Value = TaskAssetKind.DlModel, Text = "深度学习模型" },
            new LabeledOption { Value = TaskAssetKind.Recipe, Text = "配方" },
            new LabeledOption { Value = TaskAssetKind.TeachReference, Text = "示教参考" },
            new LabeledOption { Value = TaskAssetKind.Other, Text = "其他" }
        };
        public ObservableCollection<TaskTemplateAssetRef> AssetRefs { get; } = new ObservableCollection<TaskTemplateAssetRef>();

        // —— 工位部署 ——
        public ObservableCollection<StationBindVm> StationItems { get; } = new ObservableCollection<StationBindVm>();

        // —— 展示辅助 ——
        public bool IsGuidance => Tpl.Kind == TaskKind.PositioningGuidance;
        public bool IsDl => Tpl.Kind == TaskKind.DeepLearningInference;
        public bool IsAppearance => Tpl.Kind == TaskKind.AppearanceMeasurement;
        public bool IsCameraSource => Tpl.ImageSource.Kind == TaskImageSourceKind.CameraSource;
        public bool IsLocalSource => Tpl.ImageSource.Kind != TaskImageSourceKind.CameraSource;
        public string KindHint => TaskKindCatalog.KindHint(Tpl.Kind);

        public ICommand BrowseFolderCommand { get; }
        public ICommand BrowseFileCommand { get; }
        public ICommand AddMeasureCommand { get; }
        public ICommand RemoveMeasureCommand { get; }
        public ICommand AddRefCommand { get; }
        public ICommand RemoveRefCommand { get; }
        public ICommand RegenerateCodeCommand { get; }

        public TaskTemplateEditViewModel(TaskTemplateInfo source, bool isNew)
        {
            _isNew = isNew;
            Tpl = source == null
                ? new TaskTemplateInfo()
                : JsonConvert.DeserializeObject<TaskTemplateInfo>(JsonConvert.SerializeObject(source));
            WindowTitle = isNew ? "新建任务模板" : $"编辑任务模板 · {Tpl.TemplateCode}";

            _selectedKindOption = KindOptions.FirstOrDefault(o => Equals(o.Value, Tpl.Kind)) ?? KindOptions[0];
            _selectedDepOption = DependencyOptions.FirstOrDefault(o => Equals(o.Value, Tpl.DependencyMode)) ?? DependencyOptions[0];
            _selectedStatusOption = StatusOptions.FirstOrDefault(o => Equals(o.Value, Tpl.Status)) ?? StatusOptions[0];
            _selectedImageSourceOption = ImageSourceOptions.FirstOrDefault(o => Equals(o.Value, Tpl.ImageSource.Kind)) ?? ImageSourceOptions[0];

            foreach (var m in Tpl.MeasurementSpecs ?? new List<MeasurementSpecItem>()) MeasurementSpecs.Add(m);
            foreach (var r in Tpl.AssetRefs ?? new List<TaskTemplateAssetRef>()) AssetRefs.Add(r);

            LoadStationBindings();

            BrowseFolderCommand = new RelayCommand(_ => BrowseFolder());
            BrowseFileCommand = new RelayCommand(_ => BrowseModelFile());
            AddMeasureCommand = new RelayCommand(_ => MeasurementSpecs.Add(new MeasurementSpecItem { Name = "新测量项" + (MeasurementSpecs.Count + 1), ToolKind = "Caliper 卡尺", Enabled = true }));
            RemoveMeasureCommand = new RelayCommand(_ => { if (SelectedMeasure != null) MeasurementSpecs.Remove(SelectedMeasure); });
            AddRefCommand = new RelayCommand(_ => AssetRefs.Add(new TaskTemplateAssetRef { Kind = TaskAssetKind.Other }));
            RemoveRefCommand = new RelayCommand(_ => { if (SelectedRef != null) AssetRefs.Remove(SelectedRef); });
            RegenerateCodeCommand = new RelayCommand(_ => RegenerateCode());
        }

        private MeasurementSpecItem _selectedMeasure;
        public MeasurementSpecItem SelectedMeasure { get => _selectedMeasure; set => Set(ref _selectedMeasure, value); }

        private TaskTemplateAssetRef _selectedRef;
        public TaskTemplateAssetRef SelectedRef { get => _selectedRef; set => Set(ref _selectedRef, value); }

        private void NotifyKindChanged()
        {
            OnPropertyChanged(nameof(IsGuidance));
            OnPropertyChanged(nameof(IsDl));
            OnPropertyChanged(nameof(IsAppearance));
            OnPropertyChanged(nameof(KindHint));
        }

        private void RegenerateCode()
        {
            Tpl.TemplateCode = _library.GenerateCode(Tpl.Kind);
            OnPropertyChanged(nameof(Tpl));
            OnPropertyChanged(nameof(CodeText));
        }

        public string CodeText
        {
            get => Tpl.TemplateCode;
            set { Tpl.TemplateCode = value; }
        }

        public string NameText
        {
            get => Tpl.DisplayName;
            set { Tpl.DisplayName = value; }
        }

        public string SummaryText
        {
            get => Tpl.Summary;
            set { Tpl.Summary = value; }
        }

        /// <summary>模型资产下拉（深度学习模板）：文本=代码+名称</summary>
        public string ModelItemText(ModelAssetInfo m)
        {
            return m == null ? "" : $"{m.AssetCode} · {m.DisplayName}（{TaskKindCatalog.ModelKindDisplay(m.ModelKind)}）";
        }

        private void LoadStationBindings()
        {
            foreach (var line in _stationConfig.LoadAllLines())
            {
                foreach (var st in line.Stations ?? new List<StationConfigModel>())
                {
                    bool boundToThis = string.Equals(st.TaskTemplateCode, Tpl.TemplateCode, StringComparison.OrdinalIgnoreCase);
                    string other = (string.IsNullOrWhiteSpace(st.TaskTemplateCode) || boundToThis) ? null
                        : $"当前绑定 {st.TaskTemplateName ?? st.TaskTemplateCode}";
                    StationItems.Add(new StationBindVm
                    {
                        Station = st,
                        LineName = line.LineName,
                        CurrentlyBoundToThis = boundToThis,
                        IsBound = boundToThis,
                        BoundOtherText = other
                    });
                }
            }
        }

        private void BrowseFolder()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择本地图像源文件夹（独立纯软件任务将轮询该目录）";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    Tpl.ImageSource.Kind = TaskImageSourceKind.LocalFolder;
                    Tpl.ImageSource.LocalFolderPath = dlg.SelectedPath;
                    SyncImageSourceSelection();
                }
            }
        }

        private void BrowseModelFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择模型文件（复制到 Config\\Models\\{AssetCode}\\ 下后引用）",
                Filter = "ONNX 模型|*.onnx|HALCON DLTool|*.hdl|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                // 模型本体不在此处复制：仅提示落位方式；真正入库在「模型仓库」页注册时处理
                System.Windows.MessageBox.Show(
                    $"已选择：{dlg.FileName}\n\n请将模型文件复制到运行目录 Config\\Models\\{Tpl.ModelAssetCode ?? "MDL-xxx"}\\ 下，\n再在「模型仓库」页注册该资产（文件名/规格/类别/指标）。",
                    "模型文件引用", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }

        private void SyncImageSourceSelection()
        {
            _selectedImageSourceOption = ImageSourceOptions.FirstOrDefault(o => Equals(o.Value, Tpl.ImageSource.Kind)) ?? ImageSourceOptions[0];
            OnPropertyChanged(nameof(SelectedImageSourceOption));
            OnPropertyChanged(nameof(IsCameraSource));
            OnPropertyChanged(nameof(IsLocalSource));
        }

        public bool Save()
        {
            if (string.IsNullOrWhiteSpace(Tpl.TemplateCode) || string.IsNullOrWhiteSpace(Tpl.DisplayName))
            {
                System.Windows.MessageBox.Show("请填写模板代码与名称。", "校验提示");
                return false;
            }
            // 工作集合 → 模型
            Tpl.MeasurementSpecs = MeasurementSpecs.ToList();
            Tpl.AssetRefs = AssetRefs.ToList();
            Tpl.ImageSource = Tpl.ImageSource ?? new TaskTemplateImageSource();
            _library.Save(Tpl);

            // 工位绑定写回（真源 StationConfigModel.TaskTemplate* + 派生 ProcessKey）
            // stage9-2 迭代语义：工位只认任务模板；模板 → 引擎(ProcessKey) 一律经翻译服务解析，
            // 引导定位=模板 EngineKey（已注册引擎键），深度学习/外观测量=StandaloneVision。
            int changed = 0;
            int autoEngineCount = 0;
            int recipeLinkedCount = 0;
            var engineWarnings = new List<string>();
            foreach (var item in StationItems)
            {
                var st = item.Station;
                bool wantsBind = item.IsBound;
                if (wantsBind && !string.Equals(st.TaskTemplateCode, Tpl.TemplateCode, StringComparison.OrdinalIgnoreCase))
                {
                    st.TaskTemplateCode = Tpl.TemplateCode;
                    st.TaskTemplateName = Tpl.DisplayName;
                    st.TaskTemplateKindText = TaskKindCatalog.KindDisplay(Tpl.Kind);

                    // 引擎装载（模板唯一入口；不再允许工位直接装配 ProcessKey）
                    string engine = TaskTemplateTranslatorService.ResolveProcessKeyForTemplate(Tpl);
                    if (!string.IsNullOrWhiteSpace(engine))
                    {
                        if (!string.Equals(st.ProcessKey, engine, StringComparison.Ordinal))
                        {
                            st.ProcessKey = engine;
                            autoEngineCount++;
                        }
                    }
                    else if (Tpl.Kind == TaskKind.PositioningGuidance)
                    {
                        engineWarnings.Add($"工位 {st.StationName}({st.StationCode})：模板未选执行方案(EngineKey)，未挂引擎，仅记录绑定。");
                    }

                    // 独立任务（DL/测量）：模板自带配方且工位未绑定配方 → 自动带上（可运行闭环）
                    if (Tpl.Kind != TaskKind.PositioningGuidance
                        && !string.IsNullOrWhiteSpace(Tpl.BoundRecipeId)
                        && string.IsNullOrWhiteSpace(st.BoundRecipeId))
                    {
                        st.BoundRecipeId = Tpl.BoundRecipeId;
                        st.BoundRecipeName = Tpl.BoundRecipeName;
                        recipeLinkedCount++;
                    }

                    _stationConfig.SaveStation(st);
                    changed++;
                }
                else if (!wantsBind && string.Equals(st.TaskTemplateCode, Tpl.TemplateCode, StringComparison.OrdinalIgnoreCase))
                {
                    // 解绑 = 移除模板绑定 + 移除派生引擎（无模板不允许残留裸引擎入口）
                    st.TaskTemplateCode = null;
                    st.TaskTemplateName = null;
                    st.TaskTemplateKindText = null;
                    st.ProcessKey = null;
                    st.ProcessConfigJson = null;
                    _stationConfig.SaveStation(st);
                    changed++;
                }
            }
            var notes = new List<string>();
            if (autoEngineCount > 0)
                notes.Add($"其中 {autoEngineCount} 个工位已自动装载运行引擎（模板派生）。");
            if (recipeLinkedCount > 0)
                notes.Add($"其中 {recipeLinkedCount} 个独立任务工位已自动带上模板配方（绑定配方 + 定时器触发源即可【启动】离线检测）。");
            notes.AddRange(engineWarnings);
            string note = notes.Count > 0 ? "\n" + string.Join("\n", notes) : "";
            if (changed > 0)
                System.Windows.MessageBox.Show($"任务模板已保存；工位绑定变更 {changed} 处。{note}", "保存完成");
            return true;
        }
    }
}
