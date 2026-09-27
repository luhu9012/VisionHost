//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StandaloneVisionProcess.cs
// 说 明: 「独立视觉任务引擎 StandaloneVision」（stage9-2a，2026-09-09）。
//
// 目标任务族（均不依赖工位硬件——无相机/运动控制卡/PLC，只需本地图像源+模型/算法）：
//   · 深度学习推理（检测/分割/分类/异常）—— 本引擎已可端到端跑通（经配方链）
//   · 外观测量 —— stage9-3（2026-09-10）已落地同链承接：读 FitCircle.Radius / FitLine 端点算长度，
//                  用模板 PixelPerMm 换算 mm，按 MeasurementSpecs 标称±公差判 OK/NG。
//   · 识别读取 —— 2026-09-26（FeatureIdentification 首批=颜色）落地：读 ColorIdentify.AreaRatio(%)，
//                  按模板 ConfidenceThreshold（识别读取族语义=面积占比下限%）判 OK/NG。
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
            // 2026-09-25 改多实例收集：模板匹配+几何变换案例一条链上同时有 1 个圆 + 2 条线，
            // FindNode 只取第一个 ⇒ 第二条线会被静默丢掉（尺寸少一项，还看不出来）。
            var measureCircles = FindNodes(NodeType.FitCircle);
            var measureLines = FindNodes(NodeType.FitLine);
            // 识别读取族（2026-09-26，FeatureIdentification 首批=颜色）：链上 ColorIdentify 输出 AreaRatio(%)，
            // 引擎按模板 ConfidenceThreshold（识别读取族语义=面积占比下限%）判 OK/NG —— 见 ApplyIdentifyVerdict。
            var colorNodes = FindNodes(NodeType.ColorIdentify);
            // Blob 分析族（2026-09-26，梯队 B：计数/有无/异物/划痕）：链上 BlobAnalysis 输出 Count，
            // 判据在节点参数 CountMin/CountMax（引擎反射读取，Nodes 程序集不被 Core 引用）。
            var blobNodes = FindNodes(NodeType.BlobAnalysis);
            var readNode = FindNode(NodeType.ReadImageFile);

            bool isMeasureTask = dlNode == null && (measureCircles.Count > 0 || measureLines.Count > 0);
            bool isIdentifyTask = dlNode == null && !isMeasureTask && colorNodes.Count > 0;
            bool isBlobTask = dlNode == null && !isMeasureTask && !isIdentifyTask && blobNodes.Count > 0;
            if (dlNode == null && !isMeasureTask && !isIdentifyTask && !isBlobTask)
            {
                // 四族节点都没有：给明确提示
                throw new InvalidOperationException(
                    "当前独立视觉任务找不到 深度学习推理节点（DlInference / HalconDlInference）、" +
                    "外观测量节点（FitCircle / FitLine）、识别读取节点（ColorIdentify）、也找不到 Blob 分析节点（BlobAnalysis）。" +
                    "请配置配方链：深度学习族加 DL 节点，测量族加测量节点，识别族加颜色/条码/OCR 节点，计数/划痕族加 Blob 节点。");
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
                // 测量族：读链上【全部】FitCircle / FitLine 的输出 → 像素→mm → 逐量按 MeasurementSpecs 判定
                // （2026-09-25：由"单量 + 摘要首个数值"升级为"多量并列 + 派生几何量 + 按名匹配判据"）
                var outcome = BuildMeasurementOutcome(measureCircles, measureLines);
                summary = outcome.Summary;
                measurementPort = outcome.PrimaryPortValue;
                ok = ApplyMeasurementVerdict(outcome, effective);
                Log($"[{_cfg.LogTag}] {progressText} → {(ok ? "✅ OK" : "❌ NG")} | 测量摘要: {summary}");
            }
            else if (isIdentifyTask)
            {
                // 识别读取族（颜色）：读链上【全部】ColorIdentify 的 AreaRatio(%)，
                // 取最小占比（最弱一环）按模板占比阈值判 OK/NG（分拣语义：目标颜色足量 → OK）
                double minRatio = double.PositiveInfinity;
                bool anyValid = false;
                var parts = new List<string>();
                foreach (var cn in colorNodes)
                {
                    double ratio = GetOutValue<double>(cn, "AreaRatio");
                    string name = string.IsNullOrWhiteSpace(cn.DisplayName) ? "颜色识别" : cn.DisplayName;
                    if (double.IsNaN(ratio))
                    {
                        // 节点失败/未产出（判据纪律：算不出 ≠ 0，不静默当 0% 放行）
                        parts.Add($"{name}=未产出");
                        minRatio = double.NegativeInfinity;
                    }
                    else
                    {
                        parts.Add($"{name}={ratio.ToString("F2", CultureInfo.InvariantCulture)}%");
                        if (ratio < minRatio) minRatio = ratio;
                        anyValid = true;
                    }
                }
                summary = "颜色识别 " + string.Join("；", parts);
                ok = ApplyIdentifyVerdict(minRatio, anyValid, effective);
                Log($"[{_cfg.LogTag}] {progressText} → {(ok ? "✅ OK" : "❌ NG")} | 摘要: {summary}");
            }
            else if (isBlobTask)
            {
                // Blob 计数族（计数/有无/异物/划痕，2026-09-26 梯队 B）：读链上【全部】BlobAnalysis 的 Count，
                // 逐节点按其 CountMin/CountMax 判个数区间，全部节点达标 ⇒ OK。
                // ⚠ 判据纪律：这里不走 GetOutValue<double>——节点没跑时端口缺值会被读成 default(0)，
                //   而异物检测恰是 [0,0] 区间 ⇒ "没跑"会伪装成"检出 0 个=OK"静默放行。
                //   改为直接查端口 DataValue：缺值 = 未产出 = 直接 NG。
                var parts = new List<string>();
                bool blobOk = true;
                foreach (var bn in blobNodes)
                {
                    string name = string.IsNullOrWhiteSpace(bn.DisplayName) ? "Blob分析" : bn.DisplayName;
                    var port = bn.OutputPorts?.FirstOrDefault(
                        p => string.Equals(p.PortName, "Count", StringComparison.OrdinalIgnoreCase));
                    double count = port?.DataValue is double d ? d : double.NaN;
                    int minCount = TryGetNodeParamInt(bn, "CountMin") ?? 1;
                    int maxCount = TryGetNodeParamInt(bn, "CountMax") ?? int.MaxValue;

                    if (double.IsNaN(count))
                    {
                        // 节点失败/未运行/未产出：算不出 ≠ 检出 0 个，响亮 NG 不静默放行
                        parts.Add($"{name}=未产出");
                        blobOk = false;
                    }
                    else
                    {
                        bool inRange = count >= minCount && count <= maxCount;
                        if (!inRange) blobOk = false;
                        string rangeText = maxCount >= int.MaxValue
                            ? $"≥{minCount.ToString(CultureInfo.InvariantCulture)}"
                            : $"{minCount.ToString(CultureInfo.InvariantCulture)}~{maxCount.ToString(CultureInfo.InvariantCulture)}";
                        parts.Add($"{name}={count.ToString("0", CultureInfo.InvariantCulture)}个(要求{rangeText})"
                            + (inRange ? "" : " ←超差"));
                    }
                }
                summary = "Blob计数 " + string.Join("；", parts);
                ok = blobOk;
                Log($"[{_cfg.LogTag}] {progressText} → {(ok ? "✅ OK" : "❌ NG")} | 摘要: {summary}");
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

        /// <summary>一条可判定的测量量（摘要里的一段 + 供 MeasurementSpecs 按名取用的键）。</summary>
        private sealed class MeasuredQuantity
        {
            /// <summary>稳定名：既是摘要里的显示名，也是 MeasurementSpecItem.Name 的匹配键</summary>
            public string Name = string.Empty;
            /// <summary>数值（已按模板 PixelPerMm 换算；未配当量时=像素值，单位见 MeasurementOutcome.Unit）</summary>
            public double Value;
            /// <summary>false = 该量本次算不出来（拟合失败/几何退化）</summary>
            public bool Valid = true;
            /// <summary>摘要片段，形如 "圆孔直径=7.49mm"</summary>
            public string Text = string.Empty;
        }

        /// <summary>一次测量的完整产出：摘要 + 主端口值 + 逐量清单。</summary>
        private sealed class MeasurementOutcome
        {
            public string Summary = string.Empty;
            public object PrimaryPortValue;
            public string Unit = "px";
            public List<MeasuredQuantity> Quantities = new List<MeasuredQuantity>();
            /// <summary>有圆/线拟合失败（FitFailureNote 写明原因）⇒ 整单直接 NG，不进公差判定</summary>
            public bool AnyFitFailed;
            public string FitFailureNote = string.Empty;
        }

        /// <summary>
        /// 构造测量产出：读链上【全部】FitCircle / FitLine 的输出，逐量换算并生成「多量并列 + 派生几何量」摘要。
        ///
        /// 量的清单（名字即 MeasurementSpecs 的匹配键，见 ResolveQuantityForSpec）：
        ///   圆孔直径            = 2 × R（取第 1 个 FitCircle）
        ///   线{j}长度           = 第 j 条拟合线的两端点距 —— ⚠ 受卡尺跨度(HalfSpanAlongEdge)限制，
        ///                        这是"拟合段长"而非工件整边长，只作诊断量，别当产品尺寸用
        ///   圆心到边{j}距离      = 圆心 → 第 j 条拟合【直线】的垂距 ← 等价 hdev distance_pl 的派生量
        ///   边{j}到边{j+1}间距   = 相邻两条拟合直线的垂距（两条平行边的间距）
        ///
        /// 单位口径与旧版一致：模板配了 PixelPerMm → mm，否则保持 px（摘要里逐量带 unit，不混口径）。
        /// </summary>
        private MeasurementOutcome BuildMeasurementOutcome(List<FlowNodeBase> circles, List<FlowNodeBase> lines)
        {
            var outcome = new MeasurementOutcome();
            circles = circles ?? new List<FlowNodeBase>();
            lines = lines ?? new List<FlowNodeBase>();

            double? pixelPerMm = TryLoadPixelPerMm(out string unit);
            outcome.Unit = unit;

            // ---- 圆：取第 1 个 FitCircle（外观测量族一条链通常只测 1 个圆）----
            bool hasCircle = false;
            double cRow = double.NaN, cCol = double.NaN, cRadiusPx = double.NaN;
            if (circles.Count > 0)
            {
                cRadiusPx = GetOutValue<double>(circles[0], "Radius");
                cRow = GetOutValue<double>(circles[0], "CenterRow");
                cCol = GetOutValue<double>(circles[0], "CenterCol");
                if (double.IsNaN(cRadiusPx) || cRadiusPx <= 0)
                {
                    outcome.AnyFitFailed = true;
                    outcome.FitFailureNote = $"圆拟合失败（Radius={Fmt(cRadiusPx)}，种子偏离/阈值不当）";
                }
                else
                {
                    hasCircle = true;
                    double dia = Px2Value(cRadiusPx * 2.0, pixelPerMm);
                    outcome.Quantities.Add(new MeasuredQuantity
                    {
                        Name = "圆孔直径",
                        Value = dia,
                        Text = $"圆孔直径={Fmt(dia)}{unit}"
                    });
                }
            }

            // ---- 线：逐条读端点算"拟合段长"；合法的收进 segs 供派生量用 ----
            var segs = new List<double[]>();   // [r1, c1, r2, c2]
            for (int i = 0; i < lines.Count; i++)
            {
                int no = i + 1;
                double r1 = GetOutValue<double>(lines[i], "Row1");
                double c1 = GetOutValue<double>(lines[i], "Col1");
                double r2 = GetOutValue<double>(lines[i], "Row2");
                double c2 = GetOutValue<double>(lines[i], "Col2");
                double lenPx = Math.Sqrt((r2 - r1) * (r2 - r1) + (c2 - c1) * (c2 - c1));
                if (double.IsNaN(lenPx) || lenPx <= 1e-6)
                {
                    outcome.AnyFitFailed = true;
                    outcome.FitFailureNote = $"线{no} 拟合失败（端点重合/异常）";
                    continue;
                }
                segs.Add(new[] { r1, c1, r2, c2 });
                double len = Px2Value(lenPx, pixelPerMm);
                outcome.Quantities.Add(new MeasuredQuantity
                {
                    Name = $"线{no}长度",
                    Value = len,
                    Text = $"线{no}长度={Fmt(len)}{unit}"
                });
            }

            // ---- 派生量①：圆心 → 各拟合直线（垂距）＝ hdev distance_pl ----
            if (hasCircle)
            {
                for (int j = 0; j < segs.Count; j++)
                {
                    double dPx = PerpDistanceToLine(cRow, cCol, segs[j][0], segs[j][1], segs[j][2], segs[j][3]);
                    var q = new MeasuredQuantity { Name = $"圆心到边{j + 1}距离" };
                    if (double.IsNaN(dPx))
                    {
                        q.Valid = false;
                        q.Text = q.Name + "=算不出（拟合直线退化）";
                        outcome.AnyFitFailed = true;
                        outcome.FitFailureNote = q.Name + "算不出（拟合直线退化）";
                    }
                    else
                    {
                        q.Value = Px2Value(dPx, pixelPerMm);
                        q.Text = $"{q.Name}={Fmt(q.Value)}{unit}";
                    }
                    outcome.Quantities.Add(q);
                }
            }

            // ---- 派生量②：相邻两条拟合直线的间距（把后一条线上一点投到前一条直线）----
            for (int j = 0; j + 1 < segs.Count; j++)
            {
                double[] a = segs[j], b = segs[j + 1];
                double dPx = PerpDistanceToLine(b[0], b[1], a[0], a[1], a[2], a[3]);
                var q = new MeasuredQuantity { Name = $"边{j + 1}到边{j + 2}间距" };
                if (double.IsNaN(dPx))
                {
                    q.Valid = false;
                    q.Text = q.Name + "=算不出（拟合直线退化）";
                    outcome.AnyFitFailed = true;
                    outcome.FitFailureNote = q.Name + "算不出（拟合直线退化）";
                }
                else
                {
                    q.Value = Px2Value(dPx, pixelPerMm);
                    q.Text = $"{q.Name}={Fmt(q.Value)}{unit}";
                }
                outcome.Quantities.Add(q);
            }

            if (outcome.Quantities.Count == 0)
            {
                outcome.Summary = "测量节点类型未识别";
                return outcome;
            }

            outcome.Summary = string.Join(" | ", outcome.Quantities.Select(q => q.Text));
            // 主端口值 = 首个有值的量（= 2026-09-25 之前的口径：圆孔直径当主量）
            var primary = outcome.Quantities.FirstOrDefault(q => q.Valid);
            outcome.PrimaryPortValue = primary == null ? null : (object)primary.Value;
            return outcome;
        }

        /// <summary>像素 → 工程量（模板配了 PixelPerMm 才乘；否则原样返回像素值）。</summary>
        private static double Px2Value(double px, double? pixelPerMm)
            => pixelPerMm.HasValue && pixelPerMm.Value > 0 ? px * pixelPerMm.Value : px;

        private static string Fmt(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

        /// <summary>点到【无限直线】的垂距（直线过 P1-P2）；两端点重合致直线退化时返回 NaN。</summary>
        private static double PerpDistanceToLine(double row, double col,
            double r1, double c1, double r2, double c2)
        {
            double dRow = r2 - r1, dCol = c2 - c1;
            double len = Math.Sqrt(dRow * dRow + dCol * dCol);
            if (len < 1e-9) return double.NaN;
            return Math.Abs(dRow * (col - c1) - dCol * (row - r1)) / len;
        }

        /// <summary>读任务模板当量（mm/px）。读不到/未配 → (null, "px")，不影响跑通。</summary>
        private double? TryLoadPixelPerMm(out string unit)
        {
            unit = "px";
            try
            {
                var tplFile = Worker?.TaskTemplateCode;
                if (!string.IsNullOrWhiteSpace(tplFile))
                {
                    var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "TaskLibrary", tplFile.Trim() + ".json");
                    if (File.Exists(file))
                    {
                        var tpl = JsonConvert.DeserializeObject<TaskTemplateInfo>(File.ReadAllText(file));
                        if (tpl?.PixelPerMm.HasValue == true && tpl.PixelPerMm.Value > 0)
                        {
                            unit = "mm";
                            return tpl.PixelPerMm.Value;
                        }
                    }
                }
            }
            catch { /* 模板读不到时维持 px 口径，不影响跑通 */ }
            return null;
        }

        /// <summary>
        /// 识别读取族判据（2026-09-26，颜色分拣语义）：
        ///   1) 模板 ConfidenceThreshold（识别读取族语义=面积占比下限%）已配 → 占比 ≥ 阈值 → OK；
        ///   2) 链上任一 ColorIdentify 未产出（NaN）→ 直接 NG（不静默放行）；
        ///   3) 未配阈值 → 按 EmptySummaryAsOk 兜底（响亮提示去模板里配）。
        /// 注：不走 ApplyVerdict 关键字路径 —— 识别读取族摘要「颜色识别 …=xx%」不构成检测/缺陷语义。
        /// </summary>
        private bool ApplyIdentifyVerdict(double minRatio, bool anyValid, EffectiveVerdict rule)
        {
            if (!anyValid)
            {
                Log("⚠️ 识别读取链无任何 ColorIdentify 占比产出（节点失败/端口未输出）→ NG");
                return false;
            }

            double? threshold = TryLoadIdentifyRatioThreshold();
            if (!threshold.HasValue)
            {
                Log("ℹ️ 识别读取任务模板未配置 ConfidenceThreshold（识别读取族语义=面积占比下限%）" +
                    "——按 EmptySummaryAsOk 兜底放行/拦截：" + (rule.EmptySummaryAsOk ? "OK" : "NG"));
                return rule.EmptySummaryAsOk;
            }

            bool ok = minRatio >= threshold.Value;
            Log($"🎨 颜色占比下限口径：最小占比={minRatio.ToString("F2", CultureInfo.InvariantCulture)}% " +
                $"阈值={threshold.Value.ToString("F2", CultureInfo.InvariantCulture)}% → {(ok ? "✅ OK" : "❌ NG")}");
            return ok;
        }

        /// <summary>读任务模板占比阈值（ConfidenceThreshold）。读不到/未配 → null（由调用方兜底）。</summary>
        private double? TryLoadIdentifyRatioThreshold()
        {
            try
            {
                var tplFile = Worker?.TaskTemplateCode;
                if (!string.IsNullOrWhiteSpace(tplFile))
                {
                    var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "TaskLibrary", tplFile.Trim() + ".json");
                    if (File.Exists(file))
                    {
                        var tpl = JsonConvert.DeserializeObject<TaskTemplateInfo>(File.ReadAllText(file));
                        if (tpl?.ConfidenceThreshold.HasValue == true && tpl.ConfidenceThreshold.Value > 0)
                            return tpl.ConfidenceThreshold.Value;
                    }
                }
            }
            catch { /* 模板读不到时按未配阈值兜底 */ }
            return null;
        }

        /// <summary>
        /// 测量任务判据：把模板 MeasurementSpecs 里【每一条启用项】按名字匹配到本次实测的某个量，
        /// 逐项判 [Nominal±Tol]；全部命中区间 → OK，任一项超出/取不到 → NG。
        ///
        /// 兜底口径（与旧版一致的部分）：
        ///   · 拟合失败（outcome.AnyFitFailed）→ 直接 NG，不进公差（不依赖 MeasurementSpecs）；
        ///   · 模板没有 MeasurementSpecs / 没有启用项 / 全都没配 NominalMm+ToleranceMm → 按 EmptySummaryAsOk；
        ///   · 名字匹配不上的处理见 ResolveQuantityForSpec（单条启用项时退回"摘要首量"= 旧行为，多条时不猜、判 NG）。
        /// </summary>
        private bool ApplyMeasurementVerdict(MeasurementOutcome outcome, EffectiveVerdict rule)
        {
            if (outcome.AnyFitFailed)
            {
                Log("⚠️ 测量链路存在拟合失败 → NG：" + outcome.FitFailureNote);
                return false;
            }

            // 读模板 MeasurementSpecs（缓存，生命周期内不变）
            var specs = TryLoadMeasurementSpecs();
            var enabled = specs == null ? new List<MeasurementSpecItem>() : specs.Where(s => s.Enabled).ToList();
            if (enabled.Count == 0)
            {
                Log("ℹ️ 测量任务模板未配置启用的 MeasurementSpecs——按 EmptySummaryAsOk 兜底放行/拦截（演示态）：" + (rule.EmptySummaryAsOk ? "OK" : "NG"));
                return rule.EmptySummaryAsOk;
            }

            bool anyJudged = false;   // 是否至少有一项真正进了公差判定
            bool allOk = true;
            foreach (var spec in enabled)
            {
                var q = ResolveQuantityForSpec(spec, outcome, enabled.Count == 1);
                if (q == null)
                {
                    Log($"⚠️ 测量项[{spec.Name}]在本次摘要里找不到对应量 → NG。" +
                        $"可选量名：{string.Join("、", outcome.Quantities.Select(x => x.Name))}。" +
                        "请在任务模板 MeasurementSpecs 里把 Name 写成上面某个量名（或对齐 ToolKind 语义）。");
                    allOk = false;
                    continue;
                }
                if (!q.Valid)
                {
                    Log($"⚠️ 测量项[{spec.Name}]对应的量「{q.Name}」本次未算出（拟合失败/几何退化）→ NG");
                    anyJudged = true;
                    allOk = false;
                    continue;
                }
                if (!spec.NominalMm.HasValue || !spec.ToleranceMm.HasValue)
                {
                    Log($"ℹ️ 测量项[{spec.Name}]未配 NominalMm/ToleranceMm——该项跳过判定（不参与 OK/NG）");
                    continue;
                }

                double nominal = spec.NominalMm.Value;
                double tol = spec.ToleranceMm.Value;
                double lo = nominal - tol, hi = nominal + tol;
                bool ok = q.Value >= lo && q.Value <= hi;
                anyJudged = true;
                allOk = allOk && ok;
                Log($"📏 [{spec.Name}] 实测={q.Value.ToString("F3", CultureInfo.InvariantCulture)}{outcome.Unit} " +
                    $"标称={nominal.ToString("F3", CultureInfo.InvariantCulture)}±{tol.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"→ {(ok ? "✅ OK" : "❌ NG")}（区间 [{lo.ToString("F3", CultureInfo.InvariantCulture)}, {hi.ToString("F3", CultureInfo.InvariantCulture)}]）");
            }

            // 一项都没真正判（全都没配 Nominal/Tol）→ 沿用旧版的兜底开关
            if (!anyJudged && allOk)
            {
                Log("ℹ️ 测量任务 MeasurementSpecs 均未配 NominalMm/ToleranceMm——按 EmptySummaryAsOk 兜底：" + (rule.EmptySummaryAsOk ? "OK" : "NG"));
                return rule.EmptySummaryAsOk;
            }
            return allOk;
        }

        /// <summary>
        /// 把一条 MeasurementSpecItem 匹配到本次实测的某个量。匹配顺序（前者命中即返回）：
        ///   ① 名字完全一致（trim + 忽略大小写）
        ///   ② 名字互相包含（spec.Name ⊃ 量名，或 量名 ⊃ spec.Name）
        ///   ③ ToolKind 语义提示（Diameter/FitCircle→直径；PointToLine/Distance→距离；Spacing→间距；FitLine→长度）
        ///   ④ 仅当模板只启用 1 条 MeasurementSpecs 时，退化为「摘要首量」
        ///      （= 2026-09-25 之前的旧行为，保单一尺寸模板的兼容）；
        ///      多条启用项时【不猜】→ 返回 null，由调用方判 NG 并把可选量名写进日志。
        ///      （判据纪律：宁愿响亮失败，也不能把 A 尺寸拿去判 B 的公差还报 OK。）
        /// </summary>
        private MeasuredQuantity ResolveQuantityForSpec(MeasurementSpecItem spec, MeasurementOutcome outcome, bool singleSpecCompat)
        {
            var qs = outcome.Quantities;
            if (qs == null || qs.Count == 0) return null;

            string key = (spec.Name ?? string.Empty).Trim();
            if (key.Length > 0)
            {
                var exact = qs.FirstOrDefault(q => string.Equals(q.Name, key, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
                var contains = qs.FirstOrDefault(q =>
                    q.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf(q.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (contains != null) return contains;
            }

            string kind = (spec.ToolKind ?? string.Empty).Trim();
            if (kind.Length > 0)
            {
                string hint =
                    kind.IndexOf("Diam", StringComparison.OrdinalIgnoreCase) >= 0 ? "直径" :
                    kind.IndexOf("PointToLine", StringComparison.OrdinalIgnoreCase) >= 0 ? "距离" :
                    kind.IndexOf("Spacing", StringComparison.OrdinalIgnoreCase) >= 0 ? "间距" :
                    kind.IndexOf("Dist", StringComparison.OrdinalIgnoreCase) >= 0 ? "距离" :
                    kind.IndexOf("FitCircle", StringComparison.OrdinalIgnoreCase) >= 0 ? "直径" :
                    kind.IndexOf("FitLine", StringComparison.OrdinalIgnoreCase) >= 0 ? "长度" :
                    null;
                if (hint != null)
                {
                    var hit = qs.FirstOrDefault(q => q.Name.Contains(hint));
                    if (hit != null) return hit;
                }
            }

            return singleSpecCompat ? qs[0] : null;
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
