//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TaskTemplateModels.cs
// 说 明: 任务模板库（T 层 UI 事实表 v1 落地模型）——
//        把「执行方案(Engine)/配方(Recipe)/纯视觉链」抽象为面向任务族的可复用"任务模板"：
//           · 任务类型族 TaskKind：引导定位 / 深度学习推理 / 外观测量（三类经典任务，T2/T3 不依赖工位硬件）
//           · 运行载体 TaskDependencyMode：工位联动(StationBound) / 独立纯软件(Standalone)
//           · 图像源 TaskImageSource：相机源(需工位硬件) / 本地文件夹 / 本地单图(纯软件任务的离线输入)
//           · 资产引用：形状模板 / 标定档案 / 深度学习模型 / 配方 / 示教说明
//           · 输出契约：引导坐标 / 检测框·掩膜·类别 / 测量值 + OK/NG 判据
//        落盘：Config\TaskLibrary\{TemplateCode}.json（与 任务模板族v1_事实表_2026-09-06.json 同源，
//              事实表=族级骨架，本模型=工位可引用的模板实例）
//        消费方（后续 stage9-2/9-3）：工位装配向导、工位监视【启动】链路（EngineKey→ProcessKey / 独立运行器）。
//===================================================================================
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.TaskLibrary.Models
{
    /// <summary>任务类型族 —— 三大经典视觉任务（行业口径：引导/检测识别/测量）。</summary>
    public enum TaskKind
    {
        /// <summary>引导定位（视觉引导取放/对位）：依赖相机+标定+运动节拍引擎（执行方案）</summary>
        PositioningGuidance = 0,

        /// <summary>深度学习推理（检测/分割/分类/异常）：本地图片源 + 训练模型即可，无需相机/运控/PLC</summary>
        DeepLearningInference = 1,

        /// <summary>外观测量（尺寸/几何/缺陷形态）：本地图片源 + 算法算子族，纯软件可运行</summary>
        AppearanceMeasurement = 2
    }

    /// <summary>运行载体（任务对"工位硬件"的依赖声明）。</summary>
    public enum TaskDependencyMode
    {
        /// <summary>工位联动：需要工位提供相机/运动/IO（引导定位典型）；由工位监视【启动】驱动</summary>
        StationBound = 0,

        /// <summary>独立纯软件：只需本地图像源+算法/模型资产，无硬件依赖，可离线批量运行</summary>
        Standalone = 1
    }

    /// <summary>模板生命周期状态。</summary>
    public enum TaskTemplateStatus
    {
        Draft = 0,
        Published = 1,
        Retired = 2
    }

    /// <summary>图像源类型。</summary>
    public enum TaskImageSourceKind
    {
        /// <summary>相机源（占用工位相机槽，工位联动时使用）</summary>
        CameraSource = 0,

        /// <summary>本地文件夹（轮询取图，独立纯软件任务默认）</summary>
        LocalFolder = 1,

        /// <summary>本地单张图片（试运行/离线验证）</summary>
        LocalFile = 2
    }

    /// <summary>任务模板引用的视觉资产种类。</summary>
    public enum TaskAssetKind
    {
        ShapeTemplate = 0,        // 形状/相关模板（TemplateManager 资产）
        CalibrationProfile = 1,   // 标定档案（CalibrationProfileName）
        DlModel = 2,              // 深度学习模型（模型仓库 ModelAssetCode）
        Recipe = 3,               // 配方（视觉链 JSON）
        TeachReference = 4,       // 示教参考（操作/文档）
        Other = 5
    }

    /// <summary>模型仓库 —— 深度学习模型种类。</summary>
    public enum DlModelKind
    {
        Detection = 0,          // 目标检测（框）
        Segmentation = 1,       // 语义/实例分割（掩膜）
        Classification = 2,     // 分类（类别+置信度）
        AnomalyDetection = 3    // 异常检测（OK/NG+分数）
    }

    /// <summary>模型框架（推理链路 Plugins.Inference.* 已支持 ONNX；DLTool .hdl 端到端待 B6 实操）。</summary>
    public enum DlFrameworkKind
    {
        OnnxRuntime = 0,
        HalconDlTool = 1,
        Other = 2
    }

    /// <summary>模型资产状态。</summary>
    public enum ModelAssetStatus
    {
        Ready = 0,       // 就绪（可用）
        Verifying = 1,   // 验证中（训练/上架前校验）
        Disabled = 2     // 停用（不允许新模板引用）
    }

    /// <summary>图像源配置（模板级；相机源时 CameraSlotKey 指向工位相机槽）。</summary>
    public class TaskTemplateImageSource
    {
        public TaskImageSourceKind Kind { get; set; } = TaskImageSourceKind.LocalFolder;

        /// <summary>相机槽键（Kind=CameraSource 时；对应 StationProfile.CameraSlots.SlotKey / 逻辑设备名）</summary>
        public string CameraSlotKey { get; set; }

        /// <summary>本地文件夹（Kind=LocalFolder 时）：离线批量执行图像目录</summary>
        public string LocalFolderPath { get; set; }

        /// <summary>文件通配（如 *.bmp;*.png;*.jpg，空=全部受支持格式）</summary>
        public string LocalFilePattern { get; set; }

        /// <summary>循环取图间隔 ms（独立任务无人值守节拍；0=单遍）</summary>
        public int RepeatDelayMs { get; set; }
    }

    /// <summary>模板→资产的引用（通用引用表；专用字段另有 ShapeTemplateName/CalibrationProfileName/ModelAssetCode）。</summary>
    public class TaskTemplateAssetRef
    {
        public TaskAssetKind Kind { get; set; } = TaskAssetKind.Other;
        public string Name { get; set; }
        /// <summary>资产在各自库中的键（模板名/标定档案名/模型 AssetCode/配方 Code）</summary>
        public string RefCode { get; set; }
        public string Note { get; set; }
    }

    /// <summary>外观测量项（TaskKind=AppearanceMeasurement 时按行配置；输出数值+OK/NG）。</summary>
    public class MeasurementSpecItem
    {
        public string Name { get; set; }          // 测量项名称（如 宽度/圆径/两孔距）
        public string ToolKind { get; set; }      // 工具族：Caliper 卡尺 / FitLine 找线 / FitCircle 找圆 / Blob 面积 / Intensity 灰度
        public string RegionRef { get; set; }     // 测量区域/特征引用（自由文本，编辑配 Recipe 链时落节点）
        public double? NominalMm { get; set; }    // 标称值(mm)
        public double? ToleranceMm { get; set; }  // 公差 ±(mm)
        public bool Enabled { get; set; } = true;
        public string Note { get; set; }
    }

    /// <summary>工位绑定缓存（由 UI 扫描工位配置反填；非双写源，源=StationConfigModel.TaskTemplateCode）。</summary>
    public class TaskStationBinding
    {
        public string StationId { get; set; }
        public string StationCode { get; set; }
        public string StationName { get; set; }
        public string LineName { get; set; }
        /// <summary>该工位当前 ProcessKey（引导定位类模板 → 运行时引擎；空=未装配执行方案）</summary>
        public string BoundProcessKey { get; set; }
    }

    /// <summary>
    /// 任务级判定规则（判据随任务模板走，stage9-2 起由独立视觉引擎在运行时叠加）：
    /// 模板 > 工位引擎配置（ProcessConfigJson 补丁）> 引擎代码默认值。
    /// 空/未设置字段不参与叠加（保持下层值不变）。
    /// </summary>
    public class TaskVerdictRule
    {
        /// <summary>摘要含该词 → 直接 OK（如 "未检出"）</summary>
        public string OkKeyword { get; set; }

        /// <summary>摘要含该词 → NG（如 "NG"）</summary>
        public string NgKeyword { get; set; }

        /// <summary>检测/分割/异常类任务：有检出即 NG（缺陷/异常语义；null=不叠加）</summary>
        public bool? NgOnDetect { get; set; }

        /// <summary>摘要为空/判据未命中时按 OK 放行（null=不叠加，引擎默认 NG）</summary>
        public bool? EmptySummaryAsOk { get; set; }

        /// <summary>分类 Top-1 摘要以 "{OkClassName}:" 开头 → OK（如镁片分类模型 "ok"）</summary>
        public string OkClassName { get; set; }

        /// <summary>分类 Top-1 摘要以 "{NgClassName}:" 开头 → NG（如 "ng"）</summary>
        public string NgClassName { get; set; }

        public string Note { get; set; }
    }

    /// <summary>
    /// 任务模板定义（库主对象，一模板=一个任务族的"半成品"实例，可被多个工位引用）。
    /// 序列化友好：全部公开属性，无 UI 依赖，Newtonsoft 默认约定。
    /// </summary>
    public class TaskTemplateInfo
    {
        /// <summary>模板代码（唯一；命名建议 TPL-{族前缀}-{流水}，如 TPL-GD-001 / TPL-DL-002 / TPL-AM-003）</summary>
        public string TemplateCode { get; set; }

        /// <summary>模板名称（如 "通用旋转取放-单吸嘴" / "PCB 焊点缺陷检测(YOLO)" / "连接器引脚间距测量"）</summary>
        public string DisplayName { get; set; }

        /// <summary>任务类型族</summary>
        public TaskKind Kind { get; set; } = TaskKind.PositioningGuidance;

        /// <summary>运行载体（工位联动 / 独立纯软件）</summary>
        public TaskDependencyMode DependencyMode { get; set; } = TaskDependencyMode.StationBound;

        public TaskTemplateStatus Status { get; set; } = TaskTemplateStatus.Draft;

        /// <summary>模板版本（如 v1 / v1.2；发布后修改应升版并保留事实）</summary>
        public string Version { get; set; } = "v1";

        /// <summary>一句话摘要（列表副标题）</summary>
        public string Summary { get; set; }

        /// <summary>适用机型/产品/场景说明</summary>
        public string ApplicableMachines { get; set; }

        /// <summary>图像源</summary>
        public TaskTemplateImageSource ImageSource { get; set; } = new TaskTemplateImageSource();

        // —— 引导定位（PositioningGuidance）专用 ——
        /// <summary>执行方案引擎键（映射工位 ProcessKey；与 StationProcessCatalog 对账，如 VisionPickPlace/MahjongPick）</summary>
        public string EngineKey { get; set; }

        /// <summary>视觉链配方（可选；引用 RecipeManage 的 Recipe Code，发布时骨架快照进配方）</summary>
        public string BoundRecipeId { get; set; }
        public string BoundRecipeName { get; set; }

        /// <summary>形状模板资产（引导定位主识别器；TemplateManager 的 TemplateName）</summary>
        public string ShapeTemplateName { get; set; }

        /// <summary>标定档案引用（工位级/配方级标定发布域，CalibrationProfileName）</summary>
        public string CalibrationProfileName { get; set; }

        /// <summary>示教说明/挂载面板说明（对应工位监视扩展示教面板的默认参数语境）</summary>
        public string TeachNote { get; set; }

        // —— 深度学习推理（DeepLearningInference）专用 ——
        /// <summary>关联模型资产（模型仓库 AssetCode；该任务所需唯一模型资产，也可再入 AssetRefs）</summary>
        public string ModelAssetCode { get; set; }

        /// <summary>置信度/分数阈值（0~1；低于判 NG）</summary>
        public double? ConfidenceThreshold { get; set; }

        // —— 外观测量（AppearanceMeasurement）专用 ——
        /// <summary>测量项清单（尺寸/几何/面积）</summary>
        public List<MeasurementSpecItem> MeasurementSpecs { get; set; } = new List<MeasurementSpecItem>();

        /// <summary>
        /// 像素当量（mm/px）—— 外观测量族专用，将 FitCircle/FitLine 输出的像素测量值换算为 mm。
        /// null 时引擎读测量节点输出原始像素值并按像素口径判 OK/NG（MeasurementSpecs.NominalMm/ToleranceMm 也按像素口径填）。
        /// 真机产线应通过标定件实测当量填入；课堂/Demo 可设为演示用假设值并在 Remark 标注。
        /// </summary>
        public double? PixelPerMm { get; set; }

        // —— 输出契约 / 判定 ——
        /// <summary>输出契约（人读+结构化提示；引导=世界坐标 X/Y/U；DL=目标框/掩膜/类别置信度；测量=数值+OK/NG）</summary>
        public string OutputContractText { get; set; }

        /// <summary>结果是否联动 IO/PLC 判定（引导/检测工位联动时用；独立任务输出 CSV/日志即可）</summary>
        public bool VerdictToIo { get; set; }

        /// <summary>任务级判定规则（独立视觉任务引擎按此判 OK/NG；未配置时走工位引擎配置/默认值）</summary>
        public TaskVerdictRule VerdictRule { get; set; }

        // —— 通用资产引用（附加项；专用字段未覆盖的可放这里）——
        public List<TaskTemplateAssetRef> AssetRefs { get; set; } = new List<TaskTemplateAssetRef>();

        /// <summary>工位绑定缓存（UI 维护；删除模板时若仍有绑定应提示先解绑）</summary>
        public List<TaskStationBinding> Bindings { get; set; } = new List<TaskStationBinding>();

        // —— 审计 ——
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime UpdatedTime { get; set; } = DateTime.Now;
        public string Remark { get; set; }

        /// <summary>JSON 落盘文件名（Config\TaskLibrary 下）</summary>
        [Newtonsoft.Json.JsonIgnore]
        public string FileName => (string.IsNullOrWhiteSpace(TemplateCode) ? Guid.NewGuid().ToString("N") : TemplateCode) + ".json";
    }

    /// <summary>
    /// 深度学习模型资产（模型仓库条目）。
    /// 资产本体文件放 Config\Models\{AssetCode}\{FileName}（或引用外部模型目录），本模型只管理元数据+引用计数。
    /// </summary>
    public class ModelAssetInfo
    {
        /// <summary>资产代码（唯一；命名建议 MDL-{族}-{流水}，如 MDL-DET-001）</summary>
        public string AssetCode { get; set; }

        /// <summary>模型名称（如 "PCB缺陷YOLOv8s"）</summary>
        public string DisplayName { get; set; }

        /// <summary>模型种类</summary>
        public DlModelKind ModelKind { get; set; } = DlModelKind.Detection;

        /// <summary>框架</summary>
        public DlFrameworkKind Framework { get; set; } = DlFrameworkKind.OnnxRuntime;

        /// <summary>适用任务族（一般=DeepLearningInference；外观测量做 DL 缺陷时也可用）</summary>
        public TaskKind ApplicableTaskKind { get; set; } = TaskKind.DeepLearningInference;

        /// <summary>模型文件相对名（Config\Models\{AssetCode}\ 下）</summary>
        public string FileName { get; set; }

        /// <summary>附属文件相对名（HALCON DLTool 需要 .hdl + .hdict 两个文件，.hdict 放这里）</summary>
        public List<string> AuxFileNames { get; set; } = new List<string>();

        /// <summary>输入规格描述（如 "1x3x640x640 RGB"）</summary>
        public string InputSpecText { get; set; }

        /// <summary>类别表（检测/分割/分类的类别名；异常检测可为 空/Good-NG）</summary>
        public List<string> ClassNames { get; set; } = new List<string>();

        /// <summary>关键指标文本（如 "mAP50 0.923 / 分类 Acc 0.995"；训练端产出回填）</summary>
        public string MetricsText { get; set; }

        /// <summary>置信度阈值默认建议（0~1）</summary>
        public double? DefaultThreshold { get; set; }

        /// <summary>模型文件大小(KB，入库时自动填写)</summary>
        public long SizeKb { get; set; }

        public ModelAssetStatus Status { get; set; } = ModelAssetStatus.Ready;

        /// <summary>来源/训练说明（DLTool 导出 / 训练集版本等）</summary>
        public string Remark { get; set; }

        /// <summary>模型文件是否在库（UI 扫描 Config\Models\{AssetCode}\ 后回填，不入 JSON）</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool IsFileInLibrary { get; set; }

        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime UpdatedTime { get; set; } = DateTime.Now;
    }
}
