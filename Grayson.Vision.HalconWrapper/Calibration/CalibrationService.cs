using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.HalconWrapper.Core; // AsWindow()：从显示上下文取 IHalconWindowSurface 直通通道
using Grayson.Vision.HalconWrapper.Templates; // TemplateManager.MatchWithDatum：标定模板匹配特征复用全局模板库（datum 唯一口径）
using HalconDotNet;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    public class CalibrationService : ICalibrationService
    {
        /// <summary>
        /// 标定显示上下文（HDevelop 式场景绘制）。
        /// 非空时，特征提取走一步画一步：每个算子执行后立即把结果提交到视图场景
        /// （ROI 搜索框 / 候选区域 / 亚像素轮廓 / 拟合圆 / 特征点十字 / 文字标注），
        /// 让操作员实时看到"特征在哪、怎么找到的"；缩放/平移/拖动后由显示层整体重放。
        /// 生命周期约定见 Submit/SubmitRegion/BeginScene 的注释。
        /// </summary>
        public ICalibrationDisplayContext DisplayContext { get; set; }

        /// <summary>
        /// 调试开关：是否把特征提取的中间过程结果（阈值分割区域 / 连通域 / 骨架 / 骨架轮廓等）
        /// 叠加绘制到视图窗口。默认 true 便于现场调试；
        /// 置 false 即可一键关闭全部过程层，最终结果层（ROI / 拟合圆 / 拟合直线 /
        /// 中心十字 / 文字标注）不受影响。
        /// </summary>
        public bool DebugDrawProcessEnabled { get; set; } = true;

        /// <summary>
        /// 特征提取算子参数（阈值/圆度/面积/搜索半径/亚像素阈值等）。
        /// 默认值与改造前硬编码参数一致；标定向导"特征配置"步骤可实时调节。
        /// </summary>
        public FeatureExtractOptions ExtractOptions { get; set; } = new FeatureExtractOptions();

        /// <summary>
        /// 最近一次特征提取（预览/调参重试/采样）的质量报告（含匹配分 0~100 与成分明细）。
        /// 每次提取结束更新；提取未执行过时为 null。线程模型：提取统一在后台单飞任务内
        /// （标定向导 _samplingBusy 门闩），无并发读写；UI 侧应在提取调用返回后读取。
        /// </summary>
        public FeatureMatchReport LastMatchReport { get; private set; }

        /// <summary>clamp 0..1</summary>
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>分数等级文字</summary>
        private static string ScoreVerdict(double score)
        {
            if (score >= 85) return "极佳";
            if (score >= 70) return "良好";
            if (score >= 55) return "可用";
            return "偏弱";
        }

        /// <summary>
        /// 组装圆 Mark 匹配质量报告（成功路径）：从几何筛选后的候选区域重新读取
        /// 圆度/面积/中心，对照选中点、参考半径、期望位置综合评分。
        /// 失败时置 Score=0、Detail=失败诊断（供向导界面直观显示，不参与控制流）。
        /// </summary>
        private FeatureMatchReport ComposeCircleReport(
            HObject selectedRegions, int candidateCount,
            double finalPixelX, double finalPixelY, double chosenRadius,
            double expectedPx, double expectedPy, double searchRadius,
            bool usedFallback, bool success, string failDetail, string pointTag)
        {
            var report = new FeatureMatchReport
            {
                Success = success,
                PixelX = finalPixelX,
                PixelY = finalPixelY,
                CandidateCount = candidateCount,
                UsedFallback = usedFallback,
                Detail = failDetail
            };

            var sb = new System.Text.StringBuilder();
            double circ = 0.85, radiusVal = chosenRadius, areaVal = 0;
            double nearestOtherDist = double.MaxValue;
            try
            {
                if (success && candidateCount > 0 && selectedRegions != null && selectedRegions.IsInitialized())
                {
                    HOperatorSet.AreaCenter(selectedRegions, out HTuple areas, out HTuple rows, out HTuple cols);
                    HOperatorSet.RegionFeatures(selectedRegions, "circularity", out HTuple circs);
                    int n = areas.Length;
                    int chosenIdx = -1;
                    double bestD = double.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        double dx = cols[i].D - finalPixelX;
                        double dy = rows[i].D - finalPixelY;
                        double d = dx * dx + dy * dy;
                        if (d < bestD)
                        {
                            bestD = d;
                            chosenIdx = i;
                        }
                    }
                    if (chosenIdx >= 0)
                    {
                        circ = circs.Length > chosenIdx ? circs[chosenIdx].D : 0.85;
                        areaVal = areas.Length > chosenIdx ? areas[chosenIdx].D : 0;
                        if (radiusVal <= 0.001 && areaVal > 0)
                        {
                            radiusVal = Math.Sqrt(areaVal / Math.PI); // 区域中心兜底路径补等效半径
                        }
                        // 与最近其它候选的距离 → 唯一性评分
                        for (int i = 0; i < n; i++)
                        {
                            if (i == chosenIdx) continue;
                            double dx = cols[i].D - finalPixelX;
                            double dy = rows[i].D - finalPixelY;
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            if (d < nearestOtherDist) nearestOtherDist = d;
                        }
                        // 候选列表（最多 8 条，供界面展示）
                        int top = Math.Min(n, 8);
                        for (int i = 0; i < top; i++)
                        {
                            double candR = Math.Sqrt((areas.Length > i ? areas[i].D : 0) / Math.PI);
                            report.Candidates.Add(new MatchCandidateInfo
                            {
                                Index = i + 1,
                                IsSelected = i == chosenIdx,
                                PixelX = cols[i].D,
                                PixelY = rows[i].D,
                                Radius = candR,
                                Circularity = circs.Length > i ? circs[i].D : 0.5,
                                Area = areas.Length > i ? areas[i].D : 0
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"报告候选明细组装失败（不影响识别）: {ex.Message}");
            }

            if (!success)
            {
                report.Verdict = "失败";
                report.Score = 0;
                sb.AppendLine(failDetail ?? "识别失败");
                report.Detail = sb.ToString();
                return report;
            }

            // ── 综合评分（0~100）：圆度0.35 + 半径一致性0.25 + 唯一性0.2 + 可预测性0.2 ──
            double circScore = Clamp01((circ - 0.4) / 0.6);                       // 圆度 0.4→0, 1.0→1
            double radiusScore;
            string radiusLine;
            if (_referenceMarkRadius > 1.0 && radiusVal > 0.001)
            {
                double devRatio = Math.Abs(radiusVal - _referenceMarkRadius) / _referenceMarkRadius;
                radiusScore = Clamp01(1 - devRatio / 0.4);                        // 偏差≤40% 内线性
                radiusLine = $"半径一致性: {radiusVal:F1}px vs 参考 {_referenceMarkRadius:F1}px (偏差 {devRatio * 100:F0}%) → 分 {radiusScore * 100:F0}";
            }
            else
            {
                radiusScore = 0.8; // 无参考半径（首点/预览）中性给分
                radiusLine = radiusVal > 0.001
                    ? $"半径: {radiusVal:F1}px（暂无参考，中性分 80）"
                    : "半径: 未知";
            }

            double uniqueScore;
            string uniqueLine;
            if (candidateCount <= 1 || nearestOtherDist >= double.MaxValue)
            {
                uniqueScore = 1.0;
                uniqueLine = "唯一候选，无歧义 → 分 100";
            }
            else if (nearestOtherDist > 2.5 * Math.Max(radiusVal, 10))
            {
                uniqueScore = 1.0;
                uniqueLine = $"另 {candidateCount - 1} 个候选均远离选中点(最近 {nearestOtherDist:F0}px)，无歧义 → 分 100";
            }
            else
            {
                uniqueScore = 0.35 + 0.65 * Clamp01(nearestOtherDist / (2.5 * Math.Max(radiusVal, 10)));
                uniqueLine = $"⚠ 存在近距离干扰候选(最近 {nearestOtherDist:F0}px) → 分 {uniqueScore * 100:F0}";
            }

            double devScore;
            string devLine;
            if (expectedPx > 0 && expectedPy > 0)
            {
                double dev = Math.Sqrt((finalPixelX - expectedPx) * (finalPixelX - expectedPx)
                                     + (finalPixelY - expectedPy) * (finalPixelY - expectedPy));
                devScore = Clamp01(1 - dev / (3 * Math.Max(searchRadius, 10)));
                devLine = $"距期望位置 {dev:F0}px (搜索半径 {searchRadius:F0}) → 分 {devScore * 100:F0}";
            }
            else
            {
                devScore = 0.85;
                devLine = "无期望位置引导(全图搜索)，中性分 85";
            }

            double score = 100 * (0.35 * circScore + 0.25 * radiusScore + 0.2 * uniqueScore + 0.2 * devScore);
            if (usedFallback)
            {
                score *= 0.85; // 降级路径（动态阈值/区域中心）命中 → 打折
            }
            score = Math.Max(1, Math.Min(99, score));

            report.Score = score;
            report.Verdict = ScoreVerdict(score);
            sb.AppendLine($"{pointTag} 候选 {candidateCount} 个，综合匹配分 {score:F0}/100 · {report.Verdict}");
            sb.AppendLine($"圆度: {circ:F2} (权重0.35) → 分 {circScore * 100:F0}" + (usedFallback ? "  [降级路径命中，总分×0.85]" : ""));
            sb.AppendLine(radiusLine);
            sb.AppendLine(uniqueLine);
            sb.AppendLine(devLine);
            if (report.Candidates.Count > 0)
            {
                sb.AppendLine("─ 候选明细 ─");
                foreach (var c in report.Candidates)
                {
                    sb.AppendLine($"{(c.IsSelected ? "★选中" : "  候选")} #{c.Index}: P({c.PixelX:F0},{c.PixelY:F0}) 半径{c.Radius:F1}px 圆度{c.Circularity:F2}" +
                                 (c.IsSelected ? "  ← 识别结果" : (c.Radius > radiusVal * 0.6 && c.Radius < radiusVal * 1.4 ? "  ⚠同尺度(可能干扰)" : "")));
                }
            }
            sb.AppendLine(score >= 70
                ? "→ 该参数下识别质量高，走位采样不易丢点。"
                : "→ 分数偏低：建议优先放宽圆度/阈值或增大搜索半径；若候选有干扰请用面积/圆度滤除。");
            report.Detail = sb.ToString();
            return report;
        }

        #region 场景式逐步上屏（HDevelop 语义：算子执行一步、结果上屏一步）

        /// <summary>显示上下文是否可用（已注入且窗口就绪）</summary>
        private bool DisplayReady
        {
            get
            {
                var ctx = DisplayContext;
                return ctx != null && ctx.IsReady;
            }
        }

        /// <summary>
        /// 开始新场景：清空旧场景（显示层释放其中托管对象）并登记底图（借用，不转移所有权）。
        /// 每次特征提取的第一步；此后每个算子的结果通过 Submit* 系列立即上屏。
        /// 上下文不可用时静默跳过，提取主流程不受影响。
        /// </summary>
        private void BeginScene(HObject baseImage)
        {
            var ctx = DisplayContext;
            if (ctx == null || !ctx.IsReady)
            {
                return;
            }
            try
            {
                ctx.BeginScene();
                if (baseImage != null && baseImage.IsInitialized())
                {
                    ctx.AddBorrowed(baseImage);
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"场景初始化失败（已忽略）: {ex.Message}");
            }
        }

        /// <summary>
        /// 把算子产生的 HObject 提交到标定视图场景（提交即所有权转移）。
        /// 提交成功后引用置 null —— 调用方 finally 的 DisposeAndNull 自动跳过，不会双重释放；
        /// 上下文未注入 / 窗口未就绪 / 提交异常时跳过，对象仍归调用方 finally 兜底释放。
        /// </summary>
        private void Submit(ref HObject obj, string color = null, int lineWidth = 1)
        {
            var ctx = DisplayContext;
            if (obj == null || ctx == null || !ctx.IsReady)
            {
                return;
            }
            try
            {
                ctx.Add(obj, color, lineWidth);
                obj = null; // 所有权已移交显示层场景
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"场景对象提交失败（已忽略，对象仍由 finally 释放）: {ex.Message}");
            }
        }

        /// <summary>
        /// 提交 region 的"显示副本"：算子执行后立即上屏，原对象继续参与后续算子运算。
        /// 有偏移时用 MoveRegion 平移到全图坐标（副本即平移结果），无偏移时 CopyObj 快照。
        /// 原对象生命周期不受影响（finally 统一释放）——算子代码零所有权心智负担。
        /// </summary>
        private void SubmitRegion(HObject source, double rowOffset, double colOffset, string color, int lineWidth)
        {
            if (!DisplayReady || source == null || !source.IsInitialized())
            {
                return;
            }
            HObject display = null;
            try
            {
                if (Math.Abs(rowOffset) < 1e-9 && Math.Abs(colOffset) < 1e-9)
                {
                    HOperatorSet.CopyObj(source, out display, 1, 1);
                }
                else
                {
                    HOperatorSet.MoveRegion(source, out display, rowOffset, colOffset);
                }
                Submit(ref display, color, lineWidth);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"过程层显示副本提交失败（已忽略）: {ex.Message}");
            }
            finally
            {
                display?.Dispose(); // Submit 未接收所有权时（未就绪/异常）兜底释放副本
            }
        }

        /// <summary>
        /// 提交 XLD 轮廓的"显示副本"（ROI 局部坐标平移到全图坐标；Halcon 无 MoveXld，
        /// 有偏移时用 HomMat2dTranslate + AffineTransContourXld）。语义同 SubmitRegion。
        /// </summary>
        private void SubmitXld(HObject source, double rowOffset, double colOffset, string color, int lineWidth)
        {
            if (!DisplayReady || source == null || !source.IsInitialized())
            {
                return;
            }
            HObject display = null;
            try
            {
                if (Math.Abs(rowOffset) < 1e-9 && Math.Abs(colOffset) < 1e-9)
                {
                    HOperatorSet.CopyObj(source, out display, 1, 1);
                }
                else
                {
                    HOperatorSet.HomMat2dIdentity(out HTuple homMat2d);
                    HOperatorSet.HomMat2dTranslate(homMat2d, rowOffset, colOffset, out homMat2d);
                    HOperatorSet.AffineTransContourXld(source, out display, homMat2d);
                }
                Submit(ref display, color, lineWidth);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"XLD 显示副本提交失败（已忽略）: {ex.Message}");
            }
            finally
            {
                display?.Dispose();
            }
        }

        /// <summary>提交文本标注（image 坐标系，跟随缩放平移；失败仅记日志）</summary>
        private void SubmitText(string text, double row, double col, string color)
        {
            var ctx = DisplayContext;
            if (ctx == null || !ctx.IsReady)
            {
                return;
            }
            try
            {
                ctx.AddText(text, row, col, color);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"文本标注提交失败（已忽略）: {ex.Message}");
            }
        }

        /// <summary>提交十字标记（image 坐标系；失败仅记日志）</summary>
        private void SubmitCross(double row, double col, double size, string color)
        {
            var ctx = DisplayContext;
            if (ctx == null || !ctx.IsReady)
            {
                return;
            }
            try
            {
                ctx.AddCross(row, col, size, color);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"十字标记提交失败（已忽略）: {ex.Message}");
            }
        }

        /// <summary>
        /// 把匹配分叠加到视图左上角（黑底大号文字，任意亮度底图可读）。
        /// 分数颜色：≥85 绿、≥70 黄绿、≥55 黄、&lt;55 红；失败显示红色"未识别"。
        /// </summary>
        private void SubmitScoreOverlay(FeatureMatchReport report)
        {
            if (report == null) return;
            var win = DisplayContext?.AsWindow();
            string text;
            string color;
            if (!report.Success)
            {
                text = "✗ 未识别 (0/100)";
                color = "red";
            }
            else
            {
                text = $"匹配分 {report.Score:F0}/100 · {report.Verdict}";
                color = report.Score >= 85 ? "lime green" : report.Score >= 70 ? "yellow green" : report.Score >= 55 ? "yellow" : "orange red";
            }
            try
            {
                if (win != null && win.IsReady)
                {
                    win.DrawRecorded(w =>
                    {
                        HalconGlobalHelper.SetFontSafe(w, 28);
                        HalconGlobalHelper.DispTextSafe(w, text, "window", 8, 8, color);
                    });
                }
                else
                {
                    SubmitText(text, 10, 10, color);
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"匹配分叠加失败（已忽略）: {ex.Message}");
            }
        }

        /// <summary>提交圆标记（image 坐标系；失败仅记日志）</summary>
        private void SubmitCircle(double row, double col, double radius, string color)
        {
            var ctx = DisplayContext;
            if (ctx == null || !ctx.IsReady)
            {
                return;
            }
            try
            {
                ctx.AddCircle(row, col, radius, color);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"圆标记提交失败（已忽略）: {ex.Message}");
            }
        }

        /// <summary>
        /// 提交"识别结果"醒目标记（image 坐标系）：黑描边绿十字 + 双色圆环 + 阴影文字。
        /// 黑色描边保证在亮背景（中心亮）与暗背景（边缘暗）上都清晰可见；
        /// 识别错点（伪特征/反光/暗角）是标定 RMS 过大最常见的根因，
        /// 醒目标记供操作员逐点肉眼核对检测中心是否落在真实 Mark 上。
        /// </summary>
        /// <param name="row">识别中心行（全图像素坐标）</param>
        /// <param name="col">识别中心列（全图像素坐标）</param>
        /// <param name="label">随标记显示的说明文字（点号+像素坐标）</param>
        /// <param name="imgWidth">图像宽（列数）；&gt;0 时额外画"视野中心 → 识别中心"的偏移向量</param>
        /// <param name="imgHeight">图像高（行数）；同 imgWidth</param>
        private void SubmitResultMarker(double row, double col, string label,
            double imgWidth = 0, double imgHeight = 0)
        {
            double textRow = Math.Max(0, row - 90);
            double textCol = Math.Max(10, col + 45);

            // ★ HDevelop 式直通：拿到 HWindow 想画什么直接调算子，不必再为每种画法去
            //   扩展 ICalibrationDisplayContext 接口。下面每一行都与 HDevelop 里的
            //   dev_set_color + disp_cross / disp_circle / disp_arrow / disp_text 一一对应。
            //   走 DrawRecorded：内容登记进场景，用户缩放/平移后由显示层重放本闭包。
            //   闭包只捕获值类型与字符串，重放无生命周期风险。
            var win = DisplayContext.AsWindow();
            if (win != null && win.IsReady)
            {
                win.DrawRecorded(w =>
                {
                    // 十字：黑色大十字打底 + 绿色小十字叠上，形成黑描边效果（两色对比任何底色可见）
                    w.SetColor("black");
                    HOperatorSet.DispCross(w, row, col, 100, 0.785398);
                    w.SetColor("green");
                    HOperatorSet.DispCross(w, row, col, 80, 0.785398);

                    // 圆环：黑外圈 + 绿内圈，把识别中心圈出来（尺寸远大于 Mark 本体，一眼定位）
                    w.SetColor("black");
                    HOperatorSet.DispCircle(w, row, col, 70);
                    w.SetColor("green");
                    HOperatorSet.DispCircle(w, row, col, 65);

                    // ★ 场景式 API 画不出来的增强：视野中心 → 识别中心的偏移向量。
                    //   Mark 越靠近视野边缘箭头越长，一眼判断该点是否"半出视野/落在暗角/畸变大"，
                    //   这正是九点标定个别点取错特征、RMS 偏大的高频根因。
                    //   （row 对应图像高、col 对应图像宽，注意别写反）
                    if (imgWidth > 0 && imgHeight > 0)
                    {
                        double centerRow = imgHeight / 2.0;
                        double centerCol = imgWidth / 2.0;
                        double offset = Math.Sqrt((row - centerRow) * (row - centerRow)
                                                + (col - centerCol) * (col - centerCol));
                        w.SetColor("orange");
                        w.SetLineWidth(2);
                        HOperatorSet.DispArrow(w, centerRow, centerCol, row, col, 4);
                        w.SetColor("white");
                        HalconGlobalHelper.DispTextSafe(w, $"距视野中心 {offset:F0}px", "image",
                            (row + centerRow) / 2.0, (col + centerCol) / 2.0, "white");
                    }

                    // 文字：黑色阴影偏移 2px + 黄色前景，避免亮背景上黄字不可读
                    HalconGlobalHelper.DispTextSafe(w, label, "image", textRow + 2, textCol + 2, "black");
                    HalconGlobalHelper.DispTextSafe(w, label, "image", textRow, textCol, "yellow");
                });
                return;
            }

            // 降级：宿主未提供直通通道（旧版显示上下文实现）时沿用场景式 API，行为与改造前一致
            SubmitCross(row, col, 100, "black");
            SubmitCross(row, col, 80, "green");
            SubmitCircle(row, col, 70, "black");
            SubmitCircle(row, col, 65, "green");
            SubmitText(label, textRow + 2, textCol + 2, "black");
            SubmitText(label, textRow, textCol, "yellow");
        }

        #endregion

        /// <summary>
        /// 安全统计对象数量（调试文本用）：null / 未初始化 / 算子异常一律返回 0，
        /// 避免异步绘制闭包中对可能缺失或已释放的中间对象调用 CountObj 抛 HALCON #4056。
        /// </summary>
        private static int SafeCount(HObject obj)
        {
            if (obj == null || !obj.IsInitialized())
            {
                return 0;
            }
            try
            {
                HOperatorSet.CountObj(obj, out HTuple count);
                return count.I;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 统一解析图像句柄：支持直接传 HObject/HImage，也支持传 IRenderImage 包装（取其 NativeHandle）。
        /// 上层 UI（如 WpfUI）受"仅 HalconWrapper/HalconWrapper.Wpf 可引用 halcondotnet"强约束，
        /// 无法直接持有 HObject，只能传 IRenderImage，这里负责解包出 HObject 供 Halcon 算子使用。
        /// </summary>
        private static HObject ResolveHObject(object imageHandle)
        {
            if (imageHandle == null)
            {
                return null;
            }
            if (imageHandle is HObject hObject)
            {
                return hObject;
            }
            if (imageHandle is IRenderImage renderImage)
            {
                return renderImage.NativeHandle as HObject;
            }
            return null;
        }

        /// <summary>
        /// 彩色图像转单通道灰度（阈值分割 / ThresholdSubPix / 骨架化等算子均要求单通道输入）。
        /// 输入已是单通道时返回 null（调用方直接用原图，零开销）；
        /// 3 通道走 Rgb1ToGray（BT.601 加权），其余非常规通道数兜底取第 1 通道。
        /// 返回的新图像归调用方所有，须在 finally 中释放。
        /// </summary>
        private static HObject ToGrayIfNeeded(HObject image)
        {
            try
            {
                HOperatorSet.CountChannels(image, out HTuple channels);
                if (channels.I == 1)
                {
                    return null;
                }
                if (channels.I == 3)
                {
                    HOperatorSet.Rgb1ToGray(image, out HObject gray);
                    return gray;
                }
                LogBus.Warn(nameof(CalibrationService),
                    $"输入图像为 {channels.I} 通道（非 1/3 通道），已兜底取第 1 通道参与算子运算。");
                HOperatorSet.AccessChannel(image, out HObject firstChannel, 1);
                return firstChannel;
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"通道数检查失败（按单通道继续）: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 参考 Mark 半径（首个成功识别的圆 Mark 拟合半径）。
        /// 走位采样中 Mark 是同一个物理特征，半径恒定；反光点/暗角等伪特征半径往往不同，
        /// 用它过滤多候选比"离期望位置最近"更可靠——尤其期望位置不可用（全图搜索）时。
        /// </summary>
        private double _referenceMarkRadius;

        /// <summary>重置参考 Mark 半径（重新进入标定步骤 / 更换 Mark 板时调用）</summary>
        public void ResetMarkReference()
        {
            _referenceMarkRadius = 0;
        }

        /// <summary>
        /// 圆 Mark 多候选择优：先排除半径偏离参考 Mark 超过 40% 的伪特征，
        /// 再按离期望位置最近选择；无期望位置（全图搜索）时选半径最接近参考者。
        /// 无参考半径时退化为仅按期望位置择近（即 SelectClosestIndex 语义）。
        /// </summary>
        /// <param name="radiusOf">候选 i 的等效半径（XLD 拟合半径 / 区域面积等效半径）</param>
        private int SelectBestCircleIndex(HTuple row, HTuple col, Func<int, double> radiusOf, double expectedPx, double expectedPy)
        {
            int n = row.Length;
            if (n <= 1)
            {
                return 0;
            }
            bool hasExpected = expectedPx > 0 && expectedPy > 0;
            bool hasRef = _referenceMarkRadius > 1.0;
            if (!hasExpected && !hasRef)
            {
                return 0; // 两者皆无：保留旧语义（取第一个）
            }

            int bestIdx = -1;
            double bestScore = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double r = radiusOf(i);
                if (hasRef && _referenceMarkRadius > 1.0 &&
                    Math.Abs(r - _referenceMarkRadius) > _referenceMarkRadius * 0.4)
                {
                    continue; // 半径与参考 Mark 偏差>40% → 判定伪特征，直接排除
                }
                double score = hasExpected
                    ? DistanceSq(col[i].D - expectedPx, row[i].D - expectedPy)
                    : DistanceSq(r - _referenceMarkRadius, 0);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                }
            }

            // 全部候选被半径过滤（Mark 半出视野/遮挡导致半径畸变）→ 退化为按期望位置择近
            if (bestIdx < 0)
            {
                return SelectClosestIndex(row, col, expectedPx, expectedPy);
            }
            return bestIdx;
        }

        /// <summary>
        /// 组装十字 Mark 匹配质量报告（成功路径）：found=找到近似垂直直线对（精确十字）→高分；
        /// 兜底取最大候选区域中心 → 中低分；失败 → 0 分 + 诊断。
        /// </summary>
        private FeatureMatchReport ComposeCrossReport(
            int candidateCount, bool found, double angleDevDeg,
            double centerCol, double centerRow,
            bool success, string failDetail, string pointTag)
        {
            var report = new FeatureMatchReport
            {
                Success = success,
                PixelX = centerCol,
                PixelY = centerRow,
                CandidateCount = candidateCount,
                UsedFallback = !found,
                Verdict = "失败",
                Score = 0
            };

            var sb = new System.Text.StringBuilder();
            if (!success)
            {
                sb.AppendLine(failDetail ?? "十字 Mark 识别失败");
                report.Detail = sb.ToString();
                return report;
            }

            double score;
            if (found)
            {
                // 精确命中：直线对夹角越接近 90° 分越高；夹角误差容忍 30°
                double angleScore = Clamp01(1 - Math.Abs(angleDevDeg) / 30.0);
                double uniqueScore = candidateCount <= 1 ? 1.0 : 0.85; // 多候选略降
                score = 100 * (0.7 * (0.75 + 0.25 * angleScore) + 0.3 * uniqueScore);
                sb.AppendLine($"{pointTag} 十字直线对命中：候选 {candidateCount} 个，综合匹配分 {score:F0}/100 · {ScoreVerdict(score)}");
                sb.AppendLine($"两臂夹角误差 {Math.Abs(angleDevDeg):F1}°（容忍 30°）→ 角度分 {angleScore * 100:F0}");
                sb.AppendLine(candidateCount > 1 ? $"⚠ 存在 {candidateCount} 个候选，直线对已按期望位置择近（如误配请调面积/阈值过滤干扰）" : "唯一候选，无歧义");
                sb.AppendLine(score >= 70 ? "→ 十字结构清晰，走位采样不易丢点。" : "→ 分数偏低：十字两臂夹角偏差大或候选有干扰，建议检查画面/参数。");
            }
            else
            {
                // 兜底：未找到垂直直线对，取最大候选区域中心（十字可能残缺/遮挡）
                score = 45 + 15 * Clamp01((candidateCount - 1) / 3.0);
                score = Math.Min(score, 65);
                sb.AppendLine($"{pointTag} ⚠ 兜底命中（未找到垂直直线对，取最大候选区域中心）→ 分 {score:F0}/100 · {ScoreVerdict(score)}");
                sb.AppendLine("两臂未能形成近似垂直直线对：十字可能残缺、粘连或对比度不足；建议调阈值/检查 Mark 完整性。");
            }
            report.Score = Math.Max(1, Math.Min(99, score));
            report.Verdict = ScoreVerdict(report.Score);
            report.Detail = sb.ToString();
            return report;
        }

        /// <summary>平方距离（避免开方）</summary>
        private static double DistanceSq(double dx, double dy)
        {
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// 多候选时选离期望位置最近的索引。
        /// expectedPx/expectedPy <= 0 时（无 seed / 全图搜索）返回 0（取第一个）。
        /// 这是标定误检的核心修复：之前代码取 col[0]/row[0]（第一个候选），
        /// 当阈值分割到多个区域时，第一个很可能不是 Mark 而是伪特征。
        /// </summary>
        private static int SelectClosestIndex(HTuple row, HTuple col, double expectedPx, double expectedPy)
        {
            if (row.Length <= 1 || expectedPx <= 0 || expectedPy <= 0)
            {
                return 0;
            }
            int bestIdx = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < row.Length; i++)
            {
                double dx = col[i].D - expectedPx;
                double dy = row[i].D - expectedPy;
                double dist = dx * dx + dy * dy;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// 角度归一化到 [-180, 180]
        /// </summary>
        private static double NormalizeAngle180(double angle)
        {
            while (angle > 180) angle -= 360;
            while (angle < -180) angle += 360;
            return angle;
        }

        /// <summary>点到线段的最短距离（欧氏）</summary>
        private static double PointToSegmentDistance(double px, double py, double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-9)
            {
                return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
            }
            double t = Math.Max(0, Math.Min(1, ((px - x1) * dx + (py - y1) * dy) / lenSq));
            double cx = x1 + t * dx;
            double cy = y1 + t * dy;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }

        public Result<CalibrationResult> CalcNinePointHomMat(double[] pixelXList, double[] pixelYList, double[] worldXList, double[] worldYList)
        {
            try
            {
                // ── 2026-09-04 前置硬校验（防病态输入打到 HALCON 算子层）──
                //   vector_to_hom_mat2d 求 2D 映射至少 4 对点；向导层要求 ≥6，此处兜底 4。
                if (pixelXList == null || pixelYList == null || worldXList == null || worldYList == null)
                {
                    return Result<CalibrationResult>.Fail("标定输入坐标数组为空");
                }
                int n = pixelXList.Length;
                if (n != pixelYList.Length || n != worldXList.Length || n != worldYList.Length)
                {
                    return Result<CalibrationResult>.Fail("标定像素/世界坐标数组长度不一致");
                }
                if (n < 4)
                {
                    return Result<CalibrationResult>.Fail("标定点数不足：vector_to_hom_mat2d 至少需要 4 对点（推荐 9 点）");
                }

                // ── 诊断日志：拟合前逐点打印原始数据，便于排查"哪个点像素坐标不可靠" ──
                LogBus.Info(nameof(CalibrationService), $"═══ 九点标定拟合开始，共 {n} 个点 ═══");
                for (int i = 0; i < n; i++)
                {
                    LogBus.Info(nameof(CalibrationService),
                        $"  点{i + 1}: Pixel({pixelXList[i]:F2}, {pixelYList[i]:F2})  World({worldXList[i]:F3}, {worldYList[i]:F3})");
                }

                // ── 数据合理性检查：像素坐标必须二维展开，否则矩阵病态/无解（原仅告警，现直接拦下） ──
                string degenerate = Calib2DTool.CheckDegeneratePoints(pixelXList, pixelYList);
                if (degenerate != null)
                {
                    LogBus.Error(nameof(CalibrationService), "⚠ " + degenerate);
                    return Result<CalibrationResult>.Fail(degenerate);
                }

                HTuple px = new HTuple(pixelXList);
                HTuple py = new HTuple(pixelYList);
                HTuple wx = new HTuple(worldXList);
                HTuple wy = new HTuple(worldYList);

                var res = Calib2DTool.CalcNinePointHomMat(px, py, wx, wy);
                if (!res.Success) return Result<CalibrationResult>.Fail(res.Message);

                // 计算 RMS 误差 + 逐点残差诊断
                double rms = 0;
                try
                {
                    HOperatorSet.AffineTransPoint2d(res.Data, px, py, out HTuple calcWx, out HTuple calcWy);
                    double sumSquareErr = 0;
                    double maxErr = 0;
                    int maxErrIdx = -1;
                    for (int i = 0; i < n; i++)
                    {
                        double errX = calcWx[i].D - worldXList[i];
                        double errY = calcWy[i].D - worldYList[i];
                        double err2D = Math.Sqrt(errX * errX + errY * errY);
                        sumSquareErr += (errX * errX + errY * errY);
                        LogBus.Info(nameof(CalibrationService),
                            $"  残差 点{i + 1}: ΔX={errX:+0.###;-0.###;0}mm  ΔY={errY:+0.###;-0.###;0}mm  |Δ|={err2D:F3}mm");
                        if (err2D > maxErr) { maxErr = err2D; maxErrIdx = i + 1; }
                    }
                    rms = Math.Sqrt(sumSquareErr / n);
                    LogBus.Info(nameof(CalibrationService),
                        $"═══ RMS={rms:F4}mm  最大残差点=#{maxErrIdx}({maxErr:F3}mm) ═══");
                }
                catch { }

                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hommat_{Guid.NewGuid():N}.tup");
                var save = Calib2DTool.SaveHomMatToFile(res.Data, tmp);
                if (!save.Success) return Result<CalibrationResult>.Fail("矩阵保存失败: " + save.Message);

                // ── 矩阵健康检查（不止 RMS，还要度量矩阵几何合理性）──
                // ① 两轴像素当量一致性：理想正交安装下 H11≈H22（像素→mm 缩放应各向同性）
                // ② 正交性：|H12|、|H21| 相对主轴缩放应很小（否则轴/相机安装有剪切）
                // ③ 行列式：负值 = 镜像变换（轴方向/相机成像镜像，标定语义可能不对）
                // ④ 网格重建：把采样像素点映射回世界，检查 3x3 网格边长一致性（±10%）与行/列夹角正交性
                string health = BuildHomMatHealthReport(res.Data, px, py, wx, wy, rms);

                return Result<CalibrationResult>.Ok(new CalibrationResult
                {
                    SavedFilePath = tmp,
                    RmsError = rms,
                    HealthReport = health
                });
            }
            catch (Exception ex)
            {
                return Result<CalibrationResult>.Fail("标定计算异常: " + ex.Message, -1, ex);
            }
        }

        /// <summary>
        /// 构建标定矩阵健康检查报告（多行文本，供向导展示）。
        /// 指标：两轴像素当量一致性 / 正交性(剪切) / 行列式(镜像检测) / 网格重建边长与夹角 / RMS 阈值。
        /// </summary>
        private static string BuildHomMatHealthReport(HTuple hom, HTuple px, HTuple py, HTuple wx, HTuple wy, double rms)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══ 标定矩阵健康检查 ═══");
            try
            {
                double h11 = hom[0].D, h12 = hom[1].D, h13 = hom[2].D;
                double h21 = hom[3].D, h22 = hom[4].D, h23 = hom[5].D;

                // ① 两轴像素当量一致性（2026-09-09 改为旋转不变口径）
                //    ⚠ 旧判据拿 |h11| vs |h22| 比对：图像旋转 ~45° 时二者都趋近 0，判据失效
                //      （本工位图像旋转 151°，h11=-0.0607 / h22=+0.0866，看起来"差 30%"纯属巧合，
                //       换个旋转角就可能完全漏报）。
                //    正确做法：
                //      · 两轴当量取【列向量范数】sCol=√(h11²+h21²)、sRow=√(h12²+h22²)（旋转不变）
                //      · 更硬的判据是【奇异值比 σ1/σ2】（AᵀA 特征值开方），对旋转/镜像完全不变；
                //        平面成像 + 方形像素下必须 ≈1，偏离即"九点数据被污染"。
                //    为什么必须硬拦：落点用差分 P_go−P_photo=H(p_tip)−H(u)，
                //    形状失真会按差分距离线性放大（本工位失真 22.7% × 84mm ≈ 19mm，
                //    而九点 RMS 只有 0.541mm —— 残差小根本发现不了）。
                double sCol = Math.Sqrt(h11 * h11 + h21 * h21);
                double sRow = Math.Sqrt(h12 * h12 + h22 * h22);
                double scaleRatio = Math.Abs(sCol - sRow) / Math.Max(sCol, sRow);
                double trA = h11 * h11 + h21 * h21 + h12 * h12 + h22 * h22;
                double detA = h11 * h22 - h12 * h21;
                double discA = Math.Sqrt(Math.Max(0.0, trA * trA - 4.0 * detA * detA));
                double sig1 = Math.Sqrt(Math.Max(0.0, (trA + discA) / 2.0));
                double sig2 = Math.Sqrt(Math.Max(1e-18, (trA - discA) / 2.0));
                double aniso = (sig2 > 1e-12) ? sig1 / sig2 : double.NaN;
                bool badShape = double.IsNaN(aniso) || Math.Abs(aniso - 1.0) > 0.03;
                sb.Append($"① 像素当量: col={sCol:F5} row={sRow:F5} mm/px，两轴差 {scaleRatio * 100:F2}% ｜ "
                    + (badShape
                        ? $"⛔ 形状非法（各向异性 σ1/σ2={aniso:F3}，应≈1.000）"
                        : $"✓ 形状合法（各向异性 σ1/σ2={aniso:F3}）"));
                sb.AppendLine();

                // ①.x ★ 2026-09-10 二轮：形状非法时的【定向归因】。
                //   历史教训：此处原写"九点数据被污染（走位没到位/点对错位/采样 Z 不一致/模板误匹配）"，
                //   把操作员往"重做九点/查模板"方向带——但现场三次独立实测（手动 JOG 13.78%、
                //   两轮九点 1.490/1.478）都指向【世界侧轨迹非直线】，与相机/模板/采样无关。
                //   故改为按"失真量"给出可操作的排查顺序，并把决定性实验（直线性探针）写在第一条。
                if (badShape)
                {
                    double anisoPct = (aniso - 1.0) * 100.0;
                    sb.AppendLine($"   ↳ 失真量 {anisoPct:F1}%。按可能性排查（顺序勿颠倒）：");
                    sb.AppendLine("     ① 【决定性】跑『📐 直线性探针』（步骤2 数据采集页）：沿世界 +X 走 0/20/40mm，");
                    sb.AppendLine("        验三点是否共线。弦弧差 >10px = 走位轨迹是弧线 → 世界侧问题，与相机无关；");
                    sb.AppendLine("        <2px = 轨迹直 → 才轮到查相机安装/对焦、标定板平面度、采样 Z 一致性。");
                    sb.AppendLine("     ② 走位是否走了 CP 直线：日志应出现 LMOVE（非 MOVE）。出现 LMOVE 但探针仍报弧线");
                    sb.AppendLine("        → RC+ 侧脚本未『停止任务→重新编译→运行』，跑的还是旧版。");
                    sb.AppendLine("     ③ RC+ 项目里机器人型号/臂长参数是否与实际机型一致（臂长错 → 逆解关节角偏，CP 也走歪）。");
                    sb.AppendLine("     ④ 基准位是否靠近可达域内圈/外圈边界或奇异点（换到环带中腰重测）。");
                    sb.AppendLine("     ⚠ 已排除：相机斜视（需倾角 " +
                                  (aniso > 1.0 ? $"{Math.Acos(Math.Min(1.0, 1.0 / aniso)) * 180.0 / Math.PI:F1}°" : "—") +
                                  "，物理不可能）、单点采样污染（留一法剔除任一点失真不变）。");
                }

                // ② 正交性：非对角项相对主轴缩放
                double shearX = Math.Abs(h12) / Math.Max(Math.Abs(h11), 1e-9);
                double shearY = Math.Abs(h21) / Math.Max(Math.Abs(h22), 1e-9);
                sb.Append($"② 正交性: 剪切比 X={shearX * 100:F2}% Y={shearY * 100:F2}% "
                    + (shearX > 0.05 || shearY > 0.05 ? "⚠ 剪切偏大，相机/轴安装有倾斜" : "✓ 近正交"));
                sb.AppendLine();

                // ③ 行列式：负值 = 镜像（EyeInHand 相机随动为固有镜像，正常；EyeToHand 固定相机才需核查）
                double det = h11 * h22 - h12 * h21;
                sb.Append($"③ 行列式: det={det:F4} "
                    + (det < 0
                        ? "⚠ 负值=镜像变换（相机随动/EyeInHand 属固有镜像属正常；固定相机需检查轴方向/相机成像）"
                        : "✓ 正向（无镜像）"));
                sb.AppendLine();

                // ④ 网格重建：像素→世界后检查 3x3 网格边长一致性（CV）与行/列夹角
                int n = px.Length;
                if (n >= 9)
                {
                    HOperatorSet.AffineTransPoint2d(hom, px, py, out HTuple wxp, out HTuple wyp);
                    var hDists = new List<double>();
                    for (int row = 0; row < 3; row++)
                        for (int c = 0; c < 2; c++)
                        {
                            int i = row * 3 + c, j = i + 1;
                            hDists.Add(Math.Sqrt(Math.Pow(wxp[i].D - wxp[j].D, 2) + Math.Pow(wyp[i].D - wyp[j].D, 2)));
                        }
                    var vDists = new List<double>();
                    for (int col = 0; col < 3; col++)
                        for (int r = 0; r < 2; r++)
                        {
                            int i = r * 3 + col, j = i + 3;
                            vDists.Add(Math.Sqrt(Math.Pow(wxp[i].D - wxp[j].D, 2) + Math.Pow(wyp[i].D - wyp[j].D, 2)));
                        }
                    double hMean = hDists.Average(), hCv = StdDev(hDists) / Math.Max(hMean, 1e-9);
                    double vMean = vDists.Average(), vCv = StdDev(vDists) / Math.Max(vMean, 1e-9);
                    sb.Append($"④ 网格重建: 行边长均值 {hMean:F2}mm(CV={hCv * 100:F1}%)，列边长均值 {vMean:F2}mm(CV={vCv * 100:F1}%) "
                        + (hCv > 0.1 || vCv > 0.1 ? "⚠ 边长离散大，个别采样点不可靠" : "✓ 网格规整"));
                    sb.AppendLine();

                    // ⑤ 行/列夹角正交性（中心点上下向量 vs 左右向量）
                    double vx = wxp[7].D - wxp[1].D, vy = wyp[7].D - wyp[1].D;
                    double hx = wxp[5].D - wxp[3].D, hy = wyp[5].D - wyp[3].D;
                    double cosAng = (vx * hx + vy * hy) / (Math.Sqrt(vx * vx + vy * vy) * Math.Sqrt(hx * hx + hy * hy));
                    double angDeg = Math.Acos(Math.Max(-1, Math.Min(1, cosAng))) * 180.0 / Math.PI;
                    sb.Append($"⑤ 网格夹角: {angDeg:F1}° "
                        + (Math.Abs(angDeg - 90) > 3 ? "⚠ 偏离 90°，轴/相机安装不垂直" : "✓ 近正交"));
                    sb.AppendLine();
                }

                sb.Append($"RMS 重投影误差: {rms:F4} mm" + (rms > 0.5 ? " ⚠ 建议 <0.5mm 才可用于高精度引导" : " ✓ 可接受"));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"健康检查异常（不影响矩阵）: {ex.Message}");
            }
            return sb.ToString();
        }

        /// <summary>样本标准差</summary>
        private static double StdDev(IEnumerable<double> vals)
        {
            var arr = vals as double[] ?? vals.ToArray();
            if (arr.Length == 0) return 0;
            double mean = arr.Average();
            return Math.Sqrt(arr.Sum(v => (v - mean) * (v - mean)) / arr.Length);
        }

        public Result SaveHomMatFile(string sourceFilePath, string destFilePath)
        {
            try
            {
                System.IO.File.Copy(sourceFilePath, destFilePath, true);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                return Result.Fail("保存文件失败: " + ex.Message, -1, ex);
            }
        }

        public Result<FixtureData> CreateFixture(double refRow, double refCol, double refAngle, double curRow, double curCol, double curAngle)
        {
            return FixtureTool.CreateFixture(refRow, refCol, refAngle, curRow, curCol, curAngle);
        }

        public Result<DetectionResult> DetectCalibrationPoints(string imageFilePath)
        {
            return Result<DetectionResult>.Fail("DetectCalibrationPoints 尚未实现，请在 HalconWrapper 中实现检测逻辑。");
        }

        public Result<(double WorldX, double WorldY)> MapPixelToWorld(string matrixFilePath, double px, double py)
        {
            var loadRes = Calib2DTool.LoadHomMatFromFile(matrixFilePath);
            if (!loadRes.Success) return Result<(double, double)>.Fail(loadRes.Message);

            try
            {
                HOperatorSet.AffineTransPoint2d(loadRes.Data, px, py, out HTuple wx, out HTuple wy);
                return Result<(double, double)>.Ok((wx.D, wy.D));
            }
            catch (Exception ex)
            {
                return Result<(double, double)>.Fail("坐标转换失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 物理坐标逆转换像素坐标 (World -> Pixel)
        /// </summary>
        public Result<(double PixelX, double PixelY)> MapWorldToPixel(string matrixFilePath, double wx, double wy)
        {
            var loadRes = Calib2DTool.LoadHomMatFromFile(matrixFilePath);
            if (!loadRes.Success) return Result<(double, double)>.Fail(loadRes.Message);

            try
            {
                // 计算矩阵逆矩阵 HomMat2dInvert
                HOperatorSet.HomMat2dInvert(loadRes.Data, out HTuple homMat2DInvert);

                // 使用逆矩阵执行坐标转换
                HOperatorSet.AffineTransPoint2d(homMat2DInvert, wx, wy, out HTuple px, out HTuple py);

                return Result<(double, double)>.Ok((px.D, py.D));
            }
            catch (Exception ex)
            {
                return Result<(double, double)>.Fail("坐标逆转换失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 高级手眼坐标转换：考虑像素、物理旋转中心与角度补正
        /// </summary>
        public Result<(double FinalWorldX, double FinalWorldY)> MapPixelToWorldWithOffset(
            string matrixFilePath,
            double px, double py,
            double rotateAngleDeg,
            double centerWx, double centerWy,
            EyeMode eyeMode,
            (double RobotX, double RobotY) currentRobotPos)
        {
            // 1. 基础仿射变换 (Px, Py -> Wx, Wy)
            var rawRes = MapPixelToWorld(matrixFilePath, px, py);
            if (!rawRes.Success) return Result<(double, double)>.Fail(rawRes.Message);

            double wx = rawRes.Data.WorldX;
            double wy = rawRes.Data.WorldY;

            if (eyeMode == EyeMode.EyeInHand)
            {
                // 眼在手上：叠加机器人当前位置
                wx += currentRobotPos.RobotX;
                wy += currentRobotPos.RobotY;
            }

            // 2. 如果存在旋转角度补正
            if (Math.Abs(rotateAngleDeg) > 0.0001)
            {
                double rad = rotateAngleDeg * Math.PI / 180.0;
                double dx = wx - centerWx;
                double dy = wy - centerWy;

                double rotatedX = dx * Math.Cos(rad) - dy * Math.Sin(rad) + centerWx;
                double rotatedY = dx * Math.Sin(rad) + dy * Math.Cos(rad) + centerWy;

                return Result<(double, double)>.Ok((rotatedX, rotatedY));
            }

            return Result<(double, double)>.Ok((wx, wy));
        }

        /// <summary>
        /// 从磁盘配置文件目录加载所有已创建的标定 Profile 方案
        /// </summary>
        /// // 标定方案 Json 配置默认存储路径
        private readonly string _configDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Calibrations");
        public CalibrationService()
        {
            if (!Directory.Exists(_configDirectory))
            {
                Directory.CreateDirectory(_configDirectory);
            }
        }
        public Result<List<CalibrationProfile>> GetAllProfiles()
        {
            try
            {
                var list = new List<CalibrationProfile>();
                var jsonFiles = Directory.GetFiles(_configDirectory, "*.json");

                foreach (var file in jsonFiles)
                {
                    string json = File.ReadAllText(file);
                    var profile = JsonConvert.DeserializeObject<CalibrationProfile>(json);
                    if (profile != null)
                    {
                        list.Add(profile);
                    }
                }

                return Result<List<CalibrationProfile>>.Ok(list);
            }
            catch (Exception ex)
            {
                return Result<List<CalibrationProfile>>.Fail("获取标定方案失败: " + ex.Message, -1, ex);
            }
        }


        /// <summary>
        /// 【九点手眼标定】提取当前工位图像中 Mark 标记点的精准亚像素坐标。
        /// 提取过程走一步画一步（HDevelop 语义）：每个算子执行后把结果立即提交到
        /// DisplayContext 场景（ROI 搜索框 / 阈值分割 / 连通域 / 候选区域 /
        /// 亚像素轮廓 / 拟合圆 / 特征点十字 / 文字标注），供操作员实时确认识别是否正确。
        /// 坐标系说明：HALCON 的 ReduceDomain 不改变图像坐标系——对裁剪域图像做
        /// Threshold / ThresholdSubPix / AreaCenter 得到的坐标本身就是全图坐标，
        /// 本方法直接返回全图坐标，不做任何平移修正（加了反而双重偏移，Mark 会找不准）。
        /// </summary>
        public Result<(double PixelX, double PixelY)> ExtractFeaturePoint(
            object imageHandle,
            int pointIndex,
            double expectedPx,
            double expectedPy,
            bool forceFullImage = false)
        {
            // 1. 严格校验相机图像句柄是否有效（兼容 HObject 直传与 IRenderImage 包装）
            HObject hImage = ResolveHObject(imageHandle);
            if (hImage == null || !hImage.IsInitialized())
            {
                LastMatchReport = new FeatureMatchReport
                {
                    Success = false,
                    Score = 0,
                    Verdict = "失败",
                    Detail = $"[标定点 #{pointIndex}] 提取失败：当前图像缓冲区无效或相机未成功取图。"
                };
                return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 提取失败：当前图像缓冲区无效或相机未成功取图。");
            }

            HObject grayImage = null;
            HObject roi = null;
            HObject imageReduced = null;
            HObject thresholdRegion = null;
            HObject connectedRegions = null;
            HObject selectedRegions = null;
            HObject subPixelEdges = null;
            HObject selectedXld = null;

            double searchRadius = ExtractOptions.SearchRadius;
            double fitRadius = 0;
            // 本次提取是否走过降级路径（动态阈值兜底 / 区域中心兜底）——计入质量报告并打折
            bool usedFallback = false;

            try
            {
                HOperatorSet.GetImageSize(hImage, out HTuple imgWidth, out HTuple imgHeight);

                // 场景第一步：清空旧场景并登记底图（借用）。此后每个算子的结果立即上屏，
                // 走一步画一步（HDevelop 语义），缩放/平移/拖动后由显示层整体重放。
                // 底图始终登记原图（彩色图保持彩色显示，便于人工确认）。
                BeginScene(hImage);

                // 1.5 彩色图像先转单通道灰度：threshold / threshold_sub_pix 等分割算子
                // 均要求单通道输入；单通道相机图零开销直通。
                grayImage = ToGrayIfNeeded(hImage);
                HObject processImage = grayImage ?? hImage;

                // 2. 构造动态检测 ROI（有参考位置时开辟局部搜索框，否则全图搜索）。
                //    forceFullImage：降级全图重试时保留 expected 用于多候选择近，
                //    只跳过 ROI 裁剪——否则全图多候选时取第一个，极易选中伪特征。
                bool useRoi = expectedPx > 0 && expectedPy > 0 && !forceFullImage;
                if (useRoi)
                {
                    double r1 = Math.Max(0, expectedPy - searchRadius);
                    double c1 = Math.Max(0, expectedPx - searchRadius);
                    double r2 = Math.Min(imgHeight.D - 1, expectedPy + searchRadius);
                    double c2 = Math.Min(imgWidth.D - 1, expectedPx + searchRadius);

                    HOperatorSet.GenRectangle1(out roi, r1, c1, r2, c2);
                    // ROI 搜索框（绿色，全图坐标）—— 生成即上屏
                    SubmitRegion(roi, 0, 0, "green", 2);
                    // ReduceDomain 只缩小处理范围，坐标系不变：后续所有坐标均为全图坐标
                    HOperatorSet.ReduceDomain(processImage, roi, out imageReduced);
                }
                else
                {
                    // 修复 CloneObj 报错：使用 CopyImage
                    HOperatorSet.CopyImage(processImage, out imageReduced);
                }

                // 3. 图像分割与形态学筛选（每步结果立即上屏：阈值蓝 → 连通域紫 → 候选橙）
                HOperatorSet.Threshold(imageReduced, out thresholdRegion, ExtractOptions.ThresholdMin, ExtractOptions.ThresholdMax);
                if (DebugDrawProcessEnabled)
                {
                    SubmitRegion(thresholdRegion, 0, 0, "blue", 1);
                    SubmitText("蓝=阈值分割 紫=连通域 绿=ROI 橙=候选 青=XLD 红=拟合圆 黄=中心",
                        0, Math.Max(10, imgWidth.D - 560), "white");
                    SubmitText($"{(grayImage != null ? "彩色已转灰度(Rgb1ToGray)  " : "")}阈值区域数:{SafeCount(thresholdRegion)}",
                        30, Math.Max(10, imgWidth.D - 560), "white");
                }

                HOperatorSet.Connection(thresholdRegion, out connectedRegions);
                if (DebugDrawProcessEnabled)
                {
                    SubmitRegion(connectedRegions, 0, 0, "magenta", 1);
                    SubmitText($"连通域数:{SafeCount(connectedRegions)}",
                        60, Math.Max(10, imgWidth.D - 560), "white");
                }

                // 筛选圆度高于 MinCircularity、面积适中的特征区域
                HOperatorSet.SelectShape(
                    connectedRegions,
                    out selectedRegions,
                    new HTuple("circularity", "area"),
                    "and",
                    new HTuple(ExtractOptions.MinCircularity, ExtractOptions.MinArea),
                    new HTuple(1.0, ExtractOptions.MaxArea));

                HOperatorSet.CountObj(selectedRegions, out HTuple matchCount);
                if (matchCount.I <= 0)
                {
                    // 3.5 光照不均兜底（中心亮边缘暗场景）：全局阈值在暗边缘处分不到 Mark →
                    // 回退局部动态阈值（MeanImage + DynThreshold，暗+亮并集）重试一次。
                    // 动态阈值只看"像素与局部均值的偏差"，对整幅亮度渐变不敏感。
                    SubmitText("全局阈值无候选 → 回退局部动态阈值(抗光照不均)...",
                        160, Math.Max(10, imgWidth.D - 560), "yellow");
                    HObject meanImage = null;
                    HObject dynDark = null;
                    HObject dynLight = null;
                    HObject dynUnion = null;
                    try
                    {
                        // ★ 均值窗口修复（2026-09-03）：旧逻辑按 MaxArea 上限推算窗口尺寸——
                        //   MaxArea 默认 999999 → markDiameter≈1128 → meanWin≈3385px，
                        //   大于 ROI/局部图短边（SearchRadius=150 的 ROI 仅 ~300px），
                        //   mean_image 抛 HALCON #3033 "Filter size exceeds image size"，
                        //   兜底从未真正生效（日志"动态阈值兜底失败"刷屏）——暗/低对比 Mark
                        //   在全局阈值无候选后必失败（九点边缘点 / 旋转点掉点的直接根因）。
                        //   现改为：窗口 = 3×真实 Mark 直径（参考半径 2R；无参考时保守 ≥40px），
                        //   clamp 到 [31, 局部图短边-2] 且奇数；图过小时跳过兜底不抛错。
                        HOperatorSet.GetImageSize(imageReduced, out HTuple redRows, out HTuple redCols);
                        int shortSide = (int)Math.Min(redRows.D, redCols.D);
                        double markDiameterPx = _referenceMarkRadius > 1.0
                            ? _referenceMarkRadius * 2.0
                            : 60.0; // 无参考半径时保守取 60px 直径（约 3 倍于常见小 Mark）
                        int meanWin = (int)Math.Ceiling(markDiameterPx * 3.0) | 1; // 3×直径，奇数
                        if (meanWin < 31) meanWin = 31;
                        int cap = (shortSide - 2) | 1;
                        if (cap >= 31 && meanWin > cap) meanWin = cap;
                        if (meanWin < 31 || meanWin >= shortSide)
                        {
                            // 局部图太小无法承载均值窗口：跳过动态阈值兜底（不抛错）
                            LogBus.Warn(nameof(CalibrationService),
                                $"动态阈值兜底跳过（局部图短边 {shortSide}px 过小，均值窗口需 {meanWin}px）");
                            meanImage = null;
                        }
                        else
                        {
                            HOperatorSet.MeanImage(imageReduced, out meanImage, meanWin, meanWin);
                            HOperatorSet.DynThreshold(imageReduced, meanImage, out dynDark, 8, "dark");
                            HOperatorSet.DynThreshold(imageReduced, meanImage, out dynLight, 8, "light");
                            HOperatorSet.Union2(dynDark, dynLight, out dynUnion);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogBus.Warn(nameof(CalibrationService), $"动态阈值兜底失败（继续走全局阈值结果）: {ex.Message}");
                    }
                    finally
                    {
                        meanImage?.Dispose();
                        dynDark?.Dispose();
                        dynLight?.Dispose();
                    }

                    if (dynUnion != null && dynUnion.IsInitialized())
                    {
                        DisposeAndNull(ref thresholdRegion);
                        thresholdRegion = dynUnion;
                        dynUnion = null;
                        usedFallback = true; // 动态阈值兜底命中 → 报告打折提示
                        if (DebugDrawProcessEnabled)
                        {
                            SubmitRegion(thresholdRegion, 0, 0, "blue", 1);
                            SubmitText($"动态阈值区域数:{SafeCount(thresholdRegion)}",
                                190, Math.Max(10, imgWidth.D - 560), "white");
                        }
                        HOperatorSet.Connection(thresholdRegion, out connectedRegions);
                        HOperatorSet.SelectShape(
                            connectedRegions,
                            out selectedRegions,
                            new HTuple("circularity", "area"),
                            "and",
                            new HTuple(ExtractOptions.MinCircularity, ExtractOptions.MinArea),
                            new HTuple(1.0, ExtractOptions.MaxArea));
                        HOperatorSet.CountObj(selectedRegions, out matchCount);
                    }
                    // 兜底结果未被采用（null/未初始化/未转移）时释放，避免泄漏；
                    // 已转移到 thresholdRegion 的由外层 finally 统一释放
                    dynUnion?.Dispose();
                }

                // 候选区域（橙色，全图坐标）—— 筛选完成即上屏
                SubmitRegion(selectedRegions, 0, 0, "orange", 2);

                if (matchCount.I <= 0)
                {
                    // 失败路径：场景中已逐步上屏到当前步骤（底图/ROI/阈值/连通域/候选=0），
                    // 此处只补失败提示 —— 操作员能直接看清"阈值分割到了什么、候选为什么被筛光"
                    SubmitText($"几何筛选后候选数:0（圆度≥{ExtractOptions.MinCircularity:F2} 面积{ExtractOptions.MinArea}-{ExtractOptions.MaxArea}）",
                        100, Math.Max(10, imgWidth.D - 560), "white");
                    SubmitText($"#{pointIndex} 未识别到 Mark 点（检查光源/曝光/ROI）",
                        20, 20, "red");
                    LastMatchReport = ComposeCircleReport(null, 0, -1, -1, 0,
                        expectedPx, expectedPy, searchRadius, usedFallback, false,
                        $"[标定点 #{pointIndex}] 失败诊断：阈值分割区域数 {SafeCount(thresholdRegion)} → 几何筛选后候选 0 个。" +
                        $"当前参数 圆度≥{ExtractOptions.MinCircularity:F2} 面积 {ExtractOptions.MinArea:F0}-{ExtractOptions.MaxArea:F0}。" +
                        "若画面中 Mark 可见：放宽圆度下限或面积范围再试；若不可见则查光源/曝光/ROI。",
                        $"#{pointIndex}");
                    SubmitScoreOverlay(LastMatchReport);
                    return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 视觉算子未识别到符合几何圆度特征的 Mark 点，请检查光源与曝光！");
                }

                // 4. 高精度亚像素轮廓拟合
                HOperatorSet.ThresholdSubPix(imageReduced, out subPixelEdges, ExtractOptions.SubPixThreshold);
                HOperatorSet.SelectShapeXld(subPixelEdges, out selectedXld, "circularity", "and", ExtractOptions.MinCircularity, 1.0);
                // 亚像素轮廓（青色，全图坐标）—— 拟合完成即上屏
                SubmitXld(selectedXld, 0, 0, "cyan", 1);
                if (DebugDrawProcessEnabled)
                {
                    SubmitText($"几何筛选后:{SafeCount(selectedRegions)}  亚像素轮廓:{SafeCount(selectedXld)}",
                        130, Math.Max(10, imgWidth.D - 560), "white");
                }

                double finalPixelX;
                double finalPixelY;

                HOperatorSet.CountObj(selectedXld, out HTuple xldCount);
                if (xldCount.I > 0)
                {
                    HOperatorSet.FitCircleContourXld(selectedXld, "algebraic", -1, 0, 0, 3, 2,
                        out HTuple row, out HTuple col, out HTuple radius, out _, out _, out _);

                    // 多候选择优：先按参考半径排除伪特征（Mark 半径恒定，反光点半径不同），
                    // 再按离期望位置最近选择；无期望位置（全图）时选半径最接近参考者。
                    // ReduceDomain 坐标系不变，行/列即全图坐标，直接使用
                    int best = SelectBestCircleIndex(row, col, i => radius[i].D, expectedPx, expectedPy);
                    finalPixelX = col[best].D;
                    finalPixelY = row[best].D;
                    fitRadius = radius[best].D;
                    // 记录参考半径（首个成功识别的 Mark，此后用于伪特征过滤）
                    if (_referenceMarkRadius <= 1.0)
                    {
                        _referenceMarkRadius = fitRadius;
                        LogBus.Info(nameof(CalibrationService), $"[标定点 #{pointIndex}] 记录参考 Mark 半径: {fitRadius:F1}px");
                    }
                }
                else
                {
                    HOperatorSet.AreaCenter(selectedRegions, out HTuple area, out HTuple row, out HTuple col);
                    // 区域面积换算等效半径参与伪特征过滤
                    int best = SelectBestCircleIndex(row, col, i => Math.Sqrt(area[i].D / Math.PI), expectedPx, expectedPy);
                    finalPixelX = col[best].D;
                    finalPixelY = row[best].D;
                    if (_referenceMarkRadius <= 1.0)
                    {
                        _referenceMarkRadius = Math.Sqrt(area[best].D / Math.PI);
                    }
                }

                // 5. 结果层上屏（image 坐标系，跟随缩放平移）：拟合圆（红）→ 醒目结果标记（黑描边绿十字+圆环+文字）
                if (fitRadius > 0.0001)
                {
                    SubmitCircle(finalPixelY, finalPixelX, fitRadius, "red");
                }
                // 识别结果标注：把匹配分带进标签（如 "#5 92分"），随缩放跟随 —— 操作员不用切页看分数
                SubmitResultMarker(finalPixelY, finalPixelX,
                    $"#{pointIndex}  Px:{finalPixelX:F1}  Py:{finalPixelY:F1}",
                    imgWidth.D, imgHeight.D);

                // 质量报告：候选明细 + 综合分（圆度/半径一致性/唯一性/可预测性）
                LastMatchReport = ComposeCircleReport(selectedRegions, matchCount.I,
                    finalPixelX, finalPixelY, fitRadius,
                    expectedPx, expectedPy, searchRadius,
                    usedFallback || fitRadius <= 0.0001, true, null, $"#{pointIndex}");
                // 分数同步叠加到视图左上角（黄底绿字醒目；HDevelop 直通通道安全绘制）
                SubmitScoreOverlay(LastMatchReport);

                return Result<(double, double)>.Ok((finalPixelX, finalPixelY));
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CalibrationService), $"[标定点 #{pointIndex}] Halcon 算子执行异常: {ex.Message}");
                LastMatchReport = new FeatureMatchReport
                {
                    Success = false,
                    Score = 0,
                    Verdict = "失败",
                    Detail = $"[标定点 #{pointIndex}] 算子执行异常: {ex.Message}\n" +
                             "请检查图像格式/参数组合是否合法；日志已记录详细堆栈。"
                };
                return Result<(double, double)>.Fail($"算子执行异常: {ex.Message}");
            }
            finally
            {
                // 场景式所有权规则：Submit* 提交的是"显示副本"（CopyObj/MoveRegion/仿射平移产物），
                // 场景托管副本、本地句柄归本方法 —— 无论成功/失败/异常路径统一在此释放，
                // 无需任何 overlayHandedOff 之类的移交标志。
                // grayImage 为彩色转灰度的临时对象（单通道输入时为 null），此处统一释放。
                DisposeAndNull(ref grayImage);
                DisposeAndNull(ref roi);
                DisposeAndNull(ref imageReduced);
                DisposeAndNull(ref thresholdRegion);
                DisposeAndNull(ref connectedRegions);
                DisposeAndNull(ref selectedRegions);
                DisposeAndNull(ref subPixelEdges);
                DisposeAndNull(ref selectedXld);
            }
        }
        /// <summary>
        /// 【R 轴旋转中心标定】在 R 轴步进旋转指定角度后，提取偏心 Mark 点在当前视场中的精准坐标。
        /// featureType 决定走圆 Mark（圆度筛选+亚像素圆拟合）还是十字 Mark（骨架+直线交叉）算法。
        /// </summary>
        /// <param name="imageHandle">Halcon 图像句柄</param>
        /// <param name="angleDeg">当前 R 轴旋转绝对/相对物理角度（用于日志审计与轨迹拟合验证）</param>
        /// <param name="featureType">标定方案配置的特征类型（圆 / 十字）</param>
        /// <param name="expectedPx">上一次标定或理论估算的位置，用于开辟 ROI</param>
        /// <param name="expectedPy">上一次标定或理论估算的位置，用于开辟 ROI</param>
        /// <returns>提取到的偏心 Mark 点像素物理坐标</returns>
        public Result<(double PixelX, double PixelY)> ExtractRotationFeaturePoint(
            object imageHandle,
            double angleDeg,
            CalibrationFeatureType featureType,
            double expectedPx = -1,
            double expectedPy = -1)
        {
            var extractResult = ExtractFeaturePointByType(imageHandle, featureType, 0, expectedPx, expectedPy);

            if (!extractResult.Success)
            {
                LogBus.Warn(nameof(CalibrationService), $"R 轴在 {angleDeg:F1}° 位置未识别到偏心 Mark 特征点。");
            }

            return extractResult;
        }

        /// <summary>
        /// 【已采集点标记叠加】把目前所有已成功采集的标定点，以紧凑绿十字标记阵列
        /// 追加绘制到当前显示场景（不清空场景、不换底图，直接叠加在刚完成的算子
        /// 过程结果之上）。
        /// 用途：九点标定第三步逐点采样时，每采完一个点调用一次——视图窗口中除了
        /// 当前点的算子过程呈现（ROI/阈值/连通域/XLD/拟合圆/醒目标记），还累计呈现
        /// 所有已采集点的像素位置：识别正确时 9 个绿十字构成与走位网格一致的规则
        /// 3x3 阵列；误检点（伪特征/反光/暗角）表现为十字重叠、缺失或阵列畸变，
        /// 操作员目视即可发现哪个像素点取错。
        /// </summary>
        /// <param name="pointIndices">已采集点编号（1~9，用于标记标签）</param>
        /// <param name="pixelXs">已采集点像素 X（列）</param>
        /// <param name="pixelYs">已采集点像素 Y（行）</param>
        public void AppendCapturedMarks(int[] pointIndices, double[] pixelXs, double[] pixelYs)
        {
            if (pointIndices == null || pixelXs == null || pixelYs == null)
            {
                return;
            }
            int n = Math.Min(pointIndices.Length, Math.Min(pixelXs.Length, pixelYs.Length));
            for (int i = 0; i < n; i++)
            {
                // 紧凑标记：黑描边小十字 + 点号（当前点的大号醒目标记由提取流程绘制，
                // 此处统一用小尺寸阵列，9 点同屏不互相遮挡）
                SubmitCross(pixelYs[i], pixelXs[i], 46, "black");
                SubmitCross(pixelYs[i], pixelXs[i], 36, "green");
                SubmitText($"#{pointIndices[i]}", Math.Max(0, pixelYs[i] - 40), pixelXs[i] + 20, "black");
                SubmitText($"#{pointIndices[i]}", Math.Max(0, pixelYs[i] - 42), pixelXs[i] + 18, "yellow");
            }
            if (n > 0)
            {
                SubmitText($"已采集 {n} 点（绿十字阵列应与走位网格一致，重叠/畸变=误检）", 160, 20, "yellow");
            }
        }

        /// <summary>
        /// 按标定方案配置的特征类型路由提取算法（九点标定第三步采样 / 旋转采样统一入口）：
        /// CircleMark → ExtractFeaturePoint（阈值分割 + 圆度筛选 + 亚像素圆拟合）；
        /// CrossMark → ExtractCrossMarkPoint（骨架 + 直线拟合 + 交叉点）。
        /// 此前第三步采样写死圆算法，用户在第二步选择十字 Mark 时预览正常、采样必然失败——
        /// 本方法保证采样与预览使用同一套算法。
        /// </summary>
        public Result<(double PixelX, double PixelY)> ExtractFeaturePointByType(
            object imageHandle,
            CalibrationFeatureType featureType,
            int pointIndex,
            double expectedPx,
            double expectedPy,
            bool forceFullImage = false)
        {
            switch (featureType)
            {
                case CalibrationFeatureType.CrossMark:
                    return ExtractCrossMarkPoint(imageHandle, pointIndex, expectedPx, expectedPy, forceFullImage);
                case CalibrationFeatureType.TemplateMatch:
                    return ExtractTemplateMatchPoint(imageHandle, pointIndex, expectedPx, expectedPy, forceFullImage);
                case CalibrationFeatureType.CircleMark:
                default:
                    return ExtractFeaturePoint(imageHandle, pointIndex, expectedPx, expectedPy, forceFullImage);
            }
        }

        /// <summary>
        /// 【模板匹配特征提取】按模板名（全局模板库 Shape/NCC）在当前图像上做模板匹配定位特征中心（v2 datum 口径）：
        /// 1) 从 ExtractOptions 取模板名与参数（名称缺失/无候选 → 明确失败诊断）；
        /// 2) 有期望位置时在期望点 ±SearchRadius 开搜索窗口——引擎内与模板内建 SearchRoi 取交集
        ///    （ReduceDomain 不改变坐标系，匹配输出仍是全图坐标）；无期望位置或 forceFullImage=true 不加窗口；
        /// 3) 引擎入口=TemplateManager.MatchWithDatum——与生产 ShapeMatch 节点 / 模板管理页实拍验证同一条
        ///    "匹配锚点 → 卡尺精测 → datum"链路（唯一口径）：模板资产带卡尺且基准点=CircleCenter/LineIntersection 时，
        ///    采样点=卡尺亚像素精测覆盖的 datum；无卡尺/基准点=Point 时 datum=锚点直出（同一语义默认，无"旧路径"分支）。
        /// 与生产 ShapeMatch 节点同源——对光照/反光最稳，且能匹配"工件整体/正面图案"这类无圆十字 Mark 的特征源。
        /// </summary>
        private Result<(double PixelX, double PixelY)> ExtractTemplateMatchPoint(
            object imageHandle,
            int pointIndex,
            double expectedPx,
            double expectedPy,
            bool forceFullImage = false)
        {
            string templateName = ExtractOptions?.TemplateName;
            if (string.IsNullOrWhiteSpace(templateName))
            {
                LastMatchReport = new FeatureMatchReport
                {
                    Success = false,
                    Score = 0,
                    Verdict = "失败",
                    Detail = "[模板特征] 未选择模板：请在特征配置中选择已创建的模板（模板管理页创建），" +
                             "或暂时切回十字 / 圆特征；吸放式标定中可对工件正面整体建模板。"
                };
                return Result<(double, double)>.Fail("模板特征：未选择模板。请在特征配置中选择模板后再试。");
            }

            HObject hImage = ResolveHObject(imageHandle);
            if (hImage == null || !hImage.IsInitialized())
            {
                LastMatchReport = new FeatureMatchReport
                {
                    Success = false,
                    Score = 0,
                    Verdict = "失败",
                    Detail = $"[标定点 #{pointIndex}] 模板匹配提取失败：当前图像缓冲区无效或相机未成功取图。"
                };
                return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 提取失败：当前图像缓冲区无效或相机未成功取图。");
            }

            try
            {
                HOperatorSet.GetImageSize(hImage, out HTuple imgWidth, out HTuple imgHeight);
                BeginScene(hImage); // 场景：底图 + 后续标记叠加

                // 期望点搜索窗口：有期望位置且非强制全图时在期望点 ±SearchRadius 开窗
                // （引擎内与模板内建 SearchRoi 取交集；此处只画示意框，实际裁剪由引擎完成）
                bool hasExpected = expectedPx > 0 && expectedPy > 0;
                bool useRoi = hasExpected && !forceFullImage;
                if (useRoi)
                {
                    double sr = ExtractOptions.SearchRadius;
                    double r1 = Math.Max(0, expectedPy - sr);
                    double c1 = Math.Max(0, expectedPx - sr);
                    double r2 = Math.Min(imgHeight.D - 1, expectedPy + sr);
                    double c2 = Math.Min(imgWidth.D - 1, expectedPx + sr);
                    HObject roiBox = null;
                    try
                    {
                        HOperatorSet.GenRectangle1(out roiBox, r1, c1, r2, c2);
                        SubmitRegion(roiBox, 0, 0, "green", 2);
                    }
                    catch { /* 叠加失败不阻断主流程 */ }
                    finally
                    {
                        roiBox?.Dispose();
                    }
                }

                // v2 唯一口径：MatchWithDatum（匹配锚点→卡尺精测→datum），与生产 ShapeMatch 节点/实拍验证同一条链路
                var mgr = new TemplateManager();
                var outRes = mgr.MatchWithDatum(
                    templateName, hImage, ExtractOptions.TemplateMinScore,
                    ExtractOptions.TemplateAngleStart, ExtractOptions.TemplateAngleEnd,
                    hasExpected ? (double?)expectedPy : null,
                    hasExpected ? (double?)expectedPx : null,
                    useRoi ? (double?)ExtractOptions.SearchRadius : null);

                if (!outRes.Success)
                {
                    LastMatchReport = new FeatureMatchReport
                    {
                        Success = false,
                        Score = 0,
                        Verdict = "失败",
                        Detail = $"[标定点 #{pointIndex}] 模板匹配失败：{outRes.Message}"
                    };
                    SubmitText($"#{pointIndex} 模板匹配失败: {outRes.Message}", 20, 20, "red");
                    return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 模板匹配失败: {outRes.Message}");
                }

                var mo = outRes.Data;
                var chosen = mo.Match;
                if (chosen == null)
                {
                    string roiDesc = useRoi ? $"（期望点附近 ±{ExtractOptions.SearchRadius:F0}px）" : "（全图/模板内建搜索框）";
                    LastMatchReport = new FeatureMatchReport
                    {
                        Success = false,
                        Score = 0,
                        Verdict = "失败",
                        Detail = $"[标定点 #{pointIndex}] 模板 [{templateName}] {roiDesc} 无候选，" +
                                 $"最低分阈值 {ExtractOptions.TemplateMinScore:F2}。\n若画面中工件可见：降低 MinScore 或调整角度范围；" +
                                 "若不可见则检查模板视角/曝光/是否回到拍照位。"
                    };
                    SubmitText($"#{pointIndex} 模板 [{templateName}] 无候选（MinScore {ExtractOptions.TemplateMinScore:F2}）", 20, 20, "red");
                    return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 模板匹配无候选（MinScore={ExtractOptions.TemplateMinScore:F2}）。");
                }

                // 候选清单（引擎已按分数降序；供评分报告回放）
                var hits = mo.Candidates;

                // 采样点 = datum（v2 消费端特征点）：模板带卡尺且 CircleCenter/LineIntersection 时=卡尺亚像素
                // 精测覆盖；无卡尺/基准点=Point 时=匹配锚点直出（同一语义默认）——喂九点拟合的就是该点。
                double px = mo.DatumCol;
                double py = mo.DatumRow;
                double score100 = Math.Max(1, Math.Min(99, chosen.Score * 100));

                // 结果层上屏：醒目标记 + 分数文字（左上角）+ 精测信息
                string refineTag = mo.RefinedByCaliper ? $"  精测:{mo.RefineKind}" : "";
                SubmitResultMarker(py, px,
                    $"#{pointIndex}  Px:{px:F1}  Py:{py:F1}  分:{chosen.Score * 100:F0}  角:{chosen.RotateDegree:F1}°{refineTag}",
                    imgWidth.D, imgHeight.D);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"#{pointIndex} 模板 [{templateName}] 候选 {hits.Count} 个，综合匹配分 {score100:F0}/100 · {ScoreVerdict(score100)}");
                sb.AppendLine($"匹配分: {chosen.Score:F3}（模板 MinScore {ExtractOptions.TemplateMinScore:F2}）  角度: {chosen.RotateDegree:F1}°");
                sb.AppendLine($"锚点: ({chosen.PixelCol:F1}, {chosen.PixelRow:F1})  →  基准点(datum): ({px:F1}, {py:F1})" +
                    (mo.RefinedByCaliper ? $"  [{mo.RefineKind} 卡尺亚像素精测]" : "  [锚点直出]") +
                    (useRoi ? "  [期望点±局部搜索]" : "  [全图/模板内建搜索框]"));
                sb.AppendLine(score100 >= 70
                    ? "→ 模板命中分数高，走位/旋转采样不易丢点。"
                    : "→ 分数偏低：建议降低 MinScore 或检查模板与当前成像（光照/角度）差异。");
                if (mo.Issues.Count > 0)
                {
                    sb.AppendLine("⚠ " + string.Join("；", mo.Issues));
                }

                var candidates = new List<MatchCandidateInfo>();
                for (int i = 0; i < Math.Min(hits.Count, 8); i++)
                {
                    var h = hits[i];
                    candidates.Add(new MatchCandidateInfo
                    {
                        Index = i + 1,
                        IsSelected = h == chosen,
                        PixelX = h.PixelCol,
                        PixelY = h.PixelRow,
                        Score = h.Score * 100,
                        Radius = 0,
                        Circularity = 0
                    });
                }

                LastMatchReport = new FeatureMatchReport
                {
                    Success = true,
                    Score = score100,
                    Verdict = ScoreVerdict(score100),
                    Detail = sb.ToString(),
                    CandidateCount = hits.Count,
                    Candidates = candidates,
                    PixelX = px,
                    PixelY = py,
                    UsedFallback = false
                };
                return Result<(double, double)>.Ok((px, py));
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CalibrationService), $"模板匹配提取异常: {ex.Message}");
                LastMatchReport = new FeatureMatchReport
                {
                    Success = false,
                    Score = 0,
                    Verdict = "失败",
                    Detail = $"[标定点 #{pointIndex}] 模板匹配算子异常: {ex.Message}"
                };
                return Result<(double, double)>.Fail($"模板匹配算子执行异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 特征预览提取（第二步"特征配置"验证用）：按用户选择的特征类型在当前图像上提取特征中心，
        /// 并利用注入的 DisplayContext 视窗句柄，把识别过程与结果（底图 / 候选区域 /
        /// 拟合圆或拟合直线 / 中心十字 / 文字标注）叠加绘制到视图窗口。
        /// 圆形 Mark → 复用 ExtractFeaturePoint（阈值分割 + 圆度筛选 + 亚像素圆拟合）；
        /// 十字 Mark → ExtractCrossMarkPoint（骨架 + 直线拟合 + 交叉点，不依赖模板）。
        /// 注意：此方法只做"预览验证"，不写入任何标定点数据。
        /// </summary>
        public Result<(double PixelX, double PixelY)> ExtractFeaturePreview(object imageHandle, CalibrationFeatureType featureType)
        {
            switch (featureType)
            {
                case CalibrationFeatureType.CircleMark:
                    // pointIndex=0：预览模式编号；expectedPx/expectedPy=-1：无参考位置，全图搜索
                    return ExtractFeaturePoint(imageHandle, 0, -1, -1);
                case CalibrationFeatureType.CrossMark:
                    return ExtractCrossMarkPoint(imageHandle, 0, -1, -1);
                case CalibrationFeatureType.TemplateMatch:
                    // 预览 = 全图模板匹配（无期望位置）：所见即所得验证模板与当前成像是否匹配
                    return ExtractTemplateMatchPoint(imageHandle, 0, -1, -1);
                default:
                    return Result<(double, double)>.Fail($"不支持的特征类型: {featureType}");
            }
        }

        /// <summary>
        /// 【十字 Mark 特征提取】几何结构法检测十字中心：
        /// 1) 阈值分割（先暗后亮，兼容暗/亮十字；全局阈值均无候选时回退局部动态阈值抗光照不均）
        ///    → 连通域 → 按面积/宽高筛选候选区域；
        /// 2) 对候选区域骨架化（Skeleton），每条骨架段用 FitLineContourXld 拟合直线；
        /// 3) 找一对近似垂直（90°±30°）且交点靠近两线段的直线，交点即十字中心；
        /// 4) 找不到垂直对时兜底取最大候选区域中心。
        /// 支持 seed ROI（expectedPx/expectedPy>0 时 ReduceDomain 局部搜索；坐标系统一为全图坐标）。
        /// 检测过程通过 DisplayContext 叠加绘制：底图 → 候选区域(橙) → 拟合直线(青) → 中心十字+文字(黄)。
        /// 说明：当前为无模板几何法，适合配置步骤快速验证；如需高鲁棒模板匹配，
        /// 可扩展 CreateShapeModel/FindShapeModel（需要模板训练交互）。
        /// </summary>
        private Result<(double PixelX, double PixelY)> ExtractCrossMarkPoint(
            object imageHandle,
            int pointIndex,
            double expectedPx,
            double expectedPy,
            bool forceFullImage = false)
        {
            HObject hImage = ResolveHObject(imageHandle);
            if (hImage == null || !hImage.IsInitialized())
            {
                return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 十字 Mark 提取失败：当前图像缓冲区无效或相机未成功取图。");
            }

            HObject grayImage = null;
            HObject roi = null;
            HObject imageReduced = null;
            HObject thresholdRegion = null;
            HObject connectedRegions = null;
            HObject candidateRegions = null;
            HObject skeleton = null;
            HObject skelConnected = null;
            HObject skelSelected = null;
            HObject xldContours = null;

            try
            {
                HOperatorSet.GetImageSize(hImage, out HTuple imgWidth, out HTuple imgHeight);

                // 场景第一步：清空旧场景并登记底图（借用），此后走一步画一步
                BeginScene(hImage);

                // 彩色图像先转单通道灰度（threshold / skeleton 均要求单通道输入）；
                // 底图仍登记原图，显示层保持彩色。
                grayImage = ToGrayIfNeeded(hImage);
                HObject processImage = grayImage ?? hImage;

                // seed ROI：有参考位置时局部搜索（ReduceDomain 不改变坐标系，后续坐标均为全图坐标）；
                // forceFullImage：降级全图重试时保留 expected 用于候选选择，只跳过 ROI 裁剪
                bool useRoi = expectedPx > 0 && expectedPy > 0 && !forceFullImage;
                if (useRoi)
                {
                    double searchRadius = ExtractOptions.SearchRadius;
                    double r1 = Math.Max(0, expectedPy - searchRadius);
                    double c1 = Math.Max(0, expectedPx - searchRadius);
                    double r2 = Math.Min(imgHeight.D - 1, expectedPy + searchRadius);
                    double c2 = Math.Min(imgWidth.D - 1, expectedPx + searchRadius);
                    HOperatorSet.GenRectangle1(out roi, r1, c1, r2, c2);
                    SubmitRegion(roi, 0, 0, "green", 2);
                    HOperatorSet.ReduceDomain(processImage, roi, out imageReduced);
                    processImage = imageReduced;
                }

                // 1. 阈值分割：优先暗色 Mark（暗背景亮 Mark 场景自动切换）
                HOperatorSet.Threshold(processImage, out thresholdRegion, 0, ExtractOptions.CrossDarkThresholdMax);
                HOperatorSet.Connection(thresholdRegion, out connectedRegions);
                HOperatorSet.SelectShape(
                    connectedRegions,
                    out candidateRegions,
                    new HTuple("area", "width", "height"),
                    "and",
                    new HTuple(ExtractOptions.CrossMinArea, ExtractOptions.CrossMinSize, ExtractOptions.CrossMinSize),
                    new HTuple(ExtractOptions.CrossMaxArea, ExtractOptions.CrossMaxSize, ExtractOptions.CrossMaxSize));
                HOperatorSet.CountObj(candidateRegions, out HTuple candCount);
                if (DebugDrawProcessEnabled)
                {
                    SubmitRegion(thresholdRegion, 0, 0, "blue", 1);
                    SubmitRegion(connectedRegions, 0, 0, "magenta", 1);
                    SubmitText("蓝=阈值分割 紫=连通域 绿=骨架 亮绿=骨架XLD 橙=候选 青=拟合直线 黄=中心",
                        10, Math.Max(10, imgWidth.D - 640), "white");
                    SubmitText($"{(grayImage != null ? "彩色已转灰度(Rgb1ToGray)  " : "")}阈值区域数:{SafeCount(thresholdRegion)}  连通域数:{SafeCount(connectedRegions)}",
                        40, Math.Max(10, imgWidth.D - 640), "white");
                }
                if (candCount.I <= 0)
                {
                    // 暗色无结果 → 亮色十字（暗背景）：重开场景，亮色路径重新逐步上屏
                    DisposeObjects(thresholdRegion, connectedRegions, candidateRegions);
                    thresholdRegion = connectedRegions = candidateRegions = null;
                    BeginScene(hImage);
                    HOperatorSet.Threshold(processImage, out thresholdRegion, ExtractOptions.CrossLightThresholdMin, 255);
                    HOperatorSet.Connection(thresholdRegion, out connectedRegions);
                    HOperatorSet.SelectShape(
                        connectedRegions,
                        out candidateRegions,
                        new HTuple("area", "width", "height"),
                        "and",
                        new HTuple(ExtractOptions.CrossMinArea, ExtractOptions.CrossMinSize, ExtractOptions.CrossMinSize),
                        new HTuple(ExtractOptions.CrossMaxArea, ExtractOptions.CrossMaxSize, ExtractOptions.CrossMaxSize));
                    HOperatorSet.CountObj(candidateRegions, out candCount);
                    if (DebugDrawProcessEnabled)
                    {
                        SubmitRegion(thresholdRegion, 0, 0, "blue", 1);
                        SubmitRegion(connectedRegions, 0, 0, "magenta", 1);
                        SubmitText("蓝=阈值分割(亮色Mark) 紫=连通域 橙=候选 青=拟合直线 黄=中心",
                            10, Math.Max(10, imgWidth.D - 640), "white");
                    }
                }

                if (candCount.I <= 0)
                {
                    // 1.5 光照不均兜底（中心亮边缘暗场景）：暗/亮全局阈值均无候选 →
                    // 局部动态阈值（MeanImage + DynThreshold 暗+亮并集）重试一次，
                    // 只看"像素与局部均值的偏差"，对整幅亮度渐变不敏感。
                    SubmitText("全局阈值(暗/亮)无候选 → 回退局部动态阈值(抗光照不均)...",
                        20, 20, "yellow");
                    HObject meanImage = null;
                    HObject dynDark = null;
                    HObject dynLight = null;
                    HObject dynUnion = null;
                    try
                    {
                        // 均值窗口需显著大于十字整体尺寸（约 3 倍），保证十字落在"局部背景"内
                        int meanWin = Math.Max(31, (int)ExtractOptions.CrossMaxSize * 3 | 1);
                        HOperatorSet.MeanImage(processImage, out meanImage, meanWin, meanWin);
                        HOperatorSet.DynThreshold(processImage, meanImage, out dynDark, 8, "dark");
                        HOperatorSet.DynThreshold(processImage, meanImage, out dynLight, 8, "light");
                        HOperatorSet.Union2(dynDark, dynLight, out dynUnion);
                    }
                    catch (Exception ex)
                    {
                        LogBus.Warn(nameof(CalibrationService), $"十字动态阈值兜底失败（继续走全局阈值结果）: {ex.Message}");
                    }
                    finally
                    {
                        meanImage?.Dispose();
                        dynDark?.Dispose();
                        dynLight?.Dispose();
                    }

                    if (dynUnion != null && dynUnion.IsInitialized())
                    {
                        DisposeAndNull(ref thresholdRegion);
                        thresholdRegion = dynUnion;
                        dynUnion = null;
                        if (DebugDrawProcessEnabled)
                        {
                            SubmitRegion(thresholdRegion, 0, 0, "blue", 1);
                            SubmitText($"动态阈值区域数:{SafeCount(thresholdRegion)}",
                                50, Math.Max(10, imgWidth.D - 640), "white");
                        }
                        HOperatorSet.Connection(thresholdRegion, out connectedRegions);
                        HOperatorSet.SelectShape(
                            connectedRegions,
                            out candidateRegions,
                            new HTuple("area", "width", "height"),
                            "and",
                            new HTuple(ExtractOptions.CrossMinArea, ExtractOptions.CrossMinSize, ExtractOptions.CrossMinSize),
                            new HTuple(ExtractOptions.CrossMaxArea, ExtractOptions.CrossMaxSize, ExtractOptions.CrossMaxSize));
                        HOperatorSet.CountObj(candidateRegions, out candCount);
                    }
                    // 兜底结果未被采用时释放；已转移到 thresholdRegion 的由外层 finally 统一释放
                    dynUnion?.Dispose();
                }

                if (candCount.I <= 0)
                {
                    // 失败路径：场景中已逐步上屏阈值/连通域结果，此处只补失败提示
                    SubmitText($"#{pointIndex} 未检测到十字 Mark（检查光源/曝光/对焦）", 20, 20, "red");
                    LastMatchReport = ComposeCrossReport(candCount.I, false, 0, -1, -1, false,
                        $"[标定点 #{pointIndex}] 十字 Mark 失败诊断：暗/亮全局阈值+动态阈值均无候选。" +
                        $"当前参数 面积 {ExtractOptions.CrossMinArea:F0}-{ExtractOptions.CrossMaxArea:F0}，宽高 {ExtractOptions.CrossMinSize:F0}-{ExtractOptions.CrossMaxSize:F0}。" +
                        "若画面中十字可见：放宽面积/宽高范围或调整暗/亮阈值再试。",
                        $"#{pointIndex}");
                    SubmitScoreOverlay(LastMatchReport);
                    return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 十字 Mark 提取失败：阈值分割后无候选区域，请检查光源与曝光。");
                }

                // 候选区域（橙色）—— 筛选完成即上屏
                SubmitRegion(candidateRegions, 0, 0, "orange", 2);

                // 2. 骨架化候选区域，按骨架长度筛选（骨架绿 → 骨架轮廓亮绿，逐步上屏）
                HOperatorSet.Skeleton(candidateRegions, out skeleton);
                if (DebugDrawProcessEnabled)
                {
                    SubmitRegion(skeleton, 0, 0, "green", 1);
                }
                HOperatorSet.Connection(skeleton, out skelConnected);
                HOperatorSet.SelectShape(skelConnected, out skelSelected, "area", "and", 20, 999999);
                HOperatorSet.GenContourRegionXld(skelSelected, out xldContours, "border");
                if (DebugDrawProcessEnabled)
                {
                    SubmitXld(xldContours, 0, 0, "lime green", 1);
                    SubmitText($"阈值:{SafeCount(thresholdRegion)}  连通域:{SafeCount(connectedRegions)}  候选:{SafeCount(candidateRegions)}  骨架段:{SafeCount(xldContours)}",
                        70, Math.Max(10, imgWidth.D - 640), "white");
                }
                HOperatorSet.CountObj(xldContours, out HTuple xldCount);

                // 3. 每条骨架段拟合直线（拟合一条上屏一条，青色）
                var lines = new List<(double R1, double C1, double R2, double C2)>();
                for (int i = 1; i <= xldCount.I; i++)
                {
                    HObject one = null;
                    try
                    {
                        HOperatorSet.SelectObj(xldContours, out one, i);
                        HOperatorSet.FitLineContourXld(one, "tukey", -1, 0, 5, 2,
                            out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2, out _, out _, out _);
                        if (r1.Length > 0)
                        {
                            lines.Add((r1[0].D, c1[0].D, r2[0].D, c2[0].D));
                            // 拟合直线（青色）：算子产生结果立即上屏（提交即所有权转移，未提交时兜底释放）
                            HOperatorSet.GenContourPolygonXld(out HObject lineXld,
                                new HTuple(new double[] { r1[0].D, r2[0].D }),
                                new HTuple(new double[] { c1[0].D, c2[0].D }));
                            Submit(ref lineXld, "cyan", 2);
                            lineXld?.Dispose();
                        }
                    }
                    catch
                    {
                        // 单条骨架拟合失败忽略，继续下一条
                    }
                    finally
                    {
                        one?.Dispose();
                    }
                }

                // 4. 找近似垂直的直线对求交点（十字两臂）。
                //    多对垂直直线同时成立时，选交点离期望位置最近的一对（有 seed 时），
                //    避免交点落在远处伪特征上；无 seed（预览）保留第一对成立者。
                double centerRow = imgHeight.D / 2;
                double centerCol = imgWidth.D / 2;
                bool found = false;
                bool hasExpected = expectedPx > 0 && expectedPy > 0;
                double bestPairDistSq = double.MaxValue;
                double chosenAngleDevDeg = 0; // 被采纳直线对的夹角偏差（用于匹配分）
                const double AngleTolerance = 30.0;   // 与 90° 的允许偏差
                const double MaxDistToLines = 120.0;  // 交点距两线段的最大距离

                for (int i = 0; i < lines.Count; i++)
                {
                    if (found && !hasExpected)
                    {
                        break; // 无 seed 时第一对成立即可
                    }
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        var a = lines[i];
                        var b = lines[j];
                        double angA = Math.Atan2(a.R2 - a.R1, a.C2 - a.C1) * 180.0 / Math.PI;
                        double angB = Math.Atan2(b.R2 - b.R1, b.C2 - b.C1) * 180.0 / Math.PI;
                        double diff = Math.Abs(NormalizeAngle180(angA - angB));
                        if (Math.Abs(diff - 90.0) > AngleTolerance)
                        {
                            continue;
                        }

                        // 两直线解析求交
                        double d1r = a.R2 - a.R1;
                        double d1c = a.C2 - a.C1;
                        double d2r = b.R2 - b.R1;
                        double d2c = b.C2 - b.C1;
                        double det = d1r * d2c - d1c * d2r;
                        if (Math.Abs(det) < 1e-9)
                        {
                            continue;
                        }
                        double t = ((b.R1 - a.R1) * d2c - (b.C1 - a.C1) * d2r) / det;
                        double ix = a.R1 + t * d1r;
                        double iy = a.C1 + t * d1c;

                        // 交点应落在两臂延长线附近，防止远端交叉误匹配
                        if (PointToSegmentDistance(ix, iy, a.R1, a.C1, a.R2, a.C2) > MaxDistToLines ||
                            PointToSegmentDistance(ix, iy, b.R1, b.C1, b.R2, b.C2) > MaxDistToLines)
                        {
                            continue;
                        }

                        if (hasExpected)
                        {
                            // 多对成立：选交点离期望位置最近的一对
                            double dx = iy - expectedPx;
                            double dy = ix - expectedPy;
                            double distSq = dx * dx + dy * dy;
                            if (found && distSq >= bestPairDistSq)
                            {
                                continue;
                            }
                            bestPairDistSq = distSq;
                        }
                        centerRow = ix;
                        centerCol = iy;
                        found = true;
                        chosenAngleDevDeg = Math.Abs(diff - 90.0);
                    }
                }

                if (!found)
                {
                    // 兜底：取候选区域中心作为十字中心。
                    // 有 seed 选离期望最近的候选；无 seed 取面积最大的（最可能是十字本体）
                    HOperatorSet.AreaCenter(candidateRegions, out HTuple area, out HTuple row, out HTuple col);
                    int pickIdx;
                    if (hasExpected)
                    {
                        pickIdx = SelectClosestIndex(row, col, expectedPx, expectedPy);
                    }
                    else
                    {
                        pickIdx = 0;
                        for (int k = 1; k < area.Length; k++)
                        {
                            if (area[k].D > area[pickIdx].D)
                            {
                                pickIdx = k;
                            }
                        }
                    }
                    centerRow = row[pickIdx].D;
                    centerCol = col[pickIdx].D;
                }

                // 5. 结果层上屏（image 坐标系）：醒目结果标记（黑描边绿十字+圆环+文字）；
                //    未找到垂直直线对时文字注明"兜底"
                string centerLabel = found
                    ? $"#{pointIndex}  Px:{centerCol:F1}  Py:{centerRow:F1}"
                    : $"#{pointIndex}·兜底最大区域  Px:{centerCol:F1}  Py:{centerRow:F1}";
                SubmitResultMarker(centerRow, centerCol, centerLabel);

                // 质量报告（十字）：found=精确命中按夹角误差给分；兜底取区域中心则降分提示
                LastMatchReport = ComposeCrossReport(candCount.I, found, chosenAngleDevDeg,
                    centerCol, centerRow, true, null, $"#{pointIndex}");
                SubmitScoreOverlay(LastMatchReport);

                return Result<(double, double)>.Ok((centerCol, centerRow));
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CalibrationService), $"十字 Mark 提取异常: {ex.Message}");
                LastMatchReport = new FeatureMatchReport
                {
                    Success = false,
                    Score = 0,
                    Verdict = "失败",
                    Detail = $"[标定点 #{pointIndex}] 十字 Mark 算子执行异常: {ex.Message}"
                };
                return Result<(double, double)>.Fail($"十字 Mark 算子执行异常: {ex.Message}");
            }
            finally
            {
                // 场景式所有权规则：Submit* 提交的是"显示副本"，场景托管副本、本地句柄归本方法，
                // 无论成功/失败/异常路径统一在此释放。
                // grayImage 为彩色转灰度的临时对象（单通道输入时为 null），此处统一释放。
                DisposeAndNull(ref grayImage);
                DisposeAndNull(ref roi);
                DisposeAndNull(ref imageReduced);
                DisposeAndNull(ref thresholdRegion);
                DisposeAndNull(ref connectedRegions);
                DisposeAndNull(ref candidateRegions);
                DisposeAndNull(ref skeleton);
                DisposeAndNull(ref skelConnected);
                DisposeAndNull(ref skelSelected);
                DisposeAndNull(ref xldContours);
            }
        }

        /// <summary>批量释放 HObject 中间对象（disp 绘制不持有引用，可直接释放）</summary>
        private static void DisposeObjects(params HObject[] objects)
        {
            foreach (var obj in objects)
            {
                if (obj != null && obj.IsInitialized())
                {
                    obj.Dispose();
                }
            }
        }

        /// <summary>
        /// 释放 HObject 并置 null（异常安全）。用于提取方法的 finally 统一清理：
        /// 已通过 Submit(ref obj) 提交场景的对象会被置 null，此处自动跳过（防双重释放）；
        /// 其余本地句柄（含 Submit* 未提交的"显示副本"兜底路径）在此统一释放。
        /// </summary>
        private static void DisposeAndNull(ref HObject obj)
        {
            if (obj != null)
            {
                try
                {
                    if (obj.IsInitialized())
                    {
                        obj.Dispose();
                    }
                }
                catch
                {
                    // 释放失败忽略，对象本身即将被 GC 回收
                }
                obj = null;
            }
        }
    }
}