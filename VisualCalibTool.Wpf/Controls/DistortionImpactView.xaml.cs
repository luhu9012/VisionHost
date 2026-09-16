using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Controls
{
    /// <summary>
    /// 「去畸变后会好多少」的一屏结论（决策 3 的界面出口）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 三条设计原则（都对应真实的判断失误，改之前先想清楚）
    /// ══════════════════════════════════════════════════════════════════
    /// ① <b>先给毫米，再给像素</b>：决策 3 问的是"落点会偏多少、值不值得处理"，
    ///    毫米才是能直接和工艺公差比的量；像素只是给排查用的。
    /// ② <b>必须画出"越往边角越偏"这条曲线</b>：只报一个最大值，看的人会以为全画面都偏这么多。
    ///    畸变是径向的，中心几乎不动、边角最狠 —— 这条曲线的形状本身就是证据。
    /// ③ <b>阈值线必须画在图上</b>：0.10 mm（值得处理）与 0.02 mm（可忽略）两条线一画，
    ///    "要不要改消费语义"就不用再开会讨论了，看图。
    ///
    /// ★ 没测到的一律显示「—」而不是 0 或 NaN：
    ///   0 会被读成"量过、没影响"，NaN 直接印在界面上更是灾难（本项目都改过一次了）。
    /// </summary>
    public partial class DistortionImpactView : UserControl
    {
        private const double PadLeft = 48.0;
        private const double PadRight = 12.0;
        private const double PadTop = 12.0;
        private const double PadBottom = 26.0;

        private DistortionImpactAssessment _data;

        public DistortionImpactView()
        {
            InitializeComponent();
            ShowEmpty("还没量过：跑一次内参链（拿标定板当尺子），或者九点与内参都齐了做一次口径对比。");
        }

        /// <summary>
        /// 把一次量化结果画出来。传 null 或"没测到"的结果 = 显示空态与原因。
        /// ★ 视图模型侧直接调这个方法（数据是一次性的快照，不是一直在变的状态，
        ///   做成属性绑定反而要处理一堆空值分支）。
        /// </summary>
        public void SetAssessment(DistortionImpactAssessment assessment)
        {
            _data = assessment;
            Render();
        }

        /// <summary>清空。</summary>
        public void Clear()
        {
            SetAssessment(null);
        }

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawChart();
        }

        // ────────────────────────────────────────────────────────── 渲染

        private void ShowEmpty(string reason)
        {
            VerdictText.Text = reason;
            VerdictText.Style = (Style)TryFindResource("VctTextStyle") ?? VerdictText.Style;
            KpiGrid.Children.Clear();
            KpiGrid.RowDefinitions.Clear();
            ChartCanvas.Children.Clear();
            AxisText.Text = string.Empty;
        }

        private void Render()
        {
            if (_data == null)
            {
                ShowEmpty("还没量过：跑一次内参链（拿标定板当尺子），或者九点与内参都齐了做一次口径对比。");
                return;
            }

            if (!_data.Measured)
            {
                ShowEmpty(string.IsNullOrEmpty(_data.Reason)
                    ? "这次没量出畸变影响量。"
                    : _data.Reason);
                return;
            }

            VerdictText.Text = string.IsNullOrEmpty(_data.Verdict)
                ? "量出来了，但没有给出建议。"
                : _data.Verdict;
            VerdictText.Style = PickVerdictStyle();

            FillKpis();
            DrawChart();
        }

        private Style PickVerdictStyle()
        {
            // ★ 结论的颜色只由"最大位移"决定，不掺别的判断：
            //   看的人第一眼要的是"要不要管"，不是"这套数据有多复杂"。
            double mm = _data.MaxShiftMm;
            if (double.IsNaN(mm))
            {
                return (Style)TryFindResource("VctTextStyle") ?? VerdictText.Style;
            }

            if (mm >= DistortionImpactAnalyzer.NotableShiftMm)
            {
                return (Style)TryFindResource("VctWarnTextStyle") ?? VerdictText.Style;
            }

            if (mm < DistortionImpactAnalyzer.NegligibleShiftMm)
            {
                return (Style)TryFindResource("VctOkTextStyle") ?? VerdictText.Style;
            }

            return (Style)TryFindResource("VctTextStyle") ?? VerdictText.Style;
        }

        private void FillKpis()
        {
            KpiGrid.Children.Clear();
            KpiGrid.RowDefinitions.Clear();
            KpiGrid.ColumnDefinitions.Clear();

            KpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            KpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;

            // ① 最偏多少（毫米）—— 决策 3 的主语
            AddKpi(row, 0, "最偏多少", Fmt(_data.MaxShiftMm, "F3", " mm"));
            // ② 合多少像素 —— 排查用
            AddKpi(row, 1, "合多少像素", Fmt(_data.MaxShiftPx, "F2", " px"));
            row++;

            // ③ 覆盖范围：不知道覆盖到哪，"最偏多少"就没有意义
            AddKpi(row, 0, "覆盖半径", Fmt(_data.WorkRadiusMm, "F1", " mm"));
            AddKpi(row, 1, "折合像素", Fmt(_data.WorstRadiusPx, "F0", " px"));
            row++;

            // ④ 标度（像素→毫米）：让"毫米"这个数可被复核
            AddKpi(row, 0, "标度", Fmt(_data.ScaleMmPerPx, "F5", " mm/px"));
            AddKpi(row, 1, "数据来路", SourceText(_data.Source));
            row++;

            // ⑤ 换口径的收益（只有九点那条路才有）
            if (!double.IsNaN(_data.LooRmsOnRawMm) && !double.IsNaN(_data.LooRmsOnUndistortedMm))
            {
                AddKpi(row, 0, "残差（现在）", Fmt(_data.LooRmsOnRawMm, "F4", " mm"));
                AddKpi(row, 1, "残差（换口径）", Fmt(_data.LooRmsOnUndistortedMm, "F4", " mm"));
                row++;

                double pct = _data.LooImprovementPct;
                AddKpi(row, 0, "换口径能好多少",
                    double.IsNaN(pct) ? "—" : pct.ToString("F1", CultureInfo.InvariantCulture) + " %");
                AddKpi(row, 1, "矩阵形状",
                    Fmt(_data.SigmaRatioOnRaw, "F5", string.Empty)
                    + " → " + Fmt(_data.SigmaRatioOnUndistorted, "F5", string.Empty));
                row++;
            }

            // 行高：每格两行（标题 + 数值）
            for (int i = 0; i < row; i++)
            {
                KpiGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
        }

        private void AddKpi(int row, int col, string caption, string value)
        {
            while (KpiGrid.RowDefinitions.Count <= row)
            {
                KpiGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var box = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

            var cap = new TextBlock
            {
                Text = caption,
                Style = (Style)TryFindResource("VctCaptionStyle")
            };

            var val = new TextBlock
            {
                Text = value,
                Style = (Style)TryFindResource("VctMonoTextStyle"),
                Margin = new Thickness(0, 2, 0, 0)
            };

            box.Children.Add(cap);
            box.Children.Add(val);

            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            KpiGrid.Children.Add(box);
        }

        // ────────────────────────────────────────────────────────── 曲线

        private void DrawChart()
        {
            ChartCanvas.Children.Clear();

            if (_data == null || !_data.Measured)
            {
                AxisText.Text = string.Empty;
                return;
            }

            double[] rx = _data.CurveRadiusPx;
            double[] mm = _data.CurveShiftMm;
            if (rx == null || mm == null || rx.Length < 2 || rx.Length != mm.Length)
            {
                AxisText.Text = "这次没有留下随半径变化的曲线（只有最大值）。";
                return;
            }

            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w < 80.0 || h < 80.0)
            {
                return;
            }

            double plotW = w - PadLeft - PadRight;
            double plotH = h - PadTop - PadBottom;
            if (plotW <= 20.0 || plotH <= 20.0)
            {
                return;
            }

            double maxR = 0.0;
            double maxMm = 0.0;
            for (int i = 0; i < rx.Length; i++)
            {
                if (rx[i] > maxR) { maxR = rx[i]; }
                if (mm[i] > maxMm) { maxMm = mm[i]; }
            }

            if (maxR <= 1e-9)
            {
                AxisText.Text = string.Empty;
                return;
            }

            // ★ Y 轴上界至少画到"值得处理"那条线：
            //   否则畸变很小的时候曲线贴着底，看的人分不清"确实小"还是"图没画出来"。
            double yTop = maxMm;
            double floor = DistortionImpactAnalyzer.NotableShiftMm * 1.15;
            if (yTop < floor)
            {
                yTop = floor;
            }

            Func<double, double> px = delegate (double r) { return PadLeft + plotW * (r / maxR); };
            Func<double, double> py = delegate (double v) { return PadTop + plotH * (1.0 - v / yTop); };

            // ── 坐标轴 ──
            AddLine(PadLeft, PadTop, PadLeft, PadTop + plotH, "VctBorderBrush", 1.0, null);
            AddLine(PadLeft, PadTop + plotH, PadLeft + plotW, PadTop + plotH, "VctBorderBrush", 1.0, null);

            // ── 两条阈值线（决策 3 的判据，画出来就不用再开会讨论）──
            AddThreshold(py, PadLeft, plotW, DistortionImpactAnalyzer.NotableShiftMm,
                "VctWarnColor", "值得处理 " + DistortionImpactAnalyzer.NotableShiftMm.ToString("F2", CultureInfo.InvariantCulture) + " mm");
            if (yTop > DistortionImpactAnalyzer.NegligibleShiftMm * 2.0)
            {
                AddThreshold(py, PadLeft, plotW, DistortionImpactAnalyzer.NegligibleShiftMm,
                    "VctOkColor", "可忽略 " + DistortionImpactAnalyzer.NegligibleShiftMm.ToString("F2", CultureInfo.InvariantCulture) + " mm");
            }

            // ── 曲线（先铺一层半透明面积，再描线）──
            var pts = new PointCollection();
            for (int i = 0; i < rx.Length; i++)
            {
                pts.Add(new Point(px(rx[i]), py(mm[i])));
            }

            var areaPts = new PointCollection();
            for (int i = 0; i < rx.Length; i++)
            {
                areaPts.Add(new Point(px(rx[i]), py(mm[i])));
            }
            areaPts.Add(new Point(px(rx[rx.Length - 1]), py(0.0)));
            areaPts.Add(new Point(px(rx[0]), py(0.0)));

            var area = new Polygon
            {
                Points = areaPts,
                Fill = AccentBrush(0.18),
                Stroke = null
            };
            ChartCanvas.Children.Add(area);

            var line = new Polyline
            {
                Points = pts,
                Stroke = (Brush)TryFindResource("VctAccentBrush") ?? Brushes.DodgerBlue,
                StrokeThickness = 2.0,
                StrokeLineJoin = PenLineJoin.Round
            };
            ChartCanvas.Children.Add(line);

            // ── 刻度 ──
            AddLabel("0", PadLeft - 4.0, PadTop + plotH + 4.0, true);
            AddLabel(maxR.ToString("F0", CultureInfo.InvariantCulture) + " px",
                PadLeft + plotW - 30.0, PadTop + plotH + 4.0, false);
            AddLabel(yTop.ToString("F3", CultureInfo.InvariantCulture) + " mm",
                2.0, PadTop - 4.0, false);
            AddLabel("0", 2.0, PadTop + plotH - 10.0, false);

            AxisText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "横轴＝离画面中心的距离（0 → {0:F0} px），纵轴＝落点会偏多少毫米。"
                + "中心几乎不动、越往边角越偏是正常形状；整条曲线压在绿色虚线以下就不用管，"
                + "碰到橙色虚线就值得把口径换到矫正图上。",
                maxR);
        }

        private void AddThreshold(Func<double, double> py, double left, double plotW,
            double valueMm, string colorKey, string label)
        {
            double y = py(valueMm);

            var dash = new Line
            {
                X1 = left,
                Y1 = y,
                X2 = left + plotW,
                Y2 = y,
                Stroke = ColorBrush(colorKey),
                StrokeThickness = 1.0,
                StrokeDashArray = new DoubleCollection { 4.0, 3.0 }
            };
            ChartCanvas.Children.Add(dash);

            var txt = new TextBlock
            {
                Text = label,
                Foreground = ColorBrush(colorKey),
                FontSize = 10.0
            };
            Canvas.SetLeft(txt, left + 4.0);
            Canvas.SetTop(txt, y - 14.0);
            ChartCanvas.Children.Add(txt);
        }

        private void AddLine(double x1, double y1, double x2, double y2,
            string brushKey, double thickness, DoubleCollection dash)
        {
            var l = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = (Brush)TryFindResource(brushKey) ?? Brushes.Gray,
                StrokeThickness = thickness,
                StrokeDashArray = dash
            };
            ChartCanvas.Children.Add(l);
        }

        private void AddLabel(string text, double x, double y, bool rightAlign)
        {
            var t = new TextBlock
            {
                Text = text,
                Foreground = (Brush)TryFindResource("VctSubTextBrush") ?? Brushes.Gray,
                FontSize = 10.0
            };
            if (rightAlign)
            {
                Canvas.SetRight(t, ChartCanvas.ActualWidth - x);
            }
            else
            {
                Canvas.SetLeft(t, x);
            }

            Canvas.SetTop(t, y);
            ChartCanvas.Children.Add(t);
        }

        private Brush ColorBrush(string colorKey)
        {
            object res = TryFindResource(colorKey);
            var c = res is Color ? (Color)res : Colors.Gray;
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private Brush AccentBrush(double opacity)
        {
            object res = TryFindResource("VctAccentColor");
            var c = res is Color ? (Color)res : Colors.DodgerBlue;
            var b = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        // ────────────────────────────────────────────────────────── 文本

        private static string Fmt(double v, string format, string unit)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                return "—";
            }

            return v.ToString(format, CultureInfo.InvariantCulture) + unit;
        }

        private static string SourceText(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return "—";
            }

            if (source == "board")
            {
                return "标定板当尺子";
            }

            if (source == "ninepoint")
            {
                return "九点口径对比";
            }

            return source;
        }
    }
}
