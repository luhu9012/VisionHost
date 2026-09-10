using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Nodes.Common;
using System;
using System.IO;
using System.Windows.Input;
using Newtonsoft.Json;

namespace Grayson.Vision.Nodes.All.Identification.HalconDlInference
{
    /// <summary>
    /// HALCON 原生 DL 任务类型（UI 展示用枚举；值与 Contracts.Ai.InferenceTaskType /
    /// HalconWrapper.HalconDlTaskKind 保持同值，Executor 按数值映射）。
    /// 当前 .hdl 通道只落地：Classification=0（分类） / Segmentation=2（分割）。
    /// </summary>
    public enum HalconDlTaskType
    {
        ObjectDetection = 1,  // 预留
        Classification = 0,   // 图像分类（DLTool 导出，如镁片 ok/ng）
        Segmentation = 2,     // 语义分割（DLTool 导出，如药片破裂/脏污）
        AnomalyDetection = 3  // 预留
    }

    /// <summary>
    /// HALCON 原生 DL 推理节点参数（HTH《Demo_深度学习》翻译落地）。
    /// 【使用方法】
    ///   1. HdlModelPath 填 DLTool 导出的 .hdl 模型（相对运行目录或绝对路径）——右侧「浏览…」可文件选择；
    ///   2. PreprocessParamPath 填配套 .hdict 预处理参数（可空——模型 normalization=none 时仅需转 real）；
    ///   3. TaskType 按模型选：分割选 Segmentation（需填类别 ID/最小面积），分类选 Classification；
    ///   4. 分割任务：DefectClassIdsText 填缺陷类别 ID（示例：1,2），Resize 宽高按训练尺寸（示例 632×300）；
    ///   5. 分类任务：摘要为 "{Top1类名}: {置信度}"，任务模板 VerdictRule 可用 OkClassName/NgClassName 判定。
    /// 输出端口与 DlInference 一致：DetectionResults(摘要文本) + DefectRegion(缺陷 Region)，独立视觉引擎可直接消费。
    /// </summary>
    public class HalconDlInferenceParam : ParamBase
    {
        private HalconDlTaskType _taskType = HalconDlTaskType.Segmentation;
        /// <summary>AI 任务类型（分类/分割已落地）</summary>
        public HalconDlTaskType TaskType
        {
            get => _taskType;
            set => Set(ref _taskType, value);
        }

        private string _hdlModelPath = @"Config\Models\MDL-SEG-001\model_opt.hdl";
        /// <summary>HALCON .hdl 模型路径（相对运行目录，如 Config\Models\MDL-SEG-001\model_opt.hdl）</summary>
        public string HdlModelPath
        {
            get => _hdlModelPath;
            set
            {
                if (Set(ref _hdlModelPath, value))
                {
                    OnPropertyChanged(nameof(HdlModelExists));
                    OnPropertyChanged(nameof(HdlModelStateText));
                }
            }
        }

        private string _preprocessParamPath = @"Config\Models\MDL-SEG-001\model_preprocess_params.hdict";
        /// <summary>HALCON .hdict 预处理参数路径（可空；normalization=none 时推理等价=缩放+转 real）</summary>
        public string PreprocessParamPath
        {
            get => _preprocessParamPath;
            set
            {
                if (Set(ref _preprocessParamPath, value))
                {
                    OnPropertyChanged(nameof(HdictIsEmpty));
                    OnPropertyChanged(nameof(HdictModelExists));
                    OnPropertyChanged(nameof(HdictWarnMissing));
                    OnPropertyChanged(nameof(HdictModelStateText));
                }
            }
        }

        private bool _resizeEnabled = true;
        /// <summary>推理前是否缩放到训练尺寸（分割模型必须与训练一致；分类可关）</summary>
        public bool ResizeEnabled
        {
            get => _resizeEnabled;
            set => Set(ref _resizeEnabled, value);
        }

        private int _resizeWidth = 632;
        /// <summary>训练宽度（示例药片 632）</summary>
        public int ResizeWidth
        {
            get => _resizeWidth;
            set => Set(ref _resizeWidth, value);
        }

        private int _resizeHeight = 300;
        /// <summary>训练高度（示例药片 300）</summary>
        public int ResizeHeight
        {
            get => _resizeHeight;
            set => Set(ref _resizeHeight, value);
        }

        private string _resizeInterpolation = "nearest_neighbor";
        /// <summary>缩放插值（保持边缘锐利用 nearest_neighbor）</summary>
        public string ResizeInterpolation
        {
            get => _resizeInterpolation;
            set => Set(ref _resizeInterpolation, value);
        }

        private string _defectClassIdsText = "1,2";
        /// <summary>缺陷/目标类别 ID 列表（逗号分隔；示例药片：1=破裂,2=脏污；0=good 背景）</summary>
        public string DefectClassIdsText
        {
            get => _defectClassIdsText;
            set => Set(ref _defectClassIdsText, value);
        }

        private double _minDefectArea = 100;
        /// <summary>最小缺陷面积阈值（px；小于该值忽略，翻译自示例）</summary>
        public double MinDefectArea
        {
            get => _minDefectArea;
            set => Set(ref _minDefectArea, value);
        }

        /// <summary>HALCON DL 节点带图像预览</summary>
        public override bool SupportsPreview => true;

        // ===== 模型路径选择（视图右侧「浏览…」按钮；不参与配方 JSON 序列化） =====

        [JsonIgnore]
        public ICommand BrowseHdlCommand { get; }

        [JsonIgnore]
        public ICommand BrowseHdictCommand { get; }

        /// <summary>.hdl 是否存在于运行目录解析路径（用于视图状态提示）</summary>
        [JsonIgnore]
        public bool HdlModelExists => !string.IsNullOrWhiteSpace(HdlModelPath) && File.Exists(ResolveRunPath(HdlModelPath));

        /// <summary>.hdict 是否留空（可空项：留空=推理仅缩放+转 real）</summary>
        [JsonIgnore]
        public bool HdictIsEmpty => string.IsNullOrWhiteSpace(PreprocessParamPath);

        /// <summary>.hdict 是否已填且文件存在（绿色✓）</summary>
        [JsonIgnore]
        public bool HdictModelExists =>
            !HdictIsEmpty && File.Exists(ResolveRunPath(PreprocessParamPath));

        /// <summary>.hdict 已填但文件缺失（琥珀⚠；留空不算缺失）</summary>
        [JsonIgnore]
        public bool HdictWarnMissing =>
            !HdictIsEmpty && !File.Exists(ResolveRunPath(PreprocessParamPath));

        /// <summary>.hdl 状态文案（视图直接展示）</summary>
        [JsonIgnore]
        public string HdlModelStateText =>
            HdlModelExists
                ? "✓ 文件存在"
                : "⚠ 未找到——运行时会按该路径解析并报错，请用右侧「浏览…」重选";

        /// <summary>.hdict 状态文案（视图直接展示）</summary>
        [JsonIgnore]
        public string HdictModelStateText =>
            HdictIsEmpty
                ? "· 可空：normalization=none 的模型可不配（推理=缩放+转 real）"
                : HdictModelExists
                    ? "✓ 文件存在"
                    : "⚠ 未找到——请修正路径，或留空跳过预处理参数";

        public HalconDlInferenceParam()
        {
            BrowseHdlCommand = new RelayCommand(() => BrowseModelFile(true));
            BrowseHdictCommand = new RelayCommand(() => BrowseModelFile(false));
        }

        private void BrowseModelFile(bool isHdl)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = isHdl ? "选择 HALCON DL 模型 (.hdl)" : "选择预处理参数 (.hdict, 可空)",
                Filter = isHdl
                    ? "HALCON DL 模型 (*.hdl)|*.hdl|所有文件 (*.*)|*.*"
                    : "HALCON 参数字典 (*.hdict)|*.hdict|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            string current = isHdl ? HdlModelPath : PreprocessParamPath;
            string resolved = ResolveRunPath(current);
            if (!string.IsNullOrWhiteSpace(current) && File.Exists(resolved))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(resolved);
                dialog.FileName = Path.GetFileName(resolved);
            }

            if (dialog.ShowDialog() != true) return;

            // 选中文件在运行目录内 → 存相对路径（配方 JSON 可移植）；
            // 否则存绝对路径。两者 HalconDlNativeTool.ResolvePath 都能正确解析。
            string picked = dialog.FileName;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (picked.StartsWith(baseDir, System.StringComparison.OrdinalIgnoreCase))
            {
                picked = picked.Substring(baseDir.Length).TrimStart('\\', '/');
            }

            if (isHdl) HdlModelPath = picked;
            else PreprocessParamPath = picked;
        }

        /// <summary>相对路径按运行目录解析为绝对路径（供存在性检查 / 对话框初始目录用）</summary>
        private static string ResolveRunPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }
    }
}
