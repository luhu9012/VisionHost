using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Templates.Models;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Measure2D
{
    /// <summary>单条亚像素边缘点（MeasurePos 输出）</summary>
    public class CaliperEdgePoint
    {
        public double Row { get; set; }
        public double Col { get; set; }
        /// <summary>边缘幅度（对比度）</summary>
        public double Amplitude { get; set; }
        /// <summary>到测量对象中心的带符号距离（沿扫描方向）</summary>
        public double Distance { get; set; }
    }

    /// <summary>直线拟合结果（FitLineContourXld）</summary>
    public class CaliperLineFit
    {
        public double Row1 { get; set; }
        public double Col1 { get; set; }
        public double Row2 { get; set; }
        public double Col2 { get; set; }
        /// <summary>线段角度（弧度，HALCON phi 约定：atan2(ΔRow,ΔCol)）</summary>
        public double Phi => Math.Atan2(Row2 - Row1, Col2 - Col1);
        public double RmsError { get; set; }
        public int UsedPoints { get; set; }
    }

    /// <summary>圆/弧拟合结果（FitCircleContourXld）</summary>
    public class CaliperCircleFit
    {
        public double CenterRow { get; set; }
        public double CenterCol { get; set; }
        public double Radius { get; set; }
        public double RmsError { get; set; }
        public int UsedPoints { get; set; }
    }

    /// <summary>
    /// 基于 HALCON measure 工具的亚像素边缘测量（P0，替代 EdgeMeasureTool 被注释的空壳）。
    /// HALCON 语义（官方文档 2026-09-09 核实）：
    ///   · gen_measure_rectangle2(Row,Col,Phi,Length1,Length2,W,H,Interp)：长轴=剖面扫描方向，
    ///     提取【垂直于长轴】的直边；灰度剖面沿长轴采样、沿短轴(2·Length2)方向平均。
    ///   · gen_measure_arc(CenterRow,CenterCol,Radius,AngleStart,AngleExtent,AnnulusRadius,W,H,Interp)：环形同语义。
    /// 因此本类约定：调用方给出【横跨边缘的测量带】时，
    ///   measureRectLen1 = 跨边探测半长（扫描范围），measureRectLen2 = 沿边平均半宽。
    /// 单探针按 Select 语义收敛为一个点：All→幅度最大；First→距中心最近；Last→距中心最远。
    /// </summary>
    public static class CaliperMeasureTool
    {
        public static string TransitionStr(CaliperTransition t)
        {
            switch (t)
            {
                case CaliperTransition.Positive: return "positive";
                case CaliperTransition.Negative: return "negative";
                default: return "all";
            }
        }

        /// <summary>
        /// 矩形测量带单次取样：在 (row,col) 处以 scanPhi 为主轴方向（横跨边缘）取边缘点集。
        /// measureRectLen1=跨边扫描半长；measureRectLen2=沿边平均半宽。
        /// 返回按 Select 收敛后的点（All 语义=取幅度最大的一条，避免带内多边缘污染拟合）。
        /// </summary>
        public static Result<List<CaliperEdgePoint>> MeasureRectProbe(HObject gray,
            double row, double col, double scanPhiRad,
            double measureRectLen1, double measureRectLen2,
            double sigma, double threshold, CaliperTransition transition, CaliperEdgeSelect select,
            int imgW, int imgH)
        {
            if (gray == null || !gray.IsInitialized())
                return Result<List<CaliperEdgePoint>>.Fail("灰度图无效");
            // ★ HALCON 对 measure_pos 的 Sigma 有【硬下限】：实测 sigma=0.4 直接抛
            //   error #3022 "Wrong size of filter for Gauss"，而 HALCON 官方要求 Sigma>=0.4，
            //   且高斯滤波尺寸还必须小于剖面长度（即 2*Length1 太短也会抛同一个错）。
            //   旧写法把 #3022 吞进通用 catch，只报"卡尺取样异常"，现场看不到该改哪个参数。
            //   故在此前置校验 + 在 catch 里把 #3022 翻译成可行动的提示（响亮失败）。
            if (sigma < 0.4)
                return Result<List<CaliperEdgePoint>>.Fail(
                    $"卡尺 Sigma={sigma} 非法（HALCON 要求 >=0.4，否则 measure_pos 抛 #3022）");
            if (measureRectLen1 <= 0 || measureRectLen2 <= 0)
                return Result<List<CaliperEdgePoint>>.Fail(
                    $"卡尺长度为非正数（跨边半长={measureRectLen1}、沿边平均半宽={measureRectLen2}）");
            try
            {
                HTuple mh;
                // HalconDotNet 约定：handle 输出在参数最后（同 CreateShapeModel(..., out modelId)）
                HOperatorSet.GenMeasureRectangle2(row, col, scanPhiRad,
                    measureRectLen1, measureRectLen2, imgW, imgH, "bilinear", out mh);
                try
                {
                    HTuple rows, cols, amps, dists;
                    HOperatorSet.MeasurePos(gray, mh, sigma, threshold,
                        TransitionStr(transition), "all", out rows, out cols, out amps, out dists);

                    // ★ 四个输出元组的长度【不保证一致】。本环境实测（HALCON 24.11 + halcondotnet，2026-09-25）：
                    //   measure_pos 的 Distance 常是空元组（Length=0），而 Row/Column/Amplitude 正常有值。
                    //   旧写法 `dists[i].D` 会抛 HTupleAccessException("Index out of range")，又被上层
                    //   MeasureArc 的 `continue` / MeasureLine 的 warn 分支吞掉 ⇒ 表现为"取样正常、0 个点"，
                    //   一路静默到"拟合失败"。故这里按【各自真实长度】取，Distance 缺失就自己沿扫描方向
                    //   做带符号投影补上（First/Last 只用 |Distance|，与 HALCON 的符号约定无关，语义安全）。
                    int n = Math.Min(rows.Length, Math.Min(cols.Length, amps.Length));
                    if (n <= 0)
                        return Result<List<CaliperEdgePoint>>.Ok(new List<CaliperEdgePoint>());

                    double su = Math.Sin(scanPhiRad), cu = Math.Cos(scanPhiRad);
                    var pts = new List<CaliperEdgePoint>(n);
                    for (int i = 0; i < n; i++)
                    {
                        double pr = rows[i].D, pc = cols[i].D;
                        pts.Add(new CaliperEdgePoint
                        {
                            Row = pr,
                            Col = pc,
                            Amplitude = amps[i].D,
                            Distance = i < dists.Length ? dists[i].D : ((pr - row) * su + (pc - col) * cu)
                        });
                    }
                    // 按 Select 收敛
                    switch (select)
                    {
                        case CaliperEdgeSelect.First:
                            return Result<List<CaliperEdgePoint>>.Ok(new List<CaliperEdgePoint>
                            {
                                pts.OrderBy(p => Math.Abs(p.Distance)).First()
                            });
                        case CaliperEdgeSelect.Last:
                            return Result<List<CaliperEdgePoint>>.Ok(new List<CaliperEdgePoint>
                            {
                                pts.OrderByDescending(p => Math.Abs(p.Distance)).First()
                            });
                        default:
                            // All：取幅度最大（最显著）的一条边缘作为本探针代表点
                            return Result<List<CaliperEdgePoint>>.Ok(new List<CaliperEdgePoint>
                            {
                                pts.OrderByDescending(p => p.Amplitude).First()
                            });
                    }
                }
                finally
                {
                    HOperatorSet.CloseMeasure(mh);
                }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperMeasureTool), "矩形测量带取样失败", ex);
                // #3022 = 高斯滤波尺寸不对：要么 Sigma 太小，要么剖面太短（2*Length1 撑不住 Sigma）。
                // 这两种都是"配置写错"，现场必须能一眼看出改哪个数，故单独翻译。
                string hint = (ex.Message ?? string.Empty).Contains("3022")
                    ? $"（HALCON #3022：高斯滤波尺寸非法 → 调大 Sigma 或调大跨边半长 Length1；" +
                      $"当前 Sigma={sigma}、Length1={measureRectLen1}、Length2={measureRectLen2}）"
                    : string.Empty;
                return Result<List<CaliperEdgePoint>>.Fail("卡尺取样异常" + hint, -1, ex);
            }
        }

        /// <summary>
        /// 探针几何 → 测区 XLD（**"原型卡尺"上屏**用：把真正参与取样的那些测量带画出来）。
        /// 每项 = {row, col, phi, length1, length2}，语义与 gen_measure_rectangle2 的实参逐字一致
        /// ——★注意 phi 在这里是**测量侧约定**（(dRow,dCol)=(sinφ,cosφ)），落到绘制算子
        /// gen_rectangle2_contour_xld 时必须取负（原因与证据见下方循环内的定案注释）。
        ///
        /// 为什么要有它：此前三个测量节点只画"种子十字 + 命中点十字 + 拟合结果"，**测区本身不上屏**，
        /// 于是"用了卡尺"这件事在画面上完全看不出来（hdev 里靠 get_metrology_object_measures 画的
        /// 那一排小测量框，我们这里是缺的）。预览属增益：失败返回 null，绝不把主测量带下水。
        ///
        /// ★★ 2026-09-25 实测记录（由 .workbuddy/caliper_region_probe 断言）：
        ///   本实现取【逐条 scalar 调用 + ConcatObj 合并】（与 TemplateCaliperRunner.BuildOneCaliperModelXld
        ///   同一写法，仓内既有、已被生产使用）。元组版 GenRectangle2ContourXld(out, HTuple×5) 经探针
        ///   diag 实测【同样可用】（CountObj 正确）—— 两种都能用，这里只为与仓内写法保持一致。
        ///   ★ 血泪：探针第一次全红时我一度以为"元组语义不成立"，真因是探针 bin 里缺【原生】halcon.dll
        ///     （halcondotnet.dll 只是托管壳），必须把部署目录挂到 PATH 才能加载 ⇒
        ///     **环境缺失会伪装成代码 bug**：判据红之前先分清红的是被测逻辑还是运行环境。
        /// </summary>
        /// <param name="probeGeom">探针几何（MeasureLine / MeasureArc 的 probeGeom 出参）</param>
        /// <param name="maxProbes">上屏探针数上限（防超长弧段画出上千框把画面糊死）</param>
        public static HObject GenProbeRegionXld(IList<double[]> probeGeom, int maxProbes = 600)
        {
            if (probeGeom == null || probeGeom.Count == 0) return null;
            HObject merged = null;
            try
            {
                int n = Math.Min(probeGeom.Count, Math.Max(1, maxProbes));
                for (int i = 0; i < n; i++)
                {
                    var g = probeGeom[i];
                    if (g == null || g.Length < 5) continue;
                    HObject one;
                    // 半长必须为正（HALCON 对 0/负长度报错）；与 TemplateCaliperRunner 的 Math.Max(0.5,·) 同口径
                    //
                    // ★★ 2026-09-26 定案：这个 phi 必须【取负】—— 两个 HALCON 算子的 phi 语义差一个符号。
                    //    · 探针几何（MeasureArc / MeasureLine 写进 probeGeom 的 g[2]）用的是
                    //      gen_measure_rectangle2 的约定：方向向量 (dRow,dCol) = (sinφ, cosφ)。
                    //      这条由测量结果本身背书：探针确实横跨边缘取到了边缘点、拟合 RMS≈0.15px。
                    //    · gen_rectangle2_contour_xld 的主轴方向是 (dRow,dCol) = (-sinφ, cosφ)
                    //      （同样是"相对水平轴逆时针"，但行轴朝下 ⇒ 实际反号）。
                    //    证据（.workbuddy/rect_phi_probe：对 118 条环形探针逐条量"齿长轴 vs 径向"夹角）：
                    //      原样 phi ⇒ 平均 45.0°、最大 89.7°；0/90/180/270 处恰好重合（≈2°），
                    //                 45/135/225/315 处相差 90° ⇒ 齿变成【切向】。屏上表现就是
                    //                 "圆的测量带裂成四瓣"、斜边上的线带歪向另一侧（垂直边看不出，因为
                    //                 -90° ≡ +90° mod 180）。
                    //      phi 取负 ⇒ 全部 1.41°（残差 = 轮廓点整数量化的测量本底），即全部指向径向。
                    //    ★ 只改绘制侧：本函数取到的边缘点集、拟合结果都与 phi 无关，测量一行不动。
                    HOperatorSet.GenRectangle2ContourXld(out one, g[0], g[1], -g[2],
                        Math.Max(0.5, g[3]), Math.Max(0.5, g[4]));
                    if (merged == null) { merged = one; continue; }
                    try
                    {
                        HOperatorSet.ConcatObj(merged, one, out HObject joined);
                        merged.Dispose();
                        merged = joined;
                    }
                    finally { one.Dispose(); }
                }
                return merged;
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CaliperMeasureTool), "测区 XLD 生成失败（不影响测量结果）: " + ex.Message);
                try { merged?.Dispose(); } catch { }
                return null;
            }
        }

        /// <summary>
        /// 沿目标边缘的多探针直线卡尺测量：在边缘段中点 (midRow,midCol)、方向 edgePhiRad（HALCON phi 约定）
        /// 上按 NumPoints 个探针取样（探针横跨边缘），返回全部边缘点（用于 FitLine）。
        /// halfSpanAlongEdge=探针沿边散布半跨距；scanHalf=跨边探测半长；probeAvgHalf=沿边平均半宽。
        /// <paramref name="probeGeom"/> 非空时，逐条追加探针几何 {row, col, phi, length1, length2}
        /// ——供调用方用 <see cref="GenProbeRegionXld"/> 把"测区（卡尺齿）"画上屏，参数为零侵入。
        /// </summary>
        public static Result<List<CaliperEdgePoint>> MeasureLine(HObject gray,
            double midRow, double midCol, double edgePhiRad,
            double halfSpanAlongEdge, double scanHalf, double probeAvgHalf,
            int numPoints, double sigma, double threshold,
            CaliperTransition transition, CaliperEdgeSelect select,
            int imgW, int imgH,
            List<double[]> probeGeom = null)
        {
            if (numPoints < 1) numPoints = 1;
            var all = new List<CaliperEdgePoint>();
            int probeFail = 0;
            string firstErr = null;
            // 沿边单位方向（phi 约定：dRow=sinφ, dCol=cosφ）
            double duRow = Math.Sin(edgePhiRad);
            double duCol = Math.Cos(edgePhiRad);
            // 扫描方向 = 边缘法向（+90°），即测量带长轴方向
            double scanPhi = edgePhiRad + Math.PI / 2.0;
            for (int i = 0; i < numPoints; i++)
            {
                double t = numPoints == 1 ? 0.0 : -1.0 + 2.0 * i / (numPoints - 1);
                double r = midRow + t * halfSpanAlongEdge * duRow;
                double c = midCol + t * halfSpanAlongEdge * duCol;
                // 记录本探针的测区几何（与下面 MeasureRectProbe 的实参逐字一致 —— 画出来的框必须就是
                // 真正取样用的那条带，否则"看得见"反而变成新的误导来源）
                probeGeom?.Add(new double[] { r, c, scanPhi, scanHalf, probeAvgHalf });
                var res = MeasureRectProbe(gray, r, c, scanPhi, scanHalf, probeAvgHalf,
                    sigma, threshold, transition, select, imgW, imgH);
                if (res.Success && res.Data != null)
                {
                    foreach (var p in res.Data) all.Add(p);
                }
                else
                {
                    probeFail++;
                    if (firstErr == null)
                        firstErr = res.Message +
                            (res.Exception == null ? string.Empty
                             : $"（{res.Exception.GetType().Name}: {res.Exception.Message}）");
                    LogBus.Warn(nameof(CaliperMeasureTool), $"探针 #{i} 取样失败: {firstErr}");
                }
            }
            // 同 MeasureArc：全挂 ⇒ 响亮失败，不要伪装成"边缘点少"
            if (all.Count == 0 && probeFail == numPoints)
                return Result<List<CaliperEdgePoint>>.Fail(
                    $"直线卡尺 {numPoints} 条探针全部取样失败：{firstErr}", -1);
            return Result<List<CaliperEdgePoint>>.Ok(all);
        }

        /// <summary>边缘点集 → 直线拟合（tukey 抗离群）</summary>
        public static Result<CaliperLineFit> FitLine(IEnumerable<CaliperEdgePoint> pts)
        {
            var list = (pts ?? Enumerable.Empty<CaliperEdgePoint>()).Where(p => p != null).ToList();
            if (list.Count < 2)
                return Result<CaliperLineFit>.Fail($"有效边缘点不足（{list.Count}/2）");
            try
            {
                double[] rows = list.Select(p => p.Row).ToArray();
                double[] cols = list.Select(p => p.Col).ToArray();
                HObject contour;
                HOperatorSet.GenContourPolygonXld(out contour, rows, cols);
                try
                {
                    HTuple r1, c1, r2, c2, nr, nc, dist;
                    HOperatorSet.FitLineContourXld(contour, "tukey", -1, 0, 5, 2,
                        out r1, out c1, out r2, out c2, out nr, out nc, out dist);

                    // ★★ 2026-09-25 实机核验后改写（原实现把常数当残差，判据形同不存在）。
                    //   fit_line_contour_xld 的 `Dist` 输出【不是逐点残差】，而是
                    //   【直线到坐标原点的距离】（Hesse 常数；Nr·row + Nc·col = Dist），
                    //   **元组长度恒为 1**。原实现写的是 sqrt(Σ dist[i]² / dist.Length)，
                    //   于是 RmsError 恒等于 |Dist| —— 也就是一条图像内竖直边的**列坐标**。
                    //   实机证据（lk 课堂案例四帧）：
                    //     lk_01 线1 端点(184.00,165.95)→(274.00,165.19) ⇒ 点线距离 167.49 ⇒ 报"RMS=167.481px"
                    //     lk_01 线2 端点(224.00,441.92)→(314.00,441.48) ⇒ 443.02       ⇒ 报"RMS=443.024px"
                    //     lk_02 线1 端点(204.38,152.83)→(288.13,182.98) ⇒ 74.58        ⇒ 报"RMS=74.574px"
                    //   三例三中（差 <0.01px）。后果：FitLine 节点算的
                    //   `质量分 = clamp(1 − RmsError)` **恒为 0**（RmsError ≫ 1），
                    //   屏幕上打印的 RMS/分 是假数——下游任何依赖它的判据都等于没有。
                    //   合成直线验牙（注入 0.3px 已知噪声）：dist.Length==1、|dist| 与独立算出的
                    //   直线到原点距离差 0.00E+00；新口径给出 0.2117px（理论 0.2118px），
                    //   噪声放大 10× 后给出 2.1166px（理论 2.1181px）⇒ 确实在量残差。
                    //   口径与圆路径（CaliperNodeMeasure.FitCircle 里那段"按点到拟合圆径向距离补算"）
                    //   保持一致：一律自算几何残差，不信任 fit_*_contour_xld 的位置元组。
                    double nLen = Math.Sqrt(nr.D * nr.D + nc.D * nc.D);
                    if (nLen < 1e-12) nLen = 1.0; // 法向未归一化时兜底，避免除零
                    double rms = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        double d = (nr.D * list[i].Row + nc.D * list[i].Col - dist.D) / nLen;
                        rms += d * d;
                    }
                    rms = Math.Sqrt(rms / Math.Max(1, list.Count));
                    return Result<CaliperLineFit>.Ok(new CaliperLineFit
                    {
                        Row1 = r1.D, Col1 = c1.D, Row2 = r2.D, Col2 = c2.D,
                        RmsError = rms, UsedPoints = list.Count
                    });
                }
                finally { contour.Dispose(); }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperMeasureTool), "直线拟合失败", ex);
                return Result<CaliperLineFit>.Fail("直线拟合异常", -1, ex);
            }
        }

        /// <summary>
        /// 环形/圆弧卡尺测量：center 处半径 radius、角段 [arcStartRad, arcStartRad+arcExtentRad]，
        /// annulusHalf=环半宽（跨半径方向）；返回全部边缘点（用于 FitCircle）。
        ///
        /// ⚠ 2026-09-25 实机核验后改写（原实现已证不可用）：
        ///   本机 HALCON 24.11 + halcondotnet 下 `gen_measure_arc` + `measure_pos` 【恒返回 0 个边缘点】——
        ///   在一张合成完美白盘（半径 60，灰度 0/255）上同样是 0，而同位置的 `gen_measure_rectangle2`
        ///   能正常找到边缘（amp≫阈值）。即该算子组合在本环境不可用，且它是【静默】失败
        ///   （MeasurePos 正常返回、只是长度为 0），所以三个消费方——FitCircle 节点 / 模板卡尺精测 /
        ///   MeasureCircleDiameter——此前都只会得到"拟合失败"，从不报算子错。
        ///   ⇒ 改为【径向矩形探针】等价实现：沿弧等分角度布 n 个 gen_measure_rectangle2，
        ///     探针长轴沿半径方向（横跨圆周边缘）、半长 = annulusHalf，取样后汇总同一批边缘点。
        ///     对外语义（点集 + Distance=相对环中线的带符号径向距离 + Select 收敛规则）保持不变，
        ///     调用方无需改动；FitCircleContourXld 拿到的是同一性质的亚像素点集。
        /// </summary>
        public static Result<List<CaliperEdgePoint>> MeasureArc(HObject gray,
            double centerRow, double centerCol, double radius,
            double arcStartRad, double arcExtentRad, double annulusHalf,
            double sigma, double threshold, CaliperTransition transition, CaliperEdgeSelect select,
            int imgW, int imgH,
            List<double[]> probeGeom = null)
        {
            if (gray == null || !gray.IsInitialized())
                return Result<List<CaliperEdgePoint>>.Fail("灰度图无效");
            if (radius <= 0)
                return Result<List<CaliperEdgePoint>>.Fail($"环半径必须为正（当前 {radius:F2}）");
            if (annulusHalf <= 0)
                return Result<List<CaliperEdgePoint>>.Fail($"环带半宽必须为正（当前 {annulusHalf:F2}）");
            if (annulusHalf >= radius)
                return Result<List<CaliperEdgePoint>>.Fail($"环带半宽({annulusHalf:F2})必须小于环半径({radius:F2})，否则内径为负");

            try
            {
                double extent = arcExtentRad;
                if (double.IsNaN(extent) || extent <= 0) extent = 2.0 * Math.PI;
                if (extent > 2.0 * Math.PI) extent = 2.0 * Math.PI;

                // 探针条数：按弧长自适应（每条约 2px 弧长），夹在 [12, 1440]
                double arcLen = radius * extent;
                int n = (int)Math.Round(arcLen / 2.0);
                if (n < 12) n = 12;
                if (n > 1440) n = 1440;
                double alongHalf = Math.Max(1.0, arcLen / n / 2.0 + 0.5);   // 探针沿弧方向的半宽

                var all = new List<CaliperEdgePoint>(n);
                int probeFail = 0;
                string firstErr = null;
                for (int i = 0; i < n; i++)
                {
                    double ang = arcStartRad + extent * (i + 0.5) / n;
                    // HALCON phi 约定：方向向量 = (dRow, dCol) = (sinφ, cosφ)
                    double duRow = Math.Sin(ang), duCol = Math.Cos(ang);
                    double pr = centerRow + radius * duRow;
                    double pc = centerCol + radius * duCol;

                    // 探针中心落在环中线上；长轴 = 半径方向（横跨圆周边缘）
                    // 记录测区几何（供"环形卡尺"上屏：N 个小矩形沿圆周排成梳状，与 hdev
                    // get_metrology_object_measures 画出来的"卡尺齿"同形）
                    probeGeom?.Add(new double[] { pr, pc, ang, annulusHalf, alongHalf });
                    var res = MeasureRectProbe(gray, pr, pc, ang, annulusHalf, alongHalf,
                        sigma, threshold, transition, select, imgW, imgH);
                    if (!res.Success || res.Data == null)
                    {
                        probeFail++;
                        if (firstErr == null)
                            firstErr = res.Message +
                                (res.Exception == null ? string.Empty
                                 : $"（{res.Exception.GetType().Name}: {res.Exception.Message}）");
                        continue;
                    }
                    foreach (var p in res.Data)
                    {
                        // Distance 语义对齐原实现：相对环中线的带符号径向距离（+ = 向外）
                        double dr = p.Row - centerRow, dc = p.Col - centerCol;
                        p.Distance = Math.Sqrt(dr * dr + dc * dc) - radius;
                        all.Add(p);
                    }
                }

                // ★ 判据纪律：不许把"探针全挂（算子/环境故障）"当成"这条弧上没边缘（合法的工艺结果）"
                //   静默返回空集——那会让上层只看到"拟合失败"，永远指不到真正的原因。
                if (all.Count == 0 && probeFail > 0 && probeFail == n)
                    return Result<List<CaliperEdgePoint>>.Fail(
                        $"环形卡尺 {n} 条探针全部取样失败：{firstErr}", -1);

                if (all.Count <= 1) return Result<List<CaliperEdgePoint>>.Ok(all);

                // 环带内同径向上可能有内外两条边缘：按 Select 语义收敛为一条链（与原实现一致）
                switch (select)
                {
                    case CaliperEdgeSelect.First:
                        return Result<List<CaliperEdgePoint>>.Ok(all
                            .GroupBy(p => Math.Sign(p.Distance))
                            .Select(g => g.OrderBy(p => Math.Abs(p.Distance)).First()).ToList());
                    case CaliperEdgeSelect.Last:
                        return Result<List<CaliperEdgePoint>>.Ok(all
                            .GroupBy(p => Math.Sign(p.Distance))
                            .Select(g => g.OrderByDescending(p => Math.Abs(p.Distance)).First()).ToList());
                    default:
                        // All：两链都保留会互相污染拟合 ⇒ 取样本更多的一链（同数时取平均幅度更高的一链）
                        var chainA = all.Where(p => p.Distance >= 0).ToList();
                        var chainB = all.Where(p => p.Distance < 0).ToList();
                        if (chainA.Count != chainB.Count)
                            return Result<List<CaliperEdgePoint>>.Ok(chainA.Count > chainB.Count ? chainA : chainB);
                        double ampA = chainA.Count == 0 ? 0 : chainA.Average(p => p.Amplitude);
                        double ampB = chainB.Count == 0 ? 0 : chainB.Average(p => p.Amplitude);
                        return Result<List<CaliperEdgePoint>>.Ok(ampA >= ampB ? chainA : chainB);
                }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperMeasureTool), "环形卡尺取样失败", ex);
                return Result<List<CaliperEdgePoint>>.Fail("环形卡尺取样异常", -1, ex);
            }
        }

        /// <summary>边缘点集 → 圆/弧拟合（algebraic 最小二乘）</summary>
        public static Result<CaliperCircleFit> FitCircle(IEnumerable<CaliperEdgePoint> pts)
        {
            var list = (pts ?? Enumerable.Empty<CaliperEdgePoint>()).Where(p => p != null).ToList();
            if (list.Count < 3)
                return Result<CaliperCircleFit>.Fail($"有效边缘点不足（{list.Count}/3）");
            try
            {
                double[] rows = list.Select(p => p.Row).ToArray();
                double[] cols = list.Select(p => p.Col).ToArray();
                HObject contour;
                HOperatorSet.GenContourPolygonXld(out contour, rows, cols);
                try
                {
                    HTuple crow, ccol, rad, sphi, ephi, order;
                    HOperatorSet.FitCircleContourXld(contour, "algebraic", -1, 0, 0, 3, 2,
                        out crow, out ccol, out rad, out sphi, out ephi, out order);
                    return Result<CaliperCircleFit>.Ok(new CaliperCircleFit
                    {
                        CenterRow = crow.D, CenterCol = ccol.D, Radius = rad.D,
                        UsedPoints = list.Count
                    });
                }
                finally { contour.Dispose(); }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CaliperMeasureTool), "圆拟合失败", ex);
                return Result<CaliperCircleFit>.Fail("圆拟合异常", -1, ex);
            }
        }
    }
}
