using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// 一张图里"板相对相机摆成什么样"的几何事实。
    /// ★ 全部由 HALCON 层填（<c>pose_to_hom_mat3d</c> 取旋转矩阵的第三列 = 板法向），
    ///   本模块<b>不自己实现旋转约定</b>：HALCON 的 pose 有 <c>'Rp+T'/'gba'/'point'</c>
    ///   三个编码，每个都是一种约定，自己照着文档写一遍 = 埋一个只有出错时才暴露的坑。
    /// </summary>
    public struct BoardViewFact
    {
        /// <summary>板的平面法向（相机坐标系，单位向量）。正对时 ≈ (0, 0, −1)。</summary>
        public double NormalX;
        public double NormalY;
        public double NormalZ;

        /// <summary>板<b>中心</b>在相机坐标系里的位置（米）。深度取 Z。</summary>
        public double CenterX;
        public double CenterY;
        public double CenterZ;

        /// <summary>这张图检出多少 mark（0 = 没找到板）。</summary>
        public int MarkCount;

        /// <summary>检测出 mark 的像面重心（行 / 列像素），给"板偏在画面哪一侧"的提示用。</summary>
        public double MeanRow;
        public double MeanCol;

        /// <summary>
        /// 相邻 mark 的像面间距（像素，取中位数）。<b>这不是装饰性指标</b>：
        /// HALCON 找板时会把"小于期望尺寸的 mark 当噪声整片剔掉"，所以 mark 在像面上
        /// 有多大直接决定找不找得到。实测本相机：间距 19.2 px（231 mm）→ 检出 818 个 mark；
        /// 间距 18.4 px（240 mm）→ 检出 <b>0</b> 个。断崖在 ~18.8 px。
        ///
        /// ★ 为什么不用"检出 mark 的外接框"：板一倾斜或只认出一部分 mark（实测倾斜 25° 时
        ///   只认出 387/837），外接框会严重偏小 —— 曾据此算出"板宽只占画面 25%"的假结论。
        ///   相邻间距是<b>局部量</b>，局部认得出就量得准，几乎不受整体检出率影响。
        ///   （倾斜会把间距沿倾斜方向压掉 cosθ，所以取中位数而不是最小值。）
        /// </summary>
        public double ApparentSpacingPx;

        /// <summary>
        /// 板宽在像面上占多少像素 = 相邻间距 ×（列数 − 1）。
        /// 这就是操作员看得懂的那个数："板在画面里有多大"。
        /// </summary>
        public double ApparentWidthPx;
        public double ApparentHeightPx;

        /// <summary>板视在宽度占图像宽度的比例（0~1）。量不到时为 0。</summary>
        public double ApparentWidthFraction(int imageWidth)
        {
            return imageWidth > 0 ? ApparentWidthPx / imageWidth : 0.0;
        }

        /// <summary>这张图的重投影 RMS（像素）；NaN = 未算。</summary>
        public double ResidualPx;

        /// <summary>
        /// 检出 mark 的像面坐标（行 / 列像素，逐点对应）。
        /// ★ 为什么要留着：内参链的头号故障是"找不着板"，而操作员唯一能当场纠正的
        ///   就是"板摆得够不够大"。把检出点<b>画回画面上</b>，他立刻能分辨
        ///   "板太小所以被当噪声剔了" 与 "软件根本没在找" —— 只看一个总数做不到这件事。
        ///   （本字段<b>只服务于显示</b>，不参与解算，也不进产物。）
        /// </summary>
        public double[] MarkRows;

        public double[] MarkCols;

        /// <summary>人话名字（"左倾 25°"，仅用于报告）。</summary>
        public string Label;

        /// <summary>板法向与光轴夹角的度数（0 = 正对，越大越斜）。</summary>
        public double TiltDeg
        {
            get
            {
                double nz = Clamp(NormalZ, -1.0, 1.0);
                double t = Math.Acos(-nz) * 180.0 / Math.PI;
                if (t > 90.0)
                {
                    // 兜底：法向被解成"朝背"时按小角理解，免得报出 170° 这种不可能的倾角
                    t = 180.0 - t;
                }

                return t;
            }
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }

    /// <summary>覆盖度判据的阈值（可调，便于不同工位放宽 / 收紧）。</summary>
    public sealed class IntrinsicsCoverageCriteria
    {
        /// <summary>至少几张图是"斜着拍的"。</summary>
        public int MinTiltedViews = 3;

        /// <summary>最大倾角至少多少度。★ 20° 是实测钉死的下限（低于它焦距就约束不住）。</summary>
        public double MinMaxTiltDeg = 20.0;

        /// <summary>工作距离跨度至少多少毫米。</summary>
        public double MinDepthSpanMm = 10.0;

        /// <summary>倾斜方向至少覆盖几个（0/1/2/3/4，从 +X/−X/+Y/−Y 里数）。</summary>
        public int MinTiltDirections = 2;

        /// <summary>最少几张图（少于 3 张连基本解算都不稳）。</summary>
        public int MinViews = 3;

        public IntrinsicsCoverageCriteria Clone()
        {
            return (IntrinsicsCoverageCriteria)MemberwiseClone();
        }
    }

    /// <summary>
    /// ★ 姿态覆盖度的度量结果。<b>判据是几何量，不是精度指标</b> —— 这是本轮最重要的
    /// 实测结论之一：合成图无噪声时，"全部正对"的姿态集也能拟合到 RMSE 0.008 px，
    /// 甚至能解出接近真值的 kappa。换句话说<b>残差好看完全不代表覆盖度够</b>。
    /// 能真正分开"够/不够"的只有：斜着拍了几张、最大斜多少、远近变过没有。
    /// </summary>
    public sealed class IntrinsicsCoverage
    {
        public int ViewCount;
        public int FoundViewCount;

        /// <summary>倾角 ≥ <see cref="IntrinsicsCoverageCriteria.MinMaxTiltDeg"/> 的图数。</summary>
        public int TiltCount;

        public double MaxTiltDeg;
        public double MinTiltDeg;

        /// <summary>板中心深度跨度（毫米）。</summary>
        public double DepthSpanMm;

        public double MinDepthMm;
        public double MaxDepthMm;

        /// <summary>倾斜方向覆盖到的象限数（0~4：+X / −X / +Y / −Y）。</summary>
        public int TiltDirectionCount;

        /// <summary>每个方向的代表角（人话用）。</summary>
        public readonly List<string> TiltDirections = new List<string>();

        public bool Sufficient;
        public readonly List<string> Hints = new List<string>();

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} 张图（成功找板 {1} 张），其中斜着拍的 {2} 张，最大倾角 {3:F1}°、最小 {4:F1}°，"
                + "工作距离跨度 {5:F1} mm（{6:F0}~{7:F0} mm），倾斜方向覆盖 {8} 个{9}",
                ViewCount, FoundViewCount, TiltCount, MaxTiltDeg, MinTiltDeg,
                DepthSpanMm, MinDepthMm * 1000.0, MaxDepthMm * 1000.0, TiltDirectionCount,
                TiltDirections.Count == 0 ? string.Empty : "（" + string.Join("/", TiltDirections.ToArray()) + "）");
        }
    }

    /// <summary>
    /// ★ 板检出叠加载荷的组装（纯函数，零 HALCON / 零 WPF 依赖）。
    ///
    /// 为什么单独抽出来、而不是留在 <c>IntrinsicsRunner</c> 里就地组装：
    ///   · <b>它必须能被离线断言</b>。"找不着板"这条路在正常仿真里跑不出来（仿真里板永远在），
    ///     留在 runner 里就意味着一整条分支（含"0 个 mark"这个最要命的形态）永远没人验过；
    ///   · 阈值（板宽应占画面多少）<b>必须由调用方传进来</b>，不能在这里再写一个 0.50 ——
    ///     两处各写一个数字，早晚会变成两个口径。
    /// </summary>
    public static class BoardOverlayBuilder
    {
        /// <summary>由本帧的检出事实组装载荷。全部未测量量显式写 NaN，不留 0。</summary>
        public static BoardOverlayPayload FromFact(BoardViewFact fact, int expectMarkCount,
            int width, int height, byte[] rawGray, string label, double minApparentWidthFraction)
        {
            double frac = width > 0 ? fact.ApparentWidthFraction(width) : 0.0;

            var board = NewSkeleton(rawGray, width, height, label);
            board.ApparentSpacingPx = fact.ApparentSpacingPx;
            board.ApparentWidthFraction = frac;
            board.TooSmall = frac > 0.0 && minApparentWidthFraction > 0.0
                && frac < minApparentWidthFraction;

            int n = fact.MarkRows == null ? 0 : fact.MarkRows.Length;
            if (n > 0 && fact.MarkCols != null && fact.MarkCols.Length == n)
            {
                double minRow = double.MaxValue, maxRow = double.MinValue;
                double minCol = double.MaxValue, maxCol = double.MinValue;
                for (int i = 0; i < n; i++)
                {
                    double r = fact.MarkRows[i];
                    double c = fact.MarkCols[i];

                    // (X = 列, Y = 行) —— 与整套叠加载荷同一约定；绘制时才换成 (row, col)
                    board.Marks.Add(new Vec2(c, r));

                    if (r < minRow) { minRow = r; }
                    if (r > maxRow) { maxRow = r; }
                    if (c < minCol) { minCol = c; }
                    if (c > maxCol) { maxCol = c; }
                }

                board.MinRow = minRow;
                board.MaxRow = maxRow;
                board.MinCol = minCol;
                board.MaxCol = maxCol;
            }

            board.MarkCoverage = expectMarkCount > 0
                ? (double)fact.MarkCount / expectMarkCount
                : double.NaN;

            board.Verdict = BuildVerdict(fact.MarkCount, expectMarkCount, frac,
                fact.ApparentSpacingPx, board.TooSmall);
            return board;
        }

        /// <summary>
        /// 没找到板时也组装一份：<b>画面要显示，只是一个 mark 都不画</b>。
        /// 这时候操作员的疑问不是"检出率多少"，而是"我这张到底拍成了什么" ——
        /// 只给一句"没找到板"，他连"图糊了 / 板出界了 / 板太小"都分不出来。
        /// </summary>
        public static BoardOverlayPayload NotFound(byte[] rawGray, int width, int height,
            string label, string error)
        {
            var board = NewSkeleton(rawGray, width, height, label);
            board.Verdict = "没找到板：" + (string.IsNullOrEmpty(error) ? "未知原因" : error)
                + "  —— 先看这张画面：板在不在画面里、是不是拍到一半就出界了。";
            return board;
        }

        private static BoardOverlayPayload NewSkeleton(byte[] rawGray, int width, int height, string label)
        {
            return new BoardOverlayPayload
            {
                RawGray = rawGray,
                Width = width,
                Height = height,
                Label = label,

                // ★ 未测量一律 NaN，绝不留 0：留 0 会被读成"量过了，范围是零"，
                //   而这三种情况完全可能是"压根没量到"。外接框一个 mark 都没有时
                //   在数学上确实是空集，但"空"与"零尺寸"是两件事，绘图层靠 NaN 区分。
                MinCol = double.NaN,
                MaxCol = double.NaN,
                MinRow = double.NaN,
                MaxRow = double.NaN,
                MarkCoverage = double.NaN
            };
        }

        /// <summary>角标上那句人话（先说"认出了多少"，再说"够不够大"）。</summary>
        private static string BuildVerdict(int markCount, int expect, double frac,
            double spacingPx, bool tooSmall)
        {
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "检出 {0} 个 mark", markCount);
            if (expect > 0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "（板共 {0} 个，{1:P0}）",
                    expect, (double)markCount / expect);
            }

            if (spacingPx > 0.0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "；相邻间距 {0:F1} px", spacingPx);
            }

            if (frac > 0.0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "；板宽占画面 {0:P0}", frac);
            }

            sb.Append(tooSmall
                ? "  ⚠ 板偏小：找板算法会把小 mark 当噪声整片剔掉，往镜头方向挪近一点再拍"
                : "  ✓ 尺寸够用");
            return sb.ToString();
        }
    }

    /// <summary>
    /// ★★ 内参链的"够不够"判据与人话提示（纯计算，零 HALCON / 零 WPF 依赖，可离线断言）。
    ///
    /// 为什么单独成模块：这条判据要<b>在操作员还在摆板的时候</b>就能回答"还差什么"，
    /// 所以它只能是纯几何；而它又决定了整条内参链可不可信，所以必须能被离线测。
    /// </summary>
    public static class IntrinsicsGeometry
    {
        /// <summary>按事实集合算覆盖度并给结论 + 人话提示。</summary>
        public static IntrinsicsCoverage Evaluate(IList<BoardViewFact> views,
            IntrinsicsCoverageCriteria criteria)
        {
            if (criteria == null)
            {
                criteria = new IntrinsicsCoverageCriteria();
            }

            var cov = new IntrinsicsCoverage();
            if (views == null || views.Count == 0)
            {
                cov.Hints.Add("还没有任何一张图。请把标定板放进视野，拍至少 3 张（其中 3 张要斜着拍）。");
                return cov;
            }

            cov.ViewCount = views.Count;
            cov.MaxTiltDeg = double.NegativeInfinity;
            cov.MinTiltDeg = double.PositiveInfinity;
            cov.MaxDepthMm = double.NegativeInfinity;
            cov.MinDepthMm = double.PositiveInfinity;

            var dirs = new List<string>();
            for (int i = 0; i < views.Count; i++)
            {
                BoardViewFact f = views[i];
                if (f.MarkCount <= 0)
                {
                    continue;      // 没找到板的图不参与覆盖度统计（它压根没提供几何信息）
                }

                cov.FoundViewCount++;

                double tilt = f.TiltDeg;
                if (tilt > cov.MaxTiltDeg)
                {
                    cov.MaxTiltDeg = tilt;
                }

                if (tilt < cov.MinTiltDeg)
                {
                    cov.MinTiltDeg = tilt;
                }

                double depth = f.CenterZ;
                if (depth > cov.MaxDepthMm)
                {
                    cov.MaxDepthMm = depth;
                }

                if (depth < cov.MinDepthMm)
                {
                    cov.MinDepthMm = depth;
                }

                if (tilt >= criteria.MinMaxTiltDeg)
                {
                    cov.TiltCount++;
                }

                AddDirection(dirs, f, tilt, criteria.MinMaxTiltDeg);
            }

            if (cov.FoundViewCount == 0)
            {
                cov.MaxTiltDeg = 0.0;
                cov.MinTiltDeg = 0.0;
                cov.MaxDepthMm = 0.0;
                cov.MinDepthMm = 0.0;
                cov.Hints.Add("一张图都没找到标定板 —— 先解决「找板」，覆盖度无从谈起。"
                            + "常见原因：板没进画面、曝光过亮/过暗、板被反光吃掉、板文件与实物不是同一块。");
                return cov;
            }

            cov.DepthSpanMm = (cov.MaxDepthMm - cov.MinDepthMm) * 1000.0;
            cov.TiltDirectionCount = dirs.Count;
            cov.TiltDirections.AddRange(dirs);

            cov.Sufficient =
                cov.FoundViewCount >= criteria.MinViews
                && cov.TiltCount >= criteria.MinTiltedViews
                && cov.MaxTiltDeg >= criteria.MinMaxTiltDeg
                && cov.DepthSpanMm >= criteria.MinDepthSpanMm
                && cov.TiltDirectionCount >= criteria.MinTiltDirections;

            BuildHints(cov, criteria);
            return cov;
        }

        private static void AddDirection(List<string> dirs, BoardViewFact f, double tilt, double minTilt)
        {
            if (tilt < minTilt)
            {
                return;
            }

            // 板法向里"垂直于光轴"的那两个分量，就是倾斜的朝向。
            // 取主导轴 + 符号，落进 +X / −X / +Y / −Y 四格之一。
            double nx = f.NormalX;
            double ny = f.NormalY;
            string key;
            if (Math.Abs(nx) >= Math.Abs(ny))
            {
                key = nx >= 0 ? "+X" : "-X";
            }
            else
            {
                key = ny >= 0 ? "+Y" : "-Y";
            }

            if (!dirs.Contains(key))
            {
                dirs.Add(key);
            }
        }

        /// <summary>把"差什么"翻译成操作员能照做的事。</summary>
        private static void BuildHints(IntrinsicsCoverage cov, IntrinsicsCoverageCriteria c)
        {
            if (cov.FoundViewCount < c.MinViews)
            {
                cov.Hints.Add(string.Format(CultureInfo.InvariantCulture,
                    "图太少：现在成功找到板的只有 {0} 张，至少要 {1} 张。",
                    cov.FoundViewCount, c.MinViews));
            }

            if (cov.TiltCount < c.MinTiltedViews)
            {
                cov.Hints.Add(string.Format(CultureInfo.InvariantCulture,
                    "斜着拍的图不够：现在只有 {0} 张倾角 ≥{1:F0}°，至少要 {2} 张。"
                    + "把板绕自己的横轴或竖轴「翘起来」20~30°（不是平移、不是旋转），再拍 {3} 张。",
                    cov.TiltCount, c.MinMaxTiltDeg, c.MinTiltedViews, c.MinTiltedViews - cov.TiltCount));
            }

            if (cov.MaxTiltDeg < c.MinMaxTiltDeg)
            {
                cov.Hints.Add(string.Format(CultureInfo.InvariantCulture,
                    "倾斜幅度不够：最大的那张才 {0:F1}°，要到 {1:F0}° 以上。"
                    + "注意是「板面被拍歪」的角度，不是把板挪个位置。",
                    cov.MaxTiltDeg, c.MinMaxTiltDeg));
            }

            if (cov.DepthSpanMm < c.MinDepthSpanMm)
            {
                cov.Hints.Add(string.Format(CultureInfo.InvariantCulture,
                    "远近没变化：工作距离跨度只有 {0:F1} mm，要 ≥{1:F0} mm。"
                    + "把板沿光轴方向挪近 / 挪远，各拍一张（例如 {2:F0} mm 和 {3:F0} mm 处）。",
                    cov.DepthSpanMm, c.MinDepthSpanMm,
                    cov.MinDepthMm * 1000.0, cov.MinDepthMm * 1000.0 + c.MinDepthSpanMm));
            }

            if (cov.TiltDirectionCount < c.MinTiltDirections)
            {
                cov.Hints.Add(string.Format(CultureInfo.InvariantCulture,
                    "倾斜方向太单一：现在只覆盖 {0} 个方向（已覆盖 {1}），至少 {2} 个。"
                    + "同一块板，先往左翘着拍，再往右翘着拍（或前后各一次）—— "
                    + "只往一边翘会让畸变系数与主点之间互相「顶账」，两者都不可信。",
                    cov.TiltDirectionCount,
                    cov.TiltDirections.Count == 0 ? "无" : string.Join("/", cov.TiltDirections.ToArray()),
                    c.MinTiltDirections));
            }

            if (cov.Sufficient)
            {
                cov.Hints.Add("覆盖度够：倾斜张数 / 最大倾角 / 工作距离跨度 / 倾斜方向四项都达标，"
                            + "焦距与畸变可以分别约束住。");
            }
        }

        /// <summary>
        /// 逐张重投影误差的判读：找出拖后腿的那几张。
        /// ★ 单张最差比整体 RMS 更有用 —— 整体 RMS 会被一大半好图压下去，
        ///   而"某一张特别差"通常意味着那张的板位姿解错了（反光、板没放平、动过）。
        /// </summary>
        public static List<string> JudgeResiduals(IList<BoardViewFact> views, double warnPx, double blockPx)
        {
            var notes = new List<string>();
            var bad = new List<string>();
            var worst = new List<string>();

            if (views != null)
            {
                for (int i = 0; i < views.Count; i++)
                {
                    BoardViewFact f = views[i];
                    if (f.MarkCount <= 0 || double.IsNaN(f.ResidualPx))
                    {
                        worst.Add(Name(f, i) + "（没找到板）");
                        continue;
                    }

                    if (f.ResidualPx > blockPx)
                    {
                        bad.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0} {1:F3} px", Name(f, i), f.ResidualPx));
                    }
                    else if (f.ResidualPx > warnPx)
                    {
                        worst.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0} {1:F3} px", Name(f, i), f.ResidualPx));
                    }
                }
            }

            if (bad.Count > 0)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "有 {0} 张图的重投影误差超过 {1:F2} px，建议重拍：{2}。"
                    + "（单张特别差通常是那张的板被反光/阴影干扰，或拍摄时板动了。）",
                    bad.Count, blockPx, string.Join("、", bad.ToArray())));
            }

            if (worst.Count > 0)
            {
                notes.Add("相对偏大的图（可接受但值得看一眼）：" + string.Join("、", worst.ToArray()));
            }

            return notes;
        }

        private static string Name(BoardViewFact f, int index)
        {
            return string.IsNullOrEmpty(f.Label)
                ? "第 " + (index + 1).ToString(CultureInfo.InvariantCulture) + " 张"
                : f.Label;
        }

        /// <summary>
        /// ★ "去畸变之后会好多少"的量化 —— 用户真正关心的那个数。
        /// 给一个离轴像素点（相对主点最远处的世界半径），算畸变把它挪了多少像素：
        /// 这就是"不做内参标定会吃多少误差"的量级。
        /// ★ 公式用的是 HALCON 除法模型：位移 ≈ r · |κ| · r_m²，r_m = r·像素尺寸（米）。
        ///   注意 κ 的口径是「像面公制半径（1/m²）」，不是常见的归一化系数。
        /// </summary>
        public static double DistortionShiftPx(double radiusPx, double kappa, double pixelPitchM)
        {
            if (double.IsNaN(kappa) || double.IsNaN(radiusPx) || double.IsNaN(pixelPitchM))
            {
                return double.NaN;
            }

            double rm = radiusPx * pixelPitchM;
            return Math.Abs(radiusPx * kappa * rm * rm);
        }

        /// <summary>
        /// 归一化畸变系数 k1 → HALCON 除法模型的 κ。<c>κ = k1 / f²</c>（f 单位米）。
        /// ★ 这个换算就是本轮踩过的那个量纲坑：把归一化的 −0.15 直接当 κ 用，
        ///   等于"没有畸变"，会让"注入-解回"变成自证。
        /// </summary>
        public static double KappaFromNormalized(double k1, double focalM)
        {
            if (Math.Abs(focalM) < 1e-12)
            {
                return double.NaN;
            }

            return k1 / (focalM * focalM);
        }

        /// <summary>HALCON 除法模型的 κ → 归一化 k1（反向换算，写报告时用）。</summary>
        public static double NormalizedFromKappa(double kappa, double focalM)
        {
            return kappa * focalM * focalM;
        }
    }
}
