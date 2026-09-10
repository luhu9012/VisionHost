//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StandaloneVisionProcess.cs
// 说 明: 「独立视觉任务引擎 StandaloneVision」（stage9-2a，2026-09-09）。
//
// 目标任务族（均不依赖工位硬件——无相机/运动控制卡/PLC，只需本地图像源+模型/算法）：
//   · 深度学习推理（检测/分割/分类/异常）—— 本引擎已可端到端跑通（经配方链）
//   · 外观测量 —— stage9-3（2026-09-10）已落地同链承接：读 FitCircle.Radius / FitLine 端点算长度，
//                  用模板 PixelPerMm 换算 mm，按 MeasurementSpecs 标称±公差判 OK/NG。
//
// 运行形态（与既有工位运行时零侵入）：
//   工位绑定配方（ReadImageFile[文件夹批处理] → 视觉节点[DlInference 或 FitCircle/FitLine]） +
//   ProcessKey=StandaloneVision + 触发源=定时器 → 工位监视【▶ 启动】后按周期自动：
//   取图 → 视觉链 → 判据 → CSV/日志 → OK/NG 计数。
//   每周期由 Worker.TriggerOnceAsync 驱动 RunAsync 一次（与取放引擎同一套互斥/统计/停止语义）。
//
// 判据（引擎侧 v1，务实版；配方尾部可叠 MathLogic 节点做更强判定）：
//   摘要含 OkKeyword("未检出") → OK；含 NgKeyword("NG") → NG；
//   检测/分割/异常任务且 NgOnDetect=true：摘要非"未检出/无…" → NG；
//   均未命中 → 按 EmptySummaryAsOk（默认 NG 并提示配置关键词）。
//===================================================================================
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>独立视觉任务过程（纯软件运行，无硬件依赖）。</summary>
    public class StandaloneVisionProcess : StationProcessBase
    {
        public const string ProcessKeyValue = "StandaloneVision";

        // DlInference 节点输出端口（与 DlInferenceExecutor 声明一致）
        private const string PortOutResults = "DetectionResults";
        private const string PortOutRegion = "DefectRegion";

        private readonly StandaloneVisionConfig _cfg;

        public StandaloneVisionProcess(StationWorker worker, StandaloneVisionConfig cfg)
            : base(worker, "")   // 独立纯软件任务：无运动卡/轴别名
        {
            _cfg = cfg ?? new StandaloneVisionConfig();
        }

        public override string ProcessKey => ProcessKeyValue;

        public override async Task<bool> RunAsync(CancellationToken token = default)
        {
            Log($"[{_cfg.LogTag}] 周期开始（纯软件本地图像源 → 视觉链 → 判据）...");

            // ---------- 1) 前置校验：执行链就绪 ----------
            if (Worker.ActiveExecutionChain == null || Worker.ActiveExecutionChain.Count == 0)
                throw new InvalidOperationException("独立视觉任务要求工位已绑定配方（视觉链为空）。请在工位工作台绑定配方后重试。");

            // 兼容两类推理节点：ONNX（DlInference）与 HALCON 原生 DL（HalconDlInference，端口/参数语义一致）
            var dlNode = FindNode(NodeType.DlInference) ?? FindNode(NodeType.HalconDlInference);
            // 测量节点（stage9-3：FitCircle 直接给 Radius；FitLine 给 Row1/Col1/Row2/Col2 端点，由引擎算长度）
            var measureCircle = FindNode(NodeType.FitCircle);
            var measureLine = FindNode(NodeType.FitLine);
            var readNode = FindNode(NodeType.ReadImageFile);

            bool isMeasureTask = dlNode == null && (measureCircle != null || measureLine != null);
            if (dlNode == null && !isMeasureTask)
            {
                // 既无 DL 节点也无测量节点：给明确提示
                throw new InvalidOperationException(
                    "当前独立视觉任务找不到 深度学习推理节点（DlInference / HalconDlInference）" +
                    "也找不到 外观测量节点（FitCircle / FitLine）。请配置配方链：深度学习族加 DL 节点，外观测量族加测量节点。");
            }
            if (readNode == null)
                Log("⚠️ 配方链未找到 图像读取(ReadImageFile) 节点——独立任务请以 本地文件夹批处理 图像源起始；继续尝试按现有链执行。");

            // ---------- 2) 跑视觉链（直连调度器完整链，异常/设备失败在此抛出 → Worker Faulted） ----------
            token.ThrowIfCancellationRequested();
            await RunVisionFlowAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // ---------- 3) 读取结果 & 判据 ----------
            var fileName = TryGetNodeParamString(readNode, "SelectedFilePath");
            var progressText = BuildProgressText(readNode, fileName);
            var effective = ResolveEffectiveVerdict();

            string summary;
            bool ok;
            object region = null;
            int? taskTypeInt = null;
            object measurementPort = null;

            if (isMeasureTask)
            {
                // 测量族：读 FitCircle.Radius / FitLine 端点 → 像素→mm → 按 MeasurementSpecs 第一项判 OK/NG
                var (mSummary, mPort) = BuildMeasurementSummaryAndPort(measureCircle, measureLine);
                summary = mSummary;
                measurementPort = mPort;
                ok = ApplyMeasurementVerdict(summary, effective);
                Log($"[{_cfg.LogTag}] {progressText} → {(ok ? "✅ OK" : "❌ NG")} | 测量摘要: {summary}");
            }
            else
            {
                // 深度学习族：原路径不变
                summary = GetOutValue<string>(dlNode, PortOutResults) ?? string.Empty;
                region = GetOutValue<object>(dlNode, PortOutRegion);
                taskTypeInt = TryGetNodeParamInt(dlNode, "TaskType");
                ok = ApplyVerdict(taskTypeInt, summary, effective);
                Log($"[{_cfg.LogTag}] {progressText} → {(ok ? "✅ OK" : "❌ NG")} | 摘要: {summary}" +
                    (region != null ? " | 检出 Region 已输出" : ""));
            }

            // ---------- 4) 周期结果：CSV + 日志 ----------
            var csvLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff},{Worker.StationId},{EscapeCsv(fileName)},{EscapeCsv(summary)},{(ok ? "OK" : "NG")}";
            if (_cfg.EnableCsv && !string.IsNullOrWhiteSpace(_cfg.OutputCsvPath))
                AppendCsv(_cfg.OutputCsvPath.Replace("{StationId}", Worker.StationId), csvLine);

            return ok;
        }

        // ==================== 外观测量分支（stage9-3） ====================

        /// <summary>
        /// 构造测量摘要 + 返回首个数值端口值（用于 CSV/日志附加判据细节）
        /// 读 FitCircle.Radius（亚像素，px）或 FitLine 端点对算长度 → 用模板 PixelPerMm 换 mm（null 则留像素口径）
        /// 摘要形如 "圆 直径=xx.x mm RMS=x.xx px 点数=n" / "线段 长度=xx.x mm 端点(R1,C1)-(R2,C2) Score=x.xx"
        /// </summary>
        private (string summary, object primaryPortValue) BuildMeasurementSummaryAndPort(FlowNodeBase circle, FlowNodeBase line)
        {
            double? pixelPerMm = null;
            string unit = "px";
            try
            {
                var tplRule = TryLoadTemplateVerdict();
                var tplFile = Worker?.TaskTemplateCode;
                if (!string.IsNullOrWhiteSpace(tplFile))
                {
                    var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "TaskLibrary", tplFile.Trim() + ".json");
                    if (File.Exists(file))
                    {
                        var tpl = JsonConvert.DeserializeObject<TaskTemplateInfo>(File.ReadAllText(file));
                        if (tpl?.PixelPerMm.HasValue == true) { pixelPerMm = tpl.PixelPerMm.Value; unit = "mm"; }
                    }
                }
            }
            catch { /* 模板读不到时维持 px 口径，不影响跑通 */ }

            if (circle != null)
            {
                double radiusPx = GetOutValue<double>(circle, "Radius");
                double centerRow = GetOutValue<double>(circle, "CenterRow");
                double centerCol = GetOutValue<double>(circle, "CenterCol");
                if (double.IsNaN(radiusPx) || radiusPx <= 0)
                    return ($"圆 拟合失败（Radius={radiusPx}，种子偏离/阈值不当）", radiusPx);
                double diameterPx = radiusPx * 2.0;
                double measuredMm = pixelPerMm.HasValue && pixelPerMm.Value > 0 ? diameterPx * pixelPerMm.Value : diameterPx;
                return ($"圆 直径={measuredMm.ToString("F2", CultureInfo.InvariantCulture)} {unit} " +
                        $"圆心=({centerRow.ToString("F1", CultureInfo.InvariantCulture)},{centerCol.ToString("F1", CultureInfo.InvariantCulture)}) " +
                        $"R={radiusPx.ToString("F2", CultureInfo.InvariantCulture)}px",
                        measuredMm);
            }

            if (line != null)
            {
                double r1 = GetOutValue<double>(line, "Row1");
                double c1 = GetOutValue<double>(line, "Col1");
                double r2 = GetOutValue<double>(line, "Row2");
                double c2 = GetOutValue<double>(line, "Col2");
                double score = GetOutValue<double>(line, "Score");
                double lenPx = Math.Sqrt((r2 - r1) * (r2 - r1) + (c2 - c1) * (c2 - c1));
                if (double.IsNaN(lenPx) || lenPx <= 0)
                    return ($"线段 拟合失败（端点重合/异常）", lenPx);
                double measuredMm = pixelPerMm.HasValue && pixelPerMm.Value > 0 ? lenPx * pixelPerMm.Value : lenPx;
                return ($"线段 长度={measuredMm.ToString("F2", CultureInfo.InvariantCulture)} {unit} " +
                        $"端点=({r1.ToString("F1", CultureInfo.InvariantCulture)},{c1.ToString("F1", CultureInfo.InvariantCulture)})" +
                        $"-({r2.ToString("F1", CultureInfo.InvariantCulture)},{c2.ToString("F1", CultureInfo.InvariantCulture)}) " +
                        $"Score={score.ToString("F2", CultureInfo.InvariantCulture)}",
                        measuredMm);
            }

            return ("测量节点类型未识别", null);
        }

        /// <summary>
        /// 测量任务判据（复用 MeasurementSpecs 第一项 Enabled 的 NominalMm/ToleranceMm）：
        ///   值在 [Nominal-Tol, Nominal+Tol] 内 → OK，否则 NG；
        ///   摘要含 "失败/异常/重合/未识别" → NG（不依赖 MeasurementSpecs 也兜底拦截异常流）；
        ///   未配 MeasurementSpecs → 按 EmptySummaryAsOk 放行（兜底，不阻塞新接入）。
        /// </summary>
        private bool ApplyMeasurementVerdict(string summary, EffectiveVerdict rule)
        {
            // 异常路径兜底：摘要里出现失败/异常字样即 NG（与 FitCircle 失败日志"拟合失败"对齐）
            if (string.IsNullOrEmpty(summary) ||
                IndexOfIgnoreCase(summary, "失败") >= 0 ||
                IndexOfIgnoreCase(summary, "异常") >= 0 ||
                IndexOfIgnoreCase(summary, "未识别") >= 0)
            {
                Log("⚠️ 测量任务摘要异常或链路失败 → NG（请检查 FitCircle/FitLine 种子与阈值）");
                return false;
            }

            // 读模板 MeasurementSpecs（缓存，生命周期内不变）
            var specs = TryLoadMeasurementSpecs();
            if (specs == null || specs.Count == 0)
            {
                Log("ℹ️ 测量任务模板未配置 MeasurementSpecs——按 EmptySummaryAsOk 兜底放行/拦截（演示态）：" + (rule.EmptySummaryAsOk ? "OK" : "NG"));
                return rule.EmptySummaryAsOk;
            }
            var first = specs.FirstOrDefault(s => s.Enabled);
            if (first == null)
            {
                Log("ℹ️ 测量任务模板 MeasurementSpecs 无 Enabled 项——按 EmptySummaryAsOk 兜底：" + (rule.EmptySummaryAsOk ? "OK" : "NG"));
                return rule.EmptySummaryAsOk;
            }
            if (!first.NominalMm.HasValue || !first.ToleranceMm.HasValue)
            {
                Log($"ℹ️ 测量项 [{first.Name}] 未配 NominalMm/ToleranceMm——按 EmptySummaryAsOk 兜底：" + (rule.EmptySummaryAsOk ? "OK" : "NG"));
                return rule.EmptySummaryAsOk;
            }

            // 从摘要里解析数值（"圆 直径=12.34 mm ..." 或 "线段 长度=12.34 mm ..."），取首个 "数字+单位"
            if (!TryParseMeasurementValue(summary, out double measured))
            {
                Log("⚠️ 测量任务摘要未能解析数值（预期形如 直径=12.34 mm）→ NG");
                return false;
            }
            double nominal = first.NominalMm.Value;
            double tol = first.ToleranceMm.Value;
            double lo = nominal - tol, hi = nominal + tol;
            bool ok = measured >= lo && measured <= hi;
            Log($"📏 测量项[{first.Name}] 实测={measured.ToString("F3", CultureInfo.InvariantCulture)} 标称={nominal.ToString("F3", CultureInfo.InvariantCulture)}±{tol.ToString("F3", CultureInfo.InvariantCulture)} → {(ok ? "✅ OK" : "❌ NG")}（区间 [{lo.ToString("F3", CultureInfo.InvariantCulture)}, {hi.ToString("F3", CultureInfo.InvariantCulture)}]）");
            return ok;
        }

        private List<MeasurementSpecItem> _cachedSpecs;
        private List<MeasurementSpecItem> TryLoadMeasurementSpecs()
        {
            if (_cachedSpecs != null) return _cachedSpecs;
            try
            {
                var code = Worker?.TaskTemplateCode;
                if (string.IsNullOrWhiteSpace(code)) { _cachedSpecs = new List<MeasurementSpecItem>(); return _cachedSpecs; }
                var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "TaskLibrary", code.Trim() + ".json");
                if (!File.Exists(file)) { _cachedSpecs = new List<MeasurementSpecItem>(); return _cachedSpecs; }
                var tpl = JsonConvert.DeserializeObject<TaskTemplateInfo>(File.ReadAllText(file));
                _cachedSpecs = tpl?.MeasurementSpecs ?? new List<MeasurementSpecItem>();
            }
            catch (Exception ex)
            {
                Log("⚠️ 测量项清单读取失败（按空配置兜底）: " + ex.Message);
                _cachedSpecs = new List<MeasurementSpecItem>();
            }
            return _cachedSpecs;
        }

        private static bool TryParseMeasurementValue(string summary, out double value)
        {
            value = 0;
            // 形如 "直径=12.34 mm" / "长度=12.34 mm" —— 取首个 "数字" 段
            int eq = summary.IndexOf('=');
            if (eq < 0) return false;
            int i = eq + 1;
            while (i < summary.Length && (summary[i] == ' ' || summary[i] == ' ')) i++;
            int start = i;
            while (i < summary.Length && (char.IsDigit(summary[i]) || summary[i] == '.' || summary[i] == '-' || summary[i] == '+')) i++;
            if (i <= start) return false;
            return double.TryParse(summary.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // ==================== 判据（v1 务实版；模板级 VerdictRule > 工位引擎配置 > 代码默认） ====================

        /// <summary>生效判定规则（合并后的不可变快照）</summary>
        private sealed class EffectiveVerdict
        {
            public string OkKeyword, NgKeyword, OkClassName, NgClassName;
            public bool NgOnDetect = true, EmptySummaryAsOk;
        }

        private bool _templateLoaded;
        private TaskTemplateVerdictView _templateVerdict; // 模板级覆盖（null=模板无规则）

        /// <summary>
        /// 取生效判定规则 = 工位引擎配置（ProcessConfigJson 补丁叠加后的 cfg）为基底，
        /// 模板级 VerdictRule（Config\TaskLibrary\{TaskTemplateCode}.json）显式字段再覆盖。
        /// 模板文件只读一次并缓存（过程生命周期内不变）。
        /// </summary>
        private EffectiveVerdict ResolveEffectiveVerdict()
        {
            var eff = new EffectiveVerdict
            {
                OkKeyword = string.IsNullOrWhiteSpace(_cfg.OkKeyword) ? null : _cfg.OkKeyword,
                NgKeyword = string.IsNullOrWhiteSpace(_cfg.NgKeyword) ? null : _cfg.NgKeyword,
                OkClassName = string.IsNullOrWhiteSpace(_cfg.OkClassName) ? null : _cfg.OkClassName,
                NgClassName = string.IsNullOrWhiteSpace(_cfg.NgClassName) ? null : _cfg.NgClassName,
                NgOnDetect = _cfg.NgOnDetect,
                EmptySummaryAsOk = _cfg.EmptySummaryAsOk
            };

            var tplRule = TryLoadTemplateVerdict();
            if (tplRule == null) return eff;

            if (!string.IsNullOrWhiteSpace(tplRule.OkKeyword)) eff.OkKeyword = tplRule.OkKeyword.Trim();
            if (!string.IsNullOrWhiteSpace(tplRule.NgKeyword)) eff.NgKeyword = tplRule.NgKeyword.Trim();
            if (!string.IsNullOrWhiteSpace(tplRule.OkClassName)) eff.OkClassName = tplRule.OkClassName.Trim();
            if (!string.IsNullOrWhiteSpace(tplRule.NgClassName)) eff.NgClassName = tplRule.NgClassName.Trim();
            if (tplRule.NgOnDetect.HasValue) eff.NgOnDetect = tplRule.NgOnDetect.Value;
            if (tplRule.EmptySummaryAsOk.HasValue) eff.EmptySummaryAsOk = tplRule.EmptySummaryAsOk.Value;
            return eff;
        }

        private TaskTemplateVerdictView TryLoadTemplateVerdict()
        {
            if (_templateLoaded) return _templateVerdict;
            _templateLoaded = true;
            try
            {
                var code = Worker?.TaskTemplateCode;
                if (string.IsNullOrWhiteSpace(code))
                {
                    // 判据软链断点留痕：模板规则依赖 TaskTemplateCode 贯通（仅工位管理保存/模板部署路径会写），
                    // 配方下发/手工装配路径可能为空 → 明确提示，判据回落到引擎配置/内置词表兜底。
                    Log("ℹ️ 工位未绑定任务模板代码（TaskTemplateCode 为空）——模板级判据规则未生效；" +
                       "将按工位引擎配置/内置分类词表判定（分类任务建议在工位管理中绑定任务模板或显式配置判据）");
                    return null;
                }
                var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "TaskLibrary", code.Trim() + ".json");
                if (!File.Exists(file))
                {
                    Log($"⚠️ 任务模板判据文件不存在: {file}（TaskTemplateCode={code.Trim()}）——模板级判据规则未生效，按工位引擎配置/内置分类词表判定");
                    return null;
                }
                var tpl = JsonConvert.DeserializeObject<TaskTemplateInfo>(File.ReadAllText(file));
                var rule = tpl?.VerdictRule;
                if (rule == null) return null;
                _templateVerdict = new TaskTemplateVerdictView
                {
                    OkKeyword = rule.OkKeyword, NgKeyword = rule.NgKeyword,
                    OkClassName = rule.OkClassName, NgClassName = rule.NgClassName,
                    NgOnDetect = rule.NgOnDetect, EmptySummaryAsOk = rule.EmptySummaryAsOk
                };
                return _templateVerdict;
            }
            catch (Exception ex)
            {
                Log($"⚠️ 模板判据读取失败（按工位引擎配置/默认执行）: {ex.Message}");
                return null;
            }
        }

        private class TaskTemplateVerdictView
        {
            public string OkKeyword, NgKeyword, OkClassName, NgClassName;
            public bool? NgOnDetect, EmptySummaryAsOk;
        }

        /// <summary>
        /// 判定顺序（v1 务实版）：
        ///   1) 分类类名前缀：摘要以 "{OkClassName}:" 开头 → OK；"{NgClassName}:" 开头 → NG（镁片 ok/ng 场景）；
        ///   2) 摘要为空 → EmptySummaryAsOk（默认 NG，提示链路可能未产出）；
        ///   3) 摘要含 OkKeyword（默认"未检出"）→ OK；
        ///   4) 检测(1)/分割(2)/异常(3)且 NgOnDetect=true：摘要含"检出/异常"（且未命中 NG 词）→ NG；
        ///   5) 摘要含 NgKeyword（默认"NG"）→ NG；
        ///   6) 均未命中 → EmptySummaryAsOk（默认 NG，提示配置关键词）。
        /// </summary>
        private bool ApplyVerdict(int? taskTypeInt, string summary, EffectiveVerdict rule)
        {
            var s = summary ?? string.Empty;

            // 分类 Top-1 类名前缀判据（如 "ok: 99.1%" / "ng: 87.3%"）
            if (!string.IsNullOrEmpty(rule.OkClassName) && StartsWithClass(s, rule.OkClassName)) return true;
            if (!string.IsNullOrEmpty(rule.NgClassName) && StartsWithClass(s, rule.NgClassName)) return false;

            if (s.Length == 0)
            {
                Log("⚠️ 推理结果摘要为空——链路可能未产出（默认" + (rule.EmptySummaryAsOk ? "OK" : "NG") + "，请检查节点与模型路径）");
                return rule.EmptySummaryAsOk;
            }

            if (!string.IsNullOrEmpty(rule.OkKeyword) && IndexOfIgnoreCase(s, rule.OkKeyword) >= 0)
                return true;

            bool ngByKeyword = !string.IsNullOrEmpty(rule.NgKeyword)
                               && IndexOfIgnoreCase(s, rule.NgKeyword) >= 0;

            bool detectedLikeTask = taskTypeInt.HasValue && (taskTypeInt.Value == 1 || taskTypeInt.Value == 2 || taskTypeInt.Value == 3);
            bool hasDetectWord = IndexOfIgnoreCase(s, "检出") >= 0 || IndexOfIgnoreCase(s, "异常") >= 0;
            if (detectedLikeTask && rule.NgOnDetect && hasDetectWord && !ngByKeyword)
                ngByKeyword = true;

            if (ngByKeyword) return false;

            // 5.5) 分类形态摘要自判定兜底（OkClassName/NgClassName 均未显式配置时）：
            //      镁片等分类模型摘要是 "{Top1类名}: {置信度}"（"ok: 72.8 %"/"ng: 100.0 %"），
            //      本兜底不依赖 TaskTemplateCode→Config\TaskLibrary json 贯通（配方下发/手工装配常断链）
            //      也能判对；分割摘要 "检出 破裂…/未检出" 无冒号类名前缀、不落入本兜底。
            //      词表与 HalconDlInferenceExecutor 预览着色同源维护。
            if (rule.OkClassName == null && rule.NgClassName == null && TryClassifyLabelPrefix(s, out bool okByLabel))
            {
                Log("ℹ️ 摘要命中内置分类词表（" + (okByLabel ? "OK" : "NG") + " 类前缀）→ 判定" + (okByLabel ? "OK" : "NG") + "；如需精确控制请配置 OkClassName/NgClassName 或绑定任务模板");
                return okByLabel;
            }

            Log("ℹ️ 判据未命中（摘要: " + s + "）——按 EmptySummaryAsOk 默认放行/拦截；可通过 NgKeyword/NgOnDetect 配置该任务判定规则");
            return rule.EmptySummaryAsOk;
        }

        /// <summary>
        /// 分类形态摘要 "{label}:" 的语义兜底：label 精确命中 OK/NG 词表 → 给出判定。
        /// 镁片模型类名恰为 ok/ng；药片等分割摘要不含冒号类名前缀，不会误入。
        /// </summary>
        private static bool TryClassifyLabelPrefix(string summary, out bool isOk)
        {
            isOk = false;
            if (string.IsNullOrEmpty(summary)) return false;
            int colon = summary.IndexOf(':');
            if (colon < 0) colon = summary.IndexOf('：');
            if (colon <= 0) return false;
            string label = summary.Substring(0, colon).Trim().ToLowerInvariant();
            if (label.Length == 0) return false;

            string[] ngWords = { "ng", "bad", "fail", "failed", "defect", "defective", "reject", "rejected", "异常", "不良", "缺陷", "失败", "ng" };
            string[] okWords = { "ok", "good", "pass", "passed", "accept", "accepted", "合格", "良", "良品", "正常", "好" };

            foreach (var w in ngWords)
                if (label == w) { isOk = false; return true; }
            foreach (var w in okWords)
                if (label == w) { isOk = true; return true; }
            return false;
        }

        private static bool StartsWithClass(string summary, string className)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(summary)) return false;
            return summary.StartsWith(className + ":", StringComparison.OrdinalIgnoreCase)
                || summary.StartsWith(className + "：", StringComparison.OrdinalIgnoreCase);
        }

        // ==================== 辅助：节点参数反射读取（Core 不引 Nodes 程序集，故走反射） ====================

        private static int IndexOfIgnoreCase(string source, string keyword)
        {
            return source == null ? -1 : source.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        }

        private string TryGetNodeParamString(FlowNodeBase node, string propName)
        {
            try
            {
                if (node?.ParameterModel == null) return null;
                var v = node.ParameterModel.GetType().GetProperty(propName)?.GetValue(node.ParameterModel, null);
                return v as string;
            }
            catch { return null; }
        }

        private int? TryGetNodeParamInt(FlowNodeBase node, string propName)
        {
            try
            {
                if (node?.ParameterModel == null) return null;
                var prop = node.ParameterModel.GetType().GetProperty(propName);
                if (prop == null) return null;
                var v = prop.GetValue(node.ParameterModel, null);
                if (v == null) return null;
                return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        private string BuildProgressText(FlowNodeBase readNode, string fileName)
        {
            try
            {
                if (readNode?.ParameterModel == null) return string.IsNullOrWhiteSpace(fileName) ? "图像源已取图" : $"图像: {Path.GetFileName(fileName)}";
                var pm = readNode.ParameterModel;
                var idx = Convert.ToInt32(pm.GetType().GetProperty("CurrentImageIndex")?.GetValue(pm, null), CultureInfo.InvariantCulture);
                var fileItems = pm.GetType().GetProperty("FileItems")?.GetValue(pm, null) as System.Collections.IList;
                var total = fileItems?.Count ?? 0;
                if (total > 0)
                    return $"图像 {Math.Min(idx + 1, total)}/{total}: {Path.GetFileName(fileName)}";
            }
            catch { }
            return string.IsNullOrWhiteSpace(fileName) ? "图像源已取图" : $"图像: {Path.GetFileName(fileName)}";
        }

        private static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(",", "，").Replace("\r", " ").Replace("\n", " ");
        }

        private void AppendCsv(string path, string line)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                bool needHeader = !File.Exists(Path.GetFullPath(path));
                var head = needHeader ? "时间,工位,图像,结果摘要,判定" + Environment.NewLine : "";
                File.AppendAllText(Path.GetFullPath(path), head + line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Log($"⚠️ 周期结果写 CSV 失败({path}): {ex.Message}");
            }
        }

        private new void Log(string message)
        {
            LogBus.Info("StandaloneVision", $"[{Worker.StationId}] {message}");
        }
    }
}
