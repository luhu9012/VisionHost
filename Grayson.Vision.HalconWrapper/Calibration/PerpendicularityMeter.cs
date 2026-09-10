//===================================================================================
// 单帧垂直度测量服务 —— 相机装调助手 v2（A/B/C 方案，免走位、免重力参照）
//
// 场景：固定正拍相机 vs 工面（放置工件的平面）。手动调相机角度时想实时量化
//       "光轴是否垂直工面"，读数≈0 即垂直；读数无法收敛到≈0 时提示畸变/基准问题。
//
// 原理（对应《13_调试工具武器库 §2.4 v2 蓝图》访谈定稿）：
//   垂直度是静态几何量 → 单帧透视投影本身携带倾斜信息，无需走位。
//   参照物三选一（UI 下拉）：
//     A DotGridAuto —— 工面放点阵靶（圆点纸/产品圆点阵列）：自动检测多圆点圆心并
//                       排布成规整网格。垂直时行距/列距处处一致；倾斜时沿倾轴方向
//                       "近大远小"线性渐变（行距/列距梯度）→ 换算俯仰/翻滚指示角。
//     B RectEdges   —— 工件/治具自带矩形边：取两主方向直线组，组内"本应平行"的
//                       边在斜视时呈扇状散开 → 平行度展开量即透视指示角。
//     C OrthoLines  —— 现场两条正交安装边：夹角相对 90° 的偏差 = 综合倾斜量。
//   输出指标（全部不依赖重力/镜头内参）：
//       PitchDeg / RollDeg  两轴透视指示角(°)，垂直=0；符号=哪侧更近
//       BowRmsPx            边缘直线弓弯残差(px) → 畸变/靶面不平指示（纯透视倾斜不弯曲直线，弓弯≈0）
//       CenterVsEdgeDeg     中心子网 vs 全幅解算差(°)——注意：**纯倾斜也会使其非零且随倾角增大**
//                            （子网跨幅小于全幅，端到端相对变化天然偏小）。因此它只在"已调平仍大"时
//                            才指向畸变/局部异常；倾斜过程中出现大值属正常，UI 侧须以调平后为准。
//       Confidence 0~1、GridCompleteness 满阵率、Message
//   畸变判据（在 UI 侧呈现）：调平后读数稳定在非零最小值 → "疑似畸变/基准问题"；
//       中心与外缘解算不一致（须在已调平、倾斜≈0 的前提下判断）/ 边缘直线弓弯 也提示畸变 → 建议标定
//       LensDistortion 复测。
//   口径：角度按"视场方形近似"换算（θ≈atan(端到端行距相对变化)），作引导收敛量；
//       垂直=0 严格成立，绝对值与真实倾角同量级（可能差一个视场因子）。
//
// 安全：本类创建的全部 Halcon 对象在本方法内释放；不修改传入图像；可跨线程逐帧调用
//       （每帧独立对象，无共享句柄）。
//===================================================================================

using HalconDotNet;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    /// <summary>单帧垂直度测量的参照方案（A/B/C）。</summary>
    public enum PerpMeasureMode
    {
        /// <summary>A · 点阵靶（圆点阵自动检测+网格排布）—— 首选，鲁棒性最好。</summary>
        DotGridAuto = 0,
        /// <summary>B · 矩形特征（工件/治具矩形边，两主方向直线组）—— 无靶时用。</summary>
        RectEdges = 1,
        /// <summary>C · 正交安装边（现场两条近正交直线）—— 应急粗调。</summary>
        OrthoLines = 2
    }

    /// <summary>测量参数（默认值现场通用，无需改动即可跑通）。</summary>
    public sealed class PerpMeasureOptions
    {
        /// <summary>点阵模式允许的最多规整点数（按距图中心排序截断）。</summary>
        public int MaxGridPoints = 81;
        /// <summary>点阵模式最少有效规整点数。</summary>
        public int MinGridPoints = 9;
        /// <summary>分析图最大宽度(px)，越大越准越慢（实时引导建议 1000~1400）。</summary>
        public int MaxAnalysisWidth = 1280;
        /// <summary>直线模式保留参与聚类的最长线段数。</summary>
        public int TopLineCount = 24;
    }

    /// <summary>一次单帧垂直度测量结果（纯数值，不携带图像）。</summary>
    public sealed class PerpMeasurement
    {
        public bool Success;
        public string Message = "";

        /// <summary>俯仰透视指示角(°)：绕相机水平轴，垂直=0；&gt;0=画面下方更近（下仰）。</summary>
        public double PitchDeg = double.NaN;
        /// <summary>翻滚透视指示角(°)：绕相机另一水平轴，垂直=0；&gt;0=画面右方更近（右倾）。</summary>
        public double RollDeg = double.NaN;
        /// <summary>正交边模式：两直线夹角相对 90° 的偏差(°)（仅 OrthoLines 有效）。</summary>
        public double OrthoDevDeg = double.NaN;
        /// <summary>直线弓弯残差 RMS(px，原图尺度)：畸变/曲面把直线弄弯的指示，无畸变时≈0（纯透视不弯直线）。</summary>
        public double BowRmsPx = double.NaN;
        /// <summary>中心子网 vs 全幅解算角度差(°)：**纯倾斜也会非零并随倾角增大**，仅在已调平后仍大才指示畸变/靶面不平。</summary>
        public double CenterVsEdgeDeg = double.NaN;
        /// <summary>网格满阵率 0~1（点阵模式；满阵=1）。</summary>
        public double GridCompleteness = 0;
        /// <summary>整体置信度 0~1（&lt;0.30 视为不可信，UI 应提示检测不稳）。</summary>
        public double Confidence = 0;
        /// <summary>有效特征数（点阵点数 / 直线条数）。</summary>
        public int FeatureCount = 0;

        /// <summary>是否达到可展示置信度。</summary>
        public bool IsUsable => Success && Confidence >= 0.30;

        /// <summary>垂直度"可判读"读数值：A/B 用俯仰+翻滚，C 用正交偏差；返回两轴最大绝对值。</summary>
        public double MaxTiltDeg
        {
            get
            {
                double m = 0;
                if (!double.IsNaN(PitchDeg)) m = Math.Max(m, Math.Abs(PitchDeg));
                if (!double.IsNaN(RollDeg)) m = Math.Max(m, Math.Abs(RollDeg));
                if (!double.IsNaN(OrthoDevDeg)) m = Math.Max(m, Math.Abs(OrthoDevDeg));
                return m;
            }
        }
    }

    /// <summary>单帧垂直度测量入口（静态、无状态，可跨线程逐帧调用）。</summary>
    public static class PerpendicularityMeter
    {
        private const double Rad2Deg = 180.0 / Math.PI;

        /// <summary>对一帧图像测量"光轴 vs 靶面/工面"的垂直度。不修改 source。</summary>
        public static PerpMeasurement Measure(HImage source, PerpMeasureMode mode, PerpMeasureOptions opt)
        {
            var r = new PerpMeasurement();
            if (source == null || !source.IsInitialized())
            {
                r.Message = "图像无效";
                return r;
            }
            if (opt == null) opt = new PerpMeasureOptions();

            HObject gray = null;      // 分析用灰度（单通道时直接引用 source，不释放）
            HObject zoom = null;      // 缩放分析图
            bool grayOwned = false;
            try
            {
                HOperatorSet.GetImageSize(source, out HTuple wT, out HTuple hT);
                int srcW = wT.I, srcH = hT.I;
                if (srcW <= 0 || srcH <= 0) { r.Message = "图像尺寸无效"; return r; }

                // 1) 灰度化（threshold/圆点/直线类算子均要求单通道）
                HOperatorSet.CountChannels(source, out HTuple chT);
                if (chT.I == 1)
                {
                    gray = source;
                }
                else if (chT.I == 3)
                {
                    HOperatorSet.Rgb1ToGray(source, out gray);
                    grayOwned = true;
                }
                else
                {
                    r.Message = "不支持的通道数 " + chT.I;
                    return r;
                }

                // 2) 缩放分析图（提速），scale=原图/分析图
                double scale = 1.0;
                if (srcW > opt.MaxAnalysisWidth)
                {
                    scale = (double)srcW / opt.MaxAnalysisWidth;
                    int tw = opt.MaxAnalysisWidth;
                    int th = Math.Max(2, (int)Math.Round(srcH / scale));
                    HOperatorSet.ZoomImageSize(gray, out zoom, tw, th, "weighted");
                }
                else
                {
                    HOperatorSet.CopyImage(gray, out zoom);
                }

                // 3) 按方案测量
                switch (mode)
                {
                    case PerpMeasureMode.DotGridAuto:
                        MeasureDotGrid(zoom, scale, opt, r);
                        break;
                    case PerpMeasureMode.RectEdges:
                        MeasureLines(zoom, scale, true, opt, r);
                        break;
                    default:
                        MeasureLines(zoom, scale, false, opt, r);
                        break;
                }
            }
            catch (Exception ex)
            {
                r.Success = false;
                r.Message = "垂直度测量异常: " + ex.Message;
            }
            finally
            {
                if (grayOwned && gray != null) TryDispose(gray);
                TryDispose(zoom);
            }
            return r;
        }

        // ═════════════════════════════ 方案 A：点阵靶 ═════════════════════════════
        private static void MeasureDotGrid(HObject zoom, double scale, PerpMeasureOptions opt, PerpMeasurement r)
        {
            double[] rows, cols;
            int gridRows, gridCols;
            if (!TryExtractDotGrid(zoom, opt, out rows, out cols, out gridRows, out gridCols))
            {
                r.Message = "点阵识别失败：画面中未找到可排布成 ≥3×3 规整网格的圆点阵列。请确认：① A 方案需在工面放点阵靶纸/圆点阵列且充满视野 ≥50%；② 对焦清晰；③ 无强反光把圆点连成一片。";
                return;
            }

            r.FeatureCount = rows.Length;
            r.GridCompleteness = gridRows * gridCols > 0 ? (double)rows.Length / (gridRows * gridCols) : 0;
            r.Confidence = 0.45 + r.GridCompleteness * 0.30 + (gridRows >= 4 && gridCols >= 4 ? 0.15 : 0.0);
            if (r.Confidence > 0.98) r.Confidence = 0.98;
            r.Success = true;

            // 行距沿画面竖直方向渐变 → 俯仰；列距沿水平方向渐变 → 翻滚（行距远小近大）
            double pitchDeg, rollDeg, meanGap;
            ComputeGapGradients(rows, cols, gridRows, gridCols, out pitchDeg, out rollDeg, out meanGap);
            if (double.IsNaN(pitchDeg) || double.IsNaN(rollDeg) || meanGap < 2.0)
            {
                r.Success = false;
                r.Message = "点距过小/网格过密，无法稳定计算——放大分析宽度或把靶放近些。";
                return;
            }
            r.PitchDeg = pitchDeg;
            r.RollDeg = rollDeg;

            // 直线弓弯残差（每列/每行拟合直线的垂直残差 RMS，分析尺度→原图尺度）
            if (gridRows >= 4 && gridCols >= 4)
                r.BowRmsPx = ComputeBowRms(rows, cols, gridRows, gridCols) * scale;

            // 中心子网 vs 全幅（畸变/局部异常指示）
            if (gridRows >= 5 && gridCols >= 5)
            {
                int r0 = 1, r1 = gridRows - 2, c0 = 1, c1 = gridCols - 2;
                int nr = r1 - r0 + 1, nc = c1 - c0 + 1;
                var cro = new double[nr * nc];
                var cco = new double[nr * nc];
                int k = 0;
                for (int rr = r0; rr <= r1; rr++)
                    for (int cc = c0; cc <= c1; cc++)
                    {
                        cro[k] = rows[rr * gridCols + cc];
                        cco[k] = cols[rr * gridCols + cc];
                        k++;
                    }
                double pc, rc, mg;
                ComputeGapGradients(cro, cco, nr, nc, out pc, out rc, out mg);
                if (!double.IsNaN(pc) && !double.IsNaN(rc))
                    r.CenterVsEdgeDeg = Math.Max(Math.Abs(pc - pitchDeg), Math.Abs(rc - rollDeg));
            }

            r.Message = string.Format("点阵 {0}×{1} · {2} 点 · 满阵率 {3:P0} · 点距 {4:F1}px",
                gridRows, gridCols, rows.Length, r.GridCompleteness, meanGap);
        }

        /// <summary>从分析图中提取可规整成网格的圆点圆心（AreaCenter 像素级，实时引导靠多帧平均平滑）。</summary>
        private static bool TryExtractDotGrid(HObject zoom, PerpMeasureOptions opt,
            out double[] outRows, out double[] outCols, out int gridRows, out int gridCols)
        {
            outRows = null; outCols = null; gridRows = 0; gridCols = 0;
            double bestCirc = -1;
            double[] pickRows = null, pickCols = null;
            try
            {
                // 两个极性都试（白底黑点 dark / 黑底白点 light），取平均圆度更高者
                foreach (string polar in new[] { "dark", "light" })
                {
                    HObject bin = null, conn = null;
                    try
                    {
                        HOperatorSet.BinaryThreshold(zoom, out bin, "max_separability", polar, out _);
                        HOperatorSet.Connection(bin, out conn);
                        HTuple areas, rr, cc, circs, areaT;
                        HOperatorSet.AreaCenter(conn, out areas, out rr, out cc);
                        int n = areas.Length;
                        if (n < 6) continue;
                        HOperatorSet.RegionFeatures(conn, "circularity", out circs);
                        HOperatorSet.RegionFeatures(conn, "area", out areaT);

                        var idxs = new List<int>();
                        for (int i = 0; i < n; i++) idxs.Add(i);
                        idxs.Sort((a, b) => areaT[a].D.CompareTo(areaT[b].D));
                        double medArea = areaT[idxs[n / 2]].D;
                        if (medArea < 4.0) continue;

                        double sumCirc = 0; int cntCirc = 0;
                        var keepRows = new List<double>();
                        var keepCols = new List<double>();
                        for (int i = 0; i < n; i++)
                        {
                            if (circs.Length <= i) continue;
                            double cir = circs[i].D;
                            double ar = areaT[i].D;
                            if (cir < 0.55) continue;                       // 圆度下限
                            if (ar < medArea * 0.3 || ar > medArea * 3.2) continue; // 面积离群剔除
                            keepRows.Add(rr[i].D);
                            keepCols.Add(cc[i].D);
                            sumCirc += cir; cntCirc++;
                        }
                        if (keepRows.Count < 6) continue;
                        if (sumCirc / cntCirc > bestCirc)
                        {
                            bestCirc = sumCirc / cntCirc;
                            pickRows = keepRows.ToArray();
                            pickCols = keepCols.ToArray();
                        }
                    }
                    catch { /* 该极性失败可忽略 */ }
                    finally
                    {
                        TryDispose(bin);
                        TryDispose(conn);
                    }
                }
                if (pickRows == null || pickRows.Length < 6) return false;

                // 按距图中心排序截断（防外围干扰点/第二排杂乱特征）
                HOperatorSet.GetImageSize(zoom, out HTuple zw, out HTuple zh);
                double cx = zw.I / 2.0, cy = zh.I / 2.0;
                var ranked = new List<int>();
                for (int i = 0; i < pickRows.Length; i++) ranked.Add(i);
                ranked.Sort((a, b) =>
                {
                    double da = (pickRows[a] - cy) * (pickRows[a] - cy) + (pickCols[a] - cx) * (pickCols[a] - cx);
                    double db = (pickRows[b] - cy) * (pickRows[b] - cy) + (pickCols[b] - cx) * (pickCols[b] - cx);
                    return da.CompareTo(db);
                });
                int cap = Math.Min(pickRows.Length, opt.MaxGridPoints);
                var rL = new double[cap]; var cL = new double[cap];
                for (int i = 0; i < cap; i++) { rL[i] = pickRows[ranked[i]]; cL[i] = pickCols[ranked[i]]; }

                // 网格规整：按 row 聚类成行 → 每行按 col 排序 → 保留列数众数的行
                var pts = new List<(double r, double c)>();
                for (int i = 0; i < rL.Length; i++) pts.Add((rL[i], cL[i]));
                pts.Sort((a, b) => a.r != b.r ? a.r.CompareTo(b.r) : a.c.CompareTo(b.c));

                var gaps = new List<double>();
                for (int i = 1; i < pts.Count; i++) gaps.Add(pts[i].r - pts[i - 1].r);
                gaps.Sort();
                if (gaps.Count == 0) return false;
                double tol = gaps[gaps.Count / 2] * 1.6 + 1.0;

                var rowsAll = new List<List<(double r, double c)>>();
                var cur = new List<(double r, double c)> { pts[0] };
                for (int i = 1; i < pts.Count; i++)
                {
                    if (pts[i].r - pts[i - 1].r > tol)
                    {
                        rowsAll.Add(cur);
                        cur = new List<(double r, double c)>();
                    }
                    cur.Add(pts[i]);
                }
                rowsAll.Add(cur);
                foreach (var row in rowsAll) row.Sort((a, b) => a.c.CompareTo(b.c));

                var cntMap = new Dictionary<int, int>();
                foreach (var row in rowsAll)
                {
                    int cn = row.Count;
                    cntMap.TryGetValue(cn, out int v);
                    cntMap[cn] = v + 1;
                }
                int bestCols = 0, bestRows = 0;
                foreach (var kv in cntMap)
                    if (kv.Key >= 3 && kv.Value >= bestRows) { bestCols = kv.Key; bestRows = kv.Value; }
                if (bestCols < 3 || bestRows < 3) return false;

                var fullRows = new List<double>();
                var fullCols = new List<double>();
                foreach (var row in rowsAll)
                    if (row.Count == bestCols)
                        foreach (var p in row) { fullRows.Add(p.r); fullCols.Add(p.c); }

                if (fullRows.Count < opt.MinGridPoints) return false;
                outRows = fullRows.ToArray();
                outCols = fullCols.ToArray();
                gridRows = fullRows.Count / bestCols;
                gridCols = bestCols;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>行/列间距的线性梯度 → 两轴透视指示角。口径：θ≈atan(端到端总相对变化)。</summary>
        private static void ComputeGapGradients(double[] rows, double[] cols, int gridRows, int gridCols,
            out double pitchDeg, out double rollDeg, out double meanGapPx)
        {
            pitchDeg = double.NaN; rollDeg = double.NaN; meanGapPx = 0;
            if (gridRows < 3 || gridCols < 3) return;
            int R = gridRows, C = gridCols;

            // 相邻行间距（按列平均）dyByR[r]；相邻列间距（按行平均）dxByC[c]
            var dyByR = new double[R - 1];
            for (int rr = 0; rr < R - 1; rr++)
            {
                double s = 0;
                for (int cc = 0; cc < C; cc++) s += rows[(rr + 1) * C + cc] - rows[rr * C + cc];
                dyByR[rr] = s / C;
            }
            var dxByC = new double[C - 1];
            for (int cc = 0; cc < C - 1; cc++)
            {
                double s = 0;
                for (int rr = 0; rr < R; rr++) s += cols[rr * C + cc + 1] - cols[rr * C + cc];
                dxByC[cc] = s / R;
            }
            double meanDy = Mean(dyByR), meanDx = Mean(dxByC);
            if (meanDy < 0.3 || meanDx < 0.3) return;
            meanGapPx = (meanDy + meanDx) / 2.0;

            // 端到端总相对变化 g = 斜率/均值 × 段数；符号=渐变方向
            double gY = LeastSquaresSlope(dyByR) / meanDy * (R - 1);
            double gX = LeastSquaresSlope(dxByC) / meanDx * (C - 1);
            pitchDeg = 2.0 * Math.Atan(Math.Abs(gY)) * Rad2Deg * Math.Sign(gY);
            rollDeg = 2.0 * Math.Atan(Math.Abs(gX)) * Rad2Deg * Math.Sign(gX);
        }

        /// <summary>逐列拟合 col=a+b·row、逐行拟合 row=c+d·col 的垂直残差 RMS(px，分析尺度)。</summary>
        private static double ComputeBowRms(double[] rows, double[] cols, int gridRows, int gridCols)
        {
            int R = gridRows, C = gridCols;
            double sumSq = 0; long n = 0;
            for (int cc = 0; cc < C; cc++)
            {
                var xs = new double[R]; var ys = new double[R];
                for (int rr = 0; rr < R; rr++) { xs[rr] = rr; ys[rr] = cols[rr * C + cc]; }
                FitLineResid(xs, ys, out double rms);
                sumSq += rms * rms; n++;
            }
            for (int rr = 0; rr < R; rr++)
            {
                var xs = new double[C]; var ys = new double[C];
                for (int cc = 0; cc < C; cc++) { xs[cc] = cc; ys[cc] = rows[rr * C + cc]; }
                FitLineResid(xs, ys, out double rms);
                sumSq += rms * rms; n++;
            }
            return n > 0 ? Math.Sqrt(sumSq / n) : double.NaN;
        }

        // ═════════════════════════════ 方案 B/C：直线组 ═════════════════════════════
        // 实测边界(probe3/4 真透视合成验证 2026-09-08)：
        //  ① LinesGauss 只检"线状结构"(刻线/印刷细线/暗缝)，对工件/治具的阶跃边缘无响应 → B/C
        //     只适用于画面里存在细线特征的场景；② 两主方向里哪组算俯仰/翻滚取决于"最长边"落在哪个
        //     方向(轴语义会翻转)，因此 Pitch/Roll 单轴读数不可靠，只宜用"是否可用+量级+正交偏差"；
        //     ③ 幅值约为 A 点阵方案的 1/4~1/3，仍作收敛引导量。A 方案最稳，B/C 定位为无靶应急。
        private static void MeasureLines(HObject zoom, double scale, bool rectMode, PerpMeasureOptions opt,
            PerpMeasurement r)
        {
            var lines = new List<(double len, double angDeg, double rb, double cb, double re, double ce)>();
            foreach (string polar in new[] { "dark", "light" })
            {
                HObject ls = null, sel = null;
                try
                {
                    // 注意:ExtractWidth 参数是字符串 "true"/"false"(曾传 int 2 → 每次 #1205 抛错被
                    // catch 吞掉,导致 B/C 直线模式静默全失效;probe4 诊断定位)
                    HOperatorSet.LinesGauss(zoom, out ls, 1.2, 10, 26, polar, "true", "none", "true");
                    HOperatorSet.SelectShapeXld(ls, out sel, "contlength", "and", 30.0, 1e9);
                    HOperatorSet.FitLineContourXld(sel, "tukey", -1, 0, 5, 2,
                        out HTuple rbT, out HTuple cbT, out HTuple reT, out HTuple ceT,
                        out _, out _, out _);
                    int m = rbT.Length;
                    for (int i = 0; i < m; i++)
                    {
                        double ang = AngleDeg(rbT[i].D, cbT[i].D, reT[i].D, ceT[i].D);
                        double len = Math.Sqrt((reT[i].D - rbT[i].D) * (reT[i].D - rbT[i].D)
                                             + (ceT[i].D - cbT[i].D) * (ceT[i].D - cbT[i].D));
                        lines.Add((len, ang, rbT[i].D, cbT[i].D, reT[i].D, ceT[i].D));
                    }
                }
                catch { /* 该极性提取失败可忽略 */ }
                finally
                {
                    TryDispose(ls);
                    TryDispose(sel);
                }
            }
            // 总量截断，只保留最长若干参与聚类（同一物理边可能被拆多段，先按长度排序截断）
            lines.Sort((a, b) => b.len.CompareTo(a.len));
            if (lines.Count > opt.TopLineCount * 2) lines = lines.GetRange(0, opt.TopLineCount * 2);
            if (lines.Count < 2)
            {
                r.Message = "直线识别失败：画面中未找到足够长的清晰直线边缘（刻线/工件边/矩形轮廓）。A 方案点阵靶最稳，B/C 需要高对比度直线特征。";
                return;
            }
            lines.Sort((a, b) => b.len.CompareTo(a.len));

            if (rectMode) MeasureRectGroups(lines, r);
            else MeasureOrthoPair(lines, r);

            if (!r.Success) return;
            r.Confidence = 0.45 + Math.Min(0.4, lines.Count * 0.025);
            r.FeatureCount = lines.Count;
            r.Success = r.Confidence >= 0.3;
        }

        private static void MeasureRectGroups(List<(double len, double angDeg, double rb, double cb, double re, double ce)> lines,
            PerpMeasurement r)
        {
            var l0 = lines[0];
            int l1i = -1;
            for (int i = 1; i < lines.Count; i++)
            {
                double d = AngDiffDeg(l0.angDeg, lines[i].angDeg);
                if (d > 55 && d < 125) { l1i = i; break; }
            }
            if (l1i < 0)
            {
                r.Message = "未找到与最长边近似垂直的第二主方向（矩形特征不足），试试 C 方案或 A 方案。";
                return;
            }
            var l1 = lines[l1i];

            var g0 = new List<double>(); // 与 L0 同向
            var g1 = new List<double>(); // 与 L1 同向
            for (int i = 0; i < lines.Count; i++)
            {
                if (i == 0 || i == l1i) continue;
                if (AngDiffDeg(l0.angDeg, lines[i].angDeg) < 8) g0.Add(lines[i].angDeg);
                else if (AngDiffDeg(l1.angDeg, lines[i].angDeg) < 8) g1.Add(lines[i].angDeg);
            }
            if (g0.Count < 2 && g1.Count < 2)
            {
                r.Message = "主方向组内平行边不足（每组需 ≥2 条才能测透视散开）。用 A 方案点阵靶最稳。";
                return;
            }

            double spread0 = g0.Count >= 2 ? AngleSpread(g0) : 0;
            double spread1 = g1.Count >= 2 ? AngleSpread(g1) : 0;
            r.PitchDeg = spread1;  // 第二主方向（接近竖直）
            r.RollDeg = spread0;   // 第一主方向（接近水平）
            r.OrthoDevDeg = Math.Abs(AngDiffDeg(l0.angDeg, l1.angDeg) - 90.0);
            r.Success = true;
            r.Message = string.Format("矩形主向 {0:F1}°/{1:F1}° · 相邻角差 {2:F2}° · 平行散开 {3:F2}/{4:F2}°",
                l0.angDeg, l1.angDeg, r.OrthoDevDeg, spread0, spread1);
        }

        private static void MeasureOrthoPair(List<(double len, double angDeg, double rb, double cb, double re, double ce)> lines,
            PerpMeasurement r)
        {
            var l0 = lines[0];
            int l1i = -1;
            for (int i = 1; i < lines.Count; i++)
            {
                double d = AngDiffDeg(l0.angDeg, lines[i].angDeg);
                if (d > 35 && d < 145) { l1i = i; break; }
            }
            if (l1i < 0)
            {
                r.Message = "只找到单方向直线——C 方案需要现场两条正交安装边。";
                return;
            }
            var l1 = lines[l1i];
            double dev = AngDiffDeg(l0.angDeg, l1.angDeg) - 90.0;
            r.OrthoDevDeg = Math.Abs(dev);
            r.Success = true;
            r.Message = string.Format("两正交边夹角 {0:F2}°（相对 90° 偏差 {1:F2}°）",
                90.0 + dev, dev);
        }

        // ═════════════════════════════ 数值小工具 ═════════════════════════════
        private static void TryDispose(HObject obj)
        {
            try { if (obj != null) obj.Dispose(); } catch { }
        }

        private static double Mean(double[] a)
        {
            if (a == null || a.Length == 0) return double.NaN;
            double s = 0; foreach (var v in a) s += v; return s / a.Length;
        }

        private static double LeastSquaresSlope(double[] y)
        {
            int n = y.Length;
            if (n < 2) return 0;
            double mx = (n - 1) / 2.0;
            double my = Mean(y);
            double num = 0, den = 0;
            for (int i = 0; i < n; i++)
            {
                num += (i - mx) * (y[i] - my);
                den += (i - mx) * (i - mx);
            }
            return Math.Abs(den) < 1e-12 ? 0 : num / den;
        }

        private static void FitLineResid(double[] xs, double[] ys, out double rmsPx)
        {
            rmsPx = double.NaN;
            int n = xs.Length;
            if (n < 3) return;
            double mx = Mean(xs), my = Mean(ys);
            double sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = xs[i] - mx, dy = ys[i] - my;
                sxx += dx * dx; sxy += dx * dy;
            }
            if (Math.Abs(sxx) < 1e-12) { rmsPx = 0; return; }
            double slope = sxy / sxx;
            double intercept = my - slope * mx;
            double s = 0;
            for (int i = 0; i < n; i++)
            {
                double err = ys[i] - (intercept + slope * xs[i]);
                s += err * err;
            }
            rmsPx = Math.Sqrt(s / n);
        }

        private static double AngleDeg(double rb, double cb, double re, double ce)
        {
            double a = Math.Atan2(ce - cb, re - rb) * Rad2Deg; // row 向下坐标系：角度≈画面方向角
            a %= 180.0;
            if (a < 0) a += 180.0;
            return a;
        }

        private static double AngDiffDeg(double a, double b)
        {
            double d = Math.Abs(a - b) % 180.0;
            if (d > 90.0) d = 180.0 - d;
            return d;
        }

        private static double AngleSpread(List<double> angs)
        {
            double lo = 1e9, hi = -1e9;
            foreach (var a in angs)
            {
                if (a < lo) lo = a;
                if (a > hi) hi = a;
            }
            double spread = hi - lo;
            if (spread > 90.0) spread = 180.0 - spread;
            return spread;
        }
    }
}
