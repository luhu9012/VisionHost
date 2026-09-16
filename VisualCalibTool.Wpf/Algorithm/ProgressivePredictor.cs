using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// ★ 逐点采样时的"下一点大概在哪"预测器。
    ///
    /// 为什么必须要有它：
    ///   标定采样每次都是"走到新位置 → 拍照 → 找同一个固定 Mark"。九点里第 1 点没有任何先验，
    ///   只能全图搜；但如果**每一**点都全图搜，就有两个恶果：
    ///     ① 慢（全图模式是局部 ROI 的数倍开销，九点 + 三条链累起来很可观）；
    ///     ② 危险 —— 全图搜会把视场里任何一个合格圆都当候选，"选错那个 blob"从此有了可能。
    ///
    /// 三级策略（从左到右逐级退让，每一级都为下一级留好兜底）：
    ///   ① 已经能解出临时 H（≥3 个非共线点）→ 用 H⁻¹(目标世界位) **精确**预测，ROI 回到常规尺寸；
    ///   ② 有 ≥2 点 → 用实测的 |Δ世界| / |Δ像素| 量出像素尺度，把 ROI 半径放大到
    ///      "够覆盖一整步位移"（方向未知所以用半径覆盖整步，**不猜方向**）；
    ///   ③ 什么都不够 → 返回 false，让调用方走全图（第 1 点必然走这里）。
    ///
    /// ★ 第 ② 级的取向是"宁可圈大"，所以量尺度时取所有点对里的**最小**比值
    ///   （最小值对应 A 的最小奇异值，外推出的像素位移最大 ⇒ 半径最大 ⇒ 最保守）。
    ///
    /// ★ 第 ① 级必须做**退化防护**：三点共线时最小二乘照样给你一个"完美拟合这三个点"的矩阵，
    ///   但它对线外方向的预测是纯噪声。所以除了矩阵本身合法，还要检查
    ///   "像素域两列不共线" **且** "世界域的三个点不共线" —— 后者是个真坑：
    ///   九点若按逐行序（1,2,3）取前三点，世界域恰好共线，一定会踩到它。
    /// </summary>
    public sealed class ProgressivePixelPredictor
    {
        /// <summary>解临时 H 需要的最少点数（仿射 6 个未知量 → 3 点，实战同样需要 3 点起步）。</summary>
        public const int MinPointsForSolve = 3;

        /// <summary>
        /// 解出来的 H 的**两列夹角** sin 下限（低于它认为这个 H 的像空间被压平，弃用）。
        ///
        /// ★★ 这个名字有历史包袱，实际含义必须说清 —— 而且**曾经写错两次**，值得记下来：
        ///   ① 最初注释写成"像素域退化检查"（名不副实）；
        ///   ② 2026-09-14 我读代码推出"它度量的是**世界域**平坦度、与像素域无关"，
        ///      写进注释与设计文档 —— **实测证明这句也是错的**。
        ///
        /// ★ 实测事实（见 <see cref="LastHColumnSin"/>，这里是记录不是推导）：
        ///   · 世界点固定（直角等腰）、只换像素点形状 ⇒ 该值 0.447 ↔ 1.000 ⇒ 它**依赖像素域**；
        ///   · 世界针形（平坦度 0.085）时，它给出的值恰好与该针的世界夹角同量级（0.287↔0.970）；
        ///   · 像素针形（平坦度 0.029）时，它仍给 0.447 ≫ 0.05 ⇒ 它**拦不住像素域退化**。
        ///
        /// ⇒ 它是一个把两个域的尺度 / 各向异性**混在一起**的量，
        ///   **既不是**世界域平坦度、**也不是**像素域共线度；因此它**不适合单独承担退化检查**。
        ///   ⇒ 它与 <see cref="MinWorldSin"/> 在常见输入上**功能重叠**且阈值更松（0.05 < 0.20），
        ///     保留它只为覆盖 4 点以上最小二乘解（H 的像空间可能被压平，而两个点集的夹角都不小）。
        ///
        /// ★ 两个域各自的退化检查由 <see cref="MinWorldSin"/> / <see cref="MinWorldFlatness"/>
        ///   与 <see cref="MinPixelSin"/> / <see cref="MinPixelFlatness"/> 承担（2026-09-14 补齐）。
        /// </summary>
        public const double MinColumnSin = 0.05;

        /// <summary>世界域位移向量夹角的 sin 下限（低于它认为三点共线，弃用该 H）。</summary>
        public const double MinWorldSin = 0.20;

        /// <summary>
        /// **像素域**位移向量夹角的 sin 下限 —— 与 <see cref="MinWorldSin"/> 对称。
        ///
        /// ★ 为什么必须单独立这一道（2026-09-14）：像素点**几乎**排成一条线时，
        ///   最小二乘照样解得出一个有限的 H，但它在"垂直于那条线"的方向上几乎不受约束、
        ///   预测纯属噪声；而 H 被误判为 `exact=true` 之后，下游会按"精确反投影"来用这个 ROI。
        ///   修前实测：像素点只差 4 px 就共线时，`exact=true`（<see cref="MinColumnSin"/> 给 0.447 放行），
        ///   即**假精确**。严格共线反而安全（求解直接失败）—— 危险的全是"几乎"。
        /// </summary>
        public const double MinPixelSin = 0.20;

        /// <summary>
        /// 点集**平坦度**的下限：主轴标准差之比 σ2/σ1（0 = 完全共线，1 = 各向同性）。
        ///
        /// ★★ 为什么光有 <see cref="MinWorldSin"/> / <see cref="MinPixelSin"/> 不够（2026-09-14 补）：
        ///   那两道用的是"**首点处**的夹角"，因此**依赖点的顺序**。实测同一组针形点集：
        ///     · 首点在**中点** → 夹角 163°，sin=0.287 > 0.20 → **通过**（而它实际平坦度只有 0.085）；
        ///     · 首点在**端点** → sin=0.148 → 拒绝。
        ///   同一组点换个顺序结论相反 —— 那不是判据，是巧合。
        ///   而"首点在中点"恰好对应**中心先采样**这种很常见的规划（第 1 点中心、第 2/3 点左右），
        ///   于是针形点集会被放行，解出一个在"垂直于针"的方向上纯属噪声的 H。
        ///
        /// ⇒ 补一道**顺序无关、旋转无关**的形状判据：对点集去心后取 2×2 二阶矩矩阵，
        ///   平坦度 = sqrt(λ_min / λ_max)。它与本项目"落点偏差第一判据 = 矩阵形状"同一套思路。
        ///   判定 = 夹角判据**且**平坦度判据**都通过**（加性收紧，不放松任何既有拦截）。
        /// </summary>
        public const double MinWorldFlatness = 0.20;

        /// <summary>像素域平坦度下限，与 <see cref="MinWorldFlatness"/> 对称（同一套标准）。</summary>
        public const double MinPixelFlatness = 0.20;

        /// <summary>ROI 半径放大裕量（在"覆盖整步位移"之上再放一点）。</summary>
        public double MarginFactor = 1.35;

        /// <summary>ROI 半径上限（px），防手滑给出一个"覆盖全图"的假局部搜索。</summary>
        public double MaxSearchRadiusPx = 600.0;

        /// <summary>预测点与上一点的位移，最多允许是"理论位移"的多少倍（超过即认为 H 疯了）。</summary>
        public double OutlierFactor = 3.0;

        private readonly List<Vec2> _pixels = new List<Vec2>();
        private readonly List<Vec2> _worlds = new List<Vec2>();
        private readonly List<double> _stepPx = new List<double>();
        private HomMat2D? _h;

        /// <summary>
        /// ★ 旋转采样模式：法兰 XY 不动、只转 U。
        ///
        /// 这时"目标世界位"逐点不变，平面模式的第 ①②级会算出"理论上原地不动"，
        /// 从而给出一个半径只有 30 px 的 ROI —— 而 Mark 实际上正沿着一个几十到几百像素的
        /// 圆弧移动，必然脱靶。所以旋转模式下改口径：
        ///   · 用<b>上一次的实测像素步距</b>外推（该走多远是量出来的，不是猜的）；
        ///   · 还没有实测步距时，用 <see cref="RotationSeedRadiusPx"/> 把整段圆弧先圈进来。
        /// </summary>
        public bool RotationMode;

        /// <summary>首段圆弧的兜底 ROI 半径（px）：还没有实测步距时用它，宁可圈大也别脱靶。</summary>
        public double RotationSeedRadiusPx = 250.0;

        public ProgressivePixelPredictor(double mmPerPixelHint = 0.0)
        {
            MmPerPixelHint = mmPerPixelHint;
        }

        /// <summary>外部已知的像素尺度（mm/px）；&lt;=0 表示未知。</summary>
        public double MmPerPixelHint { get; set; }

        public int Count
        {
            get { return _pixels.Count; }
        }

        public bool HasExactSolution
        {
            get { return _h.HasValue; }
        }

        public HomMat2D? Solution
        {
            get { return _h; }
        }

        /// <summary>实测像素尺度（mm/px，取所有点对的最小比值 = 最保守）；未知为 0。</summary>
        public double MeasuredMmPerPixel { get; private set; }

        /// <summary>本次预测的来源说明（写进日志，便于复盘"这点是靠什么找到的"）。</summary>
        public string LastSource { get; private set; }

        /// <summary>
        /// 临时 H 被**弃用**时留下的原因：世界域共线 / 像素域共线 / 点数不足 / 求解失败；
        /// H 被正常采用时为 null。
        ///
        /// ★ 为什么必须留这句话：两道共线防护**从外部看结果完全一样** ——
        ///   都表现为"退到放大 ROI、exact=false"。但处置办法完全不同：
        ///   · 世界域共线 = **采样顺序**问题（九点按逐行序取前三点必踩，重排即可）；
        ///   · 像素域共线 = **检测**问题（mark 在图上排成一条线，得改工位/视角）。
        ///   不留痕迹就等于"卡住时说不清卡在哪"。
        /// </summary>
        public string HRejectReason { get; private set; }

        /// <summary>
        /// 最近一次解出的 H 的**两列夹角 sin**（= <see cref="MinColumnSin"/> 那道判据实际看到的数）。
        /// 没解出 H 时为 **NaN**（★ 未测到的量一律 NaN，绝不留 0 冒充真值）。
        ///
        /// ★ 暴露它的理由：这个量到底在度量什么，**只能靠实测确定，不能靠读代码猜**。
        ///   2026-09-14 实测（这里是记录，不是推导）：
        ///     · 世界点固定（直角等腰）、只换像素点形状 → 该值 **1.000 → 0.447** ⇒ 它**不是**世界域量；
        ///     · 像素点退化成针形（平坦度 0.029）时 → 该值仍给 **0.447 ≫ 0.05** ⇒ 它**拦不住像素域退化**。
        ///   ⇒ 结论：它是把两个域的尺度/各向异性**混在一起**的量，
        ///     **不适合单独承担"像素域退化检查"**（那句旧注释就是这么写的，是错的）。
        ///     真正管像素域的是 <see cref="MinPixelSin"/> + <see cref="MinPixelFlatness"/>。
        /// </summary>
        public double LastHColumnSin { get; private set; }

        public void Reset()
        {
            _pixels.Clear();
            _worlds.Clear();
            _stepPx.Clear();
            _h = null;
            MeasuredMmPerPixel = 0.0;
            LastSource = null;
            HRejectReason = null;
            LastHColumnSin = double.NaN;
        }

        /// <summary>喂入一个**已成功**的观测（失败点不要喂，否则会把噪声灌进临时 H）。</summary>
        public void Observe(Vec2 pixel, Vec2 world)
        {
            if (!pixel.IsFinite || !world.IsFinite)
            {
                return;
            }

            if (_pixels.Count > 0)
            {
                _stepPx.Add((pixel - _pixels[_pixels.Count - 1]).Length);
            }

            _pixels.Add(pixel);
            _worlds.Add(world);

            UpdateScale();
            TrySolve();
        }

        /// <summary>
        /// 预测下一个目标位在图像上的落点。
        /// </summary>
        /// <param name="worldTarget">下一个采样点的目标世界位（法兰命令位域）。</param>
        /// <param name="predicted">预测像素（X = col，Y = row）。</param>
        /// <param name="searchRadiusPx">建议的 ROI 搜索半径：精确预测时返回 0（沿用 MarkSpec 自带半径）。</param>
        /// <param name="exact">true = 由 H 反投影得到的精确预测；false = 只是"圈个大概范围"。</param>
        public bool TryPredict(Vec2 worldTarget, out Vec2 predicted, out double searchRadiusPx, out bool exact)
        {
            predicted = Vec2.Zero;
            searchRadiusPx = 0.0;
            exact = false;
            LastSource = null;

            if (!worldTarget.IsFinite || _pixels.Count == 0)
            {
                return false;
            }

            Vec2 lastPixel = _pixels[_pixels.Count - 1];
            Vec2 lastWorld = _worlds[_worlds.Count - 1];

            // ── ⓪ 旋转模式：世界位不动，只能靠"实测像素步距"外推 ──
            if (RotationMode)
            {
                double step = _stepPx.Count > 0 ? _stepPx[_stepPx.Count - 1] : 0.0;
                double r;
                if (step > 0.0)
                {
                    r = Math.Max(step * 2.0, 40.0);
                }
                else
                {
                    r = RotationSeedRadiusPx;
                }

                if (r > MaxSearchRadiusPx)
                {
                    r = MaxSearchRadiusPx;
                }

                predicted = lastPixel;
                searchRadiusPx = r;
                exact = false;
                LastSource = step > 0.0
                    ? string.Format(CultureInfo.InvariantCulture,
                        "旋转模式按实测步距外推（上一步 {0:F0} px → 半径 {1:F0} px）", step, r)
                    : string.Format(CultureInfo.InvariantCulture,
                        "旋转模式首段兜底半径 {0:F0} px（尚无实测步距）", r);
                return true;
            }

            double scale = MeasuredMmPerPixel > 0.0
                ? MeasuredMmPerPixel
                : (MmPerPixelHint > 0.0 ? MmPerPixelHint : 0.0);
            double moveMm = (worldTarget - lastWorld).Length;
            double theoreticalPx = scale > 0.0 ? moveMm / scale : 0.0;

            // ── ① 精确预测（临时 H）──
            if (_h.HasValue)
            {
                try
                {
                    Vec2 p = _h.Value.Invert().Transform(worldTarget);
                    if (p.IsFinite)
                    {
                        // 疯矩阵防护：预测位移远超「理论位移」就说明这个 H 在外推区不可信。
                        //
                        // ★★ 但必须说清：它在**默认参数下是关着的**（2026-09-14 实测核算）——
                        //    别把它当成一道有效防线（那正是"判据名不副实"的另一种形态）。
                        //    theoreticalPx = |Δ世界| / MeasuredMmPerPixel，而后者取的是点对 |ΔW|/|ΔPx| 的
                        //    【最小值】（最保守）⇒ theoreticalPx 是"真实像素位移"的**上界**，
                        //    于是 drift ≤ theoreticalPx，而 limit = theoreticalPx×3 + 50 ≥ drift **恒成立**。
                        //    实测：3 点精确拟合时 drift ≡ theoreticalPx（连外推到 200 mm 都是 2002.5 vs 2002.5）；
                        //    另跑数值搜索 20 万组随机数据 × 32 个目标（各向异性/噪声/线外目标）：**0 次命中**。
                        //  ⇒ 它真正的用武之地是**外来 H**（复用上一次标定、或从上层回流进来的 H）——
                        //    那种 H 与本轮实测点不自洽，drift 才可能爆掉。**本类目前没有喂入外来 H 的入口**，
                        //    所以它今天等价于关闭：自检里因此留了一条「零覆盖记录（结构不可达）」，
                        //    并用产品自己的公开旋钮 `OutlierFactor = 0` 证明这段代码本身是活的。
                        double drift = (p - lastPixel).Length;
                        double limit = theoreticalPx > 0.0
                            ? theoreticalPx * OutlierFactor + 50.0
                            : double.MaxValue;

                        if (drift <= limit)
                        {
                            predicted = p;
                            searchRadiusPx = 0.0;
                            exact = true;
                            LastSource = string.Format(CultureInfo.InvariantCulture,
                                "反投影(H，{0} 点，预测位移 {1:F1} px)", _pixels.Count, drift);
                            return true;
                        }

                        LastSource = string.Format(CultureInfo.InvariantCulture,
                            "临时 H 外推异常（预测位移 {0:F1} px > 上限 {1:F1} px）→ 降级为放大 ROI",
                            drift, limit);
                    }
                }
                catch (InvalidOperationException)
                {
                    LastSource = "临时 H 不可逆（det≈0）→ 降级为放大 ROI";
                }
            }

            // ── ② 放大 ROI（能覆盖一整步位移，方向未知所以不猜方向）──
            if (scale > 0.0 && moveMm > 0.0)
            {
                double r = moveMm / scale * MarginFactor;
                if (r < 30.0)
                {
                    r = 30.0;
                }

                if (r > MaxSearchRadiusPx)
                {
                    r = MaxSearchRadiusPx;
                }

                predicted = lastPixel;
                searchRadiusPx = r;
                exact = false;
                if (LastSource == null)
                {
                    // ★ 有弃用原因时把它一并说出来：否则"为什么没用上精确预测"在外部无从判断，
                    //   而两道共线防护的处置办法完全不同（重排采样 vs 改检测/视角）。
                    string why = HRejectReason == null
                        ? string.Empty
                        : HRejectReason + "；";
                    LastSource = string.Format(CultureInfo.InvariantCulture,
                        "{0}按实测像素尺度放大 ROI（{1} 点，尺度 {2:F5} mm/px → 半径 {3:F0} px）",
                        why, _pixels.Count, scale, r);
                }

                return true;
            }

            // ── ③ 什么都不够 ──
            LastSource = "无先验 → 全图搜索";
            return false;
        }

        /// <summary>
        /// 实测像素尺度：取所有点对 |Δ世界| / |Δ像素| 的**最小值**。
        /// ★ 取最小值是为了保守：外推出的像素位移 = |ΔW| / 该比值，比值越小位移越大、半径越安全。
        /// </summary>
        private void UpdateScale()
        {
            double best = 0.0;
            for (int i = 1; i < _pixels.Count; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    double dPx = (_pixels[i] - _pixels[j]).Length;
                    double dMm = (_worlds[i] - _worlds[j]).Length;
                    if (dPx < 1e-6 || dMm < 1e-6)
                    {
                        continue;
                    }

                    double ratio = dMm / dPx;
                    if (best <= 0.0 || ratio < best)
                    {
                        best = ratio;
                    }
                }
            }

            if (best > 0.0)
            {
                MeasuredMmPerPixel = best;
            }
        }

        /// <summary>
        /// 尝试用当前已有的点解一个临时 H（像素 → 世界）。失败就保持"没有 H"，
        /// 让预测退到第 ②/③ 级 —— 退让比"用一个错的 H 硬预测"安全得多。
        /// </summary>
        private void TrySolve()
        {
            HRejectReason = null;
            LastHColumnSin = double.NaN;

            if (_pixels.Count < MinPointsForSolve)
            {
                _h = null;
                HRejectReason = string.Format(CultureInfo.InvariantCulture,
                    "点数不足（{0} < {1}）→ 本轮不解临时 H", _pixels.Count, MinPointsForSolve);
                return;
            }

            // 世界域退化检查（逐行序取前三点必踩"共线"这个坑；"针形"另有坑，见下面 whyWorld）
            string whyWorld;
            if (!HasNonCollinear(_worlds, MinWorldSin, MinWorldFlatness, out whyWorld))
            {
                _h = null;
                HRejectReason = "世界域点集退化（" + whyWorld + "）→ 弃用临时 H";
                return;
            }

            // ★ 像素域退化检查（与世界域对称的另一侧输入退化）。
            //   放在解 H 之前：它是**输入**的问题，与用什么方法解无关。
            //   ★★ 为什么不能靠 MinColumnSin 顶 —— 这里曾写错，留样本：
            //      原话是「那道判据度量的是世界域的平坦度，对像素点几乎共线完全不敏感」。
            //      ★ 2026-09-14 实测证明**前半句是错的**：世界点固定、只换像素形状时，
            //        该值 1.000 ↔ 0.447（见 LastHColumnSin 的注释）⇒ 它并非世界域量。
            //      但**结论仍然成立**，理由换了：像素针形（平坦度 0.029）时它给
            //        0.447 ≫ MinColumnSin(0.05) ⇒ 它**拦不住像素域退化**。
            //      ⇒ 修前这类输入会得到 exact=true（假精确），所以必须有这道输入侧检查。
            string whyPixel;
            if (!HasNonCollinear(_pixels, MinPixelSin, MinPixelFlatness, out whyPixel))
            {
                _h = null;
                HRejectReason = "像素域点集退化（" + whyPixel + "）→ 弃用临时 H";
                return;
            }

            var samples = new List<CalibSample>(_pixels.Count);
            for (int i = 0; i < _pixels.Count; i++)
            {
                samples.Add(new CalibSample
                {
                    Index = i + 1,
                    State = CalibSampleState.Ok,
                    Pixel = _pixels[i],
                    HasPixel = true,
                    FeedbackXy = _worlds[i]
                });
            }

            NinePointResult solved = NinePointSolver.Solve(samples, null);
            if (!solved.Success || !solved.H.IsFinite)
            {
                _h = null;
                HRejectReason = "最小二乘求解失败或解出的矩阵非有限 → 弃用临时 H";
                return;
            }

            // H 像空间是否被压平：两列的夹角 sin 太小 ⇒ 这个 H 只在那条线附近可信
            // ★★ 它是把两个域的尺度/各向异性**混在一起**的量（实测见 LastHColumnSin 注释）：
            //   世界点固定只换像素形状 ⇒ 它会变；像素退化成针形 ⇒ 它仍给 0.447 放行。
            //   所以它既不是世界域量、也拦不住像素域退化，只是一道"解出来之后"的弱兜底。
            HomMat2D h = solved.H;
            double n1 = Math.Sqrt(h.H11 * h.H11 + h.H21 * h.H21);
            double n2 = Math.Sqrt(h.H12 * h.H12 + h.H22 * h.H22);
            double denom = n1 * n2;
            double sin = denom > 1e-15 ? Math.Abs(h.Det) / denom : 0.0;
            LastHColumnSin = sin;   // ★ 记下这道判据实际看到的数（诊断用）
            if (sin < MinColumnSin)
            {
                _h = null;
                HRejectReason = string.Format(CultureInfo.InvariantCulture,
                    "解出的 H 像空间被压平（两列夹角 sin={0:F3} < {1:F2}）→ 弃用临时 H",
                    sin, MinColumnSin);
                return;
            }

            _h = h;
        }

        /// <summary>
        /// 点集是否"足够二维"——**两道判据都要过**：
        ///   ① 以首点为基准，其余位移向量两两夹角的**最大** sin（"有没有张开"）；
        ///   ② 点集的**平坦度** σ2/σ1（"整体是不是一条线"）。
        ///
        /// ★★ 为什么必须两道都要：判据 ① 依赖点的**顺序**（首点在针形点集的中点 vs 端点，
        ///   结论相反：0.287 通过 / 0.148 拒绝），判据 ② 顺序无关。实测针形点集
        ///   （中心先采样规划的第 1/2/3 点）会被 ① 放行而 ② 拦下。
        ///   加性是刻意的：只收紧、不放松任何既有拦截。
        /// </summary>
        /// <param name="why">被拒时给出人话原因（说明是"没张开"还是"整体是条线"，并带上数值）。</param>
        private static bool HasNonCollinear(List<Vec2> points, double minSin, double minFlatness, out string why)
        {
            why = null;

            if (points.Count < MinPointsForSolve)
            {
                return true;      // 点数不够时不由这一道管（由 MinPointsForSolve 那道报）
            }

            Vec2 a = points[0];
            double bestSin = 0.0;
            for (int i = 1; i < points.Count; i++)
            {
                Vec2 v1 = points[i] - a;
                if (v1.Length < 1e-9)
                {
                    continue;
                }

                for (int j = i + 1; j < points.Count; j++)
                {
                    Vec2 v2 = points[j] - a;
                    if (v2.Length < 1e-9)
                    {
                        continue;
                    }

                    double s = Math.Abs(v1.X * v2.Y - v1.Y * v2.X) / (v1.Length * v2.Length);
                    if (s > bestSin)
                    {
                        bestSin = s;
                    }
                }
            }

            if (bestSin <= minSin)
            {
                why = string.Format(CultureInfo.InvariantCulture,
                    "以首点为基准的夹角 sin={0:F3} ≤ {1:F2}（点集没张开；逐行序取前三点必踩）",
                    bestSin, minSin);
                return false;
            }

            double flatness = Flatness(points);
            if (flatness <= minFlatness)
            {
                why = string.Format(CultureInfo.InvariantCulture,
                    "点集平坦度 σ2/σ1={0:F3} ≤ {1:F2}（整体近似一条线，与点的顺序无关）",
                    flatness, minFlatness);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 点集平坦度 = sqrt(λ_min / λ_max)，λ 是去心后 2×2 二阶矩矩阵的特征值。
        /// 0 = 完全共线，1 = 各向同性。**与点的顺序、坐标系旋转都无关。**
        /// </summary>
        private static double Flatness(List<Vec2> points)
        {
            int n = points.Count;
            if (n < MinPointsForSolve)
            {
                return 1.0;
            }

            double cx = 0.0;
            double cy = 0.0;
            for (int i = 0; i < n; i++)
            {
                cx += points[i].X;
                cy += points[i].Y;
            }

            cx /= n;
            cy /= n;

            double sxx = 0.0;
            double syy = 0.0;
            double sxy = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dx = points[i].X - cx;
                double dy = points[i].Y - cy;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
            }

            sxx /= n;
            syy /= n;
            sxy /= n;

            double trace = sxx + syy;
            if (trace <= 1e-30)
            {
                return 0.0;
            }

            double det = sxx * syy - sxy * sxy;
            double disc = trace * trace - 4.0 * det;
            if (disc < 0.0)
            {
                disc = 0.0;
            }

            double lambdaMax = (trace + Math.Sqrt(disc)) / 2.0;
            double lambdaMin = (trace - Math.Sqrt(disc)) / 2.0;
            if (lambdaMax <= 1e-30 || lambdaMin <= 0.0)
            {
                return 0.0;
            }

            return Math.Sqrt(lambdaMin / lambdaMax);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Predictor({0} 点，{1}，尺度 {2:F5} mm/px)", _pixels.Count,
                _h.HasValue ? "有临时 H" : "无 H", MeasuredMmPerPixel);
        }
    }
}
