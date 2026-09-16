using Grayson.Vision.WpfUI.Model;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.WpfUI.Common
{
    /// <summary>机器人基座坐标系下的二维点（mm）</summary>
    public struct Pt2
    {
        public double X;
        public double Y;

        public Pt2(double x, double y) { X = x; Y = y; }

        public double Radius => Math.Sqrt(X * X + Y * Y);

        /// <summary>极角，归一到 [0,360)</summary>
        public double Degree
        {
            get
            {
                double a = Math.Atan2(Y, X) * 180.0 / Math.PI;
                if (a < 0) a += 360.0;
                return a;
            }
        }

        public override string ToString() => $"({X:F2}, {Y:F2})";
    }

    /// <summary>单点可达性判定结果</summary>
    public enum ReachPointState
    {
        /// <summary>没有可达域数据，无法判定</summary>
        Unknown = 0,
        /// <summary>在扣除 margin 后的可达域内</summary>
        Safe = 1,
        /// <summary>越过内边界（太靠近基座 / 落进内圈空洞）</summary>
        NearInner = 2,
        /// <summary>越过外边界（够不着）</summary>
        NearOuter = 3,
        /// <summary>该方向整体不可达（关节限位扇区 / 实测无解）</summary>
        Unsafe = 4,
    }

    /// <summary>单点判定明细（含"还差多少 mm"，可直接呈现给操作员）</summary>
    public sealed class ReachPointVerdict
    {
        public ReachPointState State { get; set; }

        /// <summary>该点相对基座的半径 mm</summary>
        public double R { get; set; }

        /// <summary>该点方向角 deg</summary>
        public double Deg { get; set; }

        /// <summary>该方向实测内边界（未扣 margin；Infinity = 不可达）</summary>
        public double RIn { get; set; }

        /// <summary>该方向实测外边界（未扣 margin；0 = 不可达）</summary>
        public double ROut { get; set; }

        /// <summary>到最近边界的余量 mm：安全时为正（剩余量），不安全时为负（超出量）</summary>
        public double Slack { get; set; }

        /// <summary>操作员可读结论</summary>
        public string Text
        {
            get
            {
                switch (State)
                {
                    case ReachPointState.Safe:
                        return $"安全（余量 {Slack:F1} mm）";
                    case ReachPointState.NearInner:
                        return $"内圈越界（超出 {-Slack:F1} mm，两臂收不拢）";
                    case ReachPointState.NearOuter:
                        return $"外圈越界（超出 {-Slack:F1} mm，够不着）";
                    case ReachPointState.Unsafe:
                        return "该方向整体不可达（关节限位/无解）";
                    default:
                        return "无可达域数据";
                }
            }
        }
    }

    /// <summary>
    /// 可行基准位栅格位图：true 的像素表示"以该点为基准位时，9 个网格点全部安全"。
    /// 这是本方案最有价值的产物 —— 把"不知道怎么保证不撞机"变成"绿区里随便挑"。
    /// </summary>
    public sealed class FeasibleBitmap
    {
        public int W { get; set; }
        public int H { get; set; }

        /// <summary>行主序，[r * W + c]；r=0 对应世界 Y 最大（上方）</summary>
        public bool[] Bits { get; set; }

        public double XMin { get; set; }
        public double XMax { get; set; }
        public double YMin { get; set; }
        public double YMax { get; set; }

        /// <summary>可行像素数（0 = 该步长/margin 下不存在可行基准位）</summary>
        public int Count { get; set; }

        /// <summary>最稳基准位（可行域最大内接圆圆心），null = 无可行域</summary>
        public Pt2? BestCenter { get; set; }

        /// <summary>最稳基准位到最近不可行像素的距离 mm（可理解为"整体余量"）</summary>
        public double BestClearanceMm { get; set; }

        /// <summary>可行域贴着画布边缘 → 真实绿区可能更大（提示扩大视野）</summary>
        public bool TouchesBorder { get; set; }

        public double PixelWidth => W > 0 ? (XMax - XMin) / W : 0;

        /// <summary>世界坐标 → 像素列（不取整）</summary>
        public double ColOf(double wx) => (wx - XMin) / (XMax - XMin) * W;

        /// <summary>世界坐标 → 像素行（不取整）</summary>
        public double RowOf(double wy) => (YMax - wy) / (YMax - YMin) * H;

        public bool IsFeasibleAt(double wx, double wy)
        {
            int c = (int)Math.Floor(ColOf(wx));
            int r = (int)Math.Floor(RowOf(wy));
            if (Bits == null || c < 0 || c >= W || r < 0 || r >= H) return false;
            return Bits[r * W + c];
        }
    }

    /// <summary>
    /// 可达域几何计算（纯计算，不依赖任何 WPF 绘图类型 —— 便于离线自检与复用）。
    ///
    /// 【核心判据】给定点 (x,y)：先取极坐标 r/θ，再从实测方向序列插值出该方向的
    /// [r_in, r_out]，判定 r_in + margin ≤ r ≤ r_out − margin。
    ///
    /// 【插值取向：一律保守】
    ///   r_in 取相邻两端的 max、r_out 取相邻两端的 min（即取上/下包络）。
    ///   理由：线性插值遇到非凸边界会低估 r_in（内凹处），产生"假安全"。
    ///   宁可多留余量，也不给出可能撞机的绿区。任一端为 null（该方向不可达）时，
    ///   传播为整段不可达 —— 不假设 10° 弧内的中间角度可达。
    ///
    /// 【精度边界】扫描方向数决定角分辨率；J1 限位扇区边缘若比采样间隔更细，
    ///   会被漏掉。现场扫描请用 --dirs 36（默认）或更密。
    /// </summary>
    public static class ReachMapGeometry
    {
        /// <summary>
        /// 取某角度方向的可达半径区间（保守插值）。
        /// 返回 false 表示该方向不可达（或没有数据）。
        /// </summary>
        public static bool TryBandAt(ReachMapData map, double deg, out double rIn, out double rOut)
        {
            rIn = double.PositiveInfinity;
            rOut = 0;
            if (map == null || map.IsEmpty) return false;

            var dirs = map.Directions;
            int n = dirs.Count;

            double a = deg % 360.0;
            if (a < 0) a += 360.0;

            int i0, i1;
            double w;

            // 方向序列已按 Deg 升序。优先走"均匀分布"快路径（扫描器默认 360/n 均匀）
            double stepDeg = 360.0 / n;
            if (Math.Abs(dirs[1].Deg - dirs[0].Deg - stepDeg) < 1e-6)
            {
                double rel = a / stepDeg;
                int idx = (int)rel;
                if (idx >= n) idx = 0;          // 恰好 360° → 回到 0°
                i0 = idx;
                i1 = (i0 + 1) % n;
                w = rel - Math.Floor(rel);
            }
            else
            {
                // 通用路径：线性查找所在的采样区间（含环形回绕）
                int lo = -1;
                for (int i = 0; i < n; i++)
                {
                    if (dirs[i].Deg <= a + 1e-9) lo = i;
                    else break;
                }
                if (lo < 0) { i0 = n - 1; i1 = 0; double d0 = dirs[i0].Deg - 360.0; double span = dirs[i1].Deg - d0; w = span > 1e-9 ? (a - d0) / span : 0; }
                else if (lo == n - 1) { i0 = n - 1; i1 = 0; double span = dirs[i1].Deg + 360.0 - dirs[i0].Deg; w = span > 1e-9 ? (a - dirs[i0].Deg) / span : 0; }
                else { i0 = lo; i1 = lo + 1; double span = dirs[i1].Deg - dirs[i0].Deg; w = span > 1e-9 ? (a - dirs[i0].Deg) / span : 0; }
            }

            var d0v = dirs[i0];
            var d1v = dirs[i1];

            // ---- 保守插值 ----
            double inner0 = d0v.RIn ?? double.PositiveInfinity;
            double inner1 = d1v.RIn ?? double.PositiveInfinity;
            double outer0 = d0v.ROut ?? 0.0;
            double outer1 = d1v.ROut ?? 0.0;

            rIn = Math.Max(inner0, inner1);
            rOut = Math.Min(outer0, outer1);

            return rOut > 0 && !double.IsPositiveInfinity(rIn) && rIn < rOut;
        }

        /// <summary>判定单点（含 margin）</summary>
        public static ReachPointVerdict Evaluate(ReachMapData map, double x, double y, double margin)
        {
            var v = new ReachPointVerdict { R = Math.Sqrt(x * x + y * y) };

            double a = Math.Atan2(y, x) * 180.0 / Math.PI;
            if (a < 0) a += 360.0;
            v.Deg = a;

            if (map == null || map.IsEmpty)
            {
                v.State = ReachPointState.Unknown;
                return v;
            }

            double rIn, rOut;
            if (!TryBandAt(map, a, out rIn, out rOut))
            {
                v.State = ReachPointState.Unsafe;
                v.RIn = rIn;
                v.ROut = rOut;
                v.Slack = double.NegativeInfinity;
                return v;
            }

            v.RIn = rIn;
            v.ROut = rOut;

            double lo = rIn + margin;          // 内边界再加 margin → 更靠近外圈
            double hi = rOut - margin;         // 外边界再扣 margin → 更靠近内圈

            if (lo > hi)
            {
                // margin 比可用带宽还大：本方向没有安全区
                v.State = ReachPointState.NearInner;
                v.Slack = hi - v.R;
                return v;
            }

            if (v.R < lo) { v.State = ReachPointState.NearInner; v.Slack = v.R - lo; return v; }
            if (v.R > hi) { v.State = ReachPointState.NearOuter; v.Slack = hi - v.R; return v; }

            v.State = ReachPointState.Safe;
            v.Slack = Math.Min(v.R - lo, hi - v.R);
            return v;
        }

        /// <summary>
        /// 九点网格相对基准位的偏移（按 CalibrationPointModel.Index 1~9 固定对应 3×3 网格）。
        ///
        /// 【必须与标定引擎同公式】公式来自 CalibrationWizardViewModel.TryGetNinePointTarget：
        ///   row=(i-1)/3, col=(i-1)%3, offset=(col-1)*StepX / (row-1)*StepY；
        ///   轴镜像翻转；EyeInHand 时整体取反（标定板静止、相机反向走）。
        /// 画布上的序号标注若与此不一致，操作员会照着错的点去判断，故两处共用本方法。
        /// </summary>
        public static Pt2[] GridOffsets(double stepX, double stepY, bool invertX, bool invertY, bool eyeInHand)
        {
            var res = new Pt2[9];
            for (int i = 1; i <= 9; i++)
            {
                int row = (i - 1) / 3;
                int col = (i - 1) % 3;
                double ox = (col - 1) * stepX;
                double oy = (row - 1) * stepY;
                if (invertX) ox = -ox;
                if (invertY) oy = -oy;
                res[i - 1] = new Pt2(eyeInHand ? -ox : ox, eyeInHand ? -oy : oy);
            }
            return res;
        }

        /// <summary>九点绝对坐标（基准 + 偏移）</summary>
        public static Pt2[] BuildGrid(double baseX, double baseY, double stepX, double stepY,
                                      bool invertX, bool invertY, bool eyeInHand)
        {
            var offs = GridOffsets(stepX, stepY, invertX, invertY, eyeInHand);
            for (int i = 0; i < offs.Length; i++)
            {
                offs[i].X += baseX;
                offs[i].Y += baseY;
            }
            return offs;
        }

        /// <summary>
        /// 计算画布取景范围：以可达域全部边界端点（含 margin）为基准，可并入额外关注点
        /// （如当前机器位置）。返回的矩形保证四边留 8% 余量。
        /// </summary>
        public static void ComputeBounds(ReachMapData map, double margin,
                                         IEnumerable<Pt2> extraPoints,
                                         out double xMin, out double xMax,
                                         out double yMin, out double yMax)
        {
            double x0 = double.MaxValue, x1 = double.MinValue;
            double y0 = double.MaxValue, y1 = double.MinValue;

            void Bump(double x, double y)
            {
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }

            if (map != null && !map.IsEmpty)
            {
                foreach (var d in map.Directions)
                {
                    double rad = d.Deg * Math.PI / 180.0;
                    double ca = Math.Cos(rad), sa = Math.Sin(rad);
                    if (d.ROut.HasValue && d.ROut.Value > 0)
                    {
                        double r = d.ROut.Value + margin;
                        Bump(r * ca, r * sa);
                    }
                    if (d.RIn.HasValue && d.RIn.Value > 0)
                    {
                        double r = Math.Max(0, d.RIn.Value - margin);
                        Bump(r * ca, r * sa);
                    }
                }
            }

            Bump(0, 0);   // 基座原点永远在视野内

            if (extraPoints != null)
            {
                foreach (var p in extraPoints) Bump(p.X, p.Y);
            }

            if (x0 > x1) { x0 = -100; x1 = 100; y0 = -100; y1 = 100; }

            double w = x1 - x0, h = y1 - y0;
            if (w < 1e-6) w = 1;
            if (h < 1e-6) h = 1;
            double padX = w * 0.08, padY = h * 0.08;

            xMin = x0 - padX; xMax = x1 + padX;
            yMin = y0 - padY; yMax = y1 + padY;
        }

        /// <summary>
        /// 计算"可行基准位"栅格位图。
        ///
        /// 算法：先把"扣除 margin 后的可达域"栅格化成语义位图 safe[]，再对 9 个网格偏移
        /// 逐个平移取交集（形态学腐蚀）：
        ///     feasible[b] = AND over offsets o of safe[b + o]
        /// 即"基准位 b 的 9 个点全部落在安全域内"。
        ///
        /// 复杂度 O(W·H·(1+n))，512×512 约 20ms —— 只在 margin/步长变化时重算；
        /// 拖动基准位时不重算（safe 与 offsets 都没变），故拖动是流畅的。
        /// </summary>
        public static FeasibleBitmap ComputeFeasibleBitmap(
            ReachMapData map, double margin, Pt2[] offsets,
            double xMin, double xMax, double yMin, double yMax, int w, int h)
        {
            var bmp = new FeasibleBitmap { W = w, H = h, XMin = xMin, XMax = xMax, YMin = yMin, YMax = yMax };
            if (w <= 0 || h <= 0) return bmp;

            double sx = (xMax - xMin) / w;
            double sy = (yMax - yMin) / h;

            // ---- 1. 安全域位图（世界 Y 向上 → 行号从大到小）----
            var safe = new bool[w * h];
            for (int r = 0; r < h; r++)
            {
                double wy = yMax - (r + 0.5) * sy;
                int rowBase = r * w;
                for (int c = 0; c < w; c++)
                {
                    double wx = xMin + (c + 0.5) * sx;
                    safe[rowBase + c] = Evaluate(map, wx, wy, margin).State == ReachPointState.Safe;
                }
            }

            // ---- 2. 逐个网格偏移平移取交集 ----
            var bits = (bool[])safe.Clone();
            if (offsets != null && offsets.Length > 0)
            {
                foreach (var o in offsets)
                {
                    int dc = (int)Math.Round(o.X / sx);
                    int dr = (int)Math.Round(-o.Y / sy);   // 世界 Y 向上，行向下 → 取负

                    for (int r = 0; r < h; r++)
                    {
                        int rowBase = r * w;
                        int sr = r + dr;
                        if (sr < 0 || sr >= h)
                        {
                            for (int c = 0; c < w; c++) bits[rowBase + c] = false;
                            continue;
                        }
                        int srowBase = sr * w;
                        int lit = Math.Max(0, -dc);
                        int hit = Math.Min(w, w - dc);
                        for (int c = 0; c < lit; c++) bits[rowBase + c] = false;
                        for (int c = lit; c < hit; c++) bits[rowBase + c] &= safe[srowBase + c + dc];
                        for (int c = hit; c < w; c++) bits[rowBase + c] = false;
                    }
                }
            }

            int count = 0;
            for (int i = 0; i < bits.Length; i++) if (bits[i]) count++;
            bmp.Bits = bits;
            bmp.Count = count;

            // ---- 3. 最稳基准位：可行域的最大内接圆圆心（chamfer 3-4 距离变换）----
            if (count > 0)
            {
                FindBestCenter(bmp, sx, sy);
            }

            return bmp;
        }

        /// <summary>用 3-4 chamfer 距离变换找可行区域的最大内接圆圆心</summary>
        private static void FindBestCenter(FeasibleBitmap bmp, double sx, double sy)
        {
            int w = bmp.W, h = bmp.H;
            var bits = bmp.Bits;
            const int INF = 1 << 20;

            var dist = new int[w * h];
            for (int i = 0; i < dist.Length; i++) dist[i] = bits[i] ? INF : 0;

            // 正向传播
            for (int r = 0; r < h; r++)
            {
                int rowBase = r * w;
                int upBase = rowBase - w;
                for (int c = 0; c < w; c++)
                {
                    int i = rowBase + c;
                    int v = dist[i];
                    if (v == 0) continue;
                    if (r > 0)
                    {
                        int t = dist[upBase + c] + 3; if (t < v) v = t;
                        if (c > 0) { t = dist[upBase + c - 1] + 4; if (t < v) v = t; }
                        if (c < w - 1) { t = dist[upBase + c + 1] + 4; if (t < v) v = t; }
                    }
                    if (c > 0) { int t = dist[i - 1] + 3; if (t < v) v = t; }
                    dist[i] = v;
                }
            }

            // 反向传播
            for (int r = h - 1; r >= 0; r--)
            {
                int rowBase = r * w;
                int dnBase = rowBase + w;
                for (int c = w - 1; c >= 0; c--)
                {
                    int i = rowBase + c;
                    int v = dist[i];
                    if (v == 0) continue;
                    if (r < h - 1)
                    {
                        int t = dist[dnBase + c] + 3; if (t < v) v = t;
                        if (c > 0) { t = dist[dnBase + c - 1] + 4; if (t < v) v = t; }
                        if (c < w - 1) { t = dist[dnBase + c + 1] + 4; if (t < v) v = t; }
                    }
                    if (c < w - 1) { int t = dist[i + 1] + 3; if (t < v) v = t; }
                    dist[i] = v;
                }
            }

            int bestIdx = -1, bestD = -1;
            for (int i = 0; i < dist.Length; i++)
            {
                if (bits[i] && dist[i] > bestD) { bestD = dist[i]; bestIdx = i; }
            }
            if (bestIdx < 0) return;

            int br = bestIdx / w, bc = bestIdx % w;
            double wx = bmp.XMin + (bc + 0.5) * sx;
            double wy = bmp.YMax - (br + 0.5) * sy;
            bmp.BestCenter = new Pt2(wx, wy);

            // chamfer 距离单位是 3 ≈ 1 像素 → 除以 3；再取 x/y 像素尺度的较小者换算 mm
            double pxMm = Math.Min(sx, sy);
            bmp.BestClearanceMm = bestD / 3.0 * pxMm;

            // 贴着画布边缘 → 真实绿区可能更大，提示操作员扩大视野
            bmp.TouchesBorder = br < 3 || br > h - 4 || bc < 3 || bc > w - 4;
        }
    }
}
