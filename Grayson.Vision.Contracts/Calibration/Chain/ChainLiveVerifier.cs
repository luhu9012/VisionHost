using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Grayson.Vision.Contracts.Calibration.Services;

namespace Grayson.Vision.Contracts.Calibration.Chain
{
    /// <summary>
    /// ★★L3 真机验证（「指哪打哪」）单个验证点的判定结果。
    /// 「指」= 沿链把像素目标解成世界/法兰目标；「打」= 真机到位后实测达成的世界点。
    /// 偏差 = 实测 − 目标；正分量表示"打过头"（往 +X/+Y 偏）。
    /// </summary>
    public sealed class LiveVerifyPointResult
    {
        /// <summary>点序号（1 起，便于现场对照）</summary>
        public int Index { get; set; }

        /// <summary>验证所用的引导相机（PickAnchor 边指向的相机）</summary>
        public string CameraId { get; set; }

        /// <summary>验证所用的工具（主工具）</summary>
        public string ToolId { get; set; }

        /// <summary>操作员点选的像素坐标（标定坐标系，求值用）</summary>
        public double PixelU { get; set; }
        public double PixelV { get; set; }

        /// <summary>沿链解出的世界目标（这就是「指」的位置）</summary>
        public double TargetWorldX { get; set; }
        public double TargetWorldY { get; set; }

        /// <summary>逆解出的法兰目标</summary>
        public double TargetFlangeX { get; set; }
        public double TargetFlangeY { get; set; }

        /// <summary>到位后法兰实际反馈位置</summary>
        public double ActualFlangeX { get; set; }
        public double ActualFlangeY { get; set; }

        /// <summary>到位后实测达成的世界点（「打」到哪）</summary>
        public double ActualWorldX { get; set; }
        public double ActualWorldY { get; set; }

        /// <summary>偏差分量（mm）：实测世界 − 目标世界</summary>
        public double ErrorX { get { return ActualWorldX - TargetWorldX; } }
        public double ErrorY { get { return ActualWorldY - TargetWorldY; } }

        /// <summary>合成偏差（mm）</summary>
        public double ErrorMm { get { return Math.Sqrt(ErrorX * ErrorX + ErrorY * ErrorY); } }

        /// <summary>该点是否通过（≤ 容差）</summary>
        public bool Passed { get; set; }

        /// <summary>未参与判定时的说明（如：测点失败 / 跳过）</summary>
        public string Note { get; set; }

        /// <summary>本点是否拿到了可用于判定的实测值</summary>
        public bool Measured { get; set; }
    }

    /// <summary>
    /// ★★L3 真机验证报告：一次「指哪打哪」会话的全部点位结果 + 放行结论。
    /// 与 L1（门禁可视）/L2（数学校验）并列，是"标定产物能否投产"的最终答案。
    /// </summary>
    public sealed class LiveVerifyReport
    {
        public string StationCode { get; set; }
        public string CameraId { get; set; }
        public string ToolId { get; set; }

        /// <summary>放行容差（mm）：单点合成偏差超过即判该点不过</summary>
        public double ToleranceMm { get; set; } = 0.10;

        public List<LiveVerifyPointResult> Points { get; } = new List<LiveVerifyPointResult>();

        /// <summary>参与判定的点数（Measured=true）</summary>
        public int MeasuredCount
        {
            get { int n = 0; foreach (var p in Points) if (p.Measured) n++; return n; }
        }

        /// <summary>达标点数</summary>
        public int PassedCount
        {
            get { int n = 0; foreach (var p in Points) if (p.Measured && p.Passed) n++; return n; }
        }

        /// <summary>最大合成偏差（mm）；无实测点返回 null</summary>
        public double? MaxErrorMm
        {
            get
            {
                double? max = null;
                foreach (var p in Points)
                {
                    if (!p.Measured) continue;
                    if (max == null || p.ErrorMm > max.Value) max = p.ErrorMm;
                }
                return max;
            }
        }

        /// <summary>平均合成偏差（mm）；无实测点返回 null</summary>
        public double? MeanErrorMm
        {
            get
            {
                double sum = 0; int n = 0;
                foreach (var p in Points) { if (!p.Measured) continue; sum += p.ErrorMm; n++; }
                return n == 0 ? (double?)null : sum / n;
            }
        }

        /// <summary>整体放行结论：至少 1 个实测点，且全部实测点达标。</summary>
        public bool Passed
        {
            get
            {
                if (MeasuredCount == 0) return false;
                foreach (var p in Points)
                    if (p.Measured && !p.Passed) return false;
                return true;
            }
        }

        /// <summary>放行人话结论（给界面直接显示）</summary>
        public string VerdictText
        {
            get
            {
                if (MeasuredCount == 0) return "○ 尚无有效实测点 —— 未形成结论";
                return Passed
                    ? "✓ 通过：全部 " + MeasuredCount + " 点偏差 ≤ " + ToleranceMm.ToString("F3") + " mm，可投产"
                    : "✗ 不通过：" + (MeasuredCount - PassedCount) + " / " + MeasuredCount
                      + " 点超容差（最大 " + (MaxErrorMm.HasValue ? MaxErrorMm.Value.ToString("F3") : "-") + " mm）";
            }
        }
    }

    /// <summary>
    /// ★★L3 真机验证的纯数学内核（不碰硬件、不碰界面，便于离线断言）。
    /// 职责：给定链图 + 点的「像素目标」与「实测世界点」，算出偏差与放行判定。
    ///
    /// 为什么偏差要在【世界域】算，而不是在【法兰域】或【像素域】算：
    ///   · 法兰域偏差含运动到位误差（伺服跟随），会把"标定不准"和"机械没走到"混为一谈；
    ///   · 像素域偏差受像素当量缩放，不同位置不可比；
    ///   · 世界域是标定与生产的共同契约域（工件就在这个世界坐标系里），「指哪打哪」本来就该在这里量。
    /// 因此实测必须来自「把工具真的送到目标位后，再看它实际落在世界的哪一点」，
    /// 而不是读法兰反馈直接减——后者会把到位残差算进标定误差。
    /// </summary>
    public static class ChainLiveVerifier
    {
        /// <summary>默认放行容差（mm）：与工业视觉定位常见 ±0.1mm 同量级，可按工位收紧</summary>
        public const double DefaultToleranceMm = 0.10;

        /// <summary>
        /// 解一个验证点：像素目标 → 世界目标 → 法兰目标（「指」的完整链条）。
        /// fail-closed：链未过门禁/节点缺失/矩阵非法 ⇒ 抛 ChainResolveException。
        /// </summary>
        public static LiveVerifyPointResult PlanPoint(StationCalibGraph graph, string stationCode,
                                                      string cameraId, string toolId,
                                                      double pixelU, double pixelV,
                                                      double uFinalDeg, ChainRobotPose shootPose,
                                                      int index)
        {
            if (graph == null) throw new ChainResolveException("链图为空，无法规划验证点");
            double wx, wy, fx, fy;
            ChainEngine.ResolvePixelToWorld(graph, cameraId, pixelU, pixelV, shootPose,
                                            out wx, out wy, stationCode);
            ChainEngine.ResolveFlangeTarget(graph, toolId, wx, wy, uFinalDeg, out fx, out fy, stationCode);
            return new LiveVerifyPointResult
            {
                Index = index,
                CameraId = cameraId,
                ToolId = toolId,
                PixelU = pixelU,
                PixelV = pixelV,
                TargetWorldX = wx,
                TargetWorldY = wy,
                TargetFlangeX = fx,
                TargetFlangeY = fy,
            };
        }

        /// <summary>把「实测世界点」填进点位并做单点判定。</summary>
        public static void CompletePoint(LiveVerifyPointResult p, double actualWorldX, double actualWorldY,
                                         double toleranceMm)
        {
            if (p == null) return;
            p.ActualWorldX = actualWorldX;
            p.ActualWorldY = actualWorldY;
            p.Measured = true;
            p.Passed = p.ErrorMm <= toleranceMm;
        }

        /// <summary>
        /// 法兰到位后的实测世界点：把法兰实际点沿【工具偏移 + U 旋转】正推到世界。
        /// 这是 ChainEngine.ResolveFlangeTarget 的逆运算（fx = wx − R(Uf)·o ⇒ wx = fx + R(Uf)·o），
        /// 与逆解必须严格互逆，否则"指哪打哪"的偏差会带上一份算法自身的系统误差。
        /// </summary>
        public static void FlangeToWorld(StationCalibGraph graph, string toolId,
                                         double flangeX, double flangeY, double uFinalDeg,
                                         out double worldX, out double worldY)
        {
            var tool = graph.FindTool(toolId);
            if (tool == null) throw new ChainResolveException("工具节点不存在: " + toolId);

            double dx, dy;
            if (tool.IsMaster)
            {
                dx = tool.Offset[0]; dy = tool.Offset[1];
            }
            else
            {
                var master = graph.FindTool(tool.BindMasterToolId);
                if (master == null)
                    throw new ChainResolveException("副工具 " + tool.ToolId + " 绑定的主工具不存在: "
                                                    + (tool.BindMasterToolId ?? "(null)"));
                dx = master.Offset[0] + tool.Offset[0];
                dy = master.Offset[1] + tool.Offset[1];
            }

            // ★U0 基准角（2026-09-30）：与 ChainEngine.ResolveFlangeTarget 共用同一解析——
            //   正推/逆解一旦用了不同基准角，"指哪打哪"的残差里会混进一份算法自身的常量旋转偏差。
            double rad = (uFinalDeg - ChainEngine.OffsetBaseUOf(graph, tool)) * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            worldX = flangeX + (dx * c - dy * s);
            worldY = flangeY + (dx * s + dy * c);
        }

        /// <summary>
        /// 像素当量（mm/px）：从相机矩阵线性部分的真奇异值均值取，用于把世界偏差换算成"图上差多少像素"、
        /// 也用于把像素点选误差折算成世界误差上界（点选 ±1px ⇒ 世界 ± 当量）。
        /// </summary>
        public static double PixelScaleMmPerPx(StationCalibGraph graph, string cameraId)
        {
            var cam = graph.FindCamera(cameraId);
            if (cam == null || cam.Matrix == null || cam.Matrix.Length != 6) return 0;
            double a = cam.Matrix[0], b = cam.Matrix[1];
            double c = cam.Matrix[3], d = cam.Matrix[4];
            double P = a * a + c * c, Q = a * b + c * d, R = b * b + d * d;
            double tr = P + R, det = P * R - Q * Q;
            double disc = tr * tr / 4 - det;
            if (disc < 0) disc = 0;
            double root = Math.Sqrt(disc);
            double l1 = tr / 2 + root, l2 = tr / 2 - root;
            if (l1 < 0) l1 = 0;
            if (l2 < 0) l2 = 0;
            return (Math.Sqrt(l1) + Math.Sqrt(l2)) / 2.0;
        }

        //---------------------------------------------------------------------
        // ★★P0-3（2026-09-29）：L3 结论留痕——此前 Verdict/Report 只在会话内存，
        //   关窗即丢，"能不能投产"的答案留不下来。落盘位置与 Chain.json 同目录
        //   （Recipes\Workstations\{工位}\Calib\LiveVerify_History.json），随链走。
        //---------------------------------------------------------------------

        /// <summary>留痕文件名（与 Chain.json 同目录）</summary>
        public const string HistoryFileName = "LiveVerify_History.json";

        /// <summary>最多保留条数（防无限膨胀；最旧的先丢）</summary>
        public const int HistoryMaxEntries = 20;

        private static readonly JsonSerializerSettings HistoryJsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            Culture = CultureInfo.InvariantCulture,
        };

        /// <summary>一次 L3 验证会话的留痕条目（摘要 + 逐点明细）</summary>
        public sealed class LiveVerifyHistoryEntry
        {
            public string Timestamp { get; set; }        // ISO 8601 本地时间
            public string StationCode { get; set; }
            public string CameraId { get; set; }
            public string ToolId { get; set; }
            public double ToleranceMm { get; set; }
            public double UFinalDeg { get; set; }
            public int PointCount { get; set; }
            public int MeasuredCount { get; set; }
            public int PassedCount { get; set; }
            public double? MaxErrorMm { get; set; }
            public double? MeanErrorMm { get; set; }
            public bool Passed { get; set; }
            public string Verdict { get; set; }
            public List<LiveVerifyPointResult> Points { get; set; }
        }

        /// <summary>
        /// 把一次 L3 验证会话追加进工位留痕文件。Try 语义：失败返回 false + error，
        /// **绝不抛异常、绝不影响验证本身**——留痕是留证，不能变成新的故障点。
        /// 无实测点也允许写（"跑过但没测成"同样有诊断价值）。保留最近 HistoryMaxEntries 条。
        /// </summary>
        public static bool AppendHistory(string stationCode, LiveVerifyReport report,
                                         double uFinalDeg, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(stationCode)) { error = "工位码为空，不留痕"; return false; }
                if (report == null) { error = "报告为空，不留痕"; return false; }

                string dir = CalibrationMatrixStore.GetStationCalibDir(stationCode);
                string path = Path.Combine(dir, HistoryFileName);

                var entries = new List<LiveVerifyHistoryEntry>();
                if (File.Exists(path))
                {
                    try
                    {
                        entries = JsonConvert.DeserializeObject<List<LiveVerifyHistoryEntry>>(
                            File.ReadAllText(path), HistoryJsonSettings) ?? new List<LiveVerifyHistoryEntry>();
                    }
                    catch { entries = new List<LiveVerifyHistoryEntry>(); } // 旧文件损坏：从头记，不阻断
                }

                entries.Add(new LiveVerifyHistoryEntry
                {
                    Timestamp = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                    StationCode = stationCode,
                    CameraId = report.CameraId,
                    ToolId = report.ToolId,
                    ToleranceMm = report.ToleranceMm,
                    UFinalDeg = uFinalDeg,
                    PointCount = report.Points.Count,
                    MeasuredCount = report.MeasuredCount,
                    PassedCount = report.PassedCount,
                    MaxErrorMm = report.MaxErrorMm,
                    MeanErrorMm = report.MeanErrorMm,
                    Passed = report.Passed,
                    Verdict = report.VerdictText,
                    Points = new List<LiveVerifyPointResult>(report.Points),
                });

                while (entries.Count > HistoryMaxEntries)
                    entries.RemoveAt(0);

                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(entries, HistoryJsonSettings));
                return true;
            }
            catch (Exception ex)
            {
                error = "L3 留痕写入失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>读最近一次 L3 留痕（chip/台账显示用）；无文件/损坏/为空返回 null。</summary>
        public static LiveVerifyHistoryEntry ReadLastHistory(string stationCode)
        {
            try
            {
                string path = Path.Combine(CalibrationMatrixStore.GetStationCalibDir(stationCode), HistoryFileName);
                if (!File.Exists(path)) return null;
                var entries = JsonConvert.DeserializeObject<List<LiveVerifyHistoryEntry>>(
                    File.ReadAllText(path), HistoryJsonSettings);
                return (entries != null && entries.Count > 0) ? entries[entries.Count - 1] : null;
            }
            catch { return null; }
        }
    }
}
