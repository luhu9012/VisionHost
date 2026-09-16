// Grayson.Vision.WpfUI/ViewModel/StationMonitorExtensions/VisionPickPlaceTeachViewModel.cs
// VisionPickPlace（通用视觉定位取放引擎）工位监视扩展面板。
// 归属：通用引擎侧（S2 双吸嘴 / S3 同心吸嘴+多相机共用），经公共工位监视页
// 「扩展位」按 ProcessKey="VisionPickPlace" 挂载，公共监视页 XAML/VM 零引用。
//
// 功能：
//   ① 显示最近一次视觉流输出（wx/wy = CalibrationApply.OutputX/Y，Angle = ShapeMatch.MatchAngle）；
//   ② 示教模式开关（TeachMode=true 时只定位不走位，用于偏心/公式现场验证）；
//   ③ 角度策略参数写回（固定角归正 ↔ 视觉实测角归正：EnableVisionAngleCorrection /
//      RefAngleDeg / AngleCorrectionSign / WorkU / PlaceU）——随机姿态来料按参考姿态落盘；
//   ④ 执行参数表单写回（位点/安全Z/速度/真空IO/吸嘴偏心/单双吸嘴），S3 新机灌点入口；
//   ⑤ 参数覆盖管理（字段级补丁清单 + 一键清除恢复代码默认）。
//
// 配置语义（2026-09-01 定稿）＝「字段级覆盖补丁」：只持久化本面板拥有字段
// （示教/角度/位点/IO/偏心…），其余参数以 VisionPickPlaceConfig.cs 代码默认为准。
// ⚠ 迁移规则（2026-09-06 修复）：旧版全量快照只剪掉「与代码默认等价」的冗余字段，
//   非默认值的字段一律保留（含本面板未拥有字段，如 TilePitch）——绝不丢现场参数。
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Core.Processes;
using Grayson.Vision.WpfUI.Service;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Grayson.Vision.WpfUI.ViewModel.StationMonitorExtensions
{
    public class VisionPickPlaceTeachViewModel : ViewModelBase, IStationMonitorExtension
    {
        private readonly StationConfigService _configService;

        private string _stationCode;
        private Grayson.Vision.Core.StationWorker _worker;

        // ---- 最近一次视觉流输出的世界坐标 wx/wy 与实测角度 ----
        private double? _teachWx;
        private double? _teachWy;
        private double? _teachAngle;
        public string TeachWxText => _teachWx?.ToString("F3") ?? "--";
        public string TeachWyText => _teachWy?.ToString("F3") ?? "--";
        public string TeachAngleText => _teachAngle?.ToString("F2") ?? "--";

        private bool _teachModeEnabled;
        /// <summary>当前配置中的 TeachMode 状态（Bind 时从 ProcessConfigJson 读取）</summary>
        public bool TeachModeEnabled
        {
            get => _teachModeEnabled;
            private set
            {
                if (Set(ref _teachModeEnabled, value))
                {
                    OnPropertyChanged(nameof(TeachModeStatusText));
                    RefreshCommandStates();
                }
            }
        }

        public string TeachModeStatusText => TeachModeEnabled
            ? "🟢 示教模式开启：只采图算落点，不走位不吸取"
            : "⚪ 示教模式关闭：触发即完整执行 定位→吸取→归正→放料";

        // ---- 角度策略参数（可编辑，写回 VisionPickPlaceConfig 补丁）----
        private string _enableAngleCorrectionText = "False";
        /// <summary>视觉实测角归正开关文本（True/False，与 RefAngle/Sign 一并写回）</summary>
        public string EnableAngleCorrectionText
        {
            get => _enableAngleCorrectionText;
            set => Set(ref _enableAngleCorrectionText, value);
        }

        private string _refAngleText = "0";
        /// <summary>模板参考角 θ_ref（°）：模板建时的正位姿态角，通常 0。</summary>
        public string RefAngleText { get => _refAngleText; set => Set(ref _refAngleText, value); }

        private string _angleSignText = "1";
        /// <summary>归正方向符号（1 或 -1）：相机朝下=1；朝上(下相机检知吸起工件)=-1。</summary>
        public string AngleSignText { get => _angleSignText; set => Set(ref _angleSignText, value); }

        private string _workUText = "0";
        /// <summary>吸取时 U 轴姿态角（°）</summary>
        public string WorkUText { get => _workUText; set => Set(ref _workUText, value); }

        private string _placeUText = "0";
        /// <summary>放料目标姿态角（°）：视觉归正关闭时 U 直接转该角</summary>
        public string PlaceUText { get => _placeUText; set => Set(ref _placeUText, value); }

        // ---- 执行参数（2026-09-06 扩展：位点/安全Z/速度/真空/偏心，S3 灌点入口）----
        // 命名约定 <Config字段名>Text；LoadCfgFields/CollectAllFields 统一搬运，新增字段只需改字典。
        private string _safeZText = "50";
        private string _pickZText = "-2";
        private string _placeZText = "-1";
        private string _standbyXText = "150";
        private string _standbyYText = "0";
        private string _place1XText = "200";
        private string _place1YText = "100";
        private string _place2XText = "230";
        private string _place2YText = "100";
        private string _xySpeedText = "0";
        private string _zSpeedText = "0";
        private string _vacuumIo1Text = "0";
        private string _vacuumIo2Text = "1";
        private string _vacuumOnDelayText = "200";
        private string _vacuumOffDelayText = "150";
        private string _settleMsText = "150";
        private string _useSingleNozzleText = "True";
        private string _nozzle1EccXText = "0";
        private string _nozzle1EccYText = "0";
        private string _nozzle2EccXText = "0";
        private string _nozzle2EccYText = "0";

        public string SafeZText { get => _safeZText; set => Set(ref _safeZText, value); }
        public string PickZText { get => _pickZText; set => Set(ref _pickZText, value); }
        public string PlaceZText { get => _placeZText; set => Set(ref _placeZText, value); }
        public string StandbyXText { get => _standbyXText; set => Set(ref _standbyXText, value); }
        public string StandbyYText { get => _standbyYText; set => Set(ref _standbyYText, value); }
        public string Place1XText { get => _place1XText; set => Set(ref _place1XText, value); }
        public string Place1YText { get => _place1YText; set => Set(ref _place1YText, value); }
        public string Place2XText { get => _place2XText; set => Set(ref _place2XText, value); }
        public string Place2YText { get => _place2YText; set => Set(ref _place2YText, value); }
        public string XySpeedText { get => _xySpeedText; set => Set(ref _xySpeedText, value); }
        public string ZSpeedText { get => _zSpeedText; set => Set(ref _zSpeedText, value); }
        public string VacuumIo1Text { get => _vacuumIo1Text; set => Set(ref _vacuumIo1Text, value); }
        public string VacuumIo2Text { get => _vacuumIo2Text; set => Set(ref _vacuumIo2Text, value); }
        public string VacuumOnDelayText { get => _vacuumOnDelayText; set => Set(ref _vacuumOnDelayText, value); }
        public string VacuumOffDelayText { get => _vacuumOffDelayText; set => Set(ref _vacuumOffDelayText, value); }
        public string SettleMsText { get => _settleMsText; set => Set(ref _settleMsText, value); }
        /// <summary>单吸嘴形态开关（True/False；false=双吸嘴，Place2/VacuumIo2/Nozzle2 参与节拍）</summary>
        public string UseSingleNozzleText { get => _useSingleNozzleText; set => Set(ref _useSingleNozzleText, value); }
        public string Nozzle1EccXText { get => _nozzle1EccXText; set => Set(ref _nozzle1EccXText, value); }
        public string Nozzle1EccYText { get => _nozzle1EccYText; set => Set(ref _nozzle1EccYText, value); }
        public string Nozzle2EccXText { get => _nozzle2EccXText; set => Set(ref _nozzle2EccXText, value); }
        public string Nozzle2EccYText { get => _nozzle2EccYText; set => Set(ref _nozzle2EccYText, value); }

        // ---- 下相机二次校准段（2026-09-11 新增：复合工位"上相机抓 → 下相机校 → 放"三段节拍）----
        // 仅当【工位档案声明了下固定相机槽】时才在面板显示（见 RefreshFieldRelevance）。
        private string _enableDownCameraText = "False";
        private string _downCameraXText = "0";
        private string _downCameraYText = "0";
        private string _downCameraZText = "0";
        private string _downCameraMinScoreText = "0.5";
        private string _downCameraAngleSignText = "-1";
        private string _downCameraPosCorrText = "True";
        private string _downCameraAngleCorrText = "True";

        /// <summary>下相机二次校准总开关（True/False）。false=传统单相机流程，下相机段整段跳过。</summary>
        public string EnableDownCameraText { get => _enableDownCameraText; set => Set(ref _enableDownCameraText, value); }
        /// <summary>下相机视场中心对应的机械位 X（mm，命令位）：吸取后把卡片移到该位正上方拍照。标定九点网格中心即此点。</summary>
        public string DownCameraXText { get => _downCameraXText; set => Set(ref _downCameraXText, value); }
        /// <summary>下相机视场中心对应的机械位 Y（mm，命令位）。</summary>
        public string DownCameraYText { get => _downCameraYText; set => Set(ref _downCameraYText, value); }
        /// <summary>下相机拍照时的 Z 高度（mm）：必须与标定时一致，否则像素当量失真。</summary>
        public string DownCameraZText { get => _downCameraZText; set => Set(ref _downCameraZText, value); }
        /// <summary>下相机段匹配分数下限（低于判纠偏 NG，回落固定位放置）。</summary>
        public string DownCameraMinScoreText { get => _downCameraMinScoreText; set => Set(ref _downCameraMinScoreText, value); }
        /// <summary>下相机段角度归正符号（1 / -1）：仰视镜像通常取 -1，越归正越歪就翻。</summary>
        public string DownCameraAngleSignText { get => _downCameraAngleSignText; set => Set(ref _downCameraAngleSignText, value); }
        /// <summary>是否用下相机实测偏差做位置纠偏（True/False）。</summary>
        public string DownCameraPosCorrText { get => _downCameraPosCorrText; set => Set(ref _downCameraPosCorrText, value); }
        /// <summary>是否用下相机实测角做角度纠偏（True/False）。</summary>
        public string DownCameraAngleCorrText { get => _downCameraAngleCorrText; set => Set(ref _downCameraAngleCorrText, value); }

        /// <summary>把 Config 值灌入全部文本编辑框（Bind/重置后调用）</summary>
        private void LoadCfgFields(VisionPickPlaceConfig cfg)
        {
            if (cfg == null) return;
            TeachModeEnabled = cfg.TeachMode;
            EnableAngleCorrectionText = cfg.EnableVisionAngleCorrection ? "True" : "False";
            RefAngleText = cfg.RefAngleDeg.ToString("F1");
            AngleSignText = cfg.AngleCorrectionSign.ToString("F0");
            WorkUText = cfg.WorkU.ToString("F1");
            PlaceUText = cfg.PlaceU.ToString("F1");

            SafeZText = cfg.SafeZ.ToString("F1");
            PickZText = cfg.PickZ.ToString("F1");
            PlaceZText = cfg.PlaceZ.ToString("F1");
            StandbyXText = cfg.StandbyX.ToString("F1");
            StandbyYText = cfg.StandbyY.ToString("F1");
            Place1XText = cfg.Place1X.ToString("F1");
            Place1YText = cfg.Place1Y.ToString("F1");
            Place2XText = cfg.Place2X.ToString("F1");
            Place2YText = cfg.Place2Y.ToString("F1");
            XySpeedText = cfg.XySpeed.ToString("F1");
            ZSpeedText = cfg.ZSpeed.ToString("F1");
            VacuumIo1Text = cfg.VacuumIo1.ToString();
            VacuumIo2Text = cfg.VacuumIo2.ToString();
            VacuumOnDelayText = cfg.VacuumOnDelayMs.ToString();
            VacuumOffDelayText = cfg.VacuumOffDelayMs.ToString();
            SettleMsText = cfg.SettleMs.ToString();
            UseSingleNozzleText = cfg.UseSingleNozzle ? "True" : "False";
            Nozzle1EccXText = cfg.Nozzle1EccX.ToString("F2");
            Nozzle1EccYText = cfg.Nozzle1EccY.ToString("F2");
            Nozzle2EccXText = cfg.Nozzle2EccX.ToString("F2");
            Nozzle2EccYText = cfg.Nozzle2EccY.ToString("F2");

            EnableDownCameraText = cfg.EnableDownCameraCorrection ? "True" : "False";
            DownCameraXText = cfg.DownCameraX.ToString("F3");
            DownCameraYText = cfg.DownCameraY.ToString("F3");
            DownCameraZText = cfg.DownCameraZ.ToString("F3");
            DownCameraMinScoreText = cfg.DownCameraMinScore.ToString("F2");
            DownCameraAngleSignText = cfg.DownCameraAngleSign.ToString("F0");
            DownCameraPosCorrText = cfg.EnableDownCameraPositionCorrection ? "True" : "False";
            DownCameraAngleCorrText = cfg.EnableDownCameraAngleCorrection ? "True" : "False";
        }

        public RelayCommand SaveAngleConfigCommand { get; }
        /// <summary>保存执行参数（位点/姿态/速度/真空/偏心/单双吸嘴）为字段级补丁并热更新</summary>
        public RelayCommand SaveMotionConfigCommand { get; }
        public RelayCommand ExitTeachModeCommand { get; }
        public RelayCommand EnableTeachModeCommand { get; }
        public RelayCommand ResetToDefaultsCommand { get; }

        /// <summary>本示教面板允许持久化（写补丁）的字段全集——其余字段一律回归代码默认。</summary>
        private static readonly string[] TeachOwnedFields =
        {
            "TeachMode", "EnableVisionAngleCorrection", "RefAngleDeg",
            "AngleCorrectionSign", "WorkU", "PlaceU",
            // 姿态与位点
            "SafeZ", "PickZ", "PlaceZ", "StandbyX", "StandbyY",
            "Place1X", "Place1Y", "Place2X", "Place2Y",
            // 速度 / 真空 / 节拍
            "XySpeed", "ZSpeed", "VacuumIo1", "VacuumIo2",
            "VacuumOnDelayMs", "VacuumOffDelayMs", "SettleMs",
            // 吸嘴形态与偏心
            "UseSingleNozzle", "Nozzle1EccX", "Nozzle1EccY", "Nozzle2EccX", "Nozzle2EccY",
            // 下相机二次校准段（2026-09-11：复合工位三段节拍参数）
            "EnableDownCameraCorrection", "DownCameraX", "DownCameraY", "DownCameraZ",
            "DownCameraMinScore", "DownCameraAngleSign",
            "EnableDownCameraPositionCorrection", "EnableDownCameraAngleCorrection"
        };

        private string _overlayInfoText = "参数覆盖：无（全部参数以 VisionPickPlaceConfig.cs 代码默认为准）";
        /// <summary>当前持久化补丁与代码默认值的差异清单（面板展示用）。</summary>
        public string OverlayInfoText
        {
            get => _overlayInfoText;
            private set => Set(ref _overlayInfoText, value);
        }

        #region 面板字段相关性（任务模板 + 工位档案联动，2026-09-11）

        // 原则：面板只显示【这个工位真的用得上】的字段，不把全部字段平铺——
        //   ① 工位档案没声明"下固定相机槽"  → 整组「下相机二次校准」不显示（显示也无从填起，反而误导）；
        //   ② 单吸嘴形态（ToolHeadCount=1 且 UseSingleNozzle=True）→ 隐藏 放料2/真空2/嘴2 三行；
        //   ③ 工位档案 AngleNeed 明确"不需要角度" → 隐藏「角度策略」组；
        //   ④ 档案/模板读不到时一律【保守显示】（宁多勿缺，不把人锁在门外）。

        private bool _showAngleGroup = true;
        private bool _showDownCameraGroup;
        private bool _showDualNozzleRows = true;
        private string _relevanceHintText = "字段范围：未读取到工位档案 / 任务模板 —— 按全量显示（保守模式）。";

        /// <summary>是否显示「角度策略」分组（档案 AngleNeed 明确不需要角度时隐藏）</summary>
        public bool ShowAngleGroup { get => _showAngleGroup; private set => Set(ref _showAngleGroup, value); }

        /// <summary>是否显示「下相机二次校准」分组（工位档案声明了下固定相机槽才显示）</summary>
        public bool ShowDownCameraGroup { get => _showDownCameraGroup; private set => Set(ref _showDownCameraGroup, value); }

        /// <summary>是否显示双吸嘴相关行（放料2 / 真空2 / 嘴2 偏心）</summary>
        public bool ShowDualNozzleRows { get => _showDualNozzleRows; private set => Set(ref _showDualNozzleRows, value); }

        /// <summary>面板顶部"字段范围依据"提示：工位/档案/模板/相机槽一句话说清。</summary>
        public string RelevanceHintText { get => _relevanceHintText; private set => Set(ref _relevanceHintText, value); }

        /// <summary>
        /// 按【工位档案】+【任务模板】+【当前参数】决定面板显示哪些分组。
        /// 在 Bind 与每次 LoadCfgFields 之后调用（换模板/换吸嘴形态后立即重算）。
        /// </summary>
        private void RefreshFieldRelevance(StationConfigModel station, VisionPickPlaceConfig cfg)
        {
            var profile = LoadStationProfile(station);
            var slots = profile?.Requirement?.CameraSlots ?? new List<VisionSlotInfo>();
            string tplCode = station?.TaskTemplateCode;
            string tplName = station?.TaskTemplateName;

            bool hasProfileInfo = profile != null;
            bool hasDownSlot = slots.Any(IsDownLookingSlot);
            bool hasAnySlots = slots.Count > 0;
            bool angleNotNeeded = profile?.Requirement?.AngleNeed != null
                                  && profile.Requirement.AngleNeed.Contains("不需要");
            bool dualHeadByProfile = !string.IsNullOrWhiteSpace(profile?.Requirement?.ToolHeadCount)
                                     && profile.Requirement.ToolHeadCount.Trim() != "1";
            bool dualNozzleByCfg = cfg != null && !cfg.UseSingleNozzle;

            // ① 角度策略组：档案明确"不需要角度"才隐藏（未知/未读到时显示）
            ShowAngleGroup = !angleNotNeeded;

            // ② 下相机组：档案有声明的下固定槽才显示；档案读不到时看当前参数是否已启用过（不吞掉现场已配的值）
            ShowDownCameraGroup = hasDownSlot
                                  || (!hasProfileInfo && cfg != null && cfg.EnableDownCameraCorrection);

            // ③ 双吸嘴行：档案说多头 或 现场已切成双吸嘴 → 显示
            ShowDualNozzleRows = dualHeadByProfile || dualNozzleByCfg || !hasProfileInfo;

            // 提示文案
            if (!hasProfileInfo)
            {
                RelevanceHintText = $"字段范围：工位 {_stationCode} 无工位档案 —— 按全量显示（保守模式）。"
                                    + (string.IsNullOrWhiteSpace(tplCode) ? "" : $" 任务模板：{tplCode} {tplName}。");
                return;
            }

            string slotText = hasAnySlots
                ? string.Join(" / ", slots.Select(s => $"{s.SlotKey} {s.InstallKind}"))
                : "未声明相机槽";
            var hidden = new List<string>();
            if (!ShowAngleGroup) hidden.Add("角度策略");
            if (!ShowDownCameraGroup) hidden.Add("下相机二次校准");
            if (!ShowDualNozzleRows) hidden.Add("放料2/真空2/嘴2");

            RelevanceHintText =
                $"字段范围依据 —— 工位 {profile.StationCode} {profile.StationName}"
                + $"｜相机槽：{slotText}"
                + (string.IsNullOrWhiteSpace(tplCode) ? "｜任务模板：未绑定" : $"｜任务模板：{tplCode} {tplName}")
                + (hidden.Count > 0 ? $"｜已隐藏：{string.Join("、", hidden)}" : "｜无隐藏字段");
        }

        /// <summary>按工位配置文件（LiteDB）加载工位档案（Config\StationProfiles\{StationId}.json）。读不到返回 null。</summary>
        private StationProfile LoadStationProfile(StationConfigModel station)
        {
            try
            {
                if (station == null) return null;
                var repo = new StationProfileRepository();
                var profile = repo.GetByStationId(station.StationId);
                // 兜底：老档案可能只有 StationCode（StationId 缺失/不符）→ 按 StationCode 再找一遍
                if (profile == null && !string.IsNullOrWhiteSpace(station.StationCode))
                {
                    profile = repo.ListAll().FirstOrDefault(p =>
                        string.Equals(p.StationCode, station.StationCode, StringComparison.OrdinalIgnoreCase));
                }
                return profile;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>是否"下固定/仰视"相机槽（InstallKind 或 Purpose 里出现 下/仰 关键字）。</summary>
        private static bool IsDownLookingSlot(VisionSlotInfo slot)
        {
            if (slot == null) return false;
            string text = ((slot.InstallKind ?? "") + "|" + (slot.AxisToSurface ?? "") + "|" + (slot.SlotKey ?? ""));
            return text.Contains("下") || text.Contains("仰");
        }

        #endregion

        public event Action<string, string> Log;

        public VisionPickPlaceTeachViewModel(StationConfigService configService = null)
        {
            _configService = configService ?? new StationConfigService();

            SaveAngleConfigCommand = new RelayCommand(() => SaveAngleConfig(), () => true);
            SaveMotionConfigCommand = new RelayCommand(() => SaveMotionConfig(), () => true);
            ExitTeachModeCommand = new RelayCommand(() => SetTeachMode(false), () => TeachModeEnabled);
            EnableTeachModeCommand = new RelayCommand(() => SetTeachMode(true), () => !TeachModeEnabled);
            ResetToDefaultsCommand = new RelayCommand(() => ResetToDefaults(), () => true);
        }

        /// <summary>宿主挂载面板：订阅 worker 节点事件 + 加载当前配置到编辑框。</summary>
        public void Bind(string stationCode)
        {
            try
            {
                Unbind();
                _stationCode = stationCode;

                var worker = GetStationWorker();
                if (worker != null)
                {
                    _worker = worker;
                    worker.OnNodeExecuted += Worker_OnNodeExecuted;
                }

                var cfg = TryLoadVisionPickPlaceConfig(out var boundStation);
                if (cfg != null)
                {
                    LoadCfgFields(cfg);
                    // 按工位档案 + 任务模板收敛面板字段范围（只显示本工位用得上的分组）
                    RefreshFieldRelevance(boundStation, cfg);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VisionPickPlaceTeach] 绑定工位失败: {ex.Message}");
            }
        }

        /// <summary>宿主卸载面板：退订 worker 事件。</summary>
        public void Dispose()
        {
            Unbind();
        }

        private void Unbind()
        {
            if (_worker != null)
            {
                _worker.OnNodeExecuted -= Worker_OnNodeExecuted;
                _worker = null;
            }
            _stationCode = null;
        }

        private void RefreshCommandStates()
        {
            SaveAngleConfigCommand?.RaiseCanExecuteChanged();
            ExitTeachModeCommand?.RaiseCanExecuteChanged();
            EnableTeachModeCommand?.RaiseCanExecuteChanged();
        }

        /// <summary>取当前绑定工位的 Core Worker（未创建返回 null）。</summary>
        private Grayson.Vision.Core.StationWorker GetStationWorker()
        {
            try
            {
                return (App.StationHostRuntime as Grayson.Vision.Core.Station.StationHostRuntime)
                    ?.GetStationWorker(_stationCode);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>捕获执行节点最新输出：CalibrationApply.OutputX/Y = 世界坐标 wx/wy；
        /// ShapeMatch.MatchAngle = 实测角度（°）。不解析日志文本，直接取端口值。</summary>
        private void Worker_OnNodeExecuted(object sender, NodeEventArgs e)
        {
            try
            {
                var ports = e.Node?.OutputPorts;
                if (ports == null) return;

                var px = ports.FirstOrDefault(p => p.PortName == "OutputX")?.DataValue;
                var py = ports.FirstOrDefault(p => p.PortName == "OutputY")?.DataValue;
                if (px is double wx && py is double wy)
                {
                    _teachWx = wx;
                    _teachWy = wy;
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        OnPropertyChanged(nameof(TeachWxText));
                        OnPropertyChanged(nameof(TeachWyText));
                    }));
                }

                var pa = ports.FirstOrDefault(p => p.PortName == "MatchAngle")?.DataValue;
                if (pa is double ang)
                {
                    _teachAngle = ang;
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        OnPropertyChanged(nameof(TeachAngleText))));
                }
            }
            catch { /* 捕获失败不影响正常流程 */ }
        }

        #region 配置读写（VisionPickPlaceConfig ⇄ ProcessConfigJson 字段级补丁 + 热更新）

        /// <summary>读取当前绑定工位的有效 VisionPickPlaceConfig（代码默认 + 补丁覆盖）。
        /// 工位未绑定 VisionPickPlace 过程 / 配置读取失败时返回 null。</summary>
        private VisionPickPlaceConfig TryLoadVisionPickPlaceConfig(out StationConfigModel stationOut)
        {
            stationOut = null;
            try
            {
                var allStations = _configService.LoadAllLines()
                    .SelectMany(l => l.Stations)
                    .ToList();
                var station = allStations.FirstOrDefault(s => s.StationCode == _stationCode);
                if (station == null)
                {
                    var codes = string.Join(", ", allStations.Select(s => $"'{s.StationCode}'"));
                    AddLog("ERROR",
                        $"配置读取失败：数据库中找不到当前工位（绑定编码='{_stationCode ?? "<null>"}'）。" +
                        $"数据库现有工位: [{(string.IsNullOrEmpty(codes) ? "<空>" : codes)}]。");
                    return null;
                }
                if (!string.Equals(station.ProcessKey, "VisionPickPlace", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog("ERROR",
                        $"配置读取失败：工位 '{_stationCode}' 的 ProcessKey='{station.ProcessKey ?? "<空>"}' ≠ 'VisionPickPlace'，" +
                        "请在【工位管理】中为该工位选择 VisionPickPlace 业务过程后保存。");
                    return null;
                }
                stationOut = station;

                MigrateLegacySnapshotIfNeeded(station);

                var cfg = ProcessConfigOverlay.LoadEffective<VisionPickPlaceConfig>(station.ProcessConfigJson);
                UpdateOverlayInfo(station.ProcessConfigJson);
                return cfg;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"读取工位过程配置失败: {ex.GetType().Name}: {ex.Message}（Inner: {ex.InnerException?.Message ?? "无"}）");
                return null;
            }
        }

        /// <summary>把旧版全量快照瘦身为字段级补丁：仅剪掉「与代码默认等价」的冗余字段；
        /// 非默认值的字段一律保留（含本面板未拥有的 TilePitch 等，避免误删现场参数）。</summary>
        private void MigrateLegacySnapshotIfNeeded(StationConfigModel station)
        {
            var patch = ProcessConfigOverlay.Parse(station.ProcessConfigJson);
            if (patch.Count == 0) return;

            var defaults = JObject.FromObject(new VisionPickPlaceConfig());
            var redundant = new List<string>();
            foreach (var prop in patch.Properties())
            {
                JToken def;
                if (defaults.TryGetValue(prop.Name, out def) && JToken.DeepEquals(prop.Value, def))
                {
                    redundant.Add(prop.Name);
                }
            }
            if (redundant.Count == 0) return; // 已是纯补丁（非默认字段全部保留）

            foreach (var name in redundant) patch.Remove(name);
            station.ProcessConfigJson = patch.Count == 0 ? null : patch.ToString(Formatting.Indented);
            if (_configService.SaveStation(station))
            {
                AddLog("INFO",
                    $"已把旧全量快照瘦身为字段级补丁：剪除 {redundant.Count} 个与代码默认等价的冗余字段" +
                    $"（{string.Join(", ", redundant.Take(8))}{(redundant.Count > 8 ? "…" : "")}）；" +
                    "非默认参数全部保留。");
                HotReloadWorkerProcessConfig(station.ProcessConfigJson);
            }
            else
            {
                AddLog("WARN", "快照瘦身落库失败：本次仍按原值运行，下次打开面板再试。");
                station.ProcessConfigJson = patch.ToString(Formatting.Indented);
            }
        }

        /// <summary>刷新面板的「参数覆盖」清单展示。</summary>
        private void UpdateOverlayInfo(string overlayJson)
        {
            try
            {
                var diffs = ProcessConfigOverlay.DescribeDifferences<VisionPickPlaceConfig>(overlayJson);
                OverlayInfoText = diffs.Count == 0
                    ? "参数覆盖：无 —— 全部参数以 VisionPickPlaceConfig.cs 代码默认为准（改代码即生效）"
                    : $"参数覆盖 {diffs.Count} 项（优先于代码默认值）：\n· " + string.Join("\n· ", diffs);
            }
            catch (Exception ex)
            {
                OverlayInfoText = $"参数覆盖清单生成失败: {ex.Message}";
            }
        }

        /// <summary>把字段级修改合并进现有补丁、落库并热更新运行中 Worker（重挂业务过程）。</summary>
        private bool TrySaveOverlayFields(StationConfigModel station, IDictionary<string, object> fields)
        {
            var json = ProcessConfigOverlay.SetFields(station.ProcessConfigJson, fields, new VisionPickPlaceConfig());
            station.ProcessConfigJson = json;
            if (!_configService.SaveStation(station))
            {
                AddLog("ERROR", "工位配置保存失败（数据库写入异常），配置未变更。");
                return false;
            }
            HotReloadWorkerProcessConfig(json);
            UpdateOverlayInfo(json);
            return true;
        }

        /// <summary>热更新 Worker 的业务过程配置：更新 DesiredProcessConfigJson 后重挂业务过程。
        /// 工位正在运行时只保存不热挂（避免运行中重挂打断时序），下次启动自然生效。</summary>
        private void HotReloadWorkerProcessConfig(string json)
        {
            try
            {
                if (App.StationHostRuntime is Grayson.Vision.Core.Station.StationHostRuntime runtime)
                {
                    var worker = runtime.GetStationWorker(_stationCode);
                    if (worker == null)
                    {
                        AddLog("WARN", "运行时中未找到该工位 Worker，配置已保存、下次创建工位时生效。");
                        return;
                    }

                    worker.DesiredProcessConfigJson = json;
                    var stateText = worker.State.ToString();
                    if (stateText == "Running" || stateText == "Paused")
                    {
                        AddLog("WARN", "工位正在运行，配置已保存但未热挂载——停止后再次启动时生效。");
                        return;
                    }

                    worker.DetachProcess();
                    worker.EnsureProcessAttached();
                    AddLog("INFO", "已按新配置重新挂载业务过程（热更新即时生效）。");
                }
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"热更新业务过程配置失败: {ex.Message}（配置已保存，重启程序后生效）");
            }
        }

        #endregion

        #region 角度策略写回

        /// <summary>角度策略参数解析 + 写回：EnableVisionAngleCorrection / RefAngleDeg /
        /// AngleCorrectionSign / WorkU / PlaceU。同值自动剪枝（ProcessConfigOverlay.SetFields）。</summary>
        private void SaveAngleConfig()
        {
            var cfg = TryLoadVisionPickPlaceConfig(out var station);
            if (cfg == null || station == null)
            {
                AddLog("ERROR", "保存失败：当前工位未绑定 VisionPickPlace 业务过程或配置读取失败。");
                return;
            }

            bool enable;
            var enableText = (EnableAngleCorrectionText ?? string.Empty).Trim();
            if (enableText.Equals("True", StringComparison.OrdinalIgnoreCase) || enableText == "1")
            {
                enable = true;
            }
            else if (enableText.Equals("False", StringComparison.OrdinalIgnoreCase) || enableText == "0" || enableText.Length == 0)
            {
                enable = false;
            }
            else
            {
                AddLog("ERROR", "视觉归正开关应为 True/False（或 1/0）。");
                return;
            }
            if (!float.TryParse((RefAngleText ?? string.Empty).Trim(), out float refAngle))
            {
                AddLog("ERROR", "参考角 RefAngle 不合法（应为数字，单位°）。");
                return;
            }
            if (!float.TryParse((AngleSignText ?? string.Empty).Trim(), out float sign) ||
                (sign != 1f && sign != -1f))
            {
                AddLog("ERROR", "方向符号应为 1 或 -1。");
                return;
            }
            if (!float.TryParse((WorkUText ?? string.Empty).Trim(), out float workU))
            {
                AddLog("ERROR", "吸取角 WorkU 不合法（应为数字，单位°）。");
                return;
            }
            if (!float.TryParse((PlaceUText ?? string.Empty).Trim(), out float placeU))
            {
                AddLog("ERROR", "放料角 PlaceU 不合法（应为数字，单位°）。");
                return;
            }

            var fields = new Dictionary<string, object>
            {
                { "EnableVisionAngleCorrection", enable },
                { "RefAngleDeg", refAngle },
                { "AngleCorrectionSign", sign },
                { "WorkU", workU },
                { "PlaceU", placeU }
            };

            if (TrySaveOverlayFields(station, fields))
            {
                var oldEnable = cfg.EnableVisionAngleCorrection;
                var oldSign = cfg.AngleCorrectionSign;
                AddLog("INFO",
                    $"✅ 角度策略已保存：" +
                    $"归正开关 {oldEnable} → {enable}" +
                    (enable ? $"，RefAngle={refAngle:F1}°，Sign={oldSign:F0} → {sign:F0}（符号错=越归正越歪，翻 1/-1）" : "") +
                    $"；WorkU={workU:F1}°、PlaceU={placeU:F1}°。" +
                    "下次触发生效（固定角归正→U 转 PlaceU；视觉归正→U_place=PlaceU−Sign·(Angle−Ref)）。");
            }
        }

        /// <summary>执行参数保存：位点/姿态/速度/真空/偏心/单双吸嘴 → 字段级补丁 + 热更新。
        /// 任一字段非法则整体中止（不落半套参数）；同默认值字段由 SetFields 自动剪枝。</summary>
        private void SaveMotionConfig()
        {
            var cfg = TryLoadVisionPickPlaceConfig(out var station);
            if (cfg == null || station == null)
            {
                AddLog("ERROR", "保存失败：当前工位未绑定 VisionPickPlace 业务过程或配置读取失败。");
                return;
            }

            var fields = new Dictionary<string, object>();
            string err;

            err = CollectFloatFields(fields);
            if (err != null) { AddLog("ERROR", err); return; }
            err = CollectIntFields(fields);
            if (err != null) { AddLog("ERROR", err); return; }
            err = CollectBoolFields(fields);
            if (err != null) { AddLog("ERROR", err); return; }

            if (fields.Count == 0)
            {
                AddLog("INFO", "执行参数没有变化（全部与当前值一致），无需写库。");
                return;
            }

            if (TrySaveOverlayFields(station, fields))
            {
                var parts = fields.Keys.ToList();
                string headline = fields.ContainsKey("UseSingleNozzle")
                    ? $"吸嘴形态=双吸嘴（{fields["UseSingleNozzle"]}）" : "吸嘴形态未变";
                AddLog("INFO",
                    $"✅ 执行参数已保存 {fields.Count} 项：[{string.Join("、", parts)}]。{headline}。" +
                    "已热更新（运行中则停止后下次启动生效）；下次触发按新位点/姿态执行。");
                // 吸嘴形态/下相机开关变化会影响面板该显示哪些字段 → 重算可见性
                var fresh = TryLoadVisionPickPlaceConfig(out _) ?? cfg;
                RefreshFieldRelevance(station, fresh);
            }
        }

        /// <summary>批量收集 float 型执行参数字段（返回错误文案；null=通过）</summary>
        private string CollectFloatFields(Dictionary<string, object> fields)
        {
            var defs = new Dictionary<string, string>
            {
                { "SafeZ", SafeZText }, { "PickZ", PickZText }, { "PlaceZ", PlaceZText },
                { "StandbyX", StandbyXText }, { "StandbyY", StandbyYText },
                { "Place1X", Place1XText }, { "Place1Y", Place1YText },
                { "Place2X", Place2XText }, { "Place2Y", Place2YText },
                { "XySpeed", XySpeedText }, { "ZSpeed", ZSpeedText },
                { "Nozzle1EccX", Nozzle1EccXText }, { "Nozzle1EccY", Nozzle1EccYText },
                { "Nozzle2EccX", Nozzle2EccXText }, { "Nozzle2EccY", Nozzle2EccYText },
                // 下相机段（机位与阈值）；总开关/两个纠偏开关走 CollectBoolFields
                { "DownCameraX", DownCameraXText }, { "DownCameraY", DownCameraYText },
                { "DownCameraZ", DownCameraZText },
                { "DownCameraMinScore", DownCameraMinScoreText },
                { "DownCameraAngleSign", DownCameraAngleSignText }
            };
            foreach (var kv in defs)
            {
                if (!float.TryParse((kv.Value ?? string.Empty).Trim(), out float v))
                {
                    return $"字段 [{kv.Key}] 不合法（应为数字：{kv.Value}）——已中止，未写库。";
                }
                fields[kv.Key] = v;
            }
            return null;
        }

        /// <summary>批量收集 int 型执行参数字段（真空 IO/延时）</summary>
        private string CollectIntFields(Dictionary<string, object> fields)
        {
            var defs = new Dictionary<string, string>
            {
                { "VacuumIo1", VacuumIo1Text }, { "VacuumIo2", VacuumIo2Text },
                { "VacuumOnDelayMs", VacuumOnDelayText },
                { "VacuumOffDelayMs", VacuumOffDelayText }, { "SettleMs", SettleMsText }
            };
            foreach (var kv in defs)
            {
                if (!int.TryParse((kv.Value ?? string.Empty).Trim(), out int v) || v < 0)
                {
                    return $"字段 [{kv.Key}] 不合法（应为非负整数：{kv.Value}）——已中止，未写库。";
                }
                fields[kv.Key] = v;
            }
            return null;
        }

        /// <summary>收集 bool 型执行参数（单双吸嘴开关 + 下相机段开关）</summary>
        private string CollectBoolFields(Dictionary<string, object> fields)
        {
            string err = ParseBoolField(UseSingleNozzleText, "UseSingleNozzle", out bool single);
            if (err != null) return err;
            fields["UseSingleNozzle"] = single;

            // 下相机段三个开关：仅在面板显示该分组时提交；未显示（工位档案无下固定相机）时跳过，
            // 避免"隐藏字段把已配置值悄悄改回默认"。
            if (ShowDownCameraGroup)
            {
                err = ParseBoolField(EnableDownCameraText, "EnableDownCameraCorrection", out bool enDown);
                if (err != null) return err;
                fields["EnableDownCameraCorrection"] = enDown;

                err = ParseBoolField(DownCameraPosCorrText, "EnableDownCameraPositionCorrection", out bool posCorr);
                if (err != null) return err;
                fields["EnableDownCameraPositionCorrection"] = posCorr;

                err = ParseBoolField(DownCameraAngleCorrText, "EnableDownCameraAngleCorrection", out bool angCorr);
                if (err != null) return err;
                fields["EnableDownCameraAngleCorrection"] = angCorr;
            }
            return null;
        }

        /// <summary>True/False（或 1/0）文本解析；返回错误文案，null=通过</summary>
        private static string ParseBoolField(string text, string fieldName, out bool value)
        {
            var t = (text ?? string.Empty).Trim();
            if (t.Equals("True", StringComparison.OrdinalIgnoreCase) || t == "1") { value = true; return null; }
            if (t.Equals("False", StringComparison.OrdinalIgnoreCase) || t == "0" || t.Length == 0) { value = false; return null; }
            value = false;
            return $"字段 [{fieldName}] 应为 True/False（或 1/0）：{t}——已中止，未写库。";
        }

        /// <summary>开启/退出示教模式（不改动角度参数），写回补丁并热更新。</summary>
        private void SetTeachMode(bool enable)
        {
            var cfg = TryLoadVisionPickPlaceConfig(out var station);
            if (cfg == null || station == null)
            {
                AddLog("ERROR", "操作失败：当前工位未绑定 VisionPickPlace 业务过程或配置读取失败。");
                return;
            }

            if (cfg.TeachMode == enable)
            {
                TeachModeEnabled = enable;
                return;
            }

            var fields = new Dictionary<string, object> { { "TeachMode", enable } };
            if (TrySaveOverlayFields(station, fields))
            {
                TeachModeEnabled = enable;
                AddLog("INFO", enable
                    ? "已开启示教模式：触发只采图算落点，不走位不吸取（角度参数保持不变）。"
                    : "已退出示教模式——下次触发将完整执行 定位→吸取→归正→放料。");
            }
        }

        /// <summary>清除该工位全部持久化参数覆盖（含示教写回值），所有参数恢复代码默认值。</summary>
        private void ResetToDefaults()
        {
            var cfg = TryLoadVisionPickPlaceConfig(out var station);
            if (station == null)
            {
                AddLog("ERROR", "重置失败：当前工位未绑定 VisionPickPlace 业务过程或配置读取失败。");
                return;
            }

            if (string.IsNullOrWhiteSpace(station.ProcessConfigJson))
            {
                UpdateOverlayInfo(null);
                AddLog("INFO", "当前没有任何参数覆盖，全部参数已是代码默认值，无需重置。");
                return;
            }

            var confirm = MessageBox.Show(
                $"将清除工位 '{_stationCode}' 的全部参数覆盖（含角度策略与 TeachMode），\n" +
                "所有参数恢复为 VisionPickPlaceConfig.cs 代码默认值。\n\n确定继续？",
                "确认重置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            station.ProcessConfigJson = null;
            if (!_configService.SaveStation(station))
            {
                AddLog("ERROR", "重置落库失败，配置未变更。");
                return;
            }

            HotReloadWorkerProcessConfig(null);
            var defaults = new VisionPickPlaceConfig();
            LoadCfgFields(defaults);
            RefreshFieldRelevance(station, defaults);
            UpdateOverlayInfo(null);
            AddLog("INFO",
                $"✅ 已清除全部参数覆盖：所有参数恢复代码默认（TeachMode={defaults.TeachMode}，" +
                $"归正开关={defaults.EnableVisionAngleCorrection}）。后续改 VisionPickPlaceConfig.cs 即时生效。");
        }

        #endregion

        private void AddLog(string level, string message)
        {
            Log?.Invoke(level, message);
        }
    }
}
