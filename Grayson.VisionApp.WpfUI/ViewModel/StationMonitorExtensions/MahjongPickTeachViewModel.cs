// Grayson.Vision.WpfUI/ViewModel/StationMonitorExtensions/MahjongPickTeachViewModel.cs
// MahjongPick（双滑台工件吸取）工位监视扩展面板：示教闭环。
// 归属：业务侧（MahjongPick 特有），经公共工位监视页「扩展位」按 ProcessKey 挂载，
// 公共监视页 XAML/VM 零 Mahjong 引用。
//
// 功能：①【单次触发】采图 → ② 捕获标定转换节点输出 wx/wy → ③ 手动点动轴让吸嘴
// 正对工件背面图案中心 → ④ 填 X*/Y* → ⑤ 一键换算 NozzleOffset 写回配置并热更新
// Worker → 自动退出示教。
//
// 产品决策（2026-08-31）：Z 轴高度（PickZ/PlaceZ/SafeZ）与真空吸取时序
// （VacuumDwellAfterSenseMs/VacuumOnDelayMs/VacuumSenseTimeoutMs）不再提供界面配置，
// 仅存在于 MahjongPickConfig.cs 代码默认层面，由代码维护。
//
// 配置语义（2026-09-01 定稿）＝「字段级覆盖补丁」：
//   ProcessConfigJson 不再是全量快照——示教写回只持久化本面板拥有的字段
//   （NozzleXOffset/NozzleYOffset/MirrorGuideEnabled/TeachMode），其余参数
//   （PickZ/速度/IO/节拍…）一律以 MahjongPickConfig.cs 代码默认为准，改代码即生效。
//   旧版全量快照在首次加载时自动迁移为纯补丁；面板提供覆盖清单展示与一键重置。
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
    public class MahjongPickTeachViewModel : ViewModelBase, IStationMonitorExtension
    {
        private readonly StationConfigService _configService;

        private string _stationCode;
        private Grayson.Vision.Core.StationWorker _worker;

        // ---- 最近一次标定转换节点输出的世界坐标 wx/wy（工件相对标定 Mark 的偏移 mm）----
        private double? _teachWx;
        private double? _teachWy;
        public string TeachWxText => _teachWx?.ToString("F3") ?? "--";
        public string TeachWyText => _teachWy?.ToString("F3") ?? "--";

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
            : "⚪ 示教模式关闭：触发即完整执行走位/吸取/放料";

        private string _teachXStarText = string.Empty;
        /// <summary>示教输入：吸嘴对准工件背面图案中心时的 X 轴(轴1)读数 X*（mm）</summary>
        public string TeachXStarText { get => _teachXStarText; set => Set(ref _teachXStarText, value); }

        private string _teachYStarText = string.Empty;
        /// <summary>示教输入：对准时的 左Y 轴(轴3)读数 Y*（mm）</summary>
        public string TeachYStarText { get => _teachYStarText; set => Set(ref _teachYStarText, value); }

        private bool _teachUseMirrorFormula;
        /// <summary>落点公式选择：false=当前公式(+wx/+wy)、true=镜像公式(−wx/−wy)。
        /// 写回时同步切换 MirrorGuideEnabled（两参数必须配套，否则落点差 2×wx）。</summary>
        public bool TeachUseMirrorFormula { get => _teachUseMirrorFormula; set => Set(ref _teachUseMirrorFormula, value); }

        public RelayCommand ApplyTeachCommand { get; }
        public RelayCommand ExitTeachModeCommand { get; }
        public RelayCommand EnableTeachModeCommand { get; }
        public RelayCommand ResetToDefaultsCommand { get; }

        /// <summary>本示教面板允许持久化（写补丁）的字段——其余字段一律回归代码默认。</summary>
        private static readonly string[] TeachOwnedFields =
        {
            "NozzleXOffset", "NozzleYOffset", "MirrorGuideEnabled", "TeachMode"
        };

        private string _overlayInfoText = "参数覆盖：无（全部参数以 MahjongPickConfig.cs 代码默认为准）";
        /// <summary>当前持久化补丁与代码默认值的差异清单（面板展示用）。</summary>
        public string OverlayInfoText
        {
            get => _overlayInfoText;
            private set => Set(ref _overlayInfoText, value);
        }

        public event Action<string, string> Log;

        public MahjongPickTeachViewModel(StationConfigService configService = null)
        {
            _configService = configService ?? new StationConfigService();

            ApplyTeachCommand = new RelayCommand(() => ApplyTeach(), () => true);
            ExitTeachModeCommand = new RelayCommand(() => SetTeachMode(false), () => TeachModeEnabled);
            EnableTeachModeCommand = new RelayCommand(() => SetTeachMode(true), () => !TeachModeEnabled);
            ResetToDefaultsCommand = new RelayCommand(() => ResetToDefaults(), () => true);
        }

        /// <summary>宿主挂载面板：订阅 worker 节点事件 + 加载当前 TeachMode 状态。</summary>
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

                var cfg = TryLoadMahjongPickConfig(out _);
                TeachModeEnabled = cfg?.TeachMode ?? false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahjongPickTeach] 绑定工位失败: {ex.Message}");
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
            ApplyTeachCommand?.RaiseCanExecuteChanged();
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

        /// <summary>捕获标定转换节点（端口 OutputX/OutputY = 世界坐标 wx/wy）最新输出，
        /// 供示教面板换算 NozzleOffset（不解析日志文本，直接取端口值）。</summary>
        private void Worker_OnNodeExecuted(object sender, NodeEventArgs e)
        {
            try
            {
                var ports = e.Node?.OutputPorts;
                if (ports != null)
                {
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
                }
            }
            catch { /* 捕获失败不影响正常流程 */ }
        }

        #region 配置读写（MahjongPickConfig ⇄ ProcessConfigJson 字段级补丁 + 热更新）

        /// <summary>读取当前绑定工位的有效 MahjongPickConfig（代码默认 + 补丁覆盖）。
        /// 工位未绑定 MahjongPick 过程 / 配置读取失败时返回 null。</summary>
        private MahjongPickConfig TryLoadMahjongPickConfig(out StationConfigModel stationOut)
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
                    // 诊断（2026-09-01）：明确指出是「工位编码对不上」还是「配置库为空」
                    var codes = string.Join(", ", allStations.Select(s => $"'{s.StationCode}'"));
                    AddLog("ERROR",
                        $"配置读取失败：数据库中找不到当前工位（绑定编码='{_stationCode ?? "<null>"}'）。" +
                        $"数据库现有工位: [{(string.IsNullOrEmpty(codes) ? "<空>" : codes)}]。");
                    return null;
                }
                if (!string.Equals(station.ProcessKey, "MahjongPick", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog("ERROR",
                        $"配置读取失败：工位 '{_stationCode}' 的 ProcessKey='{station.ProcessKey ?? "<空>"}' ≠ 'MahjongPick'，" +
                        "请在【工位管理】中为该工位选择 MahjongPick 业务过程后保存。");
                    return null;
                }
                stationOut = station;

                // 旧版「全量快照」→「字段级补丁」一次性迁移（幂等）：
                // 只保留示教拥有的字段，其余被快照冻结的陈旧值全部回归代码默认。
                MigrateLegacySnapshotIfNeeded(station);

                var cfg = ProcessConfigOverlay.LoadEffective<MahjongPickConfig>(station.ProcessConfigJson);
                UpdateOverlayInfo(station.ProcessConfigJson);
                return cfg;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"读取工位过程配置失败: {ex.GetType().Name}: {ex.Message}（Inner: {ex.InnerException?.Message ?? "无"}）");
                return null;
            }
        }

        /// <summary>检测并迁移旧版全量快照：补丁中存在非示教字段时，只保留示教字段重新落库。
        /// 背景：旧版示教保存会序列化整个 Config 对象，把 PickZ/速度/IO 等全部冻结在
        /// LiteDB 里，导致改 MahjongPickConfig.cs 代码默认值零效果。</summary>
        private void MigrateLegacySnapshotIfNeeded(StationConfigModel station)
        {
            var patch = ProcessConfigOverlay.Parse(station.ProcessConfigJson);
            if (patch.Count == 0) return;

            var foreignKeys = patch.Properties().Select(p => p.Name)
                .Where(name => !TeachOwnedFields.Contains(name))
                .ToList();
            if (foreignKeys.Count == 0) return; // 已是纯补丁

            var migrated = new JObject();
            var defaults = JObject.FromObject(new MahjongPickConfig());
            foreach (var name in TeachOwnedFields)
            {
                JToken value;
                if (!patch.TryGetValue(name, out value)) continue;
                // 与代码默认同值的示教字段也不必保留（同值剪枝）
                JToken def;
                if (defaults.TryGetValue(name, out def) && JToken.DeepEquals(value, def)) continue;
                migrated[name] = value;
            }

            station.ProcessConfigJson = migrated.Count == 0 ? null : migrated.ToString(Formatting.Indented);
            if (_configService.SaveStation(station))
            {
                var kept = string.Join(", ", migrated.Properties().Select(p => p.Name));
                AddLog("WARN",
                    $"检测到旧版全量快照（{patch.Count} 字段），已迁移为字段级补丁：" +
                    $"保留示教字段 [{(string.IsNullOrEmpty(kept) ? "无" : kept)}]，" +
                    $"丢弃 {foreignKeys.Count} 个非示教字段（{string.Join(", ", foreignKeys.Take(8))}" +
                    (foreignKeys.Count > 8 ? "…" : "") +
                    "）——这些参数改 MahjongPickConfig.cs 代码默认即生效。");
                HotReloadWorkerProcessConfig(station.ProcessConfigJson);
            }
            else
            {
                AddLog("ERROR", "旧版快照迁移落库失败：本次仍按快照值运行，下次打开面板会再次尝试迁移。");
                // 回滚内存值，保持与库内一致，避免后续补丁写回把快照字段重新冻结
                station.ProcessConfigJson = patch.ToString(Formatting.Indented);
            }
        }

        /// <summary>刷新面板的「参数覆盖」清单展示。</summary>
        private void UpdateOverlayInfo(string overlayJson)
        {
            try
            {
                var diffs = ProcessConfigOverlay.DescribeDifferences<MahjongPickConfig>(overlayJson);
                OverlayInfoText = diffs.Count == 0
                    ? "参数覆盖：无 —— 全部参数以 MahjongPickConfig.cs 代码默认为准（改代码即生效）"
                    : $"参数覆盖 {diffs.Count} 项（优先于代码默认值）：\n· " + string.Join("\n· ", diffs);
            }
            catch (Exception ex)
            {
                OverlayInfoText = $"参数覆盖清单生成失败: {ex.Message}";
            }
        }

        /// <summary>把字段级修改合并进现有补丁、落库并热更新运行中 Worker（重挂业务过程）。
        /// 与代码默认同值的字段自动剪枝（不进补丁，回归代码基线）。</summary>
        private bool TrySaveOverlayFields(StationConfigModel station, IDictionary<string, object> fields)
        {
            var json = ProcessConfigOverlay.SetFields(station.ProcessConfigJson, fields, new MahjongPickConfig());
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

        #region 示教闭环

        /// <summary>示教写入：以「吸嘴对准工件背面图案中心」为基准换算 NozzleOffset 并写回。
        /// 公式（两次独立推导一致，详见 MahjongPickConfig 注释）：
        ///   当前公式(+wx)：NozzleXOffset = X* − wx、NozzleYOffset = Y* − wy（MirrorGuideEnabled=false）
        ///   镜像公式(−wx)：NozzleXOffset = X* + wx、NozzleYOffset = Y* + wy（MirrorGuideEnabled=true）
        /// ⚠ 公式与 MirrorGuideEnabled 必须配套写回，否则落点差 2×wx。</summary>
        private void ApplyTeach()
        {
            if (!_teachWx.HasValue || !_teachWy.HasValue)
            {
                AddLog("ERROR", "示教写入失败：尚未捕获到 wx/wy——请先点【单次触发】完整跑一次视觉流。");
                return;
            }
            if (!double.TryParse((TeachXStarText ?? string.Empty).Trim(), out double xStar) ||
                !double.TryParse((TeachYStarText ?? string.Empty).Trim(), out double yStar))
            {
                AddLog("ERROR", "示教写入失败：X*/Y* 输入不合法（应为数字，单位 mm）。");
                return;
            }

            var cfg = TryLoadMahjongPickConfig(out var station);
            if (cfg == null || station == null)
            {
                AddLog("ERROR", "示教写入失败：当前工位未绑定 MahjongPick 业务过程或配置读取失败。");
                return;
            }

            float nx, ny;
            string formulaName;
            if (TeachUseMirrorFormula)
            {
                nx = (float)(xStar + _teachWx.Value);
                ny = (float)(yStar + _teachWy.Value);
                cfg.MirrorGuideEnabled = true;
                formulaName = "镜像公式(−wx/−wy)";
            }
            else
            {
                nx = (float)(xStar - _teachWx.Value);
                ny = (float)(yStar - _teachWy.Value);
                cfg.MirrorGuideEnabled = false;
                formulaName = "当前公式(+wx/+wy)";
            }

            var oldNx = cfg.NozzleXOffset;
            var oldNy = cfg.NozzleYOffset;

            var fields = new Dictionary<string, object>
            {
                { "NozzleXOffset", nx },
                { "NozzleYOffset", ny },
                { "MirrorGuideEnabled", cfg.MirrorGuideEnabled },
                { "TeachMode", false }
            };

            if (TrySaveOverlayFields(station, fields))
            {
                TeachModeEnabled = false;
                AddLog("INFO",
                    $"✅ 示教写入完成（{formulaName}）：NozzleOffset ({oldNx:F1},{oldNy:F1}) → ({nx:F1},{ny:F1})，" +
                    $"MirrorGuideEnabled={cfg.MirrorGuideEnabled}，TeachMode 已关闭（仅持久化以上示教字段，其余参数仍以代码默认为准）。" +
                    "把工件挪到新位置再点【单次触发】，观察吸嘴落点是否正对工件即可验证。");
            }
        }

        /// <summary>开启/退出示教模式（不改动 NozzleOffset），写回补丁并热更新。</summary>
        private void SetTeachMode(bool enable)
        {
            var cfg = TryLoadMahjongPickConfig(out var station);
            if (cfg == null || station == null)
            {
                AddLog("ERROR", "操作失败：当前工位未绑定 MahjongPick 业务过程或配置读取失败。");
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
                    ? "已开启示教模式：触发只采图算落点，不走位不吸取（NozzleOffset 保持不变）。"
                    : $"已退出示教模式（NozzleOffset 保持 ({cfg.NozzleXOffset:F1},{cfg.NozzleYOffset:F1})）——下次触发将完整执行走位/吸取/放料。");
            }
        }

        /// <summary>清除该工位全部持久化参数覆盖（含示教写回值），所有参数恢复
        /// MahjongPickConfig.cs 代码默认值。用于代码默认值调整后"解冻"被补丁钉住的字段。</summary>
        private void ResetToDefaults()
        {
            var cfg = TryLoadMahjongPickConfig(out var station);
            if (station == null)
            {
                AddLog("ERROR", "重置失败：当前工位未绑定 MahjongPick 业务过程或配置读取失败。");
                return;
            }

            if (string.IsNullOrWhiteSpace(station.ProcessConfigJson))
            {
                UpdateOverlayInfo(null);
                AddLog("INFO", "当前没有任何参数覆盖，全部参数已是代码默认值，无需重置。");
                return;
            }

            var confirm = MessageBox.Show(
                $"将清除工位 '{_stationCode}' 的全部参数覆盖（含示教写回的 NozzleOffset / 公式选择 / TeachMode），\n" +
                "所有参数恢复为 MahjongPickConfig.cs 代码默认值。\n\n确定继续？",
                "确认重置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            station.ProcessConfigJson = null;
            if (!_configService.SaveStation(station))
            {
                AddLog("ERROR", "重置落库失败，配置未变更。");
                return;
            }

            HotReloadWorkerProcessConfig(null);
            var defaults = new MahjongPickConfig();
            TeachModeEnabled = defaults.TeachMode;
            UpdateOverlayInfo(null);
            AddLog("INFO",
                $"✅ 已清除全部参数覆盖：所有参数恢复代码默认（NozzleOffset=({defaults.NozzleXOffset:F1},{defaults.NozzleYOffset:F1})，" +
                $"TeachMode={defaults.TeachMode}）。后续改 MahjongPickConfig.cs 即时生效。");
        }

        #endregion

        private void AddLog(string level, string message)
        {
            Log?.Invoke(level, message);
        }
    }
}
