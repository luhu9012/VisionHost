//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationWizardViewModel.cs
// 说 明: 新建工位向导 ViewModel（v1.1 §0.2 问卷 + §0.5 行业模板 + §0.3 实时推导预览）。
//        · 单窗问答式（可跳答=字段留空即"待确认"）；行业模板选中即预填问卷；
//        · 任一字段变化 → StationProposalEngine 实时重算派生建议（右侧预览）；
//        · 收尾两种：💾 存草稿（只落 StationProfile Draft）/ 🚀 创建工位（父 VM 落真实工位 + Active 档案）。
//        · 结果经 ResultProfile + ExitAsDraft 暴露，由 StationManageViewModel 消费。
//===================================================================================
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class StationWizardViewModel : ViewModelBase
    {
        // ==================== 构造/上下文 ====================
        private readonly string _lineId;
        private readonly string _lineName;

        public StationWizardViewModel(string lineId, string lineName, string suggestedCode, string suggestedName)
        {
            _lineId = lineId ?? string.Empty;
            _lineName = lineName ?? string.Empty;
            _profile = new StationProfile
            {
                LineId = _lineId,
                LineName = _lineName,
                StationCode = suggestedCode ?? string.Empty,
                StationName = suggestedName ?? string.Empty
            };
            _repo = new StationProfileRepository();
            _templateOptions = BuildTemplateOptions();
            _selectedTemplate = _templateOptions[0];
            SyncSlotCollection();
            ReloadDraftOptions();
        }

        /// <summary>
        /// 编辑模式构造：把一份已有 StationProfile（Active 档案 / 父 VM 构造的"补档空壳"）载入问卷，
        /// 收尾按钮变【保存档案修改】（只回写档案，不建真实工位、不降级草稿）。
        /// </summary>
        public StationWizardViewModel(StationProfile editProfile)
            : this(editProfile?.LineId, editProfile?.LineName,
                   editProfile?.StationCode ?? string.Empty, editProfile?.StationName ?? string.Empty)
        {
            IsEditMode = editProfile != null;
            if (editProfile != null)
            {
                LoadExistingProfile(editProfile);
            }
        }

        // ==================== 编辑模式（2026-09-05：工位工作台 📋 需求档案入口） ====================
        /// <summary>true=编辑已有档案（锁定代码/名称、收尾=保存回写）；false=新建（存草稿/创建工位）</summary>
        public bool IsEditMode { get; private set; }

        public string WindowTitle => IsEditMode
            ? "📋 编辑视觉工位需求档案 —— 改需求，下游建议（旅程/标定/模板）实时联动"
            : "🛠 新建视觉工位 —— 回答『做什么检测』，系统推导该配什么";

        public string WindowSubtitle => IsEditMode
            ? "修改问卷后【保存档案修改】：装配旅程 / 标定中心 / 模板归属等下游建议将按新档案实时更新；不改动该工位已生效的运行时配置。"
            : "可跳答（留空=待确认，工作台显示『待补』）；选行业模板可预填；右侧推导建议随填写实时刷新";

        public string SchemeHeaderText => IsEditMode
            ? "方案标识（编辑模式：代码/名称锁定，需在工位工作台站头修改）"
            : "方案标识（激活后即工位代码/名称）";

        public string CreateButtonText => IsEditMode ? "💾 保存档案修改" : "🚀 创建工位";

        // ==================== 行业模板 ====================
        private readonly ObservableCollection<IndustryTemplateDef> _templateOptions;

        /// <summary>模板卡片选项（含"自由问卷"首项）—— 集合只建一次，避免绑定重求值时重置选中项</summary>
        public ObservableCollection<IndustryTemplateDef> TemplateOptions => _templateOptions;

        private static ObservableCollection<IndustryTemplateDef> BuildTemplateOptions()
        {
            var list = new List<IndustryTemplateDef> { new IndustryTemplateDef { Code = "", Icon = "📝", Name = "自由问卷（不套模板）", Description = "从零按需填写，系统逐字段推导建议" } };
            list.AddRange(StationProposalEngine.IndustryTemplates);
            return new ObservableCollection<IndustryTemplateDef>(list);
        }

        private IndustryTemplateDef _selectedTemplate;
        /// <summary>当前选中的行业模板（切换即预填问卷并刷新推导）</summary>
        public IndustryTemplateDef SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                if (ReferenceEquals(_selectedTemplate, value)) return;
                _selectedTemplate = value;
                OnPropertyChanged(nameof(SelectedTemplate));
                if (value != null && !string.IsNullOrEmpty(value.Code))
                {
                    ApplyTemplate(value);
                }
                else
                {
                    RefreshDerivedView();
                }
            }
        }

        private void ApplyTemplate(IndustryTemplateDef t)
        {
            TaskType = t.TaskType;
            CameraMount = t.CameraMount;
            ShootMode = t.ShootMode;
            Lighting = t.Lighting;
            VerdictOutput = t.VerdictOutput;
            _profile.IndustryTemplateCode = t.Code;
            OnPropertyChanged(nameof(IndustryTemplateBadge));
            RefreshDerivedView();
        }

        /// <summary>当前行业模板角标文案（创建日志用）</summary>
        public string IndustryTemplateBadge => string.IsNullOrEmpty(_profile.IndustryTemplateCode) ? "自由问卷" : SelectedTemplate?.Name ?? _profile.IndustryTemplateCode;

        // ==================== 结构字段（方案标识） ====================
        public string StationCode
        {
            get => _profile.StationCode;
            set { if (_profile.StationCode != value) { _profile.StationCode = value ?? string.Empty; OnPropertyChanged(nameof(StationCode)); } }
        }
        public string StationName
        {
            get => _profile.StationName;
            set { if (_profile.StationName != value) { _profile.StationName = value ?? string.Empty; OnPropertyChanged(nameof(StationName)); } }
        }

        // ==================== 问卷字段（平铺属性→Requirement；变更即重算推导）====================
        private readonly StationProfile _profile;
        private readonly StationProfileRepository _repo;

        private void SetReq(string field, string value, string prop)
        {
            _profile.Requirement = _profile.Requirement ?? new StationProfileRequirement();
            var p = typeof(StationProfileRequirement).GetProperty(field);
            p?.SetValue(_profile.Requirement, value);
            OnPropertyChanged(prop);
            RefreshDerivedView();
        }

        // ① 视觉任务
        public string TaskType { get => _profile.Requirement?.TaskType; set => SetReq("TaskType", value, nameof(TaskType)); }
        // ② 成像
        public string CameraMount { get => _profile.Requirement?.CameraMount; set => SetReq("CameraMount", value, nameof(CameraMount)); }
        public string ShootMode { get => _profile.Requirement?.ShootMode; set => SetReq("ShootMode", value, nameof(ShootMode)); }
        public string AxisToSurface { get => _profile.Requirement?.AxisToSurface; set => SetReq("AxisToSurface", value, nameof(AxisToSurface)); }
        public string Lighting { get => _profile.Requirement?.Lighting; set => SetReq("Lighting", value, nameof(Lighting)); }
        public string FovMm { get => _profile.Requirement?.FovMm; set => SetReq("FovMm", value, nameof(FovMm)); }
        public string WorkDistanceMm { get => _profile.Requirement?.WorkDistanceMm; set => SetReq("WorkDistanceMm", value, nameof(WorkDistanceMm)); }
        public string MinFeatureMm { get => _profile.Requirement?.MinFeatureMm; set => SetReq("MinFeatureMm", value, nameof(MinFeatureMm)); }
        // ③ 工具
        public string ToolHeadCount { get => _profile.Requirement?.ToolHeadCount; set => SetReq("ToolHeadCount", value, nameof(ToolHeadCount)); }

        private bool? _concentric;
        /// <summary>是否与旋转轴同心（三态：null=待定/偏心未知）</summary>
        public bool? ConcentricWithRotationAxis
        {
            get
            {
                _concentric = _profile.Requirement?.ConcentricWithRotationAxis;
                return _concentric;
            }
            set
            {
                _concentric = value;
                _profile.Requirement = _profile.Requirement ?? new StationProfileRequirement();
                _profile.Requirement.ConcentricWithRotationAxis = value;
                OnPropertyChanged(nameof(ConcentricWithRotationAxis));
                OnPropertyChanged(nameof(ConcentricText));
                RefreshDerivedView();
            }
        }

        /// <summary>同心三态字符串视图（空串=待确认/未知）</summary>
        public string ConcentricText
        {
            get => ConcentricWithRotationAxis == true ? "是" : ConcentricWithRotationAxis == false ? "否" : string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value)) ConcentricWithRotationAxis = null;
                else if (value == "是") ConcentricWithRotationAxis = true;
                else ConcentricWithRotationAxis = false;
            }
        }

        public string[] ConcentricOptions => new[] { string.Empty, "是", "否" };

        private bool? _usesCalibrationBoard;
        /// <summary>标定板可用性（三态：null=待定/未答，true=有板走棋盘格内参，false=无板走九点走位）</summary>
        public bool? UsesCalibrationBoard
        {
            get
            {
                _usesCalibrationBoard = _profile.Requirement?.UsesCalibrationBoard;
                return _usesCalibrationBoard;
            }
            set
            {
                _usesCalibrationBoard = value;
                _profile.Requirement = _profile.Requirement ?? new StationProfileRequirement();
                _profile.Requirement.UsesCalibrationBoard = value;
                OnPropertyChanged(nameof(UsesCalibrationBoard));
                OnPropertyChanged(nameof(UsesCalibrationBoardText));
                RefreshDerivedView();
            }
        }

        /// <summary>标定板可用性三态字符串视图（空串=待确认/未知）</summary>
        public string UsesCalibrationBoardText
        {
            get => UsesCalibrationBoard == true ? "有标定板" : UsesCalibrationBoard == false ? "无标定板" : string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value)) UsesCalibrationBoard = null;
                else if (value == "有标定板") UsesCalibrationBoard = true;
                else UsesCalibrationBoard = false;
            }
        }

        public string[] UsesCalibrationBoardOptions => new[] { string.Empty, "有标定板", "无标定板" };

        public string AngleNeed { get => _profile.Requirement?.AngleNeed; set => SetReq("AngleNeed", value, nameof(AngleNeed)); }
        // ④ 判定与节拍
        public string VerdictOutput { get => _profile.Requirement?.VerdictOutput; set => SetReq("VerdictOutput", value, nameof(VerdictOutput)); }
        public string CyclePerMin { get => _profile.Requirement?.CyclePerMin; set => SetReq("CyclePerMin", value, nameof(CyclePerMin)); }

        // ⑤ 执行机构与相机拓扑（2026-09-05 增补：多相机/机器人 → 槽级标定建议与轴约定提示）
        public string ActuatorKind { get => _profile.Requirement?.ActuatorKind; set => SetReq("ActuatorKind", value, nameof(ActuatorKind)); }
        public string[] ActuatorKindOptions => WithPending(StationProposalEngine.ActuatorKindOptions);

        /// <summary>相机槽视图集合（与 Requirement.CameraSlots 同元素引用；空=单相机走 ② 成像字段）</summary>
        public ObservableCollection<VisionSlotInfo> CameraSlotsUi { get; } = new ObservableCollection<VisionSlotInfo>();

        /// <summary>是否已配置相机槽（⑤ 区空态引导可见性）</summary>
        public bool HasCameraSlots => CameraSlotsUi.Count > 0;

        /// <summary>槽行内下拉/文本改动后由 code-behind 统一调用（重算推导）</summary>
        public void NotifySlotEdited() => RefreshDerivedView();

        /// <summary>＋添加相机槽：新槽按 ② 成像字段预填（承接单相机语义，用户逐槽微调）</summary>
        public void AddCameraSlot()
        {
            _profile.Requirement = _profile.Requirement ?? new StationProfileRequirement();
            if (_profile.Requirement.CameraSlots == null) _profile.Requirement.CameraSlots = new List<VisionSlotInfo>();

            var mount = _profile.Requirement.CameraMount;
            bool moving = string.Equals(mount, "眼在手上");
            var slot = new VisionSlotInfo
            {
                SlotKey = NextSlotKey(),
                InstallKind = MapMountToInstallKind(mount),
                ShootMode = _profile.Requirement.ShootMode,
                AxisToSurface = _profile.Requirement.AxisToSurface,
                AxisFollows = moving ? "跟随XYZU" : "固定（不随动）",
                Purpose = DefaultPurposeFromTask(_profile.Requirement.TaskType),
                UsesCalibrationBoard = _profile.Requirement.UsesCalibrationBoard
            };
            _profile.Requirement.CameraSlots.Add(slot);
            CameraSlotsUi.Add(slot);
            OnPropertyChanged(nameof(HasCameraSlots));
            RefreshDerivedView();
        }

        /// <summary>移除指定相机槽</summary>
        public void RemoveCameraSlot(VisionSlotInfo slot)
        {
            if (slot == null) return;
            _profile.Requirement = _profile.Requirement ?? new StationProfileRequirement();
            _profile.Requirement.CameraSlots?.Remove(slot);
            CameraSlotsUi.Remove(slot);
            OnPropertyChanged(nameof(HasCameraSlots));
            RefreshDerivedView();
        }

        /// <summary>把 Requirement.CameraSlots 同步进视图集合（构造/载入草稿时调用；同元素引用，双向一致）</summary>
        private void SyncSlotCollection()
        {
            CameraSlotsUi.Clear();
            var slots = _profile.Requirement?.CameraSlots;
            if (slots != null)
            {
                foreach (var s in slots)
                {
                    if (s != null) CameraSlotsUi.Add(s);
                }
            }
        }

        private string NextSlotKey()
        {
            char best = (char)('A' - 1);
            if (_profile.Requirement?.CameraSlots != null)
            {
                foreach (var s in _profile.Requirement.CameraSlots)
                {
                    if (string.IsNullOrWhiteSpace(s.SlotKey)) continue;
                    if (s.SlotKey.Length == 5 && s.SlotKey.StartsWith("Cam_"))
                    {
                        char c = s.SlotKey[4];
                        if (c >= 'A' && c <= 'Z' && c > best) best = c;
                    }
                }
            }
            char next = (char)(best + 1);
            return next <= 'Z' ? "Cam_" + next : "Cam_" + (_profile.Requirement.CameraSlots.Count + 1);
        }

        private static string MapMountToInstallKind(string mount)
        {
            if (string.Equals(mount, "眼在手上")) return "眼在手上（随执行机构）";
            if (string.Equals(mount, "眼在手外")) return "上固定（俯视工面）";
            return "上固定（俯视工面）";
        }

        private static string DefaultPurposeFromTask(string taskType)
        {
            switch (taskType)
            {
                case "定位抓取": return "引导定位";
                case "尺寸测量": return "尺寸测量";
                case "外观缺陷": return "缺陷检测";
                case "OCR·字符": return "OCR·字符";
                case "有无检测": return "有无检测";
                default: return string.Empty;
            }
        }

        // ==================== 选项集（下拉） ====================
        public string[] TaskTypeOptions => WithPending(StationProposalEngine.TaskTypeOptions);
        public string[] CameraMountOptions => WithPending(StationProposalEngine.CameraMountOptions);
        public string[] ShootModeOptions => WithPending(StationProposalEngine.ShootModeOptions);
        public string[] AxisToSurfaceOptions => WithPending(new[] { "垂直拍摄", "斜拍" });
        public string[] LightingOptions => WithPending(StationProposalEngine.LightingOptions);
        public string[] ToolHeadOptions => WithPending(new[] { "1", "2", "多" });
        public string[] VerdictOutputOptions => WithPending(StationProposalEngine.VerdictOutputOptions);
        public string[] AngleNeedOptions => WithPending(new[] { "无角度要求", "抓取/放置带角度", "任意角度（0~360）" });

        private static string[] WithPending(string[] src)
        {
            var list = new List<string> { string.Empty };
            list.AddRange(src);
            return list.ToArray();
        }

        // ==================== 推导预览（可重算，字段变化自动刷新）====================
        private string _flowText = string.Empty;
        public string FlowText { get => _flowText; private set { _flowText = value; OnPropertyChanged(nameof(FlowText)); } }

        private string _calibText = string.Empty;
        public string CalibText { get => _calibText; private set { _calibText = value; OnPropertyChanged(nameof(CalibText)); } }

        private string _assetsText = string.Empty;
        public string AssetsText { get => _assetsText; private set { _assetsText = value; OnPropertyChanged(nameof(AssetsText)); } }

        private string _warningsText = string.Empty;
        public string WarningsText { get => _warningsText; private set { _warningsText = value; OnPropertyChanged(nameof(WarningsText)); } }

        private void RefreshDerivedView()
        {
            StationProposalEngine.RebuildProposal(_profile);
            FlowText = string.IsNullOrWhiteSpace(_profile.SuggestedFlowSkeleton) ? "— 待确认 —" : _profile.SuggestedFlowSkeleton;
            CalibText = string.IsNullOrWhiteSpace(_profile.CalibrationSuggestion) ? "— 待确认 —" : _profile.CalibrationSuggestion;
            AssetsText = _profile.AssetSuggestions != null && _profile.AssetSuggestions.Count > 0
                ? "· " + string.Join("\n· ", _profile.AssetSuggestions)
                : "— 待确认 —";
            WarningsText = _profile.FeasibilityWarnings != null && _profile.FeasibilityWarnings.Count > 0
                ? "· " + string.Join("\n· ", _profile.FeasibilityWarnings)
                : "（无）";
        }

        // ==================== 草稿载入 ====================
        public class DraftOption
        {
            public StationProfile Profile { get; set; }
            public string Label =>
                $"{Profile.StationName ?? "(未命名)"} | {Profile.StationCode ?? "(无代码)"} | {Profile.Requirement?.TaskType ?? "未填任务"} | {Profile.UpdatedTime:MM-dd HH:mm}";
        }

        public ObservableCollection<DraftOption> DraftOptions { get; } = new ObservableCollection<DraftOption>();

        private void ReloadDraftOptions()
        {
            DraftOptions.Clear();
            foreach (var p in _repo.ListDrafts())
            {
                DraftOptions.Add(new DraftOption { Profile = p });
            }
            OnPropertyChanged(nameof(HasDrafts));
        }

        public bool HasDrafts => DraftOptions.Count > 0;

        private DraftOption _selectedDraft;
        public DraftOption SelectedDraft { get => _selectedDraft; set { _selectedDraft = value; OnPropertyChanged(nameof(SelectedDraft)); } }

        /// <summary>载入选中草稿（回填全部字段并重算推导）</summary>
        public bool LoadSelectedDraft()
        {
            if (SelectedDraft?.Profile == null) return false;
            LoadExistingProfile(SelectedDraft.Profile);
            return true;
        }

        /// <summary>
        /// 把一份已有 StationProfile（草稿 / Active 档案 / 补档空壳）回填当前问卷并重算推导。
        /// 载入草稿与编辑模式共用；字段拷贝后逐属性刷新绑定。
        /// </summary>
        public void LoadExistingProfile(StationProfile p)
        {
            if (p == null) return;
            _profile.ProfileId = p.ProfileId;
            _profile.Status = p.Status;
            _profile.IndustryTemplateCode = p.IndustryTemplateCode;
            _profile.Requirement = p.Requirement ?? new StationProfileRequirement();
            _profile.StationId = p.StationId;
            _profile.StationCode = p.StationCode ?? string.Empty;
            _profile.StationName = p.StationName ?? string.Empty;
            _profile.IsEnabled = p.IsEnabled;
            _profile.TimeoutMs = p.TimeoutMs;

            // 全字段刷新绑定
            OnPropertyChanged(nameof(StationCode));
            OnPropertyChanged(nameof(StationName));
            OnPropertyChanged(nameof(TaskType));
            OnPropertyChanged(nameof(CameraMount));
            OnPropertyChanged(nameof(ShootMode));
            OnPropertyChanged(nameof(AxisToSurface));
            OnPropertyChanged(nameof(Lighting));
            OnPropertyChanged(nameof(FovMm));
            OnPropertyChanged(nameof(WorkDistanceMm));
            OnPropertyChanged(nameof(MinFeatureMm));
            OnPropertyChanged(nameof(ToolHeadCount));
            OnPropertyChanged(nameof(ConcentricWithRotationAxis));
            OnPropertyChanged(nameof(ConcentricText));
            OnPropertyChanged(nameof(UsesCalibrationBoard));
            OnPropertyChanged(nameof(UsesCalibrationBoardText));
            OnPropertyChanged(nameof(AngleNeed));
            OnPropertyChanged(nameof(VerdictOutput));
            OnPropertyChanged(nameof(CyclePerMin));
            OnPropertyChanged(nameof(ActuatorKind));
            OnPropertyChanged(nameof(IndustryTemplateBadge));

            // 相机槽集合同步（档案可能带槽；Ui 与 Requirement 同元素引用）
            SyncSlotCollection();
            OnPropertyChanged(nameof(HasCameraSlots));

            // 同步模板下拉高亮（仅改选中显示，不触发 ApplyTemplate 以免模板默认值覆盖已填字段）
            if (!string.IsNullOrEmpty(p.IndustryTemplateCode))
            {
                var match = _templateOptions.FirstOrDefault(t => string.Equals(t.Code, p.IndustryTemplateCode, System.StringComparison.Ordinal));
                if (match != null)
                {
                    _selectedTemplate = match;
                    OnPropertyChanged(nameof(SelectedTemplate));
                }
            }

            RefreshDerivedView();
        }

        /// <summary>删除选中草稿</summary>
        public bool DeleteSelectedDraft()
        {
            if (SelectedDraft?.Profile == null) return false;
            bool ok = _repo.Delete(SelectedDraft.Profile.ProfileId);
            if (ok) ReloadDraftOptions();
            return ok;
        }

        // ==================== 收尾 ====================
        /// <summary>向导产物（存草稿=Draft；创建工位=Active 前置，父 VM 补 StationId 后置 Active）</summary>
        public StationProfile ResultProfile => _profile;

        /// <summary>true=用户点【存草稿】；false=点【创建工位】</summary>
        public bool ExitAsDraft { get; set; } = true;

        /// <summary>创建前基本校验（Code/Name 必填提示；返回错误文案，空=通过）</summary>
        public string ValidateForCreate()
        {
            if (string.IsNullOrWhiteSpace(StationCode)) return "请填写工位代码（StationCode，唯一标识，如 STA-003 / MHJ_A_1）";
            if (string.IsNullOrWhiteSpace(StationName)) return "请填写工位名称（如：麻将定位抓取工位）";
            return string.Empty;
        }

        /// <summary>完成前统一把最新问卷写回 profile 并重算推导</summary>
        public void FinalizeProfile()
        {
            RefreshDerivedView();
            _profile.UpdatedTime = System.DateTime.Now;
        }
    }
}
