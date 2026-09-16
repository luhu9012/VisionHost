using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// 解算后的一份相机参数快照 —— 只装"算畸变影响"真正用得上的那几个数。
    ///
    /// ★ 为什么不直接传 <see cref="Domain.IntrinsicsResult"/>：本模块是纯几何，
    ///   不该认识"标定结果"这种带成败语义的对象。缺哪个数就补哪个数，
    ///   传进来的每个字段都必须是几何量（米 / 像素 / 1/m²），不含"信不信"。
    /// </summary>
    public struct DivisionCameraModel
    {
        /// <summary>焦距（<b>米</b>）。HALCON campar 的原始口径，不是毫米也不是像素。</summary>
        public double FocalM;

        /// <summary>径向畸变 κ（<b>1/m²</b>，除法模型口径，乘的是像面公制半径）。</summary>
        public double Kappa;

        public double Cx;
        public double Cy;

        /// <summary>像元尺寸（米）。X / Y 分开存，柱面像元相机也能用。</summary>
        public double PitchXM;
        public double PitchYM;

        public int Width;
        public int Height;

        public bool IsUsable
        {
            get
            {
                return FocalM > 1e-9 && PitchXM > 1e-12 && PitchYM > 1e-12 && Width > 0 && Height > 0;
            }
        }

        /// <summary>
        /// 是否"真的有畸变"。
        /// ★ 阈值取 1.0（1/m²）而不是与 0 比大小：κ 的量纲是 1/m²，正常镜头是几百到几千，
        ///   残差级别的抖动解出来是 0.3 这种量级 —— 那种情况"去畸变"自然不会带来任何变化，
        ///   应该直接按"无畸变"处理，而不是算出一堆 0.0001 mm 的噪声当结论。
        /// </summary>
        public bool HasDistortion
        {
            get { return IsUsable && Math.Abs(Kappa) > 1.0; }
        }

        public static DivisionCameraModel FromIntrinsics(IntrinsicsResult r, double focalM, double pixelPitchM)
        {
            var m = new DivisionCameraModel();
            if (r == null || r.PrincipalPointPx == null || r.PrincipalPointPx.Length < 2
                || r.ImageSize == null || r.ImageSize.Length < 2)
            {
                return m;
            }

            m.FocalM = focalM;
            m.PitchXM = pixelPitchM;
            m.PitchYM = pixelPitchM;
            m.Kappa = r.Distortion != null && r.Distortion.Length > 0 ? r.Distortion[0] : 0.0;
            m.Cx = r.PrincipalPointPx[0];
            m.Cy = r.PrincipalPointPx[1];
            m.Width = r.ImageSize[0];
            m.Height = r.ImageSize[1];
            return m;
        }

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "f = {0:F3} mm、κ = {1:F1}（1/m²，约合归一化 k1 = {2:F3}）、像元 {3:F2} µm、主点 ({4:F1}, {5:F1})、图像 {6}×{7}",
                FocalM * 1000.0, Kappa,
                IntrinsicsGeometry.NormalizedFromKappa(Kappa, FocalM),
                PitchXM * 1e6, Cx, Cy, Width, Height);
        }
    }

    /// <summary>
    /// ★★「去畸变之后会好多少」的量化 —— 决策 3 要的那个数。
    ///
    /// 它回答的是一个<b>要花钱的决定</b>：畸变要不要管？管的话是把 H 定义到矫正图上、
    /// 还是在生产端补畸变？（设计文档 §6.5「与九点标定的关系」）
    ///
    /// ★ 为什么必须有它：只有"畸变在工作视野内造成多少毫米偏差"这一个数，
    ///   才能把"要不要改消费语义"从拍脑袋变成有数据的决策。而 <c>κ = -1070</c> 这种数
    ///   对操作员毫无意义 —— 它既不是毫米，也不是比例。
    ///
    /// ★★ 数学口径（本文件的唯一权威，别在别处再写一遍）：
    ///   HALCON「除法模型」<c>area_scan_division</c>：设 <c>x = (u−cx)·sx</c>（<b>米</b>，
    ///   畸变图上的像面公制坐标），则<b>去畸变</b>（畸变图 → 矫正图）为
    ///       <c>X = x / (1 + κ·(x²+y²))</c>
    ///   反方向（矫正 → 畸变，即成像方向）是它的<b>精确代数逆</b>，解二次方程：
    ///       <c>r_d = (1 − √(1 − 4κ·R²)) / (2κ·R)</c>，<c>x = X·r_d/R</c>
    ///   验证：κ→0 时两边都退化成恒等（"没有畸变"时不做任何动作）。
    ///
    /// ★★ 方向不是推出来的，是<b>实验钉死</b>的（这条结论代价很高，别再凭感觉改）：
    ///   同一姿态用 κ=0 与 κ=注入值 各合成一张板图，把畸变图里的 mark 搬过去比对 ——
    ///   本文件的方向残差 <b>0.0316 px</b>（原始位移 5.33 px），
    ///   换成另一个方向残差变成 <b>10.53 px ≈ 2 × 5.33</b>（把误差放大一倍）。
    ///   两个方向的差只有二阶项，<b>谁都不会报错、残差也不会变坏</b> ——
    ///   所以只能靠外部真值判，自证是判不出来的（详见 <c>IntrinsicsLab.DistortionModelCrossCheck</c>）。
    /// </summary>
    public static class DistortionImpactAnalyzer
    {
        /// <summary>判"畸变算不算数"的毫米阈值：超过它就该进决策清单。</summary>
        public const double NotableShiftMm = 0.10;

        /// <summary>低于这个量级就当没有影响（远小于常见引导精度）。</summary>
        public const double NegligibleShiftMm = 0.02;

        // ------------------------------------------------------------------ 几何原语

        /// <summary>
        /// 畸变图上的点 → 矫正图上的点（<b>这就是"去畸变"</b>）。
        ///
        /// ★★ 方向是被<b>实验</b>钉死的，不是照着公式推的（推导很容易把两个方向写成互为近似）：
        ///   同一姿态分别用 κ=0 与 κ=注入值 各合成一张板图，把畸变图里的 mark 用本方法搬过去，
        ///   与 κ=0 那张比对，实测最大残差 <b>0.0316 px</b>（原始位移 5.33 px）——
        ///   而换成另一个方向，残差会变成 <b>10.53 px</b>（≈ 2 × 5.33：把误差放大了一倍）。
        ///   残留的 0.03 px 恰好是二阶项的量级（5.33 × |κ·r²| ≈ 0.05），符合"一阶精确"。
        ///
        /// ★ 为什么这个错<b>必须靠外证</b>：两个方向的差只有二阶项，谁都不会报错、
        ///   残差也不会变坏；而方向反了会把畸变误差从 1 倍放大到 2 倍，
        ///   于是"去畸变之后更准"变成"去畸变之后更糟"，且毫无征兆。
        /// </summary>
        public static Vec2 Undistort(Vec2 distorted, DivisionCameraModel cam)
        {
            if (!cam.HasDistortion)
            {
                return distorted;
            }

            double x = (distorted.X - cam.Cx) * cam.PitchXM;
            double y = (distorted.Y - cam.Cy) * cam.PitchYM;
            double r2 = x * x + y * y;
            double s = 1.0 / (1.0 + cam.Kappa * r2);
            return new Vec2(cam.Cx + x * s / cam.PitchXM, cam.Cy + y * s / cam.PitchYM);
        }

        /// <summary>
        /// 矫正图上的点 → 畸变图上的点 —— <b>HALCON 渲染/成像的那个方向</b>，
        /// 也是 <see cref="Undistort"/> 的精确代数逆（解二次方程，不是再做一次除法）。
        /// ★ 判别式为负（κ 与像面尺度不自洽）时原样返回 —— 这时唯一的正解是不做任何动作。
        /// </summary>
        public static Vec2 Distort(Vec2 undistorted, DivisionCameraModel cam)
        {
            if (!cam.HasDistortion)
            {
                return undistorted;
            }

            double x = (undistorted.X - cam.Cx) * cam.PitchXM;
            double y = (undistorted.Y - cam.Cy) * cam.PitchYM;
            double rd = Math.Sqrt(x * x + y * y);
            if (rd < 1e-12)
            {
                return undistorted;      // 主点本身不动
            }

            double k = cam.Kappa;
            double disc = 1.0 - 4.0 * k * rd * rd;
            if (disc < 0.0)
            {
                return undistorted;
            }

            double r = (1.0 - Math.Sqrt(disc)) / (2.0 * k * rd);
            double s = r / rd;
            return new Vec2(cam.Cx + x * s / cam.PitchXM, cam.Cy + y * s / cam.PitchYM);
        }

        /// <summary>该 κ 与像面尺度能否反解（判别式 &gt; 0）。不能时任何"去畸变"都是编的。</summary>
        public static bool IsUndistortable(DivisionCameraModel cam, double maxRadiusPx)
        {
            if (!cam.IsUsable)
            {
                return false;
            }

            if (!cam.HasDistortion)
            {
                return true;
            }

            double rm = maxRadiusPx * cam.PitchXM;
            return 1.0 - 4.0 * cam.Kappa * rm * rm > 0.0;
        }

        /// <summary>该点被畸变挪了多少像素（= 把它搬到矫正图上的位移量）。</summary>
        public static double ShiftPxAt(Vec2 pointInDistortedImage, DivisionCameraModel cam)
        {
            if (!cam.HasDistortion)
            {
                return 0.0;
            }

            return Undistort(pointInDistortedImage, cam).DistanceTo(pointInDistortedImage);
        }

        // ------------------------------------------------------------------ 入口 ①：标定板（内参链自带）

        /// <summary>
        /// 用<b>标定板自身</b>当尺子量化畸变影响 —— 内参链自己就能算，不依赖九点数据。
        ///
        /// 尺度取自"相邻 mark 间距中位数"（物理 mm ÷ 像素 px）：
        /// ★ 与"板在画面里多大"用的是同一个局部量，抗倾斜、抗部分检出
        ///   （外接框在倾斜 25° 时会偏小一半，据此换算 mm 会差一倍）。
        ///
        /// 覆盖半径：主点到 mark 重心的距离 ± 板半对角，取各图并集的最大值。
        /// 这是"这份标定真正看过的像面范围"，畸变影响只在该范围内有意义。
        /// </summary>
        public static DistortionImpactAssessment MeasureBoard(
            IList<BoardViewFact> views, DivisionCameraModel cam, double spacingMm, string boardName)
        {
            var a = new DistortionImpactAssessment();
            a.Source = "board";

            // ★ 一开始就把"没测到的量"显式置 NaN。
            //   留 0 会伪造出一个"测过且没有改善"的结论 —— 例如 VerdictFor 里
            //   会打印「LOO 0.0000 → 0.0000 mm，改善不明显」，读起来像是量过了。
            a.SigmaRatioOnRaw = double.NaN;
            a.SigmaRatioOnUndistorted = double.NaN;
            a.LooRmsOnRawMm = double.NaN;
            a.LooRmsOnUndistortedMm = double.NaN;
            a.ScaleMmPerPx = double.NaN;
            a.MaxShiftPx = double.NaN;
            a.MaxShiftMm = double.NaN;
            a.WorkRadiusMm = double.NaN;
            a.WorstRadiusPx = double.NaN;

            if (views == null || views.Count == 0)
            {
                a.Reason = "没有可用的板图（内参链没跑过或全都没找到板）。";
                return a;
            }

            if (!cam.IsUsable)
            {
                a.Reason = "相机参数不完整（缺焦距 / 像元 / 图像尺寸），算不了位移。";
                return a;
            }

            if (!cam.HasDistortion)
            {
                // κ≈0：影响<b>确定</b>是 0（不是"没测"）—— 显式写 0，别留 NaN 让下游猜
                a.Measured = true;
                a.MaxShiftPx = 0.0;
                a.MaxShiftMm = 0.0;
                a.Verdict = "解出的畸变 κ≈0 —— 这支镜头没有可测量的径向畸变，去畸变不会有任何收益，维持现状即可。";
                return a;
            }

            if (spacingMm <= 0.0)
            {
                a.Reason = "板模型没有拿到 mark 间距，无法把像素换算成毫米（板描述文件可能没解析出来）。";
                return a;
            }

            // ── 尺度：物理间距 ÷ 像素间距（逐图算，取中位数）──
            var scales = new List<double>();
            double radiusMax = 0.0;
            for (int i = 0; i < views.Count; i++)
            {
                BoardViewFact f = views[i];
                if (f.MarkCount <= 0 || f.ApparentSpacingPx <= 0.0)
                {
                    continue;
                }

                scales.Add(spacingMm / f.ApparentSpacingPx);

                double rc = Math.Sqrt(
                    (f.MeanCol - cam.Cx) * (f.MeanCol - cam.Cx)
                    + (f.MeanRow - cam.Cy) * (f.MeanRow - cam.Cy));
                double half = 0.5 * Math.Sqrt(
                    f.ApparentWidthPx * f.ApparentWidthPx + f.ApparentHeightPx * f.ApparentHeightPx);
                double rMaxHere = rc + half;
                if (rMaxHere > radiusMax)
                {
                    radiusMax = rMaxHere;
                }
            }

            if (scales.Count == 0 || radiusMax <= 0.0)
            {
                a.Reason = "没有一张图量到了 mark 间距或覆盖半径，无法换算成毫米。";
                return a;
            }

            a.Measured = true;
            a.ScaleMmPerPx = Median(scales);
            a.WorstRadiusPx = radiusMax;
            a.WorkRadiusMm = radiusMax * a.ScaleMmPerPx;

            if (!IsUndistortable(cam, radiusMax))
            {
                a.Measured = false;
                a.Reason = "判别式为负：κ 与像面尺度不自洽（κ 口径或像元尺寸填错了）。"
                         + "这种解不能拿去算畸变影响，先复核相机参数。";
                return a;
            }

            BuildCurve(a, cam, 0.0, radiusMax);
            a.Verdict = VerdictFor(a, boardName);
            return a;
        }

        // ------------------------------------------------------------------ 入口 ②：九点采样（H 链）

        /// <summary>
        /// 用<b>九点采样</b>量化"口径切换"的收益：H 在原图上标 vs 在矫正图上标。
        ///
        /// 做两件事，都是真数据、不是估计：
        ///   ① 逐点位移：同一个 mark，两种口径算出的世界坐标差多少毫米
        ///      （= 只换口径不改别的东西时，落点会移动多少）；
        ///   ② 质量对比：两口径各自解一次 H，比 σ1/σ2 与 LOO 留一残差
        ///      —— 去畸变到底吃掉了多少形状失真，看这两个数的差就知道。
        ///
        /// ★ 需要 H 与内参同时在手，所以只能在"两条链都跑过"的地方调（见调用点注释）。
        /// </summary>
        public static DistortionImpactAssessment MeasureNinePoint(
            IList<CalibSample> samples, HomMat2D hRaw, CalibTopology topology,
            DivisionCameraModel cam, Vec2? imageCenterPx)
        {
            var a = new DistortionImpactAssessment();
            a.Source = "ninepoint";

            // 同 MeasureBoard：没测到的量一律 NaN，不留 0（0 会被读成"测过、无影响"）
            a.SigmaRatioOnRaw = double.NaN;
            a.SigmaRatioOnUndistorted = double.NaN;
            a.LooRmsOnRawMm = double.NaN;
            a.LooRmsOnUndistortedMm = double.NaN;
            a.ScaleMmPerPx = double.NaN;
            a.MaxShiftPx = double.NaN;
            a.MaxShiftMm = double.NaN;
            a.WorkRadiusMm = double.NaN;
            a.WorstRadiusPx = double.NaN;

            if (samples == null || !hRaw.IsFinite)
            {
                a.Reason = "缺少九点采样集，或 H 还不是个有限的仿射（未解算 / 解算失败）。";
                return a;
            }

            if (!cam.IsUsable)
            {
                a.Reason = "相机参数不完整（缺焦距 / 像元），算不了去畸变后的 H。";
                return a;
            }

            List<CalibSample> usable = NinePointSolver.CollectUsable(samples);
            if (usable.Count < 6)
            {
                a.Reason = "可用采样点不足 6 个，两套口径的对比没有统计意义。";
                return a;
            }

            if (!cam.HasDistortion)
            {
                // 与 MeasureBoard 同名分支保持一致：κ≈0 时位移确实是 0，不是"没测"
                a.Measured = true;
                a.MaxShiftPx = 0.0;
                a.MaxShiftMm = 0.0;
                a.Verdict = "畸变 κ≈0 —— 换不换口径都一样，维持现状即可。";
                return a;
            }

            // ── 把每个采样点搬到矫正图上（其它一切不动）──
            var shifted = new List<CalibSample>(usable.Count);
            int n = usable.Count;
            a.PointShiftPx = new double[n];
            a.PointShiftMm = new double[n];
            for (int i = 0; i < n; i++)
            {
                CalibSample s = usable[i];
                Vec2 up = Undistort(s.Pixel, cam);

                // 逐点位移（像素）：只看"去畸变把观测点搬了多远"
                a.PointShiftPx[i] = up.DistanceTo(s.Pixel);

                shifted.Add(new CalibSample
                {
                    Index = s.Index,
                    State = CalibSampleState.Ok,
                    PlanXy = s.PlanXy,
                    PlanZ = s.PlanZ,
                    PlanU = s.PlanU,
                    FeedbackXy = s.FeedbackXy,
                    FeedbackU = s.FeedbackU,
                    Pixel = up,
                    HasPixel = true,
                    Quality = s.Quality,
                    CandidateCount = s.CandidateCount,
                    FramePath = s.FramePath,
                    ElapsedMs = s.ElapsedMs
                });
            }

            // ── 解"矫正图上"的 H（其余条件完全相同，唯一变量就是口径）──
            NinePointResult solved = NinePointSolver.Solve(shifted, topology);
            if (!solved.Success || !solved.H.IsFinite)
            {
                a.Reason = "矫正图上的 H 解不出来（" + (solved.Error == null ? "未知" : solved.Error.ToString()) + "），无法对比。";
                return a;
            }

            HomMat2D hUnd = solved.H;

            // 逐点位移（毫米）：两套口径算出的世界坐标之差。
            // ★ 用 |H_new(p′) − H_old(p)|，不是 |H_old(p) − H_old(p′)|：
            //   前者是"换了口径之后落点会挪到哪"，后者是"把矫正图上的点当原图点喂进去会错多少"，
            //   两个都是真实风险，但决策 3 问的是前者。
            for (int i = 0; i < n; i++)
            {
                Vec2 worldNew = hUnd.Transform(shifted[i].Pixel);
                a.PointShiftMm[i] = worldNew.DistanceTo(hRaw.Transform(usable[i].Pixel));
            }

            CalibDiagnostics dRaw = MatrixQualityEvaluator.Evaluate(
                hRaw, samples, topology, imageCenterPx, null);
            CalibDiagnostics dUnd = MatrixQualityEvaluator.Evaluate(
                hUnd, shifted, topology, imageCenterPx, null);

            a.SigmaRatioOnRaw = dRaw.SigmaRatio;
            a.SigmaRatioOnUndistorted = dUnd.SigmaRatio;
            a.LooRmsOnRawMm = MatrixQualityEvaluator.ComputeLooRms(usable);
            a.LooRmsOnUndistortedMm = MatrixQualityEvaluator.ComputeLooRms(shifted);
            a.ScaleMmPerPx = ScaleFromH(hRaw);
            a.Measured = true;

            BuildCurve(a, cam, 0.0, MaxRadiusFromPrincipal(cam, usable));
            a.WorkRadiusMm = a.WorstRadiusPx * a.ScaleMmPerPx;

            a.Verdict = VerdictFor(a, null);
            return a;
        }

        // ------------------------------------------------------------------ 内部

        private static void BuildCurve(DistortionImpactAssessment a, DivisionCameraModel cam,
            double rMin, double rMax)
        {
            const int N = 24;
            if (rMax <= rMin)
            {
                return;
            }

            var radius = new double[N + 1];
            var shiftPx = new double[N + 1];
            var shiftMm = new double[N + 1];
            double scale = a.ScaleMmPerPx > 0.0 ? a.ScaleMmPerPx : 1.0;

            for (int i = 0; i <= N; i++)
            {
                double r = rMin + (rMax - rMin) * i / N;
                // 畸变是<b>径向</b>的：同一个半径上位移相同 → 沿 +X 轴采一条一维曲线就够，
                // 不需要在二维里刷一遍（也避免了"看起来更准其实更慢"的假精细）。
                double s = ShiftPxAt(new Vec2(cam.Cx + r, cam.Cy), cam);
                radius[i] = r;
                shiftPx[i] = s;
                shiftMm[i] = s * scale;
            }

            a.CurveRadiusPx = radius;
            a.CurveShiftPx = shiftPx;
            a.CurveShiftMm = shiftMm;

            double maxPx = 0.0;
            double maxMm = 0.0;
            double atR = 0.0;
            for (int i = 0; i <= N; i++)
            {
                if (shiftPx[i] > maxPx)
                {
                    maxPx = shiftPx[i];
                    maxMm = shiftMm[i];
                    atR = radius[i];
                }
            }

            a.MaxShiftPx = maxPx;
            a.MaxShiftMm = maxMm;
            a.WorstRadiusPx = atR;
        }

        private static double MaxRadiusFromPrincipal(DivisionCameraModel cam, IList<CalibSample> usable)
        {
            double r = 0.0;
            for (int i = 0; i < usable.Count; i++)
            {
                Vec2 p = usable[i].Pixel;
                double d = Math.Sqrt((p.X - cam.Cx) * (p.X - cam.Cx) + (p.Y - cam.Cy) * (p.Y - cam.Cy));
                if (d > r)
                {
                    r = d;
                }
            }

            return r;
        }

        /// <summary>从 H 的列范数取"每像素多少毫米"（旋转不变，且两轴取平均）。</summary>
        private static double ScaleFromH(HomMat2D h)
        {
            double c1 = Math.Sqrt(h.H11 * h.H11 + h.H21 * h.H21);
            double c2 = Math.Sqrt(h.H12 * h.H12 + h.H22 * h.H22);
            if (c1 <= 0.0 || c2 <= 0.0)
            {
                return 0.0;
            }

            return 0.5 * (c1 + c2);
        }

        private static string VerdictFor(DistortionImpactAssessment a, string boardName)
        {
            string where = string.IsNullOrEmpty(boardName) ? "本标定覆盖的像面范围" : boardName;

            var sb = new System.Text.StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "在{0}内（半径 {1:F0} px ≈ {2:F1} mm），畸变把点最多挪 {3:F2} px ≈ {4:F3} mm。",
                where, a.WorstRadiusPx, a.WorkRadiusMm, a.MaxShiftPx, a.MaxShiftMm);

            if (a.MaxShiftMm >= NotableShiftMm)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    " ★ 这个量级已经超过 {0:F2} mm，值得处理：要么把 H 定义到矫正图上，"
                    + "要么在生产端按同口径补畸变（两者取其一，别混用）。",
                    NotableShiftMm);
            }
            else if (a.MaxShiftMm >= NegligibleShiftMm)
            {
                sb.Append(" 量级不大，但已不是噪声 —— 若工位精度要求接近 0.1 mm，建议还是把口径统一到矫正图。");
            }
            else
            {
                sb.Append(" 量级可忽略（远小于常见引导精度），维持现状即可 —— 不必为此改消费语义。");
            }

            if (!double.IsNaN(a.LooRmsOnRawMm) && !double.IsNaN(a.LooRmsOnUndistortedMm))
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    " 两套口径的质量对比：LOO 留一残差 {0:F4} → {1:F4} mm，"
                    + "σ1/σ2 {2:F5} → {3:F5}{4}。",
                    a.LooRmsOnRawMm, a.LooRmsOnUndistortedMm,
                    a.SigmaRatioOnRaw, a.SigmaRatioOnUndistorted,
                    a.LooRmsOnUndistortedMm < a.LooRmsOnRawMm * 0.98
                        ? "（去畸变确实吃掉了真误差，不是白折腾）"
                        : "（改善不明显 —— 残差本来就不是畸变主导的，别指望换口径解决系统性问题）");
            }

            return sb.ToString();
        }

        private static double Median(List<double> xs)
        {
            if (xs == null || xs.Count == 0)
            {
                return double.NaN;
            }

            var copy = new List<double>(xs);
            copy.Sort();
            int mid = copy.Count / 2;
            return copy.Count % 2 == 1 ? copy[mid] : 0.5 * (copy[mid - 1] + copy[mid]);
        }
    }
}
