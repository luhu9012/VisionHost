using System;
using System.Collections.Generic;
using System.Globalization;

namespace VisualCalibTool.Domain
{
    /// <summary>
    /// Mark 明暗（极性）：标记相对底色是亮还是暗。
    ///
    /// ★ 实现语义是<b>整图反色</b>：选「亮底上的暗标记」时，提取器先把图反色、
    ///   再按"暗底上的亮标记"那条主路径走 —— 反色保几何（像素位置不变），
    ///   阈值带/亚像素/坐标全都不用改，也不产生第二套并行逻辑。
    /// ★ <b>模板匹配不受极性影响</b>：模板与搜索图必须处在同一个灰度世界（训练图什么样，
    ///   搜索图就什么样），反色反而会让 find_shape_model 配不上。
    /// </summary>
    public enum MarkPolarity
    {
        /// <summary>暗底上的亮标记（默认 —— 圆点主路径的出厂世界）。</summary>
        BrightMarkOnDarkBackground = 0,

        /// <summary>亮底上的暗标记（提取前整图反色，只作用于阈值类路径）。</summary>
        DarkMarkOnBrightBackground = 1
    }

    /// <summary>
    /// 特征提取算子参数（圆 Mark / 十字 Mark / 模板 共用）。
    ///
    /// ★ 默认值<b>逐条对等</b>主项目 <c>Grayson.Vision.HalconWrapper.Calibration.FeatureExtractOptions</c>。
    ///   这不是"抄一个好看的数"——这些默认值是主项目在现场调出来的（阈值 100/255、圆度 0.7、
    ///   面积 80~999999、搜索半径 150、亚像素阈值 128；十字 暗≤90 / 亮≥160 / 面积≥60 / 宽高 ≥4）。
    ///   任何一项改了，嵌入模式下的识别行为就会和宿主不一致 —— 那是"能力倒退"而不是"改进"。
    ///
    /// 这里是纯数据（无 INotifyPropertyChanged）：通知属于视图层的事，
    /// 算法层拿到的应该是一份不可变快照（这样离线单测才可复现）。
    /// </summary>
    public sealed class FeatureExtractOptions
    {
        // ── 圆形 Mark ──
        public double ThresholdMin = 100;
        public double ThresholdMax = 255;
        public double MinCircularity = 0.7;
        public double MinArea = 80;
        public double MaxArea = 999999;
        public double SearchRadius = 150;
        public double SubPixThreshold = 128;

        // ── 十字 Mark ──
        public double CrossDarkThresholdMax = 90;
        public double CrossLightThresholdMin = 160;
        public double CrossMinArea = 60;
        public double CrossMaxArea = 99999999;
        public double CrossMinSize = 4;
        public double CrossMaxSize = 3000;

        // ── 模板匹配 ──
        public double TemplateMinScore = 0.6;
        public double TemplateAngleStart = -180;
        public double TemplateAngleEnd = 180;

        /// <summary>
        /// 参考 Mark 半径容差比例（主项目硬编码 0.4）。
        /// 半径偏离参考值超过该比例 → 判为伪特征（反光点/螺丝/字符）。换板换相机必须重置参考。
        /// </summary>
        public double ReferenceRadiusTolerance = 0.4;

        /// <summary>动态阈值兜底（抗光照不均）的灰度偏差门限（主项目硬编码 8）。</summary>
        public double DynThresholdDelta = 8.0;

        /// <summary>Mark 明暗（极性）。默认 = 暗底上的亮标记（出厂世界，与主项目行为对等）。</summary>
        public MarkPolarity MarkPolarity = MarkPolarity.BrightMarkOnDarkBackground;

        /// <summary>深拷贝（Options 会被会话快照带走，必须复制而不是引用）。</summary>
        public FeatureExtractOptions Clone()
        {
            return (FeatureExtractOptions)MemberwiseClone();
        }

        /// <summary>产出一份人类可读的参数摘要（写进会话与产物，便于回溯"当时用的什么参数"）。</summary>
        public string ToSummary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "circle[thr {0:F0}-{1:F0}, circ≥{2:F2}, area {3:F0}-{4:F0}, subpix {5:F0}] "
                + "cross[dark≤{6:F0}, light≥{7:F0}, area {8:F0}-{9:F0}, size {10:F0}-{11:F0}] "
                + "template[score≥{12:F2}, angle {13:F0}..{14:F0}] roiR {15:F0} polarity {16}",
                ThresholdMin, ThresholdMax, MinCircularity, MinArea, MaxArea, SubPixThreshold,
                CrossDarkThresholdMax, CrossLightThresholdMin, CrossMinArea, CrossMaxArea,
                CrossMinSize, CrossMaxSize, TemplateMinScore, TemplateAngleStart, TemplateAngleEnd,
                SearchRadius,
                MarkPolarity == MarkPolarity.DarkMarkOnBrightBackground ? "dark(反色)" : "bright");
        }
    }

    /// <summary>
    /// 单个候选特征。★ 存在的意义是让操作员<b>看见有哪些候选、各自质量如何</b>，
    /// 从而把"反光点/螺丝/字符"造成的干扰源主动调掉，而不是盲调参数或盲信结果。
    /// </summary>
    public sealed class MatchCandidateInfo
    {
        /// <summary>序号（从 1 开始，UI 直接用）。</summary>
        public int Index;

        /// <summary>是否最终被选中。</summary>
        public bool IsSelected;

        public double PixelX;
        public double PixelY;

        /// <summary>等效半径（px）：XLD 拟合半径，或区域面积换算 √(area/π)。</summary>
        public double Radius;

        /// <summary>圆度（0~1，区域级特征）。</summary>
        public double Circularity;

        public double Area;

        /// <summary>距期望位置的像素距离；无期望位置时为 NaN。</summary>
        public double DistanceToExpected = double.NaN;

        /// <summary>是否因参考半径不符被排除。</summary>
        public bool RejectedByRadius;

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}#{1} P({2:F1},{3:F1}) r{4:F1} 圆度{5:F2}{6}",
                IsSelected ? "★选中" : "  候选", Index, PixelX, PixelY, Radius, Circularity,
                RejectedByRadius ? " [半径不符已排除]" : string.Empty);
        }
    }

    /// <summary>
    /// 特征匹配质量报告（0~100 + 成分明细）。
    ///
    /// ★ 评分口径逐条对等主项目 <c>CalibrationService.ComposeCircleReport/ComposeCrossReport</c>：
    ///
    ///   圆 Mark：`100 × (0.35×圆度 + 0.25×半径一致性 + 0.20×唯一性 + 0.20×可预测性)`，走降级路径 ×0.85，clamp [1,99]；
    ///   十字    ：`100 × (0.7×(0.75 + 0.25×夹角分) + 0.3×唯一性)`；夹角容忍 30°。
    ///
    /// 语义分级：≥85 极佳 / 70~84 良好 / 55~69 可用 / &lt;55 偏弱 / 失败 = 0。
    /// 权重本身就说明了一个立场：<b>圆度权重最高</b>——形状对不上，位置再准也是错的特征。
    /// </summary>
    public sealed class FeatureMatchReport
    {
        public bool Success;
        public double Score;
        public string Verdict = "失败";
        public string Detail;

        /// <summary>通过几何筛选的候选总数（含后续被半径过滤的）。</summary>
        public int CandidateCount;

        public readonly List<MatchCandidateInfo> Candidates = new List<MatchCandidateInfo>();

        public double PixelX = double.NaN;
        public double PixelY = double.NaN;

        /// <summary>本次是否走了降级路径（动态阈值兜底 / 区域中心兜底 / 十字兜底）→ 报告打折。</summary>
        public bool UsedFallback;

        /// <summary>本次是全图降级搜索（局部 ROI 失败后重试）而非正常局部搜索。</summary>
        public bool UsedFullImageFallback;

        /// <summary>拟合出的圆半径（px）；十字/模板为 NaN。</summary>
        public double RadiusPx = double.NaN;

        /// <summary>十字的两臂夹角误差（度）；非十字为 NaN。</summary>
        public double CrossAngleDevDeg = double.NaN;

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1:F0}/100 · {2} 候选{3}{4}",
                Success ? "OK" : "FAIL", Score, Verdict, CandidateCount,
                UsedFallback ? " [降级]" : string.Empty);
        }
    }

    /// <summary>
    /// 叠加绘制原语种类。★ 全部用普通类型表达，视图模型因此完全不知道 HALCON 存在。
    /// 坐标一律 <b>(row, col)</b> —— 与 HALCON 一致，row = 图像 Y，col = 图像 X。
    /// </summary>
    public enum TraceShapeKind
    {
        Rectangle = 0,
        Circle = 1,
        Cross = 2,
        Polyline = 3,
        Text = 4
    }

    /// <summary>一条叠加绘制原语。</summary>
    public sealed class TraceShape
    {
        public TraceShapeKind Kind;

        /// <summary>行坐标数组。Rectangle = 2 个角点；Circle/Cross/Text = 1 个锚点；Polyline = N 个点。</summary>
        public double[] Rows;

        public double[] Cols;

        public double Radius;

        public string Text;
        public string Color = "white";
        public int LineWidth = 1;

        /// <summary>绘制层级（小的先画）。</summary>
        public int Layer;
    }

    /// <summary>
    /// 提取过程的"走一步画一步"轨迹（HDevelop 语义）。
    ///
    /// ★ 为什么必须在<b>没</b>有 HALCON 的类型里表达：
    ///   ① 设计文档要求"参数可视化调参" —— 用户拖滑块要看得到 ROI/阈值/候选/拟合圆/中心各是什么，
    ///      而不是只给一个坐标；② 这层一旦泄漏 HObject，视图模型就没法离线单测。
    ///
    /// ★ 有<b>点数预算</b>：阈值区域的轮廓点数可能上万，无脑画会让界面卡死。
    ///   超预算即截断并置 <see cref="Truncated"/>，让界面如实提示"过程图已截断"而不是假装完整。
    /// </summary>
    public sealed class ExtractionTrace
    {
        public readonly List<TraceShape> Shapes = new List<TraceShape>();

        /// <summary>点数预算（默认 20000，约等于超大区域轮廓的可见上限）。</summary>
        public int MaxPoints = 20000;

        public int UsedPoints;

        public bool Truncated;

        /// <summary>是否启用过程叠加（关掉可让采样期零开销）。</summary>
        public bool Enabled = true;

        public void Clear()
        {
            Shapes.Clear();
            UsedPoints = 0;
            Truncated = false;
        }

        public void AddRectangle(double row1, double col1, double row2, double col2,
            string color, int lineWidth, int layer = 10)
        {
            Add(new TraceShape
            {
                Kind = TraceShapeKind.Rectangle,
                Rows = new double[] { row1, row2 },
                Cols = new double[] { col1, col2 },
                Color = color,
                LineWidth = lineWidth,
                Layer = layer
            });
        }

        public void AddCircle(double row, double col, double radius, string color, int lineWidth, int layer = 40)
        {
            Add(new TraceShape
            {
                Kind = TraceShapeKind.Circle,
                Rows = new double[] { row },
                Cols = new double[] { col },
                Radius = radius,
                Color = color,
                LineWidth = lineWidth,
                Layer = layer
            });
        }

        public void AddCross(double row, double col, double size, string color, int lineWidth, int layer = 60)
        {
            Add(new TraceShape
            {
                Kind = TraceShapeKind.Cross,
                Rows = new double[] { row },
                Cols = new double[] { col },
                Radius = size,
                Color = color,
                LineWidth = lineWidth,
                Layer = layer
            });
        }

        public void AddPolyline(double[] rows, double[] cols, string color, int lineWidth,
            bool closed = false, int layer = 20)
        {
            if (rows == null || cols == null || rows.Length < 2 || rows.Length != cols.Length)
            {
                return;
            }

            Add(new TraceShape
            {
                Kind = TraceShapeKind.Polyline,
                Rows = rows,
                Cols = cols,
                Color = color,
                LineWidth = lineWidth,
                Layer = layer
            });
        }

        public void AddText(double row, double col, string text, string color, int layer = 80)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Add(new TraceShape
            {
                Kind = TraceShapeKind.Text,
                Rows = new double[] { row },
                Cols = new double[] { col },
                Text = text,
                Color = color,
                LineWidth = 1,
                Layer = layer
            });
        }

        private void Add(TraceShape shape)
        {
            if (!Enabled || shape == null)
            {
                return;
            }

            int cost = shape.Rows == null ? 1 : Math.Max(1, shape.Rows.Length);
            if (UsedPoints + cost > MaxPoints)
            {
                Truncated = true;
                return;
            }

            UsedPoints += cost;
            Shapes.Add(shape);
        }

        /// <summary>按层级升序返回（调用点直接顺序绘制即可）。</summary>
        public List<TraceShape> SortedByLayer()
        {
            var copy = new List<TraceShape>(Shapes);
            copy.Sort((a, b) => a.Layer.CompareTo(b.Layer));
            return copy;
        }
    }

    /// <summary>
    /// ★ 统一观测输出（设计文档 §6.3 规定的形态：`{ 索引, 像素 XY, 世界 XY, 质量分, 剔除原因 }`）。
    /// 上层算法<b>只见统计量</b>，不见"它是圆还是十字还是模板匹配出来的"。
    /// </summary>
    public sealed class CalibObservation
    {
        /// <summary>序号（从 1 开始）。</summary>
        public int Index;

        // ── 像素域 ──
        public Vec2 Pixel;
        public bool HasPixel;

        /// <summary>世界域（由采样时的<b>反馈位</b>给出）。九点的真值就落在这里。</summary>
        public Vec2 World;

        public bool HasWorld;

        /// <summary>质量分 0~1（便于和 <see cref="FeatureMatchReport.Score"/> 的 0~100 区分开）。</summary>
        public double Quality;

        /// <summary>0~100 原始匹配分（报告口径）。</summary>
        public double MatchScore;

        public string Verdict;

        /// <summary>非空 = 该点无效，内容即剔除原因（"毫米级原因"）。</summary>
        public string RejectReason;

        public int CandidateCount;
        public bool UsedFallback;
        public bool UsedFullImageFallback;

        /// <summary>本次提取用到的特征种类（写进会话，便于复盘"当时用的是圆还是模板"）。</summary>
        public FeatureKind FeatureKind;

        public string FramePath;

        public long ElapsedMs;

        public CalibError Error;

        /// <summary>过程轨迹（可为 null = 未启用）。</summary>
        public ExtractionTrace Trace;

        public FeatureMatchReport Report;

        public bool IsUsable
        {
            get
            {
                return string.IsNullOrEmpty(RejectReason) && HasPixel && HasWorld
                       && Pixel.IsFinite && World.IsFinite;
            }
        }

        /// <summary>质量分是否低于告警线（低分点应<b>标红</b>而不是静默丢弃）。</summary>
        public bool IsLowQuality(double warnBelow01 = 0.6)
        {
            return Quality < warnBelow01;
        }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(RejectReason))
            {
                return string.Format(CultureInfo.InvariantCulture, "#{0} 无效：{1}", Index, RejectReason);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "#{0} px({1:F2},{2:F2}) → world({3:F3},{4:F3}) 质量 {5:P0}",
                Index, Pixel.X, Pixel.Y, World.X, World.Y, Quality);
        }
    }

    /// <summary>单 Mark 描述（"我在找什么东西"）。与产物里的 <c>MarkSpecSnapshot</c> 同构。</summary>
    public sealed class MarkSpec
    {
        public FeatureKind Kind = FeatureKind.CircleMark;

        /// <summary>圆 Mark 参考半径（px）。&lt;=1 表示尚无参考（由首次成功识别自动记录）。</summary>
        public double ExpectedRadiusPx;

        /// <summary>局部 ROI 搜索半径（px）。&lt;=0 表示沿用 <see cref="Options"/>.SearchRadius。</summary>
        public double SearchRadiusPx;

        /// <summary>模板匹配用：模板库键。</summary>
        public string TemplateKey;

        public FeatureExtractOptions Options = new FeatureExtractOptions();

        /// <summary>人类可读描述（免责声明与日志用）。</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case FeatureKind.CircleMark:
                    return string.Format(CultureInfo.InvariantCulture, "圆 Mark（参考半径 {0}）",
                        ExpectedRadiusPx > 1.0 ? ExpectedRadiusPx.ToString("F1", CultureInfo.InvariantCulture) + " px" : "待自动记录");
                case FeatureKind.CrossMark:
                    return "十字 Mark（骨架 + 直线对求交）";
                case FeatureKind.TemplateMatch:
                    return "模板匹配（模板 " + (string.IsNullOrEmpty(TemplateKey) ? "未指定" : TemplateKey) + "）";
                default:
                    return Kind.ToString();
            }
        }

        public MarkSpec Clone()
        {
            var c = new MarkSpec
            {
                Kind = Kind,
                ExpectedRadiusPx = ExpectedRadiusPx,
                SearchRadiusPx = SearchRadiusPx,
                TemplateKey = TemplateKey,
                Options = Options == null ? new FeatureExtractOptions() : Options.Clone()
            };
            return c;
        }

        /// <summary>实际生效的 ROI 搜索半径。</summary>
        public double EffectiveSearchRadius
        {
            get
            {
                if (SearchRadiusPx > 0.0)
                {
                    return SearchRadiusPx;
                }

                return Options == null ? 150.0 : Options.SearchRadius;
            }
        }
    }

    /// <summary>模板种类。</summary>
    public enum TemplateModelKind
    {
        /// <summary>形状模板（create_shape_model / find_shape_model）——对光照与反光最稳，首选。</summary>
        Shape = 0,

        /// <summary>NCC 模板（create_ncc_model / find_ncc_model）——纹理目标更合适。</summary>
        Ncc = 1
    }

    /// <summary>
    /// 模板描述（工具<b>自建</b>模板库的条目）。
    /// ★ 独立模式下完全自持；嵌入模式下若宿主注入了外部模板库，可优先复用宿主全局模板。
    /// </summary>
    public sealed class TemplateSpec
    {
        public string Key;
        public TemplateModelKind ModelKind = TemplateModelKind.Shape;

        /// <summary>示教 ROI（图像坐标，闭区间）。</summary>
        public double Row1, Col1, Row2, Col2;

        /// <summary>模板参考点（ROI 中心或匹配置信中心）在世界/图像上的相对锚定位置。</summary>
        public double RefRow, RefCol;

        public double MinScore = 0.6;
        public double AngleStartDeg = -180;
        public double AngleExtentDeg = 360;

        /// <summary>训练用图留档路径（复盘"模板是从哪张图教出来的"）。</summary>
        public string SourceFramePath;

        /// <summary>训练质量（形状模板的轮廓点数 / NCC 的纹理对比度），仅供界面提示。</summary>
        public double TrainQuality;

        public string Note;

        public double WidthPx
        {
            get { return Math.Abs(Col2 - Col1); }
        }

        public double HeightPx
        {
            get { return Math.Abs(Row2 - Row1); }
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} [{1}] ROI({2:F0},{3:F0})-({4:F0},{5:F0}) {6:F0}x{7:F0}px score≥{8:F2}",
                string.IsNullOrEmpty(Key) ? "(未命名模板)" : Key, ModelKind,
                Row1, Col1, Row2, Col2, WidthPx, HeightPx, MinScore);
        }
    }
}
