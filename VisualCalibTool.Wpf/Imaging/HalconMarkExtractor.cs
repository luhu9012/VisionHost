using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using HalconDotNet;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Imaging
{
    /// <summary>
    /// ★★ 三类特征提取器（圆 / 十字 / 模板）。这是 P0 第 2 步的主体。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 为什么这个类必须"逐条对等"主项目 CalibrationService
    /// ══════════════════════════════════════════════════════════════════
    /// 主项目里那段提取代码是现场调出来的，每条纪律都对应过一次事故：
    ///
    /// ① <b>ROI 局部搜索优先</b>：有期望位置就只在邻域找。全图搜索在有干扰的现场
    ///    几乎必然选到反光点，而且慢。局部失败才降级全图，且<b>降级后仍用 expected 择近</b>
    ///    —— 否则全图多候选时会取第一个，那正是"九点边缘点掉点"的直接形态。
    ///
    /// ② <b>多候选择近</b>：候选 > 1 时按"离期望位置最近 + 参考半径匹配"择一。
    ///
    /// ③ <b>参考半径 ±40% 滤伪</b>：同一次标定里 Mark 是同一个物理特征，半径恒定；
    ///    反光点/螺丝/字符的半径往往不同。首个成功识别的圆自动记录参考半径，
    ///    此后 ±40% 以外排除。★ 换板/换相机<b>必须</b> ResetMarkReference。
    ///
    /// ④ <b>动态阈值兜底</b>：全局阈值无候选时，用 MeanImage + DynThreshold（暗∪亮）重试一次。
    ///    它只看"像素与局部均值的偏差"，对整幅亮度渐变不敏感 —— 是中心亮边缘暗场景的救命稻草。
    ///    主项目曾因均值窗口按 MaxArea 推算（默认推到 3385px）导致兜底<b>从未真正生效</b>；
    ///    本实现把窗口按"3×真实 Mark 直径"算并 clamp 到图像短边，这个 bug 不会复现。
    ///
    /// ⑤ <b>质量分报告</b>：0~100 + 成分明细，让操作员有依据地调参，而不是盲调。
    ///
    /// ⑥ <b>走一步画一步</b>：ROI 框 / 阈值区域 / 连通域 / 候选 / 亚像素轮廓 / 拟合圆 / 中心
    ///    全部以<b>普通类型</b>（<see cref="ExtractionTrace"/>）输出，界面才能"边跑边看"。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 坐标系约定（很容易搞反，这里写死）
    /// ══════════════════════════════════════════════════════════════════
    /// HALCON 的 (row, col) = 图像 (Y, X)。本工具对外统一用 <see cref="Vec2"/>：
    ///   <c>Vec2.X = col</c>（列/图像 X），<c>Vec2.Y = row</c>（行/图像 Y）。
    /// 而叠加绘制接口 <c>ICalibOverlayTarget</c> 的参数是 <b>(row, col)</b>。两者在这一层转换。
    /// </summary>
    public sealed class HalconMarkExtractor : IFeatureExtractor, ITemplateTrainer, IDisposable
    {
        // ── 跨调用状态：参考半径 ──
        private double _referenceRadiusPx;

        // ── 模板库（自建）──
        private readonly Dictionary<string, HTuple> _shapeModels = new Dictionary<string, HTuple>(StringComparer.Ordinal);
        private readonly Dictionary<string, HTuple> _nccModels = new Dictionary<string, HTuple>(StringComparer.Ordinal);
        private readonly Dictionary<string, TemplateSpec> _specs = new Dictionary<string, TemplateSpec>(StringComparer.Ordinal);

        private bool _disposed;

        /// <summary>宿主模板库（可为 null = 独立模式，走自建库）。</summary>
        public IExternalTemplateLibrary ExternalLibrary;

        /// <summary>累计提取次数（诊断用）。</summary>
        public int ExtractCount;

        /// <summary>累计走了全图降级重试的次数（这个数字偏大说明现场 ROI 期望位置不可靠）。</summary>
        public int FullImageFallbackCount;

        /// <summary>累计走了动态阈值兜底的次数（偏大说明光照不均）。</summary>
        public int DynamicThresholdCount;

        /// <summary>累计因「亮底上的暗标记」极性整图反色的次数（诊断用：偏大说明现场是暗 Mark 世界）。</summary>
        public int InvertedPolarityCount;

        public HalconMarkExtractor()
        {
            TraceMaxContours = 24;
            TraceMaxPointsPerContour = 400;
        }

        /// <summary>过程轨迹：最多画几个连通域。</summary>
        public int TraceMaxContours { get; set; }

        /// <summary>过程轨迹：单个轮廓最多采样多少点。</summary>
        public int TraceMaxPointsPerContour { get; set; }

        public bool IsSimulated
        {
            get { return false; }
        }

        public double ReferenceRadiusPx
        {
            get { return _referenceRadiusPx; }
        }

        public void ResetMarkReference()
        {
            _referenceRadiusPx = 0.0;
        }

        // ══════════════════════════════════════════════════════════════
        //  入口
        // ══════════════════════════════════════════════════════════════

        public CalibObservation Extract(byte[] rawGray, int width, int height, MarkSpec spec,
            double expectedPixelX, double expectedPixelY, int sampleIndex, ExtractionTrace trace)
        {
            var obs = new CalibObservation
            {
                Index = sampleIndex,
                FeatureKind = spec == null ? FeatureKind.CircleMark : spec.Kind,
                Trace = trace
            };

            if (trace == null)
            {
                trace = new ExtractionTrace { Enabled = false };
            }

            if (spec == null)
            {
                obs.RejectReason = "没有给出要找什么特征（MarkSpec 为空）。";
                obs.Error = CalibError.Create(CalibFailureKind.FeatureNotFound, obs.RejectReason);
                return obs;
            }

            if (spec.Options == null)
            {
                spec.Options = new FeatureExtractOptions();
            }

            if (rawGray == null || rawGray.Length < (long)width * height || width <= 0 || height <= 0)
            {
                obs.RejectReason = string.Format(CultureInfo.InvariantCulture,
                    "帧数据非法（{0} 字节，需要 {1}×{2}={3}）。相机没取到图时不要继续往下走。",
                    rawGray == null ? 0 : rawGray.Length, width, height, (long)width * height);
                obs.Error = CalibError.Create(CalibFailureKind.GrabTimeout, obs.RejectReason);
                return obs;
            }

            ExtractCount++;
            var sw = Stopwatch.StartNew();

            var expect = new Expect
            {
                Col = expectedPixelX,
                Row = expectedPixelY,
                Has = expectedPixelX > 0.0 && expectedPixelY > 0.0
            };

            using (var bag = new HalconHandleBag())
            {
                // ★ FromRawGray 内部立即拷贝像素并 pin 只包住构造本身，句柄由 bag 统一释放
                HObject image = bag.Add(HalconFrameSource.FromRawGray(rawGray, width, height));

                // ★★ 极性统一：亮底上的暗标记 → 整图反色，再按"暗底上的亮标记"主路径走。
                //   反色保几何（像素位置不变），阈值带 / 亚像素 / 坐标换算全都不用动，
                //   不会长出第二套并行提取逻辑。
                //   ★ 模板匹配不反色：模板与搜索图必须同处一个灰度世界（训练图什么样，
                //     搜索图就什么样），否则 find_shape_model 的梯度极性对不上。
                if (spec.Kind != FeatureKind.TemplateMatch
                    && spec.Options.MarkPolarity == MarkPolarity.DarkMarkOnBrightBackground)
                {
                    HOperatorSet.InvertImage(image, out HObject inverted);
                    image = bag.Add(inverted);
                    InvertedPolarityCount++;
                }

                try
                {
                    switch (spec.Kind)
                    {
                        case FeatureKind.CrossMark:
                            ExtractCross(bag, image, spec, expect, obs, trace);
                            break;

                        case FeatureKind.TemplateMatch:
                            ExtractTemplate(bag, image, rawGray, width, height, spec, expect, obs, trace);
                            break;

                        default:
                            ExtractCircle(bag, image, spec, expect, obs, trace);
                            break;
                    }
                }
                catch (HOperatorException hex)
                {
                    obs.RejectReason = "HALCON 算子异常：" + hex.Message;
                    obs.Error = CalibError.Create(CalibFailureKind.FeatureNotFound, obs.RejectReason);
                    obs.Report = BuildFailureReport(
                        string.Format(CultureInfo.InvariantCulture, "HALCON 算子异常：{0}", hex.Message));
                }
                catch (Exception ex)
                {
                    obs.RejectReason = "提取过程异常：" + ex.Message;
                    obs.Error = CalibError.Create(CalibFailureKind.Internal, obs.RejectReason);
                    obs.Report = BuildFailureReport(obs.RejectReason);
                }
            }

            sw.Stop();
            obs.ElapsedMs = sw.ElapsedMilliseconds;

            FinalizeObservation(obs, trace);
            return obs;
        }

        private static void FinalizeObservation(CalibObservation obs, ExtractionTrace trace)
        {
            if (obs.Report == null)
            {
                obs.Report = BuildFailureReport(obs.RejectReason ?? "未产生质量报告");
            }

            obs.MatchScore = obs.Report.Score;
            obs.Verdict = obs.Report.Verdict;
            obs.CandidateCount = obs.Report.CandidateCount;
            obs.UsedFallback = obs.Report.UsedFallback;
            obs.UsedFullImageFallback = obs.Report.UsedFullImageFallback;
            obs.Quality = FeatureQualityScorer.ToUnitScore(obs.Report.Score);

            if (obs.Trace == null && trace.Enabled)
            {
                obs.Trace = trace;
            }
        }

        private static FeatureMatchReport BuildFailureReport(string detail)
        {
            return new FeatureMatchReport
            {
                Success = false,
                Score = 0.0,
                Verdict = "失败",
                Detail = detail
            };
        }

        /// <summary>期望位置（像素）。★ X = col，Y = row。</summary>
        private struct Expect
        {
            public double Col;
            public double Row;
            public bool Has;
        }

        // ══════════════════════════════════════════════════════════════
        //  一、圆 Mark
        // ══════════════════════════════════════════════════════════════

        private void ExtractCircle(HalconHandleBag bag, HObject image, MarkSpec spec, Expect expect,
            CalibObservation obs, ExtractionTrace trace)
        {
            bool[] roiModes = expect.Has ? new bool[] { true, false } : new bool[] { false };
            string lastFail = null;
            bool failedAfterFullImageFallback = false;

            for (int m = 0; m < roiModes.Length; m++)
            {
                bool useRoi = roiModes[m];

                // ★ 末轮失败时<b>不立刻释放</b>：要把它"阈值分割到了什么、候选为什么被筛光"画出来，
                //   否则用户只看到一句"没找到"，无从判断是参数问题还是画面问题。
                bool isLastMode = m == roiModes.Length - 1;
                var attempt = new CircleAttempt();
                try
                {
                    bool ok = CircleAttemptOnce(image, spec, expect, useRoi, attempt, out lastFail);

                    if (ok)
                    {
                        // 只有最终采用的那一次才渲染 trace（失败尝试的中间结果画出来只会干扰判读）
                        RenderCircleTrace(image, spec, expect, useRoi, attempt, trace);
                        FillCircleSuccess(obs, spec, attempt, expect, useRoi, failedAfterFullImageFallback, trace);
                        return;
                    }

                    if (isLastMode)
                    {
                        RenderCircleTrace(image, spec, expect, useRoi, attempt, trace);
                    }
                    else
                    {
                        failedAfterFullImageFallback = true;
                    }
                }
                finally
                {
                    attempt.Dispose();
                }
            }

            // ── 全败 ──
            obs.HasPixel = false;
            obs.RejectReason = lastFail ?? "未识别到符合几何特征的 Mark 点。";
            obs.Error = CalibError.Create(CalibFailureKind.FeatureNotFound, obs.RejectReason)
                .WithSample(obs.Index);
            obs.Report = FeatureQualityScorer.ScoreCircle(new CircleScoreInput
            {
                Success = false,
                FailDetail = string.Format(CultureInfo.InvariantCulture,
                    "{0} 当前参数：圆度≥{1:F2}，面积 {2:F0}-{3:F0}，搜索半径 {4:F0}px。"
                    + "若画面中 Mark 可见：放宽圆度下限或面积范围再试；若不可见则查光源/曝光/对焦。",
                    obs.RejectReason, spec.Options.MinCircularity, spec.Options.MinArea,
                    spec.Options.MaxArea, spec.EffectiveSearchRadius)
            });
        }

        /// <summary>把一次成功的圆提取结果装配进观测（含质量报告与参考半径登记）。</summary>
        private void FillCircleSuccess(CalibObservation obs, MarkSpec spec, CircleAttempt attempt,
            Expect expect, bool useRoi, bool didFullImageFallback, ExtractionTrace trace)
        {
            obs.HasPixel = true;
            obs.Pixel = new Vec2(attempt.Col, attempt.Row);

            var input = new CircleScoreInput
            {
                Success = true,
                PixelX = attempt.Col,
                PixelY = attempt.Row,
                CandidateCount = attempt.CandidateCount,
                Circularity = AttemptCircularity(attempt, attempt.Col, attempt.Row),
                RadiusPx = attempt.Radius > 0.0 ? attempt.Radius : AttemptRadius(attempt, attempt.Col, attempt.Row),
                ReferenceRadiusPx = _referenceRadiusPx,
                NearestOtherDistancePx = NearestOtherDistance(attempt, attempt.Col, attempt.Row),
                ExpectedPx = expect.Has ? expect.Col : 0.0,
                ExpectedPy = expect.Has ? expect.Row : 0.0,
                SearchRadiusPx = spec.EffectiveSearchRadius,
                UsedFallback = attempt.UsedDynThreshold || attempt.UsedCentroidFallback || attempt.UsedFullImage
            };

            obs.Report = FeatureQualityScorer.ScoreCircle(input);
            obs.Report.Candidates.AddRange(attempt.Candidates);
            obs.Report.UsedFullImageFallback = !useRoi;

            // ★ 参考半径：首个成功识别即记录，此后用于伪特征过滤
            if (_referenceRadiusPx <= 1.0 && attempt.Radius > 1.0)
            {
                _referenceRadiusPx = attempt.Radius;
            }

            if (attempt.UsedDynThreshold)
            {
                DynamicThresholdCount++;
            }

            if (!useRoi)
            {
                FullImageFallbackCount++;
            }

            if (trace != null && trace.Enabled)
            {
                double row = 20.0;
                if (didFullImageFallback)
                {
                    trace.AddText(row, 20.0, "（局部 ROI 未命中 → 已降级全图重试）", "yellow");
                    row += 24.0;
                }

                if (attempt.UsedDynThreshold)
                {
                    trace.AddText(row, 20.0, "（全局阈值无候选 → 已回退局部动态阈值）", "yellow");
                }
            }
        }

        /// <summary>一轮圆提取尝试（ROI 或全图）。</summary>
        private bool CircleAttemptOnce(HObject image, MarkSpec spec, Expect expect, bool useRoi,
            CircleAttempt attempt, out string fail)
        {
            fail = null;
            FeatureExtractOptions opt = spec.Options;
            double searchRadius = spec.EffectiveSearchRadius;
            attempt.UsedFullImage = !useRoi;

            HOperatorSet.GetImageSize(image, out HTuple imgW, out HTuple imgH);

            HObject processImage = image;
            if (useRoi)
            {
                double r1 = Math.Max(0.0, expect.Row - searchRadius);
                double c1 = Math.Max(0.0, expect.Col - searchRadius);
                double r2 = Math.Min(imgH.D - 1.0, expect.Row + searchRadius);
                double c2 = Math.Min(imgW.D - 1.0, expect.Col + searchRadius);

                HOperatorSet.GenRectangle1(out attempt.Roi, r1, c1, r2, c2);
                // ★ ReduceDomain 只缩小处理范围，坐标系不变 —— 后续坐标全部是全图坐标，
                //   绝不做平移修正（加了就是双重偏移，Mark 会找不准）
                HOperatorSet.ReduceDomain(image, attempt.Roi, out attempt.ImageReduced);
                processImage = attempt.ImageReduced;
            }

            // ── 全局阈值分割 + 连通域 + 几何筛选 ──
            if (!CircleSegment(processImage, attempt, opt, out fail))
            {
                return false;
            }

            // 亚像素轮廓
            HOperatorSet.ThresholdSubPix(processImage, out attempt.SubPixEdges, opt.SubPixThreshold);
            HOperatorSet.SelectShapeXld(attempt.SubPixEdges, out attempt.XldSelected,
                "circularity", "and", opt.MinCircularity, 1.0);

            HOperatorSet.CountObj(attempt.XldSelected, out HTuple xldCount);

            if (xldCount.I > 0)
            {
                HOperatorSet.FitCircleContourXld(attempt.XldSelected, "algebraic", -1, 0, 0, 3, 2,
                    out HTuple row, out HTuple col, out HTuple radius, out _, out _, out _);

                int best = SelectBestCircle(row, col, i => radius[i].D, expect,
                    _referenceRadiusPx, opt.ReferenceRadiusTolerance);

                attempt.Col = colLength(col, best);
                attempt.Row = rowLength(row, best);
                attempt.Radius = radius.Length > best ? radius[best].D : 0.0;
                attempt.Success = true;
                return true;
            }

            // ── 亚像素轮廓也没有 → 退化用区域中心（主项目的兜底路径，报告会打折）──
            HOperatorSet.AreaCenter(attempt.CandidatesRegion, out HTuple area, out HTuple arow, out HTuple acol);

            double[] radii = new double[area.Length];
            for (int i = 0; i < area.Length; i++)
            {
                radii[i] = area[i].D > 0.0 ? Math.Sqrt(area[i].D / Math.PI) : 0.0;
            }

            int chosen = SelectBestCircle(arow, acol, i => radii[i],
                expect, _referenceRadiusPx, opt.ReferenceRadiusTolerance);

            attempt.Col = acolLength(acol, chosen);
            attempt.Row = arowLength(arow, chosen);
            attempt.Radius = chosen < radii.Length ? radii[chosen] : 0.0;
            attempt.UsedCentroidFallback = true;
            attempt.Success = true;
            return true;
        }

        /// <summary>阈值 + 连通域 + 几何筛选，含"全局无候选 → 动态阈值兜底"。</summary>
        private bool CircleSegment(HObject processImage, CircleAttempt attempt, FeatureExtractOptions opt,
            out string fail)
        {
            fail = null;

            HOperatorSet.Threshold(processImage, out attempt.ThresholdRegion, opt.ThresholdMin, opt.ThresholdMax);
            HOperatorSet.Connection(attempt.ThresholdRegion, out attempt.ConnectedRegions);
            SelectCircleCandidates(attempt.ConnectedRegions, out attempt.CandidatesRegion, opt);
            HOperatorSet.CountObj(attempt.CandidatesRegion, out HTuple count);

            if (count.I > 0)
            {
                attempt.CandidateCount = count.I;
                FillCircleCandidates(attempt, opt);
                return true;
            }

            // ── 动态阈值兜底（抗光照不均）──
            HObject meanImage = null;
            HObject dynDark = null;
            HObject dynLight = null;
            HObject dynUnion = null;
            string dynFail = null;
            try
            {
                HOperatorSet.GetImageSize(processImage, out HTuple redRows, out HTuple redCols);
                int shortSide = (int)Math.Min(redRows.D, redCols.D);

                // ★ 均值窗口的口径统一在 ExtractionPolicy（纯函数）里 —— 见那里的长注释：
                //   主项目曾按 MaxArea 推算出事（窗口 3385px > ROI 短边 300px → HALCON #3033），
                //   导致这条兜底<b>从未真正生效</b>；把它搬成纯函数后，离线自检能直接喂边界值钉死。
                double markDiameterPx = _referenceRadiusPx > 1.0
                    ? _referenceRadiusPx * 2.0
                    : ExtractionPolicy.DefaultFeatureDiameterPx;
                int meanWin = ExtractionPolicy.MeanWindow(markDiameterPx, shortSide);

                if (meanWin > 0)
                {
                    HOperatorSet.MeanImage(processImage, out meanImage, meanWin, meanWin);
                    HOperatorSet.DynThreshold(processImage, meanImage, out dynDark, opt.DynThresholdDelta, "dark");
                    HOperatorSet.DynThreshold(processImage, meanImage, out dynLight, opt.DynThresholdDelta, "light");
                    HOperatorSet.Union2(dynDark, dynLight, out dynUnion);
                }
                else
                {
                    // ★ 跳过兜底也必须留话：否则报告只会写「全局与动态阈值都没出候选」，
                    //   而真相是「动态阈值根本没跑」—— 这两句要靠人猜，正是最贵的那类故障。
                    dynFail = ExtractionPolicy.ExplainNoMeanWindow(markDiameterPx, shortSide);
                }
            }
            catch (Exception ex)
            {
                // ★ 兜底失败不应该把主流程打断，但<b>原因必须留下来</b> ——
                //   主项目历史上正是这条兜底静默失效（HALCON #3033 窗口过大），
                //   于是"暗 Mark 在图像边缘必失败"而没人知道为什么。
                //   这里把异常写进失败原因，让它在报告里可见。
                dynUnion = null;
                dynFail = string.Format(CultureInfo.InvariantCulture,
                    "动态阈值兜底异常：{0}：{1}", ex.GetType().Name, ex.Message);
            }
            finally
            {
                SafeDispose(ref meanImage);
                SafeDispose(ref dynDark);
                SafeDispose(ref dynLight);
            }

            if (dynUnion != null && dynUnion.IsInitialized())
            {
                SafeDispose(ref attempt.ThresholdRegion);
                attempt.ThresholdRegion = dynUnion;
                dynUnion = null;
                attempt.UsedDynThreshold = true;

                HOperatorSet.Connection(attempt.ThresholdRegion, out attempt.ConnectedRegions);
                SelectCircleCandidates(attempt.ConnectedRegions, out attempt.CandidatesRegion, opt);
                HOperatorSet.CountObj(attempt.CandidatesRegion, out count);
            }

            SafeDispose(ref dynUnion);

            if (count.I <= 0)
            {
                fail = string.Format(CultureInfo.InvariantCulture,
                    "阈值分割区域 {0} 个 → 几何筛选后候选 0 个（圆度≥{1:F2}，面积 {2:F0}-{3:F0}，"
                    + "全局阈值与动态阈值都没有出候选）。{4}",
                    SafeCount(attempt.ThresholdRegion), opt.MinCircularity, opt.MinArea, opt.MaxArea,
                    dynFail == null ? string.Empty : "｜" + dynFail);
                return false;
            }

            attempt.CandidateCount = count.I;
            FillCircleCandidates(attempt, opt);
            return true;
        }

        private static void SelectCircleCandidates(HObject connectedRegions, out HObject selected,
            FeatureExtractOptions opt)
        {
            HOperatorSet.SelectShape(connectedRegions, out selected,
                new HTuple(new string[] { "circularity", "area" }),
                "and",
                new HTuple(new double[] { opt.MinCircularity, opt.MinArea }),
                new HTuple(new double[] { 1.0, opt.MaxArea }));
        }

        /// <summary>把候选明细填进尝试结果（界面要列出"找到了哪几个候选"）。</summary>
        private static void FillCircleCandidates(CircleAttempt attempt, FeatureExtractOptions opt)
        {
            try
            {
                HObject regions = attempt.CandidatesRegion;
                if (regions == null || !regions.IsInitialized())
                {
                    return;
                }

                HOperatorSet.AreaCenter(regions, out HTuple areas, out HTuple rows, out HTuple cols);
                HOperatorSet.RegionFeatures(regions, "circularity", out HTuple circs);
                HOperatorSet.SmallestCircle(regions, out HTuple cRows, out HTuple cCols, out HTuple cRadii);

                int n = areas.Length;
                int top = Math.Min(n, 8);
                for (int i = 0; i < top; i++)
                {
                    double area = areas[i].D;
                    attempt.Candidates.Add(new MatchCandidateInfo
                    {
                        Index = i + 1,
                        PixelX = cols[i].D,
                        PixelY = rows[i].D,
                        Area = area,
                        // 优先用 smallest_circle 的半径；异常时退化为面积等效半径
                        Radius = cRadii.Length > i && cRadii[i].D > 0.0
                            ? cRadii[i].D
                            : (area > 0.0 ? Math.Sqrt(area / Math.PI) : 0.0),
                        Circularity = circs.Length > i ? circs[i].D : 0.5
                    });
                }
            }
            catch (Exception)
            {
                // 候选明细只用于展示，组装失败不影响识别结果
            }
        }

        private void RenderCircleTrace(HObject image, MarkSpec spec, Expect expect, bool useRoi,
            CircleAttempt attempt, ExtractionTrace trace)
        {
            if (trace == null || !trace.Enabled)
            {
                return;
            }

            trace.Clear();

            if (useRoi && expect.Has && attempt.Roi != null)
            {
                double sr = spec.EffectiveSearchRadius;
                trace.AddRectangle(Math.Max(0.0, expect.Row - sr), Math.Max(0.0, expect.Col - sr),
                    expect.Row + sr, expect.Col + sr, "green", 2, 10);
                trace.AddText(Math.Max(0.0, expect.Row - sr), Math.Max(0.0, expect.Col - sr),
                    "ROI 局部搜索", "green", 81);
            }
            else if (attempt.Roi == null)
            {
                trace.AddText(20, 20, "全图搜索（无期望位置或局部未命中）", "yellow", 81);
            }

            TraceRegion(attempt.ThresholdRegion, trace, "blue", 60);
            TraceRegion(attempt.ConnectedRegions, trace, "magenta", 1);
            TraceRegion(attempt.CandidatesRegion, trace, "orange", 30);

            // 候选圆圈（橙色）
            for (int i = 0; i < attempt.Candidates.Count; i++)
            {
                MatchCandidateInfo c = attempt.Candidates[i];
                if (c.Radius > 0.5)
                {
                    trace.AddCircle(c.PixelY, c.PixelX, c.Radius, "orange", 1, 30);
                }
            }

            // 亚像素轮廓 + 拟合圆 + 中心十字
            TraceXld(attempt.XldSelected, trace, "cyan", 400);
            if (attempt.Success && attempt.Radius > 0.5)
            {
                trace.AddCircle(attempt.Row, attempt.Col, attempt.Radius, "red", 2, 50);
            }

            if (attempt.Success)
            {
                trace.AddCross(attempt.Row, attempt.Col, 14.0, "yellow", 2, 70);
                trace.AddText(attempt.Row + 20.0, attempt.Col + 16.0,
                    string.Format(CultureInfo.InvariantCulture, "P({0:F1},{1:F1}) r{2:F1}px",
                        attempt.Col, attempt.Row, attempt.Radius),
                    "yellow", 82);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  二、十字 Mark
        // ══════════════════════════════════════════════════════════════

        private void ExtractCross(HalconHandleBag bag, HObject image, MarkSpec spec, Expect expect,
            CalibObservation obs, ExtractionTrace trace)
        {
            bool[] roiModes = expect.Has ? new bool[] { true, false } : new bool[] { false };
            string lastFail = null;
            bool didFullImageFallback = false;

            for (int m = 0; m < roiModes.Length; m++)
            {
                bool useRoi = roiModes[m];
                bool isLastMode = m == roiModes.Length - 1;
                var attempt = new CrossAttempt();
                try
                {
                    if (CrossAttemptOnce(image, spec, expect, useRoi, attempt, out lastFail))
                    {
                        RenderCrossTrace(spec, expect, useRoi, attempt, trace);

                        obs.HasPixel = true;
                        obs.Pixel = new Vec2(attempt.Col, attempt.Row);

                        obs.Report = FeatureQualityScorer.ScoreCross(
                            attempt.CandidateCount, attempt.FoundPerpendicularPair,
                            attempt.AngleDevDeg, !attempt.FoundPerpendicularPair || !useRoi);
                        obs.Report.PixelX = attempt.Col;
                        obs.Report.PixelY = attempt.Row;
                        obs.Report.UsedFullImageFallback = !useRoi;

                        if (!useRoi)
                        {
                            FullImageFallbackCount++;
                        }

                        if (attempt.UsedDynThreshold)
                        {
                            DynamicThresholdCount++;
                        }

                        if (trace != null && trace.Enabled && didFullImageFallback)
                        {
                            trace.AddText(44.0, 20.0, "（局部 ROI 未命中 → 已降级全图重试）", "yellow");
                        }

                        return;
                    }

                    if (isLastMode)
                    {
                        // 末轮失败：把"阈值分割到了什么、候选为什么一个都没有"画出来
                        RenderCrossTrace(spec, expect, useRoi, attempt, trace);
                    }
                    else
                    {
                        didFullImageFallback = true;
                    }
                }
                finally
                {
                    attempt.Dispose();
                }
            }

            obs.HasPixel = false;
            obs.RejectReason = lastFail ?? "未检测到十字 Mark。";
            obs.Error = CalibError.Create(CalibFailureKind.FeatureNotFound, obs.RejectReason)
                .WithSample(obs.Index);
            obs.Report = BuildFailureReport(string.Format(CultureInfo.InvariantCulture,
                "{0} 当前参数：面积 {1:F0}-{2:F0}，宽高 {3:F0}-{4:F0}，暗阈值≤{5:F0}，亮阈值≥{6:F0}。"
                + "若画面中十字可见：放宽面积/宽高范围或调整暗/亮阈值再试。",
                obs.RejectReason, spec.Options.CrossMinArea, spec.Options.CrossMaxArea,
                spec.Options.CrossMinSize, spec.Options.CrossMaxSize,
                spec.Options.CrossDarkThresholdMax, spec.Options.CrossLightThresholdMin));
        }

        private bool CrossAttemptOnce(HObject image, MarkSpec spec, Expect expect, bool useRoi,
            CrossAttempt attempt, out string fail)
        {
            fail = null;
            FeatureExtractOptions opt = spec.Options;

            HOperatorSet.GetImageSize(image, out HTuple imgW, out HTuple imgH);

            HObject processImage = image;
            if (useRoi)
            {
                double sr = spec.EffectiveSearchRadius;
                double r1 = Math.Max(0.0, expect.Row - sr);
                double c1 = Math.Max(0.0, expect.Col - sr);
                double r2 = Math.Min(imgH.D - 1.0, expect.Row + sr);
                double c2 = Math.Min(imgW.D - 1.0, expect.Col + sr);
                HOperatorSet.GenRectangle1(out attempt.Roi, r1, c1, r2, c2);
                HOperatorSet.ReduceDomain(image, attempt.Roi, out attempt.ImageReduced);
                processImage = attempt.ImageReduced;
            }

            // ── 1) 先找暗十字，再找亮十字，再动态阈值兜底 ──
            if (!CrossSegment(processImage, attempt, opt, out fail))
            {
                return false;
            }

            // ── 2) 骨架化 → 在交叉点处拆开 → 得到"每条臂"各自成为一个连通域 ──
            //
            // ★ 为什么必须多这一步 junctions_skeleton 拆分（对主项目的一处刻意改进）：
            //   真实十字是一个<b>连通</b>的加号形区域。只做 skeleton + connection 的话，
            //   整根骨架是<b>一个</b>连通域，于是只能拟合出<b>一条</b>直线 ——
            //   "找近似垂直的直线对"这条路径永远进不去，每次都会静默退化成
            //   "取最大候选区域中心"的兜底（报告里看到的是分数被压到 45 分）。
            //   拆掉交叉点后，两条臂各自成段，精确十字中心那条路径才真正可达。
            //   兜底路径原样保留 —— 所以改动只会更好，不会更差。
            HOperatorSet.Skeleton(attempt.CandidatesRegion, out attempt.Skeleton);
            HOperatorSet.JunctionsSkeleton(attempt.Skeleton, out attempt.SkeletonEnds, out attempt.SkeletonJunctions);
            HOperatorSet.Difference(attempt.Skeleton, attempt.SkeletonJunctions, out attempt.SkeletonArms);
            HOperatorSet.Connection(attempt.SkeletonArms, out attempt.SkeletonArmRegions);
            HOperatorSet.SelectShape(attempt.SkeletonArmRegions, out attempt.SkeletonSelected,
                "area", "and", 20, 999999);
            HOperatorSet.GenContourRegionXld(attempt.SkeletonSelected, out attempt.SkeletonXld, "border");

            HOperatorSet.CountObj(attempt.SkeletonXld, out HTuple xldCount);

            // ── 3) 每条骨架段拟合直线 ──
            var lines = new List<double[]>(xldCount.I);
            for (int i = 1; i <= xldCount.I; i++)
            {
                HObject one = null;
                try
                {
                    HOperatorSet.SelectObj(attempt.SkeletonXld, out one, i);
                    HOperatorSet.FitLineContourXld(one, "tukey", -1, 0, 5, 2,
                        out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2,
                        out _, out _, out _);
                    if (r1.Length > 0)
                    {
                        lines.Add(new double[] { r1[0].D, c1[0].D, r2[0].D, c2[0].D });
                    }
                }
                catch (Exception)
                {
                    // 单条骨架拟合失败忽略，继续下一条
                }
                finally
                {
                    SafeDispose(ref one);
                }
            }

            attempt.Lines = lines;

            // ── 4) 找近似垂直（90°±30°）且交点靠近期望位置的直线对 ──
            double bestRow = double.NaN;
            double bestCol = double.NaN;
            double bestAngleDev = double.NaN;
            double bestScore = double.MaxValue;

            if (lines.Count >= 2)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        double angI = LineAngleDeg(lines[i]);
                        double angJ = LineAngleDeg(lines[j]);
                        double dev = Math.Abs(Math.Abs(Normalize180(angI - angJ)) - 90.0);
                        if (dev > 30.0)
                        {
                            continue;
                        }

                        HOperatorSet.IntersectionLines(
                            new HTuple(lines[i][0]), new HTuple(lines[i][1]),
                            new HTuple(lines[i][2]), new HTuple(lines[i][3]),
                            new HTuple(lines[j][0]), new HTuple(lines[j][1]),
                            new HTuple(lines[j][2]), new HTuple(lines[j][3]),
                            out HTuple iRow, out HTuple iCol, out HTuple isOverlapping);

                        if (iRow.Length == 0
                            || (isOverlapping.Length > 0 && isOverlapping[0].I == 1))
                        {
                            // 平行/重合 → 没有唯一交点，这一对作废
                            continue;
                        }

                        double score = expect.Has
                            ? DistanceSq(iCol[0].D - expect.Col, iRow[0].D - expect.Row)
                            : dev;

                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestRow = iRow[0].D;
                            bestCol = iCol[0].D;
                            bestAngleDev = dev;
                        }
                    }
                }
            }

            if (bestRow == double.NaN || bestCol == double.NaN)
            {
                // ── 兜底：最大候选区域的中心（报告会明确标注"这不是精确十字中心"）──
                HOperatorSet.AreaCenter(attempt.CandidatesRegion, out HTuple areas, out HTuple rows, out HTuple cols);
                if (areas.Length == 0)
                {
                    fail = "骨架化后没有任何可用候选区域。";
                    return false;
                }

                int pick = 0;
                if (expect.Has && rows.Length > 0)
                {
                    pick = SelectClosestIndex(rows, cols, expect);
                }

                attempt.Row = rows[pick].D;
                attempt.Col = cols[pick].D;
                attempt.FoundPerpendicularPair = false;
                attempt.AngleDevDeg = double.NaN;
                attempt.Success = true;
                return true;
            }

            attempt.Row = bestRow;
            attempt.Col = bestCol;
            attempt.FoundPerpendicularPair = true;
            attempt.AngleDevDeg = bestAngleDev;
            attempt.Success = true;
            return true;
        }

        private bool CrossSegment(HObject processImage, CrossAttempt attempt, FeatureExtractOptions opt,
            out string fail)
        {
            fail = null;

            // 1) 暗色十字
            CrossSelect(processImage, opt, 0.0, opt.CrossDarkThresholdMax, out attempt.ThresholdRegion,
                out attempt.ConnectedRegions, out attempt.CandidatesRegion);
            HOperatorSet.CountObj(attempt.CandidatesRegion, out HTuple count);
            attempt.ThresholdMode = "暗色";

            // 2) 亮色十字
            if (count.I <= 0)
            {
                SafeDispose(ref attempt.ThresholdRegion);
                SafeDispose(ref attempt.ConnectedRegions);
                SafeDispose(ref attempt.CandidatesRegion);
                CrossSelect(processImage, opt, opt.CrossLightThresholdMin, 255.0,
                    out attempt.ThresholdRegion, out attempt.ConnectedRegions, out attempt.CandidatesRegion);
                HOperatorSet.CountObj(attempt.CandidatesRegion, out count);
                attempt.ThresholdMode = "亮色";
            }

            // 3) 动态阈值兜底
            string dynFail = null;
            if (count.I <= 0)
            {
                HObject meanImage = null;
                HObject dynDark = null;
                HObject dynLight = null;
                HObject dynUnion = null;
                try
                {
                    HOperatorSet.GetImageSize(processImage, out HTuple rows, out HTuple cols);
                    int shortSide = (int)Math.Min(rows.D, cols.D);

                    // ★ 均值窗口口径与圆链共用同一处（ExtractionPolicy）：
                    //   窗口需显著大于十字整体尺寸（约 3×），并在放不下时返回 0 而不是硬跑。
                    int meanWin = ExtractionPolicy.MeanWindow(opt.CrossMaxSize, shortSide);

                    if (meanWin > 0)
                    {
                        HOperatorSet.MeanImage(processImage, out meanImage, meanWin, meanWin);
                        HOperatorSet.DynThreshold(processImage, meanImage, out dynDark, opt.DynThresholdDelta, "dark");
                        HOperatorSet.DynThreshold(processImage, meanImage, out dynLight, opt.DynThresholdDelta, "light");
                        HOperatorSet.Union2(dynDark, dynLight, out dynUnion);
                    }
                    else
                    {
                        dynFail = ExtractionPolicy.ExplainNoMeanWindow(opt.CrossMaxSize, shortSide);
                    }
                }
                catch (Exception ex)
                {
                    // ★ 以前这里是裸 catch 静默吞掉 —— 与主项目"兜底静默失效"同一个形态。
                    //   异常本身不算致命（兜底失败不该打断主流程），但<b>原因必须留下来</b>。
                    dynUnion = null;
                    dynFail = string.Format(CultureInfo.InvariantCulture,
                        "动态阈值兜底异常：{0}：{1}", ex.GetType().Name, ex.Message);
                }
                finally
                {
                    SafeDispose(ref meanImage);
                    SafeDispose(ref dynDark);
                    SafeDispose(ref dynLight);
                }

                if (dynUnion != null && dynUnion.IsInitialized())
                {
                    SafeDispose(ref attempt.ThresholdRegion);
                    SafeDispose(ref attempt.ConnectedRegions);
                    SafeDispose(ref attempt.CandidatesRegion);
                    attempt.ThresholdRegion = dynUnion;
                    dynUnion = null;
                    attempt.UsedDynThreshold = true;
                    attempt.ThresholdMode = "动态阈值";

                    HOperatorSet.Connection(attempt.ThresholdRegion, out attempt.ConnectedRegions);
                    CrossSelectShape(attempt.ConnectedRegions, out attempt.CandidatesRegion, opt);
                    HOperatorSet.CountObj(attempt.CandidatesRegion, out count);
                }

                SafeDispose(ref dynUnion);
            }

            if (count.I <= 0)
            {
                fail = string.Format(CultureInfo.InvariantCulture,
                    "暗/亮全局阈值 + 动态阈值都没有出候选（面积 {0:F0}-{1:F0}，宽高 {2:F0}-{3:F0}）。",
                    opt.CrossMinArea, opt.CrossMaxArea, opt.CrossMinSize, opt.CrossMaxSize)
                    + (dynFail == null ? string.Empty : "｜" + dynFail);
                return false;
            }

            attempt.CandidateCount = count.I;
            return true;
        }

        private static void CrossSelect(HObject processImage, FeatureExtractOptions opt,
            double thrMin, double thrMax,
            out HObject thresholdRegion, out HObject connectedRegions, out HObject candidates)
        {
            HOperatorSet.Threshold(processImage, out thresholdRegion, thrMin, thrMax);
            HOperatorSet.Connection(thresholdRegion, out connectedRegions);
            CrossSelectShape(connectedRegions, out candidates, opt);
        }

        private static void CrossSelectShape(HObject connectedRegions, out HObject candidates,
            FeatureExtractOptions opt)
        {
            HOperatorSet.SelectShape(connectedRegions, out candidates,
                new HTuple(new string[] { "area", "width", "height" }),
                "and",
                new HTuple(new double[] { opt.CrossMinArea, opt.CrossMinSize, opt.CrossMinSize }),
                new HTuple(new double[] { opt.CrossMaxArea, opt.CrossMaxSize, opt.CrossMaxSize }));
        }

        private void RenderCrossTrace(MarkSpec spec, Expect expect, bool useRoi, CrossAttempt attempt,
            ExtractionTrace trace)
        {
            if (trace == null || !trace.Enabled)
            {
                return;
            }

            trace.Clear();

            if (useRoi && expect.Has)
            {
                double sr = spec.EffectiveSearchRadius;
                trace.AddRectangle(Math.Max(0.0, expect.Row - sr), Math.Max(0.0, expect.Col - sr),
                    expect.Row + sr, expect.Col + sr, "green", 2, 10);
            }

            trace.AddText(20, 20, "阈值模式：" + attempt.ThresholdMode, "white", 81);
            TraceRegion(attempt.ThresholdRegion, trace, "blue", 60);
            TraceRegion(attempt.ConnectedRegions, trace, "magenta", 1);
            TraceRegion(attempt.CandidatesRegion, trace, "orange", 30);
            TraceRegion(attempt.Skeleton, trace, "green", 60);
            TraceXld(attempt.SkeletonXld, trace, "lime green", 300);

            // 拟合直线（青色）
            if (attempt.Lines != null)
            {
                for (int i = 0; i < attempt.Lines.Count; i++)
                {
                    trace.AddPolyline(
                        new double[] { attempt.Lines[i][0], attempt.Lines[i][2] },
                        new double[] { attempt.Lines[i][1], attempt.Lines[i][3] },
                        "cyan", 1, false, 40);
                }
            }

            if (attempt.Success)
            {
                trace.AddCross(attempt.Row, attempt.Col, 14.0, "yellow", 2, 70);
                trace.AddText(attempt.Row + 20.0, attempt.Col + 16.0,
                    attempt.FoundPerpendicularPair
                        ? string.Format(CultureInfo.InvariantCulture, "十字中心 P({0:F1},{1:F1}) 夹角误差{2:F1}°",
                            attempt.Col, attempt.Row, attempt.AngleDevDeg)
                        : string.Format(CultureInfo.InvariantCulture, "兜底：最大候选中心 P({0:F1},{1:F1})（非精确十字中心）",
                            attempt.Col, attempt.Row),
                    "yellow", 82);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  三、模板匹配
        // ══════════════════════════════════════════════════════════════

        private void ExtractTemplate(HalconHandleBag bag, HObject image, byte[] rawGray, int width, int height,
            MarkSpec spec, Expect expect, CalibObservation obs, ExtractionTrace trace)
        {
            string key = spec.TemplateKey;
            if (string.IsNullOrEmpty(key))
            {
                obs.RejectReason = "模板匹配模式但没有指定模板键。请先在第 2.5 步框选并训练一个模板。";
                obs.Error = CalibError.Create(CalibFailureKind.FeatureNotFound, obs.RejectReason);
                obs.Report = BuildFailureReport(obs.RejectReason);
                return;
            }

            // ── ① 宿主模板库优先（嵌入模式：能复用就复用，避免同一个工件反复示教）──
            if (ExternalLibrary != null)
            {
                Vec2 hostPixel;
                double hostScore;
                string hostError;
                if (ExternalLibrary.TryMatch(key, rawGray, width, height,
                        expect.Has ? expect.Col : 0.0, expect.Has ? expect.Row : 0.0,
                        out hostPixel, out hostScore, out hostError))
                {
                    if (hostPixel.IsFinite)
                    {
                        obs.HasPixel = true;
                        obs.Pixel = hostPixel;
                        obs.Report = new FeatureMatchReport
                        {
                            Success = true,
                            Score = Math.Max(1.0, Math.Min(99.0, hostScore * 100.0)),
                            Verdict = FeatureQualityScorer.Verdict(hostScore * 100.0),
                            PixelX = hostPixel.X,
                            PixelY = hostPixel.Y,
                            CandidateCount = 1,
                            Detail = "来源：宿主模板库 " + ExternalLibrary.Name
                        };

                        if (trace != null && trace.Enabled)
                        {
                            trace.Clear();
                            trace.AddCross(hostPixel.Y, hostPixel.X, 14.0, "yellow", 2, 70);
                            trace.AddText(hostPixel.Y + 20.0, hostPixel.X + 16.0,
                                "宿主模板库命中", "yellow", 82);
                        }

                        return;
                    }
                }

                // 宿主库有但没有结果 → 不是错误，只是降级到自建库（下面继续）
            }

            // ── ② 工具自建模板 ──
            HTuple modelId;
            TemplateSpec ts;
            if (!_specs.TryGetValue(key, out ts))
            {
                obs.RejectReason = string.Format(CultureInfo.InvariantCulture,
                    "模板「{0}」不存在（既不在工具自建库里{1}）。请先框选 + 训练。",
                    key, ExternalLibrary == null ? "，也没有注入宿主模板库" : "，宿主库里也没找着");
                obs.Error = CalibError.Create(CalibFailureKind.FeatureNotFound, obs.RejectReason);
                obs.Report = BuildFailureReport(obs.RejectReason);
                return;
            }

            bool isShape = ts.ModelKind == TemplateModelKind.Shape;
            if (isShape ? !_shapeModels.TryGetValue(key, out modelId) : !_nccModels.TryGetValue(key, out modelId))
            {
                obs.RejectReason = "模板「" + key + "」的模型句柄已失效（可能被移除或释放）。请重新训练。";
                obs.Error = CalibError.Create(CalibFailureKind.FeatureNotFound, obs.RejectReason);
                obs.Report = BuildFailureReport(obs.RejectReason);
                return;
            }

            // 搜索角度范围必须落在模板训练范围内，否则 HALCON 直接报错
            double angleStart = ts.AngleStartDeg;
            double angleExtent = ts.AngleExtentDeg;
            double minScore = Math.Max(0.05, spec.Options != null
                ? Math.Min(ts.MinScore, spec.Options.TemplateMinScore)
                : ts.MinScore);

            try
            {
                HTuple row;
                HTuple col;
                HTuple angle;
                HTuple score;

                if (isShape)
                {
                    HOperatorSet.FindShapeModel(image, modelId, angleStart, angleExtent, minScore,
                        0, 0.5, "least_squares", 0, 0.9, out row, out col, out angle, out score);
                }
                else
                {
                    HOperatorSet.FindNccModel(image, modelId, angleStart, angleExtent, minScore,
                        0, 0.5, "true", 0, out row, out col, out angle, out score);
                }

                if (row.Length == 0)
                {
                    obs.RejectReason = string.Format(CultureInfo.InvariantCulture,
                        "模板「{0}」在帧内未找到（最低分 {1:F2}，角度范围 {2:F0}°~{3:F0}°）。"
                        + "降低最低分、扩大角度范围，或确认光照与模板拍摄时一致。",
                        key, minScore, angleStart, angleStart + angleExtent);
                    obs.Error = CalibError.Create(CalibFailureKind.FeatureNotFound, obs.RejectReason);
                    obs.Report = BuildFailureReport(obs.RejectReason);
                    return;
                }

                // 多命中取离期望最近者
                int best = 0;
                if (row.Length > 1 && expect.Has)
                {
                    best = SelectClosestIndex(row, col, expect);
                }

                double foundCol = col[best].D;
                double foundRow = row[best].D;
                double foundScore = score.Length > best ? score[best].D : 0.0;

                obs.HasPixel = true;
                obs.Pixel = new Vec2(foundCol, foundRow);

                double score01 = Math.Max(0.0, Math.Min(1.0, foundScore));
                obs.Report = new FeatureMatchReport
                {
                    Success = true,
                    Score = Math.Max(1.0, Math.Min(99.0, score01 * 100.0)),
                    Verdict = FeatureQualityScorer.Verdict(score01 * 100.0),
                    PixelX = foundCol,
                    PixelY = foundRow,
                    CandidateCount = row.Length,
                    Detail = string.Format(CultureInfo.InvariantCulture,
                        "模板「{0}」（{1}）命中：{2} 个结果，取分 {3:F3}{4}",
                        key, isShape ? "形状模板" : "NCC 模板", row.Length, foundScore,
                        row.Length > 1 ? "（按期望位置择近）" : string.Empty)
                };
                obs.Report.Candidates.Add(new MatchCandidateInfo
                {
                    Index = best + 1,
                    IsSelected = true,
                    PixelX = foundCol,
                    PixelY = foundRow
                });

                if (trace != null && trace.Enabled)
                {
                    trace.Clear();
                    trace.AddRectangle(ts.Row1, ts.Col1, ts.Row2, ts.Col2, "green", 1, 10);
                    trace.AddCross(foundRow, foundCol, 14.0, "yellow", 2, 70);
                    trace.AddText(foundRow + 20.0, foundCol + 16.0,
                        string.Format(CultureInfo.InvariantCulture, "模板命中 P({0:F1},{1:F1}) 分{2:F3}",
                            foundCol, foundRow, foundScore), "yellow", 82);
                }
            }
            catch (HOperatorException hex)
            {
                obs.RejectReason = "模板匹配算子异常：" + hex.Message;
                obs.Error = CalibError.Create(CalibFailureKind.FeatureNotFound, obs.RejectReason);
                obs.Report = BuildFailureReport(obs.RejectReason);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  模板训练（ITemplateTrainer）
        // ══════════════════════════════════════════════════════════════

        public IList<string> TemplateKeys
        {
            get { return new List<string>(_specs.Keys); }
        }

        public bool HasTemplate(string key)
        {
            return !string.IsNullOrEmpty(key) && _specs.ContainsKey(key);
        }

        public TemplateSpec GetTemplate(string key)
        {
            TemplateSpec s;
            return !string.IsNullOrEmpty(key) && _specs.TryGetValue(key, out s) ? s : null;
        }

        public bool Train(byte[] rawGray, int width, int height, TemplateSpec spec, out string error)
        {
            error = null;

            if (spec == null || string.IsNullOrEmpty(spec.Key))
            {
                error = "模板必须有一个键（Key）。";
                return false;
            }

            if (rawGray == null || rawGray.Length < (long)width * height)
            {
                error = "训练用的帧数据非法。";
                return false;
            }

            double row1 = Math.Min(spec.Row1, spec.Row2);
            double row2 = Math.Max(spec.Row1, spec.Row2);
            double col1 = Math.Min(spec.Col1, spec.Col2);
            double col2 = Math.Max(spec.Col1, spec.Col2);

            if (row2 - row1 < 6.0 || col2 - col1 < 6.0)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "示教框太小（{0:F0}×{1:F0} px）。模板至少要 6×6 px，否则形状模型提不出轮廓。",
                    col2 - col1, row2 - row1);
                return false;
            }

            using (var bag = new HalconHandleBag())
            {
                HObject image = bag.Add(HalconFrameSource.FromRawGray(rawGray, width, height));
                HObject roi = null;
                HObject reduced = null;

                try
                {
                    HOperatorSet.GenRectangle1(out roi, row1, col1, row2, col2);
                    HOperatorSet.ReduceDomain(image, roi, out reduced);

                    // 参考点 = 示教框中心（★ 之后 find 返回的就是这个点在搜索图里的位置）
                    double refRow = (row1 + row2) / 2.0;
                    double refCol = (col1 + col2) / 2.0;

                    RemoveTemplate(spec.Key);

                    HTuple modelId;
                    if (spec.ModelKind == TemplateModelKind.Shape)
                    {
                        HOperatorSet.CreateShapeModel(reduced,
                            new HTuple("auto"),
                            new HTuple(spec.AngleStartDeg),
                            new HTuple(spec.AngleExtentDeg),
                            new HTuple("auto"),
                            new HTuple("auto"),
                            new HTuple("use_polarity"),
                            new HTuple("auto"),
                            new HTuple("auto"),
                            out modelId);

                        HOperatorSet.SetShapeModelOrigin(modelId, refRow, refCol);

                        // 训练质量：形状模型第 1 层的轮廓点数（太少说明这个框里没什么结构）
                        try
                        {
                            HOperatorSet.GetShapeModelContours(out HObject contours, modelId, 1);
                            HOperatorSet.CountObj(contours, out HTuple n);
                            spec.TrainQuality = n.I;
                            SafeDispose(ref contours);
                        }
                        catch (Exception)
                        {
                            spec.TrainQuality = 0.0;
                        }

                        _shapeModels[spec.Key] = modelId;
                    }
                    else
                    {
                        HOperatorSet.CreateNccModel(reduced,
                            new HTuple("auto"),
                            new HTuple(spec.AngleStartDeg),
                            new HTuple(spec.AngleExtentDeg),
                            new HTuple("auto"),
                            new HTuple("use_polarity"),
                            out modelId);

                        // NCC 的相对原点也设为框中心
                        HOperatorSet.SetShapeModelOrigin(modelId, refRow, refCol);
                        _nccModels[spec.Key] = modelId;
                        spec.TrainQuality = (col2 - col1) * (row2 - row1);
                    }

                    spec.RefRow = refRow;
                    spec.RefCol = refCol;
                    _specs[spec.Key] = spec;

                    if (spec.ModelKind == TemplateModelKind.Shape && spec.TrainQuality < 10.0)
                    {
                        error = string.Format(CultureInfo.InvariantCulture,
                            "模板训练完成，但第 1 层轮廓点只有 {0:F0} 个 —— 这个框里几乎没有可辨结构。"
                            + "换一个纹理/边缘更明显的区域，或改用 NCC 模板。",
                            spec.TrainQuality);
                        return false;
                    }

                    return true;
                }
                catch (HOperatorException hex)
                {
                    error = "模板训练失败（HALCON）：" + hex.Message;
                    RemoveTemplate(spec.Key);
                    return false;
                }
                catch (Exception ex)
                {
                    error = "模板训练失败：" + ex.Message;
                    RemoveTemplate(spec.Key);
                    return false;
                }
                finally
                {
                    SafeDispose(ref reduced);
                    SafeDispose(ref roi);
                }
            }
        }

        public void RemoveTemplate(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            HTuple id;
            if (_shapeModels.TryGetValue(key, out id))
            {
                try
                {
                    HOperatorSet.ClearShapeModel(id);
                }
                catch (Exception)
                {
                }

                _shapeModels.Remove(key);
            }

            if (_nccModels.TryGetValue(key, out id))
            {
                try
                {
                    HOperatorSet.ClearNccModel(id);
                }
                catch (Exception)
                {
                }

                _nccModels.Remove(key);
            }

            _specs.Remove(key);
        }

        public bool TrySave(string key, string path, out string error)
        {
            error = null;

            TemplateSpec spec = GetTemplate(key);
            if (spec == null)
            {
                error = "模板不存在：" + key;
                return false;
            }

            try
            {
                HTuple id;
                if (spec.ModelKind == TemplateModelKind.Shape)
                {
                    if (!_shapeModels.TryGetValue(key, out id))
                    {
                        error = "形状模型句柄已失效。";
                        return false;
                    }

                    HOperatorSet.WriteShapeModel(id, new HTuple(path));
                }
                else
                {
                    if (!_nccModels.TryGetValue(key, out id))
                    {
                        error = "NCC 模型句柄已失效。";
                        return false;
                    }

                    HOperatorSet.WriteNccModel(id, new HTuple(path));
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "模板落盘失败：" + ex.Message;
                return false;
            }
        }

        public bool TryLoad(string key, string path, string modelKind, out string error)
        {
            error = null;

            try
            {
                var spec = new TemplateSpec { Key = key };
                if (string.Equals(modelKind, "ncc", StringComparison.OrdinalIgnoreCase))
                {
                    spec.ModelKind = TemplateModelKind.Ncc;
                    HOperatorSet.ReadNccModel(new HTuple(path), out HTuple id);
                    _nccModels[key] = id;
                }
                else
                {
                    spec.ModelKind = TemplateModelKind.Shape;
                    HOperatorSet.ReadShapeModel(new HTuple(path), out HTuple id);
                    _shapeModels[key] = id;
                }

                spec.Note = "从文件读回：" + path;
                _specs[key] = spec;
                return true;
            }
            catch (Exception ex)
            {
                error = "模板读取失败：" + ex.Message;
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  共用：候选选择 / 轨迹绘制 / 小工具
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 圆多候选择优：先按参考半径排除伪特征（±<paramref name="tolRatio"/>），再按离期望位置最近选。
        /// 无期望位置时选半径最接近参考者。全部被半径过滤 → 退化为按期望位置择近。
        /// </summary>
        private static int SelectBestCircle(HTuple row, HTuple col, Func<int, double> radiusOf,
            Expect expect, double referenceRadius, double tolRatio)
        {
            int n = row.Length;
            if (n <= 1)
            {
                return 0;
            }

            bool hasRef = referenceRadius > 1.0;
            if (!expect.Has && !hasRef)
            {
                return 0;
            }

            int bestIdx = -1;
            double bestScore = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double r = radiusOf(i);
                if (hasRef && Math.Abs(r - referenceRadius) > referenceRadius * tolRatio)
                {
                    continue; // 半径与参考偏差过大 → 伪特征
                }

                double score = expect.Has
                    ? DistanceSq(col[i].D - expect.Col, row[i].D - expect.Row)
                    : DistanceSq(r - referenceRadius, 0.0);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                }
            }

            return bestIdx >= 0 ? bestIdx : SelectClosestIndex(row, col, expect);
        }

        private static int SelectClosestIndex(HTuple row, HTuple col, Expect expect)
        {
            if (!expect.Has || row.Length == 0)
            {
                return 0;
            }

            int best = 0;
            double bestScore = double.MaxValue;
            for (int i = 0; i < row.Length; i++)
            {
                double score = DistanceSq(col[i].D - expect.Col, row[i].D - expect.Row);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            return best;
        }

        private static double AttemptCircularity(CircleAttempt attempt, double col, double row)
        {
            for (int i = 0; i < attempt.Candidates.Count; i++)
            {
                MatchCandidateInfo c = attempt.Candidates[i];
                if (Math.Abs(c.PixelX - col) < 1.5 && Math.Abs(c.PixelY - row) < 1.5)
                {
                    return c.Circularity;
                }
            }

            return 0.85;
        }

        private static double AttemptRadius(CircleAttempt attempt, double col, double row)
        {
            for (int i = 0; i < attempt.Candidates.Count; i++)
            {
                MatchCandidateInfo c = attempt.Candidates[i];
                if (Math.Abs(c.PixelX - col) < 1.5 && Math.Abs(c.PixelY - row) < 1.5)
                {
                    return c.Radius;
                }
            }

            return 0.0;
        }

        private static double NearestOtherDistance(CircleAttempt attempt, double col, double row)
        {
            double best = double.MaxValue;
            for (int i = 0; i < attempt.Candidates.Count; i++)
            {
                MatchCandidateInfo c = attempt.Candidates[i];
                double d = Math.Sqrt(DistanceSq(c.PixelX - col, c.PixelY - row));
                if (d > 0.5 && d < best)
                {
                    best = d;
                }
            }

            return best;
        }

        /// <summary>把区域的外轮廓以折线形式送进轨迹（受点数预算保护）。</summary>
        private void TraceRegion(HObject region, ExtractionTrace trace, string color, int maxContours)
        {
            if (trace == null || !trace.Enabled || region == null || !region.IsInitialized())
            {
                return;
            }

            HObject contours = null;
            try
            {
                HOperatorSet.CountObj(region, out HTuple regionCount);
                if (regionCount.I <= 0)
                {
                    return;
                }

                HOperatorSet.GenContourRegionXld(region, out contours, "border");
                HOperatorSet.CountObj(contours, out HTuple n);

                int total = Math.Min(n.I, Math.Min(maxContours, TraceMaxContours));
                for (int i = 1; i <= total; i++)
                {
                    HObject one = null;
                    try
                    {
                        HOperatorSet.SelectObj(contours, out one, i);
                        EmitContour(one, trace, color);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        SafeDispose(ref one);
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                SafeDispose(ref contours);
            }
        }

        /// <summary>把 XLD 轮廓以折线形式送进轨迹。</summary>
        private void TraceXld(HObject xld, ExtractionTrace trace, string color, int maxContours)
        {
            if (trace == null || !trace.Enabled || xld == null || !xld.IsInitialized())
            {
                return;
            }

            try
            {
                HOperatorSet.CountObj(xld, out HTuple n);
                int total = Math.Min(n.I, Math.Min(maxContours, TraceMaxContours));
                for (int i = 1; i <= total; i++)
                {
                    HObject one = null;
                    try
                    {
                        HOperatorSet.SelectObj(xld, out one, i);
                        EmitContour(one, trace, color);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        SafeDispose(ref one);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void EmitContour(HObject contour, ExtractionTrace trace, string color)
        {
            HOperatorSet.GetContourXld(contour, out HTuple rows, out HTuple cols);
            if (rows.Length < 2)
            {
                return;
            }

            double[] r = Decimate(rows, TraceMaxPointsPerContour);
            double[] c = Decimate(cols, TraceMaxPointsPerContour);
            trace.AddPolyline(r, c, color, 1, false, 20);
        }

        /// <summary>等间隔抽稀到不超过 max 个点（保形且防卡界面）。</summary>
        private static double[] Decimate(HTuple values, int max)
        {
            int n = values.Length;
            if (n <= max)
            {
                double[] all = new double[n];
                for (int i = 0; i < n; i++)
                {
                    all[i] = values[i].D;
                }

                return all;
            }

            double[] res = new double[max];
            double step = (double)(n - 1) / (max - 1);
            for (int i = 0; i < max; i++)
            {
                int idx = (int)Math.Round(i * step);
                if (idx > n - 1)
                {
                    idx = n - 1;
                }

                res[i] = values[idx].D;
            }

            return res;
        }

        /// <summary>直线方向角（度），用于垂直判定。</summary>
        private static double LineAngleDeg(double[] line)
        {
            // line = { row1, col1, row2, col2 }
            double dr = line[2] - line[0];
            double dc = line[3] - line[1];
            return Math.Atan2(dr, dc) * 180.0 / Math.PI;
        }

        private static double Normalize180(double deg)
        {
            double d = deg % 180.0;
            if (d > 90.0)
            {
                d -= 180.0;
            }
            else if (d <= -90.0)
            {
                d += 180.0;
            }

            return d;
        }

        private static double DistanceSq(double dx, double dy)
        {
            return dx * dx + dy * dy;
        }

        private static int SafeCount(HObject obj)
        {
            if (obj == null || !obj.IsInitialized())
            {
                return 0;
            }

            try
            {
                HOperatorSet.CountObj(obj, out HTuple n);
                return n.I;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void SafeDispose(ref HObject obj)
        {
            if (obj == null)
            {
                return;
            }

            try
            {
                if (obj.IsInitialized())
                {
                    obj.Dispose();
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                obj = null;
            }
        }

        // HTuple 越界保护：拆圆/直线结果时索引可能越界（HALCON 版本差异），统一走这里
        private static double colLength(HTuple col, int i)
        {
            return i >= 0 && i < col.Length ? col[i].D : double.NaN;
        }

        private static double rowLength(HTuple row, int i)
        {
            return i >= 0 && i < row.Length ? row[i].D : double.NaN;
        }

        private static double acolLength(HTuple col, int i)
        {
            return i >= 0 && i < col.Length ? col[i].D : double.NaN;
        }

        private static double arowLength(HTuple row, int i)
        {
            return i >= 0 && i < row.Length ? row[i].D : double.NaN;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            var keys = new List<string>(_specs.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                RemoveTemplate(keys[i]);
            }

            _specs.Clear();
        }

        // ══════════════════════════════════════════════════════════════
        //  尝试结果容器
        // ══════════════════════════════════════════════════════════════

        private sealed class CircleAttempt : IDisposable
        {
            public HObject Roi;
            public HObject ImageReduced;
            public HObject ThresholdRegion;
            public HObject ConnectedRegions;
            public HObject CandidatesRegion;
            public HObject SubPixEdges;
            public HObject XldSelected;

            public bool Success;
            public bool UsedDynThreshold;
            public bool UsedCentroidFallback;
            public bool UsedFullImage;
            public double Col = double.NaN;
            public double Row = double.NaN;
            public double Radius;

            public int CandidateCount;

            public readonly List<MatchCandidateInfo> Candidates = new List<MatchCandidateInfo>();

            public void Dispose()
            {
                SafeDispose(ref ThresholdRegion);
                SafeDispose(ref ConnectedRegions);
                SafeDispose(ref CandidatesRegion);
                SafeDispose(ref SubPixEdges);
                SafeDispose(ref XldSelected);
                SafeDispose(ref ImageReduced);
                SafeDispose(ref Roi);
            }
        }

        private sealed class CrossAttempt : IDisposable
        {
            public HObject Roi;
            public HObject ImageReduced;
            public HObject ThresholdRegion;
            public HObject ConnectedRegions;
            public HObject CandidatesRegion;
            public HObject Skeleton;
            public HObject SkeletonEnds;
            public HObject SkeletonJunctions;
            public HObject SkeletonArms;
            public HObject SkeletonArmRegions;
            public HObject SkeletonSelected;
            public HObject SkeletonXld;

            public string ThresholdMode = "暗色";
            public bool Success;
            public bool UsedDynThreshold;
            public bool FoundPerpendicularPair;
            public double Col = double.NaN;
            public double Row = double.NaN;
            public double AngleDevDeg = double.NaN;
            public int CandidateCount;

            public List<double[]> Lines = new List<double[]>();

            public void Dispose()
            {
                SafeDispose(ref ThresholdRegion);
                SafeDispose(ref ConnectedRegions);
                SafeDispose(ref CandidatesRegion);
                SafeDispose(ref Skeleton);
                SafeDispose(ref SkeletonEnds);
                SafeDispose(ref SkeletonJunctions);
                SafeDispose(ref SkeletonArms);
                SafeDispose(ref SkeletonArmRegions);
                SafeDispose(ref SkeletonSelected);
                SafeDispose(ref SkeletonXld);
                SafeDispose(ref ImageReduced);
                SafeDispose(ref Roi);
            }
        }
    }
}
