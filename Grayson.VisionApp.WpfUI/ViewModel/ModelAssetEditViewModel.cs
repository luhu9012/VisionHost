// Grayson.Vision.WpfUI/ViewModel/ModelAssetEditViewModel.cs
// 模型资产注册/编辑对话框 VM（2026-09-09）。
// 元数据入 Config\ModelRegistry\models.json；文件本体需落到 Config\Models\{AssetCode}\ 下（选择文件仅登记路径/大小）。
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.WpfUI.Service;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class ModelAssetEditViewModel : ViewModelBase
    {
        private readonly ModelRegistryService _registry = new ModelRegistryService();
        private readonly bool _isNew;

        public ModelAssetInfo Asset { get; }

        public string WindowTitle { get; }

        public LabeledOption[] ModelKindOptions { get; } =
        {
            new LabeledOption { Value = DlModelKind.Detection, Text = "目标检测（框）" },
            new LabeledOption { Value = DlModelKind.Segmentation, Text = "语义/实例分割（掩膜）" },
            new LabeledOption { Value = DlModelKind.Classification, Text = "分类（类别+置信度）" },
            new LabeledOption { Value = DlModelKind.AnomalyDetection, Text = "异常检测（OK/NG+分数）" }
        };

        public LabeledOption[] FrameworkOptions { get; } =
        {
            new LabeledOption { Value = DlFrameworkKind.OnnxRuntime, Text = "ONNX Runtime（Plugins.Inference.OnnxRuntime 推理）" },
            new LabeledOption { Value = DlFrameworkKind.HalconDlTool, Text = "HALCON DLTool（.hdl，DLTool 工作流）" },
            new LabeledOption { Value = DlFrameworkKind.Other, Text = "其他" }
        };

        public LabeledOption[] StatusOptions { get; } =
        {
            new LabeledOption { Value = ModelAssetStatus.Ready, Text = "就绪（可用）" },
            new LabeledOption { Value = ModelAssetStatus.Verifying, Text = "验证中" },
            new LabeledOption { Value = ModelAssetStatus.Disabled, Text = "已停用" }
        };

        private LabeledOption _selectedKind;
        public LabeledOption SelectedKind
        {
            get => _selectedKind;
            set
            {
                if (Set(ref _selectedKind, value) && value != null)
                {
                    Asset.ModelKind = (DlModelKind)value.Value;
                    if (_isNew && string.IsNullOrWhiteSpace(Asset.FileName))
                        AssetCodeText = _registry.GenerateCode(Asset.ModelKind);
                }
            }
        }

        private LabeledOption _selectedFramework;
        public LabeledOption SelectedFramework
        {
            get => _selectedFramework;
            set { if (Set(ref _selectedFramework, value) && value != null) Asset.Framework = (DlFrameworkKind)value.Value; }
        }

        private LabeledOption _selectedStatus;
        public LabeledOption SelectedStatus
        {
            get => _selectedStatus;
            set { if (Set(ref _selectedStatus, value) && value != null) Asset.Status = (ModelAssetStatus)value.Value; }
        }

        public string AssetCodeText
        {
            get => Asset.AssetCode;
            set { Asset.AssetCode = value; OnPropertyChanged(nameof(AssetCodeText)); OnPropertyChanged(nameof(FileHintText)); }
        }

        public string NameText
        {
            get => Asset.DisplayName;
            set { Asset.DisplayName = value; OnPropertyChanged(nameof(NameText)); }
        }

        public string FileNameText
        {
            get => Asset.FileName;
            set { Asset.FileName = value; OnPropertyChanged(nameof(FileNameText)); OnPropertyChanged(nameof(FileHintText)); }
        }

        public string InputSpecText
        {
            get => Asset.InputSpecText;
            set { Asset.InputSpecText = value; }
        }

        public string MetricsText
        {
            get => Asset.MetricsText;
            set { Asset.MetricsText = value; }
        }

        public string RemarkText
        {
            get => Asset.Remark;
            set { Asset.Remark = value; }
        }

        public string ClassNamesText { get; set; }

        public string ThresholdText { get; set; }

        /// <summary>文件目标位置提示（如 Config\Models\MDL-DET-001\model.onnx）</summary>
        public string FileHintText =>
            string.IsNullOrWhiteSpace(Asset.AssetCode) ? "" : $"文件应放入运行目录 Config\\Models\\{Asset.AssetCode}\\";

        public ModelAssetEditViewModel(ModelAssetInfo source, bool isNew)
        {
            _isNew = isNew;
            Asset = source == null
                ? new ModelAssetInfo()
                : JsonConvert.DeserializeObject<ModelAssetInfo>(JsonConvert.SerializeObject(source));
            WindowTitle = isNew ? "注册模型资产" : $"编辑模型资产 · {Asset.AssetCode}";

            _selectedKind = ModelKindOptions.FirstOrDefault(o => Equals(o.Value, Asset.ModelKind)) ?? ModelKindOptions[0];
            _selectedFramework = FrameworkOptions.FirstOrDefault(o => Equals(o.Value, Asset.Framework)) ?? FrameworkOptions[0];
            _selectedStatus = StatusOptions.FirstOrDefault(o => Equals(o.Value, Asset.Status)) ?? StatusOptions[0];
            ClassNamesText = Asset.ClassNames == null ? "" : string.Join(", ", Asset.ClassNames);
            ThresholdText = Asset.DefaultThreshold.HasValue ? Asset.DefaultThreshold.Value.ToString("0.###") : "";
        }

        public void PickModelFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择模型文件（仅登记引用；入库请复制到提示目录）",
                Filter = "ONNX 模型|*.onnx|HALCON DLTool|*.hdl|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                var fi = new FileInfo(dlg.FileName);
                FileNameText = fi.Name;
                Asset.SizeKb = fi.Length / 1024;
                if (string.IsNullOrWhiteSpace(NameText))
                    NameText = Path.GetFileNameWithoutExtension(fi.Name);
                OnPropertyChanged(nameof(Asset));
            }
        }

        public bool Save()
        {
            if (string.IsNullOrWhiteSpace(Asset.AssetCode) || string.IsNullOrWhiteSpace(Asset.DisplayName))
            {
                System.Windows.MessageBox.Show("请填写资产代码与名称。", "校验提示");
                return false;
            }
            Asset.ClassNames = string.IsNullOrWhiteSpace(ClassNamesText)
                ? new System.Collections.Generic.List<string>()
                : ClassNamesText.Split(new[] { ',', '，', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            Asset.DefaultThreshold = double.TryParse(ThresholdText, out var th) ? th : (double?)null;
            _registry.Save(Asset);
            return true;
        }
    }
}
